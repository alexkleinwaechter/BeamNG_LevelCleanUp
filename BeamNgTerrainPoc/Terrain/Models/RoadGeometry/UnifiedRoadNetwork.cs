namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Unified road network containing all materials' roads.
/// This is the central data structure for the material-agnostic processing pipeline.
/// All roads from all materials are merged here for unified junction detection,
/// elevation harmonization, and terrain blending.
/// </summary>
public class UnifiedRoadNetwork
{
    /// <summary>
    /// All parameterized road splines from all materials.
    /// </summary>
    public List<ParameterizedRoadSpline> Splines { get; } = [];

    /// <summary>
    /// All cross-sections generated from all splines.
    /// Each cross-section references its owning spline via OwnerSplineId.
    /// </summary>
    public List<UnifiedCrossSection> CrossSections { get; } = [];

    /// <summary>
    /// Detected junctions in the unified network.
    /// Populated by NetworkJunctionDetector after splines and cross-sections are added.
    /// </summary>
    public List<NetworkJunction> Junctions { get; } = [];

    /// <summary>
    /// Maps SplineId -> MaterialName for the painting phase.
    /// Used to apply the correct terrain material texture to each road.
    /// </summary>
    public Dictionary<int, string> SplineMaterialMap { get; } = new();

    /// <summary>
    /// Maps SplineId -> ParameterizedRoadSpline for quick lookup.
    /// </summary>
    private readonly Dictionary<int, ParameterizedRoadSpline> _splineById = new();

    /// <summary>
    /// Thread lock for concurrent cross-section generation.
    /// </summary>
    private readonly object _crossSectionLock = new();

    /// <summary>
    /// Adds a parameterized spline to the network.
    /// </summary>
    /// <param name="spline">The spline to add</param>
    public void AddSpline(ParameterizedRoadSpline spline)
    {
        Splines.Add(spline);
        SplineMaterialMap[spline.SplineId] = spline.MaterialName;
        _splineById[spline.SplineId] = spline;
    }

    /// <summary>
    /// Adds a cross-section to the network (thread-safe).
    /// </summary>
    /// <param name="crossSection">The cross-section to add</param>
    public void AddCrossSection(UnifiedCrossSection crossSection)
    {
        lock (_crossSectionLock)
        {
            CrossSections.Add(crossSection);
        }
    }

    /// <summary>
    /// Adds multiple cross-sections to the network (thread-safe batch operation).
    /// </summary>
    /// <param name="crossSections">The cross-sections to add</param>
    public void AddCrossSections(IEnumerable<UnifiedCrossSection> crossSections)
    {
        lock (_crossSectionLock)
        {
            CrossSections.AddRange(crossSections);
        }
    }

    /// <summary>
    /// Gets a spline by its ID.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>The spline, or null if not found</returns>
    public ParameterizedRoadSpline? GetSplineById(int splineId)
    {
        return _splineById.GetValueOrDefault(splineId);
    }

    /// <summary>
    /// Gets the parameters for a spline by its ID.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>The parameters, or null if spline not found</returns>
    public RoadSmoothingParameters? GetParametersForSpline(int splineId)
    {
        return GetSplineById(splineId)?.Parameters;
    }

    /// <summary>
    /// Gets all cross-sections belonging to a specific spline.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>Cross-sections for the spline, ordered by local index</returns>
    public IEnumerable<UnifiedCrossSection> GetCrossSectionsForSpline(int splineId)
    {
        return CrossSections
            .Where(cs => cs.OwnerSplineId == splineId)
            .OrderBy(cs => cs.LocalIndex);
    }

    /// <summary>
    /// Gets all splines from a specific material.
    /// </summary>
    /// <param name="materialName">The material name</param>
    /// <returns>Splines belonging to the material</returns>
    public IEnumerable<ParameterizedRoadSpline> GetSplinesForMaterial(string materialName)
    {
        return Splines.Where(s => s.MaterialName == materialName);
    }

    /// <summary>
    /// Gets all unique material names in the network.
    /// </summary>
    public IEnumerable<string> GetMaterialNames()
    {
        return SplineMaterialMap.Values.Distinct();
    }

    /// <summary>
    /// Gets network statistics for debugging and logging.
    /// </summary>
    public NetworkStatistics GetStatistics()
    {
        return new NetworkStatistics
        {
            TotalSplines = Splines.Count,
            TotalCrossSections = CrossSections.Count,
            TotalJunctions = Junctions.Count,
            MaterialCount = GetMaterialNames().Count(),
            TotalRoadLengthMeters = Splines.Sum(s => s.TotalLengthMeters),
            SplinesByMaterial = Splines
                .GroupBy(s => s.MaterialName)
                .ToDictionary(g => g.Key, g => g.Count()),
            JunctionsByType = Junctions
                .GroupBy(j => j.Type)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    /// <summary>
    /// Clears all data from the network.
    /// </summary>
    public void Clear()
    {
        Splines.Clear();
        CrossSections.Clear();
        Junctions.Clear();
        SplineMaterialMap.Clear();
        _splineById.Clear();
    }
}

/// <summary>
/// Statistics about the unified road network.
/// </summary>
public class NetworkStatistics
{
    public int TotalSplines { get; init; }
    public int TotalCrossSections { get; init; }
    public int TotalJunctions { get; init; }
    public int MaterialCount { get; init; }
    public float TotalRoadLengthMeters { get; init; }
    public Dictionary<string, int> SplinesByMaterial { get; init; } = new();
    public Dictionary<JunctionType, int> JunctionsByType { get; init; } = new();

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Road Network Statistics:",
            $"  Splines: {TotalSplines}",
            $"  Cross-sections: {TotalCrossSections}",
            $"  Junctions: {TotalJunctions}",
            $"  Materials: {MaterialCount}",
            $"  Total road length: {TotalRoadLengthMeters:F1}m ({TotalRoadLengthMeters / 1000:F2}km)"
        };

        if (SplinesByMaterial.Count > 0)
        {
            lines.Add("  Splines by material:");
            foreach (var (material, count) in SplinesByMaterial.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"    {material}: {count}");
            }
        }

        if (JunctionsByType.Count > 0)
        {
            lines.Add("  Junctions by type:");
            foreach (var (type, count) in JunctionsByType.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"    {type}: {count}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
