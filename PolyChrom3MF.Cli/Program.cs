using PolyChrom3MF.App;

if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine("Usage: PolyChrom3MF.Cli <source.3mf|source.stl> <destination.3mf> [fun] [nombre-couleurs-4-a-32]");
    return 2;
}

try
{
    var service = new ThreeMfService();
    var document = Path.GetExtension(args[0]).Equals(".stl", StringComparison.OrdinalIgnoreCase) ? new StlService().Read(args[0]) : service.Read(args[0]);
    var fun = args.Length >= 3 && args[2].Equals("fun", StringComparison.OrdinalIgnoreCase);
    var colorCount = args.Length == 4 && int.TryParse(args[3], out var parsed) ? Math.Clamp(parsed, 4, 32) : 4;
    var proposal = new PaletteService().Create(document, generation: 0, fun: fun, colorCount: colorCount)[fun ? 3 : 0];
    Console.WriteLine(service.ExportAndValidate(document, proposal, args[1], true));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
