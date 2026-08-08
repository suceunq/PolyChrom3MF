using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace PolyChrom3MF.App;

public sealed class ThreeMfService
{
    internal static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    internal static readonly XNamespace Slic3rPe = "http://schemas.slic3r.org/3mf/2017/06";
    static readonly string[] SlicerFilamentStates =
    [
        "", "4", "8", "0C", "1C", "2C", "3C", "4C", "5C", "6C", "7C", "8C", "9C", "AC", "BC", "CC",
        "DC", "EC", "0FC", "1FC", "2FC", "3FC", "4FC", "5FC", "6FC", "7FC", "8FC", "9FC", "AFC", "BFC", "CFC", "DFC", "EFC"
    ];

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
            var minX = double.PositiveInfinity; var minY = double.PositiveInfinity; var minZ = double.PositiveInfinity;
            var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity; var maxZ = double.NegativeInfinity;
            foreach (var vertex in objects.SelectMany(x => x.Vertices))
            {
                minX = Math.Min(minX, vertex.X); minY = Math.Min(minY, vertex.Y); minZ = Math.Min(minZ, vertex.Z);
                maxX = Math.Max(maxX, vertex.X); maxY = Math.Max(maxY, vertex.Y); maxZ = Math.Max(maxZ, vertex.Z);
            }
            var componentCount = parts.Sum(p => p.Xml.Descendants(Core + "component").Count());
            var existingColors = parts
                .SelectMany(part => part.Xml.Descendants()
                    .Where(element => element.Name == Core + "base" || element.Name.LocalName == "color")
                    .Select(element => (string?)element.Attribute("displaycolor") ?? (string?)element.Attribute("color") ?? element.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var (originalColors, originalAssignments) = ReadOriginalColors(parts, objects, ReadFilamentColors(zip));
            var warnings = new List<string>();
            if (parts.Count > 1) warnings.Add($"Structure 3MF multipartie détectée ({parts.Count} fragments). ");
            if (objects.Count == 1 && componentCount == 0) warnings.Add("Objet fusionné : aucune séparation sémantique ne sera inventée.");

            // The original package is reopened on export; retaining its full XML tree
            // would duplicate hundreds of MB on dense 3MF files.
            return new ModelDocument(path, new XDocument(), mainEntry.FullName, objects,
                maxX - minX, maxY - minY, maxZ - minZ,
                zip.Entries.Select(e => e.FullName).ToList(), objects.Sum(o => (long)o.Triangles.Count),
                warnings.Count == 0 ? null : string.Join(" ", warnings), mainXml.Root!.Attribute("unit")?.Value ?? "millimeter", componentCount, existingColors, "3MF")
            {
                OriginalColors = originalColors,
                OriginalTriangleAssignments = originalAssignments
            };
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or XmlException or NotSupportedException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Le fichier est corrompu ou incompatible avec le standard 3MF.", ex);
        }
    }

    public void Export(ModelDocument document, ColorProposal proposal, string destination, IReadOnlyList<string>? filamentMaterials = null, ExportProfileSettings? exportProfile = null) =>
        ExportAndValidate(document, proposal, destination, true, filamentMaterials, exportProfile);

