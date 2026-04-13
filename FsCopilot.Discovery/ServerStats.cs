namespace P2PDiscovery;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(ServerStats.Model))]
internal partial class ServerStatsJsonContext : JsonSerializerContext;

public sealed class ServerStats
{
    private int _users;
    private int _relaySessions;

    public int P2PUsers => Volatile.Read(ref _users);
    public int RelaySessions => Volatile.Read(ref _relaySessions);

    public void SetP2PUsers(int value) => Interlocked.Exchange(ref _users, value);
    public void SetRelayLinks(int value) => Interlocked.Exchange(ref _relaySessions, value);

    public string ToJson()
    {
        var model = new Model { Users = P2PUsers, RelaySessions = RelaySessions };
        return JsonSerializer.Serialize(model, ServerStatsJsonContext.Default.Model);
    }

    public sealed class Model
    {
        [JsonPropertyName("users")]
        public int Users { get; init; }
        [JsonPropertyName("relay_sessions")]
        public int RelaySessions { get; init; }
    }
}
