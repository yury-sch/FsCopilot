namespace FsCopilot.Connection;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;
using WatsonWebsocket;
using Serilog;

public class SimClient : IDisposable
{
    private static readonly object DefaultHValue = 1;
    
    private readonly SimConnectHeadless _headless;
    private readonly ConcurrentDictionary<string, DEF> _defs = new();
    // private readonly ConcurrentDictionary<string, EVT> _events = new();
    private readonly ConcurrentDictionary<string, object> _streams = new();
    // private readonly ConcurrentDictionary<string, ushort> _lVars = new();
    private uint _defId = 100;
    private uint _requestId = 100;
    private readonly WatsonWsServer _socket;
    private readonly IObservable<WatchedVar> _varMessages;
    private readonly IObservable<string> _hEvents;
    // private readonly IObservable<(JsonElement Json, string Raw)> _socketMessages;

    public IObservable<bool> Connected => _headless.Connected.ObserveOn(TaskPoolScheduler.Default);
    public IObservable<string> Aircraft => _headless.Aircraft.ObserveOn(TaskPoolScheduler.Default);
    public IObservable<Interact> Interactions { get; }
    public IObservable<SimConfig> Config { get; }

    public SimClient(string appName)
    {
        _headless = new(appName);
        
        _socket = new(port: 8870);
        try { _socket.Start(); }
        catch (Exception e) { Log.Error(e, "Failed to start socket"); }
        
        // Set("L:FsCopilotStarted", true);

       var socketMessages = Observable
            .FromEventPattern<EventHandler<MessageReceivedEventArgs>, MessageReceivedEventArgs>(
                h => _socket.MessageReceived += h,
                h => _socket.MessageReceived -= h)
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(ep => Encoding.UTF8.GetString(ep.EventArgs.Data))
            .Do(json => Log.Debug("[SimConnect] RECV: {json}", json))
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Replay(0).RefCount();

       _varMessages = socketMessages
           .Where(json => json.String("type").Equals("var"))
           .Select(json => new WatchedVar(json.String("name"), json.Double("value")))
           .Replay(0).RefCount();

       _hEvents = socketMessages
           .Where(json => json.String("type").Equals("hevent"))
           .Select(json => json.String("name"))
           .Replay(0).RefCount();

       Interactions = socketMessages
           .Where(json => json.String("type").Equals("interact"))
            .Select(json => new Interact(json.String("instrument"), json.String("event"), json.String("id"), json.StringOrNull("value")))
            .Replay(0).RefCount();

       Config = socketMessages
           .Where(json => json.String("type").Equals("config"))
           .Select(json => new SimConfig(json.BoolOrNull("control") == null, json.BoolOrNull("control") ?? false))
           .Replay(0).RefCount();

        // _headless.Configure(sim =>
        // {
        //     var nextId = (EVT)Interlocked.Increment(ref _defId);
        //     sim.MapClientEventToSimEvent(nextId, "AXIS_AILERONS_SET");
        //     sim.AddClientEventToNotificationGroup(GRP.INPUTS, nextId, false);
        // }, _ => {});
    }

    public void Dispose()
    {
        _socket.Dispose();
        _headless.Dispose();
    }

    // public void Freeze(bool on)
    // {
    //     _headless.Post(sim =>
    //     {
    //         sim.SetInputGroupState(GRP.INPUTS, (uint)(on ? SIMCONNECT_STATE.OFF : SIMCONNECT_STATE.ON));
    //     });
    //     Set("FREEZE_LATITUDE_LONGITUDE_SET", on);
    //     Set("FREEZE_ALTITUDE_SET", on);
    //     Set("FREEZE_ATTITUDE_SET", on);
    // }

    public void Set<T>(T def) where T : struct
    {
        if (!_defs.TryGetValue(typeof(T).FullName!, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, def));
    }

