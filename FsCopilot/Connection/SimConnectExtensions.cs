namespace FsCopilot.Connection;

using System.Reflection;
using Microsoft.FlightSimulator.SimConnect;

public static class SimConnectExtensions
{
    public static void AddToDataDefinitionFromStruct<T>(this SimConnect sim, Enum defId)
        where T : struct
    {
        var fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(f => (f, attr: f.GetCustomAttribute<SimVarAttribute>()))
            .Where(x => x.attr != null)
            .OrderBy(x => x.attr!.Order)
            .ThenBy(x => x.f.MetadataToken)
            .ToArray();

        foreach (var (_, attr) in fields)
        {
            var dt = InferDataType(attr!.Units);
            // var dt = InferDataType(f.FieldType, attr!.Units);
            sim.AddToDataDefinition(defId, attr!.Name, attr!.Units, dt, 0f, SimConnect.SIMCONNECT_UNUSED);
        }
    }
    
    public static Type ToClrType(SIMCONNECT_DATATYPE dt) => dt switch
    {
        SIMCONNECT_DATATYPE.FLOAT64 => typeof(double),
        SIMCONNECT_DATATYPE.FLOAT32 => typeof(float),

        SIMCONNECT_DATATYPE.INT64 => typeof(long),
        SIMCONNECT_DATATYPE.INT32 => typeof(int),
        SIMCONNECT_DATATYPE.INT8 => typeof(sbyte),

        SIMCONNECT_DATATYPE.STRING8 => typeof(string),
        SIMCONNECT_DATATYPE.STRING32 => typeof(string),
        SIMCONNECT_DATATYPE.STRING64 => typeof(string),
        SIMCONNECT_DATATYPE.STRING128 => typeof(string),
        SIMCONNECT_DATATYPE.STRING256 => typeof(string),
        SIMCONNECT_DATATYPE.STRINGV => typeof(string),
        _ => throw new NotSupportedException($"SIMCONNECT_DATATYPE {dt} не поддержан маппером.")
    };
    
    public static SIMCONNECT_DATATYPE InferDataType(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return SIMCONNECT_DATATYPE.STRING256;

        var u = unit.Trim().ToLowerInvariant();

        // Lowercase pseudo-units (it is better to specify an explicit type)
        switch (u)
        {
            case "string8":   return SIMCONNECT_DATATYPE.STRING8;
            case "string32":  return SIMCONNECT_DATATYPE.STRING32;
            case "string64":  return SIMCONNECT_DATATYPE.STRING64;
            case "string256": return SIMCONNECT_DATATYPE.STRING256;
            case "stringv":   return SIMCONNECT_DATATYPE.STRINGV;
            // It is sometimes found at the docks:
            case "wstring":
            case "wstring256": return SIMCONNECT_DATATYPE.STRING256;
        }

        // Frequent text labels that are actually strings
        switch (u)
        {
            case "string":
            case "text":
            case "title":
            case "name":
            case "model":
            case "category":
            case "airline":
            case "icao":
            case "registration":
            case "filename":
                return SIMCONNECT_DATATYPE.STRING256;
        }

        // Boolean/indexes/flags/enum — usually INT32
        switch (u)
        {
            case "bool":
            case "boolean":
            case "index":
            case "enum":
            case "mask":
            case "bitmask":
            case "flag":
            case "flags":
            case "count":
            // case "number":
            case "items":
            case "state":
            case "switch":
            case "position index":
            case "position step":
            case "bcd16":
            case "bco16":
                return SIMCONNECT_DATATYPE.INT32;
        }

        return SIMCONNECT_DATATYPE.FLOAT64;
    }
}