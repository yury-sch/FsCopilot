namespace FsCopilot.Network;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Channels;
using Serilog;

public class Peer2Peer : IAsyncDisposable
{
    private const int MaxBufferSize = 60000;
    
    private readonly CancellationTokenSource _cts = new();
    private readonly string _host;
    private readonly Socket _sock;
    private readonly Channel<SendItem> _sendChan;
    private readonly Channel<Datagram> _handleChan;
    private readonly Dictionary<SystemPacketTypes, Action<string, IPEndPoint, BinaryReader>> _handlers;
    private readonly SeenCache _seen;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _punches = new();
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly PacketRegistry _packetRegistry = new();
    private readonly ConcurrentDictionary<Type, IPacketSubject> _subjects = new();
    private readonly BehaviorSubject<IPEndPoint?> _discoveredAddress = new(null);
    private readonly ReplaySubject<Connection> _connectionEstablished = new();
    private readonly ReplaySubject<Connection> _connectionLost = new();

    private readonly Task _handleLoop;
    private readonly Task _receiveLoop;
    private readonly Task _sendLoop;
    private readonly Task _discoveryLoop;
    private readonly Task _pingLoop;

    private IPEndPoint? _discoveryHost;
    private DateTime _lastDiscovery = DateTime.MinValue;

    public string PeerId { get; }
    // public IPEndPoint? DiscoveredEndpoint { get; private set; }
    public ICollection<Connection> Connections => _connections.Values;
    public IObservable<IPEndPoint?> DiscoveredEndpoint => _discoveredAddress;
    public IObservable<Connection> ConnectionEstablished => _connectionEstablished;
    public IObservable<Connection> ConnectionLost => _connectionLost;
    // public event DiscoveredHandler? DiscoveryChanged;
    // public event ConnectedHandler? ConnectionEstablished;
    // public event DisconnectedHandler? ConnectionLost;