    public void Set(SimConfig config)
    {
        var msg = Envelope("config", writer =>
        {
            writer.WriteBoolean("control", config.Control);
        });
        _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, msg)));
    }

    public void Set(Interact interact)
    {
        var msg = Envelope("interact", writer =>
        {
            writer.WriteString("instrument", interact.Instrument);
            writer.WriteString("event", interact.Event);
            writer.WriteString("id", interact.Id);
            writer.WriteString("value", interact.Value);
        });
        _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, msg)));
    }
    
    public void Set(string eventName, object value) =>
        Set(eventName, value, null, null, null, null);
    
    public void Set(string name, object value, object? value1, object? value2, object? value3, object? value4)
    {
        if (name.StartsWith("L:")) SetDataOnSimObject(name, value);
        if (name.StartsWith("A:")) SetDataOnSimObject(name[2..], value);
        if (name.StartsWith("Z:")) SetClientVar(name, value);
        if (name.StartsWith("H:")) SetClientVar(name, value);
        if (name.StartsWith("K:")) TransmitKEvent(name[2..], value, value1, value2, value3, value4);
        if (name.StartsWith("B:")) SetClientVar(name, value);
    }

    private void SetDataOnSimObject(string datumName, object value)
    {
        if (!_defs.TryGetValue(datumName, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }

    private void TransmitKEvent(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        var eventId = _defs.GetOrAdd($"K:{eventName}", _ =>
        {
            var nextId = (DEF)Interlocked.Increment(ref _defId);
            _headless.Configure(sim => sim.MapClientEventToSimEvent((EVT)nextId, eventName), _ => { });
            return nextId;
        });

        var dwData0 = NormalizeValue(value);
        var dwData1 = NormalizeValue(value1);
        var dwData2 = NormalizeValue(value2);
        var dwData3 = NormalizeValue(value3);
        var dwData4 = NormalizeValue(value4);

        _headless.Post(sim => sim.TransmitClientEvent_EX1(
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
        _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, msg)));
    }

    public IObservable<T> Stream<T>() where T : struct => 
        (IObservable<T>)_streams.GetOrAdd(typeof(T).FullName!, key => Observable.Create<T>(observer =>
        {
            var defId = (DEF)Interlocked.Increment(ref _defId);
            var reqId = (REQ)Interlocked.Increment(ref _requestId);
            var scheduler = new EventLoopScheduler();

            var sub = _headless.SimObjectData.ObserveOn(scheduler).Subscribe(e =>
                {
                    if ((DEF)e.dwDefineID != defId) return;
                    if (e.dwData is { Length: > 0 }) observer.OnNext((T)e.dwData[0]);
                },
                observer.OnError,
                observer.OnCompleted);

            Debug.WriteLine($"Subscribe {key}");
            var config = _headless.Configure(Initialize, Deinitialize);

            return () =>
            {
                sub.Dispose();
                config.Dispose();
                scheduler.Dispose();
            };

            void Initialize(SimConnect sim)
            {
                Debug.WriteLine($"Initialize {key}. DefId: {defId}");
                sim.AddToDataDefinitionFromStruct<T>(defId);
                sim.RegisterDataDefineStruct<T>(defId);
                sim.RequestDataOnSimObject(reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
                _defs.TryAdd(key, defId);
            }

            void Deinitialize(SimConnect sim)
            {
                Debug.WriteLine($"Deinitialize {key}. DefId: {defId}");
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.ClearDataDefinition(defId);
                _defs.TryRemove(key, out _);
            }
        }).Replay(1).RefCount());

    public IObservable<object> Stream(string name, string sUnits)
    {
        if (name.StartsWith("L:")) return SimVar(name, string.IsNullOrWhiteSpace(sUnits) ? "number" : sUnits, SIMCONNECT_DATATYPE.FLOAT32);
        if (name.StartsWith("Z:")) return ZVar(name);
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

            var sub = _headless.SimObjectData.ObserveOn(TaskPoolScheduler.Default).Subscribe(e =>
                {
                    if ((DEF)e.dwDefineID != defId) return;
                    if (e.dwData is { Length: > 0 }) observer.OnNext(e.dwData[0]);
                },
                observer.OnError,
                observer.OnCompleted);
            
            Debug.WriteLine($"Subscribe {key}");
            var config = _headless.Configure(Initialize, Deinitialize);

            return () =>
            {
                sub.Dispose();
                config.Dispose();
            };

            void Initialize(SimConnect sim)
            {
                Debug.WriteLine($"Initialize {key}. DefId: {defId}");
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

            void Deinitialize(SimConnect sim)
            {
                Debug.WriteLine($"Deinitialize {key}. DefId: {defId}");
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.ClearDataDefinition(defId);
                _defs.TryRemove(key, out _);
            }
        }).Replay(1).RefCount());
    
    private IObservable<object> ZVar(string name) => 
        (IObservable<object>)_streams.GetOrAdd(name, key => Observable.Create<object>(observer =>
        {
            var sub = _varMessages
                .Where(var => var.Name.Equals(name))
                .Subscribe(var => observer.OnNext(var.Value % 1 == 0 ? (int)var.Value : var.Value));
            
            var watch = Envelope("watch", writer =>
            {
                writer.WriteString("name", name);
                writer.WriteString("units", "number");
            });
            var conSub = Observable
                .FromEventPattern<EventHandler<ConnectionEventArgs>, ConnectionEventArgs>(
                    h => _socket.ClientConnected += h,
                    h => _socket.ClientConnected -= h)
                .Subscribe(x => _socket.SendAsync(x.EventArgs.Client.Guid, watch));
            
            _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, watch)));
    
            return () =>
            {
                conSub.Dispose();
                sub.Dispose();
                var unwatch = Envelope("unwatch", writer => writer.WriteString("name", name));
                _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, unwatch)));
            };
        }).Replay(1).RefCount());

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

    private record WatchedVar(string Name, double Value);
}