using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit;
using HelixToolkit.Maths;
using System.Numerics;
using System.Windows.Controls;
using MediaColor = System.Windows.Media.Color;
using DxPerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Vector3D = System.Windows.Media.Media3D.Vector3D;

namespace PolyChrom3MF.App;

/// <summary>Direct3D 11 viewport used for dense-model navigation and previews.</summary>
public sealed class GpuViewportHost : Grid, IDisposable
{
    readonly Viewport3DX _viewport;
    readonly DefaultEffectsManager _effects = new();
    CancellationTokenSource? _renderCancellation;
    bool _failed;

    public bool IsAvailable => !_failed;

    public GpuViewportHost()
    {
        _viewport = new Viewport3DX
        {
            EffectsManager = _effects,
            Camera = new DxPerspectiveCamera(),
            BackgroundColor = MediaColor.FromRgb(14, 18, 23),
            ShowCoordinateSystem = true,
            ShowFrameRate = false,
            IsShadowMappingEnabled = false,
            EnableDeferredRendering = true
        };
        _viewport.Items.Add(new AmbientLight3D { Color = System.Windows.Media.Colors.DimGray });
        _viewport.Items.Add(new DirectionalLight3D { Color = System.Windows.Media.Colors.White, Direction = new Vector3D(-1, -1, -2) });
        Children.Add(_viewport);
    }

    public void SetCamera(Point3D center, double radius, double yawDegrees, double pitchDegrees, double zoom)
    {
        if (_viewport.Camera is not DxPerspectiveCamera camera) return;
        var yaw = yawDegrees * Math.PI / 180; var pitch = pitchDegrees * Math.PI / 180;
        var direction = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch));
        var up = new Vector3D(-Math.Sin(pitch) * Math.Sin(yaw), -Math.Sin(pitch) * Math.Cos(yaw), Math.Cos(pitch));
        camera.Position = center + direction * (radius * 3 * zoom);
        camera.LookDirection = center - camera.Position;
        camera.UpDirection = up;
        _viewport.FixedRotationPoint = center;
    }

    public async Task RenderAsync(ModelDocument document, ColorProposal proposal, Point3D center, double radius)
    {
        if (_failed) return;
        try
        {
            var cancellation = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _renderCancellation, cancellation);
            previous?.Cancel(); previous?.Dispose();
            var data = await Task.Run(() => BuildData(document, proposal, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            while (_viewport.Items.Count > 2) _viewport.Items.RemoveAt(2);
            foreach (var meshData in data)
            {
                    var geometry = new MeshGeometry3D { Positions = new Vector3Collection(meshData.Positions), Indices = new IntCollection(meshData.Indices) };
                    geometry.UpdateNormals();
                    var color = meshData.Color;
                    var material = new PhongMaterial
                    {
                        DiffuseColor = ToColor4(color),
                        AmbientColor = ToColor4(MediaColor.Multiply(color, .28f)),
                        SpecularColor = new Color4(.18f, .18f, .18f, 1),
                        SpecularShininess = 18
                    };
                    _viewport.Items.Add(new MeshGeometryModel3D
                    {
                        Geometry = geometry,
                        Material = material,
                        CullMode = SharpDX.Direct3D11.CullMode.None,
                        IsThrowingShadow = false,
                        IsMultisampleEnabled = true
                    });
            }
            if (_viewport.Camera is not DxPerspectiveCamera camera) return;
            camera.Position = new System.Windows.Media.Media3D.Point3D(center.X + radius * 2.2, center.Y - radius * 2.2, center.Z + radius * 1.4);
            camera.LookDirection = center - camera.Position;
            camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
            camera.NearPlaneDistance = Math.Max(.01, radius / 1000);
            camera.FarPlaneDistance = Math.Max(1000, radius * 20);
            _viewport.FixedRotationPoint = center;
            _viewport.FixedRotationPointEnabled = true;
            _viewport.ZoomExtents();
        }
        catch (OperationCanceledException) { }
        catch
        {
            _failed = true;
            Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    static List<GpuMeshData> BuildData(ModelDocument document, ColorProposal proposal, CancellationToken cancellationToken)
    {
        var result = new List<GpuMeshData>();
        foreach (var obj in document.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var positions = obj.Vertices.Select(vertex => new Vector3((float)vertex.X, (float)vertex.Y, (float)vertex.Z)).ToArray();
            var byColor = Enumerable.Range(0, proposal.Colors.Count).Select(_ => new List<int>()).ToArray();
            var assignments = proposal.TriangleAssignments.GetValueOrDefault(obj.Index);
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                if ((triangleIndex & 32767) == 0) cancellationToken.ThrowIfCancellationRequested();
                var assigned = assignments is null ? proposal.Assignments.GetValueOrDefault(obj.Index, 0) : assignments[triangleIndex];
                assigned = Math.Clamp(assigned, 0, byColor.Length - 1);
                var triangle = obj.Triangles[triangleIndex];
                byColor[assigned].Add(triangle.A); byColor[assigned].Add(triangle.B); byColor[assigned].Add(triangle.C);
            }
            for (var colorIndex = 0; colorIndex < byColor.Length; colorIndex++)
                if (byColor[colorIndex].Count > 0)
                    result.Add(new GpuMeshData(positions, byColor[colorIndex].ToArray(), proposal.Colors[colorIndex].Color));
        }
        return result;
    }

    static Color4 ToColor4(MediaColor color) => new(color.ScR, color.ScG, color.ScB, color.ScA);

    public void Dispose()
    {
        _renderCancellation?.Cancel(); _renderCancellation?.Dispose();
        _effects.Dispose();
        _viewport.Dispose();
    }

    sealed record GpuMeshData(Vector3[] Positions, int[] Indices, MediaColor Color);
}
