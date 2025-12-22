namespace FsCopilot.Network;

using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

public sealed class DirectNetwork : INetwork, IDisposable
{
    private const int StunPort = 3481;
    
    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DirectGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LatencyInterval = TimeSpan.FromSeconds(1);
    private DateTime _nextLatencyPing = DateTime.MinValue;
    
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly EventBasedNetListener _directListener = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _relayFallback = new();
    private readonly Subject<Unit> _publish = new();
    private readonly PacketRegistry _packetRegistry = new PacketRegistry()
        .RegisterPacket<PeerTag, PeerTag.Codec>()
        .RegisterPacket<PeerList, PeerList.Codec>()
        .RegisterPacket<LatencyPing, LatencyPing.Codec>()
        .RegisterPacket<LatencyPong, LatencyPong.Codec>();
    
    private readonly string _host;
    private readonly string _peerId;
    private readonly string _selfName;
    private readonly NetManager _direct;

    private IPEndPoint? _stunEndpoint;
    private DateTime _nextHelloTime = DateTime.MinValue;

    public IObservable<ICollection<Peer>> Peers { get; }

    public DirectNetwork(string host, string peerId, string name)
    {
        _host = host;
        _peerId = peerId;
        _selfName = name;

        _direct = new(_directListener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            DisconnectTimeout = 15000
        };
        _direct.NatPunchModule.Init(_natListener);
        _direct.Start();

        _natListener.NatIntroductionSuccess += OnNatIntroduction;
        _directListener.ConnectionRequestEvent += OnDirectConnectionRequest;
        _directListener.PeerConnectedEvent += OnDirectConnectionSuccess;
        _directListener.PeerDisconnectedEvent += OnDirectPeerDisconnected;
        _directListener.NetworkReceiveEvent += OnDirectMessage;

        Peers = _publish
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ => _peers.Values.Where(p => p.PeerId != _peerId).ToList())
            .Publish()
            .RefCount();

