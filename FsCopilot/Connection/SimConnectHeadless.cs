namespace FsCopilot.Connection;

using System.Diagnostics;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;
using Serilog;

public sealed class SimConnectHeadless : IDisposable
{
    private readonly Lock _gate = new();
    private readonly BehaviorSubject<bool> _connected = new(false);
    private readonly ReplaySubject<Unit> _opened = new(1);
    private readonly Subject<SIMCONNECT_RECV_SIMOBJECT_DATA> _simObjectData = new();
    private readonly Subject<SIMCONNECT_RECV_EVENT> _event = new();
    private readonly Subject<SIMCONNECT_RECV_SYSTEM_STATE> _systemState = new();
    private readonly Subject<SIMCONNECT_RECV_CLIENT_DATA> _clientData = new();
    private readonly List<Action<SimConnect>> _preConfigure = [];
    private readonly Lock _preCfgLock = new();
    private readonly string _appName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Action<SimConnect>> _queue = Channel.CreateUnbounded<Action<SimConnect>>();
    private readonly Task _loopTask;

    private SimConnect? _sim;
    private AutoResetEvent? _evt;

    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(2);

    public IObservable<bool> Connected => _connected;
    public IObservable<Unit> Loaded => _opened;
    public IObservable<SIMCONNECT_RECV_SIMOBJECT_DATA> SimObjectData => _simObjectData;
    public IObservable<SIMCONNECT_RECV_EVENT> Event => _event;
    public Subject<SIMCONNECT_RECV_SYSTEM_STATE> SystemState => _systemState;
    public IObservable<SIMCONNECT_RECV_CLIENT_DATA> ClientData => _clientData;

    public SimConnectHeadless(string appName)
    {
        _appName = appName;
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        const int MAX_MSG_PER_TICK  = 200;   // limitation on sim message processing per iteration
        const int MAX_CMDS_PER_TICK = 200;   // the limit on executing commands per iteration
        const int TICK_BUDGET_MS    = 8;     // maximum iteration time (approximately ~120 Hz)
        var sw = new Stopwatch();
        
        while (!ct.IsCancellationRequested)
        {
            var wasOpen = false;
            try
            {
                lock (_gate)
                {
                    _evt = new(false);
                    _sim = new(_appName, IntPtr.Zero, 0, _evt, 0);

                    _sim.OnRecvOpen += (_, _) =>
                    {
                        lock (_preCfgLock) foreach (var action in _preConfigure)
                            try { action(_sim); } catch (Exception e) { Log.Error(e, "An error occurred when receiving data from simconnect"); }
                        wasOpen = true;
                        SetConnected(true);
                        _opened.OnNext(Unit.Default);
                    };
                    _sim.OnRecvSimobjectData += (_, data) => _simObjectData.OnNext(data);
                    _sim.OnRecvEvent += (_, data) => _event.OnNext(data);
                    _sim.OnRecvSystemState += (_, state) => _systemState.OnNext(state);
                    _sim.OnRecvClientData += (_, data) => _clientData.OnNext(data);
                    _sim.OnRecvQuit += (_, _) => CleanupSim();
                    _sim.OnRecvException += (_, _) => { /* todo add logs */ };
                } 

                while (!ct.IsCancellationRequested && IsAlive())
                {
                    // wait for SimConnect signal
                    await _evt!.WaitOneAsync(TimeSpan.FromMilliseconds(2), ct);

                    sw.Restart();
                    
                    // commands
                    var ran = 0;
                    while (wasOpen && ran < MAX_CMDS_PER_TICK && sw.ElapsedMilliseconds < TICK_BUDGET_MS
                           && _queue.Reader.TryRead(out var job))
                    {
                        try { lock (_gate) job(_sim!); } catch(Exception ex) { Log.Error(ex, "An error occurred when executing simconnect command"); }
                        ran++;
                    }
                    
                    sw.Restart();
                    
                    // messages
                    var processed = 0;
                    while (processed < MAX_MSG_PER_TICK && sw.ElapsedMilliseconds < TICK_BUDGET_MS)
                    {
                        try
                        {
                            lock (_gate) _sim?.ReceiveMessage(); // ReceiveMessage not blocks, just take next
                            processed++;
                        }
                        catch
                        {
                            // link die — exit, reconnect
                            break;
                        }
                    }

                    // if there was nothing, it's an easy concession to the planner.
                    if (processed == 0 && ran == 0)
                        await Task.Delay(1, ct);
                }
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch { /* retry */ }
            finally
            {
                CleanupSim();
                if (wasOpen) SetConnected(false);
            }

            if (ct.IsCancellationRequested) continue;
            try { await Task.Delay(_retryDelay, ct).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private bool IsAlive() { lock (_gate) return _sim != null; }

    private void CleanupSim()
    {
        lock (_gate)
        {
            try { _sim?.Dispose(); } catch { /* ignore */ }
            _sim = null;
            try { _evt?.Set(); } catch { /* ignore */ }
            try { _evt?.Dispose(); } catch { /* ignore */ }
            _evt = null;
        }
    }

    private void SetConnected(bool v) { if (_connected.Value != v) _connected.OnNext(v); }
    
    public void Post(Action<SimConnect> action)
    {
        _queue.Writer.TryWrite(action);
    }
    
    public IDisposable Configure(
        Action<SimConnect> configure,
        Action<SimConnect> deconfigure)
    {
        lock (_preCfgLock) _preConfigure.Add(configure);
        Post(configure);

        return new ActionDisposable(() =>
        {
            lock (_preCfgLock) _preConfigure.Remove(configure);
            Post(deconfigure);
        });
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            // Make thread-safe and idempotent.
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        CleanupSim();
        _connected.OnCompleted();
        _opened.OnCompleted();
        _cts.Dispose();
    }
}

// public enum DEF;