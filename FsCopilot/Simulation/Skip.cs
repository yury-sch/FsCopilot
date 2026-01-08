namespace FsCopilot.Simulation;

public static class Skip
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly TimeSpan ResetAfter = TimeSpan.FromSeconds(2);

    private sealed class Entry
    {
        public int Count;
        public DateTime LastAdded;
    }

    public static void Next(string name)
    {
        var now = DateTime.UtcNow;
        Entries.AddOrUpdate(name,
            _ => new() { Count = 1, LastAdded = now },
            (_, entry) =>
            {
                entry.Count += 1;
                entry.LastAdded = now;
                return entry;
            });
    }

    public static bool Should(string name)
    {
        if (!Entries.TryGetValue(name, out var entry)) return false;

        var now = DateTime.UtcNow;
        var elapsed = now - entry.LastAdded;

        if (elapsed > ResetAfter)
        {
            Log.Warning("Skip counter for '{Name}' expired ({Elapsed:0.00}s > {Limit:0.00}s). Possible desync detected.",
                name, elapsed.TotalSeconds, ResetAfter.TotalSeconds);
            Entries.TryRemove(name, out _);
            return false;
        }

        if (entry.Count <= 0) return false;
        if (--entry.Count <= 0) Entries.TryRemove(name, out _);
        return true;

    }
}