using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

// Génère et signe latest.json pour le système de mise à jour de PolyChrom 3MF.
//
// Utilisation (après la publication de la Release GitHub par le workflow) :
//   dotnet run --project tools/SignUpdateManifest -- --version 2.1.4 --key chemin/cle-privee.pem
//
// Options :
//   --version  numéro de version publié (obligatoire)
//   --key      clé privée ECDSA P-256 au format PEM (PKCS#8 ou EC), obligatoire. Elle n'est jamais copiée ni affichée.
//   --installer fichier local ou URL https de l'installateur (défaut : l'asset de la Release GitHub)
//   --notes    fichier de notes (défaut : RELEASE_NOTES_<version>.md à la racine du dépôt)
//   --minimum  version minimale pouvant se mettre à jour (défaut : 2.1.2)
//   --out      fichier de sortie (défaut : latest.json)
//   --allow-other-key  autorise une clé différente de celle intégrée à l'application (tests uniquement)

const string Repository = "suceunq/PolyChrom3MF";
const string InstallerName = "PolyChrom3MF_Setup_x64.exe";
const long MaxInstallerBytes = 250L * 1024 * 1024;
// Clé publique intégrée à UpdateService.cs : une signature faite avec une autre clé serait refusée par l'application.
const string EmbeddedPublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKClMkpR+nsPPMsp3Jpf4Rd4hmeOon2oK8nSEue8ETSWFkfkmiBTlvoPkex9ftmcqbO/awGeoOPI02mnI+VHy+Q==";

try
{
    var options = ParseArguments(args);
    var version = Required(options, "version");
    if (!Version.TryParse(version, out var parsed) || parsed.Major < 1)
        throw new InvalidOperationException($"Version invalide : {version}");
    var tag = $"v{parsed.ToString(3)}";
    var keyPath = Required(options, "key");
    var minimum = options.GetValueOrDefault("minimum", "2.1.2");
    var output = options.GetValueOrDefault("out", "latest.json");
    var notesPath = options.GetValueOrDefault("notes", $"RELEASE_NOTES_{parsed.ToString(3)}.md");
    var installer = options.GetValueOrDefault("installer", $"https://github.com/{Repository}/releases/download/{tag}/{InstallerName}");

    using var signer = ECDsa.Create();
    signer.ImportFromPem(File.ReadAllText(keyPath));
    if (signer.KeySize != 256)
        throw new InvalidOperationException("La clé doit être une clé ECDSA P-256.");
    var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
    if (publicKey != EmbeddedPublicKey && !options.ContainsKey("allow-other-key"))
        throw new InvalidOperationException(
            "Cette clé ne correspond pas à la clé publique intégrée à l'application : la mise à jour serait refusée par les utilisateurs. Aucun fichier écrit.");

    var installerBytes = await ReadInstallerAsync(installer);
    if (installerBytes.Length < 1024 || installerBytes[0] != (byte)'M' || installerBytes[1] != (byte)'Z')
        throw new InvalidOperationException("Le fichier d'installation n'est pas un exécutable Windows valide.");
    var sha256 = Convert.ToHexString(SHA256.HashData(installerBytes));

    var notes = File.ReadAllText(notesPath, Encoding.UTF8).Trim();
    if (notes.Length == 0) throw new InvalidOperationException($"Notes de version vides : {notesPath}");

    var fields = new Dictionary<string, object>
    {
        ["version"] = parsed.ToString(3),
        ["tag"] = tag,
        ["releaseUrl"] = $"https://github.com/{Repository}/releases/tag/{tag}",
        ["installerUrl"] = $"https://github.com/{Repository}/releases/download/{tag}/{InstallerName}",
        ["installerSha256"] = sha256,
        ["installerSize"] = (long)installerBytes.Length,
        ["publishedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["minimumSupportedVersion"] = minimum,
        ["releaseNotes"] = notes,
    };

    var signature = Convert.ToBase64String(signer.SignData(CanonicalPayload(fields), HashAlgorithmName.SHA256));
    File.WriteAllText(output, SerializeManifest(fields, signature) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"OK : {output} signé ({tag}, {installerBytes.Length:N0} octets, SHA-256 {sha256[..12]}…).");
    Console.WriteLine("Publiez ce fichier sur la branche main pour distribuer la mise à jour.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERREUR : {ex.Message}");
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException($"Argument inattendu : {args[i]}");
        var name = args[i][2..];
        if (name == "allow-other-key") { result[name] = "1"; continue; }
        if (++i >= args.Length) throw new InvalidOperationException($"Valeur manquante pour --{name}");
        result[name] = args[i];
    }
    return result;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Option obligatoire manquante : --{name}");

static async Task<byte[]> ReadInstallerAsync(string source)
{
    if (!source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        var info = new FileInfo(source);
        if (!info.Exists || info.Length > MaxInstallerBytes) throw new InvalidOperationException($"Installateur introuvable ou trop volumineux : {source}");
        return await File.ReadAllBytesAsync(source);
    }
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PolyChrom3MF-ManifestSigner/1");
    using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > MaxInstallerBytes) throw new InvalidOperationException("Installateur trop volumineux.");
    return await response.Content.ReadAsByteArrayAsync();
}

// Même sérialisation canonique que UpdateService.CanonicalManifestPayload : champs dans cet ordre exact.
static byte[] CanonicalPayload(Dictionary<string, object> f)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("version", (string)f["version"]);
        writer.WriteString("tag", (string)f["tag"]);
        writer.WriteString("releaseUrl", (string)f["releaseUrl"]);
        writer.WriteString("installerUrl", (string)f["installerUrl"]);
        writer.WriteString("installerSha256", (string)f["installerSha256"]);
        writer.WriteNumber("installerSize", (long)f["installerSize"]);
        writer.WriteString("publishedAtUtc", (string)f["publishedAtUtc"]);
        writer.WriteString("minimumSupportedVersion", (string)f["minimumSupportedVersion"]);
        writer.WriteString("releaseNotes", (string)f["releaseNotes"]);
        writer.WriteEndObject();
    }
    return stream.ToArray();
}

static string SerializeManifest(Dictionary<string, object> f, string signature)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        foreach (var key in new[] { "version", "tag", "releaseUrl", "installerUrl", "installerSha256" }) writer.WriteString(key, (string)f[key]);
        writer.WriteNumber("installerSize", (long)f["installerSize"]);
        foreach (var key in new[] { "publishedAtUtc", "minimumSupportedVersion", "releaseNotes" }) writer.WriteString(key, (string)f[key]);
        writer.WriteString("signature", signature);
        writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(stream.ToArray());
}
