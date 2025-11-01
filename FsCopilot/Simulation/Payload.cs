namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Payload
{
    [SimVar("PAYLOAD STATION WEIGHT:1", "Pounds", 1)] public double Weight1;
    [SimVar("PAYLOAD STATION WEIGHT:2", "Pounds", 2)] public double Weight2;
    [SimVar("PAYLOAD STATION WEIGHT:3", "Pounds", 3)] public double Weight3;
    [SimVar("PAYLOAD STATION WEIGHT:4", "Pounds", 4)] public double Weight4;
    [SimVar("PAYLOAD STATION WEIGHT:5", "Pounds", 5)] public double Weight5;
    [SimVar("PAYLOAD STATION WEIGHT:6", "Pounds", 6)] public double Weight6;
    [SimVar("PAYLOAD STATION WEIGHT:7", "Pounds", 7)] public double Weight7;
    [SimVar("PAYLOAD STATION WEIGHT:8", "Pounds", 8)] public double Weight8;
    [SimVar("PAYLOAD STATION WEIGHT:9", "Pounds", 9)] public double Weight9;

    public class Codec : IPacketCodec<Payload>
    {
        public void Encode(Payload packet, BinaryWriter bw)
        {
            bw.Write(packet.Weight1);
            bw.Write(packet.Weight2);
            bw.Write(packet.Weight3);
            bw.Write(packet.Weight4);
            bw.Write(packet.Weight5);
            bw.Write(packet.Weight6);
            bw.Write(packet.Weight7);
            bw.Write(packet.Weight8);
            bw.Write(packet.Weight9);
        }

        public Payload Decode(BinaryReader br) => new()
        {
            Weight1 = br.ReadDouble(),
            Weight2 = br.ReadDouble(),
            Weight3 = br.ReadDouble(),
            Weight4 = br.ReadDouble(),
            Weight5 = br.ReadDouble(),
            Weight6 = br.ReadDouble(),
            Weight7 = br.ReadDouble(),
            Weight8 = br.ReadDouble(),
            Weight9 = br.ReadDouble(),
        };
    }
}