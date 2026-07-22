using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace PolyChrom3MF.App;

public sealed record DetectedSlicer(string Name, string Path, string? Version = null)
{
    public string Label => string.IsNullOrWhiteSpace(Version) ? $"{Name} — {Path}" : $"{Name} {Version} — {Path}";
}

public sealed class SlicerDetectionService
{
    static readonly (string Name, string[] Executables)[] Known =
    [
        ("PrusaSlicer", ["prusa-slicer.exe"]),
        ("Snapmaker Orca", ["snapmaker-orca.exe", "Snapmaker_Orca.exe"]),
        ("OrcaSlicer", ["orca-slicer.exe", "OrcaSlicer.exe"]),
        ("Bambu Studio", ["bambu-studio.exe", "BambuStudio.exe"]),
        ("UltiMaker Cura", ["UltiMaker-Cura.exe", "Cura.exe"]),
        ("SuperSlicer", ["superslicer.exe", "SuperSlicer.exe"]),
        ("ideaMaker", ["ideaMaker.exe"]),
        ("Creality Print", ["CrealityPrint.exe", "Creality Print.exe"]),
        ("Anycubic Slicer", ["AnycubicSlicer.exe", "AnycubicSlicerNext.exe"]),
        ("QIDI Studio", ["QIDIStudio.exe", "QIDI Slicer.exe"]),
        ("Simplify3D", ["Simplify3D.exe"]),
        ("FlashPrint", ["FlashPrint.exe"])
    ];

    public List<DetectedSlicer> Detect(IEnumerable<string>? additionalPaths = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalPaths is not null) foreach (var path in additionalPaths) AddFile(paths, path);
        ReadAppPaths(paths, Registry.CurrentUser); ReadAppPaths(paths, Registry.LocalMachine);
        ReadUninstallRegistry(paths, Registry.CurrentUser); ReadUninstallRegistry(paths, Registry.LocalMachine);
        AddKnownLocations(paths);
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var executable in Known.SelectMany(x => x.Executables)) AddFile(paths, Path.Combine(folder, executable));

        return paths.Select(path => new DetectedSlicer(NameFor(path), path, ReadVersion(path)))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
            .OrderBy(x => Array.FindIndex(Known, known => known.Name == x.Name)).ThenBy(x => x.Name)
            .ToList();
    }

    static void AddKnownLocations(HashSet<string> paths)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var folders = new[] { "Prusa3D/PrusaSlicer", "PrusaSlicer", "Snapmaker_Orca", "Snapmaker Orca", "OrcaSlicer", "Bambu Studio", "UltiMaker Cura", "Ultimaker Cura", "SuperSlicer", "ideaMaker", "Creality Print", "AnycubicSlicer", "QIDIStudio", "Simplify3D", "FlashPrint" };
        foreach (var root in roots) foreach (var folder in folders) foreach (var executable in Known.SelectMany(x => x.Executables))
            AddFile(paths, Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), executable));
    }

    static void ReadAppPaths(HashSet<string> paths, RegistryKey hive)
    {
        try
        {
            using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (root is null) return;
            foreach (var executable in Known.SelectMany(x => x.Executables).Distinct(StringComparer.OrdinalIgnoreCase))
                using (var key = root.OpenSubKey(executable)) AddFile(paths, key?.GetValue(null)?.ToString());
        }
        catch { }
    }

    static void ReadUninstallRegistry(HashSet<string> paths, RegistryKey hive)
    {
        foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        try
        {
            using var root = hive.OpenSubKey(keyPath); if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var displayName = key?.GetValue("DisplayName")?.ToString() ?? "";
                var normalizedDisplayName = NormalizeName(displayName);
                if (!Known.Any(x => normalizedDisplayName.Contains(NormalizeName(x.Name), StringComparison.OrdinalIgnoreCase))) continue;
                AddFile(paths, CleanIconPath(key?.GetValue("DisplayIcon")?.ToString()));
                var location = key?.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(location)) foreach (var executable in Known.SelectMany(x => x.Executables)) AddFile(paths, Path.Combine(location, executable));
            }
        }
        catch { }
    }

    static string? CleanIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim().Trim('"'); var comma = result.LastIndexOf(',');
        if (comma > 2 && int.TryParse(result[(comma + 1)..], out _)) result = result[..comma].Trim('"');
        return result;
    }

    static void AddFile(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); if (File.Exists(path) && IsKnown(path)) paths.Add(path); } catch { }
    }

    static string NormalizeName(string value) => value.Replace('_', ' ').Replace('-', ' ');

    static string? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch { return null; }
    }

    static bool IsKnown(string path) => Known.Any(x => x.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
    static string NameFor(string path) => Known.FirstOrDefault(x => x.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)).Name ?? Path.GetFileNameWithoutExtension(path);
}
