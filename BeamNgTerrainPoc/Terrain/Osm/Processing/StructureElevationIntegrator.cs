using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Integrates structure elevation profiles with cross-section generation.
/// 
/// This class serves as the bridge between the structure elevation calculator
/// and the unified road network's cross-section processing pipeline.
/// 
/// Phase 5 Integration Flow:
/// 1. After network building and basic elevation calculation
/// 2. Calculate elevation profiles for all bridge/tunnel structures
/// 3. Apply structure elevations to cross-sections (overriding terrain-based elevations)
/// 4. Store profiles on splines for future DAE generation
/// 
/// Key responsibilities:
/// - Determine entry/exit elevations from connecting roads or terrain
/// - Calculate elevation profiles using StructureElevationCalculator
/// - Apply profile elevations to cross-sections
/// - Handle the case where structures connect at junctions
/// </summary>
public class StructureElevationIntegrator
{
    private readonly StructureElevationCalculator _calculator;

    /// <summary>
    /// Tolerance in meters for detecting connecting roads at structure endpoints.
    /// </summary>
    public float ConnectionTolerance { get; set; } = 15.0f;

    /// <summary>
    /// Number of terrain samples along each structure for tunnel clearance calculation.
    /// </summary>
    public int TerrainSampleCount { get; set; } = 20;

    public StructureElevationIntegrator()
    {
        _calculator = new StructureElevationCalculator();
    }

    public StructureElevationIntegrator(StructureElevationCalculator calculator)
    {
        _calculator = calculator;
    }

    /// <summary>
    /// Creates a StructureElevationIntegrator configured from TerrainCreationParameters.
    /// </summary>
    /// <param name="parameters">The terrain creation parameters containing structure elevation settings.</param>
    /// <returns>A configured StructureElevationIntegrator instance.</returns>
    public static StructureElevationIntegrator FromParameters(TerrainCreationParameters parameters)
    {
        var calculator = new StructureElevationCalculator
        {
            // Tunnel parameters
            TunnelMinClearanceMeters = parameters.TunnelMinClearanceMeters,
            TunnelInteriorHeightMeters = parameters.TunnelInteriorHeightMeters,
            TunnelMaxGradePercent = parameters.TunnelMaxGradePercent,
            ShortTunnelMaxLengthMeters = parameters.ShortTunnelMaxLengthMeters,
            
            // Bridge parameters
            ShortBridgeMaxLengthMeters = parameters.ShortBridgeMaxLengthMeters,
            MediumBridgeMaxLengthMeters = parameters.MediumBridgeMaxLengthMeters,
            
            // Terrain sampling
            DefaultTerrainSampleCount = parameters.StructureTerrainSampleCount
        };

        return new StructureElevationIntegrator(calculator)
        {
            ConnectionTolerance = parameters.StructureConnectionToleranceMeters,
            TerrainSampleCount = parameters.StructureTerrainSampleCount
        };
    }

    /// <summary>
    /// Applies configuration from TerrainCreationParameters to this integrator.
    /// </summary>
    /// <param name="parameters">The terrain creation parameters containing structure elevation settings.</param>
    public void ApplyParameters(TerrainCreationParameters parameters)
    {
        // Update calculator parameters
        _calculator.TunnelMinClearanceMeters = parameters.TunnelMinClearanceMeters;
        _calculator.TunnelInteriorHeightMeters = parameters.TunnelInteriorHeightMeters;
        _calculator.TunnelMaxGradePercent = parameters.TunnelMaxGradePercent;
        _calculator.ShortTunnelMaxLengthMeters = parameters.ShortTunnelMaxLengthMeters;
        _calculator.ShortBridgeMaxLengthMeters = parameters.ShortBridgeMaxLengthMeters;
        _calculator.MediumBridgeMaxLengthMeters = parameters.MediumBridgeMaxLengthMeters;
        _calculator.DefaultTerrainSampleCount = parameters.StructureTerrainSampleCount;

        // Update integrator parameters
        ConnectionTolerance = parameters.StructureConnectionToleranceMeters;
        TerrainSampleCount = parameters.StructureTerrainSampleCount;
    }

