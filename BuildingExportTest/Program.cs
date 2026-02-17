using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNgTerrainPoc.Terrain.Building;

// Output to Desktop for easy access
var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BuildingExportTest");
Directory.CreateDirectory(outputDir);

Console.WriteLine("=== Building DAE Export Test ===");
Console.WriteLine($"Output: {outputDir}");
Console.WriteLine();

var materialLibrary = new BuildingMaterialLibrary();
var exporter = new BuildingDaeExporter(materialLibrary);

// --- Building 1: Simple rectangular house (brick walls, tile roof) ---
var house = new BuildingData
{
    OsmId = 1001,
    BuildingType = "residential",
    Height = 6.0f,
    RoofHeight = 0f, // flat roof for Phase 1
    Levels = 2,
    WallMaterial = "BRICK",
    RoofMaterial = "ROOF_DEFAULT",
    FootprintOuter =
    [
        new Vector2(-5, -4),
        new Vector2( 5, -4),
        new Vector2( 5,  4),
        new Vector2(-5,  4),
    ]
};

// --- Building 2: L-shaped commercial building (plaster walls, concrete roof) ---
var commercial = new BuildingData
{
    OsmId = 1002,
    BuildingType = "commercial",
    Height = 9.0f,
    Levels = 3,
    WallMaterial = "BUILDING_DEFAULT",
    RoofMaterial = "CONCRETE",
    FootprintOuter =
    [
        new Vector2(-8, -6),
        new Vector2( 4, -6),
        new Vector2( 4, -1),
        new Vector2( 8, -1),
        new Vector2( 8,  6),
        new Vector2(-8,  6),
    ]
};

// --- Building 3: Small wooden shed ---
var shed = new BuildingData
{
    OsmId = 1003,
    BuildingType = "shed",
    Height = 3.0f,
    Levels = 1,
    WallMaterial = "WOOD_WALL",
    RoofMaterial = "WOOD",
    FootprintOuter =
    [
        new Vector2(-2, -1.5f),
        new Vector2( 2, -1.5f),
        new Vector2( 2,  1.5f),
        new Vector2(-2,  1.5f),
    ]
};

// --- Building 4: Building with a courtyard (hole) ---
var courtyard = new BuildingData
{
    OsmId = 1004,
    BuildingType = "apartments",
    Height = 12.0f,
    Levels = 4,
    WallMaterial = "CONCRETE",
    RoofMaterial = "CONCRETE",
    FootprintOuter =
    [
        new Vector2(-10, -8),
        new Vector2( 10, -8),
        new Vector2( 10,  8),
        new Vector2(-10,  8),
    ],
    FootprintHoles =
    [
        // Inner courtyard (clockwise winding for holes)
        [
            new Vector2(-5, -3),
            new Vector2(-5,  3),
            new Vector2( 5,  3),
            new Vector2( 5, -3),
        ]
    ]
};

var buildings = new List<BuildingData> { house, commercial, shed, courtyard };

// Export all buildings
Console.WriteLine("Exporting buildings...");
var result = exporter.ExportAll(buildings, outputDir, (current, total) =>
{
    Console.WriteLine($"  [{current}/{total}] building_{buildings[current - 1].OsmId}.dae");
});

Console.WriteLine();
Console.WriteLine(result);

// Deploy placeholder textures
Console.WriteLine();
Console.WriteLine("Deploying textures...");
var textureDir = Path.Combine(outputDir, "textures");
var usedMaterials = materialLibrary.GetUsedMaterials(buildings);
int textureCount = materialLibrary.DeployTextures(usedMaterials, textureDir);
Console.WriteLine($"  {textureCount} texture files deployed to {textureDir}");

Console.WriteLine();
Console.WriteLine("Done! Open the .dae files in Blender or BeamNG editor to verify.");
Console.WriteLine($"  Files: {outputDir}");
