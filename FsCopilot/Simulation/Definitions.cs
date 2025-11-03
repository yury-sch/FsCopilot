namespace FsCopilot.Simulation;

using System.Globalization;
using System.Collections;
using System.Text.RegularExpressions;
using Jint;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class Definitions : IReadOnlyCollection<Definition>
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // .IgnoreUnmatchedProperties() 
        .Build();

    private readonly Definition[] _links;

    public string[] Ignore { get; }

    private Definitions(Definition[] links, string[] ignore)
    {
        _links = links;
        Ignore = ignore;
    }

    public static DefinitionNode LoadTree(string path)
    {
        var cfgFile = File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Definitions", ..path.Split('/')]));
        var cfg = Deserializer.Deserialize<Config>(cfgFile);
        var master = cfg.Master
            .Where(m => !string.IsNullOrWhiteSpace(m.Get))
            .Select(m => new Definition(false, m.Get, m.Set, m.Skp)).ToArray();
        var shared = cfg.Shared
            .Where(m => !string.IsNullOrWhiteSpace(m.Get))
            .Select(m => new Definition(true, m.Get, m.Set, m.Skp)).ToArray();
        
        var includes = new List<DefinitionNode>();
        foreach (var i in cfg.Include)
        {
            try
            {
                includes.Add(LoadTree(i));
            }
            catch (Exception e)
            {
                Log.Error(e, "Error loading module {Module}", i);
            }
        }

        var ignore = cfg.Ignore.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToArray();
        return new(path, includes.ToArray(), master, shared, ignore);
    }

    public static Definitions Load(string name)
    {
        var node = LoadTree($"{name}.yaml");
        var master = new List<Definition>();
        var shared = new List<Definition>();
        var ignore = new List<string>();
        Collect(node, master, shared, ignore);
        var simVars = master.Concat(shared).ToArray();
        return new(simVars, ignore.ToArray());
    }

    private static void Collect(DefinitionNode node, List<Definition> master, List<Definition> shared, List<string> ignore)
    {
        foreach (var child in node.Include) Collect(child, master, shared, ignore);
        master.AddRange(node.Master);
        shared.AddRange(node.Shared);
        ignore.AddRange(node.Ignore);
    }

    private class Config
    {
        public string[] Include { get; set; } = [];
        public Link[] Shared { get; set; } = [];
        public Link[] Master { get; set; } = [];
        public string[] Ignore { get; set; } = [];

        public class Link
        {
            public string Get { get; set; }
            public string? Set { get; set; }
            public string? Skp { get; set; }
        }
    }

    public IEnumerator<Definition> GetEnumerator() => _links.AsReadOnly().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count { get; }
}

public record DefinitionNode(
    string Path,
    DefinitionNode[] Include,
    Definition[] Master,
    Definition[] Shared,
    string[] Ignore);

public class Definition
{
    private readonly string? _set;
    public bool Shared { get; init; }
    public string Get { get; init; }
    public string Units { get; init; }
    public string? Skip { get; init; }

    public Definition(bool shared, string get, string? set, string? skp)
    {
        Shared = shared;
        var parts = get.Split(',');
        Units = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        Get = parts[0].Trim();
        _set = set?.Trim();
        Skip = skp?.Trim();
    }

    public string Set(object value, object current, 
        out object value0, out object? value1, out object? value2, out object? value3, out object? value4)
    {
        value0 = value;
        value1 = null;
        value2 = null;
        value3 = null;
        value4 = null;
        
        if (_set == null) return Get;
        var set = _set;
        if (_set.IndexOfAny(['\'', '`', '?', '{', '}'])  >= 0)
        {
            try
            {
                var engine = new Engine().SetValue("value", value).SetValue("current", current);
                set = engine.Evaluate(_set).AsString();
            }
            catch (Exception e)
            {
                Log.Error(e, "Unable to parse event expression {Name}", _set);
                return Get;
            }
        }
        
        var rx = new Regex(@"^(?<args>.*?)\s*\(>\s*(?<name>[^)]+)\)$", RegexOptions.CultureInvariant);
        // var rx = new Regex(@"^([A-Z]):([^(:]+)(?:\(([^)]*)\))?$", RegexOptions.CultureInvariant);

        var m = rx.Match(set);
        if (!m.Success) return Get;

        // value = TransformValue(value);

        set = m.Groups["name"].Value.Trim();
        var pars = m.Groups["args"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Select(ParseParam)
            .ToArray();
        if (pars.Length == 0) value0 = value;
        if (pars.Length >= 1) value0 = pars[0] ?? 1;
        if (pars.Length >= 2) value1 = pars[1];
        if (pars.Length >= 3) value2 = pars[2];
        if (pars.Length >= 4) value3 = pars[3];
        if (pars.Length >= 5) value4 = pars[4];

        return set;

        object? ParseParam(string p) => uint.TryParse(p, out var ui) 
            ? ui 
            : int.TryParse(p, out var i) 
                ? i 
                : double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d 
                    : p;
    }
}

// public class YcDefinition
// {
//     public string[] Include { get; set; } = [];
//     public Mapping[] Shared { get; set; } = [];
//     public Mapping[] Master { get; set; } = [];
//     public Mapping[] Server { get; set; } = [];
//     public string[] Ignore { get; set; } = [];
//     
//     public class Mapping
//     {
//         public required string Type { get; set; }
//         public required string VarName { get; set; }
//         public string? VarUnits { get; set; }
//         public string? VarType { get; set; }
//         public int? AddBy { get; set; }
//         public double? MultiplyBy { get; set; }
//         public string? EventName { get; set; }
//         public string? OffEventName { get; set; }
//         public string? OnEventName { get; set; }
//         public int? EventParam { get; set; }
//         public string? Interpolate { get; set; }
//         public bool? CancelHEvents { get; set; }
//         public bool? IndexReversed { get; set; }
//         public bool? Unreliable { get; set; }
//         public bool? UseCalculator { get; set; }
//         public string? Action { get; set; }
//         public string? UpEventName { get; set; }
//         public string? DownEventName { get; set; }
//         public double? IncrementBy { get; set; }
//         public MappingCondition? Condition { get; set; }
//         
//         public class MappingCondition
//         {
//             public required MappingConditionVar Var { get; set; }
//             public MappingConditionEquals? Equals { get; set; }
//             
//             public class MappingConditionVar
//             {
//                 public required string VarName { get; set; }
//                 public string? VarUnits { get; set; }
//                 public string? VarType { get; set; }
//                 
//             }
//                 
//             public class MappingConditionEquals
//             {
//                 public bool Bool { get; set; }
//             }
//         }
//     }
// }