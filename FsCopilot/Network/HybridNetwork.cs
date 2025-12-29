namespace FsCopilot.Network;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class HybridNetwork : INetwork, IDisposable
{
    private static readonly TimeSpan DirectAttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly INetwork _p2p;
    private readonly INetwork _relay;

    public IObservable<ICollection<Peer>> Peers { get; }

    public HybridNetwork(INetwork p2p, INetwork relay)
    {
        _p2p = p2p;
        _relay = relay;

        // Merge peers from both networks. Prefer Direct if both exist for same PeerId.
        Peers = Observable.CombineLatest(
                _p2p.Peers.StartWith(),
                _relay.Peers.StartWith(),
                MergePeers)
            .Publish()
            .RefCount();
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

    public void Dispose()
    {
        if (_p2p is IDisposable d1) d1.Dispose();
        if (_relay is IDisposable d2) d2.Dispose();
    }

    // ------------------------------
    // Helpers
    // ------------------------------

    private static ICollection<Peer> MergePeers(ICollection<Peer> p2pPeers, ICollection<Peer> relayPeers)
    {
        var dict = new Dictionary<string, Peer>(StringComparer.Ordinal);

        foreach (var p in relayPeers)
            dict[p.PeerId] = p;

        foreach (var p in p2pPeers)
        {
            // Prefer direct
            if (dict.TryGetValue(p.PeerId, out var existing) && existing.Transport == Peer.TransportKind.Relay)
                dict[p.PeerId] = p;
            else
                dict[p.PeerId] = p;
        }

        return dict.Values.ToArray();
    }
}