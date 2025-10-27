namespace FsCopilot.Network;

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public static class SchemaFingerprint
{
    public static string For<T>() => For(typeof(T));
    
    public static string For(Type t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Type:{t.AssemblyQualifiedName}");

        if (t.IsValueType)
        {
            // Struct path (same guarantees as before).
            var sla = t.StructLayoutAttribute ?? new StructLayoutAttribute(LayoutKind.Sequential);
            sb.AppendLine($"Layout:{sla.Value};Pack:{sla.Pack};CharSet:{sla.CharSet}");
            sb.AppendLine($"Size:{Marshal.SizeOf(t)}");

            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => new { Field = f, Offset = Marshal.OffsetOf(t, f.Name).ToInt32() })
                .OrderBy(x => x.Offset);

            foreach (var x in fields)
                sb.AppendLine($"F:{x.Offset}:{x.Field.FieldType.AssemblyQualifiedName}:{x.Field.Name}");
        }
        else
        {
            // Class/record path:
            // Prefer primary constructor parameter order (records),
            // fallback to instance readable properties by metadata token.
            var props = GetOrderedContractProperties(t);

            foreach (var p in props)
                sb.AppendLine($"P:{p.Name}:{p.PropertyType.AssemblyQualifiedName}");
        }

        // Hash to hex (stable)
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash); // 64 hex chars
    }
    
    private static PropertyInfo[] GetOrderedContractProperties(Type t)
    {
        const BindingFlags BF =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Try primary constructor (record/class with positional params)
        var ctor = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        var allProps = t.GetProperties(BF)
            .Where(p => p.GetMethod != null && !p.GetMethod.IsStatic)
            .ToArray();

        if (ctor != null && ctor.GetParameters().Length > 0)
        {
            var map = allProps.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
            var ordered = ctor.GetParameters()
                .Select(p => map.TryGetValue(p.Name!, out var pi) ? pi : null)
                .Where(pi => pi != null)!
                .ToList();

            // Append any leftover properties deterministically
            ordered.AddRange(allProps.Except(ordered).OrderBy(p => p.MetadataToken));
            return ordered.ToArray();
        }

        // Fallback: by metadata token (stable within assembly build)
        return allProps.OrderBy(p => p.MetadataToken).ToArray();
    }
}