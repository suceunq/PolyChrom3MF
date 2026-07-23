using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace PolyChrom3MF.App;

public sealed class StlService
{
    public ModelDocument Read(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sélectionnez un fichier .stl valide.");
        var info = new FileInfo(path);
        if (info.Length < 15) throw new InvalidDataException("Le fichier STL est vide ou incomplet.");
        var (vertices, triangles) = IsBinary(path) ? ReadBinary(path) : ReadAscii(path);
        if (triangles.Count == 0) throw new InvalidDataException("Aucun triangle exploitable n’a été trouvé dans ce STL.");
        var obj = new ModelObject(0, "1", vertices, triangles, "3D/3dmodel.model");
        // Building millions of XML elements during import duplicates the whole
        // STL in memory. The 3MF XML is generated only when the user exports.
        return new ModelDocument(path, new XDocument(), "3D/3dmodel.model", [obj],
            vertices.Max(v => v.X) - vertices.Min(v => v.X), vertices.Max(v => v.Y) - vertices.Min(v => v.Y), vertices.Max(v => v.Z) - vertices.Min(v => v.Z),
            [], triangles.Count, "Le format STL ne contient ni unité ni matériaux : les dimensions sont interprétées en millimètres et le maillage forme un seul objet.", "millimeter", 0, 0, "STL");
    }

    internal static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 84) return false;
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        stream.Position = 80;
        var count = reader.ReadUInt32();
        return 84L + count * 50L == stream.Length;
    }

    static (List<Vertex>, List<Triangle>) ReadBinary(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        stream.Position = 80; var count = reader.ReadUInt32();
        if (count > int.MaxValue) throw new InvalidDataException("Le nombre de triangles dépasse la capacité d’adressage de cette version de Windows.");
        var vertices = new List<Vertex>();
        var triangles = new List<Triangle>(checked((int)count));
        var map = new Dictionary<(float X, float Y, float Z), int>();
        for (uint t = 0; t < count; t++)
        {
            stream.Position += 12;
            var indices = new int[3];
            for (var v = 0; v < 3; v++) indices[v] = AddVertex(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), vertices, map);
            reader.ReadUInt16(); triangles.Add(new Triangle(indices[0], indices[1], indices[2]));
        }
        return (vertices, triangles);
    }

    static (List<Vertex>, List<Triangle>) ReadAscii(string path)
    {
        var vertices = new List<Vertex>(); var triangles = new List<Triangle>();
        var map = new Dictionary<(float X, float Y, float Z), int>(); var current = new List<int>(3);
        using var reader = new StreamReader(path, Encoding.UTF8, true, 64 * 1024);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var values = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 4 || !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                throw new InvalidDataException("STL ASCII invalide : coordonnées de sommet incorrectes.");
            current.Add(AddVertex(x, y, z, vertices, map));
            if (current.Count == 3) { triangles.Add(new Triangle(current[0], current[1], current[2])); current.Clear(); }
        }
        if (current.Count != 0) throw new InvalidDataException("STL ASCII invalide : facette incomplète.");
        return (vertices, triangles);
    }

    static int AddVertex(float x, float y, float z, List<Vertex> vertices, Dictionary<(float, float, float), int> map)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new InvalidDataException("Le STL contient une coordonnée non finie.");
        var key = (x, y, z); if (map.TryGetValue(key, out var existing)) return existing;
        var index = vertices.Count; vertices.Add(new Vertex(x, y, z)); map[key] = index; return index;
    }

}
