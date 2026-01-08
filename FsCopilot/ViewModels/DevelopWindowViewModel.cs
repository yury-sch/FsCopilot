namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Connection;
using Simulation;

public partial class DevelopWindowViewModel : ViewModelBase, IDisposable
{
    private readonly CompositeDisposable _d = new();
    private readonly SimClient _sim;
    private readonly Subject<Unit> _reload = new();
    private readonly SerialDisposable _recording = new();
    private readonly SerialDisposable _playing = new();
    
    private readonly IObservable<Physics> _physics;
    private readonly IObservable<Control> _controls;
    private readonly IObservable<Throttle> _throttle;
    private readonly IObservable<Fuel> _fuel;
    private readonly IObservable<Payload> _payload;
    private readonly IObservable<Control.Flaps> _flaps;
    // private readonly Subject<Trace.Var> _vars = new();
    private Trace? _trace;

    public ObservableCollection<Node> Nodes { get; set; } = [];
    // public ObservableCollection<Node> InstrumentNode = [];
    [ObservableProperty] private string _loaded = string.Empty;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isRecording;

    public DevelopWindowViewModel(SimClient sim)
    {
        _sim = sim;
        var latestTree = new SerialDisposable();
        _d.Add(latestTree);
        
        _d.Add(sim.Aircraft
            .Merge(_reload.WithLatestFrom(sim.Aircraft, (_, a) => a))
            .Subscribe(path => Dispatcher.UIThread.Post(() =>
            {
                latestTree.Disposable = PopulateTreeAndAttach(path);
            })));

        _physics = Connect<Physics>();
        _controls = Connect<Control>();
        _throttle = Connect<Throttle>();
        _fuel = Connect<Fuel>();
        _payload = Connect<Payload>();
        _flaps = Connect<Control.Flaps>();
    }

    public void Dispose()
    {
        _d.Dispose();
        _playing.Dispose();
    }

    private IObservable<T> Connect<T>() where T : struct
    {
        var connectable = _sim.Stream<T>().Publish();
        _d.Add(connectable.Connect());
        return connectable;
    }

    private IDisposable PopulateTreeAndAttach(string path)
    {
        var tree = Definitions.LoadTree($"{path}.yaml");
        Nodes.Clear();
        if (tree == DefinitionNode.Empty)
        {
            Loaded = $"Failed to load {path} configuration";
            return Disposable.Empty;
        }

        var nodes = PopulateTree(_sim, tree);
        foreach (var node in nodes) Nodes.Add(node);
        Loaded = $"Loaded {path} configuration";

        return new CompositeDisposable(nodes);

        static Node[] PopulateTree(SimClient sim, DefinitionNode node)
        {
            var include = new ObservableCollection<Node>();
            var master = new ObservableCollection<Node>();
            var shared = new ObservableCollection<Node>();
        
            foreach (var i in node.Include) include.Add(new(i.Path, new(PopulateTree(sim, i)), false));
            foreach (var def in node.Master) master.Add(new(sim, def));
            foreach (var def in node.Shared) shared.Add(new(sim, def));

            var nodes = new List<Node>();
            if (include.Count > 0) nodes.Add(new("Include", include, true));
            if (master.Count > 0) nodes.Add(new("Master", master, true));
            if (shared.Count > 0) nodes.Add(new("Shared", shared, true));
            return nodes.ToArray();
        }
    }

    [RelayCommand]
    private void Reload() => _reload.OnNext(Unit.Default);

    [RelayCommand]
    private void Record()
    {
        IsRecording = !IsRecording;
        if (IsRecording)
        {
            _trace = new();
            
            _recording.Disposable = new CompositeDisposable(
                _physics.Record(_trace.Physics),
                _controls.Record(_trace.Controls),
                _throttle.Record(_trace.Throttle),
                _fuel.Record(_trace.Fuel),
                _payload.Record(_trace.Payload),
                _flaps.Record(_trace.Flaps)
                // _vars.Record(_trace.Vars)
            );
        }
        else
        {
            _recording.Disposable?.Dispose();
        }
    }

    [RelayCommand]
    private void Play()
    {
        IsPlaying = !IsPlaying; 

        if (IsPlaying)
        {
            Freeze(true);
            _trace ??=  new();
            _playing.Disposable = Observable.Merge(
                Replay(_trace.Physics), 
                Replay(_trace.Controls), 
                Replay(_trace.Throttle), 
                Replay(_trace.Fuel), 
                Replay(_trace.Payload), 
                Replay(_trace.Flaps)
            ).Subscribe(_ => {}, () =>
            {
                IsPlaying = false;
                Freeze(false);
            });
        }
        else
        {
            _playing.Disposable?.Dispose();
            Freeze(false);
        }

        return;

        void Freeze(bool isFreeze)
        {
            _sim.Set("K:FREEZE_LATITUDE_LONGITUDE_SET", isFreeze);
            _sim.Set("K:FREEZE_ALTITUDE_SET", isFreeze);
            _sim.Set("K:FREEZE_ATTITUDE_SET", isFreeze);
        }

        IObservable<Unit> Replay<T>(IReadOnlyList<Recorded<T>> trace) where T : struct =>
            trace.Playback().Do(_sim.Set).Select(_ => Unit.Default);
    }
}

