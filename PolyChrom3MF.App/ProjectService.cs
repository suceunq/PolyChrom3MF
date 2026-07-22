using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public sealed record ProjectData(string SourcePath, int SelectedProposal, List<List<string>> Proposals, List<Dictionary<int,int>> Assignments, double Yaw, double Pitch, double Zoom, int Generation, bool FunMode = false, int ColorCount = 4, List<Dictionary<int, int[]>>? TriangleAssignments = null);

public sealed class ProjectService
{
    public void Save(string path, ModelDocument document, IReadOnlyList<ColorProposal> proposals, int selected, double yaw, double pitch, double zoom, int generation = 0, bool funMode = false, int colorCount = 4)
    {
        var triangleAssignments = proposals.Select(p => p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())).ToList();
        var data = new ProjectData(document.Path, selected, proposals.Select(p => p.Colors.Select(c => c.Hex).ToList()).ToList(), proposals.Select(p=>new Dictionary<int,int>(p.Assignments)).ToList(), yaw, pitch, zoom, generation, funMode, colorCount, triangleAssignments);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public ProjectData Load(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0 || file.Length > 128L * 1024 * 1024) throw new InvalidDataException("Projet PolyChrom absent, vide ou trop volumineux.");
            var data = JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(path)) ?? throw new InvalidDataException("Projet PolyChrom invalide.");
            if (string.IsNullOrWhiteSpace(data.SourcePath) || data.Proposals.Count is < 1 or > 4 || data.SelectedProposal < 0 || data.SelectedProposal >= data.Proposals.Count || data.ColorCount is < 4 or > 32 || !double.IsFinite(data.Yaw) || !double.IsFinite(data.Pitch) || !double.IsFinite(data.Zoom) || data.Zoom <= 0)
                throw new InvalidDataException("Le projet contient des paramètres invalides.");
            for (var p = 0; p < data.Proposals.Count; p++)
            {
                var colors = data.Proposals[p];
                if (colors.Count is < 4 or > 32 || colors.Any(color => !Regex.IsMatch(color ?? "", "^#[0-9A-Fa-f]{6}$"))) throw new InvalidDataException("Le projet contient une palette invalide.");
                if (p < data.Assignments.Count && data.Assignments[p].Values.Any(value => value < 0 || value >= colors.Count)) throw new InvalidDataException("Le projet contient une affectation de couleur invalide.");
                if (data.TriangleAssignments is not null && p < data.TriangleAssignments.Count && data.TriangleAssignments[p].Values.SelectMany(values => values).Any(value => value < 0 || value >= colors.Count)) throw new InvalidDataException("Le projet contient une affectation de triangle invalide.");
            }
            return data;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Le projet PolyChrom est illisible ou corrompu.", ex);
        }
    }
}
