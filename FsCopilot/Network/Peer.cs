namespace FsCopilot.Network;

using System.Net;

public record struct Peer(string PeerId, string Name, IPAddress? Address, int Rtt, double Loss, Peer.State Status)
{
    public enum State : byte
    {
        Pending,
        Success,
        Failed,
        Rejected
    }
}