namespace BeamNgTerrainPoc.Terrain.ColorExtraction.Models;

/// <summary>
/// Result of color extraction for a single material.
/// </summary>
/// <param name="MaterialName">Name of the terrain material</param>
/// <param name="HexColor">Extracted average color in #RRGGBB format, or null if extraction failed</param>
/// <param name="PixelCount">Number of terrain pixels covered by this material</param>
/// <param name="CoveragePercent">Percentage of total terrain covered by this material (0.0 to 100.0)</param>
public record MaterialColorResult(
    string MaterialName,
    string? HexColor,
    int PixelCount,
    float CoveragePercent
);

/// <summary>
/// Complete result of terrain color extraction operation.
/// </summary>
/// <param name="Colors">Dictionary mapping material name to hex color (#RRGGBB). Empty string for materials with no coverage.</param>
/// <param name="TerrainSize">Size of the terrain (width = height, always square)</param>
/// <param name="Details">Detailed results for each material including coverage statistics</param>
public record ColorExtractionSummary(
    Dictionary<string, string> Colors,
    uint TerrainSize,
    IReadOnlyList<MaterialColorResult> Details
);
