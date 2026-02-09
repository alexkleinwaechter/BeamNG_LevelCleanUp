namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Result of unified road network smoothing.
/// Contains the modified heightmap, material layer masks, and network data.
/// </summary>
public class UnifiedSmoothingResult
{
    /// <summary>
    /// The heightmap after road smoothing has been applied.
    /// </summary>
    public required float[,] ModifiedHeightMap { get; init; }

    /// <summary>
    /// Material layer masks for terrain painting.
    /// Key = material name, Value = 2D mask (0-255, 255 = full material presence).
    /// </summary>
    public Dictionary<string, byte[,]> MaterialLayers { get; init; } = new();

    /// <summary>
    /// The unified road network containing all splines, cross-sections, and junctions.
    /// Useful for debugging and exporting.
    /// </summary>
    public required UnifiedRoadNetwork Network { get; init; }

    /// <summary>
    /// Combined statistics from the smoothing operation.
    /// </summary>
    public required SmoothingStatistics Statistics { get; init; }

    /// <summary>
    /// Delta map showing elevation changes (modified - original).
    /// Positive = fill, Negative = cut.
    /// </summary>
    public float[,]? DeltaMap { get; init; }

    /// <summary>
    /// Per-material statistics if needed.
    /// </summary>
    public Dictionary<string, SmoothingStatistics> MaterialStatistics { get; init; } = new();

    /// <summary>
    /// Spawn point extracted from the road network (position on longest road).
    /// </summary>
    public SpawnPointData? ExtractedSpawnPoint { get; set; }

    /// <summary>
    /// Converts to a standard SmoothingResult for backward compatibility.
    /// Uses the first material's geometry or creates an empty one.
    /// 
    /// IMPORTANT: This method now also sets RoadGeometry.Spline from the network's
    /// first spline to ensure debug exports use the same interpolated path as
    /// elevation smoothing and material painting.
    /// </summary>
    /// <param name="originalRoadMask">The original road mask for geometry compatibility</param>
    /// <param name="parameters">The road smoothing parameters</param>
    /// <returns>A SmoothingResult compatible with existing code</returns>
    public SmoothingResult ToSmoothingResult(byte[,] originalRoadMask, RoadSmoothingParameters parameters)
    {
        // Create a RoadGeometry from the network for compatibility
        var geometry = new RoadGeometry(originalRoadMask, parameters);

        // Set spline from network for debug exports (use first spline if available)
        // This ensures debug images show the SAME interpolated path used for smoothing
        if (Network.Splines.Count > 0)
        {
            geometry.Spline = Network.Splines[0].Spline;
        }

        // Convert UnifiedCrossSections to standard CrossSections
        foreach (var ucs in Network.CrossSections)
        {
            geometry.CrossSections.Add(ucs.ToCrossSection());
        }

        return new SmoothingResult(
            ModifiedHeightMap,
            DeltaMap ?? new float[ModifiedHeightMap.GetLength(0), ModifiedHeightMap.GetLength(1)],
            Statistics,
            geometry);
    }
}
