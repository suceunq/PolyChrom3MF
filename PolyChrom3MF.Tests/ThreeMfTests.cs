using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PolyChrom3MF.App;

namespace PolyChrom3MF.Tests;

public class ThreeMfTests
{
    const string Ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    static string Sample(int objectCount = 1, string unit = "millimeter", bool colors = false, bool components = false)
    {
        XNamespace ns = Ns;
        var resources = new XElement(ns + "resources");
        if (colors) resources.Add(new XElement(ns + "basematerials", new XAttribute("id", 5), new XElement(ns + "base", new XAttribute("name", "Rouge"), new XAttribute("displaycolor", "#FF0000"))));
        for (var id = 1; id <= objectCount; id++)
            resources.Add(new XElement(ns + "object", new XAttribute("id", id), new XAttribute("type", "model"),
                new XElement(ns + "mesh", new XElement(ns + "vertices",
                    new XElement(ns + "vertex", new XAttribute("x", (id - 1) * 20), new XAttribute("y", 0), new XAttribute("z", 0)),
                    new XElement(ns + "vertex", new XAttribute("x", (id - 1) * 20 + 10), new XAttribute("y", 0), new XAttribute("z", 0)),
                    new XElement(ns + "vertex", new XAttribute("x", (id - 1) * 20), new XAttribute("y", 10), new XAttribute("z", 5))),
                    new XElement(ns + "triangles", new XElement(ns + "triangle", new XAttribute("v1", 0), new XAttribute("v2", 1), new XAttribute("v3", 2))))));
        if (components) resources.Add(new XElement(ns + "object", new XAttribute("id", 100), new XAttribute("type", "model"), new XElement(ns + "components", new XElement(ns + "component", new XAttribute("objectid", 1), new XAttribute("transform", "1 0 0 0 1 0 0 0 1 30 0 0")))));
        var build = new XElement(ns + "build", Enumerable.Range(1, objectCount).Select(id => new XElement(ns + "item", new XAttribute("objectid", id))));
        return Archive(new XDocument(new XElement(ns + "model", new XAttribute("unit", unit), resources, build)));
    }

