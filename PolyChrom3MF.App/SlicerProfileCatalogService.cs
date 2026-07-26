using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyChrom3MF.App;

public sealed class SlicerProfileCatalogService
{
    static readonly string[] MetadataKeys =
    [
        "type", "name", "from", "setting_id", "instantiation", "inherits",
        "description", "renamed_from", "compatible_printers", "compatible_printers_condition"
    ];

    readonly Dictionary<string, IReadOnlyDictionary<string, RawPreset>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SlicerProfileCatalog Detect(DetectedSlicer slicer)
    {
        var presets = ReadPresets(slicer);
        var resolved = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var visible = presets.Values
            .GroupBy(preset => $"{preset.Type}\0{preset.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Where(preset => preset.Data["instantiation"]?.ToString() is not "false")
            .Select(preset => ToInstalled(preset, Resolve(presets, preset, [], resolved)))
            .ToList();
        return new SlicerProfileCatalog(
            slicer,
            visible.Where(preset => preset.Type == "machine").OrderBy(preset => preset.Name).ToList(),
            visible.Where(preset => preset.Type == "process").OrderBy(preset => preset.Name).ToList(),
            visible.Where(preset => preset.Type == "filament").OrderBy(preset => preset.Name).ToList());
    }

    public JsonObject BuildProjectSettings(ExportProfileSettings profile, IReadOnlyList<PaletteColor> colors)
    {
        var slicer = new DetectedSlicer(
            string.IsNullOrWhiteSpace(profile.SlicerName) ? "Slicer" : profile.SlicerName,
            profile.SlicerPath);
        var presets = ReadPresets(slicer);
        var output = new JsonObject();
        var machine = ResolveNamed(presets, profile.PrinterPreset);
        var process = ResolveNamed(presets, profile.ProcessPreset);
        MergeSettings(output, machine);
        MergeSettings(output, process);

        var slotCount = Math.Max(colors.Count, profile.MaterialSlots);
        var materialNames = Enumerable.Range(0, slotCount)
            .Select(index => ExportProfileSettings.MaterialName(profile.FilamentMaterials.ElementAtOrDefault(index)))
            .ToList();
        var selectedFilaments = Enumerable.Range(0, slotCount)
            .Select(index =>
            {
                var requested = profile.FilamentPresets.ElementAtOrDefault(index);
                var raw = ResolveNamed(presets, requested);
                if (raw.Count > 0) return raw;
                var fallback = presets.Values.FirstOrDefault(candidate =>
                    candidate.Type == "filament" &&
                    Material(candidate.Data).Equals(materialNames[index], StringComparison.OrdinalIgnoreCase) &&
                    candidate.Name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase));
                return fallback is null ? new JsonObject() : Resolve(presets, fallback, []);
            })
            .ToList();
        MergeFilamentSettings(output, selectedFilaments);

        var printerName = profile.PrinterPreset;
        var processName = profile.ProcessPreset;
        output["printer_settings_id"] = printerName;
        if (machine["printer_model"] is not null) output["printer_model"] = machine["printer_model"]!.DeepClone();
        else output["printer_model"] = PrinterModelFromPreset(printerName);
        output["printer_variant"] = profile.NozzleDiameter.ToString("0.0##", CultureInfo.InvariantCulture);
        output["print_settings_id"] = processName;
        output["default_print_profile"] = processName;
        var nozzleCount = Math.Max(profile.ExtruderCount, ArrayLength(machine["nozzle_diameter"]));
        output["nozzle_diameter"] = Repeat(profile.NozzleDiameter.ToString("0.0##", CultureInfo.InvariantCulture), nozzleCount);
        var slotColors = Enumerable.Range(0, slotCount)
            .Select(index => index < colors.Count
                ? colors[index].Hex.ToUpperInvariant()
                : profile.FilamentColors.ElementAtOrDefault(index) ?? "#FFFFFF")
            .ToList();
        output["filament_colour"] = new JsonArray(slotColors.Select(color => JsonValue.Create(color)).ToArray());
        output["default_filament_colour"] = new JsonArray(slotColors.Select(_ => JsonValue.Create("")).ToArray());
        output["filament_type"] = new JsonArray(materialNames.Select(material => JsonValue.Create(material)).ToArray());
        output["filament_settings_id"] = new JsonArray(selectedFilaments.Select((settings, index) =>
            JsonValue.Create(DisplayPresetName(profile.FilamentPresets.ElementAtOrDefault(index), materialNames[index]))).ToArray());
        output["filament_ids"] = new JsonArray(selectedFilaments.Select(settings =>
            JsonValue.Create(settings["filament_id"]?.ToString() ?? settings["setting_id"]?.ToString() ?? "GFSG00_01")).ToArray());
        output["extruder_colour"] = new JsonArray(slotColors.Take(nozzleCount).Select(color => JsonValue.Create(color)).ToArray());
        output["flush_volumes_vector"] = new JsonArray(Enumerable.Range(0, slotCount * 2).Select(_ => JsonValue.Create("140")).ToArray());
        output["flush_volumes_matrix"] = new JsonArray(Enumerable.Range(0, slotCount * slotCount)
            .Select(index => JsonValue.Create(index / slotCount == index % slotCount ? "0" : "280")).ToArray());
        output["filament_map_mode"] = "Auto For Flush";
        output["curr_bed_type"] ??= machine["default_bed_type"]?.DeepClone() ?? JsonValue.Create("Textured PEI Plate");
        return output;
    }

