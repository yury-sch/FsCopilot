using System.Diagnostics.CodeAnalysis;

namespace FsCopilot.Simulation;

using System.Globalization;
using System.Collections;
using System.Text.RegularExpressions;
using Jint;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class Definitions : IReadOnlyCollection<Definition>
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithObjectFactory(type =>
        {
            if (type == typeof(Config)) return new Config();
            if (type == typeof(Config.Link)) return new Config.Link();
            return Activator.CreateInstance(type)!;
        })
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

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(Config))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(Config.Link))]
    private static Config ParseConfig(string yaml) => Deserializer.Deserialize<Config>(yaml);

    public static bool TryLoadTree(string path, out DefinitionNode node)
    {
        Config cfg;
        try
        {
            var cfgFile = File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Definitions", ..path.Split('/')])).Trim();
            if (string.IsNullOrWhiteSpace(cfgFile))
            {
                node = DefinitionNode.Empty;
                return true;
            }
            cfg = ParseConfig(cfgFile);
        }
        catch (FileNotFoundException)
        {
            Log.Information("[Definitions] Failed to load {Module} configuration", path);
            node = DefinitionNode.Empty;
            return false;
        }
        catch (Exception e)
        {
            Log.Error(e, "[Definitions] Failed to load {Module} configuration", path);
            node = DefinitionNode.Empty;
            return false;
        }
        
        var master = (cfg.Master ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Get))
            .Select(m => new Definition(false, m.Get, m.Set, m.Skp)).ToArray();
        var shared = (cfg.Shared ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Get))
            .Select(m => new Definition(true, m.Get, m.Set, m.Skp)).ToArray();
        var ignore = (cfg.Ignore ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToArray();
        node = new(path, (cfg.Include ?? [])
            .Select(i =>
            {
                var loaded = TryLoadTree(i, out var child);
                return (loaded, child);
            })
            .Where(def => def.loaded)
            .Select(def => def.child)
            .ToArray(), master, shared, ignore);
        return true;
    }

    public static Definitions Load(string name)
    {
        if (!TryLoadTree($"{name}.yaml", out var node)) return new([], []);
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

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties 
                                | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
    private class Config
    {
        [YamlMember(Alias = "include")]
        public string[]? Include { get; set; } = [];
        [YamlMember(Alias = "shared")]
        public Link[]? Shared { get; set; } = [];
        [YamlMember(Alias = "master")]
        public Link[]? Master { get; set; } = [];
        [YamlMember(Alias = "ignore")]
        public string[]? Ignore { get; set; } = [];

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        public class Link
        {
            [YamlMember(Alias = "get")]
            public string Get { get; set; } = string.Empty;
            [YamlMember(Alias = "set")]
            public string? Set { get; set; }
            [YamlMember(Alias = "skp")]
            public string? Skp { get; set; }
        }
    }

    public IEnumerator<Definition> GetEnumerator() => _links.AsReadOnly().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count => _links.Length;
}

public record DefinitionNode(
    string Path,
    DefinitionNode[] Include,
    Definition[] Master,
    Definition[] Shared,
    string[] Ignore)
{
    public static readonly DefinitionNode Empty = new(string.Empty, [], [], [], []);
}

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
                Log.Error(e, "[Definitions] Unable to parse event expression {Name}", _set);
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
