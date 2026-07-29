using System.Numerics;
using System.Text.Json.Serialization;

namespace PolyChrom3MF.Logos;

public enum LogoImageFormat { Png, Jpeg, WebP, Svg }
public enum LogoProjectionMode { Plane, Cylindrical, Conformal }
public enum LogoRepeatMode { Manual, Horizontal, Vertical, Circular }
public enum LogoBrushMode { Erase, Restore }

public sealed record LogoRaster(
    int Width,
    int Height,
    byte[] Rgba,
    LogoImageFormat SourceFormat,
    string DisplayName)
{
    public int PixelCount => checked(Width * Height);
    public long ByteCount => Rgba.LongLength;
    public byte AlphaAt(int x, int y) => Rgba[checked((y * Width + x) * 4 + 3)];
}

public sealed record SurfaceAnchor(
    int ObjectIndex,
    int TriangleIndex,
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    Vector3 Bitangent)
{
    public static SurfaceAnchor Create(
        int objectIndex,
        int triangleIndex,
        Vector3 position,
        Vector3 normal,
        Vector3? tangent = null)
    {
        normal = Vector3.Normalize(normal);
        var candidate = tangent ?? (Math.Abs(Vector3.Dot(normal, Vector3.UnitY)) < .9f
            ? Vector3.UnitY
            : Vector3.UnitX);
        var u = candidate - normal * Vector3.Dot(candidate, normal);
        if (u.LengthSquared() < 1e-8f) u = Vector3.Cross(normal, Vector3.UnitZ);
        u = Vector3.Normalize(u);
        var v = Vector3.Normalize(Vector3.Cross(normal, u));
        return new SurfaceAnchor(objectIndex, triangleIndex, position, normal, u, v);
    }
}

public sealed record LogoTransform
{
    public required SurfaceAnchor Anchor { get; init; }
    public float WidthMm { get; init; } = 20;
    public float HeightMm { get; init; } = 20;
    public float RotationDegrees { get; init; }
    public float TiltXDegrees { get; init; }
    public float TiltYDegrees { get; init; }
    public float OffsetUmm { get; init; }
    public float OffsetVmm { get; init; }
    public float ReliefMm { get; init; }
    public bool MirrorHorizontal { get; init; }
    public bool MirrorVertical { get; init; }
    public LogoProjectionMode Projection { get; init; } = LogoProjectionMode.Conformal;
}

public sealed record LogoInstance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid AssetId { get; init; }
    public required string Name { get; init; }
    public required LogoTransform Transform { get; init; }
    public string ColorHex { get; init; } = "#FFFFFF";
    public int FilamentIndex { get; init; }
    public bool UseImageColors { get; init; }
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public int Order { get; init; }
}

public sealed record LogoAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Png { get; init; }
    public required LogoImageFormat SourceFormat { get; init; }
}

public sealed record LogoLayer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public int Order { get; init; }
    public List<LogoInstance> Instances { get; init; } = [];
}

public sealed record LogoProject
{
    public int SchemaVersion { get; init; } = 1;
    public List<LogoAsset> Assets { get; init; } = [];
    public List<LogoLayer> Layers { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<LogoInstance> Instances =>
        Layers.Where(layer => layer.Visible)
            .OrderBy(layer => layer.Order)
            .SelectMany(layer => layer.Instances.Where(instance => instance.Visible).OrderBy(instance => instance.Order));
}

public sealed record MeshVertex(float X, float Y, float Z)
{
    public Vector3 Position => new(X, Y, Z);
}

public readonly record struct MeshTriangle(int A, int B, int C);
public sealed record LogoMeshObject(int Index, IReadOnlyList<MeshVertex> Vertices, IReadOnlyList<MeshTriangle> Triangles);

public sealed record LogoProjectionHit(
    int ObjectIndex,
    int TriangleIndex,
    int FilamentIndex,
    Guid InstanceId,
    float Alpha,
    byte Red,
    byte Green,
    byte Blue,
    bool UseImageColors);

public sealed record LogoProjectionResult(IReadOnlyList<LogoProjectionHit> Hits)
{
    public IReadOnlyDictionary<int, int[]> ToAssignments(int objectIndex, int triangleCount, int[]? basis = null)
    {
        var values = basis is { Length: > 0 } ? (int[])basis.Clone() : new int[triangleCount];
        foreach (var hit in Hits.Where(hit => hit.ObjectIndex == objectIndex))
            if ((uint)hit.TriangleIndex < (uint)values.Length) values[hit.TriangleIndex] = hit.FilamentIndex;
        return new Dictionary<int, int[]> { [objectIndex] = values };
    }
}
