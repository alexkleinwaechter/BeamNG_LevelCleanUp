using BeamNgTerrainPoc.Terrain.Logging;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Represents a spawn point location extracted from terrain generation.
///     Contains position and rotation data suitable for placing a spawn point in the level.
/// </summary>
public class SpawnPointData
{
    /// <summary>
    ///     X coordinate in world space (meters)
    /// </summary>
    public double X { get; set; }

    /// <summary>
    ///     Y coordinate in world space (meters)
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    ///     Z coordinate (height) in world space (meters)
    /// </summary>
    public double Z { get; set; }

    /// <summary>
    ///     Rotation matrix as a flat array of 9 values (3x3 matrix in row-major order).
    /// </summary>
    public double[] RotationMatrix { get; set; } = new double[9];

    /// <summary>
    ///     Name of the road material this spawn point was derived from (for logging)
    /// </summary>
    public string SourceMaterialName { get; set; } = string.Empty;

    /// <summary>
    ///     Whether this spawn point is on a road
    /// </summary>
    public bool IsOnRoad { get; set; }

    /// <summary>
    ///     Creates a spawn point at the center of the terrain with default rotation.
    ///     BeamNG terrain is centered at world origin (0,0), so center is at (0,0).
    /// </summary>
    public static SpawnPointData CreateAtTerrainCenter(
        int terrainSize,
        float metersPerPixel,
        float heightAtCenter,
        float terrainBaseHeight)
    {
        // BeamNG terrain is centered at origin (0,0) in METERS
        // For a 4096 terrain with 2m/pixel, it extends from -4096m to +4096m
        // The center is at world position (0, 0) meters
        return new SpawnPointData
        {
            X = 0,
            Y = 0,
            Z = heightAtCenter + terrainBaseHeight + 0.5, // Small offset to avoid clipping
            RotationMatrix = CreateIdentityRotation(),
            IsOnRoad = false,
            SourceMaterialName = "Terrain Center"
        };
    }

    /// <summary>
    ///     Creates a spawn point on a road at the given position and direction.
    ///     Converts pixel coordinates to world coordinates in METERS, accounting for BeamNG's
    ///     terrain being centered at world origin (0,0).
    ///     
    ///     CRITICAL: BeamNG world coordinates are in METERS, not pixels!
    ///     For a terrain of size N pixels with squareSize M, the world extends from
    ///     -(N/2)*M to +(N/2)*M meters.
    ///     Example: 8192 pixels with squareSize=2 ? world from -8192m to +8192m
    /// </summary>
    /// <param name="pixelX">X coordinate in terrain pixels (0 to terrainSize-1)</param>
    /// <param name="pixelY">Y coordinate in terrain pixels (0 to terrainSize-1)</param>
    /// <param name="height">Height at this position from heightmap</param>
    /// <param name="terrainBaseHeight">Base height offset for terrain</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel) - USED to convert pixels to meters</param>
    /// <param name="terrainSize">Size of terrain in pixels</param>
    /// <param name="tangentX">Road direction tangent X</param>
    /// <param name="tangentY">Road direction tangent Y</param>
    /// <param name="materialName">Name of the road material</param>
    public static SpawnPointData CreateOnRoad(
        float pixelX,
        float pixelY,
        float height,
        float terrainBaseHeight,
        float metersPerPixel,
        int terrainSize,
        float tangentX,
        float tangentY,
        string materialName)
    {
        // CRITICAL: BeamNG world coordinates are in METERS, centered at origin (0,0)
        // 
        // Coordinate conversion:
        // 1. Pixel (0,0) is at world (-halfSizeMeters, -halfSizeMeters)
        // 2. Pixel (terrainSize/2, terrainSize/2) is at world (0, 0)
        // 3. Pixel (terrainSize-1, terrainSize-1) is at world (+halfSizeMeters - metersPerPixel, +halfSizeMeters - metersPerPixel)
        //
        // Formula: worldPos = (pixelPos - terrainSize/2) * metersPerPixel
        //
        // Example with 8192 terrain, 2 m/px:
        //   Pixel 0 ? (0 - 4096) * 2 = -8192m
        //   Pixel 4096 ? (4096 - 4096) * 2 = 0m (center)
        //   Pixel 8191 ? (8191 - 4096) * 2 = +8190m
        
        var halfSizePixels = terrainSize / 2.0f;
        var worldX = (pixelX - halfSizePixels) * metersPerPixel;  // Convert pixels to centered meters
        var worldY = (pixelY - halfSizePixels) * metersPerPixel;
        var worldZ = height + terrainBaseHeight + 0.5;

        var rotationMatrix = CreateRotationFromDirection(tangentX, tangentY);

        return new SpawnPointData
        {
            X = worldX,
            Y = worldY,
            Z = worldZ,
            RotationMatrix = rotationMatrix,
            IsOnRoad = true,
            SourceMaterialName = materialName
        };
    }

