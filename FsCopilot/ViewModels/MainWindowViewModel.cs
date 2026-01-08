namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isConnectionFailed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isVersionMismatch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isNotSupported;
    [ObservableProperty] private bool _showTakeControl;
    // [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private IPEndPoint? _address;
    [ObservableProperty] private string _version = App.Version;

    public string ErrorMessage =>
        // Address == null ? "No internet connection. Please check your network." :
        IsConnectionFailed ? "Connection failed." :
        IsVersionMismatch ? "Connection failed due to version mismatch." :
        !IsSimConnected ? "Microsoft Flight Simulator is not running!" :
        IsNotSupported ? $"{Aircraft} is not supported." :
        string.Empty;

    // public ObservableCollection<string> Configurations { get; set; } = [];
    public ObservableCollection<Connection> Connections { get; set; } = [];
    
    // [ObservableProperty] private string? _selectedConfiguration;

    private readonly INetwork _net;
    private readonly MasterSwitch _masterSwitch;
    private readonly Subject<bool> _unsubscribe = new();

    private bool CanJoin() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanJoin))]
    private async Task Join()
    {
        if (ConnectionCode.Length != 8) return;
        IsConnectionFailed = false;
        IsVersionMismatch = false;
        IsBusy = true;
        ConnectionResult result;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            _masterSwitch.Join();
            result = await _net.Connect(ConnectionCode, cts.Token);
            ConnectionCode = string.Empty;
        }
        finally
        {
            IsBusy = false;
            JoinCommand.NotifyCanExecuteChanged(); // re-enable button
        }
        
        if (result == ConnectionResult.Failed)
        {
            IsConnectionFailed = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => IsConnectionFailed = false);
        }
        else if (result == ConnectionResult.Rejected)
        {
            IsVersionMismatch = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => IsVersionMismatch = false);
        }
    }

    [RelayCommand]
    private void Left()
    {
        _net.Disconnect();
        _masterSwitch.TakeControl();
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
        INetwork net, 
        SimClient sim, 
        MasterSwitch masterSwitch, 
        Coordinator coordinator)
    {
        _net = net;
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
            
        net.Peers
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
                        Ping = peer.Ping, 
                        IsDirect = peer.Transport == Peer.TransportKind.Direct,
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
        [ObservableProperty]
        private string _peerId = string.Empty;
        
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private int _ping;

        [ObservableProperty]
        private bool _isDirect;

        [ObservableProperty]
        private string _statusText = string.Empty;

        public bool HasSeparatorAfter { get; set; }
        
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