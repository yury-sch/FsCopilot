namespace FsCopilot.Connection;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

public class SimConnectClient : IDisposable
{
    private readonly SimConnectHeadless _headless;
    private readonly ConcurrentDictionary<string, ushort> _lVars = new();
    private readonly IDisposable _wasmAckSub;
    private uint _requestId;
    
    public IObservable<bool> Connected => _headless.Connected;

    public SimConnectClient(string appName)
    {
        var reqId = Interlocked.Increment(ref _requestId);
        _headless = new(appName);
        // _headless.SystemState.Subscribe(data =>
        // {
        //     if (data.dwRequestID != reqId) return;
        //     var match = Regex.Match(data.szString, @"SimObjects\\Airplanes\\([^\\]+)");
        //     if (match.Success)
        //     {
        //         string folder = match.Groups[1].Value;
        //         Console.WriteLine(folder);
        //     }
        //     Console.WriteLine($"aircraft.cfg path (raw): {path}");
        // });
        // _headless.Post(sim => sim.RequestSystemState((REQ)reqId, "AircraftLoaded"));
        
        _headless.Configure("HABI", (sim, nextId) =>
        {
            const int MESSAGE_SIZE = 256;
            
            // register Client Data (for LVars)
            sim.MapClientDataNameToID("HABI_WASM.LVars", CLIENT_DATA_ID.LVARS);
            sim.CreateClientData(CLIENT_DATA_ID.LVARS, SimConnect.SIMCONNECT_CLIENTDATA_MAX_SIZE, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);

            // register Client Data (for WASM Module Commands)
            sim.MapClientDataNameToID("HABI_WASM.Command", CLIENT_DATA_ID.CMD);
            sim.CreateClientData(CLIENT_DATA_ID.CMD, MESSAGE_SIZE, SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
            sim.AddToClientDataDefinition(CLIENTDATA_DEFINITION_ID.CMD, 0, MESSAGE_SIZE, 0, 0);

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
        });
        // _headless.Configure("MobiFlight.Request", (sim, nextId) =>
        // {
        //     sim.MapClientDataNameToID("MobiFlight.Request",  MF_CLIENTDATA.REQUEST);
        //     sim.AddToClientDataDefinition((REQ)nextId,  0, (uint)Marshal.SizeOf<MfRequest>(), 0, 0);
        //     sim.CreateClientData(MF_CLIENTDATA.REQUEST,  (uint)Marshal.SizeOf<MfRequest>(), SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
        // });
        // _headless.Configure("MobiFlight.Response", (sim, nextId) =>
        // {
        //     sim.MapClientDataNameToID("MobiFlight.Response", MF_CLIENTDATA.RESPONSE);
        //     sim.AddToClientDataDefinition((DEF)nextId, 0, (uint)Marshal.SizeOf<MfResponse>(), 0, 0);
        //     sim.CreateClientData(MF_CLIENTDATA.RESPONSE, (uint)Marshal.SizeOf<MfResponse>(), SIMCONNECT_CREATE_CLIENT_DATA_FLAG.DEFAULT);
        //     var reqId = (REQ)Interlocked.Increment(ref _requestId);
        //     sim.RequestClientData(
        //         MF_CLIENTDATA.RESPONSE,
        //         reqId,
        //         (DEF)nextId,
        //         SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
        //         SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT, 
        //         0, 0, 0);
        // });
        _wasmAckSub = _headless.ClientData.Subscribe(data =>
        {
            if (data.dwRequestID == (uint)CLIENTDATA_REQUEST_ID.ACK)
            {
                var ackData = (LVarAck)data.dwData[0];
                var defId = ackData.DefineID;
                var key = ackData.str.Substring(3, ackData.str.Length - 4);
                _lVars.AddOrUpdate(key, _ =>
                {
                    _headless.Post(sim =>
                    {
                        sim.AddToClientDataDefinition((DEF)ackData.DefineID, ackData.Offset, sizeof(float), 0, 0);
                        sim.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, float>((DEF)ackData.DefineID);
                        var reqId = (REQ)Interlocked.Increment(ref _requestId);
                        sim.RequestClientData(
                            CLIENT_DATA_ID.LVARS,
                            reqId,
                            (DEF)ackData.DefineID,
                            SIMCONNECT_CLIENT_DATA_PERIOD
                                .ON_SET, // data will be sent whenever SetClientData is used on this client area (even if this defineID doesn't change)
                            SIMCONNECT_CLIENT_DATA_REQUEST_FLAG
                                .CHANGED, // if this is used, this defineID only is sent when its value has changed
                            0, 0, 0);
                    });
                    return defId;
                }, (_, _) => defId);
            }
        });
    }

    public void Dispose()
    {
        _wasmAckSub.Dispose();
        _headless.Dispose();
    }

    private void AddDataDefinition<T>() where T : struct
    {
        _headless.Configure(typeof(T).FullName!, (sim, nextId) =>
        {
            var reqId = (REQ)Interlocked.Increment(ref _requestId);
            sim.AddToDataDefinitionFromStruct<T>((DEF)nextId);
            sim.RegisterDataDefineStruct<T>((DEF)nextId);
            sim.RequestDataOnSimObject(
                reqId, (DEF)nextId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        });
    }

    private void AddDataDefinition(string datumName, string sUnits)
    {
        _headless.Configure(datumName, (sim, nextId) =>
        {
            var reqId = (REQ)Interlocked.Increment(ref _requestId);
            var datumType = SimConnectExtensions.InferDataType(sUnits);
            var clrType = SimConnectExtensions.ToClrType(datumType);
            
            sim.AddToDataDefinition((DEF)nextId, datumName, sUnits, datumType, 0.0f, SimConnect.SIMCONNECT_UNUSED);
            // sim.RegisterDataDefineStruct<T>((DEF)nextId);

            typeof(SimConnect).GetMethod(nameof(SimConnect.RegisterDataDefineStruct),
                BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(clrType)
                .Invoke(sim, [(DEF)nextId]);
            
            sim.RequestDataOnSimObject(
                reqId, (DEF)nextId, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        });
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

    public void TransmitClientEvent(string eventName, object? value, int idx)
    {
        var eventId = _headless.Configure($"Event:{eventName}", (sim, nextId) =>
        {
            sim.MapClientEventToSimEvent((EVT)nextId, eventName);
        });

        uint dwData = value switch
        {
            bool b => b ? 1u : 0u,
            byte by => by,
            sbyte sb => (uint)(byte)sb,
            short s => (uint)(ushort)s,
            ushort us => us,
            int i => unchecked((uint)i),
            uint ui => ui,
            long l => unchecked((uint)l),
            ulong ul => unchecked((uint)ul),
            float f => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0),
            double d => BitConverter.ToUInt32(BitConverter.GetBytes((float)d), 0), // we're losing accuracy, but 1:1 with the SDK
            Enum e => Convert.ToUInt32(e),
            _ => 0
        };

        _headless.Post(sim => sim.TransmitClientEvent_EX1(
            SimConnect.SIMCONNECT_OBJECT_ID_USER, (EVT)eventId, GRP.Dummy,
            SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY,
            idx == 0 ? dwData : 0,
            idx == 1 ? dwData : 0,
            idx == 2 ? dwData : 0,
            idx == 3 ? dwData : 0,
            idx == 4 ? dwData : 0));
    }

    public void SetDataOnSimObject<T>(T def) where T : struct
    {
        if (!_headless.TrGetDefineId(typeof(T).FullName!, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject((DEF)defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, def));
    }

    public void Set(string datumName, object value)
    {
        if (datumName.StartsWith("L:"))
        {
            _headless.Post(sim => sim.SetClientData(
                CLIENT_DATA_ID.CMD,
                CLIENTDATA_DEFINITION_ID.CMD,
                SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
                0,
                new String256 { Value = $"HW.Set.{value} (>L:{datumName[2..]})" }
            ));
        }
        else
        {
            SetDataOnSimObject(datumName, value);
        }
              
    }

    private void SetDataOnSimObject(string datumName, object value)
    {
        if (!_headless.TrGetDefineId(datumName, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject((DEF)defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }
    
    public IObservable<T> Stream<T>() where T : struct => Observable.Create<T>(observer =>
    {
        AddDataDefinition<T>();
        var sub = _headless.SimObjectData.Subscribe(e =>
            {
                if (!_headless.TrGetDefineId(typeof(T).FullName!, out var defId)) return;
                if (e.dwDefineID != defId) return;
                if (e.dwData is { Length: > 0 }) observer.OnNext((T)e.dwData[0]);
            },
            observer.OnError,
            observer.OnCompleted);
        
        return () => sub.Dispose();
    });

    public IObservable<object> Stream(string datumName, string sUnits) => 
        datumName.StartsWith("L:") ? LVar(datumName[2..]) : SimVar(datumName, sUnits);

    private IObservable<object> SimVar(string datumName, string sUnits) => Observable.Create<object>(observer =>
    {
        AddDataDefinition(datumName, sUnits);
        var sub = _headless.SimObjectData.Subscribe(e =>
            {
                if (!_headless.TrGetDefineId(datumName, out var defId)) return;
                if (e.dwDefineID != defId) return;
                if (e.dwData is { Length: > 0 }) observer.OnNext(e.dwData[0]);
            },
            observer.OnError,
            observer.OnCompleted);
        
        return () => sub.Dispose();
    });

    private IObservable<object> LVar(string name) => Observable.Create<object>(observer =>
    {
        var sub = _headless.ClientData.Subscribe(data =>
            {
                if (data.dwRequestID < (uint)CLIENTDATA_REQUEST_ID.START_LVAR) return;
                if (!_lVars.TryGetValue(name, out var defId) || defId != data.dwDefineID) return;
                observer.OnNext(data.dwData[0]);
            },
            observer.OnError,
            observer.OnCompleted);

        _headless.Configure(name, (sim, _) => sim.SetClientData(
            CLIENT_DATA_ID.CMD,
            CLIENTDATA_DEFINITION_ID.CMD,
            SIMCONNECT_CLIENT_DATA_SET_FLAG.DEFAULT,
            0,
            new String256 { Value = $"HW.Reg.(L:{name})" }
        ));
        
        return () => sub.Dispose();
    });
    
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
    
    // Structs must match the layout used by MobiFlight WASM
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct MfRequest
    {
        public uint RequestId;
        public uint Operation;          // 0=LIST, 1=GET, 2=SET, 3=EXEC
        public double Value;            // For SET or EXEC
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;             // L:VAR, H:EVENT, or calc code
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct MfResponse
    {
        public uint RequestId;
        public byte Success;
        public double Value;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 96)]
        public string Info;
    }
    
    // Structure sent back from WASM module to acknowledge for LVars
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct LVarAck
    {
        public UInt16 DefineID;
        public UInt16 Offset;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public String str;
        public float value;
    };

    // Structure to get the result of execute_calculator_code
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct Result
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
    public struct String256
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Value;
    }
}

