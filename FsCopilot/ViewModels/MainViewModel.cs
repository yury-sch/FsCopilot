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
        _errors.HasFlag(Errors.Timeout) ? "Connection attempt timed out." :
        _errors.HasFlag(Errors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(Errors.NotSupported) ? $"{_aircraft} is not supported." :
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
        IPeer2Peer peer2Peer, 
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
        
        peer2Peer.Peers
            .Sample(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(peers =>  
            { 
                var i = 0;
                Connections.Clear();
                foreach (var peer in peers) Connections.Add(new(
                    PeerId: peer.PeerId,
                    Name: string.IsNullOrWhiteSpace(peer.Name) ? "Unknown" : peer.Name,
                    Rtt: peer.Rtt,
                    Loss: peer.Loss,
                    Status: peer.Status,
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

            this.RaiseAndSetIfChanged(ref _errors, _errors & ~Errors.Timeout, nameof(ErrorMessage));
            this.RaiseAndSetIfChanged(ref _isBusy, true);
            bool result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                masterSwitch.Join();
                result = await peer2Peer.Connect(ConnectionCode, cts.Token);
            }
            finally
            {
                this.RaiseAndSetIfChanged(ref _isBusy, false);
            }
        
            if (!result)
            {
                this.RaiseAndSetIfChanged(ref _errors, _errors | ~Errors.Timeout, nameof(ErrorMessage));
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => 
                    this.RaiseAndSetIfChanged(ref _errors, _errors & ~Errors.Timeout, nameof(ErrorMessage)));
            }
        });

        LeftCommand = ReactiveCommand.Create(() =>
        {
            masterSwitch.TakeControl();
            peer2Peer.Disconnect();
        });

        TakeControlCommand = ReactiveCommand.Create(masterSwitch.TakeControl);
    }

    public void Dispose() => _d.Dispose();
    
    [Flags]
    private enum Errors : byte
    {
        None         = 0b_0000_0000,
        Timeout      = 0b_0000_0001,
        NotRunning   = 0b_0000_0010,
        NotSupported = 0b_0000_0100,
    }

    public record Connection(string PeerId, string Name, int Rtt, double Loss, Peer.State Status, bool HasSeparatorAfter)
    {
        private string StatusText => Status switch
        {
            Peer.State.Pending => "Pending connection.",
            Peer.State.Rejected => "Connection failed due to version mismatch.",
            Peer.State.Failed => "Connection failed.",
            _ => string.Empty
        };
        
        public int QualityLevel
        {
            get
            {
                if (Status != Peer.State.Success) return 0;
                if (Loss > 10 || Rtt > 800) return 5;
                if (Loss > 5 || Rtt > 400) return 4;
                if (Loss > 2 || Rtt > 200) return 3;
                if (Loss > 0.5 || Rtt > 100) return 2;
                return 1;
            }
        }
    }
}

public class DesignMainViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _d = new();
    
    private string _aircraft = string.Empty;
    private string _connectionCode = string.Empty;
    private bool _isBusy;
    private bool _connected;
    private bool _showTakeControl;
    private Errors _errors = Errors.None;
    
    public bool IsBusy => _isBusy;
    // public bool Connected => _connected;
    public bool ShowTakeControl => _showTakeControl;
    public string Version => App.Version;
    public string ErrorMessage =>
        _errors.HasFlag(Errors.Timeout) ? "Connection attempt timed out." :
        _errors.HasFlag(Errors.NotRunning) ? "Microsoft Flight Simulator is not running!" :
        _errors.HasFlag(Errors.NotSupported) ? $"{_aircraft} is not supported." :
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

    public DesignMainViewModel()
    {
        ClientName = "Yury Scherbakov";
        PeerId = "HDTRKDUI";
        
        
        
        JoinCommand = ReactiveCommand.CreateFromTask(async () =>
        {
           
        });

        LeftCommand = ReactiveCommand.Create(() =>
        {
           
        });

        TakeControlCommand = ReactiveCommand.Create(() =>
        {
            
        });
    }

    public void Dispose() => _d.Dispose();
    
    [Flags]
    private enum Errors : byte
    {
        None         = 0b_0000_0000,
        Timeout      = 0b_0000_0001,
        NotRunning   = 0b_0000_0010,
        NotSupported = 0b_0000_0100,
    }

    public record Connection(string PeerId, string Name, int Rtt, double Loss, Peer.State Status, bool HasSeparatorAfter)
    {
        private string StatusText => Status switch
        {
            Peer.State.Pending => "Pending connection.",
            Peer.State.Rejected => "Connection failed due to version mismatch.",
            Peer.State.Failed => "Connection failed.",
            _ => string.Empty
        };
        
        public int QualityLevel
        {
            get
            {
                if (Status != Peer.State.Success) return 0;
                if (Loss > 10 || Rtt > 800) return 5;
                if (Loss > 5 || Rtt > 400) return 4;
                if (Loss > 2 || Rtt > 200) return 3;
                if (Loss > 0.5 || Rtt > 100) return 2;
                return 1;
            }
        }
    }
}
