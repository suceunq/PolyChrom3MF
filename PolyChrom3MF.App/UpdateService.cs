using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PolyChrom3MF.App;

public sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string InstallerUrl, string Sha256, long Size, string ReleaseNotes);

public sealed class UpdateService
{
    public const string BundledReleaseNotes =
        "Import et peinture 3D haute fidélité 2.1.3.\n" +
        "Les couleurs 3MF provenant de Bambu Studio, OrcaSlicer et des matériaux standard sont conservées à l’identique.\n" +
        "Le noir, le rouge et les autres filaments ne sont plus délavés dans l’aperçu.\n" +
        "Les outils triangle, cercle, rectangle, lasso, sphère et plage de hauteur disposent d’une zone de surbrillance fiable.\n" +
        "Le clic maintenu peint triangle par triangle sans recentrer la caméra.\n" +
        "La subdivision et l’historique de peinture sont plafonnés pour réduire la mémoire et conserver une interface fluide.";

    const string ManifestUrl = "https://raw.githubusercontent.com/suceunq/PolyChrom3MF/main/latest.json";
    const string InstallerName = "PolyChrom3MF_Setup_x64.exe";
    const int ManifestSchemaVersion = 1;
    const int MaxManifestBytes = 256 * 1024;
    const long MaxInstallerBytes = 250L * 1024 * 1024;
    const string UpdatePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKClMkpR+nsPPMsp3Jpf4Rd4hmeOon2oK8nSEue8ETSWFkfkmiBTlvoPkex9ftmcqbO/awGeoOPI02mnI+VHy+Q==";

