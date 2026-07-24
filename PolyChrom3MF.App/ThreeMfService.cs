using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace PolyChrom3MF.App;

public sealed class ThreeMfService
{
    internal static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    public ModelDocument Read(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sélectionnez un fichier .3mf valide.");
        if (new FileInfo(path).Length == 0) throw new InvalidDataException("Le fichier 3MF est vide.");

        try
        {
            using var zip = ZipFile.OpenRead(path);
            ValidateArchive(zip);
            var mainEntry = FindModelEntry(zip) ?? throw new InvalidDataException("Document modèle 3MF absent.");
            var modelEntries = zip.Entries.Where(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && e.Length > 0).ToList();
            var parts = new List<(ZipArchiveEntry Entry, XDocument Xml)>();
            foreach (var entry in modelEntries)
            {
                var xml = LoadSecureXml(entry);
                if (xml.Root?.Name != Core + "model") throw new InvalidDataException($"Espace de noms 3MF non pris en charge dans {entry.FullName}.");
                parts.Add((entry, xml));
            }

            var mainXml = parts.First(p => p.Entry.FullName.Equals(mainEntry.FullName, StringComparison.OrdinalIgnoreCase)).Xml;
            var modelParts = parts.ToDictionary(
                part => NormalizePartPath(part.Entry.FullName),
                part => new ModelPart(
                    NormalizePartPath(part.Entry.FullName),
                    part.Xml,
                    UnitToMillimeters(part.Xml.Root!.Attribute("unit")?.Value ?? "millimeter"),
                    part.Xml.Descendants(Core + "object")
                        .Where(element => element.Attribute("id") is not null)
                        .ToDictionary(element => element.Attribute("id")!.Value, StringComparer.Ordinal)),
                StringComparer.OrdinalIgnoreCase);
            var objects = InstantiateBuild(modelParts, NormalizePartPath(mainEntry.FullName));
            if (objects.Count == 0) objects = ReadUninstantiatedMeshes(modelParts);

            if (objects.Count == 0) throw new InvalidDataException("Aucun maillage exploitable n’a été trouvé dans les fragments 3MF.");
            var all = objects.SelectMany(x => x.Vertices).ToArray();
            var componentCount = parts.Sum(p => p.Xml.Descendants(Core + "component").Count());
            var existingColors = parts.Sum(p => p.Xml.Descendants(Core + "base").Count() + p.Xml.Descendants().Count(x => x.Name.LocalName == "color"));
            var warnings = new List<string>();
            if (parts.Count > 1) warnings.Add($"Structure 3MF multipartie détectée ({parts.Count} fragments). ");
            if (objects.Count == 1 && componentCount == 0) warnings.Add("Objet fusionné : aucune séparation sémantique ne sera inventée.");

            return new ModelDocument(path, mainXml, mainEntry.FullName, objects,
                all.Max(x => x.X) - all.Min(x => x.X), all.Max(x => x.Y) - all.Min(x => x.Y), all.Max(x => x.Z) - all.Min(x => x.Z),
                zip.Entries.Select(e => e.FullName).ToList(), objects.Sum(o => (long)o.Triangles.Count),
                warnings.Count == 0 ? null : string.Join(" ", warnings), mainXml.Root!.Attribute("unit")?.Value ?? "millimeter", componentCount, existingColors, "3MF");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or XmlException or NotSupportedException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Le fichier est corrompu ou incompatible avec le standard 3MF.", ex);
        }
    }

    public void Export(ModelDocument document, ColorProposal proposal, string destination) => ExportAndValidate(document, proposal, destination, true);

