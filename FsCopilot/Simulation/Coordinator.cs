using System.Security.Cryptography;

namespace FsCopilot.Simulation;

using Connection;
using Network;

public class Coordinator : IDisposable
{
    private readonly INetwork _net;
    private readonly MasterSwitch _masterSwitch;
    private readonly SimClient _sim;
    private readonly CompositeDisposable _d = new();
    private CompositeDisposable _cSubs = new();
    private readonly BehaviorSubject<bool?> _configured = new(null);
    public IObservable<bool?> Configured => _configured;

    public Coordinator(SimClient sim, INetwork net, MasterSwitch masterSwitch)
    {
        _net = net;
        _masterSwitch = masterSwitch;
        _sim = sim;
        var sw = Stopwatch.StartNew();
        
        Span<byte> sessionBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BitConverter.ToUInt64(sessionBytes);
        
        net.RegisterPacket<Update, Update.Codec>();
        net.RegisterPacket<Interact, InteractCodec>();
        _net.RegisterPacket<Physics, Physics.Codec>();
        _net.RegisterPacket<Surfaces, Surfaces.Codec>();
        
        _d.Add(sim.Aircraft.Subscribe(Load));
        
        _d.Add(sim.Aircraft.Take(1).Subscribe(_ =>
        {
            AddLink<Physics, Physics.Codec>((ref Physics physics) =>
            {
                physics.SessionId = sessionId;
                physics.TimeMs = (uint)sw.ElapsedMilliseconds;
            });
            AddLink<Surfaces, Surfaces.Codec>((ref Surfaces surfaces) =>
            {
                surfaces.SessionId = sessionId;
                surfaces.TimeMs = (uint)sw.ElapsedMilliseconds;
            });
 
            AddLink("FUEL TANK CENTER LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK CENTER2 LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK CENTER3 LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK EXTERNAL1 LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK EXTERNAL2 LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK LEFT AUX LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK LEFT MAIN LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK LEFT TIP LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK RIGHT AUX LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK RIGHT MAIN LEVEL", "Percent Over 100", master: true, unreliable: true);
            AddLink("FUEL TANK RIGHT TIP LEVEL", "Percent Over 100", master: true, unreliable: true);

            AddLink("PAYLOAD STATION WEIGHT:1", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:2", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:3", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:4", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:5", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:6", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:7", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:8", "Pounds");
            AddLink("PAYLOAD STATION WEIGHT:9", "Pounds");
            
            AddLink("FLAPS HANDLE INDEX:0", "Number");
            AddLink("FLAPS HANDLE INDEX:1", "Number");
            AddLink("FLAPS HANDLE INDEX:2", "Number");
            AddLink("FLAPS HANDLE INDEX:3", "Number");
            AddLink("FLAPS HANDLE INDEX:4", "Number");
            AddLink("FLAPS HANDLE INDEX:5", "Number");
            AddLink("FLAPS HANDLE INDEX:6", "Number");
            AddLink("FLAPS HANDLE INDEX:7", "Number");
            AddLink("FLAPS HANDLE INDEX:8", "Number");
            AddLink("FLAPS HANDLE INDEX:9", "Number");
            AddLink("FLAPS HANDLE INDEX:10", "Number");
            AddLink("FLAPS HANDLE INDEX:11", "Number");
            AddLink("FLAPS HANDLE INDEX:12", "Number");
            AddLink("FLAPS HANDLE INDEX:13", "Number");
            AddLink("FLAPS HANDLE INDEX:14", "Number");
            AddLink("FLAPS HANDLE INDEX:15", "Number");
        }));

        _d.Add(_sim.Interactions
            .Subscribe(interact => _net.SendAll(interact)));
        _d.Add(_net.Stream<Interact>()
            .Subscribe(update => _sim.Set(update)));
    }

    public void Dispose()
    {
        _d.Dispose();
        _cSubs.Dispose();
    }

    private void Load(string name)
    {
        _configured.OnNext(null);
        if (!_cSubs.IsDisposed) _cSubs.Dispose();
        _cSubs = new();

        var definitions = Definitions.Load(name);
        if (definitions.Count == 0)
        {
            _configured.OnNext(false);
            return;
        }
        _configured.OnNext(true);

        foreach (var def in definitions) AddLink(def);
    }
    
    private void AddLink<TPacket, TCodec>(RefAction<TPacket> modify)
        where TPacket : unmanaged
        where TCodec : IPacketCodec<TPacket>, new()
    {
        _sim.Register<TPacket>();

        _d.Add(_sim.Stream<TPacket>()
            .Where(_ => _masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                modify(ref update);
                try { _net.SendAll(update, true); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error while sending packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from sim"); }));

        _d.Add(_net.Stream<TPacket>()
            .Where(_ => !_masterSwitch.IsMaster)
            .Subscribe(update =>
            {
                try { _sim.Set(update); }
                catch (Exception e) { Log.Error(e, "[Coordinator] Error processing packet {Packet}", typeof(TPacket).Name); }
            }, ex => { Log.Fatal(ex, "[Coordinator] Error while processing a message from client"); }));
    }

    private void AddLink(string name, string units, bool master = false, bool unreliable = false)
    {
        var key = $"FSC_{name}";
        _d.Add(_sim.Stream(name, units)
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Subscribe(value => _net.SendAll(new Update(key, value), unreliable)));

        _d.Add(_net.Stream<Update>()
            .Where(update => update.Name == key)
            .Subscribe(update => _sim.Set(name, update.Value)));
    }

    private void AddLink(Definition def)
    {
        var master = !def.Shared;
        object? currentValue = null;
        var getVar = def.Get;
        
        _cSubs.Add(_sim.Stream(getVar, def.Units)
            .Do(value => currentValue = value)
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Delay(getVar[0] == 'H' ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero)
            .Where(_ => !Skip.Should(getVar))
            .Subscribe(value =>
            {
                if (def.Skip != null) Skip.Next(def.Skip);
                _net.SendAll(new Update(getVar, value));
                Log.Verbose("[PACKET] SENT {Name} {Value}", getVar, value);
            }));

        _cSubs.Add(_net.Stream<Update>()
            .Where(update => update.Name == getVar)
            .Do(update => Log.Verbose("[PACKET] RECV {Name} {Value}", getVar, update.Value))
            .Where(_ => !master || !_masterSwitch.IsMaster)
            .Where(update => getVar[0] == 'H' || !update.Value.Equals(currentValue))
            .Subscribe(update =>
            {
                var setVar = def.Set(update.Value, currentValue ?? update.Value,
                    out var val0, out var val1, out var val2, out var val3, out var val4);
                Skip.Next(getVar);
                _sim.Set(setVar, val0, val1, val2, val3, val4);
            }));
    }

    private record Update(string Name, object Value)
    {
        public class Codec : IPacketCodec<Update>
        {
            public void Encode(Update packet, BinaryWriter bw)
            {
                bw.Write(packet.Name);
                var v = packet.Value;
                var type = Type.GetTypeCode(v.GetType());
                bw.Write((byte)type);
                switch (type)
                {
                    case TypeCode.String: bw.Write((string)v); break;
                    case TypeCode.Int32: bw.Write((int)v); break;
                    case TypeCode.Int64: bw.Write((long)v); break;
                    case TypeCode.Double: bw.Write((double)v); break;
                    case TypeCode.Single: bw.Write((float)v); break;
                    case TypeCode.Boolean: bw.Write((bool)v); break;
                    case TypeCode.Int16: bw.Write((short)v); break;
                    case TypeCode.UInt16: bw.Write((ushort)v); break;
                    case TypeCode.UInt32: bw.Write((uint)v); break;
                    case TypeCode.UInt64: bw.Write((ulong)v); break;
                    case TypeCode.Decimal: bw.Write((decimal)v); break;
                    case TypeCode.SByte: bw.Write((sbyte)v); break;
                    case TypeCode.Byte: bw.Write((byte)v); break;
                    default: throw new NotSupportedException( $"Data.Value type '{v.GetType().FullName}' is not supported by codec");
                }
            }

            public Update Decode(BinaryReader br)
            {
                var name = br.ReadString();
                var t = (TypeCode)br.ReadByte();
                object value = t switch
                {
                    TypeCode.String => br.ReadString(),
                    TypeCode.Int32 => br.ReadInt32(),
                    TypeCode.Int64 => br.ReadInt64(),
                    TypeCode.Double => br.ReadDouble(),
                    TypeCode.Single => br.ReadSingle(),
                    TypeCode.Boolean => br.ReadBoolean(),
                    TypeCode.Int16 => br.ReadInt16(),
                    TypeCode.UInt16 => br.ReadUInt16(),
                    TypeCode.UInt32 => br.ReadUInt32(),
                    TypeCode.UInt64 => br.ReadUInt64(),
                    TypeCode.Decimal => br.ReadDecimal(),
                    TypeCode.SByte => br.ReadSByte(),
                    TypeCode.Byte => br.ReadByte(),
                    TypeCode.DateTime => DateTime.FromBinary(br.ReadInt64()),
                    _ => throw new NotSupportedException($"Неизвестный TypeCode={t}")
                };

                return new(name, value);
            }
        }
    }
    
    private class InteractCodec : IPacketCodec<Interact>
    {
        public void Encode(Interact packet, BinaryWriter bw)
        {
            bw.Write(packet.Instrument);
            bw.Write(packet.Event);
            bw.Write(packet.Id);
            bw.Write(packet.Value != null);
            if (packet.Value != null) bw.Write(packet.Value);
        }

        public Interact Decode(BinaryReader br)
        {
            var instrument = br.ReadString();
            var @event = br.ReadString();
            var id = br.ReadString();
            var hasValue = br.ReadBoolean();
            var value = hasValue ? br.ReadString() : null; 
            return new(instrument, @event, id, value);
        }
    }
    
    delegate void RefAction<T>(ref T value);
}