using System.IO;

namespace PolyChrom3MF.App;

public enum ColorLayerKind
{
    BaseColor,
    Image,
    MonochromeLogo,
    Paint,
    SelectionMask,
    Projection,
    Text,
    Effect
}

public sealed record ColorLayer(
    Guid Id,
    string Name,
    ColorLayerKind Kind,
    bool IsVisible = true,
    bool IsLocked = false,
    double PreviewOpacity = 1)
{
    /// <summary>-1 means transparent/no override; every other value is a palette index.</summary>
    public Dictionary<int, int[]> TriangleOverrides { get; init; } = [];
    public PatternSettings? Pattern { get; init; }

    public ColorLayer Duplicate(string? name = null) => this with
    {
        Id = Guid.NewGuid(),
        Name = name ?? Name,
        TriangleOverrides = TriangleOverrides.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())
    };
}

public sealed class LayerService
{
    public ColorLayer Create(string name, ColorLayerKind kind) =>
        new(Guid.NewGuid(), SafeName(name), kind);

    public void Validate(IReadOnlyList<ColorLayer> layers, ModelDocument document, int colorCount)
    {
        if (layers.Count > 256) throw new InvalidDataException("Le projet contient trop de calques.");
        var objects = document.Objects.ToDictionary(obj => obj.Index);
        var ids = new HashSet<Guid>();
        foreach (var layer in layers)
        {
            if (layer.Id == Guid.Empty || !ids.Add(layer.Id) || layer.Name != SafeName(layer.Name) ||
                !Enum.IsDefined(layer.Kind) || !double.IsFinite(layer.PreviewOpacity) || layer.PreviewOpacity is < 0 or > 1)
                throw new InvalidDataException("Un calque contient des paramètres invalides.");
            if (layer.Pattern is not null) PatternService.ValidateSettings(layer.Pattern);
            foreach (var pair in layer.TriangleOverrides)
            {
                if (!objects.TryGetValue(pair.Key, out var obj) || pair.Value.Length != obj.Triangles.Count ||
                    pair.Value.Any(value => value < -1 || value >= colorCount))
                    throw new InvalidDataException("Les données d’un calque ne correspondent pas au modèle.");
            }
        }
    }

    public ColorProposal Compose(ModelDocument document, ColorProposal basis, IReadOnlyList<ColorLayer> layers)
    {
        Validate(layers, document, basis.Colors.Count);
        var result = new ColorProposal(
            basis.Name,
            basis.Description,
            basis.Colors.Select(color => new PaletteColor(color.Name, color.Hex)).ToList(),
            new Dictionary<int, int>(basis.Assignments))
        {
            TriangleAssignments = basis.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())
        };

        foreach (var obj in document.Objects)
        {
            if (!result.TriangleAssignments.TryGetValue(obj.Index, out var assignments) || assignments.Length != obj.Triangles.Count)
            {
                assignments = new int[obj.Triangles.Count];
                Array.Fill(assignments, result.Assignments.GetValueOrDefault(obj.Index, 0));
                result.TriangleAssignments[obj.Index] = assignments;
            }
            foreach (var layer in layers.Where(layer => layer.IsVisible))
            {
                if (!layer.TriangleOverrides.TryGetValue(obj.Index, out var overrides)) continue;
                for (var index = 0; index < assignments.Length; index++)
                    if (overrides[index] >= 0) assignments[index] = overrides[index];
            }
        }
        return result;
    }

    public ColorLayer Merge(ColorLayer lower, ColorLayer upper)
    {
        if (lower.IsLocked || upper.IsLocked) throw new InvalidOperationException("Déverrouillez les calques avant de les fusionner.");
        var objectIds = lower.TriangleOverrides.Keys.Union(upper.TriangleOverrides.Keys);
        var merged = Create($"{lower.Name} + {upper.Name}", upper.Kind);
        foreach (var objectId in objectIds)
        {
            var bottom = lower.TriangleOverrides.GetValueOrDefault(objectId);
            var top = upper.TriangleOverrides.GetValueOrDefault(objectId);
            var length = bottom?.Length ?? top?.Length ?? 0;
            if (bottom is not null && top is not null && bottom.Length != top.Length)
                throw new InvalidDataException("Les calques à fusionner ont des tailles incompatibles.");
            var values = new int[length];
            Array.Fill(values, -1);
            if (bottom is not null) Array.Copy(bottom, values, length);
            if (top is not null)
                for (var index = 0; index < length; index++)
                    if (top[index] >= 0) values[index] = top[index];
            merged.TriangleOverrides[objectId] = values;
        }
        return merged;
    }

    public static string SafeName(string? value)
    {
        var cleaned = new string((value ?? "").Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Calque" : cleaned;
    }
}
