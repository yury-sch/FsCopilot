namespace FsCopilot;

using System.Text.Json;

public sealed class Updater(string baseAddress)
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new(baseAddress)
    };

    public async Task<DateTime?> Check(string key, CancellationToken ct)
    {
        var url = $"/api/profiles/{Uri.EscapeDataString(key)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("[Application] Profile server unavailable.");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Bool("exists") ? doc.RootElement.GetProperty("published_at").GetDateTime() : null;
        }
        catch (Exception)
        {
            Log.Warning("[Application] Profile server unavailable.");
            return null;
        }
    }

    public async Task<byte[]?> Download(string key, CancellationToken ct)
    {
        var url = $"/api/profiles/{Uri.EscapeDataString(key)}/download";

        try
        {
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception)
        {
            Log.Warning("[Application] Profile {Profile} unavailable.", key);
            return null;
        }
    }
}
