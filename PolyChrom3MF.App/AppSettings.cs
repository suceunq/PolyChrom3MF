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
    public List<string> FilamentColors { get; set; } = ["#E53935", "#1E88E5", "#43A047", "#FDD835"];
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
        value.ColorCount = Math.Clamp(value.ColorCount, 4, 32);
        value.FilamentColors = (value.FilamentColors ?? [])
            .Where(color => !string.IsNullOrWhiteSpace(color) && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            .Select(color => color.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (value.FilamentColors.Count == 0) value.FilamentColors = ["#E53935", "#1E88E5", "#43A047", "#FDD835"];
        return value;
    }
}
