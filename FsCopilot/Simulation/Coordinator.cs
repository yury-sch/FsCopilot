namespace FsCopilot.Simulation;

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
    private readonly BehaviorSubject<string> _aircraft = new(string.Empty);
    private readonly BehaviorSubject<bool?> _configured = new(null);
    public IObservable<string> Aircraft => _aircraft;
    public IObservable<bool?> Configured => _configured;

    public Coordinator(SimClient sim, Peer2Peer peer2Peer, MasterSwitch masterSwitch)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        _sim = sim;
        peer2Peer.RegisterPacket<Update, Update.Codec>();
        peer2Peer.RegisterPacket<ClientUpdate, ClientUpdate.Codec>();

        _d.Add(sim.Stream<Aircraft>()
            // .StartWith(Unit.Default)
            .SelectMany(_ => Observable.FromAsync(() => _sim.SystemState<string>("AircraftLoaded")))
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
        AddLink<Fuel, Fuel.Codec>(master: true);
        AddLink<Payload, Payload.Codec>(master: false);
        AddLink<Control.Flaps, Control.Flaps.Codec>(master: false);
        
        // _d.Add(Observable
        //     .FromEventPattern<EventHandler<MessageReceivedEventArgs>, MessageReceivedEventArgs>(
        //         h => socket.MessageReceived += h,
        //         h => socket.MessageReceived -= h)
        //     .Select(ep => Encoding.UTF8.GetString(ep.EventArgs.Data))
        //     .Select(json => (JSON: JsonDocument.Parse(json).RootElement, RAW: json))
        //     // Allow time for other vars to sync before H event. 
        //     // For example, changing the frequency of the G1000 radios would "cancel" the H events telling those vars to change, so we need those to be detected first.
        //     .SelectMany(p => p.JSON.TryGetProperty("type", out var type) && type.ToString() == "interaction"
        //         ? Observable.Timer(TimeSpan.FromMilliseconds(100)).Select(_ => p)
        //         : Observable.Return(p))
        //     .Subscribe(x =>
        //     {
        //         var json = x.JSON;
        //         if (json.TryGetProperty("key", out var key) &&
        //             json.TryGetProperty("instrument", out var instrument))
        //         {
        //             if (json.TryGetProperty("value", out var value)) Log.Information("[PACKET] SENT {Name} {Value} ({Instrument})", key.ToString(), value.ToString(), instrument);
        //             else Log.Information("[PACKET] SENT {Name} ({Instrument})", key.ToString(), instrument);
        //         }
        //             
        //         peer2Peer.SendAll(new ClientUpdate(x.RAW));
        //     }));
        //
        // _d.Add(_peer2Peer.Subscribe<ClientUpdate>(update =>
        // {
        //     var json = JsonDocument.Parse(update.Payload).RootElement;
        //     if (json.TryGetProperty("key", out var key) &&
        //         json.TryGetProperty("instrument", out var instrument))
        //     {
        //         if (json.TryGetProperty("value", out var value)) Log.Information("[PACKET] RECEIVE {Name} {Value} ({Instrument})", key.ToString(), value.ToString(), instrument);
        //         else Log.Information("[PACKET] RECEIVE {Name} ({Instrument})", key.ToString(), instrument);
        //     }
        //     _ = Task.WhenAll(socket.ListClients().Select(c => socket.SendAsync(c.Guid, update.Payload)));
        // }));
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

        _d.Add(_sim.Stream<TPacket>()
            .Where(_ => !master || _masterSwitch.IsMaster)
            .Subscribe(update => _peer2Peer.SendAll(update)));

        _d.Add(_peer2Peer.Subscribe<TPacket>(update =>
        {
            if (master && _masterSwitch.IsMaster) return;
            _sim.Set(update);
        }));
    }

    private void AddLink(Definition def)
    {
        var master = !def.Shared;
        var throttle = DateTime.MinValue;
        object? prevValue = null;
        var name = def.GetVar(out var units);
        _cSubs.Add(_sim.Stream(name, units)
            // .Sample(TimeSpan.FromMilliseconds(250))
            .Subscribe(val =>
            {
                prevValue = val;
                if (master && !_masterSwitch.IsMaster) return;
                if (DateTime.UtcNow <= throttle) return;
                _peer2Peer.SendAll(new Update(name, val));
                Log.Debug("[PACKET] SENT {Name} {Value}", name, val);
            }));

        _cSubs.Add(_peer2Peer.Subscribe<Update>(update =>
        {
            var datumName = update.Name;
            var value = update.Value;
            if (datumName != name) return;
            if (master && _masterSwitch.IsMaster) return;
            Log.Debug("[PACKET] RECEIVE {Name} {Value}", name, value);
            // if (prevValue == null) return;
            if (!def.TryGetEvent(value, prevValue ?? value, out var eventName, out var val0, out var val1, out var val2, out var val3, out var val4))
            {
                if (prevValue == value) return;
                prevValue = value;
                throttle = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                _sim.Set(datumName, value);
                return;
            }
            // throttle = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            _sim.Call(eventName, val0, val1, val2, val3, val4);
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
    
    private record ClientUpdate(string Payload)
    {
        public class Codec : IPacketCodec<ClientUpdate>
        {
            public void Encode(ClientUpdate packet, BinaryWriter bw)
            {
                bw.Write(packet.Payload);
            }

            public ClientUpdate Decode(BinaryReader br)
            {
                var payload = br.ReadString();
                return new(payload);
            }
        }
    }
}