    public string ExportAndValidate(ModelDocument document, ColorProposal proposal, string destination, bool verify)
    {
        if (proposal.Colors.Count is < 2 or > 32) throw new InvalidDataException("Une proposition doit contenir entre deux et trente-deux couleurs.");
        var fullDestination = Path.GetFullPath(destination);
        if (!fullDestination.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’export doit porter l’extension .3mf.");
        var destinationFolder = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(destinationFolder);
        var workingDestination = Path.Combine(destinationFolder, $".{Path.GetFileNameWithoutExtension(fullDestination)}.{Guid.NewGuid():N}.tmp.3mf");
        try
        {
        if (document.SourceFormat == "STL" || document.IsDerived) CreatePackage(workingDestination, BuildStlModel(document));
        else File.Copy(document.Path, workingDestination, true);

        using (var zip = ZipFile.Open(workingDestination, ZipArchiveMode.Update))
        {
            var partNames = document.Objects.Select(o => o.PartPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var partName in partNames)
            {
                var entry = zip.GetEntry(partName) ?? throw new InvalidDataException($"Fragment 3MF absent pendant l’export : {partName}");
                var xml = LoadSecureXml(entry);
                var matches = document.Objects.Where(o => o.PartPath.Equals(partName, StringComparison.OrdinalIgnoreCase)).ToList();
                var resources = xml.Root!.Element(Core + "resources") ?? new XElement(Core + "resources");
                if (resources.Parent is null) xml.Root.AddFirst(resources);
                resources.Elements(Core + "basematerials").Where(x => (string?)x.Attribute("id") == "999").Remove();
                var materials = new XElement(Core + "basematerials", new XAttribute("id", "999"));
                foreach (var color in proposal.Colors)
                    materials.Add(new XElement(Core + "base", new XAttribute("name", color.Name), new XAttribute("displaycolor", color.Hex.ToUpperInvariant())));
                resources.Add(materials);
                foreach (var obj in xml.Descendants(Core + "object"))
                {
                    var match = matches.FirstOrDefault(o => o.Id == (string?)obj.Attribute("id") && obj.Element(Core + "mesh") is not null);
                    if (match is null) continue;
                    obj.SetAttributeValue("pid", "999");
                    obj.SetAttributeValue("pindex", proposal.Assignments.GetValueOrDefault(match.Index, match.Index % proposal.Colors.Count));
                    if (proposal.TriangleAssignments.TryGetValue(match.Index, out var triangleColors))
                    {
                        var triangleElements = obj.Element(Core + "mesh")?.Element(Core + "triangles")?.Elements(Core + "triangle").ToList() ?? [];
                        for (var triangleIndex = 0; triangleIndex < Math.Min(triangleElements.Count, triangleColors.Length); triangleIndex++)
                        {
                            var colorIndex = Math.Clamp(triangleColors[triangleIndex], 0, proposal.Colors.Count - 1);
                            triangleElements[triangleIndex].SetAttributeValue("pid", "999");
                            triangleElements[triangleIndex].SetAttributeValue("p1", colorIndex);
                            triangleElements[triangleIndex].SetAttributeValue("p2", colorIndex);
                            triangleElements[triangleIndex].SetAttributeValue("p3", colorIndex);
                        }
                    }
                }
                entry.Delete();
                var replacement = zip.CreateEntry(partName, CompressionLevel.Optimal);
                using var output = replacement.Open();
                using var writer = XmlWriter.Create(output, SafeWriterSettings());
                xml.Save(writer);
            }
        }

        var report = $"{document.Objects.Count} objets · {document.TriangleCount:N0} triangles · {proposal.Colors.Count} couleurs";
        if (verify)
        {
            var reopened = Read(workingDestination);
            if (reopened.Objects.Count != document.Objects.Count || reopened.TriangleCount != document.TriangleCount)
                throw new InvalidDataException("La vérification après export a échoué : objets ou triangles différents.");
            if (Math.Abs(reopened.SizeX - document.SizeX) > .0001 || Math.Abs(reopened.SizeY - document.SizeY) > .0001 || Math.Abs(reopened.SizeZ - document.SizeZ) > .0001)
                throw new InvalidDataException("La vérification après export a échoué : dimensions différentes.");
            if (reopened.ExistingColorCount < proposal.Colors.Count)
                throw new InvalidDataException("La vérification après export a échoué : matériaux absents.");
            report = $"{reopened.Objects.Count} objets · {reopened.TriangleCount:N0} triangles · dimensions identiques · {proposal.Colors.Count} couleurs";
        }
        File.Move(workingDestination, fullDestination, true);
        return report;
        }
        finally
        {
            try { if (File.Exists(workingDestination)) File.Delete(workingDestination); } catch { }
        }
    }

    static XDocument BuildStlModel(ModelDocument document)
    {
        var resources = new XElement(Core + "resources");
        var build = new XElement(Core + "build");
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
        {
            var modelObject = document.Objects[objectIndex];
            var objectId = (objectIndex + 1).ToString(CultureInfo.InvariantCulture);
            var mesh = new XElement(Core + "mesh",
                new XElement(Core + "vertices", modelObject.Vertices.Select(vertex => new XElement(Core + "vertex",
                    new XAttribute("x", vertex.X.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("y", vertex.Y.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("z", vertex.Z.ToString("R", CultureInfo.InvariantCulture))))),
                new XElement(Core + "triangles", modelObject.Triangles.Select(triangle => new XElement(Core + "triangle",
                    new XAttribute("v1", triangle.A), new XAttribute("v2", triangle.B), new XAttribute("v3", triangle.C)))));
            resources.Add(new XElement(Core + "object", new XAttribute("id", objectId), new XAttribute("type", "model"), mesh));
            build.Add(new XElement(Core + "item", new XAttribute("objectid", objectId)));
        }
        return new XDocument(new XElement(Core + "model",
            new XAttribute("unit", "millimeter"),
            new XAttribute(XNamespace.Xml + "lang", "fr-FR"),
            new XElement(Core + "metadata", new XAttribute("name", "Application"), "PolyChrom 3MF"),
            resources,
            build));
    }

    static void CreatePackage(string destination, XDocument model)
    {
        using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        SaveEntry(zip, "[Content_Types].xml", new XDocument(new XElement(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension", "model"), new XAttribute("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml")))));
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        SaveEntry(zip, "_rels/.rels", new XDocument(new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Target", "/3D/3dmodel.model"), new XAttribute("Id", "rel0"), new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")))));
        SaveEntry(zip, "3D/3dmodel.model", model);
    }

    static void SaveEntry(ZipArchive zip, string name, XDocument xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open(); using var writer = XmlWriter.Create(stream, SafeWriterSettings()); xml.Save(writer);
    }

    static XmlWriterSettings SafeWriterSettings() => new() { Encoding = new System.Text.UTF8Encoding(false), Indent = false };
    static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count == 0) throw new InvalidDataException("L’archive 3MF est vide.");
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == "..")) throw new InvalidDataException("Archive refusée : chemin ZIP non sûr.");
        }
    }

    static ZipArchiveEntry? FindModelEntry(ZipArchive zip) => zip.GetEntry("3D/3dmodel.model") ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
    static XDocument LoadSecureXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 0, MaxCharactersFromEntities = 0 });
        return XDocument.Load(reader, LoadOptions.None);
    }

    static List<ModelObject> InstantiateBuild(IReadOnlyDictionary<string, ModelPart> parts, string mainPartPath)
    {
        if (!parts.TryGetValue(mainPartPath, out var mainPart)) throw new InvalidDataException("Fragment principal 3MF introuvable.");
        var build = mainPart.Xml.Root?.Element(Core + "build");
        if (build is null) return [];
        var objects = new List<ModelObject>();
        foreach (var item in build.Elements(Core + "item"))
        {
            var objectId = item.Attribute("objectid")?.Value ?? throw new InvalidDataException("Élément de construction 3MF sans objet.");
            var targetPart = ReferencedPart(item, mainPart.Path);
            ResolveObject(parts, targetPart, objectId, ParseTransform(item, mainPart.UnitScale), objects, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        }
        return objects;
    }

    static void ResolveObject(IReadOnlyDictionary<string, ModelPart> parts, string partPath, string objectId, Matrix3D accumulated, List<ModelObject> output, HashSet<string> chain, int depth)
    {
        if (depth > 128) throw new InvalidDataException("Hiérarchie de composants 3MF trop profonde.");
        if (!parts.TryGetValue(partPath, out var part) || !part.Objects.TryGetValue(objectId, out var element))
            throw new InvalidDataException($"Composant 3MF introuvable : {partPath}#{objectId}.");
        var key = partPath + "#" + objectId;
        if (!chain.Add(key)) throw new InvalidDataException("Cycle détecté dans les composants 3MF.");
        try
        {
            if (element.Element(Core + "mesh") is not null)
            {
                var mesh = ReadMesh(element, part, accumulated, output.Count);
                if (mesh is not null) output.Add(mesh);
                return;
            }
            var components = element.Element(Core + "components")?.Elements(Core + "component").ToList() ?? [];
            foreach (var component in components)
            {
                var childId = component.Attribute("objectid")?.Value ?? throw new InvalidDataException("Composant 3MF sans objet cible.");
                var childPart = ReferencedPart(component, part.Path);
                var childTransform = ParseTransform(component, part.UnitScale);
                var combined = Matrix3D.Multiply(childTransform, accumulated);
                ResolveObject(parts, childPart, childId, combined, output, chain, depth + 1);
            }
        }
        finally { chain.Remove(key); }
    }

    static List<ModelObject> ReadUninstantiatedMeshes(IReadOnlyDictionary<string, ModelPart> parts)
    {
        var objects = new List<ModelObject>();
        foreach (var part in parts.Values)
            foreach (var element in part.Objects.Values)
            {
                if (element.Element(Core + "mesh") is null) continue;
                var mesh = ReadMesh(element, part, Matrix3D.Identity, objects.Count);
                if (mesh is not null) objects.Add(mesh);
            }
        return objects;
    }

    static ModelObject? ReadMesh(XElement element, ModelPart part, Matrix3D transform, int index)
    {
        var mesh = element.Element(Core + "mesh");
        if (mesh is null) return null;
        var vertices = mesh.Descendants(Core + "vertex")
            .Select(vertex =>
            {
                var point = transform.Transform(new Point3D(Number(vertex, "x") * part.UnitScale, Number(vertex, "y") * part.UnitScale, Number(vertex, "z") * part.UnitScale));
                return new Vertex(point.X, point.Y, point.Z);
            }).ToList();
        var triangles = mesh.Descendants(Core + "triangle")
            .Select(triangle => new Triangle(Integer(triangle, "v1"), Integer(triangle, "v2"), Integer(triangle, "v3"))).ToList();
        if (vertices.Count == 0 || triangles.Count == 0) return null;
        if (vertices.Any(vertex => !double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)))
            throw new InvalidDataException($"L’objet {element.Attribute("id")?.Value} contient une transformation invalide.");
        if (triangles.Any(triangle => triangle.A < 0 || triangle.B < 0 || triangle.C < 0 || triangle.A >= vertices.Count || triangle.B >= vertices.Count || triangle.C >= vertices.Count))
            throw new InvalidDataException($"L’objet {element.Attribute("id")?.Value} contient des indices de triangles invalides.");
        return new ModelObject(index, element.Attribute("id")?.Value ?? (index + 1).ToString(CultureInfo.InvariantCulture), vertices, triangles, part.Path);
    }

    static Matrix3D ParseTransform(XElement element, double unitScale)
    {
        var raw = element.Attribute("transform")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return Matrix3D.Identity;
        var values = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 12 || values.Any(value => !double.IsFinite(value))) throw new InvalidDataException("Transformation 3MF invalide.");
        return new Matrix3D(
            values[0], values[1], values[2], 0,
            values[3], values[4], values[5], 0,
            values[6], values[7], values[8], 0,
            values[9] * unitScale, values[10] * unitScale, values[11] * unitScale, 1);
    }

    static string ReferencedPart(XElement reference, string currentPart)
    {
        var path = reference.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("path", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(path)) return currentPart;
        if (path.StartsWith('/')) return NormalizePartPath(path);
        var separator = currentPart.LastIndexOf('/');
        return NormalizePartPath((separator >= 0 ? currentPart[..(separator + 1)] : "") + path);
    }

    static string NormalizePartPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) throw new InvalidDataException("Chemin de fragment 3MF invalide.");
        return string.Join('/', segments);
    }

    static double Number(XElement element, string name) => double.Parse(element.Attribute(name)?.Value ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
    static int Integer(XElement element, string name) => int.Parse(element.Attribute(name)?.Value ?? "-1", NumberStyles.Integer, CultureInfo.InvariantCulture);
    internal static double UnitToMillimeters(string unit) => unit.ToLowerInvariant() switch { "micron" => .001, "centimeter" => 10, "inch" => 25.4, "foot" => 304.8, "meter" => 1000, _ => 1 };

    sealed record ModelPart(string Path, XDocument Xml, double UnitScale, Dictionary<string, XElement> Objects);
}

public readonly record struct Vertex(double X, double Y, double Z);
public readonly record struct Triangle(int A, int B, int C);
public sealed record ModelObject(int Index, string Id, List<Vertex> Vertices, List<Triangle> Triangles, string PartPath)
{
    public override string ToString() => $"Objet {Id} — {Triangles.Count:N0} triangles";
}
public sealed record ModelDocument(string Path, XDocument Xml, string ModelEntry, List<ModelObject> Objects, double SizeX, double SizeY, double SizeZ, List<string> Entries, long TriangleCount, string? Warning, string Unit, int ComponentCount, int ExistingColorCount, string SourceFormat, bool IsDerived = false);
public sealed record PaletteColor(string Name, string Hex)
{
    public System.Windows.Media.Color Color => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Hex)!;
    public SolidColorBrush Brush => new(Color);
    public string Label => $"{Name} {Hex}";
}
public sealed record ColorProposal(string Name, string Description, List<PaletteColor> Colors, Dictionary<int, int> Assignments)
{
    public Dictionary<int, int[]> TriangleAssignments { get; init; } = [];
}