    public string ExportAndValidate(ModelDocument document, ColorProposal proposal, string destination, bool verify, IReadOnlyList<string>? filamentMaterials = null, ExportProfileSettings? exportProfile = null)
    {
        if (proposal.Colors.Count is < 2 or > 32) throw new InvalidDataException("Une proposition doit contenir entre deux et trente-deux couleurs.");
        var fullDestination = Path.GetFullPath(destination);
        if (!fullDestination.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’export doit porter l’extension .3mf.");
        var destinationFolder = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(destinationFolder);
        var workingDestination = Path.Combine(destinationFolder, $".{Path.GetFileNameWithoutExtension(fullDestination)}.{Guid.NewGuid():N}.tmp.3mf");
        try
        {
            // Rebuild a self-contained production-extension package. Orca/Bambu/
            // Snapmaker require each painted mesh to be a native 3MF part referenced
            // by a parent object; standards-based readers still receive p1/p2/p3.
            CreatePackage(workingDestination, document, proposal);
            if (document.SourceFormat == "3MF" && File.Exists(document.Path))
                CopySafePackageExtras(document.Path, workingDestination);

            using (var zip = ZipFile.Open(workingDestination, ZipArchiveMode.Update))
            {
                WriteSlicerProjectMetadata(zip, document, proposal, filamentMaterials, exportProfile);
            }

            var report = $"{document.Objects.Count} objets · {document.TriangleCount:N0} triangles · {proposal.Colors.Count} couleurs";
            if (verify)
            {
                var reopened = ValidateGeometryStreaming(workingDestination);
                if (reopened.ObjectCount != document.Objects.Count || reopened.TriangleCount != document.TriangleCount)
                    throw new InvalidDataException("La vérification après export a échoué : objets ou triangles différents.");
                if (Math.Abs(reopened.SizeX - document.SizeX) > .0001 || Math.Abs(reopened.SizeY - document.SizeY) > .0001 || Math.Abs(reopened.SizeZ - document.SizeZ) > .0001)
                    throw new InvalidDataException("La vérification après export a échoué : dimensions différentes.");
                if (reopened.ColorCount < proposal.Colors.Count)
                    throw new InvalidDataException("La vérification après export a échoué : matériaux absents.");
                ValidateExportProfile(workingDestination, proposal, exportProfile);
                report = $"{reopened.ObjectCount} objets · {reopened.TriangleCount:N0} triangles · dimensions identiques · {proposal.Colors.Count} couleurs";
                if (exportProfile is not null)
                    report += $" · profil {exportProfile.PrinterPreset} vérifié";
            }
            File.Move(workingDestination, fullDestination, true);
            return report;
        }
        finally
        {
            try { if (File.Exists(workingDestination)) File.Delete(workingDestination); } catch { }
        }
    }

    static XDocument BuildMainModel(ModelDocument document)
    {
        XNamespace production = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
        XNamespace bambuStudio = "http://schemas.bambulab.com/package/2021";
        var resources = new XElement(Core + "resources");
        var build = new XElement(Core + "build");
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
        {
            var partId = objectIndex * 2 + 1;
            var parentId = partId + 1;
            resources.Add(new XElement(Core + "object",
                new XAttribute("id", parentId),
                new XAttribute(production + "UUID", $"000000{objectIndex + 1:D2}-61cb-4c03-9d28-80fed5dfa1dc"),
                new XAttribute("type", "model"),
                new XElement(Core + "components",
                    new XElement(Core + "component",
                        new XAttribute("objectid", partId),
                        new XAttribute(production + "UUID", $"{objectIndex + 1:D4}0000-b206-40ff-9872-83e8017abed1"),
                        new XAttribute(production + "path", $"/3D/Objects/object_{objectIndex + 1}.model"),
                        new XAttribute("transform", "1 0 0 0 1 0 0 0 1 0 0 0")))));
            build.Add(new XElement(Core + "item",
                new XAttribute("objectid", parentId),
                new XAttribute(production + "UUID", $"{parentId:D8}-b1ec-4553-aec9-835e5b724bb4"),
                new XAttribute("printable", "1")));
        }
        var root = new XElement(Core + "model",
            new XAttribute("unit", "millimeter"),
            new XAttribute(XNamespace.Xml + "lang", "fr-FR"),
            new XAttribute(XNamespace.Xmlns + "p", production),
            new XAttribute(XNamespace.Xmlns + "BambuStudio", bambuStudio),
            new XAttribute("requiredextensions", "p"),
            new XElement(Core + "metadata", new XAttribute("name", "Application"), "PolyChrom 3MF"),
            new XElement(Core + "metadata", new XAttribute("name", "BambuStudio:3mfVersion"), "1"),
            resources,
            build);
        AddSlicerPaintingMetadata(root);
        return new XDocument(root);
    }

    static XDocument BuildMeshModel(ModelObject modelObject, int partId)
    {
        XNamespace production = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
        XNamespace bambuStudio = "http://schemas.bambulab.com/package/2021";
        var mesh = new XElement(Core + "mesh",
            new XElement(Core + "vertices", modelObject.Vertices.Select(vertex => new XElement(Core + "vertex",
                new XAttribute("x", vertex.X.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("y", vertex.Y.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("z", vertex.Z.ToString("R", CultureInfo.InvariantCulture))))),
            new XElement(Core + "triangles", modelObject.Triangles.Select(triangle => new XElement(Core + "triangle",
                new XAttribute("v1", triangle.A), new XAttribute("v2", triangle.B), new XAttribute("v3", triangle.C)))));
        return new XDocument(new XElement(Core + "model",
            new XAttribute("unit", "millimeter"),
            new XAttribute(XNamespace.Xml + "lang", "fr-FR"),
            new XAttribute(XNamespace.Xmlns + "p", production),
            new XAttribute(XNamespace.Xmlns + "BambuStudio", bambuStudio),
            new XAttribute("requiredextensions", "p"),
            new XElement(Core + "metadata", new XAttribute("name", "BambuStudio:3mfVersion"), "1"),
            new XElement(Core + "resources",
                new XElement(Core + "object",
                    new XAttribute("id", partId),
                    new XAttribute(production + "UUID", $"{(partId + 1) / 2:D4}0000-81cb-4c03-9d28-80fed5dfa1dc"),
                    new XAttribute("type", "model"),
                    mesh))));
    }

    static void CreatePackage(string destination, ModelDocument document, ColorProposal proposal)
    {
        using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        SaveEntry(zip, "[Content_Types].xml", new XDocument(new XElement(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension", "model"), new XAttribute("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml")))));
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        SaveEntry(zip, "_rels/.rels", new XDocument(new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Target", "/3D/3dmodel.model"), new XAttribute("Id", "rel0"), new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")))));
        SaveEntry(zip, "3D/3dmodel.model", BuildMainModel(document));
        var relationships = new XElement(rel + "Relationships");
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
        {
            SaveMeshEntry(zip, $"3D/Objects/object_{objectIndex + 1}.model",
                document.Objects[objectIndex], objectIndex * 2 + 1, proposal);
            relationships.Add(new XElement(rel + "Relationship",
                new XAttribute("Target", $"/3D/Objects/object_{objectIndex + 1}.model"),
                new XAttribute("Id", $"rel-{objectIndex + 1}"),
                new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")));
        }
        SaveEntry(zip, "3D/_rels/3dmodel.model.rels", new XDocument(relationships));
    }

    static void SaveMeshEntry(
        ZipArchive zip,
        string name,
        ModelObject modelObject,
        int partId,
        ColorProposal proposal)
    {
        const string production = "http://schemas.microsoft.com/3dmanufacturing/production/2015/06";
        const string bambuStudio = "http://schemas.bambulab.com/package/2021";
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, SafeWriterSettings());
        writer.WriteStartDocument();
        writer.WriteStartElement("model", Core.NamespaceName);
        writer.WriteAttributeString("unit", "millimeter");
        writer.WriteAttributeString("xml", "lang", "http://www.w3.org/XML/1998/namespace", "fr-FR");
        writer.WriteAttributeString("xmlns", "p", null, production);
        writer.WriteAttributeString("xmlns", "BambuStudio", null, bambuStudio);
        writer.WriteAttributeString("xmlns", "slic3rpe", null, Slic3rPe.NamespaceName);
        writer.WriteAttributeString("requiredextensions", "p");
        WriteMetadata("BambuStudio:3mfVersion", "1");
        WriteMetadata("slic3rpe:Version3mf", "1");
        WriteMetadata("slic3rpe:MmPaintingVersion", "1");
        writer.WriteStartElement("resources", Core.NamespaceName);
        writer.WriteStartElement("basematerials", Core.NamespaceName);
        writer.WriteAttributeString("id", "999");
        foreach (var color in proposal.Colors)
        {
            writer.WriteStartElement("base", Core.NamespaceName);
            writer.WriteAttributeString("name", color.Name);
            writer.WriteAttributeString("displaycolor", color.Hex.ToUpperInvariant());
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        var objectColor = Math.Clamp(
            proposal.Assignments.GetValueOrDefault(modelObject.Index, modelObject.Index % proposal.Colors.Count),
            0, proposal.Colors.Count - 1);
        writer.WriteStartElement("object", Core.NamespaceName);
        writer.WriteAttributeString("id", partId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("p", "UUID", production,
            $"{(partId + 1) / 2:D4}0000-81cb-4c03-9d28-80fed5dfa1dc");
        writer.WriteAttributeString("type", "model");
        writer.WriteAttributeString("pid", "999");
        writer.WriteAttributeString("pindex", objectColor.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("mesh", Core.NamespaceName);
        writer.WriteStartElement("vertices", Core.NamespaceName);
        foreach (var vertex in modelObject.Vertices)
        {
            writer.WriteStartElement("vertex", Core.NamespaceName);
            writer.WriteAttributeString("x", vertex.X.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("y", vertex.Y.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("z", vertex.Z.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("triangles", Core.NamespaceName);
        proposal.TriangleAssignments.TryGetValue(modelObject.Index, out var triangleColors);
        for (var triangleIndex = 0; triangleIndex < modelObject.Triangles.Count; triangleIndex++)
        {
            var triangle = modelObject.Triangles[triangleIndex];
            var colorIndex = triangleColors is not null && triangleIndex < triangleColors.Length
                ? Math.Clamp(triangleColors[triangleIndex], 0, proposal.Colors.Count - 1)
                : objectColor;
            var colorText = colorIndex.ToString(CultureInfo.InvariantCulture);
            writer.WriteStartElement("triangle", Core.NamespaceName);
            writer.WriteAttributeString("v1", triangle.A.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("v2", triangle.B.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("v3", triangle.C.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("pid", "999");
            writer.WriteAttributeString("p1", colorText);
            writer.WriteAttributeString("p2", colorText);
            writer.WriteAttributeString("p3", colorText);
            if (colorIndex != 0)
                writer.WriteAttributeString("slic3rpe", "mmu_segmentation", Slic3rPe.NamespaceName,
                    SlicerFilamentStates[colorIndex + 1]);
            writer.WriteAttributeString("paint_color", SlicerFilamentStates[colorIndex + 1]);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();

        void WriteMetadata(string key, string value)
        {
            writer.WriteStartElement("metadata", Core.NamespaceName);
            writer.WriteAttributeString("name", key);
            writer.WriteString(value);
            writer.WriteEndElement();
        }
    }

    static GeometryValidation ValidateGeometryStreaming(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        ValidateArchive(zip);
        var entries = zip.Entries
            .Where(entry => entry.FullName.StartsWith("3D/Objects/", StringComparison.OrdinalIgnoreCase) &&
                            entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (entries.Length == 0) throw new InvalidDataException("Aucun fragment de maillage dans le 3MF exporté.");

        long triangleCount = 0;
        var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "vertex")
                {
                    var x = double.Parse(reader.GetAttribute("x") ?? "NaN", NumberStyles.Float, CultureInfo.InvariantCulture);
                    var y = double.Parse(reader.GetAttribute("y") ?? "NaN", NumberStyles.Float, CultureInfo.InvariantCulture);
                    var z = double.Parse(reader.GetAttribute("z") ?? "NaN", NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                        throw new InvalidDataException("Sommet invalide dans le 3MF exporté.");
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
                }
                else if (reader.LocalName == "triangle")
                {
                    triangleCount++;
                }
                else if (reader.LocalName == "base" && reader.GetAttribute("displaycolor") is { Length: > 0 } color)
                {
                    colors.Add(color);
                }
            }
        }
        if (!double.IsFinite(minX)) throw new InvalidDataException("Aucun sommet dans le 3MF exporté.");
        return new GeometryValidation(
            entries.Length,
            triangleCount,
            colors.Count,
            maxX - minX,
            maxY - minY,
            maxZ - minZ);
    }

    readonly record struct GeometryValidation(
        int ObjectCount,
        long TriangleCount,
        int ColorCount,
        double SizeX,
        double SizeY,
        double SizeZ);

    static void CopySafePackageExtras(string sourcePath, string destinationPath)
    {
        using var source = ZipFile.OpenRead(sourcePath);
        using var destination = ZipFile.Open(destinationPath, ZipArchiveMode.Update);
        foreach (var entry in source.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (entry.Length <= 0 || entry.Length > 64L * 1024 * 1024) continue;
            if (normalized.StartsWith('/') || normalized.Split('/').Any(segment => segment == "..")) continue;
            var extension = Path.GetExtension(normalized);
            var isSafeAsset = new[] { ".png", ".jpg", ".jpeg", ".txt" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
            // Never inherit printer/process metadata from the source package.
            // A Bambu project exported for Snapmaker must not retain the X1C
            // machine, process, AMS or filament presets.
            if (!isSafeAsset) continue;
            if (destination.Entries.Any(existing => existing.FullName.Equals(normalized, StringComparison.OrdinalIgnoreCase))) continue;
            var copy = destination.CreateEntry(normalized, CompressionLevel.Optimal);
            using var input = entry.Open();
            using var output = copy.Open();
            input.CopyTo(output);
        }
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
        if (zip.Entries.Count > 100_000) throw new InvalidDataException("L’archive 3MF contient un nombre anormal de fichiers.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == "..")) throw new InvalidDataException("Archive refusée : chemin ZIP non sûr.");
            if (!paths.Add(normalized)) throw new InvalidDataException("Archive refusée : chemins ZIP dupliqués.");
        }
    }

    static ZipArchiveEntry? FindModelEntry(ZipArchive zip) => zip.GetEntry("3D/3dmodel.model") ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
    static ZipArchiveEntry? FindEntry(ZipArchive zip, string partName)
    {
        var normalized = NormalizePartPath(Uri.UnescapeDataString(partName));
        return zip.Entries.FirstOrDefault(entry =>
        {
            try
            {
                return NormalizePartPath(Uri.UnescapeDataString(entry.FullName))
                    .Equals(normalized, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });
    }

    static void AddSlicerPaintingMetadata(XElement root)
    {
        root.SetAttributeValue(XNamespace.Xmlns + "slic3rpe", Slic3rPe.NamespaceName);
        foreach (var name in new[] { "slic3rpe:Version3mf", "slic3rpe:MmPaintingVersion" })
            root.Elements(Core + "metadata").Where(element => (string?)element.Attribute("name") == name).Remove();
        var anchor = root.Element(Core + "resources");
        var version = new XElement(Core + "metadata", new XAttribute("name", "slic3rpe:Version3mf"), "1");
        var painting = new XElement(Core + "metadata", new XAttribute("name", "slic3rpe:MmPaintingVersion"), "1");
        if (anchor is null) { root.AddFirst(painting); root.AddFirst(version); }
        else { anchor.AddBeforeSelf(version); anchor.AddBeforeSelf(painting); }
    }

    static void WriteSlicerProjectMetadata(ZipArchive zip, ModelDocument document, ColorProposal proposal, IReadOnlyList<string>? filamentMaterials, ExportProfileSettings? exportProfile)
    {
        ReplaceXmlEntry(zip, "Metadata/model_settings.config", BuildSlicerModelSettings(document));

        var projectEntry = zip.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("Metadata/project_settings.config", StringComparison.OrdinalIgnoreCase));
        projectEntry?.Delete();
        var catalog = new SlicerProfileCatalogService();
        var settings = exportProfile is null
            ? new JsonObject()
            : catalog.BuildProjectSettings(exportProfile, proposal.Colors);

        var colors = new JsonArray(proposal.Colors.Select(color => JsonValue.Create(color.Hex.ToUpperInvariant())).ToArray());
        if (exportProfile is null)
        {
            settings["filament_colour"] = colors;
            settings["default_filament_colour"] = new JsonArray(proposal.Colors.Select(_ => JsonValue.Create("")).ToArray());
            settings["filament_ids"] = new JsonArray(proposal.Colors.Select(_ => JsonValue.Create("GFSG00_01")).ToArray());
        }
        var materials = Enumerable.Range(0, proposal.Colors.Count)
            .Select(index => ExportProfileSettings.MaterialName(
                exportProfile?.FilamentMaterials.ElementAtOrDefault(index) ?? filamentMaterials?.ElementAtOrDefault(index)))
            .ToArray();
        if (exportProfile is null)
        {
            settings["filament_type"] = new JsonArray(materials.Select(material => JsonValue.Create(material)).ToArray());
            settings["filament_settings_id"] = new JsonArray(materials.Select(material => JsonValue.Create($"Generic {material}")).ToArray());
        }
        settings["nozzle_diameter"] ??= new JsonArray(JsonValue.Create(
            (exportProfile?.NozzleDiameter ?? .4).ToString("0.0##", CultureInfo.InvariantCulture)));
        NormalizePortableSlicerSettings(settings, Math.Max(proposal.Colors.Count, exportProfile?.MaterialSlots ?? proposal.Colors.Count));

        var replacement = zip.CreateEntry("Metadata/project_settings.config", CompressionLevel.Optimal);
        using (var output = replacement.Open())
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            settings.WriteTo(writer);

        ReplaceXmlEntry(zip, "Metadata/slice_info.config", new XDocument(
            new XElement("config",
                new XElement("header",
                    new XElement("header_item", new XAttribute("key", "X-BBL-Client-Type"), new XAttribute("value", "slicer")),
                    new XElement("header_item", new XAttribute("key", "X-BBL-Client-Version"), new XAttribute("value", ""))))));

        zip.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("Metadata/Slic3r_PE.config", StringComparison.OrdinalIgnoreCase))?.Delete();
        if (exportProfile?.Family == SlicerFamily.PrusaSlicer)
        {
            var prusa = zip.CreateEntry("Metadata/Slic3r_PE.config", CompressionLevel.Optimal);
            using var writer = new StreamWriter(prusa.Open(), new System.Text.UTF8Encoding(false));
            writer.Write(catalog.BuildPrusaConfiguration(exportProfile, proposal.Colors));
        }
    }

    static void NormalizePortableSlicerSettings(JsonObject settings, int filamentCount)
    {
        // A 3MF created from an STL has no printer profile to supply these values.
        // Orca-family slicers then materialize sentinel defaults (notably -1 for
        // raft_first_layer_expansion), which are reported as project errors.
        settings["raft_first_layer_expansion"] = "2";
        settings["use_relative_e_distances"] = "1";
        settings["spiral_mode"] = "0";

        // Relative extrusion is required by the multi-colour prime/wipe tower.
        // Resetting E on every layer prevents floating-point drift and suppresses
        // the corresponding validation error in Snapmaker Orca and OrcaSlicer.
        var beforeLayer = settings["before_layer_change_gcode"]?.GetValue<string>() ?? "";
        if (!beforeLayer.Contains("G92 E0", StringComparison.OrdinalIgnoreCase))
            beforeLayer = string.IsNullOrWhiteSpace(beforeLayer)
                ? ";BEFORE_LAYER_CHANGE\nG92 E0"
                : beforeLayer.TrimEnd() + "\nG92 E0";
        settings["before_layer_change_gcode"] = beforeLayer;

        // "Auto Lift" may select spiral lifting, which requires a safety margin
        // around the bed. Normal Lift is portable and does not alter the model.
        var count = Math.Max(1, filamentCount);
        settings["z_hop_types"] = new JsonArray(
            Enumerable.Range(0, count).Select(_ => JsonValue.Create("Normal Lift")).ToArray());
        settings["z_hop"] = new JsonArray(
            Enumerable.Range(0, count).Select(_ => JsonValue.Create("0.4")).ToArray());
    }

    static void ValidateExportProfile(string path, ColorProposal proposal, ExportProfileSettings? profile)
    {
        using var zip = ZipFile.OpenRead(path);
        ValidateArchive(zip);
        var project = zip.GetEntry("Metadata/project_settings.config")
            ?? throw new InvalidDataException("La configuration du slicer est absente du 3MF.");
        if (project.Length is <= 2 or > 32 * 1024 * 1024)
            throw new InvalidDataException("La configuration du slicer est invalide.");
        JsonObject settings;
        using (var stream = project.Open())
            settings = JsonNode.Parse(stream) as JsonObject
                ?? throw new InvalidDataException("La configuration du slicer est illisible.");
        foreach (var key in new[] { "filament_colour", "filament_type", "filament_settings_id", "nozzle_diameter" })
            if (settings[key] is not JsonArray values || values.Count == 0 || values.Any(value => value is null))
                throw new InvalidDataException($"La métadonnée de slicer « {key} » est absente ou incomplète.");
        if ((settings["filament_colour"] as JsonArray)!.Count < proposal.Colors.Count ||
            (settings["filament_type"] as JsonArray)!.Count < proposal.Colors.Count)
            throw new InvalidDataException("Le profil exporté ne décrit pas tous les filaments du modèle.");
        if (profile is null) return;
        if (!string.Equals(settings["printer_settings_id"]?.ToString(), profile.PrinterPreset, StringComparison.Ordinal))
            throw new InvalidDataException("Le profil machine sélectionné n’a pas été écrit dans le 3MF.");
        if (!string.Equals(settings["print_settings_id"]?.ToString(), profile.ProcessPreset, StringComparison.Ordinal))
            throw new InvalidDataException("Le profil de processus sélectionné n’a pas été écrit dans le 3MF.");
        var expectedNozzle = profile.NozzleDiameter.ToString("0.0##", CultureInfo.InvariantCulture);
        if (!(settings["nozzle_diameter"] as JsonArray)!.All(value => value?.ToString() == expectedNozzle))
            throw new InvalidDataException("Le diamètre de buse exporté ne correspond pas au profil choisi.");
        if ((settings["nozzle_diameter"] as JsonArray)!.Count < profile.ExtruderCount)
            throw new InvalidDataException("Le nombre d’extrudeurs physiques exporté est incomplet.");
        if ((settings["filament_colour"] as JsonArray)!.Count < profile.MaterialSlots ||
            (settings["filament_settings_id"] as JsonArray)!.Count < profile.MaterialSlots)
            throw new InvalidDataException("Le nombre d’emplacements de filament exporté est incomplet.");
        if (profile.Family == SlicerFamily.PrusaSlicer && zip.GetEntry("Metadata/Slic3r_PE.config") is null)
            throw new InvalidDataException("La configuration native PrusaSlicer est absente.");
    }

    static XDocument BuildSlicerModelSettings(ModelDocument document)
    {
        var root = new XElement("config");
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
        {
            var partId = (objectIndex * 2 + 1).ToString(CultureInfo.InvariantCulture);
            var objectId = (objectIndex * 2 + 2).ToString(CultureInfo.InvariantCulture);
            var name = document.Objects.Count == 1 ? Path.GetFileNameWithoutExtension(document.Path) : $"Objet {objectIndex + 1}";
            root.Add(new XElement("object",
                new XAttribute("id", objectId),
                new XElement("metadata", new XAttribute("key", "name"), new XAttribute("value", name)),
                new XElement("metadata", new XAttribute("key", "extruder"), new XAttribute("value", "1")),
                new XElement("part",
                    new XAttribute("id", partId),
                    new XAttribute("subtype", "normal_part"),
                    new XElement("metadata", new XAttribute("key", "name"), new XAttribute("value", name)),
                    new XElement("metadata", new XAttribute("key", "extruder"), new XAttribute("value", "1")),
                    new XElement("metadata", new XAttribute("key", "matrix"), new XAttribute("value", "1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1")),
                    new XElement("mesh_stat",
                        new XAttribute("face_count", document.Objects[objectIndex].Triangles.Count),
                        new XAttribute("edges_fixed", "0"),
                        new XAttribute("degenerate_facets", "0"),
                        new XAttribute("facets_removed", "0"),
                        new XAttribute("facets_reversed", "0"),
                        new XAttribute("backwards_edges", "0")))));
        }

        var plate = new XElement("plate",
            new XElement("metadata", new XAttribute("key", "plater_id"), new XAttribute("value", "1")),
            new XElement("metadata", new XAttribute("key", "plater_name"), new XAttribute("value", "PolyChrom")),
            new XElement("metadata", new XAttribute("key", "locked"), new XAttribute("value", "false")),
            new XElement("metadata", new XAttribute("key", "filament_map_mode"), new XAttribute("value", "Auto For Flush")));
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
            plate.Add(new XElement("model_instance",
                new XElement("metadata", new XAttribute("key", "object_id"), new XAttribute("value", objectIndex * 2 + 2)),
                new XElement("metadata", new XAttribute("key", "instance_id"), new XAttribute("value", "0")),
                new XElement("metadata", new XAttribute("key", "identify_id"), new XAttribute("value", objectIndex + 1))));
        root.Add(plate);

        var assemble = new XElement("assemble");
        for (var objectIndex = 0; objectIndex < document.Objects.Count; objectIndex++)
            assemble.Add(new XElement("assemble_item",
                new XAttribute("object_id", objectIndex * 2 + 2),
                new XAttribute("instance_id", "0"),
                new XAttribute("transform", "1 0 0 0 1 0 0 0 1 0 0 0"),
                new XAttribute("offset", "0 0 0")));
        root.Add(assemble);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    static void ReplaceXmlEntry(ZipArchive zip, string name, XDocument xml)
    {
        zip.Entries.FirstOrDefault(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Delete();
        SaveEntry(zip, name, xml);
    }
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

    /// <summary>Reads filament colors from Bambu/Orca project_settings.config metadata.</summary>
    static List<string>? ReadFilamentColors(ZipArchive zip)
    {
        var entry = zip.GetEntry("Metadata/project_settings.config");
        if (entry is null) return null;
        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("filament_colour", out var colours) || colours.ValueKind != JsonValueKind.Array)
                return null;
            var result = new List<string>();
            foreach (var colour in colours.EnumerateArray())
            {
                var hex = colour.GetString();
                if (!string.IsNullOrWhiteSpace(hex) && hex.Length <= 9)
                    result.Add(hex);
            }
            return result.Count > 0 ? result : null;
        }
        catch { return null; }
    }

    static (List<PaletteColor>? Colors, Dictionary<int, int[]>? Assignments) ReadOriginalColors(
        List<(ZipArchiveEntry Entry, XDocument Xml)> parts, List<ModelObject> objects, List<string>? filamentColours = null)
    {
        var allColors = new List<PaletteColor>();
        var hexToDedupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var objectAssignments = new Dictionary<int, int[]>();

        // Pre-initialize filament colors from Bambu/Orca project settings (if available).
        // This is done once, outside the per-object loop.
        var paintColorToFilament = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (filamentColours is { Count: > 0 })
        {
            for (var fi = 0; fi < filamentColours.Count; fi++)
            {
                var hex = NormalizeImportedColor(filamentColours[fi]);
                if (hex is not null && !hexToDedupIndex.ContainsKey(hex))
                {
                    hexToDedupIndex[hex] = allColors.Count;
                    allColors.Add(new PaletteColor($"Filament {fi + 1}", hex));
                }
            }
            for (var si = 1; si < SlicerFilamentStates.Length && si - 1 < filamentColours.Count; si++)
                paintColorToFilament[SlicerFilamentStates[si]] = si - 1;
        }

        foreach (var (entry, xml) in parts)
        {
            var resources = xml.Root?.Element(Core + "resources");
            if (resources is null) continue;
            var partPath = NormalizePartPath(entry.FullName);

            // Property indexes are local to each basematerials resource (pid),
            // not global across the 3MF package.
            var colorsByResource = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            foreach (var basematerials in resources.Elements(Core + "basematerials"))
            {
                var resourceId = basematerials.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(resourceId)) continue;
                var resourceColors = new Dictionary<int, int>();
                var propertyIndex = 0;
                foreach (var baseElement in basematerials.Elements(Core + "base"))
                {
                    var name = baseElement.Attribute("name")?.Value ?? $"Couleur {allColors.Count + 1}";
                    var hex = NormalizeImportedColor(baseElement.Attribute("displaycolor")?.Value);
                    if (hex is null) { propertyIndex++; continue; }
                    if (!hexToDedupIndex.TryGetValue(hex, out var dedupIdx))
                    {
                        dedupIdx = allColors.Count;
                        hexToDedupIndex[hex] = dedupIdx;
                        allColors.Add(new PaletteColor(name, hex));
                    }
                    resourceColors[propertyIndex++] = dedupIdx;
                }
                colorsByResource[resourceId] = resourceColors;
            }

            // Read per-triangle color assignments from each object's mesh
            foreach (var objElement in resources.Elements(Core + "object"))
            {
                var mesh = objElement.Element(Core + "mesh");
                if (mesh is null) continue;
                var objId = objElement.Attribute("id")?.Value;
                // Object ids may repeat in different production fragments.
                var targetObj = objects.FirstOrDefault(o => o.Id == objId && o.PartPath.Equals(partPath, StringComparison.OrdinalIgnoreCase));
                if (targetObj is null) continue;

                var triangleElements = mesh.Element(Core + "triangles")?.Elements(Core + "triangle").ToList();
                if (triangleElements is null || triangleElements.Count != targetObj.Triangles.Count) continue;

                var triColors = new int[targetObj.Triangles.Count];
                var hasAnyColor = false;
                for (var i = 0; i < triangleElements.Count; i++)
                {
                    var tri = triangleElements[i];
                    var propertyId = tri.Attribute("pid")?.Value ?? objElement.Attribute("pid")?.Value;
                    var propertyIndexText = tri.Attribute("p1")?.Value ?? objElement.Attribute("pindex")?.Value;
                    if (propertyId is not null && propertyIndexText is not null &&
                        int.TryParse(propertyIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var propertyIndex) &&
                        colorsByResource.TryGetValue(propertyId, out var resourceColors) &&
                        resourceColors.TryGetValue(propertyIndex, out var mappedIdx))
                    {
                        triColors[i] = mappedIdx;
                        hasAnyColor = true;
                        continue;
                    }
                    // Try Bambu/Orca paint_color format
                    var paintColor = tri.Attribute("paint_color")?.Value;
                    if (paintColor is not null && paintColorToFilament.TryGetValue(paintColor, out var filamentIdx) &&
                        filamentColours is not null && filamentIdx < filamentColours.Count)
                    {
                        var hex = NormalizeImportedColor(filamentColours[filamentIdx]);
                        if (hex is not null && hexToDedupIndex.TryGetValue(hex, out var dedupIdx))
                        {
                            triColors[i] = dedupIdx;
                            hasAnyColor = true;
                        }
                        else
                        {
                            triColors[i] = 0;
                        }
                        continue;
                    }
                    triColors[i] = 0;
                }
                if (hasAnyColor)
                    objectAssignments[targetObj.Index] = triColors;
            }
        }

        if (allColors.Count == 0 || objectAssignments.Count == 0)
            return (null, null);

        return (allColors, objectAssignments);
    }

    static string? NormalizeImportedColor(string? value)
    {
        var digits = value?.Trim().TrimStart('#');
        if (digits is null || digits.Length is not (6 or 8) || !digits.All(Uri.IsHexDigit)) return null;
        // 3MF stores optional alpha after RGB. Filaments are opaque, so retain
        // the exact RGB channels and ignore that transparency byte.
        return "#" + digits[..6].ToUpperInvariant();
    }

    sealed record ModelPart(string Path, XDocument Xml, double UnitScale, Dictionary<string, XElement> Objects);
}

public readonly record struct Vertex(double X, double Y, double Z);
public readonly record struct Triangle(int A, int B, int C);
public sealed record ModelObject(int Index, string Id, List<Vertex> Vertices, List<Triangle> Triangles, string PartPath)
{
    public override string ToString() => $"Objet {Id} — {Triangles.Count:N0} triangles";
}
public sealed record ModelDocument(string Path, XDocument Xml, string ModelEntry, List<ModelObject> Objects, double SizeX, double SizeY, double SizeZ, List<string> Entries, long TriangleCount, string? Warning, string Unit, int ComponentCount, int ExistingColorCount, string SourceFormat, bool IsDerived = false)
{
    /// <summary>Original basematerials colors read from the 3MF file, or null if none.</summary>
    public List<PaletteColor>? OriginalColors { get; init; }
    /// <summary>Per-object, per-triangle original color indices (index into OriginalColors).</summary>
    public Dictionary<int, int[]>? OriginalTriangleAssignments { get; init; }
}
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