    /// <summary>
    /// Integrates structure elevation profiles into the road network.
    /// This should be called AFTER basic elevation calculation but BEFORE terrain blending.
    /// 
    /// Steps:
    /// 1. Find all bridge/tunnel splines in the network
    /// 2. For each structure, determine entry/exit elevations
    /// 3. Calculate the elevation profile (linear, parabolic, S-curve, arch)
    /// 4. Apply profile elevations to each cross-section
    /// </summary>
    /// <param name="network">The unified road network with cross-sections.</param>
    /// <param name="heightMap">The terrain heightmap [y, x] for terrain sampling.</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <returns>Result containing counts and any validation messages.</returns>
    public StructureElevationIntegrationResult IntegrateStructureElevations(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel)
    {
        var result = new StructureElevationIntegrationResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Get all structure splines
        var structureSplines = network.Splines.Where(s => s.IsStructure).ToList();

        if (structureSplines.Count == 0)
        {
            TerrainLogger.Info("StructureElevationIntegrator: No structures to process");
            return result;
        }

        TerrainLogger.Info($"StructureElevationIntegrator: Processing {structureSplines.Count} structure(s)");

        // Build a lookup of cross-sections by spline ID for efficient access
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // Build a spatial index for finding connecting roads
        var nonStructureSplines = network.Splines.Where(s => !s.IsStructure).ToList();

        foreach (var spline in structureSplines)
        {
            try
            {
                // Get cross-sections for this structure
                if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                {
                    TerrainLogger.Warning($"  Structure spline {spline.SplineId}: No cross-sections found");
                    continue;
                }

                // Step 1: Determine entry/exit elevations
                var (entryElevation, exitElevation) = DetermineEntryExitElevations(
                    spline,
                    crossSections,
                    nonStructureSplines,
                    crossSectionsBySpline,
                    heightMap,
                    metersPerPixel);

                // Step 2: Calculate elevation profile based on structure type
                StructureElevationProfile profile;

                if (spline.IsTunnel)
                {
                    // Sample terrain along tunnel path for clearance calculation
                    var terrainSamples = _calculator.SampleTerrainAlongStructure(
                        spline, heightMap, metersPerPixel, TerrainSampleCount);

                    profile = _calculator.CalculateTunnelProfile(
                        spline, entryElevation, exitElevation, terrainSamples);

                    result.TunnelsProcessed++;
                }
                else // Bridge
                {
                    profile = _calculator.CalculateBridgeProfile(
                        spline, entryElevation, exitElevation);

                    result.BridgesProcessed++;
                }

                // Step 3: Store profile on spline for future use (DAE generation)
                spline.ElevationProfile = profile;

                // Step 4: Apply profile elevations to cross-sections
                var crossSectionsModified = ApplyProfileToStructureCrossSections(
                    crossSections, profile, spline);

                result.CrossSectionsModified += crossSectionsModified;

                // Log profile summary
                TerrainLogger.Info(
                    $"  {(spline.IsBridge ? "Bridge" : "Tunnel")} spline {spline.SplineId}: " +
                    $"{profile.CurveType} profile, {profile.LengthMeters:F1}m length, " +
                    $"{entryElevation:F1}m -> {exitElevation:F1}m, " +
                    $"{crossSectionsModified} cross-sections");

                // Track validation issues
                if (!profile.IsValid)
                {
                    result.ValidationMessages.Add(
                        $"Spline {spline.SplineId}: {profile.ValidationMessage}");
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"  Error processing structure spline {spline.SplineId}: {ex.Message}");
                result.ValidationMessages.Add($"Spline {spline.SplineId}: {ex.Message}");
            }
        }

        sw.Stop();
        result.ProcessingTimeMs = sw.ElapsedMilliseconds;

        TerrainLogger.Info(
            $"StructureElevationIntegrator: Completed in {sw.ElapsedMilliseconds}ms - " +
            $"{result.BridgesProcessed} bridges, {result.TunnelsProcessed} tunnels, " +
            $"{result.CrossSectionsModified} cross-sections modified");

        return result;
    }

