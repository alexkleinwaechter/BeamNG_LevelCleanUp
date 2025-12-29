namespace BeamNgTerrainPoc.Terrain.ColorExtraction.Models;

/// <summary>
/// Result of roughness extraction for a single material.
/// </summary>
/// <param name="MaterialName">Name of the terrain material</param>
/// <param name="RoughnessValue">Extracted dominant roughness value (0-255), or null if extraction failed</param>
/// <param name="PixelCount">Number of terrain pixels covered by this material</param>
/// <param name="CoveragePercent">Percentage of total terrain covered by this material (0.0 to 100.0)</param>
public record MaterialRoughnessResult(
    string MaterialName,
    int? RoughnessValue,
    int PixelCount,
    float CoveragePercent
);

/// <summary>
/// Complete result of terrain roughness extraction operation.
/// </summary>
/// <param name="RoughnessValues">Dictionary mapping material name to roughness value (0-255). -1 for materials with no coverage.</param>
/// <param name="TerrainSize">Size of the terrain (width = height, always square)</param>
/// <param name="Details">Detailed results for each material including coverage statistics</param>
public record RoughnessExtractionSummary(
    Dictionary<string, int> RoughnessValues,
    uint TerrainSize,
    IReadOnlyList<MaterialRoughnessResult> Details
);
