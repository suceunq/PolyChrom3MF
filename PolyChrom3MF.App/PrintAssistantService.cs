using System.IO;

namespace PolyChrom3MF.App;

public sealed record LoadedFilament(int Slot, string Name, string Hex, string Material = "PLA");
public sealed record PrinterCapabilities(string Name, int MaterialSlots, double NozzleDiameter, double LayerHeight, IReadOnlyList<LoadedFilament> Filaments);
public sealed record FilamentMatch(int ColorIndex, int Slot, string ModelHex, string FilamentHex, double Distance);
public sealed record PrintPreparation(IReadOnlyList<FilamentMatch> Matches, IReadOnlyList<LoadedFilament> UnusedFilaments, IReadOnlyList<string> Warnings);

public sealed class PrintAssistantService
{
    public PrintPreparation Analyze(ColorProposal proposal, PrinterCapabilities printer, double smallestDetailMillimeters = double.PositiveInfinity)
    {
        if (printer.MaterialSlots < 1 || printer.MaterialSlots > 64 || printer.NozzleDiameter is < .1 or > 2 || printer.LayerHeight is <= 0 or > 2)
            throw new InvalidDataException("Le profil d’imprimante est invalide.");
        var matches = new List<FilamentMatch>();
        var usedSlots = new HashSet<int>();
        foreach (var (color, colorIndex) in proposal.Colors.Select((color, index) => (color, index)))
        {
            var best = printer.Filaments
                .Select(filament => new { Filament = filament, Distance = Distance(color.Hex, filament.Hex) })
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (best is null) continue;
            matches.Add(new FilamentMatch(colorIndex, best.Filament.Slot, color.Hex, best.Filament.Hex, best.Distance));
            usedSlots.Add(best.Filament.Slot);
        }
        var warnings = new List<string>();
        if (proposal.Colors.Count > printer.MaterialSlots) warnings.Add($"{proposal.Colors.Count} couleurs pour {printer.MaterialSlots} emplacements : changements manuels nécessaires.");
        if (smallestDetailMillimeters < printer.NozzleDiameter) warnings.Add($"Détail de {smallestDetailMillimeters:0.###} mm inférieur à la buse de {printer.NozzleDiameter:0.###} mm.");
        if (printer.Filaments.Count == 0) warnings.Add("Aucun filament chargé n’a été détecté.");
        return new PrintPreparation(matches, printer.Filaments.Where(filament => !usedSlots.Contains(filament.Slot)).ToList(), warnings);
    }

    internal static double Distance(string first, string second)
    {
        var a = Parse(first); var b = Parse(second);
        var dr = a.R - b.R; var dg = a.G - b.G; var db = a.B - b.B;
        return Math.Sqrt(dr * dr * .30 + dg * dg * .59 + db * db * .11);
    }

    static (int R, int G, int B) Parse(string hex)
    {
        if (hex?.Length != 7 || hex[0] != '#') throw new InvalidDataException("Une couleur de filament est invalide.");
        try { return (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16)); }
        catch (FormatException ex) { throw new InvalidDataException("Une couleur de filament est invalide.", ex); }
    }
}
