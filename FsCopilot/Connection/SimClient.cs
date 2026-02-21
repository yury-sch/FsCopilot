namespace FsCopilot.Connection;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;

public class SimClient : IDisposable
{
    private const string WasmVersion = "1.1";
    
    private static readonly object DefaultHValue = 1;
    
    private readonly SimConnectConsumer _consumer;
    private readonly SimConnectProducer _producer;
    private readonly ConcurrentDictionary<string, DEF> _defs = new();
    private readonly ConcurrentDictionary<string, object> _streams = new();
    private uint _defId = 100;
    private uint _requestId = 100;
    // private readonly IObservable<WatchedVar> _varMessages;
    private readonly IObservable<string> _hEvents;
    private readonly IObservable<bool> _conflict;
    private readonly BehaviorSubject<BehaviorControl> _control = new(BehaviorControl.Master);
    private readonly DEF _commBusDefId;
    private readonly DEF _varWatchDefId;
    private readonly DEF _watchDefId;
    private readonly DEF _unwatchDefId;
    private readonly DEF _setDefId;

    public IObservable<bool> Connected => _consumer.Connected.ObserveOn(TaskPoolScheduler.Default);
    public IObservable<string> Aircraft => _consumer.Aircraft.ObserveOn(TaskPoolScheduler.Default);
    public IObservable<bool> Conflict => _conflict.ObserveOn(TaskPoolScheduler.Default);
    public IObservable<bool> WasmReady;
    public IObservable<bool> WasmVersionMismatch;
    public IObservable<Interact> Interactions { get; }
    // public IObservable<SimConfig> Config { get; }

    public SimClient(string appName)
    {
        _consumer = new(appName);
        _producer = new(appName);
        
        var readyDefId = RegisterClientStruct<StrMsg>("FSC_READY", producer: false);
        var commBusDefId = RegisterClientStruct<StrMsg>("FSC_BUS_OUT", producer: false);
        var controlDefId = RegisterClientStruct<ControlMsg>("FSC_CONTROL", producer: true);
        _commBusDefId = RegisterClientStruct<StrMsg>("FSC_BUS_IN", producer: true);
        _watchDefId = RegisterClientStruct<VarSetMsg>("FSC_WATCH", producer: true);
        _unwatchDefId = RegisterClientStruct<VarSetMsg>("FSC_UNWATCH", producer: true);
        _varWatchDefId = RegisterClientStruct<VarSetMsg>("FSC_VARIABLE", producer: false);
        _setDefId = RegisterClientStruct<VarSetMsg>("FSC_SET", producer: true);

        var wasmVersion = new Subject<string>();
        _consumer.SimClientData
            .Where(e => (DEF)e.dwDefineID == readyDefId && e.dwData is { Length: > 0 })
            .Select(e => (StrMsg)e.dwData[0])
            .Subscribe(msg => wasmVersion.OnNext(msg.Msg));
       
        WasmReady = wasmVersion
            .Select(v => !string.IsNullOrWhiteSpace(v));
       
        WasmVersionMismatch = wasmVersion
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => !v.Equals(WasmVersion));

        Observable.Interval(TimeSpan.FromMilliseconds(200))
            .WithLatestFrom(_control, (_, control) => control)
            .Subscribe(control => _producer.Post(sim => sim.SetClientData(controlDefId, controlDefId,
                SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new ControlMsg { Value = (int)control })));
        
        _consumer.Configure(sim => sim.RequestClientData(
            readyDefId, readyDefId, readyDefId,
            SIMCONNECT_CLIENT_DATA_PERIOD.SECOND, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0), _ => {});

        _consumer.Configure(sim => sim.RequestClientData(
            commBusDefId, commBusDefId, commBusDefId, 
            SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0), _ => {});
       
        _consumer.Configure(sim => sim.RequestClientData(
            _varWatchDefId, _varWatchDefId, _varWatchDefId, 
            SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0), _ => {});
 
        var socketMessages = _consumer.SimClientData
            .ObserveOn(TaskPoolScheduler.Default)
            .Where(e => (DEF)e.dwDefineID == commBusDefId)
            .Select(e => (StrMsg)e.dwData[0])
            .Select(msg => msg.Msg)
            .Do(json => Log.Verbose("[SimConnect] RECV: {json}", json))
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Replay(0).RefCount();

