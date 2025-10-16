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
            case "number":
            case "items":
            case "state":
            case "switch":
            case "position index":
            case "position step":
                return SIMCONNECT_DATATYPE.INT32;
        }

        // Everything below is numeric with floating point → FLOAT64

        if (u is "radian" or "radians" or "rad" or
               "degree" or "degrees" or "deg" or
               "grad" or "grads")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "radians per second" or "rad/s" or
               "degrees per second" or "deg/s")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "feet" or "foot" or "ft" or
               "inch" or "inches" or "in" or
               "meter" or "meters" or "m" or
               "centimeter" or "centimeters" or "cm" or
               "millimeter" or "millimeters" or "mm" or
               "kilometer" or "kilometers" or "km" or
               "nautical mile" or "nautical miles" or "nm" or
               "mile" or "miles" or "statute miles" or "sm")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "knots" or "knot" or "kt" or "kts" or
               "mach" or
               "meters per second" or "meter per second" or "m/s" or
               "feet per second" or "ft/s" or
               "kilometers per hour" or "km/h" or
               "miles per hour" or "mph")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "feet per second per second" or "ft/s^2" or
               "meters per second squared" or "m/s^2" or
               "g" or "gforce")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "celsius" or "centigrade" or
               "fahrenheit" or
               "kelvin" or "k" or
               "rankine")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "inhg" or "in hg" or
               "psi" or
               "pascals" or "pascal" or "pa" or
               "hectopascal" or "hectopascals" or "hpa" or
               "millibar" or "mbar" or
               "bar" or
               "atm")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "pound" or "pounds" or "lb" or "lbs" or
               "kilogram" or "kilograms" or "kg" or
               "newton" or "newtons" or "n" or
               "slug" or "slugs")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "gallon" or "gallons" or "gal" or
               "liter" or "liters" or "litre" or "litres" or "l" or
               "quart" or "quarts" or "qt" or
               "gallons per hour" or "gph" or
               "pounds per hour" or "pph" or
               "liters per hour" or "l/h" or
               "kilograms per hour" or "kg/h")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "kg/m^3" or "kg per cubic meter" or
               "lb/ft^3" or "pounds per cubic foot")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "volt" or "volts" or "v" or
               "ampere" or "amperes" or "amp" or "amps" or "a" or
               "watt" or "watts" or "w" or
               "kilowatt" or "kilowatts" or "kw" or
               "ampere hour" or "ampere hours" or "ah" or
               "watt hour" or "watt hours" or "wh" or
               "kilovolt-ampere" or "kva")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "hertz" or "hz" or
               "kilohertz" or "khz" or
               "megahertz" or "mhz")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "second" or "seconds" or "sec" or "s" or
               "minute" or "minutes" or "min" or
               "hour" or "hours" or "hr" or "hrs" or
               "day" or "days")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "percent" or "%" or
               "percent over 100" or
               "ratio" or
               "scalar" or
               "per unit" or "pu")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "position 16k" or "position -16383..16384" or
               "normalized" or "normalized (-1 to 1)" or
               "normalized (0 to 1)")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "meters visibility" or
               "statute miles" or "sm visibility" or
               "nautical miles visibility")
            return SIMCONNECT_DATATYPE.FLOAT64;

        if (u is "latitude" or "longitude")
            return SIMCONNECT_DATATYPE.FLOAT64;

        return SIMCONNECT_DATATYPE.FLOAT64;
    }
}