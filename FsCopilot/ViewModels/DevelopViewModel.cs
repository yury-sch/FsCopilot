using System.Security.Cryptography;

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
using System.Reactive.Disposables.Fluent;
using System.Reactive.Subjects;
using ReactiveUI;

public class DevelopViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _d = new();
    // private readonly SimClient _sim;
    private readonly Subject<Unit> _reload = new();
    private readonly SerialDisposable _recording = new();
    private readonly SerialDisposable _playing = new();
    
    private string _loaded = string.Empty;
    private bool _isPlaying;
    private bool _isRecording;
    
    public string Loaded
    {
        get => _loaded;
        set => this.RaiseAndSetIfChanged(ref _loaded, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        set => this.RaiseAndSetIfChanged(ref _isRecording, value);
    }

    public ObservableCollection<Node> Nodes { get; set; } = [];
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }
    public ReactiveCommand<Unit, Unit> RecordCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }

    public DevelopViewModel(SimClient sim)
    {
        var latestTree = new SerialDisposable().DisposeWith(_d);
        var sw = Stopwatch.StartNew();
        
        Span<byte> sessionBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(sessionBytes);
        var sessionId = BitConverter.ToUInt64(sessionBytes);

        sim.Aircraft
            .Merge(_reload.WithLatestFrom(sim.Aircraft, (_, a) => a))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(path => latestTree.Disposable = PopulateTreeAndAttach(sim, path))
            .DisposeWith(_d);

        ReloadCommand = ReactiveCommand.Create(() => _reload.OnNext(Unit.Default));

        sim.Register<Physics>();
        sim.Register<Control>();
        Trace? trace = null;
        
        RecordCommand = ReactiveCommand.Create(() =>
        {
            IsRecording = !IsRecording;
            if (IsRecording)
            {
                trace = new();
            
                _recording.Disposable = new CompositeDisposable(
                    sim.Stream<Physics>().Record(trace.Physics),
                    sim.Stream<Control>().Record(trace.Controls)
                    // _vars.Record(_trace.Vars)
                );
            }
            else
            {
                _recording.Disposable?.Dispose();
            }
        });
        
        PlayCommand = ReactiveCommand.Create(() =>
        {
            IsPlaying = !IsPlaying;
            if (IsPlaying)
            {
                var rnd = new System.Random();
                Freeze(true);
                trace ??= new();
                _playing.Disposable = Observable.Merge(
                    trace.Physics.Playback()
                        .Do(data =>
                        {
                            data.SessionId = sessionId;
                            data.TimeMs = (uint)sw.ElapsedMilliseconds;
                            sim.Set(data);
                        })
                        .SelectMany(x => Observable.Return(x)
                            .Delay(TimeSpan.FromMilliseconds(rnd.Next(20, 101))))
                        .Select(_ => Unit.Default),
                    trace.Controls.Playback()
                        .Do(data =>
                        {
                            data.SessionId = sessionId;
                            data.TimeMs = (uint)sw.ElapsedMilliseconds;
                            sim.Set(data);
                        })
                        .SelectMany(x => Observable.Return(x)
                            .Delay(TimeSpan.FromMilliseconds(rnd.Next(20, 101))))
                        .Select(_ => Unit.Default)
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
                sim.Set("K:FREEZE_LATITUDE_LONGITUDE_SET", isFreeze);
                sim.Set("K:FREEZE_ALTITUDE_SET", isFreeze);
                sim.Set("K:FREEZE_ATTITUDE_SET", isFreeze);
            }
        });
    }

    public void Dispose()
    {
        _d.Dispose();
        _recording.Dispose();
        _playing.Dispose();
    }

    private IDisposable PopulateTreeAndAttach(SimClient sim, string path)
    {
        Nodes.Clear();
        if (!Definitions.TryLoadTree($"{path}.yaml", out var tree))
        {
            Loaded = $"Failed to load {path} configuration";
            return Disposable.Empty;
        }

        var nodes = PopulateTree(sim, tree);
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
    
    delegate void RefAction<T>(ref T value);
}

public class Node : ReactiveObject, IDisposable
{
    private readonly IDisposable? _sub;
    
    private bool _isPulse;

    public string Title { get; }
    public bool IsVariable { get; }
    public bool IsExpanded { get; }
    public bool IsPulse
    {
        get => _isPulse;
        set => this.RaiseAndSetIfChanged(ref _isPulse, value);
    }

    public ObservableCollection<Node>? SubNodes { get; }
    public ReactiveCommand<Unit, Unit>? PushCommand { get; }

    private Node(string title, bool isExpanded)
    {
        Title = title;
        IsExpanded = isExpanded;
    }

    public Node(string title, ObservableCollection<Node> subNodes, bool isExpanded) : this(title, isExpanded)
    {
        SubNodes = subNodes;
    }

    public Node(SimClient sim, Definition def) : this(string.Empty, false)
    {
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
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pair => 
            {
                if (SubNodes.Count >= 20) SubNodes.Clear();
                SubNodes.Add(new(sim, def, pair.Curr, pair.Prev));
                PulseOnce(TimeSpan.FromMilliseconds(1200));
            });
        return;

        // Turns IsPulse on, then off after duration (UI-thread safe)
        void PulseOnce(TimeSpan duration)
        {
            IsPulse = true;
            var t = new DispatcherTimer { Interval = duration };
            t.Tick += (_, _) =>
            {
                t.Stop();
                IsPulse = false;
            };
            t.Start();
        }
    }

    private Node(SimClient sim, Definition def, object value, object prevValue) : this(string.Empty, false)
    {
        IsVariable = true;
        var setVar = def.Set(value, prevValue, out var val0, out var val1, out var val2, out var val3, out var val4);
        Title += $"{Convert.ToString(val0, CultureInfo.InvariantCulture)} ";
        if (val1 != null) Title += $"{Convert.ToString(val1, CultureInfo.InvariantCulture)} ";
        if (val2 != null) Title += $"{Convert.ToString(val2, CultureInfo.InvariantCulture)} ";
        if (val3 != null) Title += $"{Convert.ToString(val3, CultureInfo.InvariantCulture)} ";
        if (val4 != null) Title += $"{Convert.ToString(val4, CultureInfo.InvariantCulture)} ";
        Title += $"(>{setVar})";

        PushCommand = ReactiveCommand.Create(() => sim.Set(setVar, val0, val1, val2, val3, val4));
    }

    public void Dispose()
    {
        foreach (var subNode in SubNodes ?? []) subNode.Dispose();
        _sub?.Dispose();
    }
}

public class Trace
{
    public List<Recorded<Physics>> Physics { get; } = [];
    public List<Recorded<Control>> Controls { get; } = [];
    // public List<Recorded<Var>> Vars { get; set; } = [];

    // public record Var(string Name, object Value);
}
