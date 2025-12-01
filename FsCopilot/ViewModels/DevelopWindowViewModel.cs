using System.Diagnostics;

namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Avalonia.Threading;
using Connection;
using Serilog;
using Simulation;
using System.Reactive;
using System.Reactive.Subjects;

public partial class DevelopWindowViewModel : ViewModelBase
{
    private readonly SimClient _sim;
    private readonly Subject<Unit> _reload = new();
    private readonly List<Recorded<Physics>> _trace = [];
    private readonly  SerialDisposable _recording = new();
    private readonly  SerialDisposable _playing = new();

    public ObservableCollection<Node> Nodes { get; set; } = [];
    // public ObservableCollection<Node> InstrumentNode = [];
    [ObservableProperty] private string _loaded;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isRecording;

    public DevelopWindowViewModel(SimClient sim)
    {
        _sim = sim;
        var latestTree = new SerialDisposable();
        
        sim.Aircraft
            .Merge(_reload.WithLatestFrom(sim.Aircraft, (_, a) => a))
            .Subscribe(path => Dispatcher.UIThread.Post(() =>
            {
                latestTree.Disposable = PopulateTreeAndAttach(path);
            }));
    }
    
    // Helper method that returns IDisposable for cleanup.
    IDisposable PopulateTreeAndAttach(string path)
    {
        var d = new CompositeDisposable();

        var definitions = Definitions.LoadTree($"{path}.yaml");
        Nodes.Clear();
        if (definitions == DefinitionNode.Empty)
        {
            Loaded = $"Failed to load {path} configuration";
            return d;
        }
        foreach (var node in PopulateTree(definitions, d)) Nodes.Add(node);
        // Nodes.Add(new("Instrument events", InstrumentNode));
        Loaded = $"Loaded {path} configuration";

        return d;
    }

    // private static Node? FindNodeByName(IEnumerable<Node> nodes, string name)
    // {
    //     foreach (var node in nodes)
    //     {
    //         if (node.Name == name) return node;
    //
    //         if (node.SubNodes == null) continue;
    //         var found = FindNodeByName(node.SubNodes, name);
    //         if (found != null) return found;
    //     }
    //
    //     return null;
    // }

    private Node[] PopulateTree(DefinitionNode node, CompositeDisposable d)
    {
        var include = new ObservableCollection<Node>();
        var master = new ObservableCollection<Node>();
        var shared = new ObservableCollection<Node>();
        
        foreach (var i in node.Include) include.Add(new(i.Path, new(PopulateTree(i, d)), false));
        foreach (var def in node.Master) master.Add(VarNode(def));
        foreach (var def in node.Shared) shared.Add(VarNode(def));

        var nodes = new List<Node>();
        if (include.Count > 0) nodes.Add(new("Include", include, true));
        if (master.Count > 0) nodes.Add(new("Master", master, true));
        if (shared.Count > 0) nodes.Add(new("Shared", shared, true));
        return nodes.ToArray();

        Node VarNode(Definition def)
        {
            var varNode = new Node(_sim, def);
            d.Add(varNode);
            return varNode;
        }
    }

    [RelayCommand]
    private void Reload()
    {
        _reload.OnNext(Unit.Default);
    }

    [RelayCommand]
    private void Record()
    {
        var codec = new Physics.Codec();
        IsRecording = !IsRecording;
        if (IsRecording)
        {
            _trace.Clear();
            _recording.Disposable = _sim.Stream<Physics>().Select(p =>
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);
                codec.Encode(p, bw);
                ms.Position = 0;
                using var br = new BinaryReader(ms);
                return codec.Decode(br);
            }).Record(_trace);
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
            _sim.Set("K:FREEZE_LATITUDE_LONGITUDE_SET", true);
            _sim.Set("K:FREEZE_ALTITUDE_SET", true);
            _sim.Set("K:FREEZE_ATTITUDE_SET", true);
            var s = _sim.Stream<Physics>().Subscribe(_ => { });
            _playing.Disposable = _trace.Playback()
                .Subscribe(
                    x => _sim.Set(x),
                    _ => { },
                    () =>
                    {
                        IsPlaying = false;
                        s.Dispose();
                        _sim.Set("K:FREEZE_LATITUDE_LONGITUDE_SET", false);
                        _sim.Set("K:FREEZE_ALTITUDE_SET", false);
                        _sim.Set("K:FREEZE_ATTITUDE_SET", false);
                    });
        }
        else
        {
            _playing.Disposable?.Dispose();
        }
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
