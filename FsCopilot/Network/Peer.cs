namespace FsCopilot.Network;

public record struct Peer(
    string PeerId, 
    string Name, 
    int Ping,
    Peer.State Status,
    Peer.TransportKind Transport)
{
    public enum State : byte
    {
        Pending,
        Success,
        Failed,
        Rejected
    }
    
    public enum TransportKind { Direct, Relay }
}