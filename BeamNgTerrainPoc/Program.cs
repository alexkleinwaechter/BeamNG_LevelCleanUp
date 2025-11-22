// Simple example

using BeamNGTerrainGenerator.BeamNG;
using SixLabors.ImageSharp.PixelFormats;

Console.WriteLine("BeamNG Terrain Creator");

// With arguments
if (args.Length > 0)
{
    Console.WriteLine($"Arguments: {string.Join(", ", args)}");
}

//var _level = new LevelExporter();
//_level.Height.Image = SixLabors.ImageSharp.Image.Load(@"D:\temp\TestMappingTools\_import\theTerrain_heightmap.png").CloneAs<L16>();

// Async support
await Task.Delay(100);
Console.WriteLine("Done!");