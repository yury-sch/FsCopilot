namespace FsCopilot.Network;

public interface IPeer2Peer
{
    // IObservable<IPEndPoint?> DiscoveredEndpoint { get; }
    
    IObservable<ICollection<Peer>> Peers { get; }
    
    Task<bool> Connect(string target, CancellationToken ct);

    void Disconnect();

    void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull;

    void RegisterPacket<TPacket, TCodec>() where TCodec : IPacketCodec<TPacket>, new();

    IObservable<TPacket> Stream<TPacket>();
}