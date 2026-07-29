using System.Numerics;
using System.IO;
using PolyChrom3MF.Logos;

namespace PolyChrom3MF.App;

public sealed record LogoApplicationResult(
    ModelDocument Document,
    List<ColorProposal> Bases,
    List<ColorProposal> Proposals,
    int AddedTriangles,
    int SubdivisionLevels);

public sealed class LogoApplicationService
{
    readonly LogoProjector _projector = new();
    readonly AdaptiveSubdivisionService _subdivision = new();

    public LogoApplicationResult Apply(
        ModelDocument source,
        IReadOnlyList<ColorProposal> bases,
        LogoProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bases);
        ArgumentNullException.ThrowIfNull(project);
        if (bases.Count == 0) throw new InvalidDataException("Aucune proposition de couleurs.");
        LogoProjectStore.Validate(project);

        var sourceMeshes = ConvertMeshes(source.Objects);
        // Select the complete rectangular footprint, including transparent
        // holes. Otherwise a coarse source triangle can miss a thin letter
        // before it ever gets a chance to be refined.
        var probe = _projector.Project(
            sourceMeshes,
            project,
            includeTransparentFootprint: true,
            cancellationToken: cancellationToken);
        var selected = probe.Hits
            .GroupBy(hit => hit.ObjectIndex)
            .ToDictionary(group => group.Key, group => group.Select(hit => hit.TriangleIndex).ToHashSet());
        var selectedCount = selected.Values.Sum(set => set.Count);
        var levels = RefinementLevels(source.TriangleCount, selectedCount);

        var document = source;
        var mappings = source.Objects.ToDictionary(
            obj => obj.Index,
            obj => Enumerable.Range(0, obj.Triangles.Count).ToArray());
        if (levels > 0)
        {
            var objects = new List<ModelObject>(source.Objects.Count);
            foreach (var obj in source.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var triangles = selected.GetValueOrDefault(obj.Index) ?? [];
                var refined = _subdivision.Subdivide(obj, triangles, levels, cancellationToken);
                objects.Add(refined.Object);
                mappings[obj.Index] = refined.SourceTriangles;
            }
            var vertices = objects.SelectMany(obj => obj.Vertices).ToArray();
            document = source with
            {
                Objects = objects,
                TriangleCount = objects.Sum(obj => (long)obj.Triangles.Count),
                SizeX = vertices.Max(vertex => vertex.X) - vertices.Min(vertex => vertex.X),
                SizeY = vertices.Max(vertex => vertex.Y) - vertices.Min(vertex => vertex.Y),
                SizeZ = vertices.Max(vertex => vertex.Z) - vertices.Min(vertex => vertex.Z),
                IsDerived = true
            };
        }