public partial class Node : ObservableObject, IDisposable
{
    private readonly SimClient? _sim;
    private readonly Definition? _def;
    private readonly IDisposable? _sub;
    // private readonly string? _rawJson;
    
    public ObservableCollection<Node>? SubNodes { get; }

    // public string Name { get; }
    public string Title { get; }
    public object? Value { get; }
    public object? PrevValueValue { get; }
    public bool IsVariable { get; }
    public bool IsExpanded { get; } = true;
    [ObservableProperty]
    private bool _isPulse; // IsPulse

    private Node(string title, bool isExpanded)
    {
        // Name = name;
        Title = title;
        IsExpanded = isExpanded;
    }

    public Node(string title, ObservableCollection<Node> subNodes, bool isExpanded) : this(title, isExpanded)
    {
        SubNodes = subNodes;
    }

    public Node(SimClient sim, Definition def) : this(string.Empty, false)
    {
        _sim = sim;
        SubNodes = [];
        
        var getVar = def.Get;
        var units = def.Units;
        var title = new StringBuilder();
        title.Append(getVar);
        if (!string.IsNullOrWhiteSpace(units)) title.Append($", {units}");
        Title = title.ToString();
        
        _sub = sim.Stream(getVar, units)
            .Sample(TimeSpan.FromMilliseconds(250))
            .Do(value => Log.Information("[DEVELOP] RECV {Name} {Value}", getVar, value))
            .WithPreviousFirstPair()
            .Subscribe(pair => Dispatcher.UIThread.Post(() =>
            {
                if (SubNodes.Count >= 20) SubNodes.Clear();
                SubNodes.Add(new(_sim, def, pair.Curr, pair.Prev));
                PulseOnce(TimeSpan.FromMilliseconds(1200));
            }));
    }

    private Node(SimClient sim, Definition def, object value, object prevValue) : this(string.Empty, false)
    {
        _sim = sim;
        _def = def;
        Value = value;
        PrevValueValue = prevValue;
        IsVariable = true;
        var setVar = def.Set(value, prevValue, out var val0, out var val1, out var val2, out var val3, out var val4);
        Title += $"{Convert.ToString(val0, CultureInfo.InvariantCulture)} ";
        if (val1 != null) Title += $"{Convert.ToString(val1, CultureInfo.InvariantCulture)} ";
        if (val2 != null) Title += $"{Convert.ToString(val2, CultureInfo.InvariantCulture)} ";
        if (val3 != null) Title += $"{Convert.ToString(val3, CultureInfo.InvariantCulture)} ";
        if (val4 != null) Title += $"{Convert.ToString(val4, CultureInfo.InvariantCulture)} ";
        Title += $"(>{setVar})";
    }

    // Turns IsPulse on, then off after duration (UI-thread safe)
    private void PulseOnce(TimeSpan duration)
    {
        Dispatcher.UIThread.Post(() => IsPulse = true);
        var t = new DispatcherTimer { Interval = duration };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Dispatcher.UIThread.Post(() => IsPulse = false);
        };
        t.Start();
    }

    // public Node(string raw, JsonElement json)
    // {
    //     _rawJson = raw;
    //     IsVariable = true;
    //     Title = raw;
    //     // var json = x.JSON;
    //     // if (json.TryGetProperty("key", out var key) &&
    //     //     json.TryGetProperty("instrument", out var instrument))
    //     // {
    //     //     if (json.TryGetProperty("value", out var value)) Log.Information("[PACKET] SENT {Name} {Value} ({Instrument})", key.ToString(), value.ToString(), instrument);
    //     //     else Log.Information("[PACKET] SENT {Name} ({Instrument})", key.ToString(), instrument);
    //     // }
    // }

    public void Dispose()
    {
        foreach (var subNode in SubNodes ?? []) subNode.Dispose();
        _sub?.Dispose();
    }

    [RelayCommand]
    private void Push()
    {
        if (_sim == null || _def == null || Value == null || PrevValueValue == null) return;
        var eventName = _def.Set(Value, PrevValueValue, out var val0, out var val1, out var val2, out var val3,
            out var val4);
        _sim.Set(eventName, val0, val1, val2, val3, val4);
    }
}

public class Trace
{
    public List<Recorded<Physics>> Physics { get; } = [];
    public List<Recorded<Control>> Controls { get; } = [];
    public List<Recorded<Throttle>> Throttle { get; } = [];
    public List<Recorded<Fuel>> Fuel { get; } = [];
    public List<Recorded<Payload>> Payload { get; } = [];
    public List<Recorded<Control.Flaps>> Flaps { get; } = [];
    // public List<Recorded<Var>> Vars { get; set; } = [];

    // public record Var(string Name, object Value);
}
