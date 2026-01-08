namespace FsCopilot.Network;

using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Open.Nat;

public sealed class P2PNetwork : INetwork, IDisposable
{
    private const int StunPort = 3480;
    
    private static readonly TimeSpan IntroduceInterval = TimeSpan.FromSeconds(20);
    
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly EventBasedNetListener _netListener = new();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly Subject<Unit> _publish = new();
    private readonly Codecs _codecs = new Codecs()
            .Add<PeerTags, PeerTags.Codec>();
    
    private readonly ConcurrentDictionary<string, string> _peerNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ConnectionResult>> _connectWaiters = new(StringComparer.Ordinal);
    
    private readonly string _host;
    private readonly string _peerId;
    private readonly string _selfName;
    private readonly bool _autoConnect;
    private readonly NetManager _net;

    private IPEndPoint? _stunEndpoint;

    public IObservable<ICollection<Peer>> Peers { get; }

    public P2PNetwork(string host, string peerId, string name, bool autoConnect = true)
    {
        _host = host;
        _peerId = peerId;
        _selfName = name;
        _autoConnect = autoConnect;

        _net = new(_netListener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            DisconnectTimeout = 15000
        };
        _net.NatPunchModule.Init(_natListener);

        _natListener.NatIntroductionSuccess += OnNatIntroduction;
        _netListener.ConnectionRequestEvent += OnConnectionRequest;
        _netListener.PeerConnectedEvent += OnConnectionSuccess;
        _netListener.PeerDisconnectedEvent += OnPeerDisconnected;
        _netListener.NetworkReceiveEvent += OnMessage;
        _netListener.NetworkReceiveUnconnectedEvent += OnUnconnected;

        var peers = new List<NetPeer>();
        Peers = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ =>
            {
                _net.GetPeersNonAlloc(peers, ConnectionState.Any);
                return peers
                    .Where(p => p.Tag is string)
                    .Select(p => new Peer(
                        (string)p.Tag, 
                        _peerNames.TryGetValue((string)p.Tag, out var peerName) ? peerName : string.Empty, 
                        p.Ping,
                        Peer.TransportKind.Direct))
                    .ToArray();
            })
            .Publish()
            .RefCount();
        
        Stream<PeerTags>().Subscribe(OnPeerTags, _cts.Token);

        Task.Run(() => Start(_cts.Token), _cts.Token);
        Task.Run(() => Loop(_cts.Token), _cts.Token);
        Task.Run(() => IntroduceLoop(_cts.Token), _cts.Token);
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

    private async Task Start(CancellationToken ct)
    {
        var portMapper = PortMapper.Upnp;
        var device = await GetNat(portMapper, ct);
        if (device == null)
        {
            portMapper = PortMapper.Pmp;
            await GetNat(PortMapper.Upnp, ct);
        }
        
        if (device != null)
        {
            try
            {
                _net.Start(device.LocalAddress, IPAddress.IPv6Any, 0);
                Log.Information("[Peer2Peer] BIND {Address}", device.LocalAddress);
            }
            catch (Exception)
            {
                _net.Start();
            }
        }
        else
        {
            _net.Start();
            return;
        }

        try
        {
            await device.CreatePortMapAsync(new(Protocol.Udp, _net.LocalPort, _net.LocalPort, "FS Copilot"));
            Log.Information("[Peer2Peer] {Mapper} {Host} -> {Address}", portMapper == PortMapper.Upnp ? "UPnP" : "NAT-PMP", 
                device.HostEndPoint.Address, new IPEndPoint(device.LocalAddress, _net.LocalPort));
        }
        catch { /* ignore */ }
    }