        var mappedBases = bases.Select(basis => MapBasis(document, basis, mappings)).ToList();
        var finalProjection = _projector.Project(ConvertMeshes(document.Objects), project, cancellationToken: cancellationToken);
        document = ApplyRelief(document, project, finalProjection, cancellationToken);
        var proposals = mappedBases.Select(Clone).ToList();
        foreach (var proposal in proposals)
        {
            foreach (var obj in document.Objects)
            {
                if (!proposal.TriangleAssignments.TryGetValue(obj.Index, out var assignments))
                {
                    assignments = new int[obj.Triangles.Count];
                    Array.Fill(assignments, proposal.Assignments.GetValueOrDefault(obj.Index, 0));
                    proposal.TriangleAssignments[obj.Index] = assignments;
                }
            }
            foreach (var hit in finalProjection.Hits)
            {
                var filamentIndex = ResolveFilament(proposal, hit);
                if (filamentIndex >= proposal.Colors.Count)
                    throw new InvalidDataException($"Le logo utilise le filament {hit.FilamentIndex + 1}, absent de la palette.");
                proposal.TriangleAssignments[hit.ObjectIndex][hit.TriangleIndex] = filamentIndex;
            }
        }
        return new LogoApplicationResult(
            document,
            mappedBases,
            proposals,
            checked((int)Math.Min(int.MaxValue, document.TriangleCount - source.TriangleCount)),
            levels);
    }

    static int RefinementLevels(long sourceTriangleCount, int selectedTriangleCount)
    {
        if (selectedTriangleCount == 0) return 0;

        // Spend the detail budget only below the stamp. A small logo on a
        // coarse model may need 32–64 subdivisions along a source edge for
        // letters and holes to survive conversion to printable triangles.
        var refinedTriangleBudget = sourceTriangleCount switch
        {
            < 500_000 => 100_000L,
            < 2_000_000 => 70_000L,
            _ => 40_000L
        };
        var projected = (long)selectedTriangleCount;
        var levels = 0;
        while (levels < 6 && projected <= refinedTriangleBudget / 4)
        {
            projected *= 4;
            levels++;
        }
        return levels;
    }

    static ModelDocument ApplyRelief(
        ModelDocument document,
        LogoProject project,
        LogoProjectionResult projection,
        CancellationToken cancellationToken)
    {
        var reliefs = project.Instances
            .Where(instance => MathF.Abs(instance.Transform.ReliefMm) > 1e-5f)
            .ToDictionary(instance => instance.Id, instance => instance.Transform.ReliefMm);
        if (reliefs.Count == 0) return document;

        var hitsByObject = projection.Hits
            .Where(hit => reliefs.ContainsKey(hit.InstanceId))
            .GroupBy(hit => hit.ObjectIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (hitsByObject.Count == 0) return document;

        var instances = project.Instances.ToDictionary(instance => instance.Id);
        var objects = new List<ModelObject>(document.Objects.Count);
        foreach (var obj in document.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hitsByObject.TryGetValue(obj.Index, out var hits))
            {
                objects.Add(obj);
                continue;
            }

            var displacement = new Vector3[obj.Vertices.Count];
            var weight = new float[obj.Vertices.Count];
            foreach (var hit in hits)
            {
                if ((uint)hit.TriangleIndex >= (uint)obj.Triangles.Count) continue;
                var triangle = obj.Triangles[hit.TriangleIndex];
                var instance = instances[hit.InstanceId];
                var offset = instance.Transform.Anchor.Normal *
                             (instance.Transform.ReliefMm * Math.Clamp(hit.Alpha, 0, 1));
                Add(triangle.A, offset);
                Add(triangle.B, offset);
                Add(triangle.C, offset);
            }

            var vertices = new List<Vertex>(obj.Vertices.Count);
            for (var index = 0; index < obj.Vertices.Count; index++)
            {
                var source = obj.Vertices[index];
                var delta = weight[index] > 0 ? displacement[index] / weight[index] : Vector3.Zero;
                vertices.Add(new Vertex(source.X + delta.X, source.Y + delta.Y, source.Z + delta.Z));
            }
            objects.Add(obj with { Vertices = vertices });

            void Add(int vertexIndex, Vector3 offset)
            {
                if ((uint)vertexIndex >= (uint)displacement.Length) return;
                displacement[vertexIndex] += offset;
                weight[vertexIndex]++;
            }
        }

        var allVertices = objects.SelectMany(obj => obj.Vertices).ToArray();
        return document with
        {
            Objects = objects,
            SizeX = allVertices.Max(vertex => vertex.X) - allVertices.Min(vertex => vertex.X),
            SizeY = allVertices.Max(vertex => vertex.Y) - allVertices.Min(vertex => vertex.Y),
            SizeZ = allVertices.Max(vertex => vertex.Z) - allVertices.Min(vertex => vertex.Z),
            IsDerived = true
        };
    }

    public List<ColorProposal> Preview(
        ModelDocument document,
        IReadOnlyList<ColorProposal> bases,
        LogoProject project,
        CancellationToken cancellationToken = default)
    {
        LogoProjectStore.Validate(project);
        var projection = _projector.Project(ConvertMeshes(document.Objects), project, cancellationToken: cancellationToken);
        var proposals = bases.Select(Clone).ToList();
        foreach (var proposal in proposals)
        {
            foreach (var obj in document.Objects)
            {
                if (!proposal.TriangleAssignments.TryGetValue(obj.Index, out var values) ||
                    values.Length != obj.Triangles.Count)
                {
                    values = new int[obj.Triangles.Count];
                    Array.Fill(values, proposal.Assignments.GetValueOrDefault(obj.Index, 0));
                    proposal.TriangleAssignments[obj.Index] = values;
                }
            }
            foreach (var hit in projection.Hits)
            {
                var filamentIndex = ResolveFilament(proposal, hit);
                if (filamentIndex >= proposal.Colors.Count) continue;
                proposal.TriangleAssignments[hit.ObjectIndex][hit.TriangleIndex] = filamentIndex;
            }
        }
        return proposals;
    }

    public static LogoInstance FromPattern(
        Guid assetId,
        PatternSettings settings,
        string name,
        int filamentIndex)
    {
        if (!settings.HasSurfaceFrame) throw new InvalidDataException("Placez d’abord le logo sur une surface.");
        var normal = new Vector3((float)settings.SurfaceNx, (float)settings.SurfaceNy, (float)settings.SurfaceNz);
        var tangent = new Vector3((float)settings.SurfaceUx, (float)settings.SurfaceUy, (float)settings.SurfaceUz);
        var bitangent = new Vector3((float)settings.SurfaceVx, (float)settings.SurfaceVy, (float)settings.SurfaceVz);
        var anchor = new SurfaceAnchor(
            settings.TargetObject,
            -1,
            new Vector3((float)settings.SurfaceX, (float)settings.SurfaceY, (float)settings.SurfaceZ),
            Vector3.Normalize(normal),
            Vector3.Normalize(tangent),
            Vector3.Normalize(bitangent));
        var size = (float)Math.Max(.01, settings.SurfaceWorldSize * settings.Scale / 100);
        var mode = settings.Mode switch
        {
            PatternMode.Cylindrical => LogoProjectionMode.Cylindrical,
            PatternMode.Triplanar => LogoProjectionMode.Conformal,
            _ => LogoProjectionMode.Plane
        };
        return new LogoInstance
        {
            AssetId = assetId,
            Name = name,
            FilamentIndex = filamentIndex,
            UseImageColors = !settings.MonochromeLogo,
            ColorHex = "#FFFFFF",
            Transform = new LogoTransform
            {
                Anchor = anchor,
                WidthMm = size * (float)(settings.StretchX / 100),
                HeightMm = size * (float)(settings.StretchY / 100),
                RotationDegrees = (float)settings.Rotation,
                TiltXDegrees = (float)settings.TiltX,
                TiltYDegrees = (float)settings.TiltY,
                OffsetUmm = (float)(settings.OffsetX / 100 * settings.SurfaceWorldSize),
                OffsetVmm = (float)(-settings.OffsetY / 100 * settings.SurfaceWorldSize),
                ReliefMm = (float)settings.ReliefDepth,
                MirrorHorizontal = settings.MirrorX,
                MirrorVertical = settings.MirrorY,
                Projection = mode
            }
        };
    }

    static List<LogoMeshObject> ConvertMeshes(IEnumerable<ModelObject> objects) =>
        objects.Select(obj => new LogoMeshObject(
            obj.Index,
            obj.Vertices.Select(vertex => new MeshVertex((float)vertex.X, (float)vertex.Y, (float)vertex.Z)).ToArray(),
        obj.Triangles.Select(triangle => new MeshTriangle(triangle.A, triangle.B, triangle.C)).ToArray())).ToList();

    static int ResolveFilament(ColorProposal proposal, LogoProjectionHit hit)
    {
        if (!hit.UseImageColors) return hit.FilamentIndex;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < proposal.Colors.Count; index++)
        {
            var color = proposal.Colors[index].Color;
            var dr = color.R - hit.Red;
            var dg = color.G - hit.Green;
            var db = color.B - hit.Blue;
            var distance = dr * dr * .30 + dg * dg * .59 + db * db * .11;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return best;
    }

    static ColorProposal MapBasis(ModelDocument document, ColorProposal source, IReadOnlyDictionary<int, int[]> mappings)
    {
        var result = Clone(source);
        result.TriangleAssignments.Clear();
        foreach (var obj in document.Objects)
        {
            var mapping = mappings[obj.Index];
            var sourceAssignments = source.TriangleAssignments.GetValueOrDefault(obj.Index);
            var values = new int[mapping.Length];
            for (var index = 0; index < values.Length; index++)
                values[index] = sourceAssignments is { Length: > 0 }
                    ? sourceAssignments[mapping[index]]
                    : source.Assignments.GetValueOrDefault(obj.Index, 0);
            result.TriangleAssignments[obj.Index] = values;
        }
        return result;
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
