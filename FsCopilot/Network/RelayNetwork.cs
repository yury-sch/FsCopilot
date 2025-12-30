namespace FsCopilot.Network;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using Serilog;

public sealed class RelayNetwork : INetwork, IDisposable
{
    private const int RelayPort = 3600;
    private const byte ControlChannel = 0;
    private const byte DataChannel = 1;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;

    private readonly string _host;
    private readonly string _peerId;
    private readonly string _selfName;

    private readonly Subject<Unit> _publish = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectionResult>> _connectWaiters = new(StringComparer.Ordinal);

    private readonly Codecs _codecs = new Codecs()
        .Add<PeerTags, PeerTags.Codec>();
            // .Add<PeerName, PeerName.Codec>()
            // .Add<Ping, Ping.Codec>()
            // .Add<Pong, Pong.Codec>()

    private IPEndPoint? _relayEndpoint;
    private volatile NetPeer? _relayPeer;

    public IObservable<ICollection<Peer>> Peers { get; }

    public RelayNetwork(string host, string peerId, string name)
    {
        _host = host;
        _peerId = peerId;
        _selfName = name;

        _net = new(_listener)
        {
            IPv6Enabled = true,
            UnconnectedMessagesEnabled = false,
            NatPunchEnabled = false,
            DisconnectTimeout = 15000,
            ChannelsCount = 2
        };

        // Client should not accept inbound connections
        _listener.ConnectionRequestEvent += req => req.Reject();

        _listener.PeerConnectedEvent += OnRelayConnected;
        _listener.PeerDisconnectedEvent += OnRelayDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkLatencyUpdateEvent += OnLatencyUpdate;

        if (!_net.Start())
            throw new InvalidOperationException($"Failed to start relay client on port {_net.LocalPort}");

        Peers = _publish
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ => _peers.Values.Where(p => p.PeerId != _peerId).ToList())
            .Publish()
            .RefCount();
        
        Stream<PeerTags>().Subscribe(OnPeerTags, _cts.Token);