    static string Archive(XDocument model, string? extraEntry = null)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("3D/3dmodel.model");
        using (var writer = entry.Open()) model.Save(writer);
        if (extraEntry is not null) using (zip.CreateEntry(extraEntry).Open()) { }
        return path;
    }

    static string Png()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 };
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
        return path;
    }

    static string JpegWithBackground()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        const int size = 24;
        var pixels = Enumerable.Repeat((byte)255, size * size * 3).ToArray();
        for (var y = 7; y < 17; y++)
            for (var x = 7; x < 17; x++)
            {
                var offset = (y * size + x) * 3;
                pixels[offset] = 15; pixels[offset + 1] = 30; pixels[offset + 2] = 210;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
        return path;
    }

    static string MultipartSample()
    {
        XNamespace ns = Ns; XNamespace production = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
        var root = new XDocument(new XElement(ns + "model", new XAttribute("unit", "centimeter"), new XAttribute(XNamespace.Xmlns + "p", production),
            new XElement(ns + "resources", new XElement(ns + "object", new XAttribute("id", 2), new XAttribute("type", "model"), new XElement(ns + "components", new XElement(ns + "component", new XAttribute(production + "path", "/3D/Objects/object_1.model"), new XAttribute("objectid", 1))))),
            new XElement(ns + "build", new XElement(ns + "item", new XAttribute("objectid", 2)))));
        var child = new XDocument(new XElement(ns + "model", new XAttribute("unit", "centimeter"),
            new XElement(ns + "resources", new XElement(ns + "object", new XAttribute("id", 1), new XAttribute("type", "model"), new XElement(ns + "mesh",
                new XElement(ns + "vertices", new XElement(ns + "vertex", new XAttribute("x", 0), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 1), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 0), new XAttribute("y", 1), new XAttribute("z", 1))),
                new XElement(ns + "triangles", new XElement(ns + "triangle", new XAttribute("v1", 0), new XAttribute("v2", 1), new XAttribute("v3", 2))))))));
        var path = Archive(root); using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.CreateEntry("3D/Objects/object_1.model"); using var output = entry.Open(); child.Save(output); return path;
    }

    static string ComponentTransformSample()
    {
        XNamespace ns = Ns;
        var resources = new XElement(ns + "resources",
            new XElement(ns + "object", new XAttribute("id", 1), new XAttribute("type", "model"), new XElement(ns + "mesh",
                new XElement(ns + "vertices", new XElement(ns + "vertex", new XAttribute("x", 0), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 10), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 0), new XAttribute("y", 10), new XAttribute("z", 0))),
                new XElement(ns + "triangles", new XElement(ns + "triangle", new XAttribute("v1", 0), new XAttribute("v2", 1), new XAttribute("v3", 2))))),
            new XElement(ns + "object", new XAttribute("id", 2), new XAttribute("type", "model"), new XElement(ns + "components", new XElement(ns + "component", new XAttribute("objectid", 1), new XAttribute("transform", "1 0 0 0 1 0 0 0 1 5 0 0")))),
            new XElement(ns + "object", new XAttribute("id", 3), new XAttribute("type", "model"), new XElement(ns + "mesh",
                new XElement(ns + "vertices", new XElement(ns + "vertex", new XAttribute("x", 1000), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 1010), new XAttribute("y", 0), new XAttribute("z", 0)), new XElement(ns + "vertex", new XAttribute("x", 1000), new XAttribute("y", 10), new XAttribute("z", 0))),
                new XElement(ns + "triangles", new XElement(ns + "triangle", new XAttribute("v1", 0), new XAttribute("v2", 1), new XAttribute("v3", 2))))));
        var model = new XDocument(new XElement(ns + "model", new XAttribute("unit", "millimeter"), resources,
            new XElement(ns + "build", new XElement(ns + "item", new XAttribute("objectid", 2), new XAttribute("transform", "2 0 0 0 2 0 0 0 2 100 0 0")))));
        return Archive(model);
    }

    static string AsciiStl()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stl");
        File.WriteAllText(path, "solid test\n facet normal 0 0 1\n outer loop\n vertex 0 0 0\n vertex 10 0 0\n vertex 0 10 2\n endloop\n endfacet\nendsolid test"); return path;
    }

    static string BinaryStl()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stl"); using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[80]); writer.Write((uint)1); for (var i = 0; i < 3; i++) writer.Write(0f);
        foreach (var value in new[] { 0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 2f }) writer.Write(value); writer.Write((ushort)0); return path;
    }

    static ModelDocument FunDocument()
    {
        var vertices = new List<Vertex>(); var triangles = new List<Triangle>(); const int side = 18;
        for (var y = 0; y <= side; y++)
            for (var x = 0; x <= side; x++)
                vertices.Add(new Vertex(x, y, Math.Sin(x * .55) * 3 + Math.Cos(y * .4) * 2));
        for (var y = 0; y < side; y++) for (var x = 0; x < side; x++)
        {
            var a = y * (side + 1) + x; var b = a + 1; var c = a + side + 1; var d = c + 1;
            triangles.Add(new Triangle(a, b, d)); triangles.Add(new Triangle(a, d, c));
        }
        var obj = new ModelObject(0, "fun", vertices, triangles, "3D/3dmodel.model");
        return new ModelDocument("fun.3mf", new XDocument(), "3D/3dmodel.model", [obj], side, side, 10, [], triangles.Count, null, "millimeter", 0, 0, "3MF");
    }

    [Fact] public void Lit_un_3mf_valide() { var d = new ThreeMfService().Read(Sample()); Assert.Single(d.Objects); Assert.Equal(1, d.TriangleCount); Assert.Equal(10, d.SizeX); }
    [Fact] public void Lit_plusieurs_objets() { var d = new ThreeMfService().Read(Sample(4)); Assert.Equal(4, d.Objects.Count); Assert.Equal(4, d.TriangleCount); }
    [Fact] public void Lit_maillage_dans_fragment_production() { var d = new ThreeMfService().Read(MultipartSample()); Assert.Single(d.Objects); Assert.Equal(1, d.ComponentCount); Assert.Equal(10, d.SizeX); Assert.Contains("multipartie", d.Warning); }
    [Fact] public void Assemble_les_composants_et_ignore_les_maillages_non_instancies() { var d = new ThreeMfService().Read(ComponentTransformSample()); Assert.Single(d.Objects); Assert.Equal(20, d.SizeX, 6); Assert.Equal(110, d.Objects[0].Vertices.Min(vertex => vertex.X), 6); Assert.Equal(130, d.Objects[0].Vertices.Max(vertex => vertex.X), 6); }
    [Fact] public void Exporte_couleur_dans_fragment_production() { var s = new ThreeMfService(); var d = s.Read(MultipartSample()); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); s.Export(d, new PaletteService().Create(1)[0], output); var reopened = s.Read(output); Assert.True(reopened.ExistingColorCount >= 1); Assert.Equal(1, reopened.TriangleCount); }
    [Fact] public void Detecte_composant_et_couleur_existante() { var d = new ThreeMfService().Read(Sample(colors: true, components: true)); Assert.Equal(1, d.ComponentCount); Assert.Equal(1, d.ExistingColorCount); }
    [Fact] public void Convertit_les_pouces_en_millimetres() { var d = new ThreeMfService().Read(Sample(unit: "inch")); Assert.Equal(254, d.SizeX, 3); }
    [Fact] public void Refuse_extension_invalide() { var p = Path.GetTempFileName(); Assert.Throws<InvalidDataException>(() => new ThreeMfService().Read(p)); }
    [Fact] public void Refuse_fichier_vide() { var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); File.WriteAllBytes(p, []); Assert.Throws<InvalidDataException>(() => new ThreeMfService().Read(p)); }
    [Fact] public void Refuse_archive_corrompue() { var p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); File.WriteAllText(p, "pas un zip"); Assert.Throws<InvalidDataException>(() => new ThreeMfService().Read(p)); }
    [Fact] public void Refuse_traversee_de_chemin_zip() { var p = Sample(); using (var z = ZipFile.Open(p, ZipArchiveMode.Update)) using (z.CreateEntry("../danger.xml").Open()) { } Assert.Throws<InvalidDataException>(() => new ThreeMfService().Read(p)); }
    [Fact] public void Reconnait_un_stl_binaire_de_plus_de_500000_triangles_sans_plafond_arbitraire() { const uint count = 500_001; var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stl"); using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) { stream.SetLength(84L + count * 50L); stream.Position = 80; using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true); writer.Write(count); } Assert.True(StlService.IsBinary(path)); }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(8)] public void Chaque_proposition_contient_exactement_quatre_couleurs(int objects) { var p = new PaletteService().Create(objects); Assert.Equal(4, p.Count); Assert.All(p, x => Assert.Equal(4, x.Colors.Count)); }
    [Fact] public void Palettes_sont_distinctes() { var p = new PaletteService().Create(4); Assert.Equal(4, p.Select(x => string.Join(',', x.Colors.Select(c => c.Hex))).Distinct().Count()); }
    [Fact] public void Utilise_uniquement_filaments_personnalises() { var source = new[] { "#010203", "#AABBCC", "#445566", "#DDEEFF" }; var p = new PaletteService().Create(4, source); Assert.All(p.SelectMany(x => x.Colors), c => Assert.Contains(c.Hex, source)); }
    [Fact] public void Quatre_couleurs_sont_reparties_sur_les_triangles() { var d = new ThreeMfService().Read(Sample(4)); var proposals = new PaletteService().Create(d); Assert.All(proposals, p => Assert.Equal(4, p.TriangleAssignments.SelectMany(x => x.Value).Distinct().Count())); }
    [Fact] public void Regeneration_produit_de_nouvelles_palettes() { var first = new PaletteService().Create(1, generation: 0); var second = new PaletteService().Create(1, generation: 1); Assert.NotEqual(string.Join(',', first[0].Colors.Select(c => c.Hex)), string.Join(',', second[0].Colors.Select(c => c.Hex))); }
    [Fact] public void Mode_fun_produit_quatre_styles_decoratifs() { var p = new PaletteService().Create(FunDocument(), fun: true); Assert.Equal(4, p.Count); Assert.All(p, x => Assert.StartsWith("Fun ", x.Name)); }
    [Fact] public void Chaque_motif_fun_utilise_au_moins_quatre_couleurs() { var p = new PaletteService().Create(FunDocument(), fun: true); Assert.All(p, x => Assert.Equal(4, x.TriangleAssignments.SelectMany(a => a.Value).Distinct().Count())); }
    [Fact] public void Regeneration_fun_change_les_motifs() { var d = FunDocument(); var first = new PaletteService().Create(d, generation: 0, fun: true); var second = new PaletteService().Create(d, generation: 1, fun: true); Assert.All(Enumerable.Range(0, 4), i => Assert.NotEqual(first[i].TriangleAssignments[0], second[i].TriangleAssignments[0])); }
    [Fact] public void Detecte_un_slicer_installe() { var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(folder); var executable = Path.Combine(folder, "OrcaSlicer.exe"); File.WriteAllBytes(executable, [0]); var detected = new SlicerDetectionService().Detect([executable]); Assert.Contains(detected, x => x.Name == "OrcaSlicer" && x.Path == executable); }
    [Fact] public void Detecte_snapmaker_orca_actuel() { var folder = Path.Combine(Path.GetTempPath(), "Snapmaker_Orca", Guid.NewGuid().ToString()); Directory.CreateDirectory(folder); var executable = Path.Combine(folder, "snapmaker-orca.exe"); File.WriteAllBytes(executable, [0]); var detected = new SlicerDetectionService().Detect([executable]); Assert.Contains(detected, x => x.Name == "Snapmaker Orca" && x.Path == executable); }
    [Fact] public void Genere_huit_couleurs_distinctes() { var proposals = new PaletteService().Create(FunDocument(), fun: true, colorCount: 8); Assert.All(proposals, p => { Assert.Equal(8, p.Colors.Count); Assert.Equal(8, p.Colors.Select(c => c.Hex).Distinct().Count()); Assert.Equal(8, p.TriangleAssignments.SelectMany(x => x.Value).Distinct().Count()); }); }
    [Fact] public void Genere_et_repartit_deux_couleurs() { var proposals = new PaletteService().Create(FunDocument(), fun: true, colorCount: 2); Assert.All(proposals, proposal => { Assert.Equal(2, proposal.Colors.Count); Assert.Equal(2, proposal.TriangleAssignments.SelectMany(pair => pair.Value).Distinct().Count()); }); }
    [Fact] public void Exporte_deux_couleurs_reelles() { var service = new ThreeMfService(); var document = service.Read(Sample(2)); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); service.Export(document, new PaletteService().Create(document, colorCount: 2)[0], output); Assert.Equal(2, service.Read(output).ExistingColorCount); }
    [Fact] public void Exporte_huit_couleurs_reelles() { var service = new ThreeMfService(); var document = service.Read(Sample(8)); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); service.Export(document, new PaletteService().Create(document, fun: true, colorCount: 8)[0], output); Assert.Equal(8, service.Read(output).ExistingColorCount); }
    [Fact] public void Exporte_et_relit_sans_perte() { var p = Sample(2); var s = new ThreeMfService(); var d = s.Read(p); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); var report = s.ExportAndValidate(d, new PaletteService().Create(2)[0], output, true); var r = s.Read(output); Assert.Equal(2, r.Objects.Count); Assert.Equal(2, r.TriangleCount); Assert.Contains("dimensions identiques", report); Assert.True(r.ExistingColorCount >= 2); }
    [Fact] public void Peut_exporter_sur_le_fichier_source_sans_le_corrompre() { var path = Sample(); var service = new ThreeMfService(); var document = service.Read(path); service.Export(document, new PaletteService().Create(document)[0], path); var reopened = service.Read(path); Assert.Equal(document.TriangleCount, reopened.TriangleCount); Assert.True(reopened.ExistingColorCount >= 4); }
    [Fact] public void Export_preserve_les_entrees_originales() { var p = Sample(); using (var z = ZipFile.Open(p, ZipArchiveMode.Update)) using (var w = new StreamWriter(z.CreateEntry("Metadata/keep.txt").Open())) w.Write("conserver"); var s = new ThreeMfService(); var d = s.Read(p); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); s.Export(d, new PaletteService().Create(1)[0], output); using var result = ZipFile.OpenRead(output); Assert.NotNull(result.GetEntry("Metadata/keep.txt")); }
    [Fact] public void Projet_portable_conserve_modele_et_affectations() { var source = Sample(2); var d = new ThreeMfService().Read(source); var proposals = new PaletteService().Create(d); proposals[0].Assignments[1] = 0; proposals[0].TriangleAssignments[0][0] = 3; var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".poly3mf"); var service = new ProjectService(); service.Save(path, d, proposals, 0, 1, 2, 3, funMode: true, colorCount: 8); File.Delete(source); var loaded = service.Load(path); Assert.True(File.Exists(loaded.SourcePath)); Assert.Equal(2, new ThreeMfService().Read(loaded.SourcePath).TriangleCount); Assert.Equal(0, loaded.Assignments[0][1]); Assert.Equal(3, loaded.TriangleAssignments![0][0][0]); Assert.Equal(3, loaded.Zoom); Assert.True(loaded.FunMode); Assert.Equal(8, loaded.ColorCount); }
    [Fact] public void Parametres_corrompus_sont_assainis() { var settings = new AppSettings { Theme = "inconnu", ExportFolder = "", PreferredSlicer = null!, ColorCount = 99, FilamentColors = ["incorrect", "#aabbcc", "#AABBCC"] }; SettingsService.Normalize(settings); Assert.Equal("Sombre", settings.Theme); Assert.Equal(32, settings.ColorCount); Assert.Equal("", settings.PreferredSlicer); Assert.Equal(["#AABBCC"], settings.FilamentColors); Assert.False(string.IsNullOrWhiteSpace(settings.ExportFolder)); }
    [Fact] public void Refuse_un_projet_aux_couleurs_invalides() { var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".poly3mf"); File.WriteAllText(path, "{\"SourcePath\":\"x.3mf\",\"SelectedProposal\":0,\"Proposals\":[[\"danger\",\"#000000\",\"#111111\",\"#222222\"]],\"Assignments\":[],\"Yaw\":0,\"Pitch\":0,\"Zoom\":1,\"Generation\":0,\"ColorCount\":4}"); Assert.Throws<InvalidDataException>(() => new ProjectService().Load(path)); }
    [Fact] public void Refuse_un_projet_aux_listes_absentes() { var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".poly3mf"); File.WriteAllText(path, "{\"SourcePath\":\"x.3mf\",\"SelectedProposal\":0,\"Proposals\":null,\"Assignments\":null,\"Yaw\":0,\"Pitch\":0,\"Zoom\":1,\"Generation\":0,\"ColorCount\":4}"); Assert.Throws<InvalidDataException>(() => new ProjectService().Load(path)); }
    [Fact] public void Analyse_une_release_github_securisee() { var update = UpdateService.ParseRelease("{\"tag_name\":\"v2.4.1\",\"html_url\":\"https://github.com/suceunq/PolyChrom3MF/releases/tag/v2.4.1\",\"body\":\"## Nouveautés\\n- Projets portables\",\"assets\":[{\"name\":\"PolyChrom3MF_Setup_x64.exe\",\"browser_download_url\":\"https://github.com/suceunq/PolyChrom3MF/releases/download/v2.4.1/PolyChrom3MF_Setup_x64.exe\",\"digest\":\"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"size\":123456}]}"); Assert.Equal(new Version(2, 4, 1), update.Version); Assert.Equal("v2.4.1", update.Tag); Assert.Equal(123456, update.Size); Assert.Contains("Projets portables", update.ReleaseNotes); }
    [Fact] public void Compare_signature_mise_a_jour_sans_fuite_temporelle() { Assert.True(UpdateService.DigestMatches(new string('A', 64), new string('a', 64))); Assert.False(UpdateService.DigestMatches(new string('A', 64), new string('B', 64))); }
    [Fact] public void Resume_de_mise_a_jour_possede_un_texte_de_secours() { Assert.False(string.IsNullOrWhiteSpace(UpdateService.ReleaseSummary("**Full Changelog**: https://github.com/exemple"))); }
    [Fact] public void Lien_de_don_paypal_est_officiel_et_securise() { Assert.True(DonationService.IsOfficialPayPalUrl(DonationService.DonationUrl)); Assert.True(DonationService.IsOfficialPayPalUrl("https://paypal.me/exemple")); Assert.False(DonationService.IsOfficialPayPalUrl("http://paypal.me/exemple")); Assert.False(DonationService.IsOfficialPayPalUrl("https://paypal.com.exemple.org/donate")); Assert.False(DonationService.IsOfficialPayPalUrl("https://www.paypal.com/signin")); }
    [Fact] public void Applique_un_png_sur_les_triangles_avec_la_palette_active() { var document = FunDocument(); var proposal = new PaletteService().Create(document, colorCount: 4)[0]; var result = new PatternService().Apply(document, proposal, new PatternSettings(Png())); Assert.True(result.ColoredTriangles > 0); Assert.All(proposal.TriangleAssignments.SelectMany(pair => pair.Value), color => Assert.InRange(color, 0, 3)); }
    [Theory]
    [InlineData(255, 255, 255, false, true)]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(255, 255, 255, true, false)]
    [InlineData(0, 0, 0, true, true)]
    public void Mode_logo_detecte_forme_claire_ou_sombre(byte red, byte green, byte blue, bool invert, bool expected) { Assert.Equal(expected, PatternService.IsLogoPixel(red, green, blue, 128, invert)); }
    [Fact] public void Refuse_un_indice_de_couleur_logo_invalide() { Assert.Throws<InvalidDataException>(() => PatternService.ValidateSettings(new PatternSettings("motif.png", MonochromeLogo: true, LogoColorIndex: 32))); }
    [Fact] public void Couverture_repetee_etend_une_projection_frontale() { var result = PatternService.ApplyCoverage(.25, .75, false, PatternMode.Front, true); Assert.Equal(.75, result.U, 8); Assert.Equal(2.25, result.V, 8); Assert.True(result.Repeats); }
    [Fact] public void Couverture_desactivee_preserve_la_projection_originale() { var result = PatternService.ApplyCoverage(.25, .75, false, PatternMode.Front, false); Assert.Equal(.25, result.U, 8); Assert.Equal(.75, result.V, 8); Assert.False(result.Repeats); }
    [Fact] public void Ancien_reglage_de_motif_active_la_couverture_par_defaut() { var settings = JsonSerializer.Deserialize<PatternSettings>("{\"ImagePath\":\"motif.png\"}"); Assert.NotNull(settings); Assert.True(settings!.RepeatAcrossModel); }
    [Fact] public void Apercu_du_motif_respecte_l_annulation_du_calcul() { var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => new PatternService().Apply(FunDocument(), new PaletteService().Create(FunDocument())[0], new PatternSettings(Png()), cancellationToken: cancellation.Token)); }
    [Fact] public void Convertit_un_jpg_en_png_et_supprime_le_fond_des_bords() { var output = PatternService.PrepareImage(JpegWithBackground(), true, 35); Assert.Equal(".png", Path.GetExtension(output)); using var stream = File.OpenRead(output); var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); Assert.Equal(0, pixels[3]); var center = ((converted.PixelHeight / 2) * converted.PixelWidth + converted.PixelWidth / 2) * 4; Assert.True(pixels[center + 3] > 0); }
    [Fact] public async Task Prepare_un_jpg_sur_un_thread_de_fond_sans_objet_wpf_partage() { var source = JpegWithBackground(); var output = await Task.Run(() => PatternService.PrepareImage(source, true, 35)); Assert.True(File.Exists(output)); PatternService.ValidateImage(output); }
    [Fact] public void Nettoie_les_notes_de_mise_a_jour_pour_la_fenetre() { var notes = WhatsNewWindow.NormalizeNotes("- Ajout important\n* Correction utile\n\n"); Assert.Equal(["Ajout important", "Correction utile"], notes); }
    [Fact] public void Projet_portable_embarque_le_png_du_motif() { var source = Sample(); var document = new ThreeMfService().Read(source); var proposals = new PaletteService().Create(document); var png = Png(); var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".poly3mf"); new ProjectService().Save(path, document, proposals, 0, 1, 2, 3, pattern: new PatternSettings(png, PatternMode.Repeated, 75, DisplayName: "motif-test.png")); File.Delete(png); var loaded = new ProjectService().Load(path); Assert.NotNull(loaded.Pattern); Assert.True(File.Exists(loaded.Pattern!.ImagePath)); Assert.Equal(PatternMode.Repeated, loaded.Pattern.Mode); Assert.Equal("motif-test.png", loaded.Pattern.DisplayName); }
    [Fact] public void Motif_png_est_exporte_dans_un_3mf_valide() { var source = Sample(4); var service = new ThreeMfService(); var document = service.Read(source); var proposal = new PaletteService().Create(document)[0]; new PatternService().Apply(document, proposal, new PatternSettings(Png(), PatternMode.Cylindrical)); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); service.Export(document, proposal, output); var reopened = service.Read(output); Assert.Equal(document.TriangleCount, reopened.TriangleCount); Assert.True(reopened.ExistingColorCount >= 1); }
    [Fact] public void Refuse_un_motif_ciblant_un_objet_absent() { var document = FunDocument(); var proposal = new PaletteService().Create(document)[0]; Assert.Throws<InvalidDataException>(() => new PatternService().Apply(document, proposal, new PatternSettings(Png(), TargetObject: 999))); }
    [Fact] public void Refuse_un_nom_de_motif_demesure() { Assert.Throws<InvalidDataException>(() => PatternService.ValidateSettings(new PatternSettings("motif.png", DisplayName: new string('x', 261)))); }
    [Fact] public void Refuse_des_affectations_ne_correspondant_pas_au_modele() { var document = FunDocument(); var data = new ProjectData("model/test.3mf", 0, [["#000000", "#111111", "#222222", "#333333"]], [new Dictionary<int, int> { [999] = 0 }], 0, 0, 1, 0); Assert.Throws<InvalidDataException>(() => ProjectService.ValidateForDocument(data, document)); }
    [Fact] public void Selection_de_zone_contient_le_triangle_clique() { var obj = FunDocument().Objects[0]; var triangle = obj.Triangles[10]; var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C]; var center = new System.Windows.Media.Media3D.Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3); Assert.Contains(10, MainWindow.SelectNearbyTriangles(obj, 10, center, 2)); }
    [Fact] public void Zoom_perspectif_est_ancre_sous_le_curseur() { var camera = new System.Windows.Media.Media3D.PerspectiveCamera(new(0, -10, 0), new(0, 10, 0), new(0, 0, 1), 60); var center = MainWindow.CursorPointOnPlane(camera, new(400, 300), 800, 600, new()); var right = MainWindow.CursorPointOnPlane(camera, new(600, 300), 800, 600, new()); Assert.NotNull(center); Assert.NotNull(right); Assert.Equal(0, center!.Value.X, 8); Assert.Equal(0, center.Value.Y, 8); Assert.True(right!.Value.X > center.Value.X); }
    [Fact] public void Zoom_orthographique_est_ancre_sous_le_curseur() { var camera = new System.Windows.Media.Media3D.OrthographicCamera(new(0, -10, 0), new(0, 10, 0), new(0, 0, 1), 20); var center = MainWindow.CursorPointOnPlane(camera, new(400, 300), 800, 600, new()); var right = MainWindow.CursorPointOnPlane(camera, new(800, 300), 800, 600, new()); Assert.NotNull(center); Assert.NotNull(right); Assert.Equal(0, center!.Value.X, 8); Assert.Equal(10, right!.Value.X, 8); }
    [Fact] public void Rotation_orbitale_reste_stable_sur_360_degres() { var start = MainWindow.OrbitFrame(0, 0); var full = MainWindow.OrbitFrame(360, 360); var upsideDown = MainWindow.OrbitFrame(0, 180); Assert.Equal(start.Direction.X, full.Direction.X, 8); Assert.Equal(start.Direction.Y, full.Direction.Y, 8); Assert.Equal(start.Direction.Z, full.Direction.Z, 8); Assert.Equal(-start.Direction.Y, upsideDown.Direction.Y, 8); Assert.Equal(0, System.Windows.Media.Media3D.Vector3D.DotProduct(upsideDown.Direction, upsideDown.Up), 8); Assert.True(upsideDown.Up.LengthSquared > .99); }
    [Fact] public void Pinceau_interpole_un_trait_sans_trou() { var points = MainWindow.BrushStrokeCenters(new(0, 0), new(100, 0), 5); Assert.Equal(new System.Windows.Point(0, 0), points[0]); Assert.Equal(new System.Windows.Point(100, 0), points[^1]); Assert.All(points.Zip(points.Skip(1)), pair => Assert.InRange((pair.Second - pair.First).Length, 0, 5.001)); }
    [Fact] public void Pinceau_projette_le_centre_de_la_vue() { var projection = new MainWindow.PaintProjection(new(0, -10, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 0), true, 60, 800, 600); Assert.True(MainWindow.TryProjectPoint(new(0, 0, 0), projection, out var screen, out _)); Assert.Equal(400, screen.X, 6); Assert.Equal(300, screen.Y, 6); }
    [Fact] public void Pinceau_ne_traverse_pas_la_surface_visible() { var vertices = new List<Vertex> { new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1), new(-1, 2, -1), new(1, 2, -1), new(0, 2, 1) }; var obj = new ModelObject(0, "1", vertices, [new(0, 1, 2), new(3, 4, 5)], "3D/3dmodel.model"); var document = new ModelDocument("", new System.Xml.Linq.XDocument(), "", [obj], 2, 2, 2, [], 2, null, "millimeter", 0, 0, "3MF"); var projection = new MainWindow.PaintProjection(new(0, -10, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 0), true, 60, 800, 600); var selected = MainWindow.SelectTrianglesFromScreenStroke(document, [new(400, 316)], 20, projection); Assert.Contains(0, selected[0]); Assert.DoesNotContain(1, selected[0]); }
    [Fact] public void Apercu_adaptatif_conserve_un_maillage_valide_et_les_indices_source() { var original = FunDocument().Objects[0]; var source = original with { Triangles = Enumerable.Range(0, 5).SelectMany(_ => original.Triangles).ToList() }; var preview = MainWindow.SimplifyForPreview(source, 200); Assert.NotEmpty(preview.Triangles); Assert.True(preview.Triangles.Count < source.Triangles.Count); Assert.All(preview.Triangles, triangle => { Assert.InRange(triangle.A, 0, preview.Vertices.Count - 1); Assert.InRange(triangle.B, 0, preview.Vertices.Count - 1); Assert.InRange(triangle.C, 0, preview.Vertices.Count - 1); Assert.InRange(triangle.SourceIndex, 0, source.Triangles.Count - 1); }); }
    [Fact] public void Importe_stl_ascii() { var d = new StlService().Read(AsciiStl()); Assert.Equal("STL", d.SourceFormat); Assert.Equal(1, d.TriangleCount); Assert.Equal(10, d.SizeX); }
    [Fact] public void Importe_stl_binaire() { var d = new StlService().Read(BinaryStl()); Assert.Equal(1, d.TriangleCount); Assert.Equal(3, d.Objects[0].Vertices.Count); }
    [Fact] public void Convertit_stl_en_3mf_valide() { var d = new StlService().Read(AsciiStl()); var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".3mf"); new ThreeMfService().Export(d, new PaletteService().Create(1)[0], output); Assert.Equal(1, new ThreeMfService().Read(output).TriangleCount); }
}