        Task.Run(() => DirectPollLoop(_cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _direct.Stop();
        _publish.OnCompleted();

        foreach (var subj in _streams.Values)
        {
            var mi = subj.GetType().GetMethod("OnCompleted");
            mi?.Invoke(subj, null);
        }
    }

    private async Task DirectPollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _direct.PollEvents();
                _direct.NatPunchModule.PollEvents();

                if (DateTime.UtcNow >= _nextHelloTime)
                {
                    await Introduce(ct);
                    _nextHelloTime = DateTime.UtcNow + HelloInterval;
                }
                
                if (DateTime.UtcNow >= _nextLatencyPing)
                {
                    SendAll(new LatencyPing(DateTime.UtcNow.Ticks), unreliable: true);
                    _nextLatencyPing = DateTime.UtcNow + LatencyInterval;
                }

                await Task.Delay(15, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal
            }
            catch (Exception e)
            {
                Log.Error(e, "[Network] Loop error");
            }
        }
    }

    private void OnNatIntroduction(IPEndPoint endpoint, NatAddressType type, string token)
    {
        // token examples:
        // Direct: "A|B"
        // Relay : "RLY|A|B"
        var isRelay = token.StartsWith("RLY|", StringComparison.Ordinal);

        // Keep your existing "find other peer" logic, it still works.
        // For "RLY|A|B" this will return the "other id" correctly.
        var targetPeer = token.Split('|').Last(p => p != _peerId && p != "RLY");
        
         // Add for direct attempt and update for relay mode
        _peers.AddOrUpdate(
            targetPeer,
            _ => new(
                PeerId: targetPeer,
                Name: string.Empty,
                Ping: 0,
                Status: Peer.State.Pending,
                Transport: isRelay ? Peer.TransportKind.Relay : Peer.TransportKind.Direct),
            (_, old) => old with
            {
                Status = Peer.State.Pending,
                Transport = isRelay ? Peer.TransportKind.Relay : old.Transport
            });
        _publish.OnNext(Unit.Default);

        // Start fallback ONLY for direct attempts
        if (!isRelay) ScheduleRelayFallback(targetPeer);

        _direct.Connect(endpoint, _packetRegistry.Schema);
    }

    private void OnDirectConnectionRequest(ConnectionRequest request)
    {
        if (_packetRegistry.Schema.Equals(request.Data.GetString())) request.Accept();
        else request.Reject(NetDataWriter.FromString(_peerId));
    }

    private void OnDirectConnectionSuccess(NetPeer peer)
    {
        if (!_packetRegistry.TryGetCodec<PeerTag>(out var packetId, out var codec)) return;
        var tag = new PeerTag(_peerId, _selfName);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

        bw.Write(packetId);
        codec.Encode(tag, bw);
        bw.Flush();
        var data = ms.ToArray();
        
        peer.Send(data, DeliveryMethod.ReliableOrdered);
    }

    private void OnPeerTag(NetPeer peer, PeerTag tag)
    {
        peer.Tag = tag;

        if (_relayFallback.TryRemove(tag.PeerId, out var t))
        {
            t.Cancel();
            t.Dispose();
        }
        
        UpdatePeer(tag.PeerId, p => p with
        {
            Name = tag.Name,
            Status = Peer.State.Success,
            Transport = Peer.TransportKind.Direct
        });
        
        Log.Debug("[Network] Broadcasting peer list: {Ids}", string.Join(", ", _peers.Keys));
        
        if (!_packetRegistry.TryGetCodec<PeerList>(out var packetId, out var codec)) return;
        var peerList = new PeerList(_peers.Values.Select(p => new PeerTag(p.PeerId, p.Name)).ToArray());

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

        bw.Write(packetId);
        codec.Encode(peerList, bw);
        bw.Flush();
        var data = ms.ToArray();
 
        peer.Send(data, DeliveryMethod.ReliableOrdered);
    }

    private void OnPeerList(NetPeer _, PeerList list)
    {
        var anyNew = false;

        foreach (var peer in list.Peers)
        {
            if (string.IsNullOrEmpty(peer.PeerId)) continue;
            if (peer.PeerId == _peerId) continue;

            // If we already know, we don’t bother the server again.
            if (!_peers.TryAdd(peer.PeerId, new(
                    PeerId: peer.PeerId,
                    Name: peer.Name,
                    Ping: 0,
                    Status: Peer.State.Pending,
                    Transport: Peer.TransportKind.Direct))) continue;

            Log.Debug("[Network] Discovered peer via list: {PeerId}", peer.PeerId);
            anyNew = true;

            SendDirectConnectionRequest(peer.PeerId);
        }

        if (anyNew) _publish.OnNext(Unit.Default);
    }

    private void OnLatencyPing(NetPeer peer, LatencyPing ping)
    {
        // Reply immediately with the same timestamp.
        // This yields end-to-end RTT independent of transport (direct/relay).
        if (!_packetRegistry.TryGetCodec<LatencyPong>(out var pongId, out var pongCodec)) return;

        using var rms = new MemoryStream();
        using var rbw = new BinaryWriter(rms, Encoding.UTF8, true);

        rbw.Write(pongId);
        pongCodec.Encode(new LatencyPong(ping.TicksUtc), rbw);
        rbw.Flush();

        peer.Send(rms.ToArray(), DeliveryMethod.Unreliable);
    }

    private void OnLatencyPong(NetPeer peer, LatencyPong pong)
    {
        // We can only attribute latency if we know peerId (PeerTag handshake done).
        if (peer.Tag is not PeerTag tag) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        var rttTicks = nowTicks - pong.TicksUtc;
        if (rttTicks <= 0) return;
        var rttMs = TimeSpan.FromTicks(rttTicks).TotalMilliseconds;

        // Store one-way estimate (RTT/2) in Peer.Ping
        var oneWayMs = (int)Math.Round(rttMs / 2.0);
        UpdatePeer(tag.PeerId, p => p with { Ping = oneWayMs });
    }

    private void OnDirectPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (info.Reason == DisconnectReason.ConnectionRejected)
        {
            var peerId = info.AdditionalData.GetString();
            UpdatePeer(peerId, p => p with { Status = Peer.State.Rejected });
            Log.Information("[Network] Peer {PeerId} rejected connection", peerId);
            return;
        }

        if (peer.Tag is not PeerTag tag) return;
        if (info.Reason == DisconnectReason.RemoteConnectionClose)
        {
            if (_peers.TryRemove(tag.PeerId, out _)) _publish.OnNext(Unit.Default);
            return;
        }

        UpdatePeer(tag.PeerId, p => p with { Status = Peer.State.Failed });
        Log.Information("[Network] Peer {PeerId} disconnected by reason {Reason}", tag.PeerId, info.Reason);
    }

    private void OnDirectMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var length = reader.AvailableBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            reader.GetBytes(buffer, length);

            using var ms = new MemoryStream(buffer, 0, length, writable: false, publiclyVisible: true);
            using var br = new BinaryReader(ms, Encoding.UTF8, true);

            var packetType = br.ReadByte();
            if (!_packetRegistry.TryGetCodec(packetType, out var codec)) return;

            object packet;
            try
            {
                packet = codec.Decode(br);
            }
            catch (Exception e)
            {
                Log.Error(e, "[Network] An error occurred while decoding {DataType}", packetType);
                return;
            }

            if (packet is PeerTag hello) OnPeerTag(peer, hello);
            if (packet is PeerList list) OnPeerList(peer, list);
            if (packet is LatencyPing ping) OnLatencyPing(peer, ping);
            if (packet is LatencyPong pong) OnLatencyPong(peer, pong);

            if (!_streams.TryGetValue(packet.GetType(), out var subjObj)) return;
            var onNextMethod = subjObj.GetType().GetMethod("OnNext");
            onNextMethod!.Invoke(subjObj, [packet]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reader.Recycle();
        }
    }

    public async Task<bool> Connect(string targetPeerId, CancellationToken ct)
    {
        try
        {
            await Introduce(ct);

            SendDirectConnectionRequest(targetPeerId);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            Log.Error(e, "[Network] Failed to connect to discovery host {Host}", _host);
            
            return false;
        }
    }

    public void Disconnect()
    {
        _direct.DisconnectAll();
        _peers.Clear();
        _publish.OnNext(Unit.Default);
    }

    public void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull
    {
        if (!_packetRegistry.TryGetCodec<TPacket>(out var packetId, out var codec)) return;

        byte[] data;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            bw.Write(packetId);
            codec.Encode(packet, bw);
            bw.Flush();
            data = ms.ToArray();
        }

        var method = unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;

        _direct.SendToAll(data, method);
    }

    public IObservable<TPacket> Stream<TPacket>() =>
        ((IObservable<TPacket>)_streams.GetOrAdd(typeof(TPacket), _ => new Subject<TPacket>()))
        .ObserveOn(TaskPoolScheduler.Default);

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new()
    {
        _packetRegistry.RegisterPacket<TPacket, TCodec>();
    }

    private async Task Introduce(CancellationToken ct)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(_host, ct);
            var ip = ips.First(x => x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
            _stunEndpoint = new(ip, StunPort);
        }
        catch (Exception e)
        {
            _stunEndpoint = null;
            return;
        }
        
        if (_stunEndpoint == null) return;
        
        _direct.NatPunchModule.SendNatIntroduceRequest(_stunEndpoint, _peerId);
    }

    private void SendDirectConnectionRequest(string targetPeerId)
    {
        if (_stunEndpoint == null) return;

        var msg = $"CALL|{_peerId}|{targetPeerId}";
        var writer = new NetDataWriter();
        writer.Put(msg);

        _direct.SendUnconnectedMessage(writer, _stunEndpoint);
        Log.Debug("[Network] CALL {SelfId} -> {TargetId}", _peerId, targetPeerId);
    }
    
    private void SendRelayConnectionRequest(string targetPeerId)
    {
        if (_stunEndpoint == null) return;

        var msg = $"RELAY|{_peerId}|{targetPeerId}";
        var writer = new NetDataWriter();
        writer.Put(msg);

        _direct.SendUnconnectedMessage(writer, _stunEndpoint);
        Log.Debug("[Network] RELAY {SelfId} -> {TargetId}", _peerId, targetPeerId);
    }
    
    private void ScheduleRelayFallback(string targetPeerId)
    {
        // Cancel previous timer for this peer (if any)
        if (_relayFallback.TryRemove(targetPeerId, out var old))
        {
            old.Cancel();
            old.Dispose();
        }

        var cts = new CancellationTokenSource();
        _relayFallback[targetPeerId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DirectGrace, cts.Token).ConfigureAwait(false);

                // If we already succeeded - no fallback
                if (_peers.TryGetValue(targetPeerId, out var p) && p.Status == Peer.State.Success)
                    return;

                // Switch to relay mode in peer list (UI / logic)
                UpdatePeer(targetPeerId, x => x with { Transport = Peer.TransportKind.Relay });

                // Ask STUN to introduce us to relay-as-peer
                SendRelayConnectionRequest(targetPeerId);

                Log.Information("[Direct] No PeerTag after {Grace}s -> RELAY request for {PeerId}", DirectGrace.TotalSeconds, targetPeerId);
            }
            catch (OperationCanceledException)
            {
                // normal
            }
            catch (Exception e)
            {
                Log.Error(e, "[Direct] Relay fallback timer failed for {PeerId}", targetPeerId);
            }
        }, CancellationToken.None);
    }
    
    private void UpdatePeer(string peerId, Func<Peer, Peer> updater)
    {
        while (true)
        {
            if (!_peers.TryGetValue(peerId, out var oldValue))
                return;
            var newValue = updater(oldValue);
            if (_peers.TryUpdate(peerId, newValue, oldValue))
                break;
        }
        _publish.OnNext(Unit.Default);
    }

    private record PeerTag(string PeerId, string Name)
    {
        public sealed class Codec : IPacketCodec<PeerTag>
        {
            public void Encode(PeerTag packet, BinaryWriter bw)
            {
                bw.Write(packet.PeerId);
                bw.Write(packet.Name);
            }

            public PeerTag Decode(BinaryReader br)
            {
                var peerId = br.ReadString();
                var name = br.ReadString();
                return new(peerId, name);
            }
        }
    }

    private record PeerList(PeerTag[] Peers)
    {
        public sealed class Codec : IPacketCodec<PeerList>
        {
            private readonly PeerTag.Codec _helloCodec = new();
            
            public void Encode(PeerList packet, BinaryWriter bw)
            {
                bw.Write(packet.Peers.Length);
                foreach (var peer in packet.Peers) _helloCodec.Encode(peer, bw);
            }

            public PeerList Decode(BinaryReader br)
            {
                var count = br.ReadInt32();
                var peers = new PeerTag[count];
                for (var i = 0; i < count; i++)
                    peers[i] = _helloCodec.Decode(br);
                return new(peers);
            }
        }
    }

    private record LatencyPing(long TicksUtc)
    {
        public sealed class Codec : IPacketCodec<LatencyPing>
        {
            public void Encode(LatencyPing p, BinaryWriter bw) => bw.Write(p.TicksUtc);
            public LatencyPing Decode(BinaryReader br) => new(br.ReadInt64());
        }
    }

    private record LatencyPong(long TicksUtc)
    {
        public sealed class Codec : IPacketCodec<LatencyPong>
        {
            public void Encode(LatencyPong p, BinaryWriter bw) => bw.Write(p.TicksUtc);
            public LatencyPong Decode(BinaryReader br) => new(br.ReadInt64());
        }
    }
}