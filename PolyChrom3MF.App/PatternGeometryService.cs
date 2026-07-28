namespace PolyChrom3MF.App;

public sealed record PatternGeometryResult(ModelDocument Document, List<ColorProposal> BaseProposals, List<ColorProposal> Proposals, int AddedTriangles, int Levels);

public sealed class PatternGeometryService
{
    readonly PatternService _patterns = new();
    readonly AdaptiveSubdivisionService _subdivision = new();

    public PatternGeometryResult Build(
        ModelDocument source,
        IReadOnlyList<ColorProposal> bases,
        PatternSettings settings,
        IReadOnlyList<PatternMode> modes,
        CancellationToken cancellationToken = default,
        IReadOnlySet<int>? targetProposals = null)
    {
        var refinement = new Dictionary<int, HashSet<int>>();
        for (var index = 0; index < bases.Count; index++)
        {
            if (targetProposals is not null && !targetProposals.Contains(index)) continue;
            var probe = Clone(bases[index]);
            var result = _patterns.Apply(source, probe, settings, modes[Math.Min(index, modes.Count - 1)], cancellationToken, collectRefinement: true);
            foreach (var pair in result.RefinementTriangles)
            {
                if (!refinement.TryGetValue(pair.Key, out var target)) refinement[pair.Key] = target = [];
                target.UnionWith(pair.Value);
            }
        }

        var boundaryCount = refinement.Values.Sum(set => set.Count);
        var levels = source.TriangleCount switch { < 100_000 => 2, < 1_000_000 => 1, _ => 0 };
        if (boundaryCount > 350_000) levels = Math.Min(levels, 1);
        if (levels == 0 || boundaryCount == 0)
        {
            var unchangedBases = bases.Select(Clone).ToList();
            var unchanged = unchangedBases.Select(Clone).ToList();
            for (var index = 0; index < unchanged.Count; index++)
            {
                if (targetProposals is not null && !targetProposals.Contains(index)) continue;
                _patterns.Apply(source, unchanged[index], settings, modes[Math.Min(index, modes.Count - 1)], cancellationToken);
            }
            return new PatternGeometryResult(source, unchangedBases, unchanged, 0, 0);
        }

        var objects = new List<ModelObject>(source.Objects.Count);
        var mappings = new Dictionary<int, int[]>();
        foreach (var obj in source.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = refinement.GetValueOrDefault(obj.Index) ?? [];
            var result = _subdivision.Subdivide(obj, selected, levels, cancellationToken);
            objects.Add(result.Object);
            mappings[obj.Index] = result.SourceTriangles;
        }
        var triangleCount = objects.Sum(obj => (long)obj.Triangles.Count);
        var allVertices = objects.SelectMany(obj => obj.Vertices).ToArray();
        var derived = source with
        {
            Objects = objects,
            TriangleCount = triangleCount,
            SizeX = allVertices.Max(vertex => vertex.X) - allVertices.Min(vertex => vertex.X),
            SizeY = allVertices.Max(vertex => vertex.Y) - allVertices.Min(vertex => vertex.Y),
            SizeZ = allVertices.Max(vertex => vertex.Z) - allVertices.Min(vertex => vertex.Z),
            IsDerived = true
        };
        var derivedBases = new List<ColorProposal>(bases.Count);
        var proposals = new List<ColorProposal>(bases.Count);
        var reliefTriangles = new Dictionary<int, HashSet<int>>();
        for (var proposalIndex = 0; proposalIndex < bases.Count; proposalIndex++)
        {
            var proposal = Clone(bases[proposalIndex]);
            foreach (var obj in objects)
            {
                var sourceAssignments = bases[proposalIndex].TriangleAssignments.GetValueOrDefault(obj.Index);
                var mapping = mappings[obj.Index];
                var mapped = new int[mapping.Length];
                for (var index = 0; index < mapped.Length; index++)
                    mapped[index] = sourceAssignments is not null
                        ? sourceAssignments[mapping[index]]
                        : bases[proposalIndex].Assignments.GetValueOrDefault(obj.Index, 0);
                proposal.TriangleAssignments[obj.Index] = mapped;
            }
            derivedBases.Add(Clone(proposal));
            if (targetProposals is null || targetProposals.Contains(proposalIndex))
            {
                var applied = _patterns.Apply(derived, proposal, settings, modes[Math.Min(proposalIndex, modes.Count - 1)], cancellationToken, collectRefinement: Math.Abs(settings.ReliefDepth) > .0001);
                foreach (var pair in applied.RefinementTriangles)
                {
                    if (!reliefTriangles.TryGetValue(pair.Key, out var selected)) reliefTriangles[pair.Key] = selected = [];
                    selected.UnionWith(pair.Value);
                }
            }
            proposals.Add(proposal);
        }
        if (Math.Abs(settings.ReliefDepth) > .0001 && reliefTriangles.Count > 0)
            derived = DisplaceRelief(derived, reliefTriangles, settings.ReliefDepth);
        return new PatternGeometryResult(derived, derivedBases, proposals, checked((int)Math.Min(int.MaxValue, triangleCount - source.TriangleCount)), levels);
    }

    static ModelDocument DisplaceRelief(ModelDocument document, IReadOnlyDictionary<int, HashSet<int>> selectedByObject, double depth)
    {
        var objects = document.Objects.Select(obj =>
        {
            if (!selectedByObject.TryGetValue(obj.Index, out var selected) || selected.Count == 0) return obj;
            var normals = new (double X, double Y, double Z)[obj.Vertices.Count];
            foreach (var triangleIndex in selected)
            {
                if ((uint)triangleIndex >= (uint)obj.Triangles.Count) continue;
                var triangle = obj.Triangles[triangleIndex];
                var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
                var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
                var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
                var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
                foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
                    normals[vertex] = (normals[vertex].X + nx, normals[vertex].Y + ny, normals[vertex].Z + nz);
            }
            var vertices = obj.Vertices.Select((vertex, index) =>
            {
                var normal = normals[index];
                var length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
                return length <= 1e-12 ? vertex : new Vertex(vertex.X + normal.X / length * depth, vertex.Y + normal.Y / length * depth, vertex.Z + normal.Z / length * depth);
            }).ToList();
            return obj with { Vertices = vertices };
        }).ToList();
        var vertices = objects.SelectMany(obj => obj.Vertices).ToArray();
        return document with
        {
            Objects = objects,
            SizeX = vertices.Max(vertex => vertex.X) - vertices.Min(vertex => vertex.X),
            SizeY = vertices.Max(vertex => vertex.Y) - vertices.Min(vertex => vertex.Y),
            SizeZ = vertices.Max(vertex => vertex.Z) - vertices.Min(vertex => vertex.Z),
            IsDerived = true
        };
    }

    static ColorProposal Clone(ColorProposal proposal) => new(
        proposal.Name,
        proposal.Description,
        proposal.Colors.Select(color => new PaletteColor(color.Name, color.Hex)).ToList(),
        new Dictionary<int, int>(proposal.Assignments))
    {
        TriangleAssignments = proposal.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone())
    };
}
