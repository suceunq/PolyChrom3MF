using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public sealed record ProjectData(string SourcePath, int SelectedProposal, List<List<string>> Proposals, List<Dictionary<int,int>> Assignments, double Yaw, double Pitch, double Zoom, int Generation, bool FunMode = false, int ColorCount = 4, List<Dictionary<int, int[]>>? TriangleAssignments = null, PatternSettings? Pattern = null, List<string>? ProposalNames = null, List<string>? ProposalDescriptions = null, List<List<ColorLayer>>? Layers = null, List<Dictionary<int, int>>? LayerBaseAssignments = null, List<Dictionary<int, int[]>>? LayerBaseTriangles = null);

public sealed class ProjectService
{
    public void Save(string path, ModelDocument document, IReadOnlyList<ColorProposal> proposals, int selected, double yaw, double pitch, double zoom, int generation = 0, bool funMode = false, int colorCount = 4, PatternSettings? pattern = null, IReadOnlyList<IReadOnlyList<ColorLayer>>? layers = null, IReadOnlyList<ColorProposal>? layerBases = null)
    {
        if (!File.Exists(document.Path)) throw new FileNotFoundException("Le modèle 3D source est introuvable.", document.Path);
        string? derivedSnapshot = null;
        var modelSource = document.Path;
        if (document.IsDerived)
        {
            derivedSnapshot = Path.Combine(Path.GetTempPath(), $"PolyChrom-derived-{Guid.NewGuid():N}.3mf");
            try { new ThreeMfService().ExportAndValidate(document, proposals[Math.Clamp(selected, 0, proposals.Count - 1)], derivedSnapshot, false); }
            catch
            {
                try { if (File.Exists(derivedSnapshot)) File.Delete(derivedSnapshot); } catch { }
                throw;
            }
            modelSource = derivedSnapshot;
        }
        var extension = Path.GetExtension(modelSource).ToLowerInvariant();
        if (extension is not ".3mf" and not ".stl") throw new InvalidDataException("Le modèle du projet doit être un fichier 3MF ou STL.");
        var modelName = SafeFileName(Path.GetFileNameWithoutExtension(document.Path)) + extension;
        var modelEntryName = "model/" + modelName;
        var triangleAssignments = proposals.Select(p => p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())).ToList();
        var patternSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextPattern = 1;
        PatternSettings? StorePattern(PatternSettings? value)
        {
            if (value is null) return null;
            PatternService.ValidateSettings(value);
            var image = new FileInfo(value.ImagePath);
            if (!image.Exists || image.Length <= 0 || !image.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Le PNG d’un motif est absent ou invalide.");
            var sourcePath = image.FullName;
            if (!patternSources.TryGetValue(sourcePath, out var entryPath))
            {
                entryPath = $"pattern/motif-{nextPattern++:000}.png";
                patternSources[sourcePath] = entryPath;
            }
            return value with { ImagePath = entryPath };
        }
        var storedPattern = StorePattern(pattern);
        var storedLayers = layers?.Select(group => group.Select(layer => layer with
        {
            TriangleOverrides = layer.TriangleOverrides.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()),
            Pattern = StorePattern(layer.Pattern)
        }).ToList()).ToList();
        var baseAssignments = layerBases?.Select(p => new Dictionary<int, int>(p.Assignments)).ToList();
        var baseTriangles = layerBases?.Select(p => p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())).ToList();
        var data = new ProjectData(modelEntryName, selected, proposals.Select(p => p.Colors.Select(c => c.Hex).ToList()).ToList(), proposals.Select(p => new Dictionary<int,int>(p.Assignments)).ToList(), yaw, pitch, zoom, generation, funMode, colorCount, triangleAssignments, storedPattern, proposals.Select(p => p.Name).ToList(), proposals.Select(p => p.Description).ToList(), storedLayers, baseAssignments, baseTriangles);
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
                using (var stream = settings.Open()) JsonSerializer.Serialize(stream, data);
                var model = archive.CreateEntry(modelEntryName, extension == ".3mf" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using (var source = new FileStream(modelSource, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = model.Open()) source.CopyTo(destination);
                foreach (var asset in patternSources)
                {
                    var patternEntry = archive.CreateEntry(asset.Value, CompressionLevel.Optimal);
                    using (var patternSource = new FileStream(asset.Key, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var patternDestination = patternEntry.Open()) patternSource.CopyTo(patternDestination);
                }
            }
            File.Move(temporary, fullPath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
        finally
        {
            try { if (derivedSnapshot is not null && File.Exists(derivedSnapshot)) File.Delete(derivedSnapshot); } catch { }
        }
    }

    public ProjectData Load(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new InvalidDataException("Projet PolyChrom absent ou vide.");
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
        var settings = archive.GetEntry("project.json") ?? throw new InvalidDataException("Les réglages du projet sont absents.");
        if (settings.Length <= 0) throw new InvalidDataException("Les réglages du projet sont invalides.");
        ProjectData data;
        using (var stream = settings.Open()) data = JsonSerializer.Deserialize<ProjectData>(stream) ?? throw new InvalidDataException("Projet PolyChrom invalide.");
        Validate(data);
        var referencedPatterns = new HashSet<string>(StringComparer.Ordinal);
        if (data.Pattern is not null) referencedPatterns.Add(data.Pattern.ImagePath);
        if (data.Layers is not null)
            foreach (var layerPattern in data.Layers.SelectMany(group => group).Select(layer => layer.Pattern).Where(value => value is not null))
                referencedPatterns.Add(layerPattern!.ImagePath);
        foreach (var entryPath in referencedPatterns)
            if (entryPath != "pattern/motif.png" && !Regex.IsMatch(entryPath, "^pattern/motif-[0-9]{3}\\.png$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Le chemin d’un motif intégré est invalide.");
        var allowedEntries = new HashSet<string>(referencedPatterns, StringComparer.Ordinal) { "project.json", data.SourcePath };
        if (archive.Entries.Any(entry => !allowedEntries.Contains(entry.FullName)) || archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count ||
            archive.Entries.Count != allowedEntries.Count)
            throw new InvalidDataException("Le projet PolyChrom contient des fichiers inattendus.");

        var modelEntry = archive.GetEntry(data.SourcePath);
        var modelName = Path.GetFileName(data.SourcePath);
        var extension = Path.GetExtension(modelName).ToLowerInvariant();
        if (modelEntry is null || modelEntry.Length <= 0 || data.SourcePath != "model/" + modelName || extension is not ".3mf" and not ".stl")
            throw new InvalidDataException("Le modèle 3D intégré au projet est absent ou invalide.");

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath).ToUpperInvariant())))[..20];
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom 3MF", "SharedProjects", identity);
        Directory.CreateDirectory(folder);
        var extractedPath = Path.Combine(folder, modelName);
        Extract(modelEntry, extractedPath);
        var extractedPatterns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entryPath in referencedPatterns)
        {
            var patternEntry = archive.GetEntry(entryPath);
            if (patternEntry is null || patternEntry.Length <= 0) throw new InvalidDataException("Le motif PNG intégré est absent.");
            var patternPath = Path.Combine(folder, Path.GetFileName(entryPath));
            Extract(patternEntry, patternPath);
            extractedPatterns[entryPath] = patternPath;
        }
        PatternSettings? ExtractPattern(PatternSettings? value) =>
            value is null ? null : value with { ImagePath = extractedPatterns[value.ImagePath] };
        var extractedPattern = ExtractPattern(data.Pattern);
        var extractedLayers = data.Layers?.Select(group => group.Select(layer =>
            layer.Pattern is null ? layer : layer with { Pattern = ExtractPattern(layer.Pattern) }).ToList()).ToList();
        return data with { SourcePath = extractedPath, Pattern = extractedPattern, Layers = extractedLayers };
    }

    static void Extract(ZipArchiveEntry entry, string destinationPath)
    {
        var temporary = destinationPath + ".tmp";
        try
        {
            using (var source = entry.Open())
            using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    static ProjectData LoadLegacy(Stream input)
    {
        var data = JsonSerializer.Deserialize<ProjectData>(input) ?? throw new InvalidDataException("Projet PolyChrom invalide.");
        Validate(data);
        return data;
    }

    internal static void ValidateForDocument(ProjectData data, ModelDocument document)
    {
        var objects = document.Objects.ToDictionary(obj => obj.Index);
        if (data.Pattern is not null && data.Pattern.TargetObject >= 0 && !objects.ContainsKey(data.Pattern.TargetObject))
            throw new InvalidDataException("L’objet ciblé par le motif n’existe pas dans le modèle intégré.");
        foreach (var assignments in data.Assignments)
            if (assignments.Keys.Any(key => !objects.ContainsKey(key))) throw new InvalidDataException("Le projet cible un objet absent du modèle intégré.");
        if (data.TriangleAssignments is not null)
            foreach (var assignments in data.TriangleAssignments)
                foreach (var pair in assignments)
                    if (!objects.TryGetValue(pair.Key, out var obj) || pair.Value.Length != obj.Triangles.Count)
                        throw new InvalidDataException("Les affectations de triangles ne correspondent pas au modèle intégré.");
        if (data.Layers is not null)
        {
            var layerService = new LayerService();
            for (var index = 0; index < data.Layers.Count; index++)
                layerService.Validate(data.Layers[index], document, data.Proposals[Math.Min(index, data.Proposals.Count - 1)].Count);
        }
        if (data.LayerBaseTriangles is not null)
            foreach (var assignments in data.LayerBaseTriangles)
                foreach (var pair in assignments)
                    if (!objects.TryGetValue(pair.Key, out var obj) || pair.Value.Length != obj.Triangles.Count)
                        throw new InvalidDataException("La base des calques ne correspond pas au modèle intégré.");
    }

    static void Validate(ProjectData data)
    {
        if (data is null || string.IsNullOrWhiteSpace(data.SourcePath) || data.Proposals is null || data.Assignments is null || data.Proposals.Count is < 1 or > 4 || data.Assignments.Count > 4 || data.TriangleAssignments?.Count > 4 || data.SelectedProposal < 0 || data.SelectedProposal >= data.Proposals.Count || data.ColorCount is < 2 or > 32 || !double.IsFinite(data.Yaw) || !double.IsFinite(data.Pitch) || !double.IsFinite(data.Zoom) || data.Zoom <= 0)
            throw new InvalidDataException("Le projet contient des paramètres invalides.");
        if (data.Pattern is not null) PatternService.ValidateSettings(data.Pattern);
        if (data.Layers?.Count > 4 || data.LayerBaseAssignments?.Count > 4 || data.LayerBaseTriangles?.Count > 4)
            throw new InvalidDataException("Le projet contient trop de groupes de calques.");
        if (data.ProposalNames is not null && data.ProposalNames.Count != data.Proposals.Count || data.ProposalDescriptions is not null && data.ProposalDescriptions.Count != data.Proposals.Count) throw new InvalidDataException("Les noms des propositions sont invalides.");
        if (data.ProposalNames?.Any(value => InvalidText(value, 160)) == true || data.ProposalDescriptions?.Any(value => InvalidText(value, 1000)) == true)
            throw new InvalidDataException("Les textes des propositions sont invalides.");
        for (var p = 0; p < data.Proposals.Count; p++)
        {
            var colors = data.Proposals[p];
            if (colors is null || colors.Count is < 2 or > 32 || colors.Any(color => !Regex.IsMatch(color ?? "", "^#[0-9A-Fa-f]{6}$"))) throw new InvalidDataException("Le projet contient une palette invalide.");
            if (p < data.Assignments.Count && (data.Assignments[p] is null || data.Assignments[p].Any(pair => pair.Key < 0 || pair.Value < 0 || pair.Value >= colors.Count))) throw new InvalidDataException("Le projet contient une affectation de couleur invalide.");
            if (data.TriangleAssignments is not null && p < data.TriangleAssignments.Count && (data.TriangleAssignments[p] is null || data.TriangleAssignments[p].Any(pair => pair.Key < 0 || pair.Value is null || pair.Value.Any(value => value < 0 || value >= colors.Count)))) throw new InvalidDataException("Le projet contient une affectation de triangle invalide.");
        }
    }

    static string SafeFileName(string value)
    {
        var cleaned = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "modele" : cleaned;
    }

    static bool InvalidText(string? value, int maximumLength) => value is null || value.Length > maximumLength || value.Any(char.IsControl);
}
