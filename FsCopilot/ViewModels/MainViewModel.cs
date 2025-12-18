namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Connection;
using Network;
using ReactiveUI;
using Simulation;

public class MainViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _d = new();
    
    private string _aircraft = string.Empty;
    private string _connectionCode = string.Empty;
    private bool _isBusy;
    private bool _connected;
    private bool _showTakeControl;
    private Errors _errors = Errors.None;
    
    public bool IsBusy => _isBusy;
    public bool Connected => _connected;
    public bool ShowTakeControl => _showTakeControl;
    public string Version => App.Version;
    public string ErrorMessage =>
        _errors.HasFlag(Errors.Failed) ? "Connection attempt timed out." :
        _errors.HasFlag(Errors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(Errors.NotSupported) ? $"{_aircraft} is not supported." :
        _errors.HasFlag(Errors.Rejected) ? "Client version mismatch." :
        string.Empty;

    public string ClientName { get; init; }
    public string PeerId { get; init; }
    public string ConnectionCode
    {
        get => _connectionCode;
        set => this.RaiseAndSetIfChanged(ref _connectionCode, value);
    }

    public ObservableCollection<Connection> Connections { get; set; } = [];
    public ReactiveCommand<Unit, Unit> JoinCommand { get; }
    public ReactiveCommand<Unit, Unit> LeftCommand { get; }
    public ReactiveCommand<Unit, Unit> TakeControlCommand { get; }

    public MainViewModel(string peerId,
        string name,
        INetwork net, 
        SimClient sim, 
        MasterSwitch masterSwitch, 
        Coordinator coordinator)
    {
        ClientName = name;
        PeerId = peerId;
        
        sim.Aircraft
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(aircraft => this.RaiseAndSetIfChanged(ref _aircraft, aircraft, nameof(ErrorMessage)))
            .DisposeWith(_d);
        
        coordinator.Configured
            .Select(configured => configured == false)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => this.RaiseAndSetIfChanged(ref _errors, x 
                ? _errors | Errors.NotSupported 
                : _errors & ~Errors.NotSupported, nameof(ErrorMessage)))
            .DisposeWith(_d);
        
        sim.Connected
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => this.RaiseAndSetIfChanged(ref _errors, x 
                ? _errors |= Errors.NotRunning 
                : _errors &= ~Errors.NotRunning, nameof(ErrorMessage)))
            .DisposeWith(_d);
        
        net.Peers
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(peers =>  
            { 
                var i = 0;
                Connections.Clear();
                foreach (var peer in peers) Connections.Add(new(
                    PeerId: peer.PeerId,
                    Name: string.IsNullOrWhiteSpace(peer.Name) ? "Unknown" : peer.Name,
                    Ping: peer.Ping,
                    IsDirect: peer.Transport == Peer.TransportKind.Direct,
                    HasSeparatorAfter: i++ < peers.Count - 1
                ));
                this.RaiseAndSetIfChanged(ref _connected, Connections.Any());
            })
            .DisposeWith(_d);
        
        masterSwitch.Master
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(isMaster => !isMaster)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isSlave => this.RaiseAndSetIfChanged(ref _showTakeControl, isSlave))
            .DisposeWith(_d);
        
        JoinCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;
            if (ConnectionCode.Length != 8) return;

            this.RaiseAndSetIfChanged(ref _errors, _errors & ~Errors.Failed, nameof(ErrorMessage));
            this.RaiseAndSetIfChanged(ref _isBusy, true);
            ConnectionResult result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                masterSwitch.Join();
                result = await net.Connect(ConnectionCode, cts.Token);
            }
            finally
            {
                this.RaiseAndSetIfChanged(ref _isBusy, false);
            }
        
            if (result == ConnectionResult.Failed)
            {
                this.RaiseAndSetIfChanged(ref _errors, _errors | ~Errors.Failed, nameof(ErrorMessage));
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => 
                    this.RaiseAndSetIfChanged(ref _errors, _errors & ~Errors.Failed, nameof(ErrorMessage)));
            }
            else if (result == ConnectionResult.Rejected)
            {
                this.RaiseAndSetIfChanged(ref _errors, _errors | ~Errors.Rejected, nameof(ErrorMessage));
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => 
                    this.RaiseAndSetIfChanged(ref _errors, _errors & ~Errors.Rejected, nameof(ErrorMessage)));
            }
        });

        LeftCommand = ReactiveCommand.Create(() =>
        {
            net.Disconnect();
            masterSwitch.TakeControl();
        });

        TakeControlCommand = ReactiveCommand.Create(masterSwitch.TakeControl);
    }

    public void Dispose() => _d.Dispose();
    
    [Flags]
    private enum Errors : byte
    {
        None         = 0b_0000_0000,
        Failed       = 0b_0000_0001,
        NotRunning   = 0b_0000_0010,
        NotSupported = 0b_0000_0100,
        Rejected     = 0b_0000_1000
    }

    public record Connection(string PeerId, string Name, int Ping, bool IsDirect, bool HasSeparatorAfter)
    {
        public int QualityLevel
        {
            get
            {
                if (Ping > 800) return 5;
                if (Ping > 400) return 4;
                if (Ping > 200) return 3;
                if (Ping > 100) return 2;
                return 1;
            }
        }
    }
}
