using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Sombre";
    public string ExportFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string PreferredSlicer { get; set; } = "";
    public bool OpenFolderAfterExport { get; set; }
    public bool VerifyAfterExport { get; set; } = true;
    public bool ShowWelcome { get; set; } = true;
    public int ColorCount { get; set; } = 4;
    public string PrinterName { get; set; } = "Imprimante multicolore";
    public int MaterialSlots { get; set; } = 4;
    public double NozzleDiameter { get; set; } = .4;
    public double LayerHeight { get; set; } = .2;
    public List<string> FilamentColors { get; set; } = ["#E53935", "#1E88E5", "#43A047", "#FDD835"];
    public string LastSeenVersion { get; set; } = "";
    public string LastReleaseNotes { get; set; } = "";
    public string PendingUpdateVersion { get; set; } = "";
    public string PendingUpdateNotes { get; set; } = "";
}

public sealed class SettingsService
{
    readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom3MF");
    public string FilePath => Path.Combine(_folder, "settings.json");

    public AppSettings Load()
    {
        try
        {
            var loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
            return Normalize(loaded);
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings value)
    {
        Normalize(value);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static AppSettings Normalize(AppSettings value)
    {
        if (value.Theme is not ("Sombre" or "Clair" or "Système")) value.Theme = "Sombre";
        if (string.IsNullOrWhiteSpace(value.ExportFolder)) value.ExportFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        value.PreferredSlicer ??= "";
        value.LastSeenVersion = SafeText(value.LastSeenVersion, 32);
        value.LastReleaseNotes = SafeText(value.LastReleaseNotes, 4000);
        value.PendingUpdateVersion = SafeText(value.PendingUpdateVersion, 32);
        value.PendingUpdateNotes = SafeText(value.PendingUpdateNotes, 4000);
        value.ColorCount = Math.Clamp(value.ColorCount, 2, 32);
        value.PrinterName = SafeText(value.PrinterName, 100);
        if (string.IsNullOrWhiteSpace(value.PrinterName)) value.PrinterName = "Imprimante multicolore";
        value.MaterialSlots = Math.Clamp(value.MaterialSlots, 1, 64);
        if (!double.IsFinite(value.NozzleDiameter)) value.NozzleDiameter = .4;
        if (!double.IsFinite(value.LayerHeight)) value.LayerHeight = .2;
        value.NozzleDiameter = Math.Clamp(value.NozzleDiameter, .1, 2);
        value.LayerHeight = Math.Clamp(value.LayerHeight, .02, 2);
        value.FilamentColors = (value.FilamentColors ?? [])
            .Where(color => !string.IsNullOrWhiteSpace(color) && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            .Select(color => color.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (value.FilamentColors.Count == 0) value.FilamentColors = ["#E53935", "#1E88E5", "#43A047", "#FDD835"];
        return value;
    }

    static string SafeText(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character)).Take(maximum).ToArray()).Trim();
}
