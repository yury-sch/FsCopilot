namespace FsCopilot.Network;

public interface INetwork
{
    IObservable<ICollection<Peer>> Peers { get; }
    
    Task<ConnectionResult> Connect(string target, CancellationToken ct);

    void Disconnect();

    void SendAll<TPacket>(TPacket packet, bool unreliable = false) where TPacket : notnull;

    void RegisterPacket<TPacket, TCodec>() 
        where TPacket : notnull
        where TCodec : IPacketCodec<TPacket>, new();

    IObservable<TPacket> Stream<TPacket>();
}