namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Control
{
    [SimVar("AILERON POSITION", "Position 16k", 1)] public int AilPos;
    [SimVar("ELEVATOR POSITION", "Position 16k", 2)] public int ElevPos;
    [SimVar("RUDDER POSITION", "Position 16k", 3)] public int RudPos;
    public ulong SessionId;
    public uint TimeMs;

    public class Codec : IPacketCodec<Control>
    {
        public void Encode(Control packet, BinaryWriter bw)
        {
            bw.Write(packet.AilPos);
            bw.Write(packet.ElevPos);
            bw.Write(packet.RudPos);
            bw.Write(packet.SessionId);
            bw.Write(packet.TimeMs);
        }

        public Control Decode(BinaryReader br) => new()
        {
            AilPos = br.ReadInt32(),
            ElevPos = br.ReadInt32(),
            RudPos = br.ReadInt32(),
            SessionId = br.ReadUInt64(),
            TimeMs = br.ReadUInt32()
        };
    }
}