       _hEvents = socketMessages
           .Where(json => json.String("type").Equals("hevent"))
           .Select(json => json.String("name"))
           .Replay(0).RefCount();

       Interactions = socketMessages
           .Where(json => json.String("type").Equals("interact"))
            .Select(json => new Interact(json.String("instrument"), json.String("event"), json.String("id"), json.StringOrNull("value")))
            .Replay(0).RefCount();

       // Config = socketMessages
       //     .Where(json => json.String("type").Equals("config"))
       //     .Select(json => new SimConfig(json.BoolOrNull("control") == null, json.BoolOrNull("control") ?? false))
       //     .Replay(0).RefCount();

       _conflict = Stream("L:YourControlsPanelId", "number")
           .Select(value => Convert.ToInt32(value) > 0)
           .DistinctUntilChanged()
           .Replay(0).RefCount(); 
       
       return;
       
       DEF RegisterClientStruct<T>(string name, bool producer)
       {
           var defId = (DEF)Interlocked.Increment(ref _defId);
           Action<SimConnect> configure = sim =>
           {
               sim.MapClientDataNameToID(name, defId);
               var size = (uint)Marshal.SizeOf<T>();
               sim.CreateClientData(defId, size, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
               sim.AddToClientDataDefinition(defId, 0, size, 0, 0);
               sim.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, T>(defId);
           };
           
           if (producer) _producer.Configure(configure, _ => {});
           else _consumer.Configure(configure, _ => {});
        
           return defId;
       }
    }

    public void Dispose()
    {
        _consumer.Dispose();
        _producer.Dispose();
    }
    
    public IDisposable Register<T>() where T : unmanaged
    {
        var key = typeof(T).FullName!;
        var defId = (DEF)Interlocked.Increment(ref _defId);
        if (!_defs.TryAdd(typeof(T).FullName!, defId)) return Disposable.Empty;
        
        return new CompositeDisposable(
            _consumer.Configure(InitializeConsumer, DeinitializeConsumer),
            _producer.Configure(InitializeProducer, DeinitializeProducer),
            Disposable.Create(() => _defs.TryRemove(key, out _)));
        
        void InitializeConsumer(SimConnect sim)
        {
            sim.AddToDataDefinitionFromStruct<T>(defId);
            sim.RegisterDataDefineStruct<T>(defId);
        }

        void DeinitializeConsumer(SimConnect sim)
        {
            sim.ClearDataDefinition(defId);
        }

        void InitializeProducer(SimConnect sim)
        {
            sim.MapClientDataNameToID($"FSC_{typeof(T).Name.ToUpper()}", defId);

            var size = (uint)Unsafe.SizeOf<T>();
            try { sim.CreateClientData(defId, size, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT); }
            catch (Exception) { /* ignore */ }

            sim.AddToClientDataDefinition(defId, 0, size, 0.0f, 0xFFFFFFFF);
        }
        
        void DeinitializeProducer(SimConnect sim)
        {
            sim.ClearClientDataDefinition(defId);
        }   
    }

    public void Set<T>(T def) where T : unmanaged
    {
        if (!_defs.TryGetValue(typeof(T).FullName!, out var defId)) return;
        
        _producer.Post(sim => sim.SetClientData(defId, defId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, def));
    }

    // public void Set(SimConfig config)
    // {
    //     var msg = Envelope("config", writer =>
    //     {
    //         writer.WriteBoolean("control", config.Control);
    //     });
    //     
    //     _producer.Post(sim => sim.SetClientData(_commBusDefId, _commBusDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new CommBusMsg { Msg = msg }));
    // }

    public void SetControl(BehaviorControl value) => _control.OnNext(value);

    public void Set(Interact interact)
    {
        var msg = Envelope("interact", writer =>
        {
            writer.WriteString("instrument", interact.Instrument);
            writer.WriteString("event", interact.Event);
            writer.WriteString("id", interact.Id);
            writer.WriteString("value", interact.Value);
        });
        _producer.Post(sim => sim.SetClientData(_commBusDefId, _commBusDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new StrMsg { Msg = msg }));
    }
    
