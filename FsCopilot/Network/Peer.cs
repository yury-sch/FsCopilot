namespace FsCopilot.Network;

public record struct Peer(
    string PeerId, 
    string Name, 
    int Ping,
    Peer.TransportKind Transport)
{
    public enum TransportKind { Direct, Relay }
}