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
    [ObservableProperty] private string _sessionCode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private string _aircraft;
    [ObservableProperty] private string _connectionCode = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isSimConnected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isConnectionTimeout;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isVersionMismatch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isNotSupported;
    [ObservableProperty] private bool _showTakeControl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private IPEndPoint? _address;
    [ObservableProperty] private string _version = App.Version;

    public string ErrorMessage =>
        Address == null ? "No internet connection. Please check your network." :
        IsConnectionTimeout ? "Connection attempt timed out." :
        IsVersionMismatch ? "Connection failed due to version mismatch." :
        !IsSimConnected ? "Microsoft Flight Simulator is not running!" :
        IsNotSupported ? $"{Aircraft} is not supported." :
        string.Empty;

    // public ObservableCollection<string> Configurations { get; set; } = [];
    public ObservableCollection<Connection> Connections { get; set; } = [];
    
    // [ObservableProperty] private string? _selectedConfiguration;

    private readonly Peer2Peer _peer2Peer;
    private readonly MasterSwitch _masterSwitch;
    private readonly Subject<bool> _unsubscribe = new();

    private bool CanConnect() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        IsConnectionTimeout = false;
        IsVersionMismatch = false;
        IsBusy = true;
        ConnectionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            await _masterSwitch.Join();
            result = await _peer2Peer.Connect(ConnectionCode, cts.Token);
            ConnectionCode = string.Empty;
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged(); // re-enable button
        }
        
        if (result == ConnectionResult.Cancelled)
        {
            IsConnectionTimeout = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => IsConnectionTimeout = false);
        }
        else if (result == ConnectionResult.VersionMismatch)
        {
            IsVersionMismatch = true;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => IsVersionMismatch = false);
        }
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

    public MainWindowViewModel(Peer2Peer peer2Peer, 
        SimClient sim, 
        MasterSwitch masterSwitch, 
        Coordinator coordinator)
    {
        _peer2Peer = peer2Peer;
        _masterSwitch = masterSwitch;
        // _configuration = configuration;

        SessionCode = peer2Peer.PeerId;
        
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

        peer2Peer.DiscoveredEndpoint
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(x => Dispatcher.UIThread.Post(() => Address = x));
        
        var connections = Observable.Merge(
                Observable.Interval(TimeSpan.FromSeconds(1)).Select(_ => peer2Peer.Connections),
                peer2Peer.ConnectionEstablished.Select(_ => peer2Peer.Connections),
                peer2Peer.ConnectionLost.Select(_ => peer2Peer.Connections));
            
        connections
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(items => Dispatcher.UIThread.Post(() => 
            { 
                var i = 0;
                Connections.Clear();
                foreach (var connection in items)
                    Connections.Add(new(connection.PeerId, connection.Rtt)
                        { HasSeparatorAfter = i++ < items.Count - 1 }); 
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

    public partial class Connection : ObservableObject
    {
        [ObservableProperty]
        private string _peerId;

        [ObservableProperty]
        private uint _rtt;
        
        public bool HasSeparatorAfter { get; set; }

        public Connection(string peerId, uint rtt)
        {
            _peerId = peerId;
            _rtt = rtt;
        }
    }
}