        Task.Run(() => PollLoop(_cts.Token), _cts.Token);
        Task.Run(() => ReconnectLoop(_cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();

        try { _net.Stop(); } catch { /* ignore */ }

        _publish.OnCompleted();

        foreach (var subj in _streams.Values)
        {
            var mi = subj.GetType().GetMethod("OnCompleted");
            mi?.Invoke(subj, null);
        }

        foreach (var kv in _connectWaiters)
            kv.Value.TrySetResult(ConnectionResult.Failed);
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _net.PollEvents();
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal
            }
            catch (Exception e)
            {
                Log.Error(e, "[Relay] Loop error");
            }
        }
    }

    private async Task ReconnectLoop(CancellationToken ct)
    {
        await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var rp = Volatile.Read(ref _relayPeer);
                if (rp is { ConnectionState: ConnectionState.Connected })
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }

                await EnsureRelayConnected(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e)
            {
                Log.Warning(e, "[Relay] Reconnect attempt failed");
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<ConnectionResult> Connect(string target, CancellationToken ct)
    {
        try
        {
            await EnsureRelayConnected(ct).ConfigureAwait(false);

            var waiter = new TaskCompletionSource<ConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_connectWaiters.TryAdd(target, waiter))
                return ConnectionResult.Failed;

            SendConnectIntent(target);

            await using var reg = ct.Register(() => waiter.TrySetCanceled(ct));
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConnectionResult.Failed;
        }
        catch (Exception)
        {
            return ConnectionResult.Failed;
        }
        finally
        {
            _connectWaiters.TryRemove(target, out _);
        }
    }

    public void Disconnect() => DisconnectAllVirtual();

    public void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull
    {
        try
        {
            var peer = Volatile.Read(ref _relayPeer);
            if (peer is null || peer.ConnectionState != ConnectionState.Connected)
                return;

            var data = _codecs.Encode(packet);
            if (data.Length == 0)
                return;

            var method = unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
            peer.Send(data, DataChannel, method);
        }
        catch (Exception e)
        {
            Log.Error(e, "[Relay] SendAll error");
        }
    }

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new() =>
        _codecs.Add<TPacket, TCodec>();

    public IObservable<TPacket> Stream<TPacket>() =>
        ((IObservable<TPacket>)_streams.GetOrAdd(typeof(TPacket), _ => new Subject<TPacket>()))
        .ObserveOn(TaskPoolScheduler.Default);

    private async Task EnsureRelayConnected(CancellationToken ct)
    {
        var rp = Volatile.Read(ref _relayPeer);
        if (rp is { ConnectionState: ConnectionState.Connected })
            return;

        _relayEndpoint ??= await ResolveRelayEndpoint(ct).ConfigureAwait(false);
        if (_relayEndpoint is null)
            throw new InvalidOperationException("Relay endpoint resolution failed");

        var token = $"v=1;pid={_peerId};schema={_codecs.Schema}";

        _net.Connect(_relayEndpoint, token);

        var start = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            rp = Volatile.Read(ref _relayPeer);
            if (rp is { ConnectionState: ConnectionState.Connected })
                return;

            if (DateTime.UtcNow - start > ConnectAttemptTimeout)
                throw new TimeoutException("Relay connect timeout");

            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException(ct);
    }

    private async Task<IPEndPoint?> ResolveRelayEndpoint(CancellationToken ct)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false);
            var ip = ips.FirstOrDefault(x => x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
            return ip is null ? null : new IPEndPoint(ip, RelayPort);
        }
        catch (Exception e)
        {
            Log.Error(e, "[Relay] Failed to resolve relay host {Host}", _host);
            return null;
        }
    }

    private void OnRelayConnected(NetPeer peer)
    {
        Volatile.Write(ref _relayPeer, peer);
        Log.Information("[Relay] Connected: {Ep}", peer.Address);
    }

    private void OnRelayDisconnected(NetPeer peer, DisconnectInfo info)
    {
        Volatile.Write(ref _relayPeer, null);

        // Links are implicitly gone when relay connection is gone
        _peers.Clear();
        _publish.OnNext(Unit.Default);

        foreach (var kv in _connectWaiters)
            kv.Value.TrySetResult(ConnectionResult.Failed);

        if (info.Reason == DisconnectReason.ConnectionRejected)
        {
            var reason = info.AdditionalData.GetString();
            Log.Warning("[Relay] Connection rejected: {Reason}", reason);
        }
        else
        {
            Log.Warning("[Relay] Disconnected: {Reason}", info.Reason);
        }
    }

    private void OnLatencyUpdate(NetPeer peer, int _)
    {
        var ping = peer.Ping;

        foreach (var kv in _peers)
            _peers[kv.Key] = kv.Value with { Ping = ping };

        _publish.OnNext(Unit.Default);
    }

    // ------------------------------
    // Control protocol
    // ------------------------------

    private enum ControlType : byte
    {
        // client -> server
        ConnectIntent = 1,
        Disconnect = 2,

        // server -> client
        LinkReady = 10,
        LinkClosed = 11,

        Error = 255
    }

    private void SendConnectIntent(string targetPeerId)
    {
        var peer = Volatile.Read(ref _relayPeer);
        if (peer is null || peer.ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Relay is not connected");

        var w = new NetDataWriter();
        w.Put((byte)ControlType.ConnectIntent);
        w.Put(targetPeerId);

        peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private void DisconnectAllVirtual()
    {
        var peer = Volatile.Read(ref _relayPeer);
        if (peer is { ConnectionState: ConnectionState.Connected })
        {
            var w = new NetDataWriter();
            w.Put((byte)ControlType.Disconnect);
            peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
        }

        // Local reset immediately
        _peers.Clear();
        _publish.OnNext(Unit.Default);
    }

    // ------------------------------
    // Receive
    // ------------------------------

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (channel == ControlChannel)
        {
            HandleControl(reader);
            return;
        }

        HandleData(reader);
    }

    private void HandleControl(NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1) return;

        var type = (ControlType)reader.GetByte();

        switch (type)
        {
            case ControlType.LinkReady:
            {
                var otherId = reader.GetString();
                OnLinkReady(otherId);
                break;
            }
            case ControlType.LinkClosed:
            {
                var otherId = reader.GetString();
                var code = reader.GetString();
                var msg = reader.GetString();
                OnLinkClosed(otherId, code, msg);
                break;
            }
            case ControlType.Error:
            {
                var code = reader.GetString();
                var msg = reader.GetString();
                OnError(code, msg);
                break;
            }
        }
    }

    private void HandleData(NetPacketReader reader)
    {
        var obj = _codecs.Decode(reader);
        if (obj is null)
            return;

        if (!_streams.TryGetValue(obj.GetType(), out var subjObj))
            return;

        var onNextMethod = subjObj.GetType().GetMethod("OnNext");
        onNextMethod!.Invoke(subjObj, [obj]);
    }

    // ------------------------------
    // Peers + waiters
    // ------------------------------

    private void OnLinkReady(string otherPeerId)
    {
        var ping = Volatile.Read(ref _relayPeer)?.Ping ?? 0;

        _peers.AddOrUpdate(otherPeerId,
            _ => new Peer(otherPeerId, Name: string.Empty, Ping: ping, Transport: Peer.TransportKind.Relay),
            (_, old) => old with { Ping = ping, Transport = Peer.TransportKind.Relay });

        _publish.OnNext(Unit.Default);

        if (_connectWaiters.TryGetValue(otherPeerId, out var tcs))
            tcs.TrySetResult(ConnectionResult.Success);

        var peerList = new PeerTags(_peers.Values
            .Select(p => new PeerTags.Tag(p.PeerId, p.Name))
            .Prepend(new(_peerId, _selfName))
            .ToArray());

        SendAll(peerList, unreliable: false);
    }

    private void OnLinkClosed(string otherPeerId, string code, string message)
    {
        if (otherPeerId != "*")
        {
            _peers.TryRemove(otherPeerId, out _);
            _publish.OnNext(Unit.Default);

            if (_connectWaiters.TryGetValue(otherPeerId, out var tcs))
                tcs.TrySetResult(ConnectionResult.Failed);
        }

        Log.Information("[Relay] Link closed: {Other} ({Code}) {Msg}", otherPeerId, code, message);
    }

    private void OnError(string code, string message)
    {
        Log.Warning("[Relay] Error: {Code} {Msg}", code, message);

        // Minimal protocol: no target id in error => fail all pending connects
        var result = code == "SCHEMA_MISMATCH" ? ConnectionResult.Rejected : ConnectionResult.Failed;

        foreach (var kv in _connectWaiters)
            kv.Value.TrySetResult(result);
    }

    private void OnPeerTags(PeerTags tags)
    {
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Any);
        
        foreach (var tag in tags.Peers)
        {
            if (tag.PeerId == _peerId) continue;
            
            while (true)
            {
                if (!_peers.TryGetValue(tag.PeerId, out var oldValue)) break;
                if (_peers.TryUpdate(tag.PeerId, oldValue with { Name = tag.Name }, oldValue)) break;
            }
            
            if (!_peers.ContainsKey(tag.PeerId)) SendConnectIntent(tag.PeerId);
        }
    }

    // private void OnPeerName(NetPeer peer, PeerName name)
    // {
    //     _peerNames.AddOrUpdate(peer, name.Name, (key, value) => name.Name);
    // }

    // private void OnPing(NetPeer peer, Ping ping)
    // {
    //     var data = _codecs.Encode(new Pong(ping.TicksUtc));
    //     if (data.Length == 0) return;
    //     peer.Send(data, DeliveryMethod.Unreliable);
    // }
    //
    // private void OnPong(NetPeer peer, Pong pong)
    // {
    //     if (peer.Tag is not PeerTag tag) return;
    //     var nowTicks = Stopwatch.GetTimestamp();
    //     var rtt = (nowTicks - pong.TicksUtc) * 1000.0 / Stopwatch.Frequency;
    //     if (rtt <= 0) return;
    //     // var rttMs = TimeSpan.FromTicks(rttTicks).TotalMilliseconds;
    //
    //     // Store one-way estimate (RTT/2) in Peer.Ping
    //     var oneWayMs = (int)Math.Round(rtt / 2.0);
    //     UpdatePeer(tag.PeerId, p => p with { Ping = oneWayMs });
    // }

    // private record PeerName(string Name)
    // {
    //     public sealed class Codec : IPacketCodec<PeerName>
    //     {
    //         public void Encode(PeerName packet, BinaryWriter bw) => bw.Write(packet.Name);
    //
    //         public PeerName Decode(BinaryReader br) => new(br.ReadString());
    //     }
    // }

    private record PeerTags(PeerTags.Tag[] Peers)
    {
        public sealed class Codec : IPacketCodec<PeerTags>
        {
            public void Encode(PeerTags packet, BinaryWriter bw)
            {
                bw.Write(packet.Peers.Length);
                foreach (var peer in packet.Peers)
                {
                    bw.Write(peer.PeerId);
                    bw.Write(peer.Name);
                }
            }
    
            public PeerTags Decode(BinaryReader br)
            {
                var count = br.ReadInt32();
                var peers = new Tag[count];
                for (var i = 0; i < count; i++)
                {
                    var peerId = br.ReadString();
                    var name = br.ReadString();
                    peers[i] = new(peerId, name);
                }
                return new(peers);
            }
        }

        public record Tag(string PeerId, string Name);
    }

    // private record Ping(long TicksUtc)
    // {
    //     public sealed class Codec : IPacketCodec<Ping>
    //     {
    //         public void Encode(Ping p, BinaryWriter bw) => bw.Write(p.TicksUtc);
    //         public Ping Decode(BinaryReader br) => new(br.ReadInt64());
    //     }
    // }
    //
    // private record Pong(long TicksUtc)
    // {
    //     public sealed class Codec : IPacketCodec<Pong>
    //     {
    //         public void Encode(Pong p, BinaryWriter bw) => bw.Write(p.TicksUtc);
    //         public Pong Decode(BinaryReader br) => new(br.ReadInt64());
    //     }
    // }
}