namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Engine
{
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:0", "Percent", 1)]
    public double Throttle0;

    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent", 2)]
    public double Throttle1;

    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:2", "Percent", 3)]
    public double Throttle2;

    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:3", "Percent", 4)]
    public double Throttle3;


    public class Codec : IPacketCodec<Engine>
    {
        public void Encode(Engine packet, BinaryWriter bw)
        {
            bw.Write(packet.Throttle0);
            bw.Write(packet.Throttle1);
            bw.Write(packet.Throttle2);
            bw.Write(packet.Throttle3);
        }

        public Engine Decode(BinaryReader br)
        {
            var aircraft = new Engine
            {
                Throttle0 = br.ReadDouble(),
                Throttle1 = br.ReadDouble(),
                Throttle2 = br.ReadDouble(),
                Throttle3 = br.ReadDouble()
            };
            return aircraft;
        }
    }
}