using System.Diagnostics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Single-pass terrain blender for the unified road network.
/// 
/// This is the main orchestrator that coordinates the blending pipeline:
/// 1. Build road core mask from ALL splines (for EDT)
/// 2. Build road core protection mask with ownership tracking
/// 3. Compute single global EDT from combined mask
/// 4. Build elevation map with per-pixel source spline tracking
/// 5. Apply protected blending (road core pixels are never modified by blend zones)
/// 
/// Key improvements over per-material blending:
/// - Single EDT computation for the entire road network (faster)
/// - Road core pixels are PROTECTED - never modified by neighbor's blend zone
/// - Per-spline blend ranges are respected
/// - Overlapping blend zones use smooth interpolation between both splines
/// 
/// This eliminates the problem where sequential material processing would
/// overwrite previously smoothed road surfaces.
/// 
/// Implementation details are delegated to focused component classes in the
/// BeamNgTerrainPoc.Terrain.Algorithms.Blending namespace:
/// - DistanceFieldCalculator: Felzenszwalb &amp; Huttenlocher EDT algorithm
/// - RoadMaskBuilder: Road core mask and protection mask with ownership
/// - ElevationMapBuilder: Elevation map with per-pixel ownership tracking
/// - ProtectedBlendingProcessor: Protected blending with priority rules
/// - PostProcessingSmoother: Gaussian, Box, and Bilateral smoothing
/// </summary>
public class UnifiedTerrainBlender
{
    private readonly RoadMaskBuilder _maskBuilder;
    private readonly ElevationMapBuilder _elevationMapBuilder;
    private readonly ProtectedBlendingProcessor _blendingProcessor;
    private readonly PostProcessingSmoother _postProcessingSmoother;

    /// <summary>
    /// Distance field from the last blend operation (for post-processing).
    /// </summary>
    private float[,]? _lastDistanceField;

    public UnifiedTerrainBlender()
    {
        _maskBuilder = new RoadMaskBuilder();
        _elevationMapBuilder = new ElevationMapBuilder();
        _blendingProcessor = new ProtectedBlendingProcessor();
        _postProcessingSmoother = new PostProcessingSmoother();
    }

    /// <summary>
    /// Gets the last computed distance field for reuse in post-processing.
    /// </summary>
    /// <exception cref="InvalidOperationException">If no distance field has been computed yet.</exception>
    public float[,] GetLastDistanceField()
    {
        if (_lastDistanceField == null)
            throw new InvalidOperationException(
                "No distance field has been computed yet. Call BlendNetworkWithTerrain first.");
        return _lastDistanceField;
    }

    /// <summary>
    /// Blends the unified road network with the terrain using a single-pass protected algorithm.
    /// 
    /// Algorithm:
    /// 1. Build COMBINED road core mask from ALL splines (for EDT)
    /// 2. Build road core PROTECTION mask with ownership (filled polygons tracking which spline owns each pixel)
    /// 3. Compute SINGLE global EDT from combined mask
    /// 4. Build elevation map with per-pixel source spline tracking (respecting protection mask ownership)
    /// 5. Apply protected blending (ANY road core pixel is NEVER modified by ANY blend zone)
    /// </summary>
    /// <param name="originalHeightMap">The original terrain heightmap.</param>
    /// <param name="network">The unified road network with harmonized elevations.</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <returns>The blended heightmap.</returns>
    public float[,] BlendNetworkWithTerrain(
        float[,] originalHeightMap,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        if (network.CrossSections.Count == 0)
        {
            TerrainLogger.Info("UnifiedTerrainBlender: No cross-sections to blend");
            return (float[,])originalHeightMap.Clone();
        }

        var height = originalHeightMap.GetLength(0);
        var width = originalHeightMap.GetLength(1);

        TerrainLogger.Info("=== UNIFIED TERRAIN BLENDING ===");
        TerrainLogger.Info($"  Network: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");
        TerrainLogger.Info($"  Terrain: {width}x{height} pixels, {metersPerPixel}m/pixel");

        // Step 1: Build COMBINED road core mask from ALL splines (for EDT)
        TerrainCreationLogger.Current?.InfoFileOnly("Step 1: Building combined road core mask...");
        var sw = Stopwatch.StartNew();
        var combinedCoreMask = _maskBuilder.BuildCombinedRoadCoreMask(network, width, height, metersPerPixel);
        perfLog?.Timing($"  BuildCombinedRoadCoreMask: {sw.ElapsedMilliseconds}ms");

        // Step 2: Build road core PROTECTION mask with ownership (tracks which spline owns each road core pixel)
        TerrainCreationLogger.Current?.InfoFileOnly("Step 2: Building road core protection mask with ownership...");
        sw.Restart();
        var protectionResult = _maskBuilder.BuildRoadCoreProtectionMaskWithOwnership(
            network, width, height, metersPerPixel);
        perfLog?.Timing($"  BuildRoadCoreProtectionMaskWithOwnership: {sw.ElapsedMilliseconds}ms");

        // Step 3: Compute SINGLE global EDT from combined mask
        TerrainCreationLogger.Current?.InfoFileOnly("Step 3: Computing global distance field (EDT)...");
        sw.Restart();
        var distanceField = DistanceFieldCalculator.ComputeDistanceField(combinedCoreMask, metersPerPixel);
        _lastDistanceField = distanceField;
        perfLog?.Timing($"  ComputeDistanceField: {sw.ElapsedMilliseconds}ms");

        // Step 4: Build elevation map with per-pixel source spline tracking (respecting core ownership)
        TerrainCreationLogger.Current?.InfoFileOnly("Step 4: Building elevation map with ownership...");
        sw.Restart();
        var elevationResult = _elevationMapBuilder.BuildElevationMapWithOwnership(
            network, width, height, metersPerPixel,
            protectionResult.ProtectionMask,
            protectionResult.OwnershipMap,
            protectionResult.ElevationMap);
        perfLog?.Timing($"  BuildElevationMapWithOwnership: {sw.ElapsedMilliseconds}ms");

        // Step 5: Apply protected blending
        TerrainCreationLogger.Current?.InfoFileOnly("Step 5: Applying protected blending...");
        sw.Restart();
        var (result, _) = _blendingProcessor.ApplyProtectedBlending(
            originalHeightMap,
            distanceField,
            elevationResult.Elevations,
            elevationResult.Owners,
            elevationResult.MaxBlendRanges,
            protectionResult.ProtectionMask,
            network,
            metersPerPixel);
        perfLog?.Timing($"  ApplyProtectedBlending: {sw.ElapsedMilliseconds}ms");

        totalSw.Stop();
        perfLog?.Timing($"UnifiedTerrainBlender TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s");
        TerrainLogger.Info("=== UNIFIED TERRAIN BLENDING COMPLETE ===");

        return result;
    }

    /// <summary>
    /// Applies post-processing smoothing to eliminate staircase artifacts on the road surface.
    /// Uses a masked smoothing approach - only smooths within the road and shoulder areas.
    /// </summary>
    public void ApplyPostProcessingSmoothing(
        float[,] heightMap,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        if (_lastDistanceField == null)
        {
            TerrainLogger.Warning("Cannot apply post-processing: no distance field available");
            return;
        }

        _postProcessingSmoother.ApplyPostProcessingSmoothing(
            heightMap, _lastDistanceField, network, metersPerPixel);
    }
}