namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Physics
{
    /// <summary>
    /// Latitude of aircraft, North is positive, South negative.
    /// </summary>
    [SimVar("PLANE LATITUDE", "Radians", 1)] public double Lat;
    /// <summary>
    /// Longitude of aircraft, East is positive, West negative.
    /// </summary>
    [SimVar("PLANE LONGITUDE", "Radians", 2)] public double Lon;
    /// <summary>
    /// Altitude of aircraft.
    /// </summary>
    [SimVar("PLANE ALTITUDE", "Feet", 3)] public double AltFeet;
    /// <summary>
    /// Pitch angle, although the name mentions degrees the units used are radians.
    /// </summary>
    [SimVar("PLANE PITCH DEGREES", "Radians", 4)] public double Pitch;
    /// <summary>
    /// Bank angle, although the name mentions degrees the units used are radians.
    /// </summary>
    [SimVar("PLANE BANK DEGREES", "Radians", 5)] public double Bank;
    /// <summary>
    /// Heading indicator taken from the aircraft gyro.
    /// </summary>
    [SimVar("PLANE HEADING DEGREES GYRO", "Degrees", 6)] public int HdgDegGyro;
    /// <summary>
    /// Heading relative to true north - although the name mentions degrees the units used are radians.
    /// </summary>
    [SimVar("PLANE HEADING DEGREES TRUE", "Radians", 7)] public double HdgDegTrue;
    /// <summary>
    /// The current indicated vertical speed for the aircraft.
    /// </summary>
    [SimVar("VERTICAL SPEED", "Feet per second", 8)] public double VerticalSpeed;
    /// <summary>
    /// 
    /// </summary>
    [SimVar("G FORCE", "Gforce", 9)] public double GForce;
    /// <summary>
    /// True vertical speed, relative to aircraft axis
    /// </summary>
    [SimVar("VELOCITY BODY Y", "Feet per second", 10)] public double VBodyY;
    /// <summary>
    /// True longitudinal speed, relative to aircraft axis
    /// </summary>
    [SimVar("VELOCITY BODY Z", "Feet per second", 11)] public double VBodyZ;
    /// <summary>
    /// Speed relative to earth, in East/West direction
    /// </summary>
    [SimVar("VELOCITY WORLD X", "Feet per second", 12)] public double Vx;
    /// <summary>
    /// Speed relative to earth, in North/South direction
    /// </summary>
    [SimVar("VELOCITY WORLD Z", "Feet per second", 13)] public double Vz;
    // [SimVar("VELOCITY WORLD Y", "Feet per second", 8)]
    // public double Vy;

    public class Codec : IPacketCodec<Physics>
    {
        public void Encode(Physics packet, BinaryWriter bw)
        {
            bw.Write(packet.Lat);
            bw.Write(packet.Lon);
            bw.Write(packet.AltFeet);
            bw.Write(packet.Pitch);
            bw.Write(packet.Bank);
            bw.Write(packet.HdgDegGyro);
            bw.Write(packet.HdgDegTrue);
            bw.Write(packet.VerticalSpeed);
            bw.Write(packet.GForce);
            bw.Write(packet.VBodyY);
            bw.Write(packet.VBodyZ);
            bw.Write(packet.Vx);
            bw.Write(packet.Vz);
        }

        public Physics Decode(BinaryReader br) => new()
        {
            Lat = br.ReadDouble(),
            Lon = br.ReadDouble(),
            AltFeet = br.ReadDouble(),
            Pitch = br.ReadDouble(),
            Bank = br.ReadDouble(),
            HdgDegGyro = br.ReadInt32(),
            HdgDegTrue = br.ReadDouble(),
            VerticalSpeed = br.ReadDouble(),
            GForce = br.ReadDouble(),
            VBodyY = br.ReadDouble(),
            VBodyZ = br.ReadDouble(),
            Vx = br.ReadDouble(),
            Vz = br.ReadDouble()
        };
    }

    // public class Interpolator : IInterpolator<Physics>
    // {
    //     public Physics Lerp(in Physics a, in Physics b, double u)
    //     {
    //         var r = a;
    //         r.Lat = LerpAngleDeg(a.Lat, b.Lat, u);
    //         r.Lon = LerpAngleDeg(a.Lon, b.Lon, u);
    //         r.AltFeet = LerpLin(a.AltFeet, b.AltFeet, u);
    //         r.Pitch = LerpAngleDeg(a.Pitch, b.Pitch, u);
    //         r.Bank  = LerpAngleDeg(a.Bank,  b.Bank,  u);
    //         r.HdgDegTrue = LerpAngleDeg(a.HdgDegTrue, b.HdgDegTrue, u);
    //         
    //         r.Vx = LerpLin(a.Vx, b.Vx, u);
    //         r.Vz = LerpLin(a.Vz, b.Vz, u);
    //         r.VerticalSpeed = LerpLin(a.VerticalSpeed, b.VerticalSpeed, u);
    //         r.GForce = LerpLin(a.GForce, b.GForce, u);
    //         r.VBodyY = LerpLin(a.VBodyY, b.VBodyY, u);
    //         r.VBodyZ = LerpLin(a.VBodyZ, b.VBodyZ, u);
    //         return r;
    //     }
    //
    //     private static double LerpLin(double x, double y, double u) => x + (y - x) * u;
    //
    //     private static double LerpAngleDeg(double a, double b, double u)
    //     {
    //         var d = (b - a + 540) % 360 - 180;
    //         return a + d * u;
    //     }
    // }
}