    public string BuildPrusaConfiguration(ExportProfileSettings profile, IReadOnlyList<PaletteColor> colors)
    {
        var materials = Enumerable.Range(0, colors.Count)
            .Select(index => ExportProfileSettings.MaterialName(profile.FilamentMaterials.ElementAtOrDefault(index)));
        var lines = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["printer_settings_id"] = profile.PrinterPreset,
            ["print_settings_id"] = profile.ProcessPreset,
            ["nozzle_diameter"] = profile.NozzleDiameter.ToString("0.0##", CultureInfo.InvariantCulture),
            ["filament_settings_id"] = string.Join(';', Enumerable.Range(0, colors.Count)
                .Select(index => DisplayPresetName(profile.FilamentPresets.ElementAtOrDefault(index), ExportProfileSettings.MaterialName(profile.FilamentMaterials.ElementAtOrDefault(index))))),
            ["filament_type"] = string.Join(';', materials),
            ["filament_colour"] = string.Join(';', colors.Select(color => color.Hex.ToUpperInvariant())),
            ["extruder_colour"] = string.Join(';', colors.Select(color => color.Hex.ToUpperInvariant())),
            ["single_extruder_multi_material"] = colors.Count > 1 ? "1" : "0"
        };
        var builder = new StringBuilder();
        foreach (var pair in lines) builder.Append(pair.Key).Append(" = ").AppendLine(pair.Value);
        return builder.ToString();
    }

    IReadOnlyDictionary<string, RawPreset> ReadPresets(DetectedSlicer slicer)
    {
        var cacheKey = $"{slicer.Name}|{slicer.Path}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        var result = new Dictionary<string, RawPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ProfileRoots(slicer).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Take(20000).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in paths)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length is <= 1 or > 16 * 1024 * 1024) continue;
                    var data = ParseProfile(File.ReadAllText(path));
                    if (data is null) continue;
                    var name = data["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                    var type = data["type"]?.ToString() ?? TypeFromPath(path);
                    if (type is not ("machine" or "process" or "filament")) continue;
                    var preset = new RawPreset(name, type, path, data);
                    result[name] = preset;
                    result.TryAdd(Path.GetFileNameWithoutExtension(path), preset);
                    if (data["renamed_from"] is JsonValue renamed && !string.IsNullOrWhiteSpace(renamed.ToString()))
                        result.TryAdd(renamed.ToString(), preset);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
        }
        _cache[cacheKey] = result;
        return result;
    }

    static IEnumerable<string> ProfileRoots(DetectedSlicer slicer)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var product in ProductFolders(FamilyFor(slicer.Name, slicer.Path)))
        {
            yield return Path.Combine(roaming, product);
            yield return Path.Combine(local, product);
        }
        if (!string.IsNullOrWhiteSpace(slicer.Path))
        {
            string? folder = null;
            try { folder = Path.GetDirectoryName(Path.GetFullPath(slicer.Path)); } catch { }
            if (!string.IsNullOrWhiteSpace(folder))
            {
                yield return Path.Combine(folder, "resources", "profiles");
                yield return Path.Combine(folder, "profiles");
            }
        }
    }

    static string[] ProductFolders(SlicerFamily family) => family switch
    {
        SlicerFamily.SnapmakerOrca => ["Snapmaker_Orca", "Snapmaker Orca"],
        SlicerFamily.BambuStudio => ["BambuStudio", "Bambu Studio"],
        SlicerFamily.Orca => ["OrcaSlicer"],
        SlicerFamily.PrusaSlicer => ["PrusaSlicer"],
        _ => []
    };

    public static SlicerFamily FamilyFor(string? name, string? path)
    {
        var value = $"{name} {Path.GetFileNameWithoutExtension(path ?? "")}";
        if (value.Contains("Snapmaker", StringComparison.OrdinalIgnoreCase)) return SlicerFamily.SnapmakerOrca;
        if (value.Contains("Bambu", StringComparison.OrdinalIgnoreCase)) return SlicerFamily.BambuStudio;
        if (value.Contains("Prusa", StringComparison.OrdinalIgnoreCase)) return SlicerFamily.PrusaSlicer;
        if (value.Contains("Orca", StringComparison.OrdinalIgnoreCase)) return SlicerFamily.Orca;
        return SlicerFamily.Generic;
    }

    static InstalledSlicerPreset ToInstalled(RawPreset preset, JsonObject data)
    {
        var nozzle = FirstNumber(data["nozzle_diameter"], .4);
        var count = Math.Max(1, ArrayLength(data["nozzle_diameter"]));
        var compatible = data["compatible_printers"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToList()
            : [];
        return new InstalledSlicerPreset(
            preset.Name, preset.Type, preset.Path,
            data["printer_model"]?.ToString() ?? "",
            data["printer_variant"]?.ToString() ?? "",
            nozzle, count, Material(data), compatible);
    }

    static JsonObject ResolveNamed(IReadOnlyDictionary<string, RawPreset> presets, string? name) =>
        !string.IsNullOrWhiteSpace(name) && presets.TryGetValue(name, out var preset)
            ? Resolve(presets, preset, [])
            : new JsonObject();

    static JsonObject Resolve(
        IReadOnlyDictionary<string, RawPreset> presets,
        RawPreset preset,
        HashSet<string> stack,
        Dictionary<string, JsonObject>? cache = null)
    {
        if (cache?.TryGetValue(preset.Path, out var cached) == true) return cached;
        if (!stack.Add(preset.Path)) return new JsonObject();
        var result = new JsonObject();
        var parents = preset.Data["inherits"] switch
        {
            JsonArray array => array.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)),
            JsonValue value when !string.IsNullOrWhiteSpace(value.ToString()) => [value.ToString()],
            _ => []
        };
        foreach (var parentName in parents)
            if (parentName is not null && presets.TryGetValue(parentName, out var parent))
                MergeAll(result, Resolve(presets, parent, stack, cache));
        MergeAll(result, preset.Data);
        stack.Remove(preset.Path);
        if (cache is not null) cache[preset.Path] = result;
        return result;
    }

    static void MergeSettings(JsonObject destination, JsonObject source)
    {
        foreach (var pair in source)
            if (!MetadataKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) && pair.Value is not null)
                destination[pair.Key] = pair.Value.DeepClone();
    }

    static void MergeFilamentSettings(JsonObject destination, IReadOnlyList<JsonObject> filaments)
    {
        foreach (var key in filaments.SelectMany(item => item.Select(pair => pair.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (MetadataKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var values = filaments.Select(item => item[key]).ToList();
            if (values.All(value => value is null)) continue;
            if (values.Any(value => value is JsonArray))
            {
                var fallback = values.Select(value => value is JsonArray array && array.Count > 0 ? array[0] : value)
                    .FirstOrDefault(value => value is not null);
                if (fallback is null) continue;
                destination[key] = new JsonArray(values.Select(value =>
                    (value is JsonArray array && array.Count > 0 ? array[0] : value ?? fallback)?.DeepClone()).ToArray());
            }
        }
    }

    static void MergeAll(JsonObject destination, JsonObject source)
    {
        foreach (var pair in source)
            if (pair.Value is not null)
                destination[pair.Key] = pair.Value.DeepClone();
    }

    static string Material(JsonObject data)
    {
        var node = data["filament_type"];
        return node is JsonArray array && array.Count > 0 ? array[0]?.ToString() ?? "" : node?.ToString() ?? "";
    }

    static string TypeFromPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/machine/", StringComparison.OrdinalIgnoreCase)) return "machine";
        if (normalized.Contains("/process/", StringComparison.OrdinalIgnoreCase)) return "process";
        if (normalized.Contains("/filament/", StringComparison.OrdinalIgnoreCase)) return "filament";
        return "";
    }

    static int ArrayLength(JsonNode? node) => node is JsonArray array ? array.Count : node is null ? 0 : 1;
    static double FirstNumber(JsonNode? node, double fallback)
    {
        var text = node is JsonArray array && array.Count > 0 ? array[0]?.ToString() : node?.ToString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    static JsonArray Repeat(string value, int count) =>
        new(Enumerable.Range(0, count).Select(_ => JsonValue.Create(value)).ToArray());

    static string DisplayPresetName(string? value, string material)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"Generic {material}";
        return value.EndsWith(" @System", StringComparison.OrdinalIgnoreCase) ? value[..^8] : value;
    }

    static string PrinterModelFromPreset(string preset)
    {
        var marker = preset.LastIndexOf(" (", StringComparison.Ordinal);
        return marker > 0 ? preset[..marker] : preset;
    }

    static JsonObject? ParseProfile(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 128
        });
        return ConvertElement(document.RootElement) as JsonObject;
    }

    static JsonNode? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(ConvertElement).ToArray()),
        JsonValueKind.String => JsonValue.Create(element.GetString()),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => JsonValue.Create(integer),
        JsonValueKind.Number => JsonValue.Create(element.GetDouble()),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        _ => null
    };

    static JsonObject ConvertObject(JsonElement element)
    {
        var result = new JsonObject();
        foreach (var property in element.EnumerateObject()) result[property.Name] = ConvertElement(property.Value);
        return result;
    }

    sealed record RawPreset(string Name, string Type, string Path, JsonObject Data);
}
