using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace P2PDiscovery;

public sealed class Stun : BackgroundService
{
    private const int Port = 3480;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);
    
    private readonly ILogger<Stun> _logger;
    private readonly EventBasedNetListener _listener = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly NetManager _net;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();

    public Stun(ILogger<Stun> logger)
    {
        _logger = logger;

        _net = new(_listener)
        {
            NatPunchEnabled = true,
            UnconnectedMessagesEnabled = true,
            IPv6Enabled = true,
            DisconnectTimeout = 15000
        };
        _net.NatPunchModule.Init(_natListener);

        _listener.ConnectionRequestEvent += request => request.Reject();
        _listener.NetworkReceiveUnconnectedEvent += OnUnconnectedMessage;
        _natListener.NatIntroductionRequest += OnNatIntroductionRequest;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        if (!_net.Start(Port))
            throw new InvalidOperationException($"Failed to start the server on UDP port '{Port}'.");
        _logger.LogInformation("Server started on UDP port: {Port}", Port);
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Stopping server...");
        _net.Stop();
        return base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastCleanup = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (now - lastCleanup >= CleanupInterval)
            {
                lastCleanup = now;
                foreach (var kv in _peers)
                {
                    if (now - kv.Value.LastSeen <= PeerTtl) continue;
                    if (_peers.TryRemove(kv.Key, out _)) _logger.LogInformation("STALE {PeerId} => REMOVED", kv.Key);
                }
            }
            
            try
            {
                _net.PollEvents();
                _net.NatPunchModule.PollEvents();
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex) { _logger.LogError(ex, "LOOP error"); }
        }
        
        _logger.LogInformation("Server stopped.");
    }

    private void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string selfId)
    {
        if (!IsValidPeerId(selfId))
        {
            _logger.LogWarning("INTRODUCE {PeerId} => INVALID ({Remote})", selfId, remote);
            return;
        }

        var now = DateTime.UtcNow;
        var peer = new PeerInfo(local, remote, now);
        var info = _peers.AddOrUpdate(selfId, peer, (_, _) => peer);

        _logger.LogInformation("INTRODUCE {PeerId} => {External}, {Internal}", selfId, info.External, info.Internal);
    }

    private void OnUnconnectedMessage(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (messageType != UnconnectedMessageType.BasicMessage)
        {
            reader.Recycle();
            return;
        }

        var msg = reader.GetString();
        reader.Recycle();

        try
        {
            if (msg.StartsWith("CALL|", StringComparison.Ordinal)) HandleCall(remote, msg);
            else _logger.LogWarning("MSG ? ({Remote}) '{Text}'", remote, msg);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "MSG ERR ({Remote}) '{Text}'", remote, msg);
        }
    }

    private void HandleCall(IPEndPoint remote, string msg)
    {
        // text = "CALL|SELFID|TARGETID"
        var parts = msg.Split('|');
        if (parts.Length != 3)
        {
            _logger.LogWarning("CALL ? ({Remote}) '{Text}'", remote, msg);
            return;
        }

        var selfId = parts[1].Trim();
        var targetId = parts[2].Trim();

        if (!IsValidPeerId(selfId) || !IsValidPeerId(targetId))
        {
            _logger.LogWarning("CALL {SelfId} -> {TargetId} => INVALID ({Remote})", selfId, targetId, remote);
            _net.SendUnconnectedMessage(NetDataWriter.FromString($"REJECT|{selfId}|{targetId}|NOT_FOUND"), remote);
            return;
        }

        if (selfId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CALL {SelfId} -> {TargetId} => SELF ({Remote})", selfId, targetId, remote);
            _net.SendUnconnectedMessage(NetDataWriter.FromString($"REJECT|{selfId}|{targetId}|NOT_FOUND"), remote);
            return;
        }

        if (!_peers.TryGetValue(selfId, out var self))
        {
            _logger.LogWarning("CALL {SelfId} (NOT_FOUND) -> {TargetId} (SKIPPED) => Ignored", selfId, targetId);
            _net.SendUnconnectedMessage(NetDataWriter.FromString($"REJECT|{selfId}|{targetId}|NOT_FOUND"), remote);
            return;
        }

        if (!_peers.TryGetValue(targetId, out var target))
        {
            _logger.LogInformation("CALL {SelfId} ({SelfExt}) -> {TargetId} (NOT_FOUND) => Rejected", 
                selfId, self.External, targetId);
            _net.SendUnconnectedMessage(NetDataWriter.FromString($"REJECT|{selfId}|{targetId}|NOT_FOUND"), remote);
            return;
        }

        _logger.LogInformation("CALL {SelfId} ({SelfExt}) -> {TargetId} ({TargetExt}) => Introduce",
            selfId, self.External, targetId, target.External);

        _net.NatPunchModule.NatIntroduce(
            self.Internal, self.External,
            target.Internal, target.External,
            $"PEERS|{selfId}|{targetId}");
    }

    private static bool IsValidPeerId(string peerId) => !string.IsNullOrEmpty(peerId) && peerId.Length == 8;

    private sealed record PeerInfo(IPEndPoint Internal, IPEndPoint External, DateTime LastSeen);
}