    /// <summary>
    ///     Extracts the best spawn point from road materials and heightmap.
    ///     Prefers spawn points on roads (wider roads first), falls back to terrain center.
    /// </summary>
    public static SpawnPointData? ExtractFromRoads(
        List<MaterialDefinition> materials,
        float[,] heightMap,
        int terrainSize,
        float metersPerPixel,
        float terrainBaseHeight)
    {
        // Get road materials with pre-built splines
        var roadMaterials = materials
            .Where(m => m.RoadParameters?.PreBuiltSplines != null && m.RoadParameters.PreBuiltSplines.Any())
            .OrderByDescending(m => m.RoadParameters?.RoadWidthMeters ?? 0) // Prefer wider roads
            .ToList();

        // Try to find spawn on a road
        foreach (var material in roadMaterials)
        {
            var splines = material.RoadParameters!.PreBuiltSplines!;

            // Find the longest spline
            var longestSpline = splines.OrderByDescending(s => s.TotalLength).FirstOrDefault();
            if (longestSpline == null || longestSpline.TotalLength < 20) // Minimum 20m for good spawn
                continue;

            // Get a point at 5% of the way along the road (near the beginning)
            // This places the spawn near the start of the road, giving the player
            // a clear view of where to drive
            var distance = longestSpline.TotalLength * 0.05f;
            var position = longestSpline.GetPointAtDistance(distance);
            var tangent = longestSpline.GetTangentAtDistance(distance);

            // CRITICAL: Spline coordinates are in METERS (0 to terrainSize * metersPerPixel)
            // with origin at BOTTOM-LEFT (Y increases upward).
            // Convert to PIXELS for heightmap lookup
            var pixelX = position.X / metersPerPixel;
            var pixelY = position.Y / metersPerPixel;

            // DEBUG: Log the coordinate transformation chain
            TerrainLogger.Info($"[SpawnPoint] Spline position (meters): ({position.X:F1}, {position.Y:F1})");
            TerrainLogger.Info($"[SpawnPoint] Converted to pixels: ({pixelX:F1}, {pixelY:F1})");
            TerrainLogger.Info($"[SpawnPoint] TerrainSize={terrainSize}, MetersPerPixel={metersPerPixel}");

            // Get height at this position from the heightmap
            // The heightmap array is in BOTTOM-UP format (same as spline coordinates):
            // - Row 0 = south edge (bottom of terrain)
            // - Row size-1 = north edge (top of terrain)
            // So NO Y flip is needed - both coordinate systems match!
            var size = heightMap.GetLength(0);
            var ix = (int)Math.Clamp(pixelX, 0, size - 1);
            var iy = (int)Math.Clamp(pixelY, 0, size - 1);
            var height = heightMap[iy, ix];

            TerrainLogger.Info($"[SpawnPoint] Heightmap lookup: [{iy}, {ix}] = {height:F1}m");

            // Calculate world coordinates for logging (in METERS)
            var halfSizePixels = terrainSize / 2.0f;
            var worldX = (pixelX - halfSizePixels) * metersPerPixel;
            var worldY = (pixelY - halfSizePixels) * metersPerPixel;
            var worldZ = height + terrainBaseHeight + 0.5;
            
            TerrainLogger.Info($"[SpawnPoint] World coords (meters): ({worldX:F1}, {worldY:F1}, {worldZ:F1})");
            TerrainLogger.Info($"[SpawnPoint] Calculation: pixelX={pixelX:F1} - halfSize={halfSizePixels:F0} = {pixelX - halfSizePixels:F1} * mpp={metersPerPixel} = {worldX:F1}");
            TerrainLogger.Info($"[SpawnPoint] TerrainBaseHeight={terrainBaseHeight:F1}");

            // Pass PIXEL coordinates to CreateOnRoad (it handles the conversion to world coords)
            return CreateOnRoad(
                pixelX,
                pixelY,
                height,
                terrainBaseHeight,
                metersPerPixel,
                terrainSize,
                tangent.X,
                tangent.Y,
                material.MaterialName);
        }

        // Fallback to terrain center
        var centerPixel = terrainSize / 2;
        var centerHeight = heightMap[centerPixel, centerPixel];

        return CreateAtTerrainCenter(
            terrainSize,
            metersPerPixel,
            centerHeight,
            terrainBaseHeight);
    }

    private static double[] CreateIdentityRotation()
    {
        return new double[]
        {
            1, 0, 0,
            0, 1, 0,
            0, 0, 1
        };
    }

    private static double[] CreateRotationFromDirection(float tangentX, float tangentY)
    {
        var length = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
        if (length < 0.001)
            return CreateIdentityRotation();

        var forwardX = tangentX / length;
        var forwardY = tangentY / length;

        return new double[]
        {
            forwardX, -forwardY, 0,
            forwardY, forwardX, 0,
            0, 0, 1
        };
    }

    /// <summary>
    ///     Converts position to an array for JSON serialization.
    /// </summary>
    public double[] ToPositionArray()
    {
        return new[] { X, Y, Z };
    }
}
