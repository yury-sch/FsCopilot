namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Threading;
using Connection;
using Network;
using Simulation;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private string _clientName;
    [ObservableProperty] private string _peerId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private string _aircraft;
    [ObservableProperty] private string _connectionCode = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _connected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isSimConnected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isConnectionTimeout;
    // [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isVersionMismatch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isNotSupported;
    [ObservableProperty] private bool _showTakeControl;
    // [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private IPEndPoint? _address;
    [ObservableProperty] private string _version = App.Version;

    public string ErrorMessage =>
        // Address == null ? "No internet connection. Please check your network." :
        IsConnectionTimeout ? "Connection attempt timed out." :
        // IsVersionMismatch ? "Connection failed due to version mismatch." :
        !IsSimConnected ? "Microsoft Flight Simulator is not running!" :
        IsNotSupported ? $"{Aircraft} is not supported." :
        string.Empty;

    // public ObservableCollection<string> Configurations { get; set; } = [];
    public ObservableCollection<Connection> Connections { get; set; } = [];
    
    // [ObservableProperty] private string? _selectedConfiguration;

    private readonly IPeer2Peer _peer2Peer;
    private readonly MasterSwitch _masterSwitch;
    private readonly Subject<bool> _unsubscribe = new();

    private bool CanJoin() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task Join()
    {
        IsConnectionTimeout = false;
        // IsVersionMismatch = false;
        IsBusy = true;
        bool result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            _masterSwitch.Join();
            result = await _peer2Peer.Connect(ConnectionCode, cts.Token);
        }
        finally
        {
            IsBusy = false;
            JoinCommand.NotifyCanExecuteChanged(); // re-enable button
        }
        
        if (!result)
        {
            IsConnectionTimeout = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => IsConnectionTimeout = false);
        }
    }

    [RelayCommand]
    private void Left()
    {
        _masterSwitch.TakeControl();
        _peer2Peer.Disconnect();
    }

    [RelayCommand]
    private void TakeControl() => _masterSwitch.TakeControl();
    
    // partial void OnSelectedConfigurationChanged(string? value)
    // {
    //     if (string.IsNullOrWhiteSpace(value)) return;
    //     try
    //     {
    //         var config = _configuration.Load(value);
    //         _coordinator.Load(config);
    //     }
    //     catch (Exception e)
    //     {
    //         Debug.WriteLine(e);
    //     }
    // }

    // private void NumerateConnections(object? sender, NotifyCollectionChangedEventArgs e)
    // {
    //     for (var i = 0; i < Connections.Count; i++) Connections[i].HasSeparatorAfter = i < Connections.Count - 1;
    // }

    public MainWindowViewModel(string peerId,
        string name,
        IPeer2Peer peer2Peer, 
        SimClient sim, 
        MasterSwitch masterSwitch, 
        Coordinator coordinator)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        // _configuration = configuration;

        ClientName = name;
        PeerId = peerId;
        
        sim.Aircraft
            // .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(aircraft => Dispatcher.UIThread.Post(() =>
            {
                Aircraft = aircraft;
            }));
        
        coordinator.Configured
            .TakeUntil(_unsubscribe)
            .Subscribe(configured => Dispatcher.UIThread.Post(() => IsNotSupported = configured == false));
        
        sim.Connected
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            // .ObserveOn(AvaloniaScheduler.Instance)
            // .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x => Dispatcher.UIThread.Post(() => IsSimConnected = x));

        // peer2Peer.DiscoveredEndpoint
        //     .Sample(TimeSpan.FromMilliseconds(250))
        //     .TakeUntil(_unsubscribe)
        //     .Subscribe(x => Dispatcher.UIThread.Post(() => Address = x));
            
        peer2Peer.Peers
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(peers => Dispatcher.UIThread.Post(() => 
            { 
                var i = 0;
                Connections.Clear();
                foreach (var peer in peers)
                    Connections.Add(new()
                    {
                        PeerId = peer.PeerId,
                        Name = string.IsNullOrWhiteSpace(peer.Name) ? "Unknown" : peer.Name, 
                        Rtt = peer.Rtt, 
                        Loss = peer.Loss, 
                        Status = peer.Status,
                        HasSeparatorAfter = i++ < peers.Count - 1
                    });
                Connected = Connections.Any();
            }));
        
        masterSwitch.Master
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Select(isMaster => !isMaster)
            // .CombineLatest(connections, (isSlave, items) => isSlave && items.Count != 0)
            .Subscribe(isSlave => Dispatcher.UIThread.Post(() => ShowTakeControl = isSlave));
        
        // configuration.Files
        //     .Sample(TimeSpan.FromMilliseconds(250))
        //     .TakeUntil(_unsubscribe)
        //     .Subscribe(files => Dispatcher.UIThread.Post(() => 
        //     { 
        //         Configurations.Clear();
        //         foreach (var file in files) Configurations.Add(file); 
        //     }));
    }

    public void Dispose()
    {
        _unsubscribe.OnNext(true);
        _unsubscribe.Dispose();
        // Connections.CollectionChanged -= NumerateConnections;
    }

    public partial class Connection
        : ObservableObject
    {
        private Peer.State _status;
        
        [ObservableProperty]
        private string _peerId = string.Empty;
        
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private int _rtt;

        [ObservableProperty]
        private double _loss;

        [ObservableProperty]
        private string _statusText = string.Empty;

        public Peer.State Status
        {
            get => _status;
            set
            {
                _status = value;
                StatusText = value switch
                {
                    Peer.State.Pending => "Pending connection.",
                    Peer.State.Rejected => "Connection failed due to version mismatch.",
                    Peer.State.Failed => "Connection failed.",
                    _ => string.Empty
                };
            }
        }

        public bool HasSeparatorAfter { get; set; }
        
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