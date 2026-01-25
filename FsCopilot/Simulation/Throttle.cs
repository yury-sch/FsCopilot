namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Throttle
{
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:1", "Position 16k", 1)] public int Throttle1;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:2", "Position 16k", 2)] public int Throttle2;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:3", "Position 16k", 3)] public int Throttle3;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:4", "Position 16k", 4)] public int Throttle4;
    public ulong SessionId;
    public uint TimeMs;

    public class Codec : IPacketCodec<Throttle>
    {
        public void Encode(Throttle packet, BinaryWriter bw)
        {
            bw.Write(packet.Throttle1);
            bw.Write(packet.Throttle2);
            bw.Write(packet.Throttle3);
            bw.Write(packet.Throttle4);
            bw.Write(packet.SessionId);
            bw.Write(packet.TimeMs);
        }

        public Throttle Decode(BinaryReader br) => new()
        {
            Throttle1 = br.ReadInt32(),
            Throttle2 = br.ReadInt32(),
            Throttle3 = br.ReadInt32(),
            Throttle4 = br.ReadInt32(),
            SessionId = br.ReadUInt64(),
            TimeMs = br.ReadUInt32()
        };
    }
}
