namespace PolyChrom3MF.App;

public sealed class PaletteService
{
    readonly PaletteColor[][][] _styles =
    [
        [
            [new("Bleu ardoise", "#385A7C"), new("Sable", "#D6A96C"), new("Ivoire", "#EEE8D7"), new("Vert forêt", "#48705B")],
            [new("Bleu océan", "#2878A5"), new("Terracotta", "#C96F4A"), new("Crème", "#F1DFC0"), new("Sauge", "#6E9277")],
            [new("Indigo", "#465A9A"), new("Abricot", "#E89B62"), new("Lin", "#E8DCC8"), new("Émeraude", "#3D8068")],
            [new("Pétrole", "#296879"), new("Cuivre", "#B86F45"), new("Perle", "#E9E6DC"), new("Olive", "#70804A")]
        ],
        [
            [new("Bleu nuit", "#173D63"), new("Orange vif", "#E66A2C"), new("Blanc", "#F4F5F1"), new("Anthracite", "#30343B")],
            [new("Violet", "#5B2C83"), new("Jaune", "#F2C230"), new("Cyan", "#24A7B8"), new("Rouge", "#C73D45")],
            [new("Marine", "#102B4E"), new("Corail", "#F06C5F"), new("Citron", "#E9D842"), new("Blanc froid", "#EAF3F7")],
            [new("Noir", "#202124"), new("Magenta", "#C13A87"), new("Turquoise", "#22A6A1"), new("Orange", "#F08B32")]
        ],
        [
            [new("Gris chaud", "#807B72"), new("Crème", "#DED6C5"), new("Brun", "#765143"), new("Noir doux", "#292A2B")],
            [new("Graphite", "#41464D"), new("Argent", "#AAB0B5"), new("Taupe", "#81756B"), new("Ivoire", "#E7E0D1")],
            [new("Ardoise", "#4B5663"), new("Grège", "#AAA093"), new("Chocolat", "#59443B"), new("Sable clair", "#D7C9AD")],
            [new("Charbon", "#303236"), new("Pierre", "#8C8A83"), new("Bronze", "#806346"), new("Porcelaine", "#E5E2D8")]
        ],
        [
            [new("Prune", "#713D6B"), new("Turquoise", "#2B9EA3"), new("Corail", "#E36B65"), new("Jaune soleil", "#E8B93F")],
            [new("Fuchsia", "#B33B82"), new("Lagon", "#24A6A0"), new("Mandarine", "#ED7D31"), new("Lilas", "#8E72C7")],
            [new("Bleu électrique", "#286ACB"), new("Rose", "#E34F91"), new("Menthe", "#48B88A"), new("Or", "#D9A52E")],
            [new("Aubergine", "#613659"), new("Aqua", "#2B9CB3"), new("Papaye", "#E9824B"), new("Citron vert", "#9ABF45")]
        ]
    ];

    static readonly string[] Names = ["Proposition 1 — Équilibrée", "Proposition 2 — Contrastée", "Proposition 3 — Sobre", "Proposition 4 — Créative"];
    static readonly string[] Descriptions = ["Zones harmonieuses", "Zones fortement contrastées", "Zones neutres et élégantes", "Zones originales et expressives"];
    static readonly string[] FunNames = ["Fun 1 — Aurore ondulée", "Fun 2 — Camouflage organique", "Fun 3 — Double personnalité", "Fun 4 — Graffiti pop"];
    static readonly string[] FunDescriptions = ["Dégradé vivant aux frontières ondulées", "Taches organiques réparties sur toute la sculpture", "Séparation dramatique avec accents contrastés", "Réseau de lignes et symboles abstraits"];

    public static bool DisableColor(ColorProposal proposal, int colorIndex)
    {
        if (proposal.Colors.Count <= 2 || colorIndex < 0 || colorIndex >= proposal.Colors.Count) return false;
        var replacementOldIndex = FindReplacementColorIndex(proposal, colorIndex);
        if (replacementOldIndex < 0) return false;
        foreach (var key in proposal.Assignments.Keys.ToList())
            proposal.Assignments[key] = RemapColorIndex(proposal.Assignments[key], colorIndex, replacementOldIndex);
        foreach (var values in proposal.TriangleAssignments.Values)
            for (var index = 0; index < values.Length; index++)
                values[index] = RemapColorIndex(values[index], colorIndex, replacementOldIndex);
        proposal.Colors.RemoveAt(colorIndex);
        return true;
    }

