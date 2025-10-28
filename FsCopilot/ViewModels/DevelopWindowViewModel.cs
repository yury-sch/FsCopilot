namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Connection;
using Serilog;
using Simulation;

public partial class DevelopWindowViewModel : ViewModelBase
{
    private readonly SimClient _sim;

    public ObservableCollection<Node> Nodes { get; set; } = [];
    // public ObservableCollection<Node> InstrumentNode = [];
    [ObservableProperty] private string _loaded;

    public DevelopWindowViewModel(SimClient sim)
    {
        _sim = sim;
        var latestTree = new SerialDisposable();
        
        sim.Aircraft
            .Subscribe(path => Dispatcher.UIThread.Post(() =>
            {
                latestTree.Disposable = PopulateTreeAndAttach(path);
            }));
    }
    
    // Helper method that returns IDisposable for cleanup.
    IDisposable PopulateTreeAndAttach(string path)
    {
        var d = new CompositeDisposable();
        var match = Regex.Match(path, @"SimObjects\\Airplanes\\([^\\]+)");
        if (match.Success) path = match.Groups[1].Value;

        try
        {
            var definitions = Definitions.LoadTree($"{path}.yaml");
            Nodes.Clear();
            foreach (var node in PopulateTree(definitions, d)) Nodes.Add(node);
            // Nodes.Add(new("Instrument events", InstrumentNode));
            Loaded = $"Loaded {path} configuration";
        }
        catch (Exception e)
        {
            Log.Error(e, $"Failed to load {path} configuration");
            Nodes.Clear();
            Loaded = $"Failed to load {path} configuration";
        }

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
}

public partial class Node : ObservableObject, IDisposable
{
    private readonly SimClient? _sim;
    private readonly Definition? _def;
    private readonly IDisposable? _sub;
    private readonly string? _rawJson;
    public ObservableCollection<Node>? SubNodes { get; }

    // public string Name { get; }
    public string Title { get; }
    public object? Value { get; }
    public object? PrevValueValue { get; }
    public bool IsVariable { get; }
    public bool IsExpanded { get; } = true;

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
        
        var name = def.GetVar(out var units);
        var title = new StringBuilder();
        title.Append(name);
        if (!string.IsNullOrWhiteSpace(units)) title.Append($", {units}");
        Title = title.ToString();
        
        _sub = sim.Stream(name, units)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .WithPreviousFirstPair()
            .Subscribe(pair => Dispatcher.UIThread.Post(() =>
            {
                SubNodes.Add(new(_sim, def, pair.Curr, pair.Prev));
            }));
    }

    private Node(SimClient sim, Definition def, object value, object prevValue) : this(string.Empty, false)
    {
        // Name =  $"{def.Name}_{DateTime.Now:O}_{Convert.ToString(value, CultureInfo.InvariantCulture)}"
        // Title = $"{DateTime.Now:HH:mm:ss}: {value}"
        _sim = sim;
        _def = def;
        Value = value;
        PrevValueValue = prevValue;
        IsVariable = true;
        if (def.TryGetEvent(value, prevValue, out var eventName, out var val0, out var val1, out var val2, out var val3, out var val4))
        {
            if (val0 != null) Title += $"{Convert.ToString(val0, CultureInfo.InvariantCulture)} ";
            if (val1 != null) Title += $"{Convert.ToString(val1, CultureInfo.InvariantCulture)} ";
            if (val2 != null) Title += $"{Convert.ToString(val2, CultureInfo.InvariantCulture)} ";
            if (val3 != null) Title += $"{Convert.ToString(val3, CultureInfo.InvariantCulture)} ";
            if (val4 != null) Title += $"{Convert.ToString(val4, CultureInfo.InvariantCulture)} ";
            Title += $"(>{eventName})";
        }
        else
        {
            var name = def.GetVar(out _);
            Title += $"{Convert.ToString(value, CultureInfo.InvariantCulture)} ";
            Title += $"(>{name})";
        }
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
        if (_sim != null && _def != null && Value != null && PrevValueValue != null)
        {
            if (!_def.TryGetEvent(Value, PrevValueValue, out var eventName, out var val0, out var val1, out var val2,
                    out var val3, out var val4))
            {
                var name = _def.GetVar(out _);
                _sim.Set(name, Value);
                return;
            }

            // var value = _def.TransformValue(Value);
            _sim.Call(eventName, val0, val1, val2, val3, val4);
            return;
        }
        // else if (_socket != null && _rawJson != null)
        // {
        //     await Task.WhenAll(_socket.ListClients().Select(c => _socket.SendAsync(c.Guid, _rawJson)));
        // }
    }
}
