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
            var warnings = new List<string>();
            if (parts.Count > 1) warnings.Add($"Structure 3MF multipartie détectée ({parts.Count} fragments). ");
            if (objects.Count == 1 && componentCount == 0) warnings.Add("Objet fusionné : aucune séparation sémantique ne sera inventée.");

            // The original package is reopened on export; retaining its full XML tree
            // would duplicate hundreds of MB on dense 3MF files.
            return new ModelDocument(path, new XDocument(), mainEntry.FullName, objects,
                maxX - minX, maxY - minY, maxZ - minZ,
                zip.Entries.Select(e => e.FullName).ToList(), objects.Sum(o => (long)o.Triangles.Count),
                warnings.Count == 0 ? null : string.Join(" ", warnings), mainXml.Root!.Attribute("unit")?.Value ?? "millimeter", componentCount, existingColors, "3MF");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or XmlException or NotSupportedException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Le fichier est corrompu ou incompatible avec le standard 3MF.", ex);
        }
    }

    public void Export(ModelDocument document, ColorProposal proposal, string destination, IReadOnlyList<string>? filamentMaterials = null) => ExportAndValidate(document, proposal, destination, true, filamentMaterials);

    public string ExportAndValidate(ModelDocument document, ColorProposal proposal, string destination, bool verify, IReadOnlyList<string>? filamentMaterials = null)
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
        CreatePackage(workingDestination, document);
        if (document.SourceFormat == "3MF" && File.Exists(document.Path))
            CopySafePackageExtras(document.Path, workingDestination);

        using (var zip = ZipFile.Open(workingDestination, ZipArchiveMode.Update))
        {
            var partNames = document.Objects.Select((_, index) => $"3D/Objects/object_{index + 1}.model").ToList();
            for (var partIndex = 0; partIndex < partNames.Count; partIndex++)
            {
                var partName = partNames[partIndex];
                var entry = FindEntry(zip, partName) ?? throw new InvalidDataException($"Fragment 3MF absent pendant l’export : {partName}");
                var xml = LoadSecureXml(entry);
                var matches = new List<ModelObject> { document.Objects[partIndex] };
                var resources = xml.Root!.Element(Core + "resources") ?? new XElement(Core + "resources");
                if (resources.Parent is null) xml.Root.AddFirst(resources);
                AddSlicerPaintingMetadata(xml.Root);
                resources.Elements(Core + "basematerials").Where(x => (string?)x.Attribute("id") == "999").Remove();
                var materials = new XElement(Core + "basematerials", new XAttribute("id", "999"));
                foreach (var color in proposal.Colors)
                    materials.Add(new XElement(Core + "base", new XAttribute("name", color.Name), new XAttribute("displaycolor", color.Hex.ToUpperInvariant())));
                resources.Add(materials);
                foreach (var obj in xml.Descendants(Core + "object"))
                {
                    ModelObject? match;
                    match = matches[0];
                    if (match is null) continue;
                    obj.SetAttributeValue("pid", "999");
                    var objectColor = Math.Clamp(proposal.Assignments.GetValueOrDefault(match.Index, match.Index % proposal.Colors.Count), 0, proposal.Colors.Count - 1);
                    obj.SetAttributeValue("pindex", objectColor);
                    var triangleElements = obj.Element(Core + "mesh")?.Element(Core + "triangles")?.Elements(Core + "triangle").ToList() ?? [];
                    proposal.TriangleAssignments.TryGetValue(match.Index, out var triangleColors);
                    for (var triangleIndex = 0; triangleIndex < triangleElements.Count; triangleIndex++)
                    {
                        var colorIndex = triangleColors is not null && triangleIndex < triangleColors.Length
                            ? Math.Clamp(triangleColors[triangleIndex], 0, proposal.Colors.Count - 1)
                            : objectColor;
                        var triangle = triangleElements[triangleIndex];
                        triangle.SetAttributeValue("pid", "999");
                        triangle.SetAttributeValue("p1", colorIndex);
                        triangle.SetAttributeValue("p2", colorIndex);
                        triangle.SetAttributeValue("p3", colorIndex);
                        // PrusaSlicer, OrcaSlicer, Bambu Studio and their
                        // derivatives intentionally ignore the standard
                        // p1/p2/p3 color properties on import.  Their native
                        // multi-material painting attribute is emitted in
                        // parallel while the standard properties remain for
                        // Cura and other standards-based readers.
                        triangle.SetAttributeValue(Slic3rPe + "mmu_segmentation",
                            colorIndex == 0 ? null : SlicerFilamentStates[colorIndex + 1]);
                        // Snapmaker Orca's U1 fork reads the same serialized
                        // facet state from an unqualified paint_color attribute
                        // and expects it even for filament 1.
                        triangle.SetAttributeValue("paint_color", SlicerFilamentStates[colorIndex + 1]);
                    }
                }
                entry.Delete();
                var replacement = zip.CreateEntry(partName, CompressionLevel.Optimal);
                using var output = replacement.Open();
                using var writer = XmlWriter.Create(output, SafeWriterSettings());
                xml.Save(writer);
            }
            WriteSlicerProjectMetadata(zip, document, proposal, filamentMaterials);
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

    static void CreatePackage(string destination, ModelDocument document)
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
            SaveEntry(zip, $"3D/Objects/object_{objectIndex + 1}.model", BuildMeshModel(document.Objects[objectIndex], objectIndex * 2 + 1));
            relationships.Add(new XElement(rel + "Relationship",
                new XAttribute("Target", $"/3D/Objects/object_{objectIndex + 1}.model"),
                new XAttribute("Id", $"rel-{objectIndex + 1}"),
                new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")));
        }
        SaveEntry(zip, "3D/_rels/3dmodel.model.rels", new XDocument(relationships));
    }

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
            var isSlicerSettings = normalized.Equals("Metadata/project_settings.config", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Metadata/slice_info.config", StringComparison.OrdinalIgnoreCase);
            if (!isSafeAsset && !isSlicerSettings) continue;
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
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == "..")) throw new InvalidDataException("Archive refusée : chemin ZIP non sûr.");
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

    static void WriteSlicerProjectMetadata(ZipArchive zip, ModelDocument document, ColorProposal proposal, IReadOnlyList<string>? filamentMaterials)
    {
        ReplaceXmlEntry(zip, "Metadata/model_settings.config", BuildSlicerModelSettings(document));

        var projectEntry = zip.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("Metadata/project_settings.config", StringComparison.OrdinalIgnoreCase));
        JsonObject settings;
        if (projectEntry is not null)
        {
            try
            {
                using var reader = new StreamReader(projectEntry.Open());
                settings = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                settings = new JsonObject();
            }
            projectEntry.Delete();
        }
        else settings = new JsonObject();

        var colors = new JsonArray(proposal.Colors.Select(color => JsonValue.Create(color.Hex.ToUpperInvariant())).ToArray());
        settings["filament_colour"] = colors;
        settings["default_filament_colour"] = new JsonArray(proposal.Colors.Select(_ => JsonValue.Create("")).ToArray());
        settings["filament_ids"] = new JsonArray(proposal.Colors.Select(_ => JsonValue.Create("GFSG00_01")).ToArray());
        var materials = Enumerable.Range(0, proposal.Colors.Count)
            .Select(index => string.Equals(filamentMaterials?.ElementAtOrDefault(index), "PETG", StringComparison.OrdinalIgnoreCase) ? "PETG" : "PLA")
            .ToArray();
        settings["filament_type"] = new JsonArray(materials.Select(material => JsonValue.Create(material)).ToArray());
        settings["filament_settings_id"] = new JsonArray(materials.Select(material => JsonValue.Create($"Generic {material}")).ToArray());
        settings["nozzle_diameter"] ??= new JsonArray(JsonValue.Create("0.4"));
        NormalizePortableSlicerSettings(settings, proposal.Colors.Count);

        var replacement = zip.CreateEntry("Metadata/project_settings.config", CompressionLevel.Optimal);
        using (var output = replacement.Open())
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            settings.WriteTo(writer);

        if (!zip.Entries.Any(entry => entry.FullName.Equals("Metadata/slice_info.config", StringComparison.OrdinalIgnoreCase)))
        {
            ReplaceXmlEntry(zip, "Metadata/slice_info.config", new XDocument(
                new XElement("config",
                    new XElement("header",
                        new XElement("header_item", new XAttribute("key", "X-BBL-Client-Type"), new XAttribute("value", "slicer")),
                        new XElement("header_item", new XAttribute("key", "X-BBL-Client-Version"), new XAttribute("value", ""))))));
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
