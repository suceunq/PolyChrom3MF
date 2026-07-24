using System.IO;
using System.Windows.Media.Media3D;

namespace PolyChrom3MF.App;

public sealed class SmartSelectionService
{
    public Dictionary<int, HashSet<int>> SemanticRegion(ModelDocument document, string region)
    {
        region = (region ?? "").Trim().ToLowerInvariant();
        var keywords = region switch
        {
            "yeux" => new[] { "eye", "eyes", "oeil", "yeux", "iris", "pupil" },
            "cheveux" => new[] { "hair", "cheveu", "cheveux", "coiff" },
            "vêtements" => new[] { "cloth", "shirt", "coat", "dress", "pant", "robe", "veste", "habit" },
            "peau" => new[] { "skin", "body", "face", "head", "peau", "visage", "tete" },
            "accessoires" => new[] { "access", "bag", "belt", "hat", "cap", "bijou", "sac", "ceinture" },
            "socle" => new[] { "base", "stand", "socle", "plate" },
            "armes" => new[] { "weapon", "sword", "gun", "rifle", "knife", "arme", "epee", "fusil" },
            _ => Array.Empty<string>()
        };
        var named = document.Objects.Where(obj => keywords.Any(keyword =>
            (obj.Id + " " + obj.PartPath).Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToList();
        if (named.Count > 0) return named.ToDictionary(obj => obj.Index, obj => Enumerable.Range(0, obj.Triangles.Count).ToHashSet());

        var all = document.Objects.SelectMany(obj => obj.Vertices).ToArray();
        if (all.Length == 0) return [];
        var minZ = all.Min(v => v.Z); var maxZ = all.Max(v => v.Z); var height = Math.Max(1e-9, maxZ - minZ);
        var minX = all.Min(v => v.X); var maxX = all.Max(v => v.X); var width = Math.Max(1e-9, maxX - minX);
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var obj in document.Objects)
        {
            var selected = new HashSet<int>();
            for (var index = 0; index < obj.Triangles.Count; index++)
            {
                var triangle = obj.Triangles[index];
                var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
                var x = ((a.X + b.X + c.X) / 3 - minX) / width;
                var z = ((a.Z + b.Z + c.Z) / 3 - minZ) / height;
                var match = region switch
                {
                    "socle" => z <= .12,
                    "cheveux" => z >= .78,
                    "yeux" => z is >= .62 and <= .82 && Math.Abs(x - .5) is >= .04 and <= .24,
                    "vêtements" => z is >= .22 and <= .68,
                    "peau" => z is >= .58 and <= .86,
                    "accessoires" => (x <= .16 || x >= .84) && z >= .25,
                    "armes" => (x <= .12 || x >= .88) && z >= .38,
                    _ => false
                };
                if (match) selected.Add(index);
            }
            if (selected.Count > 0) result[obj.Index] = selected;
        }
        return result;
    }

    public HashSet<int> ConnectedIsland(ModelObject obj, int seed, CancellationToken cancellationToken = default)
    {
        ValidateSeed(obj, seed);
        var neighbors = EdgeNeighbors(obj);
        var selected = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            foreach (var neighbor in neighbors[current])
                if (selected.Add(neighbor)) queue.Enqueue(neighbor);
        }
        return selected;
    }

    public HashSet<int> SimilarFaces(
        ModelObject obj,
        int seed,
        double maximumAngleDegrees,
        bool connectedOnly,
        CancellationToken cancellationToken = default)
    {
        ValidateSeed(obj, seed);
        if (!double.IsFinite(maximumAngleDegrees) || maximumAngleDegrees is < 0 or > 180)
            throw new ArgumentOutOfRangeException(nameof(maximumAngleDegrees));
        var reference = Normal(obj, obj.Triangles[seed]);
        var minimumDot = Math.Cos(maximumAngleDegrees * Math.PI / 180);
        bool Similar(int index) => Vector3D.DotProduct(reference, Normal(obj, obj.Triangles[index])) >= minimumDot;
        if (!connectedOnly)
            return Enumerable.Range(0, obj.Triangles.Count).Where(Similar).ToHashSet();

        var neighbors = EdgeNeighbors(obj);
        var selected = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            foreach (var neighbor in neighbors[current])
                if (!selected.Contains(neighbor) && Similar(neighbor))
                {
                    selected.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
        }
        return selected;
    }

    public HashSet<int> FacingCamera(ModelObject obj, Vector3D cameraLookDirection, double toleranceDegrees = 85)
    {
        if (cameraLookDirection.LengthSquared < 1e-12) throw new ArgumentException("La direction de caméra est invalide.", nameof(cameraLookDirection));
        if (!double.IsFinite(toleranceDegrees) || toleranceDegrees is < 0 or > 90)
            throw new ArgumentOutOfRangeException(nameof(toleranceDegrees));
        cameraLookDirection.Normalize();
        var towardCamera = -cameraLookDirection;
        var minimumDot = Math.Cos(toleranceDegrees * Math.PI / 180);
        return Enumerable.Range(0, obj.Triangles.Count)
            .Where(index => Vector3D.DotProduct(Normal(obj, obj.Triangles[index]), towardCamera) >= minimumDot)
            .ToHashSet();
    }

    public HashSet<int> ByColor(ModelObject obj, ColorProposal proposal, int colorIndex)
    {
        if (colorIndex < 0 || colorIndex >= proposal.Colors.Count) throw new ArgumentOutOfRangeException(nameof(colorIndex));
        if (!proposal.TriangleAssignments.TryGetValue(obj.Index, out var colors))
            return proposal.Assignments.GetValueOrDefault(obj.Index, 0) == colorIndex
                ? Enumerable.Range(0, obj.Triangles.Count).ToHashSet()
                : [];
        if (colors.Length != obj.Triangles.Count) throw new InvalidDataException("Les couleurs ne correspondent pas au maillage.");
        return Enumerable.Range(0, colors.Length).Where(index => colors[index] == colorIndex).ToHashSet();
    }

    static List<HashSet<int>> EdgeNeighbors(ModelObject obj)
    {
        var neighbors = Enumerable.Range(0, obj.Triangles.Count).Select(_ => new HashSet<int>()).ToList();
        var owners = new Dictionary<Edge, int>();
        for (var index = 0; index < obj.Triangles.Count; index++)
        {
            var triangle = obj.Triangles[index];
            foreach (var edge in new[] { new Edge(triangle.A, triangle.B), new Edge(triangle.B, triangle.C), new Edge(triangle.C, triangle.A) })
            {
                if (owners.TryGetValue(edge, out var owner))
                {
                    neighbors[index].Add(owner);
                    neighbors[owner].Add(index);
                }
                else owners[edge] = index;
            }
        }
        return neighbors;
    }

    static Vector3D Normal(ModelObject obj, Triangle triangle)
    {
        var a = obj.Vertices[triangle.A];
        var b = obj.Vertices[triangle.B];
        var c = obj.Vertices[triangle.C];
        var ab = new Vector3D(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var ac = new Vector3D(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var normal = Vector3D.CrossProduct(ab, ac);
        if (normal.LengthSquared > 1e-20) normal.Normalize();
        return normal;
    }

    static void ValidateSeed(ModelObject obj, int seed)
    {
        if (seed < 0 || seed >= obj.Triangles.Count) throw new ArgumentOutOfRangeException(nameof(seed));
    }

    readonly record struct Edge
    {
        public Edge(int a, int b) => (A, B) = a <= b ? (a, b) : (b, a);
        public int A { get; }
        public int B { get; }
    }
}
