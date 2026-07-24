namespace PolyChrom3MF.App;

public sealed record LocalRefinementResult(
    ModelDocument Document,
    List<ColorProposal> Proposals,
    List<ColorProposal> Bases,
    List<List<ColorLayer>> Layers,
    Dictionary<int, HashSet<int>> Selection,
    int AddedTriangles);

public sealed class LocalRefinementService
{
    readonly AdaptiveSubdivisionService _subdivision = new();

    public LocalRefinementResult Refine(
        ModelDocument document,
        IReadOnlyList<ColorProposal> proposals,
        IReadOnlyList<ColorProposal> bases,
        IReadOnlyList<IReadOnlyList<ColorLayer>> layers,
        IReadOnlyDictionary<int, HashSet<int>> selection,
        int levels,
        CancellationToken cancellationToken = default)
    {
        if (levels <= 0 || selection.Count == 0)
            return new(document, proposals.Select(Clone).ToList(), bases.Select(Clone).ToList(), CloneLayers(layers), selection.ToDictionary(pair => pair.Key, pair => new HashSet<int>(pair.Value)), 0);
        var objects = new List<ModelObject>(document.Objects.Count);
        var mappings = new Dictionary<int, int[]>();
        var refinedSelection = new Dictionary<int, HashSet<int>>();
        foreach (var obj in document.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _subdivision.Subdivide(obj, selection.GetValueOrDefault(obj.Index) ?? [], levels, cancellationToken);
            objects.Add(result.Object); mappings[obj.Index] = result.SourceTriangles;
            if (selection.TryGetValue(obj.Index, out var selected))
                refinedSelection[obj.Index] = Enumerable.Range(0, result.SourceTriangles.Length).Where(index => selected.Contains(result.SourceTriangles[index])).ToHashSet();
        }
        var refinedDocument = document with { Objects = objects, TriangleCount = objects.Sum(obj => (long)obj.Triangles.Count), IsDerived = true };
        List<ColorProposal> MapProposals(IReadOnlyList<ColorProposal> source) => source.Select(proposal =>
        {
            var mapped = Clone(proposal);
            mapped.TriangleAssignments.Clear();
            foreach (var obj in objects)
            {
                var sourceColors = proposal.TriangleAssignments.GetValueOrDefault(obj.Index);
                mapped.TriangleAssignments[obj.Index] = mappings[obj.Index].Select(parent => sourceColors is null ? proposal.Assignments.GetValueOrDefault(obj.Index, 0) : sourceColors[parent]).ToArray();
            }
            return mapped;
        }).ToList();
        var mappedLayers = CloneLayers(layers);
        foreach (var group in mappedLayers)
            for (var layerIndex = 0; layerIndex < group.Count; layerIndex++)
            {
                var layer = group[layerIndex];
                var overrides = new Dictionary<int, int[]>();
                foreach (var pair in layer.TriangleOverrides)
                    overrides[pair.Key] = mappings[pair.Key].Select(parent => pair.Value[parent]).ToArray();
                group[layerIndex] = layer with { TriangleOverrides = overrides };
            }
        return new(refinedDocument, MapProposals(proposals), MapProposals(bases), mappedLayers, refinedSelection,
            checked((int)Math.Min(int.MaxValue, refinedDocument.TriangleCount - document.TriangleCount)));
    }

    static ColorProposal Clone(ColorProposal proposal) => new(proposal.Name, proposal.Description,
        proposal.Colors.Select(color => new PaletteColor(color.Name, color.Hex)).ToList(), new Dictionary<int, int>(proposal.Assignments))
    { TriangleAssignments = proposal.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };

    static List<List<ColorLayer>> CloneLayers(IReadOnlyList<IReadOnlyList<ColorLayer>> groups) => groups.Select(group =>
        group.Select(layer => layer.Duplicate(layer.Name) with { Id = layer.Id }).ToList()).ToList();
}