    /// <summary>
    /// Determines the entry and exit elevations for a structure by looking for
    /// connecting roads at the endpoints, or falling back to terrain elevation.
    /// </summary>
    private (float entryElevation, float exitElevation) DetermineEntryExitElevations(
        ParameterizedRoadSpline structureSpline,
        List<UnifiedCrossSection> structureCrossSections,
        List<ParameterizedRoadSpline> nonStructureSplines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float[,] heightMap,
        float metersPerPixel)
    {
        var entryPoint = structureSpline.StartPoint;
        var exitPoint = structureSpline.EndPoint;

        // Try to find connecting road elevations
        var entryElevation = FindConnectingRoadElevation(
            entryPoint, structureSpline.SplineId, nonStructureSplines, crossSectionsBySpline);

        var exitElevation = FindConnectingRoadElevation(
            exitPoint, structureSpline.SplineId, nonStructureSplines, crossSectionsBySpline);

        // If no connecting road found, fall back to terrain or cross-section elevation
        if (float.IsNaN(entryElevation))
        {
            // Try to use the first cross-section's original terrain elevation
            var firstCS = structureCrossSections.FirstOrDefault();
            if (firstCS != null && !float.IsNaN(firstCS.OriginalTerrainElevation))
            {
                entryElevation = firstCS.OriginalTerrainElevation;
            }
            else
            {
                // Fall back to direct terrain sampling
                entryElevation = _calculator.GetTerrainElevationAtEntry(
                    structureSpline, heightMap, metersPerPixel);
            }
        }

        if (float.IsNaN(exitElevation))
        {
            // Try to use the last cross-section's original terrain elevation
            var lastCS = structureCrossSections.LastOrDefault();
            if (lastCS != null && !float.IsNaN(lastCS.OriginalTerrainElevation))
            {
                exitElevation = lastCS.OriginalTerrainElevation;
            }
            else
            {
                // Fall back to direct terrain sampling
                exitElevation = _calculator.GetTerrainElevationAtExit(
                    structureSpline, heightMap, metersPerPixel);
            }
        }

        return (entryElevation, exitElevation);
    }