    public static int FindReplacementColorIndex(ColorProposal proposal, int colorIndex)
    {
        if (proposal.Colors.Count <= 1 || colorIndex < 0 || colorIndex >= proposal.Colors.Count) return -1;
        var removed = proposal.Colors[colorIndex].Color;
        return Enumerable.Range(0, proposal.Colors.Count)
            .Where(index => index != colorIndex)
            .MinBy(index =>
            {
                var candidate = proposal.Colors[index].Color;
                var red = candidate.R - removed.R;
                var green = candidate.G - removed.G;
                var blue = candidate.B - removed.B;
                return red * red + green * green + blue * blue;
            });
    }

    public static int RemapColorIndex(int value, int removedIndex, int replacementOldIndex)
    {
        if (value < 0) return value;
        var replacement = replacementOldIndex > removedIndex ? replacementOldIndex - 1 : replacementOldIndex;
        if (value == removedIndex) return replacement;
        return value > removedIndex ? value - 1 : value;
    }

    public List<ColorProposal> Create(int objectCount, IReadOnlyList<string>? available = null, int generation = 0, bool fun = false, int colorCount = 4)
    {
        colorCount = Math.Clamp(colorCount, 2, 32);
        var result = new List<ColorProposal>(4);
        for (var style = 0; style < 4; style++)
        {
            var paletteStyle = fun ? new[] { 3, 1, 0, 3 }[style] : style;
            var colors = available is { Count: > 0 }
                ? TakeAvailable(available, style + generation, colorCount)
                : ExpandPalette(_styles[paletteStyle][Math.Abs(generation + (fun ? style : 0)) % _styles[paletteStyle].Length], colorCount);
            var assignments = Enumerable.Range(0, objectCount).ToDictionary(index => index, index => (index + style + generation) % colorCount);
            result.Add(new ColorProposal(fun ? FunNames[style] : Names[style], fun ? FunDescriptions[style] : Descriptions[style], colors, assignments));
        }
        return result;
    }

    public List<ColorProposal> Create(ModelDocument document, IReadOnlyList<string>? available = null, int generation = 0, bool fun = false, int colorCount = 4)
    {
        colorCount = Math.Clamp(colorCount, 2, 32);
        var proposals = Create(document.Objects.Count, available, generation, fun, colorCount);
        for (var style = 0; style < proposals.Count; style++)
        {
            foreach (var obj in document.Objects)
                proposals[style].TriangleAssignments[obj.Index] = CreateTriangleAssignments(obj, style, generation, fun, colorCount);
            EnsureColors(proposals[style], colorCount);
        }
        return proposals;
    }

