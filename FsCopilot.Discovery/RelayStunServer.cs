namespace P2PDiscovery;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class RelayStunServer : BackgroundService
{
    private const int StunPort = 3481;

    // How long we keep peer presence (HELLO refreshes it)
    private static readonly TimeSpan PeerTtl = TimeSpan.FromMinutes(5);

    // How long we keep relay sessions without traffic / connections
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);

    private readonly ILogger<RelayStunServer> _logger;
    private readonly RelayOptions _options;

    // === STUN / Discovery net (unconnected + NatPunchModule) ===
    private readonly EventBasedNetListener _stunListener = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly NetManager _stunNet;

    // === Relay net (connected peers; forwards packets) ===
    private readonly EventBasedNetListener _relayListener = new();
    private readonly NetManager _relayNet;

    // Public endpoint clients should connect to for relay.
    // You should configure it explicitly (public server IP + relay port).
    private IPEndPoint _relayPublic;

    // peerId -> info (from HELLO)
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);

    // external endpoint -> peerId (reverse mapping to identify who connected to relay)
    private readonly ConcurrentDictionary<EndPointKey, string> _peerIdByExternal = new();

    // sessionKey -> session
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);

    public RelayStunServer(
        ILogger<RelayStunServer> logger,
        IOptions<RelayOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        // --- STUN net ---
        _stunNet = new NetManager(_stunListener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            IPv6Enabled = true,
            DisconnectTimeout = 15000
        };
        _stunNet.NatPunchModule.Init(_natListener);

        _stunListener.ConnectionRequestEvent += r => r.Reject();
        _stunListener.NetworkReceiveUnconnectedEvent += OnUnconnectedMessage;
        _natListener.NatIntroductionRequest += OnNatIntroductionRequest;

        // --- Relay net ---
        _relayNet = new NetManager(_relayListener)
        {
            NatPunchEnabled = false,
            UnconnectedMessagesEnabled = false,
            IPv6Enabled = true,
            DisconnectTimeout = 15000
        };

        _relayListener.ConnectionRequestEvent += OnRelayConnectionRequest;
        _relayListener.PeerConnectedEvent += OnRelayPeerConnected;
        _relayListener.PeerDisconnectedEvent += OnRelayPeerDisconnected;
        _relayListener.NetworkReceiveEvent += OnRelayReceive;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_stunNet.Start(StunPort))
            throw new InvalidOperationException($"Failed to start STUN on UDP port '{StunPort}'.");

        // Relay can be on a different port than STUN (recommended)
        if (!_relayNet.Start(_relayPublic.Port))
            throw new InvalidOperationException($"Failed to start RELAY on UDP port '{_relayPublic.Port}'.");

        _logger.LogInformation("STUN started on UDP {Port}", StunPort);
        _logger.LogInformation("RELAY started on UDP {Port} (public={Public})", _relayPublic.Port, _relayPublic);

        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping STUN/RELAY...");
        _stunNet.Stop();
        _relayNet.Stop();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(_options.PublicHost, ct);
        var ip = addresses.First(a =>
            a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);

        _relayPublic = new(ip, _options.Port);
        
        var lastCleanup = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _stunNet.PollEvents();
                _stunNet.NatPunchModule.PollEvents();

                _relayNet.PollEvents();

                var now = DateTime.UtcNow;
                if (now - lastCleanup >= CleanupInterval)
                {
                    Cleanup(now);
                    lastCleanup = now;
                }

                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server loop crashed");
        }
        finally
        {
            _logger.LogInformation("Server loop stopped.");
        }
    }

    // =========================
    // STUN: HELLO (NatIntroduceRequest)
    // =========================
    private void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string selfId)
    {
        if (string.IsNullOrWhiteSpace(selfId))
        {
            _logger.LogWarning("Invalid selfId in HELLO from {Remote}", remoteEndPoint);
            return;
        }

        var now = DateTime.UtcNow;
        var info = new PeerInfo(localEndPoint, remoteEndPoint, now);

        _peers[selfId] = info;
        _peerIdByExternal[new EndPointKey(remoteEndPoint)] = selfId;

        _logger.LogInformation("HELLO {PeerId}: internal={Internal}, external={External}", selfId, info.Internal, info.External);
    }

    // =========================
    // STUN: Unconnected commands (CALL / DIRECT / RELAY)
    // =========================
    private void OnUnconnectedMessage(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (messageType != UnconnectedMessageType.BasicMessage)
        {
            reader.Recycle();
            return;
        }

        var text = reader.GetString();
        reader.Recycle();

        try
        {
            if (text.StartsWith("CALL|", StringComparison.Ordinal))
                HandleCall(text);
            
            else if (text.StartsWith("DIRECT|", StringComparison.Ordinal))
                HandleDirect(text);

            else if (text.StartsWith("RELAY|", StringComparison.Ordinal))
                HandleRelayRequest(text);

            else
                _logger.LogWarning("Unknown unconnected message '{Text}' from {Remote}", text, remoteEndPoint);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while processing unconnected message '{Text}' from {Remote}", text, remoteEndPoint);
        }
    }

    private void HandleCall(string text)
    {
        // text = "CALL|SELFID|TARGETID"
        var parts = text.Split('|');
        if (parts.Length != 3) return;

        var a = parts[1].Trim();
        var b = parts[2].Trim();

        if (!_peers.TryGetValue(a, out var aInfo))
        {
            _logger.LogWarning("CALL from {A}, but not registered", a);
            return;
        }
        if (!_peers.TryGetValue(b, out var bInfo))
        {
            _logger.LogInformation("CALL {A} -> {B}: target not found", a, b);
            return;
        }

        var token = $"{a}|{b}";

        _logger.LogInformation("CALL {A} ({AExt}) -> {B} ({BExt}) => NatIntroduce", a, aInfo.External, b, bInfo.External);

        _stunNet.NatPunchModule.NatIntroduce(
            aInfo.Internal, aInfo.External,
            bInfo.Internal, bInfo.External,
            token);
    }

    private void HandleDirect(string text)
    {
        // text = "DIRECT|SELFID|TARGETID"
        var parts = text.Split('|');
        if (parts.Length != 3) return;

        var a = parts[1].Trim();
        var b = parts[2].Trim();

        if (!_peers.TryGetValue(a, out var aInfo))
        {
            _logger.LogWarning("DIRECT from {A}, but not registered", a);
            return;
        }
        if (!_peers.TryGetValue(b, out var bInfo))
        {
            _logger.LogInformation("DIRECT {A} -> {B}: target not found", a, b);
            return;
        }

        var token = $"{a}|{b}";

        _logger.LogInformation("DIRECT {A} ({AExt}) -> {B} ({BExt}) => NatIntroduce", a, aInfo.External, b, bInfo.External);

        _stunNet.NatPunchModule.NatIntroduce(
            aInfo.Internal, aInfo.External,
            bInfo.Internal, bInfo.External,
            token);
    }

    private void HandleRelayRequest(string text)
    {
        // text = "RELAY|A|B"
        var parts = text.Split('|');
        if (parts.Length != 3) return;

        var a = parts[1].Trim();
        var b = parts[2].Trim();

        if (!_peers.TryGetValue(a, out var aInfo))
        {
            _logger.LogWarning("RELAY from {A}, but not registered", a);
            return;
        }
        if (!_peers.TryGetValue(b, out var bInfo))
        {
            _logger.LogInformation("RELAY {A} -> {B}: target not found", a, b);
            return;
        }

        var key = MakeSessionKey(a, b);
        var now = DateTime.UtcNow;

        _sessions.AddOrUpdate(
            key,
            _ => new RelaySession(a, b, now),
            (_, old) => old with { LastTouched = now });

        // Token format: client code "token.Split('|').Last(p => p != _peerId)" still works.
        // Example: RLY|A|B
        var token = $"RLY|{a}|{b}";

        // IMPORTANT: we introduce EACH peer with RELAY endpoint as "the other side".
        // This causes both clients to receive NatIntroductionSuccess(endpoint=relayPublic, token=...)
        // and then connect to relay server as if it was the peer.
        _logger.LogInformation("RELAY {A} <-> {B} via {Relay} (token={Token})", a, b, _relayPublic, token);

        // relay "internal" and "external" are both the public endpoint here.
        // If your server has separate internal/public, pass proper internal too.
        _stunNet.NatPunchModule.NatIntroduce(
            _relayPublic, _relayPublic,
            aInfo.Internal, aInfo.External,
            token);
        
        _stunNet.NatPunchModule.NatIntroduce(
            _relayPublic, _relayPublic,
            bInfo.Internal, bInfo.External,
            token);
    }

    // =========================
    // RELAY: Accept connections
    // =========================
    private void OnRelayConnectionRequest(ConnectionRequest request)
    {
        // We can accept all, but we will only bridge peers that:
        // 1) are known (HELLO was received)
        // 2) belong to some active relay session
        request.Accept();
    }

    private void OnRelayPeerConnected(NetPeer peer)
    {
        // Identify peerId by its external endpoint seen in HELLO.
        // This avoids decoding any app packets on server.
        var key = new EndPointKey(peer.Address, peer.Port);

        if (!_peerIdByExternal.TryGetValue(key, out var peerId))
        {
            _logger.LogWarning("RELAY connected unknown endpoint {EndPoint} (no HELLO mapping). Disconnecting.", peer.Address);
            peer.Disconnect();
            return;
        }

        peer.Tag = peerId;

        // Attach this connection to a session if possible
        var attached = false;
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (s.Contains(peerId))
            {
                _sessions[kv.Key] = s.Attach(peerId, peer, DateTime.UtcNow);
                attached = true;
                break;
            }
        }

        _logger.LogInformation("RELAY connected {PeerId} from {EndPoint} (attached={Attached})", peerId, peer.Address, attached);
    }

    private void OnRelayPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        var peerId = peer.Tag as string;
        if (string.IsNullOrEmpty(peerId)) return;

        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (!s.Contains(peerId)) continue;

            _sessions[kv.Key] = s.Detach(peerId, DateTime.UtcNow);
            break;
        }

        _logger.LogInformation("RELAY disconnected {PeerId} reason={Reason}", peerId, info.Reason);
    }

    // =========================
    // RELAY: Forward packets byte-to-byte
    // =========================
    private void OnRelayReceive(NetPeer from, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var fromId = from.Tag as string;
        if (string.IsNullOrEmpty(fromId))
        {
            reader.Recycle();
            return;
        }

        // Find matching session + the other side
        RelaySession? session = null;
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (s.Contains(fromId))
            {
                session = s;
                break;
            }
        }

        if (session is null)
        {
            reader.Recycle();
            return;
        }

        var to = session.Value.GetOther(fromId);
        if (to is null || to.ConnectionState != ConnectionState.Connected)
        {
            reader.Recycle();
            return;
        }

        // Forward raw packet bytes
        var len = reader.AvailableBytes;
        var buf = ArrayPool<byte>.Shared.Rent(len);

        try
        {
            reader.GetBytes(buf, len);
            to.Send(buf, 0, len, channel, method);
            session = session.Value with { LastTouched = DateTime.UtcNow };
            _sessions[MakeSessionKey(session.Value.A, session.Value.B)] = session.Value;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            reader.Recycle();
        }
    }

    // =========================
    // Cleanup
    // =========================
    private void Cleanup(DateTime now)
    {
        foreach (var kv in _peers)
        {
            if (now - kv.Value.LastSeen <= PeerTtl) continue;

            if (_peers.TryRemove(kv.Key, out var removed))
            {
                _peerIdByExternal.TryRemove(new EndPointKey(removed.External), out _);
                _logger.LogInformation("Removed stale peer {PeerId}", kv.Key);
            }
        }

        foreach (var kv in _sessions)
        {
            if (now - kv.Value.LastTouched <= SessionTtl) continue;

            if (_sessions.TryRemove(kv.Key, out var removed))
                _logger.LogInformation("Removed stale relay session {SessionKey} ({A}<->{B})", kv.Key, removed.A, removed.B);
        }
    }

    private static string MakeSessionKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    // =========================
    // Types
    // =========================
    private sealed record PeerInfo(IPEndPoint Internal, IPEndPoint External, DateTime LastSeen);

    private readonly record struct EndPointKey(IPAddress Address, int Port)
    {
        public EndPointKey(IPEndPoint ep) : this(ep.Address, ep.Port) { }
    }

    private readonly record struct RelaySession(string A, string B, DateTime LastTouched)
    {
        public NetPeer? ConnA { get; init; }
        public NetPeer? ConnB { get; init; }

        public bool Contains(string peerId) => peerId == A || peerId == B;

        public NetPeer? GetOther(string peerId)
        {
            if (peerId == A) return ConnB;
            if (peerId == B) return ConnA;
            return null;
        }

        public RelaySession Attach(string peerId, NetPeer conn, DateTime now)
        {
            if (peerId == A) return this with { ConnA = conn, LastTouched = now };
            if (peerId == B) return this with { ConnB = conn, LastTouched = now };
            return this with { LastTouched = now };
        }

        public RelaySession Detach(string peerId, DateTime now)
        {
            if (peerId == A) return this with { ConnA = null, LastTouched = now };
            if (peerId == B) return this with { ConnB = null, LastTouched = now };
            return this with { LastTouched = now };
        }
    }
}