using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using PolyChrom3MF.Logos;
using SkiaSharp;

namespace PolyChrom3MF.Tests;

public sealed class LogoModuleTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, LogoImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg, LogoImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp, LogoImageFormat.WebP)]
    public void Importe_les_formats_raster(SKEncodedImageFormat encoded, LogoImageFormat expected)
    {
        using var stream = RasterStream(encoded, transparent: encoded == SKEncodedImageFormat.Png);
        var raster = new LogoImageImporter().Import(stream, expected, $"test.{expected}");
        Assert.Equal(64, raster.Width);
        Assert.Equal(48, raster.Height);
        Assert.Equal(expected, raster.SourceFormat);
        Assert.Equal(64 * 48 * 4, raster.Rgba.Length);
    }

    [Fact]
    public void Importe_et_rasterise_un_svg()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
              <rect x="10" y="10" width="180" height="80" rx="10" fill="#e53935"/>
            </svg>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
        var raster = new LogoImageImporter().Import(stream, LogoImageFormat.Svg, "logo.svg", 400);
        Assert.Equal(400, raster.Width);
        Assert.Equal(200, raster.Height);
        Assert.Contains(Enumerable.Range(0, raster.PixelCount), index => raster.Rgba[index * 4 + 3] > 0);
    }

    [Fact]
    public void Svg_refuse_dtd_et_ne_charge_pas_de_ressource_externe()
    {
        const string svgWithDtd = "<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&xxe;</text></svg>";
        using var dtdStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgWithDtd));
        Assert.ThrowsAny<Exception>(() => new LogoImageImporter().Import(dtdStream, LogoImageFormat.Svg, "unsafe.svg"));

        const string external = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">
              <image href="https://example.invalid/tracker.png" width="100" height="100"/>
              <circle cx="50" cy="50" r="20" fill="red"/>
            </svg>
            """;
        using var externalStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(external));
        var raster = new LogoImageImporter().Import(externalStream, LogoImageFormat.Svg, "safe.svg", 100);
        Assert.Equal(100, raster.Width);
    }

    [Fact]
    public void Refuse_une_image_corrompue()
    {
        using var stream = new MemoryStream([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => new LogoImageImporter().Import(stream, LogoImageFormat.Png, "broken.png"));
    }

    [Fact]
    public void Conserve_la_transparence_png()
    {
        using var stream = RasterStream(SKEncodedImageFormat.Png, transparent: true);
        var raster = new LogoImageImporter().Import(stream, LogoImageFormat.Png, "transparent.png");
        Assert.Equal(0, raster.AlphaAt(0, 0));
        Assert.Equal(255, raster.AlphaAt(raster.Width / 2, raster.Height / 2));
        var roundTrip = LogoImageImporter.DecodeNormalizedPng(LogoImageImporter.EncodePng(raster));
        Assert.Equal(0, roundTrip.AlphaAt(0, 0));
        Assert.Equal(255, roundTrip.AlphaAt(roundTrip.Width / 2, roundTrip.Height / 2));
    }

    [Fact]
    public void Supprime_uniquement_le_fond_connecte_au_bord()
    {
        var raster = SolidRaster(20, 20, 255, 255, 255, 255);
        for (var y = 5; y < 15; y++)
            for (var x = 5; x < 15; x++)
            {
                Set(raster.Rgba, raster.Width, x, y, 220, 20, 20, 255);
            }
        Set(raster.Rgba, raster.Width, 10, 10, 255, 255, 255, 255);
        var editor = new LogoMaskEditor(raster);
        var removed = editor.RemoveBorderBackground(10);
        var result = editor.Snapshot();
        Assert.True(removed > 0);
        Assert.Equal(0, result.AlphaAt(0, 0));
        Assert.Equal(255, result.AlphaAt(6, 6));
        Assert.Equal(255, result.AlphaAt(10, 10));
    }

    [Fact]
    public void Gomme_et_restauration_sont_reversibles()
    {
        var editor = new LogoMaskEditor(SolidRaster(32, 32, 10, 20, 30, 255));
        Assert.True(editor.ApplyBrush(16, 16, 5, LogoBrushMode.Erase) > 0);
        Assert.Equal(0, editor.Snapshot().AlphaAt(16, 16));
        Assert.True(editor.ApplyBrush(16, 16, 5, LogoBrushMode.Restore) > 0);
        Assert.Equal(255, editor.Snapshot().AlphaAt(16, 16));
    }

    [Fact]
    public void Projection_plane_ne_touche_que_la_zone_du_logo()
    {
        var (project, instance) = ProjectWithOpaqueAsset();
        var mesh = Grid(20, 20, 1);
        var result = new LogoProjector().Project([mesh], project);
        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, hit => Assert.Equal(instance.Id, hit.InstanceId));
        Assert.True(result.Hits.Count < mesh.Triangles.Count);
    }

    [Fact]
    public void Un_seul_echantillon_opaque_ne_colore_plus_un_triangle_entier()
    {
        var raster = SolidRaster(17, 17, 255, 0, 0, 0);
        Set(raster.Rgba, raster.Width, 8, 8, 255, 0, 0, 255);
        var asset = new LogoAsset
        {
            Name = "point.png",
            Width = raster.Width,
            Height = raster.Height,
            Png = LogoImageImporter.EncodePng(raster),
            SourceFormat = LogoImageFormat.Png
        };
        var anchor = SurfaceAnchor.Create(0, 0, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX);
        var instance = new LogoInstance
        {
            AssetId = asset.Id,
            Name = "Point",
            Transform = new LogoTransform { Anchor = anchor, WidthMm = 10, HeightMm = 10 }
        };
        var project = new LogoProject
        {
            Assets = [asset],
            Layers = [new LogoLayer { Name = "Point", Instances = [instance] }]
        };
        var mesh = new LogoMeshObject(
            0,
            [new(-5, -5, 0), new(5, -5, 0), new(0, 5, 0)],
            [new(0, 1, 2)]);

        Assert.Empty(new LogoProjector().Project([mesh], project).Hits);
        Assert.Single(new LogoProjector().Project(
            [mesh],
            project,
            alphaThreshold: 128,
            includeTransparentFootprint: true).Hits);
    }

    [Theory]
    [InlineData(LogoProjectionMode.Plane)]
    [InlineData(LogoProjectionMode.Cylindrical)]
    [InlineData(LogoProjectionMode.Conformal)]
    public void Projette_sur_surface_plane_ou_courbe(LogoProjectionMode mode)
    {
        var (project, _) = ProjectWithOpaqueAsset(mode);
        var mesh = mode == LogoProjectionMode.Cylindrical ? Cylinder(64, 12, 10) : Grid(20, 20, 1);
        var result = new LogoProjector().Project([mesh], project);
        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, hit => Assert.InRange(hit.TriangleIndex, 0, mesh.Triangles.Count - 1));
    }

    [Fact]
    public void Les_copies_sont_des_instances_independantes_et_espacees()
    {
        var (_, source) = ProjectWithOpaqueAsset();
        var copies = LogoProjector.Duplicate(source, 4, 7.5f, LogoRepeatMode.Horizontal);
        Assert.Equal(4, copies.Count);
        Assert.Equal(4, copies.Select(copy => copy.Id).Distinct().Count());
        Assert.Equal([0f, 7.5f, 15f, 22.5f], copies.Select(copy => copy.Transform.OffsetUmm).ToArray());
        var changed = copies[0] with { Transform = copies[0].Transform with { WidthMm = 99 } };
        Assert.NotEqual(changed.Transform.WidthMm, copies[1].Transform.WidthMm);
    }

    [Fact]
    public void Sauvegarde_recharge_images_calques_et_positions()
    {
        var (project, instance) = ProjectWithOpaqueAsset();
        using var stream = new MemoryStream();
        new LogoProjectStore().Save(stream, project);
        stream.Position = 0;
        var loaded = new LogoProjectStore().Load(stream);
        Assert.Single(loaded.Assets);
        Assert.Single(loaded.Layers);
        var restored = Assert.Single(loaded.Layers[0].Instances);
        Assert.Equal(instance.Transform.Anchor.Position, restored.Transform.Anchor.Position);
        Assert.Equal(project.Assets[0].Png, loaded.Assets[0].Png);
    }

    [Fact]
    public void Projet_incomplet_est_refuse()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            archive.CreateEntry("wrong.json");
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => new LogoProjectStore().Load(stream));
    }

    [Fact]
    public void Annulation_interrompt_la_projection()
    {
        var (project, _) = ProjectWithOpaqueAsset();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LogoProjector().Project([Grid(50, 50, 1)], project, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Projection_200000_triangles_reste_bornee_en_temps_et_memoire()
    {
        var (project, _) = ProjectWithOpaqueAsset();
        var mesh = Grid(316, 316, .1f);
        var before = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();
        var result = new LogoProjector().Project([mesh], project);
        timer.Stop();
        var allocated = GC.GetTotalMemory(false) - before;
        Assert.NotEmpty(result.Hits);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"Projection trop lente: {timer.Elapsed}");
        Assert.True(allocated < 256 * 1024 * 1024, $"Mémoire excessive: {allocated:N0} octets");
    }

    static (LogoProject Project, LogoInstance Instance) ProjectWithOpaqueAsset(LogoProjectionMode mode = LogoProjectionMode.Plane)
    {
        var raster = SolidRaster(64, 64, 230, 20, 20, 255);
        var asset = new LogoAsset
        {
            Name = "logo.png",
            Width = 64,
            Height = 64,
            Png = LogoImageImporter.EncodePng(raster),
            SourceFormat = LogoImageFormat.Png
        };
        var anchor = SurfaceAnchor.Create(0, 0, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX);
        var instance = new LogoInstance
        {
            AssetId = asset.Id,
            Name = "Logo test",
            FilamentIndex = 2,
            Transform = new LogoTransform { Anchor = anchor, WidthMm = 10, HeightMm = 10, Projection = mode }
        };
        return (new LogoProject
        {
            Assets = [asset],
            Layers = [new LogoLayer { Name = "Logo test", Instances = [instance] }]
        }, instance);
    }

    static LogoMeshObject Grid(int columns, int rows, float step)
    {
        var vertices = new List<MeshVertex>((columns + 1) * (rows + 1));
        for (var y = 0; y <= rows; y++)
            for (var x = 0; x <= columns; x++)
                vertices.Add(new MeshVertex((x - columns / 2f) * step, (y - rows / 2f) * step, 0));
        var triangles = new List<MeshTriangle>(columns * rows * 2);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var a = y * (columns + 1) + x;
                var b = a + 1;
                var c = a + columns + 1;
                var d = c + 1;
                triangles.Add(new MeshTriangle(a, b, d));
                triangles.Add(new MeshTriangle(a, d, c));
            }
        return new LogoMeshObject(0, vertices, triangles);
    }

    static LogoMeshObject Cylinder(int segments, int rows, float radius)
    {
        var vertices = new List<MeshVertex>((segments + 1) * (rows + 1));
        for (var y = 0; y <= rows; y++)
            for (var index = 0; index <= segments; index++)
            {
                var angle = (index - segments / 2f) / segments * MathF.PI;
                vertices.Add(new MeshVertex(MathF.Sin(angle) * radius, (y - rows / 2f), radius - MathF.Cos(angle) * radius));
            }
        var triangles = new List<MeshTriangle>();
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < segments; x++)
            {
                var a = y * (segments + 1) + x;
                var b = a + 1;
                var c = a + segments + 1;
                var d = c + 1;
                triangles.Add(new MeshTriangle(a, b, d));
                triangles.Add(new MeshTriangle(a, d, c));
            }
        return new LogoMeshObject(0, vertices, triangles);
    }

    static MemoryStream RasterStream(SKEncodedImageFormat format, bool transparent)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
        canvas.DrawCircle(32, 24, 16, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return new MemoryStream(data.ToArray(), writable: false);
    }

    static LogoRaster SolidRaster(int width, int height, byte r, byte g, byte b, byte a)
    {
        var bytes = new byte[width * height * 4];
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = r;
            bytes[offset + 1] = g;
            bytes[offset + 2] = b;
            bytes[offset + 3] = a;
        }
        return new LogoRaster(width, height, bytes, LogoImageFormat.Png, "solid.png");
    }

    static void Set(byte[] bytes, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        var offset = (y * width + x) * 4;
        bytes[offset] = r;
        bytes[offset + 1] = g;
        bytes[offset + 2] = b;
        bytes[offset + 3] = a;
    }
}

public sealed class LogoApplicationIntegrationTests
{
    [Fact]
    public void Subdivision_locale_reproduit_un_trait_fin_sans_elargir_toute_la_face()
    {
        var document = DocumentGrid(2, 2);
        var basis = Proposal(document, 2);
        var bytes = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 14; x <= 17; x++)
            {
                var offset = (y * 32 + x) * 4;
                bytes[offset] = 255;
                bytes[offset + 3] = 255;
            }
        var raster = new LogoRaster(32, 32, bytes, LogoImageFormat.Png, "trait-fin.png");
        var asset = new LogoAsset
        {
            Name = "trait-fin.png",
            Width = raster.Width,
            Height = raster.Height,
            Png = LogoImageImporter.EncodePng(raster),
            SourceFormat = raster.SourceFormat
        };
        var anchor = SurfaceAnchor.Create(0, 0, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX);
        var instance = new LogoInstance
        {
            AssetId = asset.Id,
            Name = "Trait fin",
            FilamentIndex = 1,
            Transform = new LogoTransform { Anchor = anchor, WidthMm = 2, HeightMm = 2 }
        };
        var project = new LogoProject
        {
            Assets = [asset],
            Layers = [new LogoLayer { Name = "Trait fin", Instances = [instance] }]
        };

        var result = new PolyChrom3MF.App.LogoApplicationService().Apply(document, [basis], project);
        Assert.True(result.SubdivisionLevels >= 5);
        var obj = result.Document.Objects[0];
        var colors = result.Proposals[0].TriangleAssignments[0];
        var coloredCenters = obj.Triangles
            .Select((triangle, index) => (triangle, index))
            .Where(item => colors[item.index] == 1)
            .Select(item =>
            {
                var a = obj.Vertices[item.triangle.A];
                var b = obj.Vertices[item.triangle.B];
                var c = obj.Vertices[item.triangle.C];
                return (a.X + b.X + c.X) / 3;
            })
            .ToArray();

        Assert.NotEmpty(coloredCenters);
        Assert.All(coloredCenters, x => Assert.InRange(x, -.2, .2));
        Assert.Contains(colors, color => color == 0);
    }

    [Fact]
    public void Application_du_logo_ne_modifie_pas_la_coloration_hors_empreinte()
    {
        var document = DocumentGrid(20, 20);
        var basis = Proposal(document, 3);
        var project = Project(document, width: 4, filament: 1);
        var result = new PolyChrom3MF.App.LogoApplicationService().Apply(document, [basis], project);
        var before = result.Bases[0].TriangleAssignments[0];
        var after = result.Proposals[0].TriangleAssignments[0];
        var changed = after.Select((value, index) => (value, index)).Where(item => item.value != before[item.index]).ToArray();
        Assert.NotEmpty(changed);
        Assert.True(changed.Length < after.Length / 2);
        Assert.All(changed, item => Assert.Equal(1, item.value));
        Assert.Equal(before.Where((_, index) => changed.All(item => item.index != index)),
            after.Where((_, index) => changed.All(item => item.index != index)));
    }

    [Fact]
    public void Projet_poly3mf_conserve_le_logo_et_son_ancrage()
    {
        var sourcePath = SimpleStl();
        var document = new PolyChrom3MF.App.StlService().Read(sourcePath);
        var proposals = new PolyChrom3MF.App.PaletteService().Create(document, colorCount: 4);
        var project = Project(document, 5, 1);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".poly3mf");
        new PolyChrom3MF.App.ProjectService().Save(path, document, proposals, 0, 0, 0, 1, logoProject: project);
        var loaded = new PolyChrom3MF.App.ProjectService().Load(path);
        Assert.NotNull(loaded.LogoArchive);
        using var stream = new MemoryStream(loaded.LogoArchive!, writable: false);
        var restored = new LogoProjectStore().Load(stream);
        Assert.Single(restored.Assets);
        Assert.Single(restored.Instances);
        Assert.Equal(project.Instances.Single().Transform.Anchor.Position,
            restored.Instances.Single().Transform.Anchor.Position);
        File.Delete(path);
        File.Delete(sourcePath);
    }

    [Fact]
    public void Export_3mf_conserve_les_couleurs_du_logo()
    {
        var document = DocumentGrid(8, 8);
        var basis = Proposal(document, 4);
        var project = Project(document, 6, 2);
        var applied = new PolyChrom3MF.App.LogoApplicationService().Apply(document, [basis], project);
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf");
        new PolyChrom3MF.App.ThreeMfService().Export(applied.Document, applied.Proposals[0], output);
        var reopened = new PolyChrom3MF.App.ThreeMfService().Read(output);
        Assert.True(reopened.ExistingColorCount >= 2);
        Assert.True(new FileInfo(output).Length > 0);
        File.Delete(output);
    }

    [Fact]
    public void Relief_deplace_uniquement_les_sommets_sous_le_logo()
    {
        var document = DocumentGrid(20, 20);
        var basis = Proposal(document, 4);
        var project = Project(document, 4, 2);
        var sourceInstance = project.Instances.Single();
        project.Layers[0].Instances[0] = sourceInstance with
        {
            Transform = sourceInstance.Transform with { ReliefMm = 1.25f }
        };

        var result = new PolyChrom3MF.App.LogoApplicationService().Apply(document, [basis], project);
        var displaced = result.Document.Objects[0].Vertices
            .Count(vertex => Math.Abs(vertex.Z) > 1e-6);

        Assert.True(displaced > 0);
        Assert.True(displaced < result.Document.Objects[0].Vertices.Count);
        Assert.True(result.Document.SizeZ >= 1.24);
    }

    static PolyChrom3MF.App.ModelDocument DocumentGrid(int columns, int rows)
    {
        var vertices = new List<PolyChrom3MF.App.Vertex>();
        for (var y = 0; y <= rows; y++)
            for (var x = 0; x <= columns; x++)
                vertices.Add(new PolyChrom3MF.App.Vertex(x - columns / 2d, y - rows / 2d, 0));
        var triangles = new List<PolyChrom3MF.App.Triangle>();
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var a = y * (columns + 1) + x;
                var b = a + 1;
                var c = a + columns + 1;
                var d = c + 1;
                triangles.Add(new PolyChrom3MF.App.Triangle(a, b, d));
                triangles.Add(new PolyChrom3MF.App.Triangle(a, d, c));
            }
        var obj = new PolyChrom3MF.App.ModelObject(0, "1", vertices, triangles, "3D/3dmodel.model");
        return new PolyChrom3MF.App.ModelDocument(
            "memory.stl",
            new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("model")),
            "3D/3dmodel.model",
            [obj], columns, rows, 0, [], triangles.Count, null, "millimeter", 0, 0, "STL");
    }

    static PolyChrom3MF.App.ColorProposal Proposal(PolyChrom3MF.App.ModelDocument document, int colorCount)
    {
        var colors = Enumerable.Range(0, colorCount)
            .Select(index => new PolyChrom3MF.App.PaletteColor($"C{index}", index switch
            {
                0 => "#112233",
                1 => "#E53935",
                2 => "#1E88E5",
                _ => "#43A047"
            })).ToList();
        var values = Enumerable.Repeat(0, document.Objects[0].Triangles.Count).ToArray();
        return new PolyChrom3MF.App.ColorProposal("Base", "Base", colors, new Dictionary<int, int> { [0] = 0 })
        {
            TriangleAssignments = new Dictionary<int, int[]> { [0] = values }
        };
    }

    static LogoProject Project(PolyChrom3MF.App.ModelDocument document, float width, int filament)
    {
        var bytes = new byte[32 * 32 * 4];
        for (var index = 0; index < bytes.Length; index += 4)
        {
            bytes[index] = 229; bytes[index + 1] = 57; bytes[index + 2] = 53; bytes[index + 3] = 255;
        }
        var raster = new LogoRaster(32, 32, bytes, LogoImageFormat.Png, "logo.png");
        var asset = new LogoAsset
        {
            Name = "logo.png",
            Width = 32,
            Height = 32,
            Png = LogoImageImporter.EncodePng(raster),
            SourceFormat = LogoImageFormat.Png
        };
        var anchor = SurfaceAnchor.Create(0, 0, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX);
        var instance = new LogoInstance
        {
            AssetId = asset.Id,
            Name = "Logo",
            FilamentIndex = filament,
            Transform = new LogoTransform { Anchor = anchor, WidthMm = width, HeightMm = width }
        };
        return new LogoProject
        {
            Assets = [asset],
            Layers = [new LogoLayer { Name = "Logo", Instances = [instance] }]
        };
    }

    static string SimpleStl()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stl");
        File.WriteAllText(path, """
            solid logo
              facet normal 0 0 1
                outer loop
                  vertex -5 -5 0
                  vertex 5 -5 0
                  vertex 0 5 0
                endloop
              endfacet
            endsolid logo
            """);
        return path;
    }
}
