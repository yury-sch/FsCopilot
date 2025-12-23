namespace FsCopilot.Network;

using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

public sealed class MeshNetwork : INetwork, IDisposable
{
    private const int StunPort = 3481;
    
    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DirectGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LatencyInterval = TimeSpan.FromSeconds(1);
    private DateTime _nextLatencyPing = DateTime.MinValue;
    
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly EventBasedNetListener _netListener = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _relayFallback = new();
    private readonly Subject<Unit> _publish = new();
    private readonly PacketRegistry _packetRegistry = new PacketRegistry()
        .RegisterPacket<PeerTag, PeerTag.Codec>()
        .RegisterPacket<PeerList, PeerList.Codec>()
        .RegisterPacket<Ping, Ping.Codec>()
        .RegisterPacket<Pong, Pong.Codec>();
    
    private readonly string _host;
    private readonly string _peerId;
    private readonly string _selfName;
    private readonly NetManager _net;

    private IPEndPoint? _stunEndpoint;
    private DateTime _nextHelloTime = DateTime.MinValue;

    public IObservable<ICollection<Peer>> Peers { get; }

    public MeshNetwork(string host, string peerId, string name)
    {
        _host = host;
        _peerId = peerId;
        _selfName = name;

        _net = new(_netListener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            DisconnectTimeout = 15000
        };
        _net.NatPunchModule.Init(_natListener);
        _net.Start();

        _natListener.NatIntroductionSuccess += OnNatIntroduction;
        _netListener.ConnectionRequestEvent += OnConnectionRequest;
        _netListener.PeerConnectedEvent += OnConnectionSuccess;
        _netListener.PeerDisconnectedEvent += OnPeerDisconnected;
        _netListener.NetworkReceiveEvent += OnMessage;

        Peers = _publish
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ => _peers.Values.Where(p => p.PeerId != _peerId).ToList())
            .Publish()
            .RefCount();

        Task.Run(() => Loop(_cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _net.Stop();
        _publish.OnCompleted();

        foreach (var subj in _streams.Values)
        {
            var mi = subj.GetType().GetMethod("OnCompleted");
            mi?.Invoke(subj, null);
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _net.PollEvents();
                _net.NatPunchModule.PollEvents();

                if (DateTime.UtcNow >= _nextHelloTime)
                {
                    await Introduce(ct);
                    _nextHelloTime = DateTime.UtcNow + HelloInterval;
                }
                
                if (DateTime.UtcNow >= _nextLatencyPing)
                {
                    SendAll(new Ping(DateTime.UtcNow.Ticks), unreliable: true);
                    _nextLatencyPing = DateTime.UtcNow + LatencyInterval;
                }

                await Task.Delay(15, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Network] Loop error"); }
        }
    }

    private void OnNatIntroduction(IPEndPoint endpoint, NatAddressType type, string token)
    {
        var parts = token.Split('|');
        if (parts.Length < 3) return;
        var isRelay = parts[0].Equals("RELAY");
        
        var targetPeer = parts.First(p => p != _peerId);
        
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
                Transport = isRelay ? Peer.TransportKind.Relay : Peer.TransportKind.Direct
            });
        _publish.OnNext(Unit.Default);

        // Start fallback ONLY for direct attempts
        if (!isRelay) ScheduleRelayFallback(targetPeer);

        _net.Connect(endpoint, $"{_peerId}|{targetPeer}|{_packetRegistry.Schema}");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_packetRegistry.Schema.Equals(request.Data.GetString())) request.Accept();
        else request.Reject(NetDataWriter.FromString(_peerId));
    }

    private void OnConnectionSuccess(NetPeer peer)
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

    private void OnPing(NetPeer peer, Ping ping)
    {
        if (!_packetRegistry.TryGetCodec<Pong>(out var pongId, out var pongCodec)) return;

        using var rms = new MemoryStream();
        using var rbw = new BinaryWriter(rms, Encoding.UTF8, true);

        rbw.Write(pongId);
        pongCodec.Encode(new Pong(ping.TicksUtc), rbw);
        rbw.Flush();

        peer.Send(rms.ToArray(), DeliveryMethod.Unreliable);
    }

    private void OnPong(NetPeer peer, Pong pong)
    {
        if (peer.Tag is not PeerTag tag) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        var rttTicks = nowTicks - pong.TicksUtc;
        if (rttTicks <= 0) return;
        var rttMs = TimeSpan.FromTicks(rttTicks).TotalMilliseconds;

        // Store one-way estimate (RTT/2) in Peer.Ping
        var oneWayMs = (int)Math.Round(rttMs / 2.0);
        UpdatePeer(tag.PeerId, p => p with { Ping = oneWayMs });
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
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

    private void OnMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
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
            if (packet is Ping ping) OnPing(peer, ping);
            if (packet is Pong pong) OnPong(peer, pong);

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
        _net.DisconnectAll();
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

        _net.SendToAll(data, method);
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
        
        _net.NatPunchModule.SendNatIntroduceRequest(_stunEndpoint, $"{_peerId}|{_packetRegistry.Schema}");
    }

    private void SendDirectConnectionRequest(string targetPeerId)
    {
        if (_stunEndpoint == null) return;

        var msg = $"DIRECT|{_peerId}|{targetPeerId}";
        var writer = new NetDataWriter();
        writer.Put(msg);

        _net.SendUnconnectedMessage(writer, _stunEndpoint);
        Log.Debug("[Network] DIRECT {SelfId} -> {TargetId}", _peerId, targetPeerId);
    }
    
    private void ScheduleRelayFallback(string targetPeerId)
    {
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
                if (_peers.TryGetValue(targetPeerId, out var p) && p.Status == Peer.State.Success) return;

                // Ask STUN to introduce us to relay-as-peer
                if (_stunEndpoint != null)
                {
                    _net.SendUnconnectedMessage(NetDataWriter.FromString($"RELAY|{_peerId}|{targetPeerId}"), _stunEndpoint);
                    Log.Debug("[Network] RELAY {SelfId} -> {TargetId}", _peerId, targetPeerId);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Network] Relay fallback timer failed for {PeerId}", targetPeerId); }
        }, CancellationToken.None);
    }
    
    private void UpdatePeer(string peerId, Func<Peer, Peer> updater)
    {
        while (true)
        {
            if (!_peers.TryGetValue(peerId, out var oldValue)) return;
            var newValue = updater(oldValue);
            if (_peers.TryUpdate(peerId, newValue, oldValue)) break;
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

    private record Ping(long TicksUtc)
    {
        public sealed class Codec : IPacketCodec<Ping>
        {
            public void Encode(Ping p, BinaryWriter bw) => bw.Write(p.TicksUtc);
            public Ping Decode(BinaryReader br) => new(br.ReadInt64());
        }
    }

    private record Pong(long TicksUtc)
    {
        public sealed class Codec : IPacketCodec<Pong>
        {
            public void Encode(Pong p, BinaryWriter bw) => bw.Write(p.TicksUtc);
            public Pong Decode(BinaryReader br) => new(br.ReadInt64());
        }
    }
}