namespace FsCopilot.Simulation;

using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Connection;
using Network;
using Serilog;

public class Coordinator : IDisposable
{
    private readonly Peer2Peer _peer2Peer;
    private readonly MasterSwitch _masterSwitch;
    private readonly SimClient _sim;
    private readonly CompositeDisposable _d = new();
    private CompositeDisposable _cSubs = new();
    private readonly BehaviorSubject<bool?> _configured = new(null);
    public IObservable<bool?> Configured => _configured;

    public Coordinator(SimClient sim, Peer2Peer peer2Peer, MasterSwitch masterSwitch)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        _sim = sim;
        peer2Peer.RegisterPacket<Update, Update.Codec>();
        peer2Peer.RegisterPacket<Interact, InteractCodec>();
        
        _d.Add(sim.Aircraft.Subscribe(Load));

        AddLink<Physics, Physics.Codec>(master: true);
        AddLink<Control, Control.Codec>(master: true);
        AddLink<Throttle, Throttle.Codec>(master: true);
        AddLink<Fuel, Fuel.Codec>(master: true);
        AddLink<Payload, Payload.Codec>(master: false);
        AddLink<Control.Flaps, Control.Flaps.Codec>(master: false);

        _d.Add(_sim.Interactions
            .Subscribe(interact => _peer2Peer.SendAll(interact)));

        _d.Add(_peer2Peer.Stream<Interact>()
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

    // private void AddLink<TPacket, TCodec, TInterpolator>(bool master) 
    //     where TPacket : struct 
    //     where TCodec : IPacketCodec<TPacket>, new()
    //     where TInterpolator : IInterpolator<TPacket>, new()
    // {
    //     _peer2Peer.RegisterPacket<TPacket, TCodec>();
    //     _simConnect.AddDataDefinition<TPacket>();
    //     var interpolation = new InterpolationQueue<TPacket, TInterpolator>();
    //
    //     _d.Add(_simConnect.Stream<TPacket>()
    //         .Where(_ => !master || _masterSwitch.IsMaster)
    //         .Subscribe(update => _peer2Peer.SendAll(update)));
    //     
    //     _d.Add(_peer2Peer.Subscribe<TPacket>(update =>
    //     {
    //         if (master && _masterSwitch.IsMaster) return;
    //         interpolation.Push(update);
    //     }));
    //     
    //     _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(5)).Subscribe(_ =>
    //     {
    //         if (master && _masterSwitch.IsMaster) return;
    //         if (interpolation.TryGet(out var value)) _simConnect.SetDataOnSimObject(value);
    //     }));
    // }

    private void AddLink<TPacket, TCodec>(bool master)
        where TPacket : struct where TCodec : IPacketCodec<TPacket>, new()
    {
        _peer2Peer.RegisterPacket<TPacket, TCodec>();

        _d.Add(_sim.Stream<TPacket>()
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Subscribe(update => _peer2Peer.SendAll(update)));

        _d.Add(_peer2Peer.Stream<TPacket>()
            .Where(_ => !master || !_masterSwitch.IsMaster)
            .Subscribe(update => _sim.Set(update)));
    }

    private void AddLink(Definition def)
    {
        var master = !def.Shared;
        object? currentValue = null;
        var getVar = def.Get;
        Skip.Next(getVar);
        
        _cSubs.Add(_sim.Stream(getVar, def.Units)
            .Do(value => currentValue = value)
            .Delay(getVar[0] == 'H' ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero)
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Where(_ => !Skip.Should(getVar))
            .Subscribe(value =>
            {
                if (def.Skip != null) Skip.Next(def.Skip);
                _peer2Peer.SendAll(new Update(getVar, value));
                Log.Debug("[PACKET] SENT {Name} {Value}", getVar, value);
            }));

        _cSubs.Add(_peer2Peer.Stream<Update>()
            .Where(update => update.Name == getVar)
            .Do(update => Log.Debug("[PACKET] RECV {Name} {Value}", getVar, update.Value))
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
            
            
        }

        public Interact Decode(BinaryReader br)
        {
            throw new NotImplementedException();
        }
    }
}