namespace FsCopilot;

using System.IO.Compression;
using System.Net.Http.Headers;
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

    public async Task<GitHubReleaseInfo?> CheckForUpdateAsync(string version, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yury-sch/FsCopilot/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("FS-Copilot-Updater");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();

            if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(htmlUrl))
                return null;

            var latestVersion = ParseVersion(tagName);
            var currentVersion = ParseVersion(version);

            if (latestVersion is null || currentVersion is null)
                return null;

            return latestVersion > currentVersion
                ? new GitHubReleaseInfo(tagName, latestVersion, htmlUrl)
                : null;
        }
        catch
        {
            return null;
        }
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

    private static Version? ParseVersion(string value)
    {
        // Supports: v1.2.3, 1.2.3, 1.2.3-beta.1
        var cleaned = value.Trim();

        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[1..];

        var dashIndex = cleaned.IndexOf('-');
        if (dashIndex >= 0)
            cleaned = cleaned[..dashIndex];

        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}

public sealed record ProfileFile(string RelativePath, byte[] Content);

public sealed record GitHubReleaseInfo(string TagName, Version Version, string HtmlUrl);
