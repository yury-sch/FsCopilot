namespace FsCopilot.Network;

using Microsoft.Extensions.Caching.Memory;

public class SeenCache(TimeSpan seenTtl, long sizeLimit = 10_000) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = sizeLimit
    });

    public bool TryMarkSeen(Guid msgId)
    {
        var created = false;
        _cache.GetOrCreate(msgId, entry =>
        {
            entry.SetAbsoluteExpiration(seenTtl);
            entry.SetSize(1);
            created = true;
            return true;
        });
        return created;
    }

    public void Dispose() => _cache.Dispose();
}