namespace P2PDiscovery;

using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;

public sealed class LiteHost : BackgroundService
{
    private const int Port = 3480;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);
    
    private readonly ILogger<LiteHost> _logger;
    private readonly EventBasedNetListener _listener = new();
    private readonly EventBasedNatPunchListener _natListener = new();
    private readonly NetManager _net;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();

    public LiteHost(ILogger<LiteHost> logger)
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

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_net.Start(Port))
            throw new InvalidOperationException($"Failed to start NAT server on UDP port '{Port}'.");

        _logger.LogInformation("NAT server started on UDP port {Port}", Port);
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping NAT server...");
        _net.Stop();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanup = DateTime.UtcNow;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _net.PollEvents();
                _net.NatPunchModule.PollEvents();

                var now = DateTime.UtcNow;
                if (now - lastCleanup >= CleanupInterval)
                {
                    CleanupStalePeers(now);
                    lastCleanup = now;
                }

                try
                {
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // normal on shutdown
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NAT loop crashed");
        }
        finally
        {
            _logger.LogInformation("NAT loop stopped.");
        }
    }

    private void OnNatIntroductionRequest(
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        string selfId)
    {
        if (!IsValidPeerId(selfId))
        {
            _logger.LogWarning("Invalid selfId '{SelfId}' in HELLO from {Remote}", selfId, remoteEndPoint);
            return;
        }

        var now = DateTime.UtcNow;
        var peer = new PeerInfo(localEndPoint, remoteEndPoint, now);
        var info = _peers.AddOrUpdate(selfId, peer, (_, _) => peer);

        _logger.LogInformation("HELLO {PeerId}: internal={Internal}, external={External}",
            selfId, info.Internal, info.External);
    }

    private void CleanupStalePeers(DateTime now)
    {
        foreach (var kv in _peers)
        {
            if (now - kv.Value.LastSeen <= PeerTtl) continue;
            if (_peers.TryRemove(kv.Key, out _)) _logger.LogInformation("Removed stale peer {PeerId}", kv.Key);
        }
    }

    private void OnUnconnectedMessage(
        IPEndPoint remoteEndPoint,
        NetPacketReader reader,
        UnconnectedMessageType messageType)
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
                HandleCall(remoteEndPoint, text);
            else
                _logger.LogWarning("Unknown unconnected message '{Text}' from {Remote}", text, remoteEndPoint);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error while processing unconnected message '{Text}' from {Remote}",
                text, remoteEndPoint);
        }
    }

    private void HandleCall(IPEndPoint remote, string text)
    {
        // text = "CALL|SELFID|TARGETID"
        var parts = text.Split('|');
        if (parts.Length != 3)
        {
            _logger.LogWarning("Invalid CALL msg '{Text}' from {Remote}", text, remote);
            return;
        }

        var selfId = parts[1].Trim();
        var targetId = parts[2].Trim();

        if (!IsValidPeerId(selfId) || !IsValidPeerId(targetId))
        {
            _logger.LogWarning("Invalid ids in CALL '{Text}' from {Remote}", text, remote);
            return;
        }

        if (!_peers.TryGetValue(selfId, out var selfInfo))
        {
            _logger.LogWarning("CALL from {SelfId}, but this peer is not registered yet", selfId);
            return;
        }

        if (!_peers.TryGetValue(targetId, out var targetInfo))
        {
            _logger.LogInformation("CALL {SelfId} -> {TargetId}: target not found", selfId, targetId);
            return;
        }

        _logger.LogInformation(
            "CALL {SelfId} ({SelfExt}) -> {TargetId} ({TargetExt}) => NatIntroduce",
            selfId, selfInfo.External, targetId, targetInfo.External);

        // Perform NAT introduction: A <-> B
        // The token can be used for debugging, but clients don't really need it.
        var token = $"PEERS|{selfId}|{targetId}";

        _net.NatPunchModule.NatIntroduce(
            selfInfo.Internal, selfInfo.External,
            targetInfo.Internal, targetInfo.External,
            token);
    }

    private static bool IsValidPeerId(string peerId) => !string.IsNullOrEmpty(peerId);

    private sealed record PeerInfo(
        IPEndPoint Internal,
        IPEndPoint External,
        DateTime LastSeen);
}