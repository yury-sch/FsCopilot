namespace FsCopilot.Connection;

using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;

public sealed class SimConnectConsumer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reconnectTask;

    private readonly BehaviorSubject<bool> _connected = new(false);
    private readonly BehaviorSubject<string?> _aircraft = new(null);

    private readonly Subject<SIMCONNECT_RECV_SIMOBJECT_DATA> _simObjectData = new();
    private readonly Subject<SIMCONNECT_RECV_CLIENT_DATA> _simClientData = new();

    private readonly Channel<SIMCONNECT_RECV_SIMOBJECT_DATA> _simObjectDataCh =
        Channel.CreateUnbounded<SIMCONNECT_RECV_SIMOBJECT_DATA>(new()
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });

    private readonly Channel<SIMCONNECT_RECV_CLIENT_DATA> _simClientDataCh =
        Channel.CreateUnbounded<SIMCONNECT_RECV_CLIENT_DATA>(new()
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true
        });

    private readonly List<Action<SimConnect>> _configure = [];
    private readonly Lock _cfgLock = new();
    private readonly Channel<Action<SimConnect>> _cfgQueue =
        Channel.CreateUnbounded<Action<SimConnect>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

    private readonly Task _pumpObjTask;
    private readonly Task _pumpClientTask;

    private readonly string _appName;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);

    public IObservable<bool> Connected => _connected;
    public IObservable<string> Aircraft => _aircraft.Where(a => a != null).Select(a => a!).DistinctUntilChanged();
    public IObservable<SIMCONNECT_RECV_SIMOBJECT_DATA> SimObjectData => _simObjectData;
    public IObservable<SIMCONNECT_RECV_CLIENT_DATA> SimClientData => _simClientData;

    public SimConnectConsumer(string appName)
    {
        _appName = appName + " (Consumer)";

        _pumpObjTask = Task.Factory.StartNew(
            () => PumpAsync(_simObjectDataCh.Reader, _simObjectData, _cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _pumpClientTask = Task.Factory.StartNew(
            () => PumpAsync(_simClientDataCh.Reader, _simClientData, _cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _reconnectTask = Task.Factory.StartNew(
            () => AutoReconnect(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();

        _cfgQueue.Writer.TryComplete();
        _simObjectDataCh.Writer.TryComplete();
        _simClientDataCh.Writer.TryComplete();

        try { _reconnectTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { _pumpObjTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { _pumpClientTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }

        _connected.OnCompleted();
        _aircraft.OnCompleted();
        _simObjectData.OnCompleted();

        _cts.Dispose();
    }

    private async Task AutoReconnect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_reconnectDelay, ct);
                Connect(ct);
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception) { /* ignore */ }
        }
    }

    private void Connect(CancellationToken ct)
    {
        try
        {
            using var evt = new AutoResetEvent(false);
            using var sim = new SimConnect(_appName, IntPtr.Zero, 0, evt, 0);
            sim.OnRecvSimobjectData += (_, data) => _simObjectDataCh.Writer.TryWrite(data);
            sim.OnRecvClientData += (_, data) => _simClientDataCh.Writer.TryWrite(data);
            sim.OnRecvEventFilename += (s, data) =>
            {
                if (data.uEventID != (uint)EVT.AircraftLoaded) return;
                s.RequestSystemState(REQ.AircraftLoaded, "AircraftLoaded");
            };
            sim.OnRecvSystemState += (_, state) =>
            {
                if (state.dwRequestID != (uint)REQ.AircraftLoaded) return;
                PushAircraft(state.szString);
            };
            evt.WaitOne();

            sim.SubscribeToSystemEvent(EVT.AircraftLoaded, "AircraftLoaded");
            sim.RequestSystemState(REQ.AircraftLoaded, "AircraftLoaded");
            _connected.OnNext(true);
            Log.Information("[SimConnect] Consumer connected");

            lock (_cfgLock)
            {
                foreach (var action in _configure)
                {
                    try { action(sim); }
                    catch (Exception e) { Log.Error(e, "[SimConnect] Consumer initialization error"); }
                }

                while (_cfgQueue.Reader.TryRead(out _))
                {
                }
            }
        
            while (!ct.IsCancellationRequested)
            {
                while (_cfgQueue.Reader.TryRead(out var job))
                {
                    try { job(sim); }
                    catch (System.Runtime.InteropServices.COMException) { return; }
                    catch (Exception e) { Log.Fatal(e, "[SimConnect] Consumer execution error"); }
                }

                if (!evt.WaitOne(10)) continue;
            
                try { sim.ReceiveMessage(); }
                catch (System.Runtime.InteropServices.COMException) { return; }
                catch (Exception e) { Log.Fatal(e, "[SimConnect] Consumer processing error"); }
            }
        }
        finally
        {
            if (_connected.Value)
            {
                Log.Information("[SimConnect] Consumer disconnected");
                _connected.OnNext(false);
            }
        }
    }

    private void PushAircraft(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var match = Regex.Match(path, @"SimObjects\\Airplanes\\([^\\]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var name = match.Success ? match.Groups[1].Value : null;
        if (name == null) return;

        if (!name.Equals(_aircraft.Value))
        {
            Log.Information("[SimConnect] Loaded aircraft: {Aircraft}", name);
            _aircraft.OnNext(name);
        }
    }

    private static async Task PumpAsync<T>(ChannelReader<T> reader, IObserver<T> sink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct)) continue;
                while (reader.TryRead(out var msg)) sink.OnNext(msg);
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception e) { Log.Error(e, "[SimConnect] Consumer sinking error"); }
        }
    }
    
    public IDisposable Configure(Action<SimConnect> configure, Action<SimConnect> deconfigure)
    {
        lock (_cfgLock) _configure.Add(configure);
        _cfgQueue.Writer.TryWrite(configure);

        return Disposable.Create(() =>
        {
            lock (_cfgLock) _configure.Remove(configure);
            _cfgQueue.Writer.TryWrite(deconfigure);
        });
    }

    private enum EVT : uint { AircraftLoaded = uint.MaxValue - 1 }
    private enum REQ { AircraftLoaded }
}
