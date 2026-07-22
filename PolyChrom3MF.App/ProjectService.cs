using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public sealed record ProjectData(string SourcePath, int SelectedProposal, List<List<string>> Proposals, List<Dictionary<int,int>> Assignments, double Yaw, double Pitch, double Zoom, int Generation, bool FunMode = false, int ColorCount = 4, List<Dictionary<int, int[]>>? TriangleAssignments = null);

public sealed class ProjectService
{
    const long MaxProjectSize = 1024L * 1024 * 1024;
    const long MaxSettingsSize = 128L * 1024 * 1024;

    public void Save(string path, ModelDocument document, IReadOnlyList<ColorProposal> proposals, int selected, double yaw, double pitch, double zoom, int generation = 0, bool funMode = false, int colorCount = 4)
    {
        if (!File.Exists(document.Path)) throw new FileNotFoundException("Le modèle 3D source est introuvable.", document.Path);
        var extension = Path.GetExtension(document.Path).ToLowerInvariant();
        if (extension is not ".3mf" and not ".stl") throw new InvalidDataException("Le modèle du projet doit être un fichier 3MF ou STL.");
        var modelName = SafeFileName(Path.GetFileNameWithoutExtension(document.Path)) + extension;
        var modelEntryName = "model/" + modelName;
        var triangleAssignments = proposals.Select(p => p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())).ToList();
        var data = new ProjectData(modelEntryName, selected, proposals.Select(p => p.Colors.Select(c => c.Hex).ToList()).ToList(), proposals.Select(p => new Dictionary<int,int>(p.Assignments)).ToList(), yaw, pitch, zoom, generation, funMode, colorCount, triangleAssignments);
        Validate(data);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var settings = archive.CreateEntry("project.json", CompressionLevel.Optimal);
                using (var stream = settings.Open()) JsonSerializer.Serialize(stream, data, new JsonSerializerOptions { WriteIndented = true });
                var model = archive.CreateEntry(modelEntryName, extension == ".3mf" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var source = new FileStream(document.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var destination = model.Open();
                source.CopyTo(destination);
            }
            File.Move(temporary, fullPath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    public ProjectData Load(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > MaxProjectSize) throw new InvalidDataException("Projet PolyChrom absent, vide ou trop volumineux.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[2];
            if (input.Read(signature) != 2) throw new InvalidDataException("Projet PolyChrom incomplet.");
            input.Position = 0;
            return signature[0] == (byte)'P' && signature[1] == (byte)'K' ? LoadPortable(path, input) : LoadLegacy(input);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("Le projet PolyChrom est illisible ou corrompu.", ex);
        }
    }

    static ProjectData LoadPortable(string projectPath, Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count != 2) throw new InvalidDataException("Le projet PolyChrom contient des fichiers inattendus.");
        var settings = archive.GetEntry("project.json") ?? throw new InvalidDataException("Les réglages du projet sont absents.");
        if (settings.Length <= 0 || settings.Length > MaxSettingsSize) throw new InvalidDataException("Les réglages du projet sont invalides ou trop volumineux.");
        ProjectData data;
        using (var stream = settings.Open()) data = JsonSerializer.Deserialize<ProjectData>(stream) ?? throw new InvalidDataException("Projet PolyChrom invalide.");
        Validate(data);

        var modelEntry = archive.GetEntry(data.SourcePath);
        var modelName = Path.GetFileName(data.SourcePath);
        var extension = Path.GetExtension(modelName).ToLowerInvariant();
        if (modelEntry is null || modelEntry.Length <= 0 || modelEntry.Length > MaxProjectSize || data.SourcePath != "model/" + modelName || extension is not ".3mf" and not ".stl")
            throw new InvalidDataException("Le modèle 3D intégré au projet est absent ou invalide.");

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToUpperInvariant())))[..20];
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom 3MF", "SharedProjects", identity);
        Directory.CreateDirectory(folder);
        var extractedPath = Path.Combine(folder, modelName);
        var temporary = extractedPath + ".tmp";
        try
        {
            using (var source = modelEntry.Open())
            using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
            File.Move(temporary, extractedPath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
        return data with { SourcePath = extractedPath };
    }

    static ProjectData LoadLegacy(Stream input)
    {
        if (input.Length > MaxSettingsSize) throw new InvalidDataException("Ancien projet PolyChrom trop volumineux.");
        var data = JsonSerializer.Deserialize<ProjectData>(input) ?? throw new InvalidDataException("Projet PolyChrom invalide.");
        Validate(data);
        return data;
    }

    static void Validate(ProjectData data)
    {
        if (data is null || string.IsNullOrWhiteSpace(data.SourcePath) || data.Proposals is null || data.Assignments is null || data.Proposals.Count is < 1 or > 4 || data.Assignments.Count > 4 || data.TriangleAssignments?.Count > 4 || data.SelectedProposal < 0 || data.SelectedProposal >= data.Proposals.Count || data.ColorCount is < 4 or > 32 || !double.IsFinite(data.Yaw) || !double.IsFinite(data.Pitch) || !double.IsFinite(data.Zoom) || data.Zoom <= 0)
            throw new InvalidDataException("Le projet contient des paramètres invalides.");
        for (var p = 0; p < data.Proposals.Count; p++)
        {
            var colors = data.Proposals[p];
            if (colors is null || colors.Count is < 4 or > 32 || colors.Any(color => !Regex.IsMatch(color ?? "", "^#[0-9A-Fa-f]{6}$"))) throw new InvalidDataException("Le projet contient une palette invalide.");
            if (p < data.Assignments.Count && (data.Assignments[p] is null || data.Assignments[p].Any(pair => pair.Key < 0 || pair.Value < 0 || pair.Value >= colors.Count))) throw new InvalidDataException("Le projet contient une affectation de couleur invalide.");
            if (data.TriangleAssignments is not null && p < data.TriangleAssignments.Count && (data.TriangleAssignments[p] is null || data.TriangleAssignments[p].Any(pair => pair.Key < 0 || pair.Value is null || pair.Value.Any(value => value < 0 || value >= colors.Count)))) throw new InvalidDataException("Le projet contient une affectation de triangle invalide.");
        }
    }

    static string SafeFileName(string value)
    {
        var cleaned = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "modele" : cleaned;
    }
}