    /// <summary>
    /// Finds the elevation of a connecting road at a given point.
    /// Returns the target elevation of the nearest cross-section from a non-structure road.
    /// </summary>
    private float FindConnectingRoadElevation(
        Vector2 connectionPoint,
        int structureSplineId,
        List<ParameterizedRoadSpline> nonStructureSplines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        float nearestDistance = float.MaxValue;
        float nearestElevation = float.NaN;

        foreach (var spline in nonStructureSplines)
        {
            // Check if this spline's start or end is near the connection point
            var startDist = Vector2.Distance(spline.StartPoint, connectionPoint);
            var endDist = Vector2.Distance(spline.EndPoint, connectionPoint);

            // Check start point
            if (startDist <= ConnectionTolerance && startDist < nearestDistance)
            {
                if (crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                {
                    var firstCS = crossSections.FirstOrDefault();
                    if (firstCS != null && !float.IsNaN(firstCS.TargetElevation))
                    {
                        nearestDistance = startDist;
                        nearestElevation = firstCS.TargetElevation;
                    }
                }
            }

            // Check end point
            if (endDist <= ConnectionTolerance && endDist < nearestDistance)
            {
                if (crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                {
                    var lastCS = crossSections.LastOrDefault();
                    if (lastCS != null && !float.IsNaN(lastCS.TargetElevation))
                    {
                        nearestDistance = endDist;
                        nearestElevation = lastCS.TargetElevation;
                    }
                }
            }

            // Also check if any cross-section center is very close to the connection point
            // This handles cases where the connection is not at the exact spline endpoint
            if (crossSectionsBySpline.TryGetValue(spline.SplineId, out var allCrossSections))
            {
                foreach (var cs in allCrossSections)
                {
                    var csDist = Vector2.Distance(cs.CenterPoint, connectionPoint);
                    if (csDist <= ConnectionTolerance && csDist < nearestDistance)
                    {
                        if (!float.IsNaN(cs.TargetElevation))
                        {
                            nearestDistance = csDist;
                            nearestElevation = cs.TargetElevation;
                        }
                    }
                }
            }
        }

        return nearestElevation;
    }

    /// <summary>
    /// Applies the elevation profile to all cross-sections of a structure.
    /// Overrides the terrain-based target elevation with the calculated profile elevation.
    /// </summary>
    /// <param name="crossSections">Cross-sections belonging to the structure.</param>
    /// <param name="profile">The calculated elevation profile.</param>
    /// <param name="spline">The structure spline (for logging).</param>
    /// <returns>Number of cross-sections modified.</returns>
    private int ApplyProfileToStructureCrossSections(
        List<UnifiedCrossSection> crossSections,
        StructureElevationProfile profile,
        ParameterizedRoadSpline spline)
    {
        int modifiedCount = 0;

        foreach (var cs in crossSections)
        {
            // Calculate the elevation at this cross-section's distance along the structure
            var elevation = _calculator.CalculateStructureElevation(
                cs.DistanceAlongSpline, profile);

            // Store the original terrain elevation before overriding
            // (This may already be set, but ensure it's preserved)
            if (float.IsNaN(cs.OriginalTerrainElevation))
            {
                cs.OriginalTerrainElevation = cs.TargetElevation;
            }

            // Override the target elevation with the calculated structure elevation
            cs.TargetElevation = elevation;
            modifiedCount++;
        }

        return modifiedCount;
    }

    /// <summary>
    /// Calculates and applies elevation profiles for structures in the network
    /// that already have cross-sections with target elevations calculated.
    /// 
    /// Use this for post-processing when structures were skipped during normal elevation calculation.
    /// </summary>
    /// <param name="network">The unified road network.</param>
    /// <param name="heightMap">The terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="excludeBridges">When false, bridges are skipped.</param>
    /// <param name="excludeTunnels">When false, tunnels are skipped.</param>
    /// <returns>Integration result.</returns>
    public StructureElevationIntegrationResult IntegrateStructureElevationsSelective(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        bool excludeBridges = true,
        bool excludeTunnels = true)
    {
        var result = new StructureElevationIntegrationResult();

        if (!excludeBridges && !excludeTunnels)
        {
            // Nothing to do - no structures are being processed
            return result;
        }

        // Get structure splines that should be processed
        var structureSplines = network.Splines
            .Where(s => (s.IsBridge && excludeBridges) || (s.IsTunnel && excludeTunnels))
            .ToList();

        if (structureSplines.Count == 0)
        {
            return result;
        }

        TerrainLogger.Info(
            $"StructureElevationIntegrator: Processing {structureSplines.Count} structure(s) selectively " +
            $"(bridges: {excludeBridges}, tunnels: {excludeTunnels})");

        // Build lookup and delegate to main integration method
        // but filter the network splines appropriately
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var nonStructureSplines = network.Splines
            .Where(s => !((s.IsBridge && excludeBridges) || (s.IsTunnel && excludeTunnels)))
            .ToList();

        foreach (var spline in structureSplines)
        {
            try
            {
                if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                {
                    continue;
                }

                var (entryElevation, exitElevation) = DetermineEntryExitElevations(
                    spline, crossSections, nonStructureSplines, crossSectionsBySpline,
                    heightMap, metersPerPixel);

                StructureElevationProfile profile;

                if (spline.IsTunnel)
                {
                    var terrainSamples = _calculator.SampleTerrainAlongStructure(
                        spline, heightMap, metersPerPixel, TerrainSampleCount);
                    profile = _calculator.CalculateTunnelProfile(
                        spline, entryElevation, exitElevation, terrainSamples);
                    result.TunnelsProcessed++;
                }
                else
                {
                    profile = _calculator.CalculateBridgeProfile(
                        spline, entryElevation, exitElevation);
                    result.BridgesProcessed++;
                }

                spline.ElevationProfile = profile;

                // For excluded structures, we DON'T apply to cross-sections 
                // (they're excluded from terrain anyway)
                // But we DO store the profile for future DAE generation
                // The cross-sections remain excluded with their original terrain elevations

                if (!profile.IsValid)
                {
                    result.ValidationMessages.Add($"Spline {spline.SplineId}: {profile.ValidationMessage}");
                }
            }
            catch (Exception ex)
            {
                result.ValidationMessages.Add($"Spline {spline.SplineId}: {ex.Message}");
            }
        }

        return result;
    }
}

/// <summary>
/// Result of structure elevation profile integration.
/// </summary>
public class StructureElevationIntegrationResult
{
    /// <summary>
    /// Number of bridge structures processed.
    /// </summary>
    public int BridgesProcessed { get; set; }

    /// <summary>
    /// Number of tunnel structures processed.
    /// </summary>
    public int TunnelsProcessed { get; set; }

    /// <summary>
    /// Total cross-sections that had their elevation modified.
    /// </summary>
    public int CrossSectionsModified { get; set; }

    /// <summary>
    /// Processing time in milliseconds.
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// Validation messages (warnings/errors) for structures that had issues.
    /// </summary>
    public List<string> ValidationMessages { get; } = new();

    /// <summary>
    /// Total structures processed.
    /// </summary>
    public int TotalStructuresProcessed => BridgesProcessed + TunnelsProcessed;

    /// <summary>
    /// Whether all structures were processed successfully.
    /// </summary>
    public bool AllValid => ValidationMessages.Count == 0;
}
