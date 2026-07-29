using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PolyChrom3MF.App;

public sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string InstallerUrl, string Sha256, long Size, string ReleaseNotes);

public sealed class UpdateService
{
    public const string BundledReleaseNotes =
        "Version de stabilisation 2.0.15.\n" +
        "Le module expérimental d’importation et de placement d’images/logos est retiré de l’interface utilisateur.\n" +
        "Les boutons, menus, transformations et raccourcis associés ne sont plus accessibles tant que le module reconstruit n’est pas validé.\n" +
        "Les projets existants restent lisibles et conservent leurs couleurs déjà calculées.\n" +
        "La coloration 3MF/STL, les calques, la peinture, les palettes, les profils d’impression et les exports slicers restent disponibles.\n" +
        "La documentation a été alignée sur les fonctions réellement présentes dans cette version stable.";

    const string LatestReleaseApi = "https://api.github.com/repos/suceunq/PolyChrom3MF/releases/latest";
    const string InstallerName = "PolyChrom3MF_Setup_x64.exe";
    readonly HttpClient _client;

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any()) _client.DefaultRequestHeaders.UserAgent.ParseAdd("PolyChrom3MF-UpdateChecker");
    }

    public async Task<UpdateInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _client.GetAsync(LatestReleaseApi, timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        var latest = ParseRelease(json);
        return latest.Version > CurrentVersion() ? latest : null;
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var updateFolder = Path.Combine(Path.GetTempPath(), "PolyChrom3MF", "Updates", update.Tag);
        Directory.CreateDirectory(updateFolder);
        var destination = Path.Combine(updateFolder, InstallerName);
        var temporary = destination + ".download";
        try
        {
            using var response = await _client.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var expectedLength = response.Content.Headers.ContentLength ?? update.Size;
            if (expectedLength <= 0 || expectedLength > 250L * 1024 * 1024) throw new InvalidDataException("Taille de mise à jour invalide.");
            long received = 0;
            string actualDigest;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > expectedLength || received > 250L * 1024 * 1024) throw new InvalidDataException("La mise à jour reçue dépasse la taille annoncée.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(Math.Clamp(received * 100d / expectedLength, 0, 100));
                }
                await output.FlushAsync(cancellationToken);
                actualDigest = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (received != expectedLength) throw new InvalidDataException("Le téléchargement de la mise à jour est incomplet.");
            if (!DigestMatches(actualDigest, update.Sha256)) throw new InvalidDataException("La signature SHA-256 de la mise à jour ne correspond pas à la Release GitHub.");
            File.Move(temporary, destination, true);
            progress?.Report(100);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    internal static UpdateInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.Trim() ?? throw new InvalidDataException("Version de mise à jour absente.");
        var releaseUrl = ValidatedGitHubUrl(root.GetProperty("html_url").GetString(), "Adresse de Release GitHub invalide.");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidDataException($"Version de mise à jour invalide : {tag}");
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), InstallerName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Installateur Windows absent de la Release GitHub.");
        var installerUrl = ValidatedGitHubUrl(asset.GetProperty("browser_download_url").GetString(), "Adresse de téléchargement GitHub invalide.");
        var digest = asset.GetProperty("digest").GetString()?.Trim() ?? throw new InvalidDataException("Signature SHA-256 absente de la Release GitHub.");
        var size = asset.GetProperty("size").GetInt64();
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("Signature SHA-256 GitHub invalide.");
        if (size <= 0 || size > 250L * 1024 * 1024) throw new InvalidDataException("Taille de mise à jour invalide.");
        var notes = root.TryGetProperty("body", out var body) ? ReleaseSummary(body.GetString()) : "Améliorations et corrections de stabilité.";
        return new UpdateInfo(version, tag, releaseUrl, installerUrl, digest[7..].ToUpperInvariant(), size, notes);
    }

    static string ValidatedGitHubUrl(string? value, string error)
    {
        var url = value?.Trim() ?? throw new InvalidDataException(error);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(error);
        return url;
    }

    internal static string ReleaseSummary(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "Améliorations et corrections de stabilité.";
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(line => line.Trim().TrimStart('#').Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase))
            .Take(8);
        var summary = string.Join(Environment.NewLine, lines);
        if (string.IsNullOrWhiteSpace(summary)) return "Améliorations et corrections de stabilité.";
        return summary.Length <= 1000 ? summary : summary[..997] + "…";
    }

    internal static bool DigestMatches(string actual, string expected) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
    internal static Version CurrentVersion() => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
}
