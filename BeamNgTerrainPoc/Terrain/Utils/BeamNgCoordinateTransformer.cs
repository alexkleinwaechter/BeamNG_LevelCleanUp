using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Utils;

/// <summary>
/// Centralized utility for transforming coordinates between terrain/pixel space and BeamNG world coordinates.
/// 
/// BeamNG coordinate system:
/// - World origin (0, 0) is at the CENTER of the terrain
/// - Terrain extends from -halfSizeMeters to +halfSizeMeters in both X and Y
/// - Z is height (elevation)
/// 
/// Terrain/Pixel space (used by heightmaps and splines in this application):
/// - Origin (0, 0) is at BOTTOM-LEFT corner
/// - X increases to the right
/// - Y increases upward
/// - Coordinates are in METERS (after multiplying pixels by metersPerPixel)
/// </summary>
public static class BeamNgCoordinateTransformer
{
    /// <summary>
    /// Converts a position from terrain meter coordinates (origin at bottom-left) to BeamNG world coordinates (origin at center).
    /// </summary>
    /// <param name="terrainX">X coordinate in terrain space (meters from left edge)</param>
    /// <param name="terrainY">Y coordinate in terrain space (meters from bottom edge)</param>
    /// <param name="terrainZ">Z coordinate (height/elevation in meters)</param>
    /// <param name="terrainSizePixels">Terrain size in pixels</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel, aka squareSize)</param>
    /// <returns>Position in BeamNG world coordinates (centered origin)</returns>
    public static Vector3 TerrainToWorld(
        float terrainX,
        float terrainY,
        float terrainZ,
        int terrainSizePixels,
        float metersPerPixel)
    {
        // Calculate half-size in meters
        // Total world size = terrainSizePixels * metersPerPixel
        // Half size = (terrainSizePixels / 2) * metersPerPixel
        var halfSizeMeters = (terrainSizePixels / 2.0f) * metersPerPixel;
        
        // Convert from terrain coordinates (0 to terrainSizePixels * metersPerPixel) 
        // to world coordinates (-halfSizeMeters to +halfSizeMeters)
        var worldX = terrainX - halfSizeMeters;
        var worldY = terrainY - halfSizeMeters;
        var worldZ = terrainZ;
        
        return new Vector3(worldX, worldY, worldZ);
    }
    
    /// <summary>
    /// Converts a 2D position from terrain meter coordinates to BeamNG world coordinates.
    /// </summary>
    public static Vector2 TerrainToWorld2D(
        float terrainX,
        float terrainY,
        int terrainSizePixels,
        float metersPerPixel)
    {
        var halfSizeMeters = (terrainSizePixels / 2.0f) * metersPerPixel;
        
        var worldX = terrainX - halfSizeMeters;
        var worldY = terrainY - halfSizeMeters;
        
        return new Vector2(worldX, worldY);
    }
    
    /// <summary>
    /// Converts a Vector2 position from terrain meter coordinates to BeamNG world coordinates.
    /// </summary>
    public static Vector2 TerrainToWorld2D(
        Vector2 terrainPos,
        int terrainSizePixels,
        float metersPerPixel)
    {
        return TerrainToWorld2D(terrainPos.X, terrainPos.Y, terrainSizePixels, metersPerPixel);
    }
    
    /// <summary>
    /// Converts a position from BeamNG world coordinates to terrain meter coordinates.
    /// </summary>
    public static Vector3 WorldToTerrain(
        float worldX,
        float worldY,
        float worldZ,
        int terrainSizePixels,
        float metersPerPixel)
    {
        var halfSizeMeters = (terrainSizePixels / 2.0f) * metersPerPixel;
        
        var terrainX = worldX + halfSizeMeters;
        var terrainY = worldY + halfSizeMeters;
        var terrainZ = worldZ;
        
        return new Vector3(terrainX, terrainY, terrainZ);
    }
    
    /// <summary>
    /// Converts pixel coordinates to terrain meter coordinates.
    /// </summary>
    public static Vector2 PixelsToMeters(float pixelX, float pixelY, float metersPerPixel)
    {
        return new Vector2(pixelX * metersPerPixel, pixelY * metersPerPixel);
    }
    
    /// <summary>
    /// Converts pixel coordinates directly to BeamNG world coordinates.
    /// Convenience method combining PixelsToMeters and TerrainToWorld.
    /// </summary>
    public static Vector3 PixelsToWorld(
        float pixelX,
        float pixelY,
        float height,
        int terrainSizePixels,
        float metersPerPixel)
    {
        var terrainX = pixelX * metersPerPixel;
        var terrainY = pixelY * metersPerPixel;
        
        return TerrainToWorld(terrainX, terrainY, height, terrainSizePixels, metersPerPixel);
    }
    
    /// <summary>
    /// Calculates the terrain world bounds (min/max corners) in BeamNG world coordinates.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) GetWorldBounds(int terrainSizePixels, float metersPerPixel)
    {
        var halfSizeMeters = (terrainSizePixels / 2.0f) * metersPerPixel;
        
        return (
            new Vector2(-halfSizeMeters, -halfSizeMeters),
            new Vector2(halfSizeMeters, halfSizeMeters)
        );
    }
}
