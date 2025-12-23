namespace FsCopilot.Network;

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
    // private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _relayFallback = new();
    private readonly Subject<Unit> _publish = new();
    private readonly Codecs _codecs = new Codecs()
            // .Add<PeerName, PeerName.Codec>()
            .Add<PeerTags, PeerTags.Codec>()
            // .Add<Ping, Ping.Codec>()
            // .Add<Pong, Pong.Codec>()
            ;
    
    private readonly ConcurrentDictionary<NetPeer, string> _peerNames = new();
    
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

        var peers = new List<NetPeer>();
        Peers = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ =>
            {
                _net.GetPeersNonAlloc(peers, ConnectionState.Any);
                return peers.Select(p => new Peer(
                        p.Tag as string ?? string.Empty, 
                        _peerNames.TryGetValue(p, out var peerName) ? peerName : string.Empty, 
                        p.Ping,
                        p.ConnectionState switch
                        {
                            ConnectionState.Outgoing => Peer.State.Pending,
                            ConnectionState.Connected => Peer.State.Success,
                            _ => Peer.State.Failed
                        }, 
                        p.Address.Equals(_stunEndpoint?.Address) ? Peer.TransportKind.Relay : Peer.TransportKind.Direct))
                    .ToArray();
            })
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
                
                // if (DateTime.UtcNow >= _nextLatencyPing)
                // {
                //     SendAll(new Ping(Stopwatch.GetTimestamp()), unreliable: true);
                //     _nextLatencyPing = DateTime.UtcNow + LatencyInterval;
                // }

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
        // var isRelay = parts[0].Equals("RELAY");
        
        var targetPeer = parts.Skip(1).First(p => p != _peerId);
        Log.Debug("[Network] NAT {Peer} -> {Address}",targetPeer, endpoint);

        var peer = _net.Connect(endpoint, $"{_peerId}|{targetPeer}|{_codecs.Schema}");
        if (peer != null) peer.Tag = targetPeer; // already awaiting
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var token = request.Data.GetString();
        var parts = token.Split("|");
        if (parts.Length < 3) return;
        var targetPeer = parts.First(p => p != _peerId);
        var schema = parts[2];
        if (!_codecs.Schema.Equals(schema))
        {
            request.Reject(NetDataWriter.FromString(_peerId));
            return;
        }

        var peer = request.Accept();
        peer.Tag = targetPeer;
    }

    private void OnConnectionSuccess(NetPeer peer)
    {
        var peerId = peer.Tag as string ?? string.Empty;
        Log.Debug("[Network] CON {Peer} -> {Address}", peerId, new IPEndPoint(peer.Address, peer.Port));
        
        if (_relayFallback.TryRemove(peerId, out var t))
        {
            t.Cancel();
            t.Dispose();
        }
        
        // var data = _codecs.Encode(new PeerName(_selfName));
        // peer.Send(data, DeliveryMethod.ReliableUnordered);
        
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Any);
        var peerList = new PeerTags(peers
            .Select(p => new PeerTags.Tag(p.Tag as string ?? string.Empty, _peerNames.TryGetValue(p, out var peerName) ? peerName : string.Empty))
            .Prepend(new(_peerId, _selfName))
            .ToArray());
        var data = _codecs.Encode(peerList);
        if (data.Length == 0) return;
        peer.Send(data, DeliveryMethod.ReliableUnordered);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        var peerId = peer.Tag as string ?? string.Empty;
        Log.Debug("[Network] REJ {PeerId} -> {Address} ({Reason})", peerId, new IPEndPoint(peer.Address, peer.Port), info.Reason);
        
        // if (info.Reason == DisconnectReason.ConnectionRejected)
        // {
        //     // var peerId = info.AdditionalData.GetString();
        //     // UpdatePeer(tag.PeerId, p => p with { Status = Peer.State.Rejected });
        //     Log.Debug("[Network] Peer {PeerId} rejected connection", peerId);
        //     return;
        // }
        //
        // if (info.Reason is DisconnectReason.RemoteConnectionClose or DisconnectReason.DisconnectPeerCalled)
        // {
        //     // if (_peers.TryRemove(tag.PeerId, out _)) _publish.OnNext(Unit.Default);
        //     return;
        // }

        // UpdatePeer(tag.PeerId, p => p with { Status = Peer.State.Failed });
    }

    private void OnMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var packet = _codecs.Decode(reader);
        if (packet == null)
        {
            Log.Error("[Network] An error occurred while decoding message {Data}", reader.RawData);
            return;
        }
        
        if (packet is PeerTags tags) OnPeerTags(peer, tags);
        // if (packet is Ping ping) OnPing(peer, ping);
        // if (packet is Pong pong) OnPong(peer, pong);
        // if (packet is PeerName name) OnPeerName(peer, name);

        if (!_streams.TryGetValue(packet.GetType(), out var subjObj)) return;
        var onNextMethod = subjObj.GetType().GetMethod("OnNext");
        onNextMethod!.Invoke(subjObj, [packet]);
    }

    // private void OnPeerName(NetPeer peer, PeerName name)
    // {
    //     _peerNames.AddOrUpdate(peer, name.Name, (key, value) => name.Name);
    // }

    private void OnPeerTags(NetPeer peer, PeerTags tags)
    {
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Any);
        
        foreach (var tag in tags.Peers)
        {
            if (tag.PeerId == _peerId) continue;
            _peerNames.AddOrUpdate(peer, tag.Name, (_, _) => tag.Name);
            
            if (!peers.Any(p => p.Tag.Equals(tag.PeerId))) AskIntroduce(tag.PeerId);
        }
    }

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

    public async Task<bool> Connect(string targetPeerId, CancellationToken ct)
    {
        try
        {
            await Introduce(ct);
        
            // UpdatePeer(targetPeerId, peer => peer);

            AskIntroduce(targetPeerId);

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

    public void Disconnect() => _net.DisconnectAll();

    public void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull
    {
        var data = _codecs.Encode(packet);
        if (data.Length == 0) return;
        var method = unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
        _net.SendToAll(data, method);
    }

    public IObservable<TPacket> Stream<TPacket>() =>
        ((IObservable<TPacket>)_streams.GetOrAdd(typeof(TPacket), _ => new Subject<TPacket>()))
        .ObserveOn(TaskPoolScheduler.Default);

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new() =>
        _codecs.Add<TPacket, TCodec>();

    private async Task Introduce(CancellationToken ct)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(_host, ct);
            var ip = ips.First(x => x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
            _stunEndpoint = new(ip, StunPort);
            _net.NatPunchModule.SendNatIntroduceRequest(_stunEndpoint, $"{_peerId}|{_codecs.Schema}");
        }
        catch (Exception) { /* ignore */ }
    }

    private void AskIntroduce(string targetPeerId)
    {
        if (_stunEndpoint == null) return;

        _net.SendUnconnectedMessage(NetDataWriter.FromString($"DIRECT|{_peerId}|{targetPeerId}"), _stunEndpoint);
        Log.Debug("[Network] DIR {SelfId} -> {TargetId}", _peerId, targetPeerId);

        // Start fallback ONLY for direct attempts
        // ScheduleRelayFallback(targetPeerId);
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
                // if (_peers.TryGetValue(targetPeerId, out var p) && p.Status == Peer.State.Success) return;

                // Ask STUN to introduce us to relay-as-peer
                if (_stunEndpoint != null)
                {
                    
                    var peer = _net.Connect(_stunEndpoint, $"{_peerId}|{targetPeerId}|{_codecs.Schema}");
                    if (peer != null) peer.Tag = targetPeerId;
                    // _net.SendUnconnectedMessage(NetDataWriter.FromString($"RELAY|{_peerId}|{targetPeerId}"), _stunEndpoint);
                    Log.Debug("[Network] RLY {SelfId} -> {TargetId}", _peerId, targetPeerId);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Network] Relay fallback timer failed for {PeerId}", targetPeerId); }
        }, CancellationToken.None);
    }

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