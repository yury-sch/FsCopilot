namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Fuel
{
    [SimVar("FUEL TANK LEFT MAIN LEVEL", "Percent Over 100", 1)] public double LeftMain;
    [SimVar("FUEL TANK RIGHT MAIN LEVEL", "Percent Over 100", 2)] public double RightMain;
    [SimVar("FUEL TANK LEFT AUX LEVEL", "Percent Over 100", 3)] public double LeftAux;
    [SimVar("FUEL TANK RIGHT AUX LEVEL", "Percent Over 100", 4)] public double RightAux;

    public class Codec : IPacketCodec<Fuel>
    {
        public void Encode(Fuel packet, BinaryWriter bw)
        {
            bw.Write(packet.LeftMain);
            bw.Write(packet.RightMain);
            bw.Write(packet.LeftAux);
            bw.Write(packet.RightAux);
        }

        public Fuel Decode(BinaryReader br) => new()
        {
            LeftMain = br.ReadDouble(),
            RightMain = br.ReadDouble(),
            LeftAux = br.ReadDouble(),
            RightAux = br.ReadDouble()
        };
    }
}