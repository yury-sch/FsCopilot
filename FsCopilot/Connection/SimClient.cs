namespace FsCopilot.Connection;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Runtime.InteropServices;
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

    public IObservable<bool> Connected => _headless.Connected;

    public SimClient(string appName)
    {
        _headless = new(appName);
        
        _headless.Configure(sim =>
        {
            const int messageSize = 256;
            
            // register Client Data (for LVars)
            sim.MapClientDataNameToID("HABI_WASM.LVars", CLIENT_DATA_ID.LVARS);
            sim.CreateClientData(CLIENT_DATA_ID.LVARS, SimConnect.SIMCONNECT_CLIENTDATA_MAX_SIZE, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);

            // register Client Data (for WASM Module Commands)
            sim.MapClientDataNameToID("HABI_WASM.Command", CLIENT_DATA_ID.CMD);
            sim.CreateClientData(CLIENT_DATA_ID.CMD, messageSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
            sim.AddToClientDataDefinition(CLIENTDATA_DEFINITION_ID.CMD, 0, messageSize, 0, 0);

            // register Client Data (for LVar acknowledge)
            sim.MapClientDataNameToID("HABI_WASM.Acknowledge", CLIENT_DATA_ID.ACK);
            sim.CreateClientData(CLIENT_DATA_ID.ACK, (uint)Marshal.SizeOf<LVarAck>(), SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
            sim.AddToClientDataDefinition(CLIENTDATA_DEFINITION_ID.ACK, 0, (uint)Marshal.SizeOf<LVarAck>(), 0, 0);
            sim.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, LVarAck>(CLIENTDATA_DEFINITION_ID.ACK);
            sim.RequestClientData(
                CLIENT_DATA_ID.ACK,
                CLIENTDATA_REQUEST_ID.ACK,
                CLIENTDATA_DEFINITION_ID.ACK,
                SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);

            // register Client Data (for RESULT)
            sim.MapClientDataNameToID("HABI_WASM.Result", CLIENT_DATA_ID.RESULT);
            sim.CreateClientData(CLIENT_DATA_ID.RESULT, (uint)Marshal.SizeOf<Result>(), SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
            sim.AddToClientDataDefinition(CLIENTDATA_DEFINITION_ID.RESULT, 0, (uint)Marshal.SizeOf<Result>(), 0, 0);
            sim.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, Result>(CLIENTDATA_DEFINITION_ID.RESULT);
            sim.RequestClientData(
                CLIENT_DATA_ID.RESULT,
                CLIENTDATA_REQUEST_ID.RESULT,
                CLIENTDATA_DEFINITION_ID.RESULT,
                SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);
        }, _ => {});
        
        Set("L:FsCopilotStarted", true);
        
        _socket = new(port: 8870);
        _socket.Start();
        
        Observable
            .FromEventPattern<EventHandler<MessageReceivedEventArgs>, MessageReceivedEventArgs>(
                h => _socket.MessageReceived += h,
                h => _socket.MessageReceived -= h)
            .Select(ep => Encoding.UTF8.GetString(ep.EventArgs.Data))
            .Select(json =>
            {
                Debug.WriteLine(json);
                return JsonDocument.Parse(json).RootElement;
            })
            // .Where(json => json.TryGetProperty("type", out var type) && type.ToString() == "interaction")
            // .Select(json => json.TryGetProperty())
            .Subscribe(json => 
            {
                var type = json.TryGetProperty("type", out var typeProp) ? typeProp.ToString() : string.Empty;
                if (type == "interaction" && json.TryGetProperty("key", out var keyProp))
                {
                    _hEvents.OnNext(keyProp.ToString());
                    return;
                }
                
            });
    }

    public void Dispose()
    {
        _socket.Dispose();
        _headless.Dispose();
    }
    
    public Task<T> SystemState<T>(string szState)
    {
        var reqId = Interlocked.Increment(ref _requestId);
        
        var tcs = new TaskCompletionSource<T>();

        _headless.SystemState.Where(e => e.dwRequestID == reqId).Take(1).Subscribe(data =>
        {
            // var match = Regex.Match(data.szString, @"SimObjects\\Airplanes\\([^\\]+)");
            // if (match.Success)
            // {
            //     string folder = match.Groups[1].Value;
            //     Console.WriteLine(folder);
            // }
            //
            // Console.WriteLine($"aircraft.cfg path (raw): {path}");

            if (typeof(T) == typeof(string))
                tcs.SetResult((T)(object)data.szString);
            else if (typeof(T) == typeof(float))
                tcs.SetResult((T)(object)data.fFloat);
            else if (typeof(T) == typeof(uint)) 
                tcs.SetResult((T)(object)data.dwInteger);
        });
        
        _headless.Post(sim => sim.RequestSystemState((REQ)reqId, szState));
        return tcs.Task;
    }

    public void Call(string eventName) =>
        Call(eventName, null, null, null, null, null);

    public void Call(string eventName, object? value) =>
        Call(eventName, value, null, null, null, null);

    public void Call(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        if (eventName.StartsWith("K:")) TransmitKEvent(eventName[2..], value, value1, value2, value3, value4);
        if (eventName.StartsWith("B:")) TransmitBEvent(eventName[2..], value, value1, value2, value3, value4);
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
            SimConnect.SIMCONNECT_OBJECT_ID_USER, (EVT)eventId, GRP.Dummy,
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
                double d => BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0), // we're losing accuracy, but 1:1 with the SDK
                Enum e => Convert.ToUInt32(e),
                _ => 0
            };
        }
    }

    private void TransmitBEvent(string eventName, object? value, object? value1, object? value2, object? value3, object? value4)
    {
        var cmd = $"HW.Exe.{Convert.ToString(value, CultureInfo.InvariantCulture)} (>B:{eventName})";
        Debug.WriteLine(cmd);
        _headless.Post(sim => sim.SetClientData(
            CLIENT_DATA_ID.CMD,
            CLIENTDATA_DEFINITION_ID.CMD,
            SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
            0,
            new String256 { Value = cmd }
        ));
    }

    public void Set<T>(T def) where T : struct
    {
        if (!_defs.TryGetValue(typeof(T).FullName!, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject(defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, def));
    }

    public void Set(string datumName, object value)
    {
        if (datumName.StartsWith("L:"))
        {
            var cmd = $"HW.Set.{Convert.ToString(value, CultureInfo.InvariantCulture)} (>L:{datumName[2..]})";
            Debug.WriteLine(cmd);
            _headless.Post(sim => sim.SetClientData(
                CLIENT_DATA_ID.CMD,
                CLIENTDATA_DEFINITION_ID.CMD,
                SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
                0,
                new String256 { Value = cmd }
            ));
        }
        else if (datumName.StartsWith("A:"))
        {
            SetDataOnSimObject(datumName[2..], value);
        }
        else if (datumName.StartsWith("H:"))
        {
            var key = datumName[2..];
            // var payload = $$"""{"type":"interaction", "key": "{key}"}""";
            var payload = $$"""{"type":"interaction","key":"{{key}}"}""";
            _ = Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, payload)));
            SetDataOnSimObject(datumName[2..], value);
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

            var config = _headless.Configure(Initialize, Deinitialize);

            return () =>
            {
                sub.Dispose();
                config.Dispose();
            };

            void Initialize(SimConnect sim)
            {
                Debug.WriteLine($"Initialize {key}");
                sim.AddToDataDefinitionFromStruct<T>(defId);
                sim.RegisterDataDefineStruct<T>(defId);
                sim.RequestDataOnSimObject(reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
                _defs.TryAdd(key, defId);
            }

            void Deinitialize(SimConnect sim)
            {
                Debug.WriteLine($"Deinitialize {key}");
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.ClearDataDefinition(defId);
                _defs.TryRemove(key, out _);
            }
        }).Replay(1).RefCount());

    public IObservable<object> Stream(string datumName, string sUnits)
    {
        if (datumName.StartsWith("L:")) return LVar(datumName[2..]);
        else if (datumName.StartsWith("A:")) return SimVar(datumName[2..], sUnits);
        else if (datumName.StartsWith("H:")) return HVar(datumName[2..]);
        // else if (datumName.StartsWith("K:")) return KEvent(datumName[2..], sUnits);
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

            var config = _headless.Configure(Initialize, Deinitialize);

            return () =>
            {
                sub.Dispose();
                config.Dispose();
            };

            void Initialize(SimConnect sim)
            {
                Debug.WriteLine($"Initialize {key}");
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
                Debug.WriteLine($"Deinitialize {key}");
                sim.RequestDataOnSimObject(
                    reqId, defId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                sim.ClearDataDefinition(defId);
                _defs.TryRemove(key, out _);
            }
        }).Replay(1).RefCount());

    private IObservable<object> LVar(string name) => 
        (IObservable<object>)_streams.GetOrAdd(name, key => Observable.Create<object>(observer =>
        {
            //var defId = (DEF)Interlocked.Increment(ref _defId);
            //var reqId = (REQ)Interlocked.Increment(ref _requestId);
            DEF? defId = null;
            IDisposable? config = null;
            
            var sub = _headless.ClientData
                .Subscribe(data =>
                {
                    if (data.dwRequestID == (uint)CLIENTDATA_REQUEST_ID.ACK)
                    {
                        var ack = (LVarAck)data.dwData[0];
                        if (ack.str != $"(L:{name})") return;
                        defId = (DEF)ack.DefineID;
                        config = _headless.Configure(
                            sim => Initialize(sim, ack.Offset), 
                            Deinitialize);
                    }
                    else if (data.dwRequestID >= (uint)CLIENTDATA_REQUEST_ID.START_LVAR && (DEF)data.dwDefineID == defId)
                    {
                        observer.OnNext(data.dwData[0]);
                    }
                });
            
            _headless.Post(sim => sim.SetClientData(
                CLIENT_DATA_ID.CMD,
                CLIENTDATA_DEFINITION_ID.CMD,
                SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
                0,
                new String256 { Value = $"HW.Reg.(L:{name})" }));

            return () =>
            {
                sub.Dispose();
                config?.Dispose();
            };

            void Initialize(SimConnect sim, ushort offset)
            {
                Debug.WriteLine($"Initialize {key}");
                var reqId = (REQ)Interlocked.Increment(ref _requestId);
                sim.AddToClientDataDefinition(defId, offset, sizeof(float), 0, 0);
                sim.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, float>(defId);
                sim.RequestClientData(
                    CLIENT_DATA_ID.LVARS, reqId, defId,
                    // data will be sent whenever SetClientData is used on this client area (even if this defineID doesn't change)
                    SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
                    // if this is used, this defineID only is sent when its value has changed
                    SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.CHANGED,
                    0, 0, 0);
            }
            
            void Deinitialize(SimConnect sim)
            {
                Debug.WriteLine($"Deinitialize {key}");
                var reqId = (REQ)Interlocked.Increment(ref _requestId);
                sim.RequestClientData(
                    CLIENT_DATA_ID.LVARS,
                    reqId,
                    defId,
                    SIMCONNECT_CLIENT_DATA_PERIOD.NEVER,
                    SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0);
                sim.ClearClientDataDefinition(defId);
            }
        }).Replay(1).RefCount());
    
    private IObservable<object> HVar(string datumName) =>
        _hEvents.Where(e => string.Equals(e, datumName, StringComparison.OrdinalIgnoreCase)).Select(_ => DefaultHValue);

    // private IObservable<object> KEvent(string datumName, string sUnits) => 
    //     (IObservable<object>)_streams.GetOrAdd(datumName, key => Observable.Create<object>(observer =>
    //     {
    //         // var defId = (DEF)Interlocked.Increment(ref _defId);
    //         // var reqId = (REQ)Interlocked.Increment(ref _requestId);
    //         
    //         var eventId = _defs.GetOrAdd($"K:{datumName}", _ =>
    //         {
    //             var nextId = (DEF)Interlocked.Increment(ref _defId);
    //             Console.WriteLine("MapClientEventToSimEvent {0} {1}", (EVT)nextId, datumName);
    //             _headless.Configure(sim => sim.MapClientEventToSimEvent((EVT)nextId, datumName), _ => { });
    //             return nextId;
    //         });
    //
    //         var sub = _headless.Event.Subscribe(e =>
    //             {
    //                 Console.WriteLine("Invoked {0}", e.uGroupID);
    //                 if (e.uEventID != (uint)eventId) return;
    //                 observer.OnNext(e.dwData);
    //             },
    //             observer.OnError,
    //             observer.OnCompleted);
    //
    //         var config = _headless.Configure(Initialize, Deinitialize);
    //         Console.WriteLine("Registered {0} with id {1}", key, eventId);
    //
    //         return () =>
    //         {
    //             sub.Dispose();
    //             config.Dispose();
    //         };
    //
    //         void Initialize(SimConnect sim)
    //         {
    //             Debug.WriteLine($"Initialize {key}");
    // //         sim.SubscribeToSystemEvent((EVT)nextId, eventName);
    //             sim.AddClientEventToNotificationGroup(GRP.Dummy, eventId, false);
    //             // sim.SetInputGroupState(GRP.Dummy, (uint)SIMCONNECT_STATE.ON);
    //             // sim.SetNotificationGroupPriority(GRP.Dummy, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
    //         }
    //
    //         void Deinitialize(SimConnect sim)
    //         {
    //             Debug.WriteLine($"Deinitialize {key}");
    //             sim.ClearNotificationGroup(eventId);
    //         }
    //     }).Replay(1).RefCount());
    
    // public IObservable<Unit> Event(string eventName) => Observable.Create<Unit>(observer =>
    // {
    //     var eventId = _headless.Configure($"Event:{eventName}", (sim, nextId) =>
    //     {
    //         sim.SubscribeToSystemEvent((EVT)nextId, eventName);
    //         // sim.MapClientEventToSimEvent((EVT)nextId, eventName);
    //     });
    //     
    //     var sub = _headless.Event.Subscribe(e =>
    //         {
    //             if (e.uEventID != eventId) return;
    //             observer.OnNext(Unit.Default);
    //         },
    //         observer.OnError,
    //         observer.OnCompleted);
    //     
    //     return () => sub.Dispose();
    // });

    private enum DEF;
    private enum REQ;
    private enum EVT;
    private enum GRP { Dummy }
    
    // private enum MF_CLIENTDATA : uint { REQUEST = 1, RESPONSE = 2 }
    
    private enum CLIENT_DATA_ID
    {
        LVARS = 0,
        CMD = 1,
        ACK = 2,
        RESULT = 3
    }
    
    // // Structs must match the layout used by MobiFlight WASM
    // [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    // private struct MfRequest
    // {
    //     public uint RequestId;
    //     public uint Operation;          // 0=LIST, 1=GET, 2=SET, 3=EXEC
    //     public double Value;            // For SET or EXEC
    //     [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    //     public string Name;             // L:VAR, H:EVENT, or calc code
    // }
    //
    // [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    // private struct MfResponse
    // {
    //     public uint RequestId;
    //     public byte Success;
    //     public double Value;
    //     [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 96)]
    //     public string Info;
    // }
    
    // Structure sent back from WASM module to acknowledge for LVars
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct LVarAck
    {
        public UInt16 DefineID;
        public UInt16 Offset;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public String str;
        public float value;
    };

    // Structure to get the result of execute_calculator_code
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct Result
    {
        public double exeF;
        public Int32 exeI;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public String exeS;
    }
    
    private enum CLIENTDATA_DEFINITION_ID
    {
        CMD,
        ACK,
        RESULT
    }

    // Client Data Area RequestID's for receiving Acknowledge and LVARs
    private enum CLIENTDATA_REQUEST_ID
    {
        ACK,
        RESULT,
        START_LVAR
    }
    
    // Currently we only work with strings with a fixed size of 256
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct String256
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Value;
    }
}

