using System.IO;
using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public sealed class PrinterProfileDetectionService
{
    static readonly string[] ProductFolders = ["Snapmaker Orca", "OrcaSlicer", "BambuStudio", "PrusaSlicer"];

    public PrinterCapabilities? Detect()
    {
        var files = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (var product in ProductFolders)
            {
                var folder = Path.Combine(root, product);
                if (!Directory.Exists(folder)) continue;
                try
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).Take(300));
                    files.AddRange(Directory.EnumerateFiles(folder, "*.ini", SearchOption.AllDirectories).Take(300));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        return DetectFromFiles(files);
    }

    internal static PrinterCapabilities? DetectFromFiles(IEnumerable<string> paths)
    {
        string? printer = null; var slots = 0; var nozzle = .4; var layer = .2;
        var colors = new List<string>();
        foreach (var path in paths.OrderByDescending(path => { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }))
        {
            string text;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > 16 * 1024 * 1024) continue;
                text = File.ReadAllText(path);
            }
            catch { continue; }
            printer ??= Find(text, "\"printer_model\"\\s*:\\s*\"([^\"]+)\"") ?? Find(text, "(?im)^printer_model\\s*=\\s*(.+)$");
            printer ??= Find(text, "\"name\"\\s*:\\s*\"([^\"]*(?:U1|Snapmaker|Bambu|Prusa)[^\"]*)\"");
            if (TryNumber(text, "\"nozzle_diameter\"\\s*:\\s*(?:\"|\\[)?([0-9.]+)", out var detectedNozzle)) nozzle = detectedNozzle;
            if (TryNumber(text, "\"layer_height\"\\s*:\\s*(?:\"|\\[)?([0-9.]+)", out var detectedLayer)) layer = detectedLayer;
            foreach (Match match in Regex.Matches(text, "#[0-9A-Fa-f]{6}"))
                if (!colors.Contains(match.Value, StringComparer.OrdinalIgnoreCase) && colors.Count < 64) colors.Add(match.Value.ToUpperInvariant());
            if (TryNumber(text, "\"(?:extruder_count|filament_count|material_slots)\"\\s*:\\s*(\\d+)", out var detectedSlots))
                slots = Math.Max(slots, (int)detectedSlots);
        }
        if (printer is null && colors.Count == 0) return null;
        slots = Math.Clamp(Math.Max(slots, colors.Count), 1, 64);
        var filaments = colors.Select((color, index) => new LoadedFilament(index + 1, $"Filament {index + 1}", color)).ToList();
        return new PrinterCapabilities(printer ?? "Imprimante détectée", slots, Math.Clamp(nozzle, .1, 2), Math.Clamp(layer, .02, 2), filaments);
    }

    static string? Find(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : null;
    }

    static bool TryNumber(string text, string pattern, out double value)
    {
        var found = Find(text, pattern);
        return double.TryParse(found, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
