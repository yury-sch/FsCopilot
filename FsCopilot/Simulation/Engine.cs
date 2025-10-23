namespace FsCopilot.Simulation;

using System.Runtime.InteropServices;
using Connection;
using Network;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Engine
{
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent", 1)] public double Throttle1;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:2", "Percent", 2)] public double Throttle2;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:3", "Percent", 3)] public double Throttle3;
    [SimVar("GENERAL ENG THROTTLE LEVER POSITION:4", "Percent", 4)] public double Throttle4;
    
    [SimVar("GENERAL ENG RPM:1", "RPM", 5)] public double EngRpm1;
    [SimVar("GENERAL ENG RPM:2", "RPM", 6)] public double EngRpm2;
    [SimVar("GENERAL ENG RPM:3", "RPM", 7)] public double EngRpm3;
    [SimVar("GENERAL ENG RPM:4", "RPM", 8)] public double EngRpm4;
    
    // [SimVar("ENG ON FIRE:1", "Bool", 9)] public bool OnFire1;
    // [SimVar("ENG ON FIRE:2", "Bool", 10)] public bool OnFire2;
    // [SimVar("ENG ON FIRE:3", "Bool", 11)] public bool OnFire3;
    // [SimVar("ENG ON FIRE:4", "Bool", 12)] public bool OnFire4;
    
    [SimVar("GENERAL ENG FUEL PRESSURE:1", "Pounds per square inch", 13)] public double FuelPressure1;
    [SimVar("GENERAL ENG FUEL PRESSURE:2", "Pounds per square inch", 14)] public double FuelPressure2;
    [SimVar("GENERAL ENG FUEL PRESSURE:3", "Pounds per square inch", 15)] public double FuelPressure3;
    [SimVar("GENERAL ENG FUEL PRESSURE:4", "Pounds per square inch", 16)] public double FuelPressure4;
    
    [SimVar("GENERAL ENG OIL PRESSURE:1", "Psf", 17)] public double OilPressure1;
    [SimVar("GENERAL ENG OIL PRESSURE:2", "Psf", 18)] public double OilPressure2;
    [SimVar("GENERAL ENG OIL PRESSURE:3", "Psf", 19)] public double OilPressure3;
    [SimVar("GENERAL ENG OIL PRESSURE:4", "Psf", 20)] public double OilPressure4;
    
    [SimVar("GENERAL ENG OIL TEMPERATURE:1", "Rankine", 21)] public double OilTemperature1;
    [SimVar("GENERAL ENG OIL TEMPERATURE:2", "Rankine", 22)] public double OilTemperature2;
    [SimVar("GENERAL ENG OIL TEMPERATURE:3", "Rankine", 23)] public double OilTemperature3;
    [SimVar("GENERAL ENG OIL TEMPERATURE:4", "Rankine", 24)] public double OilTemperature4;
    
    [SimVar("GENERAL ENG PROPELLER LEVER POSITION:1", "Percent", 25)] public double Propeller1;
    [SimVar("GENERAL ENG PROPELLER LEVER POSITION:2", "Percent", 26)] public double Propeller2;
    [SimVar("GENERAL ENG PROPELLER LEVER POSITION:3", "Percent", 27)] public double Propeller3;
    [SimVar("GENERAL ENG PROPELLER LEVER POSITION:4", "Percent", 28)] public double Propeller4;
    
    [SimVar("PROP RPM:1", "RPM", 29)] public double PropRpm1;
    [SimVar("PROP RPM:2", "RPM", 30)] public double PropRpm2;
    [SimVar("PROP RPM:3", "RPM", 31)] public double PropRpm3;
    [SimVar("PROP RPM:4", "RPM", 32)] public double PropRpm4;

    public class Codec : IPacketCodec<Engine>
    {
        public void Encode(Engine packet, BinaryWriter bw)
        {
            bw.Write(packet.Throttle1);
            bw.Write(packet.Throttle2);
            bw.Write(packet.Throttle3);
            bw.Write(packet.Throttle4);
            bw.Write(packet.EngRpm1);
            bw.Write(packet.EngRpm2);
            bw.Write(packet.EngRpm3);
            bw.Write(packet.EngRpm4);
            // bw.Write(packet.OnFire1);
            // bw.Write(packet.OnFire2);
            // bw.Write(packet.OnFire3);
            // bw.Write(packet.OnFire4);
            bw.Write(packet.FuelPressure1);
            bw.Write(packet.FuelPressure2);
            bw.Write(packet.FuelPressure3);
            bw.Write(packet.FuelPressure4);
            bw.Write(packet.OilPressure1);
            bw.Write(packet.OilPressure2);
            bw.Write(packet.OilPressure3);
            bw.Write(packet.OilPressure4);
            bw.Write(packet.OilTemperature1);
            bw.Write(packet.OilTemperature2);
            bw.Write(packet.OilTemperature3);
            bw.Write(packet.OilTemperature4);
            bw.Write(packet.Propeller1);
            bw.Write(packet.Propeller2);
            bw.Write(packet.Propeller3);
            bw.Write(packet.Propeller4);
            bw.Write(packet.PropRpm1);
            bw.Write(packet.PropRpm2);
            bw.Write(packet.PropRpm3);
            bw.Write(packet.PropRpm4);
        }

        public Engine Decode(BinaryReader br)
        {
            var aircraft = new Engine
            {
                Throttle1 = br.ReadDouble(),
                Throttle2 = br.ReadDouble(),
                Throttle3 = br.ReadDouble(),
                Throttle4 = br.ReadDouble(),
                EngRpm1 = br.ReadDouble(),
                EngRpm2 = br.ReadDouble(),
                EngRpm3 = br.ReadDouble(),
                EngRpm4 = br.ReadDouble(),
                // OnFire1 = br.ReadBoolean(),
                // OnFire2 = br.ReadBoolean(),
                // OnFire3 = br.ReadBoolean(),
                // OnFire4 = br.ReadBoolean(),
                FuelPressure1 = br.ReadDouble(),
                FuelPressure2 = br.ReadDouble(),
                FuelPressure3 = br.ReadDouble(),
                FuelPressure4 = br.ReadDouble(),
                OilPressure1 = br.ReadDouble(),
                OilPressure2 = br.ReadDouble(),
                OilPressure3 = br.ReadDouble(),
                OilPressure4 = br.ReadDouble(),
                OilTemperature1 = br.ReadDouble(),
                OilTemperature2 = br.ReadDouble(),
                OilTemperature3 = br.ReadDouble(),
                OilTemperature4 = br.ReadDouble(),
                Propeller1 = br.ReadDouble(),
                Propeller2 = br.ReadDouble(),
                Propeller3 = br.ReadDouble(),
                Propeller4 = br.ReadDouble(),
                PropRpm1 = br.ReadDouble(),
                PropRpm2 = br.ReadDouble(),
                PropRpm3 = br.ReadDouble(),
                PropRpm4 = br.ReadDouble(),
            };
            return aircraft;
        }
    }
}