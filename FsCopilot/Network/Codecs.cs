namespace FsCopilot.Network;

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteNetLib;

internal interface IPacketCodecAdapter
{
    void Encode(object packet, BinaryWriter bw);
    object Decode(BinaryReader br);
}

internal sealed class Codecs
{
    private readonly List<IPacketCodecAdapter> _byId = [];
    private readonly Dictionary<Type, byte> _idByType = new();
    
    public string Schema { get; private set; } = string.Empty;

    public Codecs Add<TPacket, TCodec>()
        where TCodec : IPacketCodec<TPacket>, new()
    {
        var t = typeof(TPacket);
        if (_idByType.TryGetValue(t, out _)) return this;

        var id = _byId.Count;
        var codec = new TCodec();
        var adapter = new CodecAdapter<TPacket>(codec);

        _byId.Add(adapter);
        _idByType.Add(t, (byte)id);
        
        Schema += SchemaFor<TPacket>();
        return this;
    }

    private bool TryGet<TPacket>(out byte id, [MaybeNullWhen(false)] out IPacketCodecAdapter codec)
    {
        codec = null;
        return _idByType.TryGetValue(typeof(TPacket), out id) && TryGet(id, out codec);
    }

    private bool TryGet(byte id, [MaybeNullWhen(false)] out IPacketCodecAdapter codec)
    {
        codec = id < (uint)_byId.Count ? _byId[id] : null;
        return codec is not null;
    }

    public byte[] Encode<TPacket>(TPacket packet) where TPacket: notnull
    {
        if (!TryGet<TPacket>(out var packetId, out var codec)) return [];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, true);
        bw.Write(packetId);
        codec.Encode(packet, bw);
        bw.Flush();
        return ms.ToArray();
    }

    public object? Decode(NetPacketReader reader)
    {
        var length = reader.AvailableBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            reader.GetBytes(buffer, length);

            using var ms = new MemoryStream(buffer, 0, length, writable: false, publiclyVisible: true);
            using var br = new BinaryReader(ms, Encoding.UTF8, true);

            var packetType = br.ReadByte();
            if (!TryGet(packetType, out var codec)) return null;

            try
            {
                return codec.Decode(br);
            }
            catch (Exception)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reader.Recycle();
        }
    }

    private sealed class CodecAdapter<T>(IPacketCodec<T> inner) : IPacketCodecAdapter
    {
        public void Encode(object packet, BinaryWriter bw) => inner.Encode((T)packet, bw);
        public object Decode(BinaryReader br) => inner.Decode(br);
    }
    
    private static string SchemaFor<T>() => SchemaFor(typeof(T));
    
    private static string SchemaFor(Type t)
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