    readonly HttpClient _client;

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("PolyChrom3MF-UpdateChecker/2");
    }

    public async Task<UpdateInfo?> GetAvailableUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _client.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        ValidateFinalResponseUri(response, "raw.githubusercontent.com", "/suceunq/PolyChrom3MF/main/latest.json");
        var json = await ReadLimitedContentAsync(response, MaxManifestBytes, timeout.Token);
        var latest = ParseSignedManifest(json);
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
            ValidateInstallerResponseUri(response);
            var expectedLength = response.Content.Headers.ContentLength ?? update.Size;
            if (expectedLength != update.Size || expectedLength <= 0 || expectedLength > MaxInstallerBytes)
                throw new InvalidDataException("Taille de mise à jour invalide.");
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
                    if (received > expectedLength || received > MaxInstallerBytes)
                        throw new InvalidDataException("La mise à jour reçue dépasse la taille annoncée.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(Math.Clamp(received * 100d / expectedLength, 0, 100));
                }
                await output.FlushAsync(cancellationToken);
                actualDigest = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (received != expectedLength) throw new InvalidDataException("Le téléchargement de la mise à jour est incomplet.");
            if (!DigestMatches(actualDigest, update.Sha256))
                throw new InvalidDataException("L’empreinte SHA-256 de la mise à jour ne correspond pas au manifeste signé.");
            await using (var executable = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z')
                    throw new InvalidDataException("Le fichier téléchargé n’est pas un installateur Windows valide.");
            }
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

    public string? CreateRollbackBackup()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;
        var version = CurrentVersion().ToString(3);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PolyChrom3MF",
            "Rollback");
        var folder = Path.Combine(root, version);
        Directory.CreateDirectory(folder);
        var backup = Path.Combine(folder, "PolyChrom3MF.exe");
        File.Copy(executable, backup, true);
        string hash;
        using (var backupStream = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(SHA256.HashData(backupStream));
        var metadata = JsonSerializer.Serialize(new
        {
            version,
            originalPath = Path.GetFullPath(executable),
            backupPath = Path.GetFullPath(backup),
            sha256 = hash,
            createdAtUtc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAtomically(Path.Combine(folder, "rollback.json"), metadata);
        PruneRollbackFolders(root, folder);
        return backup;
    }

    internal static UpdateInfo ParseSignedManifest(string json, string publicKey = UpdatePublicKey)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
            throw new InvalidDataException("Le manifeste de mise à jour est trop volumineux.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != ManifestSchemaVersion)
            throw new InvalidDataException("Version du manifeste de mise à jour non prise en charge.");
        var signatureText = RequiredString(root, "signature");
        byte[] signature;
        byte[] publicKeyBytes;
        try
        {
            signature = Convert.FromBase64String(signatureText);
            publicKeyBytes = Convert.FromBase64String(publicKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Signature du manifeste invalide.", ex);
        }
        var canonical = CanonicalManifestPayload(root);
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
            if (bytesRead != publicKeyBytes.Length ||
                !verifier.VerifyData(canonical, signature, HashAlgorithmName.SHA256))
                throw new InvalidDataException("La signature cryptographique du manifeste de mise à jour est invalide.");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("La signature cryptographique du manifeste de mise à jour est invalide.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicKeyBytes);
            CryptographicOperations.ZeroMemory(canonical);
        }

        var versionText = RequiredString(root, "version");
        var tag = RequiredString(root, "tag");
        if (!Version.TryParse(versionText, out var version) || version.Major < 1 ||
            !tag.Equals($"v{version.ToString(3)}", StringComparison.Ordinal))
            throw new InvalidDataException("Version de mise à jour invalide.");
        var minimumText = RequiredString(root, "minimumSupportedVersion");
        if (!Version.TryParse(minimumText, out var minimum) || minimum.Major < 1)
            throw new InvalidDataException("Version minimale invalide.");
        if (CurrentVersion() < minimum)
            throw new InvalidDataException($"Cette mise à jour nécessite au minimum PolyChrom 3MF {minimum.ToString(3)}.");
        if (!DateTimeOffset.TryParse(RequiredString(root, "publishedAtUtc"), out var publishedAt) ||
            publishedAt > DateTimeOffset.UtcNow.AddHours(24))
            throw new InvalidDataException("Date de publication du manifeste invalide.");

        var releaseUrl = ValidateRepositoryUrl(RequiredString(root, "releaseUrl"), $"/suceunq/PolyChrom3MF/releases/tag/{tag}");
        var installerUrl = ValidateRepositoryUrl(
            RequiredString(root, "installerUrl"),
            $"/suceunq/PolyChrom3MF/releases/download/{tag}/{InstallerName}");
        var digest = RequiredString(root, "installerSha256");
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException("Empreinte SHA-256 invalide.");
        var size = root.GetProperty("installerSize").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("Taille de mise à jour invalide.");
        var notes = ReleaseSummary(RequiredString(root, "releaseNotes"));
        return new UpdateInfo(version, tag, releaseUrl, installerUrl, digest.ToUpperInvariant(), size, notes);
    }

    internal static byte[] CanonicalManifestPayload(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", root.GetProperty("schemaVersion").GetInt32());
            writer.WriteString("version", RequiredString(root, "version"));
            writer.WriteString("tag", RequiredString(root, "tag"));
            writer.WriteString("releaseUrl", RequiredString(root, "releaseUrl"));
            writer.WriteString("installerUrl", RequiredString(root, "installerUrl"));
            writer.WriteString("installerSha256", RequiredString(root, "installerSha256"));
            writer.WriteNumber("installerSize", root.GetProperty("installerSize").GetInt64());
            writer.WriteString("publishedAtUtc", RequiredString(root, "publishedAtUtc"));
            writer.WriteString("minimumSupportedVersion", RequiredString(root, "minimumSupportedVersion"));
            writer.WriteString("releaseNotes", RequiredString(root, "releaseNotes"));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Champ obligatoire absent du manifeste : {name}.");
        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Champ obligatoire vide dans le manifeste : {name}.")
            : value.Trim();
    }

    static string ValidateRepositoryUrl(string value, string expectedPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Adresse GitHub invalide dans le manifeste.");
        return uri.AbsoluteUri;
    }

    static void ValidateFinalResponseUri(HttpResponseMessage response, string expectedHost, string expectedPath)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException("La réponse réseau provient d’une adresse inattendue.");
    }

    static void ValidateInstallerResponseUri(HttpResponseMessage response)
    {
        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Le téléchargement a été redirigé vers une adresse non autorisée.");
    }

    static async Task<string> ReadLimitedContentAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new InvalidDataException("La réponse réseau est trop volumineuse.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("La réponse réseau est trop volumineuse.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    static void PruneRollbackFolders(string root, string currentFolder)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root)
                         .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Skip(1))
                Directory.Delete(folder, true);
        }
        catch
        {
            // Une sauvegarde ancienne non supprimable ne doit jamais bloquer une mise à jour valide.
        }
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

    internal static bool DigestMatches(string actual, string expected)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static Version CurrentVersion() => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
}
