namespace FsCopilot.Network;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class HybridNetwork : INetwork, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _peerId;
    private static readonly TimeSpan DirectAttemptTimeout = TimeSpan.FromSeconds(4);

    private readonly P2PNetwork _p2p;
    private readonly RelayNetwork _relay;

    public IObservable<ICollection<Peer>> Peers { get; }

    public HybridNetwork(string host, string peerId, string name)
    {
        _peerId = peerId;
        _p2p = new(host, peerId, name, false);
        _relay = new(host, peerId, name, false);

        // Merge peers from both networks. Prefer Direct if both exist for same PeerId.
        Peers = Observable.CombineLatest(
                _p2p.Peers.StartWith(),
                _relay.Peers.StartWith(),
                MergePeers)
            .Publish()
            .RefCount();
        
        Stream<PeerTags>().Subscribe(OnPeerTags, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Dispose();
        _p2p.Dispose();
        _relay.Dispose();
    }

    public async Task<ConnectionResult> Connect(string target, CancellationToken ct)
    {
        // 1) Try Direct with timeout = 5s (implemented here, not inside P2PNetwork)
        using var directCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        directCts.CancelAfter(DirectAttemptTimeout);
        
        var directResult = await _p2p.Connect(target, directCts.Token).ConfigureAwait(false);
        if (directResult == ConnectionResult.Success)
            return ConnectionResult.Success;
        
        // If caller cancelled - stop here
        if (ct.IsCancellationRequested)
            return ConnectionResult.Failed;

        // 2) Fallback to Relay (use original token, no hidden timeout)
        return await _relay.Connect(target, ct).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        // "Virtual disconnect" semantics depend on your implementations.
        // Here we call both to keep state consistent.
        _p2p.Disconnect();
        _relay.Disconnect();
    }

    public void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull
    {
        // Assumption: peers won't be connected via both transports simultaneously.
        // If that can happen, you'd need per-peer routing (not possible with current INetwork API).
        _p2p.SendAll(packet, unreliable);
        _relay.SendAll(packet, unreliable);
    }

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new()
    {
        // Register in both so decoding works no matter which transport delivered the packet.
        _p2p.RegisterPacket<TPacket, TCodec>();
        _relay.RegisterPacket<TPacket, TCodec>();
    }

    public IObservable<TPacket> Stream<TPacket>()
        => Observable.Merge(_p2p.Stream<TPacket>(), _relay.Stream<TPacket>());

    private static ICollection<Peer> MergePeers(ICollection<Peer> p2pPeers, ICollection<Peer> relayPeers)
    {
        var dict = new Dictionary<string, Peer>(StringComparer.Ordinal);

        foreach (var p in p2pPeers) dict[p.PeerId] = p;
        // Prefer direct
        foreach (var p in relayPeers) dict.TryAdd(p.PeerId, p);

        return dict.Values.ToArray();
    }

    private void OnPeerTags(PeerTags tags)
    {
        var peerIds = tags.Peers
            .Select(t => t.PeerId)
            .Where(id => id != _peerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _ = ConnectBurstAsync(peerIds);
    }

    private async Task ConnectBurstAsync(string[] peerIds)
    {
        using var hybridCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        hybridCts.CancelAfter(DirectAttemptTimeout * 2);

        try
        {
            var token = hybridCts.Token;
            var tasks = peerIds.Select(id => Connect(id, token)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception e) { Log.Error(e, "[Hybrid] ConnectBurst failed"); }
    }
}