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
    private ViewErrors _errors = ViewErrors.None;
    
    private string Aircraft
    {
        set
        {
            _aircraft = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
        }
    }
    
    private ViewErrors Errors
    {
        get => _errors;
        set
        {
            _errors = value;
            this.RaisePropertyChanged(nameof(ErrorMessage));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool Connected
    {
        get => _connected;
        set => this.RaiseAndSetIfChanged(ref _connected, value);
    }

    public bool ShowTakeControl
    {
        get => _showTakeControl;
        set => this.RaiseAndSetIfChanged(ref _showTakeControl, value);
    }
    
    public string ConnectionCode
    {
        get => _connectionCode;
        set => this.RaiseAndSetIfChanged(ref _connectionCode, value);
    }

    public string PeerId { get; init; }
    public string ClientName { get; init; }
    
    public string Version => App.Version;
    public string ErrorMessage =>
        _errors.HasFlag(ViewErrors.Failed) ? "Failed to connect." :
        _errors.HasFlag(ViewErrors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(ViewErrors.NotSupported) ? $"{_aircraft} is not supported." :
        _errors.HasFlag(ViewErrors.Rejected) ? "Peer version mismatch." :
        string.Empty;

    public ObservableCollection<Connection> Connections { get; set; } = [];
    public ReactiveCommand<Unit, Unit> JoinCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveCommand { get; }
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
            .Subscribe(aircraft => Aircraft = aircraft)
            .DisposeWith(_d);
        
        coordinator.Configured
            .Select(configured => configured == false)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notSupported => Errors = notSupported
                ? _errors | ViewErrors.NotSupported
                : _errors & ~ViewErrors.NotSupported)
            .DisposeWith(_d);
        
        sim.Connected
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(connected => !connected)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(notRunning => Errors = notRunning
                ? _errors | ViewErrors.NotRunning
                : _errors & ~ViewErrors.NotRunning)
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
                Connected = Connections.Any();
            })
            .DisposeWith(_d);
        
        masterSwitch.Master
            .Sample(TimeSpan.FromMilliseconds(250))
            .Select(isMaster => !isMaster)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isSlave => ShowTakeControl = isSlave)
            .DisposeWith(_d);
        
        JoinCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;
            if (ConnectionCode.Length != 8) return;

            Errors &= ~ViewErrors.Failed;
            IsBusy = true;
            ConnectionResult result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

                masterSwitch.Join();
                result = await net.Connect(ConnectionCode, cts.Token);
            }
            finally
            {
                IsBusy = false;;
            }

            if (result == ConnectionResult.Success)
            {
                ConnectionCode = string.Empty;
            }
            else if (result == ConnectionResult.Failed)
            {
                Errors |= ViewErrors.Failed;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Failed);
            }
            else if (result == ConnectionResult.Rejected)
            {
                Errors |= ViewErrors.Rejected;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Errors &= ~ViewErrors.Rejected);
            }
        });

        LeaveCommand = ReactiveCommand.Create(() =>
        {
            net.Disconnect();
            masterSwitch.TakeControl();
        });

        TakeControlCommand = ReactiveCommand.Create(masterSwitch.TakeControl);
    }

    public void Dispose() => _d.Dispose();
    
    [Flags]
    private enum ViewErrors : byte
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
