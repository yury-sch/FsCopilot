namespace FsCopilot.Network;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using Serilog;

public sealed class LiteNetPeer2Peer : IPeer2Peer, IDisposable
{
    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(20);
    
    private readonly CancellationTokenSource _cts = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<Type, object> _streams = new();
    private readonly Subject<Unit> _publish = new();
    private readonly PacketRegistry _packetRegistry = new PacketRegistry()
        .RegisterPacket<PeerTag, PeerTag.Codec>()
        .RegisterPacket<PeerList, PeerList.Codec>();
    
    private readonly string _host;
    private readonly string _peerId;
    private readonly string _selfName;
    private readonly NetManager _net;

    private string _schema = string.Empty;
    private IPEndPoint? _discoveryHost;
    private DateTime _nextHelloTime = DateTime.MinValue;

    public IObservable<ICollection<Peer>> Peers { get; }

    public LiteNetPeer2Peer(string host, string peerId, string name)
    {
        _host = host;
        _peerId = peerId;
        _selfName = name;

        _net = new(_listener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            DisconnectTimeout = 15000
        };
        _net.NatPunchModule.Init(_natListener);
        if (!_net.Start())
            throw new InvalidOperationException($"Failed to start NetManager on port {_net.LocalPort}");

        _natListener.NatIntroductionSuccess += OnNatListenerOnNatIntroductionSuccess;
        _listener.ConnectionRequestEvent += OnListenerOnConnectionRequestEvent;
        _listener.PeerConnectedEvent += _ => SendAll(new PeerTag(_peerId, _selfName));
        _listener.PeerDisconnectedEvent += OnListenerOnPeerDisconnectedEvent;
        _listener.NetworkLatencyUpdateEvent += OnListenerOnNetworkLatencyUpdateEvent;
        _listener.NetworkReceiveEvent += OnNetworkReceiveEvent;

        Peers = _publish
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(_ => _peers.Values.Where(p => p.PeerId != _peerId).ToList())
            .Publish()
            .RefCount();

        Task.Run(() => PollLoop(_cts.Token), _cts.Token);
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

    private async Task PollLoop(CancellationToken ct)
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

                await Task.Delay(15, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal
            }
        }
    }

    private void OnNatListenerOnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string token) 
        => _net.Connect(endpoint, _schema);

    private void OnListenerOnConnectionRequestEvent(ConnectionRequest request)
    {
        if (_schema.Equals(request.Data.GetString())) request.Accept();
        else request.Reject(NetDataWriter.FromString(_peerId));
    }

    private void OnListenerOnPeerDisconnectedEvent(NetPeer peer, DisconnectInfo info)
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
            if (_peers.TryRemove(tag.PeerId, out var _)) _publish.OnNext(Unit.Default);
            return;
        }

        UpdatePeer(tag.PeerId, p => p with { Status = Peer.State.Failed });
        Log.Information("[Network] Peer {PeerId} disconnected by reason {Reason}", tag.PeerId, info.Reason);
    }

    private void OnListenerOnNetworkLatencyUpdateEvent(NetPeer peer, int _)
    {
        if (peer.Tag is not PeerTag info) return;
        UpdatePeer(info.PeerId, p => p with { 
            Rtt = peer.Ping,
            Loss = peer.Statistics.PacketLossPercent
        });
    }

    private void OnNetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
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

            if (packet is PeerTag hello) HandlePeerTag(peer, hello);
            if (packet is PeerList list) HandlePeerList(peer, list);

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

            // Let's add the target to knowns to avoid making unnecessary CALLs if it pops up from PeerList
            _peers.TryAdd(targetPeerId, new(
                    PeerId: targetPeerId,
                    Name: string.Empty,
                    Address: null,
                    Rtt: 0,
                    Loss: 0,
                    Status: Peer.State.Pending));
            _publish.OnNext(Unit.Default);

            SendCall(targetPeerId);

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

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);

        bw.Write(packetId);
        codec.Encode(packet, bw);
        bw.Flush();
        var data = ms.ToArray();
 
        _net.SendToAll(data, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }

    public IObservable<TPacket> Stream<TPacket>() =>
        ((IObservable<TPacket>)_streams.GetOrAdd(typeof(TPacket), _ => new Subject<TPacket>()))
        .ObserveOn(TaskPoolScheduler.Default);

    public void RegisterPacket<TPacket, TCodec>()
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new()
    {
        _packetRegistry.RegisterPacket<TPacket, TCodec>();
        _schema += SchemaFingerprint.For<TPacket>();
    }

    private async Task Introduce(CancellationToken ct)
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync(_host, ct);
            var ip = ips.First(x => x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
            _discoveryHost = new IPEndPoint(ip, 3480);
        }
        catch (Exception e)
        {
            _discoveryHost = null;
            return;
        }
        
        if (_discoveryHost == null) return;

        _net.NatPunchModule.SendNatIntroduceRequest(_discoveryHost, _peerId);
    }

    private void SendCall(string targetPeerId)
    {
        if (_discoveryHost == null) return;

        var msg = $"CALL|{_peerId}|{targetPeerId}";
        var writer = new NetDataWriter();
        writer.Put(msg);

        _net.SendUnconnectedMessage(writer, _discoveryHost);
        Log.Debug("[Network] CALL {SelfId} -> {TargetId}", _peerId, targetPeerId);
    }

    private void BroadcastPeerList()
    {
        Log.Debug("[Network] Broadcasting peer list: {Ids}", string.Join(", ", _peers.Keys));
        SendAll(new PeerList(_peers.Values.Select(p => new PeerTag(p.PeerId, p.Name)).ToArray()));
    }

    private void HandlePeerTag(NetPeer peer, PeerTag tag)
    {
        peer.Tag = tag;
        
        _peers.AddOrUpdate(tag.PeerId, id => new(tag.PeerId, tag.Name, peer.Address, 0, 0, Peer.State.Success), (_, old) => old with
        {
            Name = tag.Name,
            Address = peer.Address,
            Status = Peer.State.Success
        });
        _publish.OnNext(Unit.Default);
        // UpdatePeer(tag.PeerId, p => p with
        // {
        //     Name = tag.Name, 
        //     Address = peer.Address, 
        //     Status = Peer.State.Success
        // });

        BroadcastPeerList();
    }

    private void HandlePeerList(NetPeer _, PeerList list)
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
                    Address: null,
                    Rtt: 0,
                    Loss: 0,
                    Status: Peer.State.Pending))) continue;

            Log.Debug("[Network] Discovered peer via list: {PeerId}", peer.PeerId);
            anyNew = true;

            SendCall(peer.PeerId);
        }

        if (anyNew) _publish.OnNext(Unit.Default);
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
}