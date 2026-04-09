namespace FsCopilot;

using System.IO.Compression;
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

    public async Task<IReadOnlyCollection<ProfileFile>> Download(CancellationToken ct)
    {
        const string url = "/api/profiles/download";

        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var zipBuffer = await _http.GetByteArrayAsync(url, ct);
                return ExtractProfiles(zipBuffer);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Warning("[Application] Profiles download failed. Retrying in {Delay}s. {Error}",
                    delay.TotalSeconds, e.Message);

                await Task.Delay(delay, ct);

                // exponential backoff
                delay = TimeSpan.FromSeconds(
                    Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }

        return [];
    }

    private static List<ProfileFile> ExtractProfiles(byte[] zipBuffer)
    {
        using var ms = new MemoryStream(zipBuffer);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var files = new List<ProfileFile>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue; // directory

            var extension = Path.GetExtension(entry.FullName);
            if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var fileStream = new MemoryStream();
            entryStream.CopyTo(fileStream);

            files.Add(new ProfileFile(
                NormalizeRelativePath(entry.FullName),
                fileStream.ToArray()));
        }

        return files;
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');

        // Prevent weird zip-slip style paths
        while (normalized.StartsWith("../", StringComparison.Ordinal))
            normalized = normalized[3..];

        return normalized;
    }
}

public sealed record ProfileFile(string RelativePath, byte[] Content);
