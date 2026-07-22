using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.IO;

namespace PolyChrom3MF.App;

public sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl);

public sealed class UpdateService
{
    const string LatestReleaseApi = "https://api.github.com/repos/suceunq/PolyChrom3MF/releases/latest";
    readonly HttpClient _client;

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any()) _client.DefaultRequestHeaders.UserAgent.ParseAdd("PolyChrom3MF-UpdateChecker");
    }

    public async Task<UpdateInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var latest = ParseRelease(json);
        return latest.Version > CurrentVersion() ? latest : null;
    }

    internal static UpdateInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.Trim() ?? throw new InvalidDataException("Version de mise à jour absente.");
        var releaseUrl = root.GetProperty("html_url").GetString()?.Trim() ?? throw new InvalidDataException("Adresse de mise à jour absente.");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidDataException($"Version de mise à jour invalide : {tag}");
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Adresse de mise à jour GitHub invalide.");
        return new UpdateInfo(version, tag, releaseUrl);
    }

    internal static Version CurrentVersion() => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
}
