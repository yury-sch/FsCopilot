namespace FsCopilot.Network;

public interface IPacketCodec<TPacket>
{
    void Encode(TPacket packet, BinaryWriter bw);
    TPacket Decode(BinaryReader br);
}