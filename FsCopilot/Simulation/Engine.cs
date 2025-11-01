namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Throttle
{
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent", 1)] public double Throttle1;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:2", "Percent", 2)] public double Throttle2;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:3", "Percent", 3)] public double Throttle3;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:4", "Percent", 4)] public double Throttle4;

    public class Codec : IPacketCodec<Throttle>
    {
        public void Encode(Throttle packet, BinaryWriter bw)
        {
            bw.Write(packet.Throttle1);
            bw.Write(packet.Throttle2);
            bw.Write(packet.Throttle3);
            bw.Write(packet.Throttle4);
        }

        public Throttle Decode(BinaryReader br) => new()
        {
            Throttle1 = br.ReadDouble(),
            Throttle2 = br.ReadDouble(),
            Throttle3 = br.ReadDouble(),
            Throttle4 = br.ReadDouble()
        };
    }
}
