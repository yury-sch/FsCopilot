using Serilog;

namespace FsCopilot.Connection;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.FlightSimulator.SimConnect;
using WatsonWebsocket;

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
    private readonly Subject<string> _hEvents = new();
    private readonly WatsonWsServer _socket;
    private readonly IObservable<JsonElement> _socketMessages;

    public IObservable<bool> Connected => _headless.Connected;
    public IObservable<string> Aircraft => _headless.Aircraft;
    
    public IObservable<string> HEvents => _hEvents;

    public SimClient(string appName)
    {
        _headless = new(appName);
        
        _socket = new(port: 8870);
        try { _socket.Start(); }
        catch (Exception e) { Log.Error(e, "Failed to start socket"); }
        
        // Set("L:FsCopilotStarted", true);
         
        _socketMessages = Observable
            .FromEventPattern<EventHandler<MessageReceivedEventArgs>, MessageReceivedEventArgs>(
                h => _socket.MessageReceived += h,
                h => _socket.MessageReceived -= h)
            .Select(ep => Encoding.UTF8.GetString(ep.EventArgs.Data))
            .Select(json =>
            {
                Debug.WriteLine(json);
                return JsonDocument.Parse(json).RootElement;
            });
            
        _socketMessages.Subscribe(json => 
            {
                var type = json.TryGetProperty("type", out var typeProp) ? typeProp.ToString() : string.Empty;
                if (type == "hevent" && json.TryGetProperty("key", out var keyProp))
                {
                    _hEvents.OnNext(keyProp.ToString());
                    return;
                }
            });

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
    //     Call("FREEZE_LATITUDE_LONGITUDE_SET", on);
    //     Call("FREEZE_ALTITUDE_SET", on);
    //     Call("FREEZE_ATTITUDE_SET", on);
    // }

    public void Call(string eventName) =>
        Call(eventName, null, null, null, null, null);

    public void Call(string eventName, object? value) =>
        Call(eventName, value, null, null, null, null);

    public void Call(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        if (eventName.StartsWith("K:")) TransmitKEvent(eventName[2..], value, value1, value2, value3, value4);
        if (eventName.StartsWith("B:")) TransmitBEvent(eventName, value, value1, value2, value3, value4);
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
            
            val = val switch
            {
                uint u => u,
                int i and >= 0 => (uint)i,
                double d and >= 0 and <= uint.MaxValue when Math.Floor(d) == d => (uint)d,
                float f and >= 0 and <= uint.MaxValue when MathF.Floor(f) == f => (uint)f,
                decimal m and >= 0 and <= uint.MaxValue when decimal.Truncate(m) == m => (uint)m,
                string s when uint.TryParse(s, out var parsed) => parsed,
                _ => value
            };
            
            // todo fix conversion for double value. For some reason it doesn't work. Maybe we need to switch between big/little endian? I don't know yet what simconnect expecting for
            return val switch
            {
                uint ui => ui,
                bool b => b ? 1u : 0u,
                byte by => by,
                sbyte sb => (uint)(byte)sb,
                short s => (uint)(ushort)s,
                ushort us => us,
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                ulong ul => unchecked((uint)ul),
                float f => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0),
                double d => BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0),
                Enum e => Convert.ToUInt32(e),
                _ => 0
            };
        }
    }

    private void TransmitBEvent(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        var payload = $$"""{"type":"set","key":"{{eventName}}","value":"{{value}}"}""";
        _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, payload)));
    }

    public void Set<T>(T def) where T : struct
    {
        if (!_defs.TryGetValue(typeof(T).FullName!, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, def));
    }

    public void Set(string name, object value)
    {
        if (name.StartsWith("L:"))
        {
            SetDataOnSimObject(name, value);
        }
        else if (name.StartsWith("A:"))
        {
            SetDataOnSimObject(name[2..], value);
        }
        else if (name.StartsWith("H:"))
        {
            var payload = $$"""{"type":"call","key":"{{name}}"}""";
            _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, payload)));
        }
    }

    private void SetDataOnSimObject(string datumName, object value)
    {
        if (!_defs.TryGetValue(datumName, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }

    public IObservable<T> Stream<T>() where T : struct => 
        (IObservable<T>)_streams.GetOrAdd(typeof(T).FullName!, key => Observable.Create<T>(observer =>
        {
            var defId = (DEF)Interlocked.Increment(ref _defId);
            var reqId = (REQ)Interlocked.Increment(ref _requestId);

            var sub = _headless.SimObjectData.Subscribe(e =>
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
        if (name.StartsWith("L:")) return SimVar(name, "number");
        if (name.StartsWith("A:")) return SimVar(name[2..], sUnits);
        if (name.StartsWith("H:")) return HVar(name);
        // if (datumName.StartsWith("K:")) return KEvent(datumName[2..], sUnits);
        return Observable.Empty<object>();
    }

    private IObservable<object> SimVar(string datumName, string sUnits) => 
        (IObservable<object>)_streams.GetOrAdd(datumName, key => Observable.Create<object>(observer =>
        {
            var defId = (DEF)Interlocked.Increment(ref _defId);
            var reqId = (REQ)Interlocked.Increment(ref _requestId);

            var sub = _headless.SimObjectData.Subscribe(e =>
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
                var datumType = SimConnectExtensions.InferDataType(sUnits);
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
    
    private IObservable<object> HVar(string datumName) =>
        _hEvents.Where(e => string.Equals(e, datumName[2..], StringComparison.OrdinalIgnoreCase)).Select(_ => DefaultHValue);

    private enum DEF : uint;
    private enum REQ : uint;
    private enum EVT : uint;
    private enum GRP { DUMMY, INPUTS }
}

