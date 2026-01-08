using System.Buffers;
using LiteNetLib;
using LiteNetLib.Utils;

namespace P2PDiscovery;

public sealed class Relay : BackgroundService
{
    private const int Port = 3600;
    private const byte ControlChannel = 0;
    private const int TickMs = 10;
    
    private readonly ILogger<Relay> _logger;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;

    // Mutated ONLY on PollEvents loop thread
    private readonly Dictionary<NetPeer, PeerState> _byPeer = new();
    private readonly Dictionary<string, PeerState> _byId = new(StringComparer.Ordinal);

    public Relay(ILogger<Relay> logger)
    {
        _logger = logger;

        _net = new(_listener)
        {
            IPv6Enabled = true,
            DisconnectTimeout = 15_000,
            NatPunchEnabled = false,
            UnconnectedMessagesEnabled = false,
            ChannelsCount = 2
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_net.Start(Port))
            throw new InvalidOperationException($"Failed to start the server on UDP port '{Port}'.");
        _logger.LogInformation("Server started on UDP port: {Port}", Port);
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping server...");
        _net.Stop();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _net.PollEvents();
                await Task.Delay(TickMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { _logger.LogError(ex, "An error occurred during server event processing"); }
        }
        
        _logger.LogInformation("Server stopped.");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var token = request.Data.GetString();
    
        // Token handshake: pid=...;schema=...
        if (!TryParseToken(token, out var peerId, out var schemaId))
        {
            _logger.LogError("Invalid connection request: {Request}", token);
            request.Reject(NetDataWriter.FromString("PROTOCOL_ERROR"));
            return;
        }

        if (_byId.TryGetValue(peerId, out var existing) &&
            existing.NetPeer.ConnectionState == ConnectionState.Connected)
        {
            request.Reject(NetDataWriter.FromString("PEER_ID_TAKEN"));
            return;
        }

        var peer = request.Accept();
        if (peer == null) return;
        
        var ps = new PeerState(peerId, schemaId, peer);
        _byPeer[peer] = ps;
        _byId[peerId] = ps;

        _logger.LogInformation("CONNECT pid={PeerId} schema={Schema} ep={Ep}", peerId, schemaId, peer.Address);
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_byPeer.TryGetValue(peer, out var ps))
            return;

        _byPeer.Remove(peer);

        if (_byId.TryGetValue(ps.PeerId, out var cur) && ReferenceEquals(cur.NetPeer, peer))
            _byId.Remove(ps.PeerId);

        // Remove links and notify other side
        foreach (var other in ps.Sessions)
        {
            if (!_byPeer.TryGetValue(other, out var otherState))
                continue;

            otherState.Sessions.Remove(peer);

            if (other.ConnectionState == ConnectionState.Connected)
                SendLinkClosed(other, ps.PeerId, "PEER_DISCONNECTED", $"Peer '{ps.PeerId}' disconnected");
        }

        ps.Sessions.Clear();

