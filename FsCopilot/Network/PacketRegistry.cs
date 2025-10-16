namespace FsCopilot.Network;

using System.Diagnostics.CodeAnalysis;

internal interface IPacketCodecAdapter
{
    void Encode(object packet, BinaryWriter bw);
    object Decode(BinaryReader br);
}

internal sealed class PacketRegistry
{
    private readonly List<IPacketCodecAdapter> _byId = [];
    private readonly Dictionary<Type, byte> _idByType = new();

    public void RegisterPacket<TPacket, TCodec>()
        where TCodec : IPacketCodec<TPacket>, new()
    {
        var t = typeof(TPacket);
        if (_idByType.TryGetValue(t, out _)) return;

        var id = _byId.Count;
        var codec = new TCodec();
        var adapter = new CodecAdapter<TPacket>(codec);

        _byId.Add(adapter);
        _idByType.Add(t, (byte)id);
    }

    public bool TryGetCodec<TPacket>(out byte id, [MaybeNullWhen(false)] out IPacketCodecAdapter codec)
    {
        codec = null;
        return _idByType.TryGetValue(typeof(TPacket), out id) && TryGetCodec(id, out codec);
    }

    public bool TryGetCodec(byte id, [MaybeNullWhen(false)] out IPacketCodecAdapter codec)
    {
        codec = id < (uint)_byId.Count ? _byId[id] : null;
        return codec is not null;
    }

    private sealed class CodecAdapter<T>(IPacketCodec<T> inner) : IPacketCodecAdapter
    {
        public void Encode(object packet, BinaryWriter bw) => inner.Encode((T)packet, bw);
        public object Decode(BinaryReader br) => inner.Decode(br);
    }
}