    public Peer2Peer(string host, int localPort)
    {
        _host = host;
        PeerId = GenerateRandomPeer(8);
        _sock = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            { DualMode = true, ReceiveTimeout = 0 };
        _sock.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));

        _handleChan = Channel.CreateBounded<Datagram>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _sendChan = Channel.CreateBounded<SendItem>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _handleLoop = Task.Run(() => HandleLoop(_cts.Token));
        _receiveLoop = Task.Run(() => ReceiveLoop(_cts.Token));
        _sendLoop = Task.Run(() => SendLoop(_cts.Token));
        _discoveryLoop = Task.Run(() => DiscoveryLookAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingLoop(_cts.Token));
        _handlers = new()
        {
            [SystemPacketTypes.DISCOVER] = DiscoverHandle,
            [SystemPacketTypes.CONNECT] = ConnectHandle,
            [SystemPacketTypes.PUNCH] = PunchHandle,
            [SystemPacketTypes.HOLE] = HoleHandle,
            [SystemPacketTypes.PING] = PingHandle,
            [SystemPacketTypes.PONG] = PongHandle,
            [SystemPacketTypes.DATA] = DataHandle,
        };
        _seen = new(TimeSpan.FromMinutes(30), sizeLimit: 100_000);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _handleChan.Writer.TryComplete();
        _sendChan.Writer.TryComplete();
        try { await _handleLoop; } catch { /* ignore */ }
        try { await _pingLoop; } catch { /* ignore */ }
        try { await _discoveryLoop; } catch { /* ignore */ }
        try { await _receiveLoop; } catch { /* ignore */ }
        try { await _sendLoop; } catch { /* ignore */ }
        _cts.Dispose();
    }

    private async Task DiscoveryLookAsync(CancellationToken ct)
    {
        _connectionEstablished.Subscribe(async void (connection) =>
        {
            try
            {
                if (_discoveryHost == null) return;
                foreach (var active in _connections.Where(c => c.Key != connection.PeerId))
                    Send(SystemPacketTypes.CONNECT, bw =>
                    {
                        bw.Write(active.Key);
                        bw.Write(connection.PeerId);
                    }, _discoveryHost);
            }
            catch (Exception) { /* ignore */ }
        });
        
        await Task.Delay(100, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync(_host, ct);
                var ip = ips.First(x => x.AddressFamily is AddressFamily.InterNetworkV6 or AddressFamily.InterNetwork);
                _discoveryHost = new(ip, 3478);
            }
            catch (Exception)
            {
                if (_discoveredAddress.Value != null) _discoveredAddress.OnNext(null);
                _discoveryHost = null;
                await Task.Delay(5000, ct);
                continue;
            }

            if (_discoveredAddress.Value != null && _lastDiscovery < DateTime.UtcNow - TimeSpan.FromMinutes(1))
            {
                _discoveredAddress.OnNext(null);
            }

            Send(SystemPacketTypes.DISCOVER, bw =>
            {
                var local = GetLocalCandidates(((IPEndPoint)_sock.LocalEndPoint!).Port).ToArray();
                bw.Write(local.Length);
                foreach (var lep in local)
                    bw.Write(lep.ToString());
            }, _discoveryHost);
            await Task.Delay(20000, ct);
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buf = ArrayPool<byte>.Shared.Rent(MaxBufferSize);
                // var buf = new byte[MaxBufferSize];
                EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);

                SocketReceiveFromResult res;
                try
                {
                    res = await _sock.ReceiveFromAsync(new ArraySegment<byte>(buf), SocketFlags.None, from, ct);
                    // from = res.RemoteEndPoint;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log.Error(ex, "An error occurred while receiving data from the socket");
                    Debug.WriteLine(ex.Message);
                    ArrayPool<byte>.Shared.Return(buf);
                    continue;
                }

                var size = res.ReceivedBytes;
                if (size <= 0)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    continue;
                }

                var dg = new Datagram((IPEndPoint)res.RemoteEndPoint!, buf, size);
                if (!_handleChan.Writer.TryWrite(dg))
                {
                    // queue overloaded — freeing the buffer
                    dg.Dispose();
                }
            }
        }
        finally
        {
            _handleChan.Writer.TryComplete();
        }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            while (await _sendChan.Reader.WaitToReadAsync(ct))
            {
                while (_sendChan.Reader.TryRead(out var item))
                {
                    try
                    {
                        await _sock.SendToAsync(item.Buffer, SocketFlags.None, item.To, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                        // ignore network errors and move on
                    }
                }
            }
        }
        finally
        {
            _sendChan.Writer.TryComplete();
        }
    }

    private async Task HandleLoop(CancellationToken ct)
    {
        await foreach (var dg in _handleChan.Reader.ReadAllAsync(ct))
        {
            using var msr = new MemoryStream(dg.Buffer);
            using var br = new BinaryReader(msr, Encoding.UTF8, true);
            var packetType = (SystemPacketTypes)br.ReadByte();
            var peerId = br.ReadString();
            if (_handlers.TryGetValue(packetType, out var handler))
            {
                try
                {
                    handler(peerId, dg.From, br);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An error occured while handling {0}.", packetType);
                }
            }
            dg.Dispose(); // returning the buffer to the pool
        }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);
            
            if (_discoveryHost == null) continue;
            foreach (var connection in _connections)
            {
                if (DateTime.UtcNow - connection.Value.LastSeen > TimeSpan.FromSeconds(10))
                {
                    if (_connections.TryRemove(connection.Key, out _))
                    {
                        _connectionLost.OnNext(connection.Value);
                        Send(SystemPacketTypes.CONNECT, bw =>
                        {
                            bw.Write(PeerId);
                            bw.Write(connection.Key);
                        }, _discoveryHost);
                    }

                    continue;
                }

                Send(SystemPacketTypes.PING, bw => bw.Write(Stopwatch.GetTimestamp()), connection.Value.Ip);
            }
        }
    }

    private void Send(SystemPacketTypes packetType, Action<BinaryWriter> write, IPEndPoint to)
    {
        using var msw = new MemoryStream();
        using var bw = new BinaryWriter(msw, Encoding.UTF8, true);
        bw.Write((byte)packetType);
        bw.Write(PeerId);
        write(bw);
        bw.Flush();
        var data = msw.ToArray();
        _sendChan.Writer.TryWrite(new(data, to));
    }

    private void DiscoverHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        var textIp = br.ReadString();
        if (!IPEndPoint.TryParse(textIp, out var ep)) return;
        _lastDiscovery = DateTime.UtcNow;
        if (_discoveredAddress.Value?.ToString() == ep.ToString()) return;
        _discoveredAddress.OnNext(ep);
    }

    private void ConnectHandle(string targetPeer, IPEndPoint from, BinaryReader br)
    {
        var targets = new List<IPEndPoint>();
        var udpSeen = br.ReadString();

        var localCount = br.ReadInt32();
        for (var i = 0; i < localCount; i++)
        {
            var localAddrStr = br.ReadString();
            if (IPEndPoint.TryParse(localAddrStr, out var localAdd)) targets.Add(localAdd);
        }

        if (IPEndPoint.TryParse(udpSeen, out var remoteEp)) targets.Add(remoteEp);

        // if (_punchCts != null) _punchCts.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        _punches.AddOrUpdate(targetPeer, cts, (_, exist) =>
        {
            exist.Cancel();
            return cts;
        });
        
        _ = Task.Run(async () =>
        {
            var window = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(DateTime.UtcNow < window ? 20 : 10000, cts.Token);
                foreach (var target in targets)
                {
                    Send(SystemPacketTypes.PUNCH, bw => bw.Write(targetPeer), target);
                }
            }
        }, cts.Token);
    }

    private void PunchHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        var target = br.ReadString();
        if (target != PeerId) return;

        Send(SystemPacketTypes.HOLE, _ => { }, from);
    }

    private void HoleHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        if (_punches.TryRemove(peerId, out var punchCts))
        {
            punchCts.Cancel();
            var connection = new Connection(peerId, from, DateTime.UtcNow, 0);
            _connections.AddOrUpdate(peerId, _ =>
            {
                _connectionEstablished.OnNext(connection);
                return connection;
            }, (_, _) => connection);
        }
    }

    private void PingHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        var ts = br.ReadInt64();
        var connection = new Connection(peerId, from, DateTime.UtcNow, 0);
        _connections.AddOrUpdate(peerId, _ =>
        {
            _connectionEstablished.OnNext(connection);
            return connection;
        }, (_, exist) => connection with{ Rtt = exist.Rtt});
        Send(SystemPacketTypes.PONG, bw => bw.Write(ts), from);
    }

    private void PongHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        var sentTicks = br.ReadInt64();
        var nowTicks = Stopwatch.GetTimestamp();
        var rtt = (nowTicks - sentTicks) * 1000.0 / Stopwatch.Frequency;
        var connection = new Connection(peerId, from, DateTime.UtcNow, (uint)rtt);
        _connections.AddOrUpdate(peerId, id =>
        {
            _connectionEstablished.OnNext(connection);
            return connection;
        }, (_, _) => connection);
    }

    private void DataHandle(string peerId, IPEndPoint from, BinaryReader br)
    {
        var bytes = br.ReadBytes(16);
        var msgId = new Guid(bytes);
        
        if (!_seen.TryMarkSeen(msgId)) return; // have seen it yet — drop

        var dataType = br.ReadByte();
        if (!_packetRegistry.TryGetCodec(dataType, out var codec)) return;
        try
        {
            var packet = codec.Decode(br);
            if (_subjects.TryGetValue(packet.GetType(), out var subj))
                subj.Publish(packet);
        }
        catch (Exception e)
        {
            Log.Error(e, "An error occurred while decoding {DataType}", dataType);
        }
    }

    public Task Connect(string target)
    {
        if (_discoveryHost == null) return Task.CompletedTask;
        target = target.Trim().ToUpperInvariant();
        // if (_connections.TryRemove(peerId, out var punchCts))

        // _connections.GetOrAdd(target, (_) => {
        //     var a = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // });
        // _connections.

        // _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // ct.Register(() => _connectTcs.TrySetResult(false));
        Send(SystemPacketTypes.CONNECT, bw =>
        {
            bw.Write(PeerId);
            bw.Write(target);
        }, _discoveryHost);
        // return _connectTcs.Task;
        return Task.CompletedTask;
    }

    public void SendAll<TPacket>(TPacket packet)
    {
        if (!_packetRegistry.TryGetCodec<TPacket>(out var packetId, out var codec)) return;
        
        foreach (var connection in _connections.Values)
        {
            Send(SystemPacketTypes.DATA, bw =>
            {
                bw.Write(Guid.NewGuid().ToByteArray());
                bw.Write(packetId);
                codec.Encode(packet, bw);
            }, connection.Ip);
        }
    }

    public void RegisterPacket<TPacket, TCodec>()
        where TCodec : IPacketCodec<TPacket>, new()
    {
        _packetRegistry.RegisterPacket<TPacket, TCodec>();
    }

    public IDisposable Subscribe<TPacket>(Action<TPacket> action)
    {
        var subject = (PacketSubject<TPacket>)_subjects.GetOrAdd(typeof(TPacket), _ => new PacketSubject<TPacket>());
        return subject.Subscribe(action);
    }

    static IEnumerable<IPEndPoint> GetLocalCandidates(int port)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    yield return new(ua.Address, port);
                }
            }
        }
    }

    static string GenerateRandomPeer(int length)
    {
        var rnd = new Random();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        StringBuilder sb = new(length);
        for (var i = 0; i < length; i++) sb.Append(chars[rnd.Next(chars.Length)]);
        return sb.ToString();
    }

    private readonly record struct SendItem(byte[] Buffer, IPEndPoint To);

    private readonly record struct Datagram(IPEndPoint From, byte[] Buffer, int Count) : IDisposable
    {
        public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
    }

    private enum SystemPacketTypes : byte
    {
        DISCOVER = 0,
        CONNECT = 1,
        PUNCH = 10,
        HOLE = 11,
        PING = 22,
        PONG = 23,
        DATA = 200
    }
}

public record struct Connection(string PeerId, IPEndPoint Ip, DateTime LastSeen, uint Rtt);