    public void Set(string eventName, object value) =>
        Set(eventName, value, null, null, null, null);
    
    public void Set(string name, object value, object? value1, object? value2, object? value3, object? value4)
    {
        if (name.StartsWith("L:")) SetLVar(name, Convert.ToSingle(value));
        if (name.StartsWith("A:")) SetSimVar(name[2..], value);
        if (name.StartsWith("Z:")) SetClientVar(name, value);
        if (name.StartsWith("H:")) SetClientVar(name, value);
        if (name.StartsWith("B:")) SetClientVar(name, value);
        if (name.StartsWith("K:")) TransmitKEvent(name[2..], value, value1, value2, value3, value4);
    }

    private void SetLVar(string datumName, object value)
    {
        var defId = _defs.GetOrAdd(datumName, _ =>
        {
            var nextId = (DEF)Interlocked.Increment(ref _defId);
            _producer.Configure(sim =>
            {
                const SIMCONNECT_DATATYPE datumType = SIMCONNECT_DATATYPE.FLOAT32;
                var clrType = SimConnectExtensions.ToClrType(datumType);

                sim.AddToDataDefinition(nextId, datumName, "number", datumType, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                typeof(SimConnect).GetMethod(nameof(SimConnect.RegisterDataDefineStruct),
                        BindingFlags.Public | BindingFlags.Instance)!
                    .MakeGenericMethod(clrType)
                    .Invoke(sim, [nextId]);
            }, _ => { });
            return nextId;
        });
        
        _producer.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }

    private void SetSimVar(string datumName, object value)
    {
        if (!_defs.TryGetValue(datumName, out var defId)) return;
        
        _producer.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }

    private void TransmitKEvent(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        var eventId = _defs.GetOrAdd($"K:{eventName}", _ =>
        {
            var nextId = (DEF)Interlocked.Increment(ref _defId);
            _producer.Configure(sim => sim.MapClientEventToSimEvent((EVT)nextId, eventName), _ => { });
            return nextId;
        });

        var dwData0 = NormalizeValue(value);
        var dwData1 = NormalizeValue(value1);
        var dwData2 = NormalizeValue(value2);
        var dwData3 = NormalizeValue(value3);
        var dwData4 = NormalizeValue(value4);

        _producer.Post(sim => sim.TransmitClientEvent_EX1(
            SimConnect.SIMCONNECT_OBJECT_ID_USER, (EVT)eventId, GRP.DUMMY,
            SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY,
            dwData0 ?? 0, dwData1 ?? 0, dwData2 ?? 0, dwData3 ?? 0, dwData4 ?? 0));
        return;

        uint? NormalizeValue(object? val)
        {
            if (val == null) return null;
            uint vb = val switch
            {
                uint ui => ui,
                bool b => b ? 1u : 0u,
                byte by => by,
                sbyte sb => (byte)sb,
                short s => (ushort)s,
                ushort us => us,
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                ulong ul => unchecked((uint)ul),
                float d => unchecked((uint)(int)Math.Round(d, MidpointRounding.AwayFromZero)),
                double d => unchecked((uint)(int)Math.Round(d, MidpointRounding.AwayFromZero)),
                Enum e => Convert.ToUInt32(e),
                _ => 0
            };
            return vb;
        }
    }

    private void SetClientVar(string name, object value)
    {
        var msg = Envelope("set", writer =>
        {
            writer.WriteString("name", name);
            writer.WritePrimitive("value", value);
        });
        // _producer.Post(sim => sim.SetClientData(_commBusDefId, _commBusDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new CommBusMsg { Msg = msg }));
        _producer.Post(sim => sim.SetClientData(_setDefId, _setDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new VarSetMsg { Name = name, Value = Convert.ToDouble(value) }));
    }

    public IObservable<T> Stream<T>() where T : struct
    {
        var key = typeof(T).FullName!;
        if (!_defs.TryGetValue(key, out var defId)) return Observable.Empty<T>();
        
        return (IObservable<T>)_streams.GetOrAdd(key, _ => Observable.Create<T>(observer =>
        {
            var scheduler = new EventLoopScheduler();
            var started = false;
            ulong seq = 0;

            var sub = _consumer.SimObjectData
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(e =>
                {
                    if ((DEF)e.dwDefineID != defId) return;
                    if (e.dwData is not { Length: > 0 }) return;
                    if (!started)
                    {
                        started = true;
                        Log.Debug("[SimConnect] Stream {Stream} started", typeof(T).Name);
                    }

                    seq++;
                    observer.OnNext((T)e.dwData[0]);
                },
                observer.OnError,
                observer.OnCompleted);

            var consumerConfig = _consumer.Configure(InitializeConsumer, DeinitializeConsumer);

            return () =>
            {
                sub.Dispose();
                consumerConfig.Dispose();
                scheduler.Dispose();
            };

            void InitializeConsumer(SimConnect sim) =>
                sim.RequestDataOnSimObject(
                    (REQ)Interlocked.Increment(ref _requestId), defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

            void DeinitializeConsumer(SimConnect sim) =>
                sim.RequestDataOnSimObject(
                    (REQ)Interlocked.Increment(ref _requestId), defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        }).Replay(1).RefCount());
    }

    public IObservable<object> Stream(string name, string sUnits)
    {
        if (name.StartsWith("L:")) return SimVar(name, string.IsNullOrWhiteSpace(sUnits) ? "number" : sUnits, SIMCONNECT_DATATYPE.FLOAT32);
        if (name.StartsWith("B:")) return ClientVar(name, string.IsNullOrWhiteSpace(sUnits) ? "number" : sUnits);
        if (name.StartsWith("Z:")) return ClientVar(name, string.IsNullOrWhiteSpace(sUnits) ? "number" : sUnits);
        if (name.StartsWith("A:")) return SimVar(name[2..], sUnits);
        if (name.StartsWith("H:")) return HVar(name);
        // if (datumName.StartsWith("K:")) return KEvent(datumName[2..], sUnits);
        return Observable.Empty<object>();
    }

    private IObservable<object> SimVar(string datumName, string sUnits, SIMCONNECT_DATATYPE? datatype = null) => 
        (IObservable<object>)_streams.GetOrAdd(datumName, key => Observable.Create<object>(observer =>
        {
            var defId = (DEF)Interlocked.Increment(ref _defId);
            var reqId = (REQ)Interlocked.Increment(ref _requestId);

            var sub = _consumer.SimObjectData.ObserveOn(TaskPoolScheduler.Default).Subscribe(e =>
                {
                    if ((DEF)e.dwDefineID != defId) return;
                    if (e.dwData is { Length: > 0 }) observer.OnNext(e.dwData[0]);
                },
                observer.OnError,
                observer.OnCompleted);

            var consumerConfig = _consumer.Configure(InitializeConsumer, DeinitializeConsumer);
            var producerConfig = _producer.Configure(InitializeProducer, DeinitializeProducer);

            return () =>
            {
                sub.Dispose();
                consumerConfig.Dispose();
                producerConfig.Dispose();
            };

            void InitializeConsumer(SimConnect sim)
            {
                SIMCONNECT_DATATYPE datumType = datatype ?? SimConnectExtensions.InferDataType(sUnits);
                var clrType = SimConnectExtensions.ToClrType(datumType);

                sim.AddToDataDefinition(defId, datumName, sUnits, datumType, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                // sim.RegisterDataDefineStruct<T>((DEF)nextId);

                typeof(SimConnect).GetMethod(nameof(SimConnect.RegisterDataDefineStruct),
                        BindingFlags.Public | BindingFlags.Instance)!
                    .MakeGenericMethod(clrType)
                    .Invoke(sim, [defId]);
            
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
                _defs.TryAdd(key, defId);
            }

            void DeinitializeConsumer(SimConnect sim)
            {
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.ClearDataDefinition(defId);
                _defs.TryRemove(key, out _);
            }

            void InitializeProducer(SimConnect sim)
            {
                SIMCONNECT_DATATYPE datumType = datatype ?? SimConnectExtensions.InferDataType(sUnits);
                var clrType = SimConnectExtensions.ToClrType(datumType);

                sim.AddToDataDefinition(defId, datumName, sUnits, datumType, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                typeof(SimConnect).GetMethod(nameof(SimConnect.RegisterDataDefineStruct),
                        BindingFlags.Public | BindingFlags.Instance)!
                    .MakeGenericMethod(clrType)
                    .Invoke(sim, [defId]);
            }

            void DeinitializeProducer(SimConnect sim)
            {
                sim.ClearDataDefinition(defId);
            }
        }).Replay(1).RefCount());

    private IObservable<object> ClientVar(string datumName, string sUnits) => 
        (IObservable<object>)_streams.GetOrAdd(datumName, key => Observable.Create<object>(observer =>
        {
            var sub = _consumer.SimClientData
                .ObserveOn(TaskPoolScheduler.Default)
                .Where(e => (DEF)e.dwDefineID == _varWatchDefId && e.dwData is { Length: > 0 })
                .Select(e => (VarSetMsg)e.dwData[0])
                .Where(e => e.Name.Equals(datumName, StringComparison.InvariantCultureIgnoreCase))
                .Subscribe(e => observer.OnNext(e.Value),
                    observer.OnError,
                    observer.OnCompleted);

            var watch = Observable.Interval(TimeSpan.FromMilliseconds(1000))
                .StartWith(0)
                // _wasmReady
                // .Where(ready => ready)
                .Subscribe(_ => _producer.Post(sim => sim.SetClientData(_watchDefId, _watchDefId,
                    SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new VarSetMsg { Name = datumName, Units = sUnits })));
 
            return () =>
            {
                watch.Dispose();
                sub.Dispose();
                _producer.Post(sim => sim.SetClientData(_unwatchDefId, _unwatchDefId,
                    SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new VarSetMsg { Name = datumName }));
            };
        }).Replay(1).RefCount());
    
    // private IObservable<object> ZVar(string name) => 
    //     (IObservable<object>)_streams.GetOrAdd(name, key => Observable.Create<object>(observer =>
    //     {
    //         var sub = _varMessages
    //             .Where(var => var.Name.Equals(name))
    //             .Subscribe(var => observer.OnNext(var.Value % 1 == 0 ? (int)var.Value : var.Value));
    //         
    //         var watch = Envelope("watch", writer =>
    //         {
    //             writer.WriteString("name", name);
    //             writer.WriteString("units", "number");
    //         });
    //         _producer.Post(sim => sim.SetClientData(_commBusDefId, _commBusDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new CommBusMsg { Msg = watch }));
    //
    //         return () =>
    //         {
    //             sub.Dispose();
    //             var unwatch = Envelope("unwatch", writer => writer.WriteString("name", name));
    //             _producer.Post(sim => sim.SetClientData(_commBusDefId, _commBusDefId, SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT, 0, new CommBusMsg { Msg = unwatch }));
    //         };
    //     }).Replay(1).RefCount());

    private IObservable<object> HVar(string name) => 
        (IObservable<object>)_streams.GetOrAdd(name, key => Observable.Create<object>(observer =>
        {
            var sub = _hEvents
                .Where(ev => ev.Equals(name[2..]))
                .Subscribe(row => observer.OnNext(DefaultHValue));
    
            return () => sub.Dispose();
        }).Replay(1).RefCount());

    private static string Envelope(string type, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            write(writer);
            writer.WriteEndObject();    
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private enum DEF : uint;
    private enum REQ : uint;
    private enum EVT : uint;
    private enum GRP { DUMMY, INPUTS }

    // private record WatchedVar(string Name, double Value);
    
    // [StructLayout(LayoutKind.Sequential, Pack = 1)]
    // private struct InterpolateHeader
    // {
    //     public uint Seq;
    //     public uint TimeMs;
    // }
    //
    // [StructLayout(LayoutKind.Sequential, Pack = 1)]
    // private struct Interpolate<T> where T : unmanaged
    // {
    //     public InterpolateHeader Header;
    //     public T Data;
    // }
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct ControlMsg
    {
        public int Value;
    }
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct StrMsg
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string Msg;
    }
    
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct VarSetMsg
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Units;
        public double Value;
    }
}