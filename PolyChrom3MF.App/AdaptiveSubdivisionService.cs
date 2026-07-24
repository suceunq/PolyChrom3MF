namespace PolyChrom3MF.App;

public sealed record SubdivisionResult(ModelObject Object, int[] SourceTriangles)
{
    public int AddedTriangles { get; init; } = Object.Triangles.Count - SourceTriangles.Distinct().Count();
}

/// <summary>
/// Builds a conforming derived mesh without changing the imported model.
/// Red triangles are split on their three edges; adjacent green triangles
/// split only the shared edges, preventing T-junctions and surface cracks.
/// </summary>
public sealed class AdaptiveSubdivisionService
{
    public SubdivisionResult Subdivide(
        ModelObject source,
        IReadOnlySet<int> sourceTriangles,
        int levels = 1,
        CancellationToken cancellationToken = default)
    {
        if (sourceTriangles.Count == 0)
            return new SubdivisionResult(
                source with { Vertices = [.. source.Vertices], Triangles = [.. source.Triangles] },
                Enumerable.Range(0, source.Triangles.Count).ToArray());
        if (levels is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(levels));
        if (sourceTriangles.Any(index => index < 0 || index >= source.Triangles.Count))
            throw new ArgumentOutOfRangeException(nameof(sourceTriangles));

        var current = source with { Vertices = [.. source.Vertices], Triangles = [.. source.Triangles] };
        var parents = Enumerable.Range(0, source.Triangles.Count).ToArray();
        for (var level = 0; level < levels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = Enumerable.Range(0, current.Triangles.Count)
                .Where(index => sourceTriangles.Contains(parents[index]))
                .ToHashSet();
            (current, parents) = SubdivideLevel(current, parents, selected, cancellationToken);
        }
        return new SubdivisionResult(current, parents);
    }

    static (ModelObject Object, int[] Parents) SubdivideLevel(
        ModelObject source,
        IReadOnlyList<int> parents,
        IReadOnlySet<int> selected,
        CancellationToken cancellationToken)
    {
        var splitEdges = new HashSet<Edge>();
        foreach (var index in selected)
        {
            var triangle = source.Triangles[index];
            splitEdges.Add(new Edge(triangle.A, triangle.B));
            splitEdges.Add(new Edge(triangle.B, triangle.C));
            splitEdges.Add(new Edge(triangle.C, triangle.A));
        }

        var vertices = new List<Vertex>(source.Vertices);
        var midpoints = new Dictionary<Edge, int>();
        var triangles = new List<Triangle>(source.Triangles.Count + selected.Count * 3);
        var nextParents = new List<int>(triangles.Capacity);

        int Midpoint(Edge edge)
        {
            if (midpoints.TryGetValue(edge, out var existing)) return existing;
            var a = source.Vertices[edge.A];
            var b = source.Vertices[edge.B];
            var index = vertices.Count;
            vertices.Add(new Vertex((a.X + b.X) * .5, (a.Y + b.Y) * .5, (a.Z + b.Z) * .5));
            midpoints.Add(edge, index);
            return index;
        }

        void Add(int a, int b, int c, int parent)
        {
            if (a == b || b == c || c == a) return;
            triangles.Add(new Triangle(a, b, c));
            nextParents.Add(parent);
        }

        for (var index = 0; index < source.Triangles.Count; index++)
        {
            if ((index & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            var triangle = source.Triangles[index];
            var abEdge = new Edge(triangle.A, triangle.B);
            var bcEdge = new Edge(triangle.B, triangle.C);
            var caEdge = new Edge(triangle.C, triangle.A);
            var ab = splitEdges.Contains(abEdge);
            var bc = splitEdges.Contains(bcEdge);
            var ca = splitEdges.Contains(caEdge);
            var count = (ab ? 1 : 0) + (bc ? 1 : 0) + (ca ? 1 : 0);
            var parent = parents[index];
            if (count == 0)
            {
                Add(triangle.A, triangle.B, triangle.C, parent);
                continue;
            }

            var mab = ab ? Midpoint(abEdge) : -1;
            var mbc = bc ? Midpoint(bcEdge) : -1;
            var mca = ca ? Midpoint(caEdge) : -1;
            if (count == 3)
            {
                Add(triangle.A, mab, mca, parent);
                Add(mab, triangle.B, mbc, parent);
                Add(mca, mbc, triangle.C, parent);
                Add(mab, mbc, mca, parent);
            }
            else if (count == 1 && ab)
            {
                Add(triangle.A, mab, triangle.C, parent);
                Add(mab, triangle.B, triangle.C, parent);
            }
            else if (count == 1 && bc)
            {
                Add(triangle.B, mbc, triangle.A, parent);
                Add(mbc, triangle.C, triangle.A, parent);
            }
            else if (count == 1)
            {
                Add(triangle.C, mca, triangle.B, parent);
                Add(mca, triangle.A, triangle.B, parent);
            }
            else if (ab && bc)
            {
                Add(triangle.B, mbc, mab, parent);
                Add(triangle.A, mab, triangle.C, parent);
                Add(mab, mbc, triangle.C, parent);
            }
            else if (bc && ca)
            {
                Add(triangle.C, mca, mbc, parent);
                Add(triangle.A, triangle.B, mca, parent);
                Add(triangle.B, mbc, mca, parent);
            }
            else
            {
                Add(triangle.A, mab, mca, parent);
                Add(mab, triangle.B, mca, parent);
                Add(triangle.B, triangle.C, mca, parent);
            }
        }

        return (source with { Vertices = vertices, Triangles = triangles }, nextParents.ToArray());
    }

    readonly record struct Edge
    {
        public Edge(int a, int b) => (A, B) = a <= b ? (a, b) : (b, a);
        public int A { get; }
        public int B { get; }
    }
}
