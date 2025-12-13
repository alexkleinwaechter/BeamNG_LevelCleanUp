using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNG_LevelCleanUp.Logic;

/// <summary>
///     Extracts spawn point suggestions from terrain generation data.
///     Prefers spawn points on roads if road smoothing was applied.
/// </summary>
public static class SpawnPointExtractor
{
    /// <summary>
    ///     Extracts the best spawn point from terrain generation parameters and smoothing results.
    /// </summary>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <param name="modifiedHeightMap">The smoothed heightmap (or null if no smoothing)</param>
    /// <param name="roadMaterials">Materials that had road smoothing applied</param>
    /// <returns>A spawn point suggestion, or null if extraction failed</returns>
    public static SpawnPointSuggestion? ExtractSpawnPoint(
        TerrainCreationParameters parameters,
        float[,]? modifiedHeightMap,
        List<MaterialDefinition>? roadMaterials)
    {
        try
        {
            // First, try to find a spawn point on a road
            if (roadMaterials != null && roadMaterials.Any())
            {
                var roadSpawn = FindSpawnOnRoad(roadMaterials, modifiedHeightMap, parameters);
                if (roadSpawn != null)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Found spawn point on road '{roadSpawn.SourceMaterialName}'");
                    return roadSpawn;
                }
            }

            // Fallback: spawn at terrain center
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No road spawn found, using terrain center");
            return CreateCenterSpawnPoint(parameters, modifiedHeightMap);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error extracting spawn point: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Finds a suitable spawn point on a road from the road materials.
    /// </summary>
    private static SpawnPointSuggestion? FindSpawnOnRoad(
        List<MaterialDefinition> roadMaterials,
        float[,]? heightMap,
        TerrainCreationParameters parameters)
    {
        // Sort materials by priority - prefer wider roads (higher road width = more important road)
        var sortedMaterials = roadMaterials
            .Where(m => m.RoadParameters?.PreBuiltSplines != null && m.RoadParameters.PreBuiltSplines.Any())
            .OrderByDescending(m => m.RoadParameters?.RoadWidthMeters ?? 0)
            .ToList();

        foreach (var material in sortedMaterials)
        {
            var spawn = FindSpawnOnMaterialRoad(material, heightMap, parameters);
            if (spawn != null) return spawn;
        }

        return null;
    }

    /// <summary>
    ///     Finds a spawn point on roads from a specific material.
    ///     Prefers a point near the middle of the longest road.
    /// </summary>
    private static SpawnPointSuggestion? FindSpawnOnMaterialRoad(
        MaterialDefinition material,
        float[,]? heightMap,
        TerrainCreationParameters parameters)
    {
        var splines = material.RoadParameters?.PreBuiltSplines;
        if (splines == null || !splines.Any())
            return null;

        // Find the longest spline
        var longestSpline = splines.OrderByDescending(s => s.TotalLength).FirstOrDefault();
        if (longestSpline == null || longestSpline.TotalLength < 10) // Minimum 10 meters
            return null;

        // Get a point near 1/3 of the way along the road (not at the very start)
        var distance = longestSpline.TotalLength * 0.33f;
        var position = longestSpline.GetPointAtDistance(distance);
        var tangent = longestSpline.GetTangentAtDistance(distance);

        // CRITICAL: Splines are in METER coordinates (scaled by metersPerPixel)
        // We need to convert back to PIXEL coordinates for BeamNG's terrain coordinate system
        // Splines are built with coords from 0 to (terrainSize * metersPerPixel) in meters
        // After dividing, we get 0 to terrainSize in pixels
        // BeamNG expects centered coords from -size/2 to +size/2, so subtract halfSize
        var halfSize = parameters.Size / 2.0f;
        var pixelX = (position.X / parameters.MetersPerPixel) - halfSize;
        var pixelY = (position.Y / parameters.MetersPerPixel) - halfSize;

        // Get height from heightmap
        // Heightmap array uses indices 0 to size-1
        // pixelX/pixelY are now in BeamNG coords (-size/2 to +size/2), add halfSize back for array index
        float height = 0;
        if (heightMap != null)
        {
            var size = heightMap.GetLength(0);
            var arrayHalfSize = size / 2;
            // Convert from BeamNG coords back to array indices
            var ix = (int)Math.Clamp(pixelX + arrayHalfSize, 0, size - 1);
            var iy = (int)Math.Clamp(pixelY + arrayHalfSize, 0, size - 1);
            height = heightMap[iy, ix];
        }

        return SpawnPointSuggestion.CreateOnRoad(
            pixelX,
            pixelY,
            height,
            parameters.TerrainBaseHeight,
            parameters.MetersPerPixel,
            tangent.X,
            tangent.Y,
            material.MaterialName);
    }

    /// <summary>
    ///     Creates a spawn point at the center of the terrain.
    /// </summary>
    private static SpawnPointSuggestion CreateCenterSpawnPoint(
        TerrainCreationParameters parameters,
        float[,]? heightMap)
    {
        var size = parameters.Size;
        var centerPixel = size / 2;

        // Get height at center
        float heightAtCenter = 0;
        if (heightMap != null) heightAtCenter = heightMap[centerPixel, centerPixel];

        return SpawnPointSuggestion.CreateAtTerrainCenter(
            size,
            parameters.MetersPerPixel,
            heightAtCenter,
            parameters.TerrainBaseHeight);
    }

    /// <summary>
    ///     Finds a spawn point from pre-built splines in material definitions.
    ///     Call this from GenerateTerrain after the splines are created but before terrain generation.
    /// </summary>
    /// <param name="materials">All materials (only road materials with PreBuiltSplines will be considered)</param>
    /// <param name="terrainSize">Size of the terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="terrainBaseHeight">Base height offset</param>
    /// <param name="heightmapPath">Path to the heightmap for reading center height (optional)</param>
    /// <returns>Spawn point suggestion or null</returns>
    public static SpawnPointSuggestion? FindSpawnFromSplines(
        List<MaterialDefinition> materials,
        int terrainSize,
        float metersPerPixel,
        float terrainBaseHeight,
        float[,]? heightMap = null)
    {
        var roadMaterials = materials
            .Where(m => m.RoadParameters?.PreBuiltSplines != null && m.RoadParameters.PreBuiltSplines.Any())
            .ToList();

        if (!roadMaterials.Any())
        {
            // No road splines - return center spawn
            float centerHeight = 0;
            if (heightMap != null)
            {
                var centerPixel = terrainSize / 2;
                centerHeight = heightMap[centerPixel, centerPixel];
            }

            return SpawnPointSuggestion.CreateAtTerrainCenter(
                terrainSize,
                metersPerPixel,
                centerHeight,
                terrainBaseHeight);
        }

        // Sort by road width (wider = more important)
        var sortedMaterials = roadMaterials
            .OrderByDescending(m => m.RoadParameters?.RoadWidthMeters ?? 0)
            .ToList();

        foreach (var material in sortedMaterials)
        {
            var splines = material.RoadParameters!.PreBuiltSplines!;

            // Find the longest spline
            var longestSpline = splines.OrderByDescending(s => s.TotalLength).FirstOrDefault();
            if (longestSpline == null || longestSpline.TotalLength < 20) // Minimum 20 meters for a good spawn
                continue;

            // Get a point at 1/3 of the way along the road
            var distance = longestSpline.TotalLength * 0.33f;
            var position = longestSpline.GetPointAtDistance(distance);
            var tangent = longestSpline.GetTangentAtDistance(distance);

            // CRITICAL: Splines are in METER coordinates (scaled by metersPerPixel)
            // We need to convert back to PIXEL coordinates for BeamNG's terrain coordinate system
            // Splines are built with coords from 0 to (terrainSize * metersPerPixel) in meters
            // After dividing, we get 0 to terrainSize in pixels
            // BeamNG expects centered coords from -size/2 to +size/2, so subtract halfSize
            var halfSize = terrainSize / 2.0f;
            var pixelX = (position.X / metersPerPixel) - halfSize;
            var pixelY = (position.Y / metersPerPixel) - halfSize;

            // Get height at this position
            // Heightmap array uses indices 0 to size-1
            // pixelX/pixelY are now in BeamNG coords (-size/2 to +size/2), add halfSize back for array index
            float height = 0;
            if (heightMap != null)
            {
                var size = heightMap.GetLength(0);
                var arrayHalfSize = size / 2;
                // Convert from BeamNG coords back to array indices
                var ix = (int)Math.Clamp(pixelX + arrayHalfSize, 0, size - 1);
                var iy = (int)Math.Clamp(pixelY + arrayHalfSize, 0, size - 1);
                height = heightMap[iy, ix];
            }

            return SpawnPointSuggestion.CreateOnRoad(
                pixelX,
                pixelY,
                height,
                terrainBaseHeight,
                metersPerPixel,
                tangent.X,
                tangent.Y,
                material.MaterialName);
        }

        // Fallback to center
        float fallbackHeight = 0;
        if (heightMap != null)
        {
            var centerPixel = terrainSize / 2;
            fallbackHeight = heightMap[centerPixel, centerPixel];
        }

        return SpawnPointSuggestion.CreateAtTerrainCenter(
            terrainSize,
            metersPerPixel,
            fallbackHeight,
            terrainBaseHeight);
    }
}