        _logger.LogInformation("DISCONNECT pid={PeerId} reason={Reason}", ps.PeerId, info.Reason);
    }

    // ---------------------------------
    // Receive
    // ---------------------------------

    private void OnNetworkReceive(NetPeer from, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (!_byPeer.TryGetValue(from, out var self))
                return;

            if (channel == ControlChannel)
            {
                HandleControl(from, self, reader);
                return;
            }

            // DATA: forward to all linked peers (broadcast inside "Sessions")
            ForwardToLinkedPeers(from, self, reader, channel, method);
        }
        finally
        {
            reader.Recycle();
        }
    }

    // ---------------------------------
    // Control channel (0)
    // ---------------------------------

    private void HandleControl(NetPeer from, PeerState self, NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1)
            return;

        var type = (ControlType)reader.GetByte();

        switch (type)
        {
            case ControlType.ConnectIntent:
                HandleConnectIntent(from, self, reader);
                break;

            case ControlType.DisconnectIntent:
                HandleDisconnectIntent(from, self, reader);
                break;

            default:
                SendError(from, "Unknown", "PROTOCOL_ERROR", $"Unknown control message {(byte)type}");
                break;
        }
    }

    private void HandleConnectIntent(NetPeer from, PeerState self, NetPacketReader reader)
    {
        var targetId = reader.GetString();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _logger.LogError("Invalid target peer {Peer}", targetId);
            SendError(from, targetId ?? "Unknown", "PROTOCOL_ERROR", "Invalid ConnectIntent payload");
            return;
        }

        if (!_byId.TryGetValue(targetId, out var target) ||
            target.NetPeer.ConnectionState != ConnectionState.Connected)
        {
            SendError(from, targetId, "TARGET_NOT_FOUND", $"Target '{targetId}' not connected");
            return;
        }

        if (!string.Equals(self.SchemaId, target.SchemaId, StringComparison.Ordinal))
        {
            SendError(from, targetId, "SCHEMA_MISMATCH", $"Schema mismatch: self={self.SchemaId}, target={target.SchemaId}");
            return;
        }
        
        if (self.Sessions.Contains(target.NetPeer) && target.Sessions.Contains(from)) return;

        // Create symmetric link
        self.Sessions.Add(target.NetPeer);
        target.Sessions.Add(from);

        SendLinkReady(from, targetId);
        SendLinkReady(target.NetPeer, self.PeerId);

        _logger.LogInformation("LINK UP {A} <-> {B}", self.PeerId, target.PeerId);
    }

    private void HandleDisconnectIntent(NetPeer from, PeerState self, NetPacketReader reader)
    {
        // Snapshot to avoid modifying while iterating
        var others = self.Sessions.ToArray();

        foreach (var otherPeer in others)
        {
            // Remove reverse link if possible
            if (_byPeer.TryGetValue(otherPeer, out var otherState))
            {
                otherState.Sessions.Remove(from);

                if (otherPeer.ConnectionState == ConnectionState.Connected)
                {
                    SendLinkClosed(
                        otherPeer,
                        self.PeerId,
                        code: "PEER_LEFT",
                        message: $"Peer '{self.PeerId}' left all relay links");
                }
            }
        }

        self.Sessions.Clear();

        // Optional: ack to self (useful for UI state)
        SendLinkClosed(
            from,
            otherPeerId: "*",
            code: "LEFT_ALL",
            message: "Left all relay links");
    }

    private void SendLinkReady(NetPeer peer, string otherPeerId)
    {
        var w = new NetDataWriter();
        w.Put((byte)ControlType.LinkReady);
        w.Put(otherPeerId);
        peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private void SendLinkClosed(NetPeer peer, string otherPeerId, string code, string message)
    {
        var w = new NetDataWriter();
        w.Put((byte)ControlType.LinkClosed);
        w.Put(otherPeerId);
        w.Put(code);
        w.Put(message);
        peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    private void SendError(NetPeer peer, string target, string code, string message)
    {
        var w = new NetDataWriter();
        w.Put((byte)ControlType.Error);
        w.Put(target);
        w.Put(code);
        w.Put(message);
        peer.Send(w, ControlChannel, DeliveryMethod.ReliableOrdered);
    }

    // ---------------------------------
    // Data forwarding: broadcast to linked peers
    // ---------------------------------

    private static void ForwardToLinkedPeers(NetPeer from, PeerState self, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var len = reader.AvailableBytes;
        if (len <= 0) return;

        var buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            reader.GetBytes(buf, len);

            var sessions = self.Sessions.ToArray();
            foreach (var peer in sessions)
            {
                if (Equals(peer, from)) continue;
                if (peer.ConnectionState != ConnectionState.Connected) continue;

                // Preserve delivery semantics (Unreliable stays Unreliable, ReliableOrdered stays ReliableOrdered)
                peer.Send(buf, 0, len, channel, method);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ---------------------------------
    // Token parsing
    // v=1;pid=...;schema=...
    // ---------------------------------

    private static bool TryParseToken(string token, out string peerId, out string schemaId)
    {
        peerId = schemaId = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string v = string.Empty;

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;

            var key = part.Substring(0, eq).Trim();
            var val = part.Substring(eq + 1).Trim();

            switch (key)
            {
                case "pid": peerId = val; break;
                case "schema": schemaId = val; break;
            }
        }

        if (string.IsNullOrWhiteSpace(peerId)) return false;

        return true;
    }

    // ---------------------------------
    // Types
    // ---------------------------------

    private enum ControlType : byte
    {
        // client -> server
        ConnectIntent = 1,
        DisconnectIntent = 2,

        // server -> client
        LinkReady = 10,
        LinkClosed = 11,

        Error = 255
    }

    private sealed class PeerState
    {
        public PeerState(string peerId, string schemaId, NetPeer netPeer)
        {
            PeerId = peerId;
            SchemaId = schemaId;
            NetPeer = netPeer;
        }

        public string PeerId { get; }
        public string SchemaId { get; }
        public NetPeer NetPeer { get; }

        // "Sessions" == currently linked peers (relay links)
        public HashSet<NetPeer> Sessions { get; } = new();
    }
}