using System.Text.Json;

namespace FsCopilot.Connection;

public static class JsonExtensions
{
    public static string String(this JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : fallback;
    
    public static string? StringOrNull(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : null;

    public static double Double(this JsonElement el, string prop, double fallback = 0)
        => el.TryGetProperty(prop, out var v) && v.TryGetDouble(out var d)
            ? d
            : fallback;

    public static void WritePrimitive(this Utf8JsonWriter w, string key, object v)
    {
        switch (v)
        {
            case string s: w.WriteString(key, s); return;
            case int i: w.WriteNumber(key, i); return;
            case long l: w.WriteNumber(key, l); return;
            case uint ui: w.WriteNumber(key, ui); return;
            case double d: w.WriteNumber(key, d); return;
            case float f: w.WriteNumber(key, f); return;
            case bool b: w.WriteBoolean(key, b); return;
            default: return;
        }
    }
}