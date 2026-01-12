namespace FsCopilot.Connection;

using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;

public sealed class SimConnectProducer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _appName;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly Task _reconnectTask;

    private readonly Channel<Action<SimConnect>> _queue =
        Channel.CreateUnbounded<Action<SimConnect>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    private readonly List<Action<SimConnect>> _configure = [];
    private readonly Lock _cfgLock = new();

    public SimConnectProducer(string appName)
    {
        _appName = appName + " (Producer)";
        _reconnectTask = Task.Factory.StartNew(
            () => AutoReconnect(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _reconnectTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private async Task AutoReconnect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Connect(ct); }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception) { await Task.Delay(_reconnectDelay, ct); }
        }
    }

    private async Task Connect(CancellationToken ct)
    {
        using var evt = new AutoResetEvent(false);
        using var sim = new SimConnect(_appName, IntPtr.Zero, 0, evt, 0);
        evt.WaitOne();
        Log.Information("[SimConnect] Producer connected");
        
        lock (_cfgLock) foreach (var action in _configure)
        {
            try { action(sim); }
            catch (Exception e) { Log.Fatal(e, "[SimConnect] Producer initialization error"); }
        }
        
        while (!ct.IsCancellationRequested)
        {
            if (!await _queue.Reader.WaitToReadAsync(ct)) return;
        
            while (_queue.Reader.TryRead(out var job))
            {
                try { job(sim); }
                catch (System.Runtime.InteropServices.COMException) { Log.Information("[SimConnect] Producer disconnected"); return; }
                catch (Exception e) { Log.Fatal(e, "[SimConnect] Producer execution error"); }
            }
        }
    }
    
    public void Post(Action<SimConnect> action) => _queue.Writer.TryWrite(action);

    public IDisposable Configure(Action<SimConnect> configure, Action<SimConnect> deconfigure)
    {
        lock (_cfgLock) _configure.Add(configure);
        Post(configure);

        return Disposable.Create(() =>
        {
            lock (_cfgLock) _configure.Remove(configure);
            Post(deconfigure);
        });
    }
}