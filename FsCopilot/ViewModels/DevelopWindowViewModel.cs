namespace FsCopilot.ViewModels;

using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Connection;
using Simulation;

public partial class DevelopWindowViewModel : ViewModelBase
{
    private readonly SimConnectClient _simConnect;
    
    public ObservableCollection<Node> Nodes { get; set; } = new();
    [ObservableProperty] private string _loaded;

    public DevelopWindowViewModel(SimConnectClient simConnect)
    {
        _simConnect = simConnect;
        simConnect.Stream<Aircraft>()
            // .StartWith(Unit.Default)
            .SelectMany(_ => Observable.FromAsync(() => _simConnect.SystemState<string>("AircraftLoaded")))
            .Take(1)
            .Subscribe(path => Dispatcher.UIThread.Post(() => 
            {
                var match = Regex.Match(path, @"SimObjects\\Airplanes\\([^\\]+)");
                if (match.Success) path = match.Groups[1].Value;
                
                var definitions = Definitions.LoadTree($"{path}.yaml");
                Loaded = path;
                foreach (var node in PopulateTree(definitions)) Nodes.Add(node);
            }));
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

    private Node[] PopulateTree(DefinitionNode node)
    {
        var include = new ObservableCollection<Node>();
        var master = new ObservableCollection<Node>();
        var shared = new ObservableCollection<Node>();
        
        foreach (var i in node.Include) include.Add(new(i.Path, i.Path, new(PopulateTree(i))));
        foreach (var def in node.Master) master.Add(VarNode(def));
        foreach (var def in node.Shared) shared.Add(VarNode(def));

        var nodes = new List<Node>();
        if (include.Count > 0) nodes.Add(new("Include", "Include", include));
        if (master.Count > 0) nodes.Add(new("Master", "Master", master));
        if (shared.Count > 0) nodes.Add(new("Shared", "Shared", shared));
        return nodes.ToArray();

        Node VarNode(Definition def)
        {
            var title = new StringBuilder();
            title.Append(def.Name);
            if (!string.IsNullOrWhiteSpace(def.Units)) title.Append($" ({def.Units})");
            var values = new ObservableCollection<Node>();
            var varNode = new Node(def.Name, title.ToString(), values);

            _simConnect.Stream(def.Name, def.Units).Subscribe(value =>
            {
                values.Add(new(_simConnect, def, value));
            });
            return varNode;
        }
    }
}

public partial class Node : ObservableObject
{
    private readonly SimConnectClient? _simConnect;
    private readonly Definition? _def;
    public ObservableCollection<Node>? SubNodes { get; }

    public string Name { get; }
    public string Title { get; }
    public object? Value { get; }
    public bool IsVariable { get; }

    public Node(string name, string title)
    {
        Name = name;
        Title = title;
    }

    public Node(string name, string title, ObservableCollection<Node> subNodes) : this(name, title)
    {
        SubNodes = subNodes;
    }

    public Node(SimConnectClient simConnect, Definition def, object value) : this(
        $"{def.Name}_{DateTime.Now:O}_{value}", $"{DateTime.Now:HH:mm:ss}: {value}")
    {
        _simConnect = simConnect;
        _def = def;
        Value = value;
        IsVariable = true;
        if (def.TryGetEvent(value, out var eventName, out var paramIx))
        {
            value = def.TransformValue(value);
            Title += $" --> {eventName}:{paramIx} ({value})";
        }
    }

    [RelayCommand]
    private void Push(string name)
    {
        if (_simConnect == null || _def == null || Value == null) return;
        
        if (!_def.TryGetEvent(Value, out var eventName, out var paramIx))
        {
            _simConnect.Set(_def.Name, Value);
            return;
        }

        var value = _def.TransformValue(Value);
        _simConnect.TransmitClientEvent(eventName, value, paramIx);
    }
}
