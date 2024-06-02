// See https://aka.ms/new-console-template for more information
using etsmaterialgen;

Console.WriteLine("ETS2 to BeamNG Materialgenerator");
if (args.Length == 2)
{
    foreach (var arg in args)
    {
        Console.WriteLine($"Argument={arg}");
    }

    var converter = new EtsMaterialConverter(args[0], args[1]);
    converter.Convert();
}
else
{
    Console.WriteLine("No or not enough arguments");
}
