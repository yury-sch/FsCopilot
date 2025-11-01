namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Control
{
    [SimVar("AILERON POSITION", "Position 16k", 1)] public double AilPos;
    [SimVar("ELEVATOR POSITION", "Position 16k", 2)] public double ElevPos;
    [SimVar("RUDDER POSITION", "Position 16k", 3)] public double RudPos;

    public class Codec : IPacketCodec<Control>
    {
        public void Encode(Control packet, BinaryWriter bw)
        {
            bw.Write(packet.AilPos);
            bw.Write(packet.ElevPos);
            bw.Write(packet.RudPos);
        }

        public Control Decode(BinaryReader br) => new()
        {
            AilPos = br.ReadDouble(),
            ElevPos = br.ReadDouble(),
            RudPos = br.ReadDouble()
        };
    }

    // public sealed class Interpolator : IInterpolator<Control>
    // {
    //     private const double Min16k = -16383.0;
    //     private const double Max16k =  16383.0;
    //
    //     public Control Lerp(in Control a, in Control b, double u)
    //     {
    //         if (u < 0) u = 0;
    //         else if (u > 1) u = 1;
    //
    //         Control r = default;
    //         r.AilPos = Clamp16K(LerpLin(a.AilPos, b.AilPos, u));
    //         r.ElevPos = Clamp16K(LerpLin(a.ElevPos, b.ElevPos, u));
    //         r.RudPos = Clamp16K(LerpLin(a.RudPos, b.RudPos, u));
    //         return r;
    //     }
    //
    //     private static double Clamp16K(double v) =>
    //         v < Min16k ? Min16k : v > Max16k ? Max16k : v;
    //
    //     private static double LerpLin(double x, double y, double u) => x + (y - x) * u;
    // }

    // [StructLayout(LayoutKind.Sequential, Pack = 1)]
    // public struct Yoke
    // {
    //     [SimVar("YOKE X POSITION", "Position 16k", 4)]
    //     public double X;
    //     
    //     [SimVar("YOKE Y POSITION", "Position 16k", 5)]
    //     public double Y;
    //
    //     public class Codec : IPacketCodec<Yoke>
    //     {
    //         public void Encode(Yoke packet, BinaryWriter bw)
    //         {
    //             bw.Write(packet.X);
    //             bw.Write(packet.Y);
    //         }
    //
    //         public Yoke Decode(BinaryReader br)
    //         {
    //             var aircraft = new Yoke
    //             {
    //                 X = br.ReadDouble(),
    //                 Y = br.ReadDouble()
    //             };
    //             return aircraft;
    //         }
    //     }
    //     
    // }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Flaps
    {
        [SimVar("FLAPS HANDLE INDEX:0", "Number", 1)] public int Flaps0;
        [SimVar("FLAPS HANDLE INDEX:1", "Number", 2)] public int Flaps1;
        [SimVar("FLAPS HANDLE INDEX:2", "Number", 3)] public int Flaps2;
        [SimVar("FLAPS HANDLE INDEX:3", "Number", 4)] public int Flaps3;
        [SimVar("FLAPS HANDLE INDEX:4", "Number", 5)] public int Flaps4;
        [SimVar("FLAPS HANDLE INDEX:5", "Number", 6)] public int Flaps5;
        [SimVar("FLAPS HANDLE INDEX:6", "Number", 7)] public int Flaps6;
        [SimVar("FLAPS HANDLE INDEX:7", "Number", 8)] public int Flaps7;
        [SimVar("FLAPS HANDLE INDEX:8", "Number", 9)] public int Flaps8;
        [SimVar("FLAPS HANDLE INDEX:9", "Number", 10)] public int Flaps9;
        [SimVar("FLAPS HANDLE INDEX:10", "Number", 11)] public int Flaps10;
        [SimVar("FLAPS HANDLE INDEX:11", "Number", 12)] public int Flaps11;
        [SimVar("FLAPS HANDLE INDEX:12", "Number", 13)] public int Flaps12;
        [SimVar("FLAPS HANDLE INDEX:13", "Number", 14)] public int Flaps13;
        [SimVar("FLAPS HANDLE INDEX:14", "Number", 15)] public int Flaps14;
        [SimVar("FLAPS HANDLE INDEX:15", "Number", 16)] public int Flaps15;

        public class Codec : IPacketCodec<Flaps>
        {
            public void Encode(Flaps packet, BinaryWriter bw)
            {
                bw.Write(packet.Flaps0);
                bw.Write(packet.Flaps1);
                bw.Write(packet.Flaps2);
                bw.Write(packet.Flaps3);
                bw.Write(packet.Flaps4);
                bw.Write(packet.Flaps5);
                bw.Write(packet.Flaps6);
                bw.Write(packet.Flaps7);
                bw.Write(packet.Flaps8);
                bw.Write(packet.Flaps9);
                bw.Write(packet.Flaps10);
                bw.Write(packet.Flaps11);
                bw.Write(packet.Flaps12);
                bw.Write(packet.Flaps13);
                bw.Write(packet.Flaps14);
                bw.Write(packet.Flaps15);
            }

            public Flaps Decode(BinaryReader br) => new()
            {
                Flaps0 = br.ReadInt32(),
                Flaps1 = br.ReadInt32(),
                Flaps2 = br.ReadInt32(),
                Flaps3 = br.ReadInt32(),
                Flaps4 = br.ReadInt32(),
                Flaps5 = br.ReadInt32(),
                Flaps6 = br.ReadInt32(),
                Flaps7 = br.ReadInt32(),
                Flaps8 = br.ReadInt32(),
                Flaps9 = br.ReadInt32(),
                Flaps10 = br.ReadInt32(),
                Flaps11 = br.ReadInt32(),
                Flaps12 = br.ReadInt32(),
                Flaps13 = br.ReadInt32(),
                Flaps14 = br.ReadInt32(),
                Flaps15 = br.ReadInt32(),
            };
        }
    }
}