using System.Collections.Concurrent;
using System.Numerics;

namespace PolyChrom3MF.Logos;

public sealed class LogoProjector
{
    public LogoProjectionResult Project(
        IReadOnlyList<LogoMeshObject> objects,
        LogoProject project,
        byte alphaThreshold = 16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(project);
        var assets = project.Assets.ToDictionary(asset => asset.Id);
        var hits = new Dictionary<(int Object, int Triangle), LogoProjectionHit>();
        foreach (var instance in project.Instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(instance);
            if (!assets.TryGetValue(instance.AssetId, out var asset))
                throw new InvalidDataException($"Image absente pour le logo « {instance.Name} ».");
            var raster = LogoImageImporter.DecodeNormalizedPng(asset.Png, asset.Name);
            var target = objects.FirstOrDefault(item => item.Index == instance.Transform.Anchor.ObjectIndex)
                         ?? throw new InvalidDataException($"Objet cible absent pour le logo « {instance.Name} ».");
            ProjectInstance(target, raster, instance, alphaThreshold, hits, cancellationToken);
        }
        return new LogoProjectionResult(hits.Values.OrderBy(hit => hit.ObjectIndex).ThenBy(hit => hit.TriangleIndex).ToArray());
    }

    static void ProjectInstance(
        LogoMeshObject mesh,
        LogoRaster raster,
        LogoInstance instance,
        byte alphaThreshold,
        Dictionary<(int Object, int Triangle), LogoProjectionHit> hits,
        CancellationToken cancellationToken)
    {
        var transform = instance.Transform;
        var radians = transform.RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var tangent = transform.Anchor.Tangent;
        var bitangent = transform.Anchor.Bitangent;
        var normal = transform.Anchor.Normal;

        LogoProjectionHit? ProjectTriangle(int triangleIndex)
        {
            var triangle = mesh.Triangles[triangleIndex];
            if ((uint)triangle.A >= (uint)mesh.Vertices.Count ||
                (uint)triangle.B >= (uint)mesh.Vertices.Count ||
                (uint)triangle.C >= (uint)mesh.Vertices.Count)
                throw new InvalidDataException("Triangle faisant référence à un sommet absent.");
            var a = mesh.Vertices[triangle.A].Position;
            var b = mesh.Vertices[triangle.B].Position;
            var c = mesh.Vertices[triangle.C].Position;
            var center = (a + b + c) / 3f;
            var triangleNormal = Vector3.Cross(b - a, c - a);
            if (triangleNormal.LengthSquared() < 1e-12f) return null;
            triangleNormal = Vector3.Normalize(triangleNormal);
            if (Vector3.Dot(triangleNormal, normal) < -.15f) return null;

            byte alpha = 0, red = 0, green = 0, blue = 0;
            void Sample(Vector3 sample)
            {
                var delta = sample - transform.Anchor.Position;
                var (localU, localV) = Coordinates(delta, transform, tangent, bitangent, normal);
                localU -= transform.OffsetUmm;
                localV -= transform.OffsetVmm;
                var rotatedU = localU * cos + localV * sin;
                var rotatedV = -localU * sin + localV * cos;
                if (transform.MirrorHorizontal) rotatedU = -rotatedU;
                if (transform.MirrorVertical) rotatedV = -rotatedV;
                var u = rotatedU / transform.WidthMm + .5f;
                var v = .5f - rotatedV / transform.HeightMm;
                if (u < 0 || u > 1 || v < 0 || v > 1) return;
                var x = Math.Clamp((int)MathF.Round(u * (raster.Width - 1)), 0, raster.Width - 1);
                var y = Math.Clamp((int)MathF.Round(v * (raster.Height - 1)), 0, raster.Height - 1);
                var offset = (y * raster.Width + x) * 4;
                var candidate = raster.Rgba[offset + 3];
                if (candidate <= alpha) return;
                alpha = candidate;
                red = raster.Rgba[offset];
                green = raster.Rgba[offset + 1];
                blue = raster.Rgba[offset + 2];
            }
            Sample(center);
            Sample(a);
            Sample(b);
            Sample(c);
            Sample((a + b) * .5f);
            Sample((b + c) * .5f);
            Sample((c + a) * .5f);
            if (alpha < alphaThreshold) return null;
            return new LogoProjectionHit(
                mesh.Index, triangleIndex, instance.FilamentIndex, instance.Id, alpha / 255f,
                red, green, blue, instance.UseImageColors);
        }

        if (mesh.Triangles.Count < 100_000)
        {
            for (var triangleIndex = 0; triangleIndex < mesh.Triangles.Count; triangleIndex++)
            {
                if ((triangleIndex & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (ProjectTriangle(triangleIndex) is { } hit)
                    hits[(mesh.Index, triangleIndex)] = hit;
            }
            return;
        }

        var batches = new ConcurrentBag<List<LogoProjectionHit>>();
        Parallel.ForEach(
            Partitioner.Create(0, mesh.Triangles.Count, 16_384),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
            range =>
            {
                var local = new List<LogoProjectionHit>();
                for (var triangleIndex = range.Item1; triangleIndex < range.Item2; triangleIndex++)
                    if (ProjectTriangle(triangleIndex) is { } hit) local.Add(hit);
                if (local.Count > 0) batches.Add(local);
            });
        foreach (var batch in batches)
            foreach (var hit in batch)
                hits[(hit.ObjectIndex, hit.TriangleIndex)] = hit;
    }

    static (float U, float V) Coordinates(
        Vector3 delta,
        LogoTransform transform,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 normal)
    {
        var tiltX = transform.TiltXDegrees * MathF.PI / 180f;
        var tiltY = transform.TiltYDegrees * MathF.PI / 180f;
        if (MathF.Abs(tiltX) > 1e-5f)
        {
            var rotation = Quaternion.CreateFromAxisAngle(tangent, tiltX);
            bitangent = Vector3.Transform(bitangent, rotation);
            normal = Vector3.Transform(normal, rotation);
        }
        if (MathF.Abs(tiltY) > 1e-5f)
        {
            var rotation = Quaternion.CreateFromAxisAngle(bitangent, tiltY);
            tangent = Vector3.Transform(tangent, rotation);
            normal = Vector3.Transform(normal, rotation);
        }
        var u = Vector3.Dot(delta, tangent);
        var v = Vector3.Dot(delta, bitangent);
        if (transform.Projection == LogoProjectionMode.Plane) return (u, v);
        if (transform.Projection == LogoProjectionMode.Conformal)
        {
            var depth = Vector3.Dot(delta, normal);
            var correction = MathF.Max(.25f, 1f - MathF.Abs(depth) / MathF.Max(transform.WidthMm, transform.HeightMm));
            return (u / correction, v);
        }
        var radius = MathF.Max(transform.WidthMm / (2 * MathF.PI), 1e-3f);
        var depthU = Vector3.Dot(delta, normal);
        var angle = MathF.Atan2(u, radius + depthU);
        return (angle * radius, v);
    }

    public static IReadOnlyList<LogoInstance> Duplicate(
        LogoInstance source,
        int copies,
        float spacingMm,
        LogoRepeatMode mode)
    {
        if (copies < 1 || copies > 1024) throw new ArgumentOutOfRangeException(nameof(copies));
        if (!float.IsFinite(spacingMm)) throw new ArgumentOutOfRangeException(nameof(spacingMm));
        var instances = new List<LogoInstance>(copies);
        for (var index = 0; index < copies; index++)
        {
            var offset = index * spacingMm;
            var transform = mode switch
            {
                LogoRepeatMode.Vertical => source.Transform with { OffsetVmm = source.Transform.OffsetVmm + offset },
                LogoRepeatMode.Circular => source.Transform with
                {
                    RotationDegrees = source.Transform.RotationDegrees + 360f * index / copies,
                    OffsetUmm = source.Transform.OffsetUmm + MathF.Cos(2 * MathF.PI * index / copies) * spacingMm,
                    OffsetVmm = source.Transform.OffsetVmm + MathF.Sin(2 * MathF.PI * index / copies) * spacingMm
                },
                _ => source.Transform with { OffsetUmm = source.Transform.OffsetUmm + offset }
            };
            instances.Add(source with { Id = Guid.NewGuid(), Name = $"{source.Name} {index + 1}", Transform = transform, Order = source.Order + index });
        }
        return instances;
    }

    public static void Validate(LogoInstance instance)
    {
        if (instance.AssetId == Guid.Empty) throw new InvalidDataException("Image du logo absente.");
        if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Length > 260)
            throw new InvalidDataException("Nom du logo invalide.");
        if (instance.FilamentIndex is < 0 or > 31) throw new InvalidDataException("Indice de filament invalide.");
        var transform = instance.Transform;
        if (!float.IsFinite(transform.WidthMm) || !float.IsFinite(transform.HeightMm) ||
            transform.WidthMm <= 0 || transform.HeightMm <= 0)
            throw new InvalidDataException("Dimensions du logo invalides.");
        if (transform.Anchor.Normal.LengthSquared() < .5f ||
            transform.Anchor.Tangent.LengthSquared() < .5f ||
            transform.Anchor.Bitangent.LengthSquared() < .5f)
            throw new InvalidDataException("Repère de surface invalide.");
    }
}
