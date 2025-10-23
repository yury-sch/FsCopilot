namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Connection;
using Network;
using Simulation;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private string _sessionCode;
    [ObservableProperty] private string _aircraft;
    [ObservableProperty] private bool _notSupported;
    [ObservableProperty] private string _connectionCode = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private bool _isSimConnected;
    [ObservableProperty] private bool _showTakeControl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorMessage))] private IPEndPoint? _address;

    public string ErrorMessage =>
        Address == null ? "No internet connection. Please check your network." :
        !IsSimConnected ? "Microsoft Flight Simulator is not running!" :
        string.Empty;

    // public ObservableCollection<string> Configurations { get; set; } = [];
    public ObservableCollection<Connection> Connections { get; set; } = [];
    
    // [ObservableProperty] private string? _selectedConfiguration;

    private readonly Peer2Peer _peer2Peer;
    private readonly MasterSwitch _masterSwitch;
    private readonly Coordinator _coordinator;
    private readonly Subject<bool> _unsubscribe = new();

    [RelayCommand]
    private async Task Connect()
    {
        await _masterSwitch.Join();
        await _peer2Peer.Connect(ConnectionCode);
        ConnectionCode = string.Empty;
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
        _coordinator = coordinator;
        // _configuration = configuration;

        SessionCode = peer2Peer.PeerId;
        
        coordinator.Aircraft
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(aircraft => Dispatcher.UIThread.Post(() => Aircraft = aircraft));
        
        coordinator.Configured
            .Sample(TimeSpan.FromMilliseconds(250))
            .TakeUntil(_unsubscribe)
            .Subscribe(configured => Dispatcher.UIThread.Post(() => NotSupported = configured == false));
        
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

public class StringNotEmptyToBoolConverter : IValueConverter
{
    public static readonly StringNotEmptyToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringEmptyToBoolConverter : IValueConverter
{
    public static readonly StringEmptyToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}