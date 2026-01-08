namespace FsCopilot.Network;

public record PeerTags(PeerTags.Tag[] Peers)
{
    public sealed class Codec : IPacketCodec<PeerTags>
    {
        public void Encode(PeerTags packet, BinaryWriter bw)
        {
            bw.Write(packet.Peers.Length);
            foreach (var peer in packet.Peers)
            {
                bw.Write(peer.PeerId);
                bw.Write(peer.Name);
            }
        }
    
        public PeerTags Decode(BinaryReader br)
        {
            var count = br.ReadInt32();
            var peers = new Tag[count];
            for (var i = 0; i < count; i++)
            {
                var peerId = br.ReadString();
                var name = br.ReadString();
                peers[i] = new(peerId, name);
            }
            return new(peers);
        }
    }

    public record Tag(string PeerId, string Name);
}