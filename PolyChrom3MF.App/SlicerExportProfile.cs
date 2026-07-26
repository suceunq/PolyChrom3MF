using System.Text.RegularExpressions;

namespace PolyChrom3MF.App;

public enum SlicerFamily
{
    Generic,
    Orca,
    SnapmakerOrca,
    BambuStudio,
    PrusaSlicer
}

public sealed class ExportProfileSettings
{
    public string Name { get; set; } = "Profil d’export";
    public string SlicerPath { get; set; } = "";
    public string SlicerName { get; set; } = "";
    public string PrinterPreset { get; set; } = "";
    public string ProcessPreset { get; set; } = "";
    public double NozzleDiameter { get; set; } = .4;
    public int ExtruderCount { get; set; } = 1;
    public int MaterialSlots { get; set; } = 4;
    public List<string> FilamentPresets { get; set; } = [];
    public List<string> FilamentMaterials { get; set; } = [];
    public List<string> FilamentColors { get; set; } = [];

    public SlicerFamily Family => SlicerProfileCatalogService.FamilyFor(SlicerName, SlicerPath);

    internal static ExportProfileSettings Normalize(ExportProfileSettings value)
    {
        value.Name = Safe(value.Name, 100);
        if (string.IsNullOrWhiteSpace(value.Name)) value.Name = "Profil d’export";
        value.SlicerPath = Safe(value.SlicerPath, 1024);
        value.SlicerName = Safe(value.SlicerName, 80);
        value.PrinterPreset = Safe(value.PrinterPreset, 160);
        value.ProcessPreset = Safe(value.ProcessPreset, 160);
        if (!double.IsFinite(value.NozzleDiameter)) value.NozzleDiameter = .4;
        value.NozzleDiameter = Math.Clamp(value.NozzleDiameter, .1, 2);
        value.ExtruderCount = Math.Clamp(value.ExtruderCount, 1, 64);
        value.MaterialSlots = Math.Clamp(value.MaterialSlots, 1, 64);
        value.FilamentPresets = (value.FilamentPresets ?? []).Select(item => Safe(item, 160)).Take(32).ToList();
        value.FilamentMaterials = (value.FilamentMaterials ?? [])
            .Select(MaterialName).Take(32).ToList();
        value.FilamentColors = (value.FilamentColors ?? [])
            .Select(color => Regex.IsMatch(color ?? "", "^#[0-9A-Fa-f]{6}$") ? color!.ToUpperInvariant() : "#FFFFFF")
            .Take(32).ToList();
        return value;
    }

    internal static string MaterialName(string? value)
    {
        var material = Safe(value, 32).ToUpperInvariant();
        return Regex.IsMatch(material, "^[A-Z0-9][A-Z0-9+_. -]{0,31}$") ? material : "PLA";
    }

    static string Safe(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character)).Take(maximum).ToArray()).Trim();
}

public sealed record InstalledSlicerPreset(
    string Name,
    string Type,
    string FilePath,
    string PrinterModel,
    string PrinterVariant,
    double NozzleDiameter,
    int ExtruderCount,
    string Material,
    IReadOnlyList<string> CompatiblePrinters)
{
    public string Label => Type switch
    {
        "machine" when !string.IsNullOrWhiteSpace(PrinterModel) => $"{Name} — {PrinterModel}",
        "filament" when !string.IsNullOrWhiteSpace(Material) => $"{Name} — {Material}",
        _ => Name
    };
}

public sealed record SlicerProfileCatalog(
    DetectedSlicer Slicer,
    IReadOnlyList<InstalledSlicerPreset> Printers,
    IReadOnlyList<InstalledSlicerPreset> Processes,
    IReadOnlyList<InstalledSlicerPreset> Filaments);
