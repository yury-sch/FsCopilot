namespace FsCopilot.Simulation;

using System.Reactive;
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
    private readonly SimConnectClient _simConnect;
    private readonly CompositeDisposable _d = new();
    private CompositeDisposable _cSubs = new();
    private readonly BehaviorSubject<string> _aircraft = new(string.Empty);
    private readonly BehaviorSubject<bool?> _configured = new(null);
    public IObservable<string> Aircraft => _aircraft;
    public IObservable<bool?> Configured => _configured;

    public Coordinator(SimConnectClient simConnect, Peer2Peer peer2Peer, MasterSwitch masterSwitch)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        _simConnect = simConnect;
        peer2Peer.RegisterPacket<Update, Update.Codec>();

        _d.Add(simConnect.Stream<Aircraft>()
            // .StartWith(Unit.Default)
            .SelectMany(_ => Observable.FromAsync(() => _simConnect.SystemState<string>("AircraftLoaded")))
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
        AddLink<Engine, Engine.Codec>(master: true);
        AddLink<Control.Flaps, Control.Flaps.Codec>(master: false);
    }

    public void Dispose()
    {
        _d.Dispose();
        _cSubs.Dispose();
    }

    private void Load(string name)
    {
        _aircraft.OnNext(name);
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

        _d.Add(_simConnect.Stream<TPacket>()
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Subscribe(update => _peer2Peer.SendAll(update)));

        _d.Add(_peer2Peer.Subscribe<TPacket>(update =>
        {
            if (master && _masterSwitch.IsMaster) return;
            _simConnect.SetDataOnSimObject(update);
        }));
    }

    private void AddLink(Definition def)
    {
        var master = !def.Shared;
        var throttle = DateTime.MinValue;
        _cSubs.Add(_simConnect.Stream(def.Name, def.Units).Subscribe(val =>
        {
            if (master && !_masterSwitch.IsMaster) return;
            if (DateTime.Now <= throttle) return;
            _peer2Peer.SendAll(new Update(def.Name, val));
            Log.Information("[PACKET] SENT {Name} {Value}", def.Name, val);
        }));

        _cSubs.Add(_peer2Peer.Subscribe<Update>(update =>
        {
            var datumName = update.Name;
            var value = update.Value;
            if (datumName != def.Name) return;
            if (master && _masterSwitch.IsMaster) return;
            Log.Information("[PACKET] RECEIVE {Name} {Value}", def.Name, value);
            if (!def.TryGetEvent(value, out var eventName, out var paramIx))
            {
                _simConnect.Set(datumName, value);
                return;
            }

            value = def.TransformValue(value);
            throttle = DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
            _simConnect.TransmitClientEvent(eventName, value, paramIx);
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
                TypeCode type = Type.GetTypeCode(packet.Value.GetType());
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
                            $"Data.Value type '{v.GetType().FullName}' не поддержан кодеком");
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
}