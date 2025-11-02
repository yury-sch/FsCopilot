namespace FsCopilot.Simulation;

using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
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
    // private readonly BehaviorSubject<string> _aircraft = new(string.Empty);
    private readonly BehaviorSubject<bool?> _configured = new(null);
    // public IObservable<string> Aircraft => _aircraft;
    public IObservable<bool?> Configured => _configured;
    private readonly ConcurrentDictionary<string, DateTime> _throttle = new ();

    public Coordinator(SimClient sim, Peer2Peer peer2Peer, MasterSwitch masterSwitch)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        _sim = sim;
        peer2Peer.RegisterPacket<Update, Update.Codec>();
        peer2Peer.RegisterPacket<HUpdate, HUpdate.Codec>();
        
        _d.Add(sim.Aircraft
            .Subscribe(path =>
            {
                var match = Regex.Match(path, @"SimObjects\\Airplanes\\([^\\]+)");
                if (match.Success) path = match.Groups[1].Value;
                Load(path);
            }));

        // _d.Add(simConnect.Stream<Aircraft>()
        //     .Subscribe(Load));

        AddLink<Physics, Physics.Codec>(master: true);
        AddLink<Control, Control.Codec>(master: true);
        AddLink<Throttle, Throttle.Codec>(master: true);
        AddLink<Fuel, Fuel.Codec>(master: true);
        AddLink<Payload, Payload.Codec>(master: false);
        AddLink<Control.Flaps, Control.Flaps.Codec>(master: false);
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

        Definitions definitions;
        try
        {
            definitions = Definitions.Load(name);
            _configured.OnNext(true);
        }
        catch (Exception e)
        {
            Log.Error(e, "Unable to load aircraft configuration");
            _configured.OnNext(false);
            return;
        }

        foreach (var def in definitions) AddLink(def);
        
        // var ignoreEvents = definitions.Ignore.Where(i => i[0] == 'H').ToHashSet();
        // _cSubs.Add(_sim.HEvents
        //     .Delay(TimeSpan.FromMilliseconds(100))
        //     .Subscribe(evt =>
        //     {
        //         evt = $"H:{evt}";
        //         if (ignoreEvents.Contains(evt) || Throttled(evt)) return;
        //         _peer2Peer.SendAll(new HUpdate(evt));
        //         Log.Debug("[PACKET] Sent {Name}", evt);
        //     }));
        //
        // _cSubs.Add(_peer2Peer.Subscribe<HUpdate>(update =>
        // {
        //     Log.Debug("[PACKET] RECEIVE {Name}", update.Evt);
        //     _sim.Set(update.Evt, string.Empty);
        // }));
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
        var name = def.GetVar(out var units);
        
        _cSubs.Add(_sim.Stream(name, units)
            .Do(value => currentValue = value)
            .Delay(name[0] == 'H' ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero)
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Subscribe(value =>
            {
                if (Throttled(name)) return;
                if (def.Skip != null) Throttle(def.Skip);
                _peer2Peer.SendAll(new Update(name, value));
                Log.Debug("[PACKET] SENT {Name} {Value}", name, value);
            }));

        _cSubs.Add(_peer2Peer.Stream<Update>()
            .Where(update => update.Name == name)
            .Do(update => Log.Debug("[PACKET] RECEIVE {Name} {Value}", name, update.Value))
            .Where(_ => !master || !_masterSwitch.IsMaster)
            .Where(update => name[0] == 'H' || !update.Value.Equals(currentValue))
            .Subscribe(update =>
            {
                if (def.TryGetEvent(update.Value, currentValue ?? update.Value, out var eventName, 
                        out var val0, out var val1, out var val2, out var val3, out var val4))
                {
                    if (eventName[0] != 'H') Throttle(name);
                    _sim.Set(eventName, val0, val1, val2, val3, val4);
                }
                else
                {
                    if (name[0] != 'H') Throttle(name);
                    _sim.Set(name, update.Value);
                }
            }));
    }

    private void Throttle(string key) => _throttle.AddOrUpdate(key,
        _ => DateTime.UtcNow + TimeSpan.FromMilliseconds(200),
        (_, _) => DateTime.UtcNow + TimeSpan.FromMilliseconds(200));

    private bool Throttled(string name) => _throttle.TryGetValue(name, out var throttle) && DateTime.UtcNow <= throttle;

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
                    case TypeCode.String:
                        bw.Write((string)v);
                        break;
                    case TypeCode.Int32:
                        bw.Write((int)v);
                        break;
                    case TypeCode.Int64:
                        bw.Write((long)v);
                        break;
                    case TypeCode.Double:
                        bw.Write((double)v);
                        break;
                    case TypeCode.Single:
                        bw.Write((float)v);
                        break;
                    case TypeCode.Boolean:
                        bw.Write((bool)v);
                        break;
                    case TypeCode.Int16:
                        bw.Write((short)v);
                        break;
                    case TypeCode.UInt16:
                        bw.Write((ushort)v);
                        break;
                    case TypeCode.UInt32:
                        bw.Write((uint)v);
                        break;
                    case TypeCode.UInt64:
                        bw.Write((ulong)v);
                        break;
                    case TypeCode.Decimal:
                        bw.Write((decimal)v);
                        break;
                    case TypeCode.SByte:
                        bw.Write((sbyte)v);
                        break;
                    case TypeCode.Byte:
                        bw.Write((byte)v);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Data.Value type '{v.GetType().FullName}' is not supported by codec");
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
    
    private record HUpdate(string Evt)
    {
        public class Codec : IPacketCodec<HUpdate>
        {
            public void Encode(HUpdate packet, BinaryWriter bw)
            {
                bw.Write(packet.Evt);
            }
    
            public HUpdate Decode(BinaryReader br)
            {
                var evt = br.ReadString();
                return new(evt);
            }
        }
    }
}