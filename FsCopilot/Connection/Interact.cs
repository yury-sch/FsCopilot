using System.Text.Json.Serialization;

namespace FsCopilot.Connection;

public record Interact(
    [property: JsonPropertyName("instrument")] string Instrument,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] string? Value);