    private static async Task<NatDevice?> GetNat(PortMapper mapper, CancellationToken ct)
    {
        try
        {
            var discoverer = new NatDiscoverer();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            return await discoverer.DiscoverDeviceAsync(mapper, timeoutCts);
        }
        catch (Exception)
        {
            return null;
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

                await Task.Delay(15, ct);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Peer2Peer] Loop error"); }
        }
    }

    private async Task IntroduceLoop(CancellationToken ct)
    {
        await Task.Delay(1000, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureIntroduced(ct);
                await Task.Delay(IntroduceInterval, ct);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception e) { Log.Error(e, "[Peer2Peer] Failed to introduce"); }
        }
    }

    private void OnNatIntroduction(IPEndPoint endpoint, NatAddressType type, string token)
    {
        var parts = token.Split('|');
        if (parts.Length < 3) return;
        
        var targetPeer = parts.Skip(1).First(p => p != _peerId);
        Log.Debug("[Peer2Peer] NAT {Peer} -> {Address}",targetPeer, endpoint);

        var peer = _net.Connect(endpoint, $"{_peerId}|{targetPeer}|{_codecs.Schema}");
        if (peer == null) return; // already awaiting
        peer.Tag = targetPeer;
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var token = request.Data.GetString();
        var parts = token.Split("|");
        if (parts.Length < 3) { request.Reject(); return; }
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
        Log.Debug("[Peer2Peer] CON {Peer} -> {Address}", peerId, new IPEndPoint(peer.Address, peer.Port));

        if (!string.IsNullOrEmpty(peerId) && _connectWaiters.TryGetValue(peerId, out var tcs))
            tcs.TrySetResult(ConnectionResult.Success);
        
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Any);

        var peerList = new PeerTags(peers
            .Where(p => p.Tag is string)
            .Select(p => new PeerTags.Tag(
                (string)p.Tag,
                _peerNames.TryGetValue((string)p.Tag, out var peerName) ? peerName : string.Empty))
            .Prepend(new(_peerId, _selfName))
            .ToArray());

        var data = _codecs.Encode(peerList);
        if (data.Length == 0) return;
        peer.Send(data, DeliveryMethod.ReliableUnordered);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        var peerId = peer.Tag as string ?? string.Empty;
        Log.Debug("[Peer2Peer] DIS {PeerId} -> {Address} ({Reason})",
            peerId, new IPEndPoint(peer.Address, peer.Port), info.Reason);

        if (string.IsNullOrEmpty(peerId))
            return;

        if (info.Reason == DisconnectReason.ConnectionRejected)
        {
            if (_connectWaiters.TryGetValue(peerId, out var tcs))
                tcs.TrySetResult(ConnectionResult.Rejected);

            Log.Debug("[Peer2Peer] REJ {PeerId} {Reason}", peerId, "VERSION_MISMATCH");
            return;
        }

        // If we were waiting for this connection and it died -> fail
        if (_connectWaiters.TryGetValue(peerId, out var waiter))
            waiter.TrySetResult(ConnectionResult.Failed);
    }

    private void OnMessage(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var packet = _codecs.Decode(reader);
        if (packet == null)
        {
            Log.Error("[Peer2Peer] An error occurred while decoding message {Data}", reader.RawData);
            return;
        }

        if (!_streams.TryGetValue(packet.GetType(), out var subjObj)) return;
        var onNextMethod = subjObj.GetType().GetMethod("OnNext");
        onNextMethod!.Invoke(subjObj, [packet]);
    }
    
    private void OnUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
    {
        if (type != UnconnectedMessageType.BasicMessage)
        {
            reader.Recycle();
            return;
        }

        var msg = reader.GetString();
        reader.Recycle();

        // REJECT|selfId|targetId|NOT_FOUND
        if (!msg.StartsWith("REJECT|", StringComparison.Ordinal)) return;

        var parts = msg.Split('|');
        if (parts.Length < 4) return;

        var selfId = parts[1];
        var targetId = parts[2];
        var reason = parts[3];

        Log.Debug("[Peer2Peer] REJ {PeerId} {Reason}", targetId, reason);
        // If Connect(targetId) is waiting — resolve immediately
        if (_connectWaiters.TryGetValue(targetId, out var tcs))
            tcs.TrySetResult(ConnectionResult.Failed);
    }

    private void OnPeerTags(PeerTags tags)
    {
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Any);
        
        foreach (var tag in tags.Peers)
        {
            if (tag.PeerId == _peerId) continue;
            _peerNames.AddOrUpdate(tag.PeerId, tag.Name, (_, _) => tag.Name);
            
            if (_autoConnect && !peers.Any(p => p.Tag.Equals(tag.PeerId))) AskIntroduce(tag.PeerId);
        }
    }

    public async Task<ConnectionResult> Connect(string target, CancellationToken ct)
    {
        if (target.Trim().Equals(_peerId, StringComparison.OrdinalIgnoreCase)) return ConnectionResult.Failed;
        // Already connected?
        var peers = new List<NetPeer>();
        _net.GetPeersNonAlloc(peers, ConnectionState.Connected);
        if (peers.Any(p => (p.Tag as string) == target))
            return ConnectionResult.Success;

        var tcs = new TaskCompletionSource<ConnectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_connectWaiters.TryAdd(target, tcs))
            return ConnectionResult.Failed;

        try
        {
            await EnsureIntroduced(ct).ConfigureAwait(false);

            AskIntroduce(target);

            using var reg = ct.Register(
                static s => ((TaskCompletionSource<ConnectionResult>)s!).TrySetResult(ConnectionResult.Failed),
                tcs);

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConnectionResult.Failed;
        }
        catch (InvalidOperationException)
        {
            return ConnectionResult.Failed;
        }
        catch (Exception e)
        {
            Log.Error(e, "[Peer2Peer] Connect failed");
            return ConnectionResult.Failed;
        }
        finally
        {
            _connectWaiters.TryRemove(target, out _);
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
    
    private async Task<IPEndPoint?> ResolveStunEndpoint(CancellationToken ct)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(_host, ct);
            var ip = ips.FirstOrDefault(x =>
                x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
            return ip is null ? null : new IPEndPoint(ip, StunPort);
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureIntroduced(CancellationToken ct)
    {
        if (_stunEndpoint is null)
            _stunEndpoint = await ResolveStunEndpoint(ct);

        if (_stunEndpoint is null)
            throw new InvalidOperationException("STUN endpoint resolution failed");
        _net.NatPunchModule.SendNatIntroduceRequest(_stunEndpoint, _peerId);
    }

    private void AskIntroduce(string targetPeerId)
    {
        if (_stunEndpoint == null) return;
        _net.SendUnconnectedMessage(NetDataWriter.FromString($"CALL|{_peerId}|{targetPeerId}"), _stunEndpoint);
        Log.Debug("[Peer2Peer] DIR {SelfId} -> {TargetId}", _peerId, targetPeerId);
    }
}