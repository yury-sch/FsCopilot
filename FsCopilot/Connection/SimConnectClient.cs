namespace FsCopilot.Connection;

using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using Microsoft.FlightSimulator.SimConnect;

public class SimConnectClient(string appName) : IDisposable
{
    private readonly SimConnectHeadless _headless = new(appName);
    private int _requestId;
    
    public IObservable<bool> Connected => _headless.Connected;

    public void Dispose()
    {
        _headless.Dispose();
    }

    public void AddDataDefinition<T>() where T : struct
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

    public void AddDataDefinition(string datumName, string sUnits)
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

    public void SetDataOnSimObject(string datumName, object value)
    {
        if (!_headless.TrGetDefineId(datumName, out var defId)) return;
        
        _headless.Post(sim => sim.SetDataOnSimObject((DEF)defId, 
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, value));
    }
    
    public IObservable<T> Stream<T>() where T : struct => Observable.Create<T>(observer =>
    {
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
    
    public IObservable<object> Stream(string datumName) => Observable.Create<object>(observer =>
    {
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
    
    public IObservable<Unit> Event(string eventName) => Observable.Create<Unit>(observer =>
    {
        var eventId = _headless.Configure($"Event:{eventName}", (sim, nextId) =>
        {
            sim.SubscribeToSystemEvent((EVT)nextId, eventName);
            // sim.MapClientEventToSimEvent((EVT)nextId, eventName);
        });
        
        var sub = _headless.Event.Subscribe(e =>
            {
                if (e.uEventID != eventId) return;
                observer.OnNext(Unit.Default);
            },
            observer.OnError,
            observer.OnCompleted);
        
        return () => sub.Dispose();
    });

    private enum DEF;
    private enum REQ;
    private enum EVT;
    private enum GRP { Dummy }
}

