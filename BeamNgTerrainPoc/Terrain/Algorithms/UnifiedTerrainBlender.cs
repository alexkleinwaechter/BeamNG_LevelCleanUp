using System.Diagnostics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Single-pass terrain blender for the unified road network.
///
/// This is the main orchestrator that coordinates the blending pipeline:
/// 1. Build combined road mask with elevation from ALL splines
/// 2. Compute EDT with nearest-source tracking
/// 3. Single-pass blend using BeamNG-style exponential falloff
///
/// Implementation details are delegated to focused component classes in the
/// BeamNgTerrainPoc.Terrain.Algorithms.Blending namespace:
/// - DistanceFieldCalculator: Felzenszwalb &amp; Huttenlocher EDT + JFA source tracking
/// - RoadMaskBuilder: Combined road mask with banking-aware elevation
/// - SinglePassBlender: BeamNG-style nearest-source blending
/// - PostProcessingSmoother: Gaussian, Box, and Bilateral smoothing
/// </summary>
public class UnifiedTerrainBlender
{
    private readonly RoadMaskBuilder _maskBuilder;
    private readonly SinglePassBlender _singlePassBlender;
    private readonly PostProcessingSmoother _postProcessingSmoother;

    /// <summary>
    /// Distance field from the last blend operation (for post-processing).
    /// </summary>
    private float[,]? _lastDistanceField;

    public UnifiedTerrainBlender()
    {
        _maskBuilder = new RoadMaskBuilder();
        _singlePassBlender = new SinglePassBlender();
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
    /// Blends the unified road network with the terrain using a single-pass approach.
    ///
    /// Algorithm:
    /// 1. Build COMBINED road mask with elevation from ALL splines (filled polygons)
    /// 2. Compute EDT with nearest-source tracking (distance + which road pixel is nearest)
    /// 3. Single-pass blend: road pixels pinned, blend zone uses nearest-source elevation
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

        // Step 1: Build combined road mask with elevations (ALL roads, single pass)
        TerrainCreationLogger.Current?.InfoFileOnly("Step 1: Building combined road mask with elevation...");
        var sw = Stopwatch.StartNew();
        var maskResult = _maskBuilder.BuildCombinedMaskWithElevation(network, width, height, metersPerPixel);
        perfLog?.Timing($"  BuildCombinedMaskWithElevation: {sw.ElapsedMilliseconds}ms");

        // Step 2: Compute EDT with nearest-source tracking
        TerrainCreationLogger.Current?.InfoFileOnly("Step 2: Computing EDT with nearest-source tracking...");
        sw.Restart();
        var edtResult = DistanceFieldCalculator.ComputeDistanceFieldWithSources(maskResult.Mask, metersPerPixel);
        _lastDistanceField = edtResult.Distances;
        perfLog?.Timing($"  ComputeDistanceFieldWithSources: {sw.ElapsedMilliseconds}ms");

        // Step 3: Single-pass blend
        TerrainCreationLogger.Current?.InfoFileOnly("Step 3: Applying single-pass blend...");
        sw.Restart();
        var blendResult = _singlePassBlender.Blend(
            originalHeightMap, maskResult.Mask, maskResult.ElevationMap,
            maskResult.SplineOwnerMap, edtResult, network, metersPerPixel);
        perfLog?.Timing($"  SinglePassBlend: {sw.ElapsedMilliseconds}ms");

        totalSw.Stop();
        perfLog?.Timing($"UnifiedTerrainBlender TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s");
        TerrainLogger.Info("=== UNIFIED TERRAIN BLENDING COMPLETE ===");

        return blendResult.HeightMap;
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

    /// <summary>
    /// Clears cached data (distance field) to release memory after terrain generation is complete.
    /// Call this after all post-processing is done and the distance field is no longer needed.
    /// </summary>
    public void ClearCachedData()
    {
        _lastDistanceField = null;
    }
}
