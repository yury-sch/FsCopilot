namespace FsCopilot.Simulation;

using DynamicExpresso;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class Definitions
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // .IgnoreUnmatchedProperties() 
        .Build();

    public SimVarDefinition[] SimVars { get; }

    private Definitions(SimVarDefinition[] simVars)
    {
        SimVars = simVars;
    }

    public static Definitions Load(string name)
    {
        var cfgName = File.Exists(Path.Combine(AppContext.BaseDirectory, "Definitions", $"{name}.yaml"))
            ? $"{name}.yaml"
            : "default.yaml";

        var cfg = LoadRecursive(cfgName);
        var simVars = cfg.Master.SimVars.Select(v => (Shared: false, Var: v))
            .Concat(cfg.Shared.SimVars.Select(v => (Shared: true, Var: v)))
            .Select(v => new SimVarDefinition(v.Shared, v.Var.Name, v.Var.Units, v.Var.Event, v.Var.Transform))
            .ToArray();
        return new(simVars);
    }

    private static Config LoadRecursive(params string[] path)
    {
        var cfgFile = File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Definitions", ..path]));
        var cfg = Deserializer.Deserialize<Config>(cfgFile);
        if (cfg.Include.Length == 0) return cfg;

        foreach (var include in cfg.Include)
        {
            var innerCfg = LoadRecursive(include.Split('/'));
            cfg.Shared.SimVars = innerCfg.Shared.SimVars.Concat(cfg.Shared.SimVars).ToArray();
            cfg.Master.SimVars = innerCfg.Master.SimVars.Concat(cfg.Master.SimVars).ToArray();
        }

        return cfg;
    }

    private class Config
    {
        public string[] Include { get; set; } = [];
        public Level Shared { get; set; } = new();

        public Level Master { get; set; } = new();
        // public Map[] Server { get; set; } = [];
        // public string[] Ignore { get; set; } = [];

        public class Level
        {
            public SimVar[] SimVars { get; set; } = [];
        }

        public class SimVar
        {
            public string Name { get; set; }
            public string Units { get; set; }
            public string? Event { get; set; }
            public string? Transform { get; set; }
        }
    }
}

public class SimVarDefinition(bool shared, string name, string units, string? @event, string? transform)
{
    public bool Shared => shared;
    public string Name => name;
    public string Units => units;

    public bool TryGetEvent(object value, out string eventName, out int paramIx)
    {
        if (@event == null)
        {
            paramIx = 0;
            eventName = string.Empty;
            return false;
        }

        eventName = @event.Trim();
        paramIx = 0;
        if (eventName.Contains(' '))
        {
            try
            {
                var expr = eventName.Replace("'", "\"");
                eventName = new Interpreter()
                    .ParseAsDelegate<Func<object, string>>(expr, "value")
                    .Invoke(value);
            }
            catch (Exception e)
            {
                Log.Error(e, "Unable to parse event {Name}", eventName);
                return false;
            }
        }

        if (!eventName.Contains(':')) return true;

        var parts = eventName.Split(':', 2);
        if (parts.Length == 2 && int.TryParse(parts[1], out var n))
        {
            eventName = parts[0];
            paramIx = n;
        }
        else
        {
            Log.Error("Unable to parse event {Name}", eventName);
            return false;
        }

        return true;
    }

    public object TransformValue(object value)
    {
        if (transform == null) return value;

        try
        {
            return new Interpreter()
                .ParseAsDelegate<Func<object, object>>(transform, "value")
                .Invoke(value);
        }
        catch (Exception e)
        {
            Log.Error(e, "Unable to modify {Name} value {Value} with expression {Expression}", Name, value, transform);
            return value;
        }
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