    static void EnsureColors(ColorProposal proposal, int colorCount)
    {
        var all = proposal.TriangleAssignments.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.Select((_, index) => (Values: pair.Value, Index: index))).Take(colorCount).ToList();
        if (all.Count < colorCount) return;
        for (var color = 0; color < colorCount; color++) all[color].Values[all[color].Index] = color;
    }

    static int[] CreateTriangleAssignments(ModelObject obj, int style, int generation, bool fun, int colorCount)
    {
        var result = new int[obj.Triangles.Count];
        var minX = obj.Vertices.Min(v => v.X); var maxX = obj.Vertices.Max(v => v.X);
        var minY = obj.Vertices.Min(v => v.Y); var maxY = obj.Vertices.Max(v => v.Y);
        var minZ = obj.Vertices.Min(v => v.Z); var maxZ = obj.Vertices.Max(v => v.Z);
        var sizeX = Math.Max(.000001, maxX - minX); var sizeY = Math.Max(.000001, maxY - minY); var sizeZ = Math.Max(.000001, maxZ - minZ);
        var centerX = (minX + maxX) / 2; var centerY = (minY + maxY) / 2;
        for (var i = 0; i < obj.Triangles.Count; i++)
        {
            var triangle = obj.Triangles[i]; var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
            var x = (a.X + b.X + c.X) / 3; var y = (a.Y + b.Y + c.Y) / 3; var z = (a.Z + b.Z + c.Z) / 3;
            var nx = (x - minX) / sizeX; var ny = (y - minY) / sizeY; var nz = (z - minZ) / sizeZ;
            if (!fun)
                result[i] = style switch
                {
                    0 => Mod((int)Math.Min(colorCount - 1, Math.Floor(nz * colorCount)) + generation, colorCount),
                    1 => Mod((int)Math.Floor((Math.Atan2(y - centerY, x - centerX) + Math.PI) / (Math.PI * 2) * colorCount) + generation, colorCount),
                    2 => Mod((int)Math.Min(colorCount - 1, Math.Floor((generation % 2 == 0 ? nx : ny) * colorCount)) + (generation / 2), colorCount),
                    _ => Mod((int)Math.Floor(nx * (colorCount + 1)) + (int)Math.Floor(ny * colorCount) + (int)Math.Floor(nz * Math.Max(3, colorCount - 1)) + generation, colorCount)
                };
            else
                result[i] = FunColor(style, nx, ny, nz, generation, colorCount);
        }
        if (result.Length >= colorCount && result.Distinct().Count() < colorCount)
            for (var i = 0; i < colorCount; i++) result[i] = i;
        return result;
    }

    static int FunColor(int style, double x, double y, double z, int generation, int colorCount)
    {
        var phase = generation * .83;
        return style switch
        {
            0 => Mod((int)Math.Floor(z * colorCount + Math.Sin(x * 11 + phase) * 1.2 + Math.Sin(y * 13 - phase) * .9), colorCount),
            1 => Math.Clamp((int)Math.Floor(((Math.Sin(x * 12 + Math.Sin(y * 7 + phase) * 2) + Math.Sin(y * 11 + Math.Sin(z * 9 - phase) * 2) + Math.Sin(z * 13 + Math.Sin(x * 8) * 2)) / 6 + .5) * colorCount), 0, colorCount - 1),
            2 => SplitColor(x, y, z, phase, colorCount),
            _ => GraffitiColor(x, y, z, generation, colorCount)
        };
    }

    static int SplitColor(double x, double y, double z, double phase, int colorCount)
    {
        var half = Math.Max(1, colorCount / 2); var other = colorCount - half;
        return x < .5
            ? Mod((int)Math.Floor((y * 1.7 + z + Math.Sin((y + z) * 18 + phase) * .25) * half), half)
            : half + Mod((int)Math.Floor((y * 1.4 - z + Math.Sin((y - z) * 18 - phase) * .25) * other), other);
    }

    static int GraffitiColor(double x, double y, double z, int generation, int colorCount)
    {
        var stroke = Math.Abs(Math.Sin(x * 34 + Math.Sin(y * 15 + generation) * 4 + z * 9));
        if (stroke < .2) return Mod(1 + (int)Math.Floor(y * colorCount), colorCount);
        var crossStroke = Math.Abs(Math.Cos(y * 29 + Math.Sin(z * 17) * 3 - x * 8));
        if (crossStroke < .16) return Mod(colorCount - 1 - (int)Math.Floor(z * colorCount), colorCount);
        return HashCell((int)(x * 9), (int)(y * 9), (int)(z * 9), generation) % colorCount;
    }

    static int HashCell(int x, int y, int z, int seed)
    {
        unchecked { var hash = x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed * 265443576; return hash & int.MaxValue; }
    }

    static int Mod(int value, int modulo) => (value % modulo + modulo) % modulo;

    static PaletteColor CloneColor(PaletteColor color) => new(color.Name, color.Hex);
    static List<PaletteColor> TakeAvailable(IReadOnlyList<string> source, int offset, int count) => Enumerable.Range(0, count).Select(i => new PaletteColor("Filament " + (i + 1), source[(i + offset) % source.Count])).ToList();

    static List<PaletteColor> ExpandPalette(IReadOnlyList<PaletteColor> source, int count)
    {
        var result = source.Take(count).Select(CloneColor).ToList();
        for (var i = result.Count; i < count; i++)
        {
            var original = source[i % source.Count]; var (h, s, v) = ToHsv(original.Hex); var cycle = i / source.Count;
            h = (h + cycle * 31 + (i % source.Count) * 7) % 360; s = Math.Clamp(s * (.92 + cycle * .035), .42, .95); v = Math.Clamp(v * (cycle % 2 == 0 ? .88 : 1.08), .48, .96);
            result.Add(new PaletteColor($"{original.Name} {cycle + 1}", FromHsv(h, s, v)));
        }
        return result;
    }

    static (double H, double S, double V) ToHsv(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255d; var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255d; var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var d = max - min; var h = 0d;
        if (d > 0) h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360; return (h, max == 0 ? 0 : d / max, max);
    }

    static string FromHsv(double h, double s, double v)
    {
        var c = v * s; var x = c * (1 - Math.Abs((h / 60) % 2 - 1)); var m = v - c; (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return $"#{(int)Math.Round((r + m) * 255):X2}{(int)Math.Round((g + m) * 255):X2}{(int)Math.Round((b + m) * 255):X2}";
    }
}
