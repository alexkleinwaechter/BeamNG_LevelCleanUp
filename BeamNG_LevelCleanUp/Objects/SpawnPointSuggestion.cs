namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     Represents a suggested spawn point location derived from terrain generation.
///     Contains position and rotation data suitable for updating a SpawnSphere in MissionGroup.
/// </summary>
public class SpawnPointSuggestion
{
    /// <summary>
    ///     X coordinate in BeamNG world space (PIXELS - not scaled by squareSize/metersPerPixel)
    /// </summary>
    public double X { get; set; }

    /// <summary>
    ///     Y coordinate in BeamNG world space (PIXELS - not scaled by squareSize/metersPerPixel)
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    ///     Z coordinate (height) in BeamNG world space (meters - affected by terrainBaseHeight)
    /// </summary>
    public double Z { get; set; }

    /// <summary>
    ///     Rotation matrix as a flat array of 9 values (3x3 matrix in row-major order).
    ///     Used by BeamNG to orient the spawned vehicle.
    /// </summary>
    public double[] RotationMatrix { get; set; } = new double[9];

    /// <summary>
    ///     Name of the road material this spawn point was derived from (for logging)
    /// </summary>
    public string SourceMaterialName { get; set; } = string.Empty;

    /// <summary>
    ///     Whether this spawn point is on a smoothed road
    /// </summary>
    public bool IsOnRoad { get; set; }

    /// <summary>
    ///     Creates a spawn point at the center of the terrain with default rotation.
    ///     BeamNG terrain is centered at world origin (0,0).
    /// </summary>
    /// <param name="terrainSize">Terrain size in pixels</param>
    /// <param name="metersPerPixel">Scale factor (NOT USED - BeamNG terrain coordinates are always in pixels)</param>
    /// <param name="heightAtCenter">Height at center of terrain</param>
    /// <param name="terrainBaseHeight">Base height offset for terrain</param>
    public static SpawnPointSuggestion CreateAtTerrainCenter(
        int terrainSize,
        float metersPerPixel,
        float heightAtCenter,
        float terrainBaseHeight)
    {
        // CRITICAL: BeamNG terrain is centered at world origin (0,0) in PIXEL coordinates.
        // For a 4096 terrain, it extends from -2048 to +2048 pixels, REGARDLESS of squareSize (metersPerPixel).
        // The squareSize setting only affects how BeamNG displays distances in-game, not the coordinate system.
        // The center is ALWAYS at world position (0, 0), no matter what metersPerPixel is set to.
        return new SpawnPointSuggestion
        {
            X = 0,
            Y = 0,
            Z = heightAtCenter + terrainBaseHeight + 0.5, // Add small offset to avoid clipping
            RotationMatrix = CreateIdentityRotation(),
            IsOnRoad = false,
            SourceMaterialName = "Terrain Center"
        };
    }

    /// <summary>
    ///     Creates a spawn point on a road at the given position and direction.
    /// </summary>
    /// <param name="pixelX">X coordinate in terrain pixels</param>
    /// <param name="pixelY">Y coordinate in terrain pixels</param>
    /// <param name="height">Height at this position (from smoothed heightmap)</param>
    /// <param name="terrainBaseHeight">Base height offset for terrain</param>
    /// <param name="metersPerPixel">Scale factor (NOT USED - BeamNG terrain coordinates are always in pixels)</param>
    /// <param name="tangentX">Road direction tangent X component (normalized)</param>
    /// <param name="tangentY">Road direction tangent Y component (normalized)</param>
    /// <param name="materialName">Name of the road material</param>
    public static SpawnPointSuggestion CreateOnRoad(
        float pixelX,
        float pixelY,
        float height,
        float terrainBaseHeight,
        float metersPerPixel,
        float tangentX,
        float tangentY,
        string materialName)
    {
        // CRITICAL: BeamNG terrain coordinate system is ALWAYS in pixels, regardless of squareSize (metersPerPixel).
        // The squareSize only affects how BeamNG displays the terrain in-game, not the coordinate system.
        // For an 8192 terrain, coordinates range from -4096 to +4096, no matter what squareSize is set to.
        // DO NOT multiply by metersPerPixel here - that would put the spawn point outside terrain bounds!
        var worldX = pixelX;
        var worldY = pixelY;
        var worldZ = height + terrainBaseHeight + 0.5; // Add small offset for vehicle spawn

        // Create rotation matrix from tangent direction
        // The tangent gives us the forward direction of the road
        var rotationMatrix = CreateRotationFromDirection(tangentX, tangentY);

        return new SpawnPointSuggestion
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
    ///     Creates an identity rotation matrix (no rotation).
    /// </summary>
    private static double[] CreateIdentityRotation()
    {
        return new double[]
        {
            1, 0, 0,
            0, 1, 0,
            0, 0, 1
        };
    }

    /// <summary>
    ///     Creates a rotation matrix that orients the vehicle along the road direction.
    ///     In BeamNG, the vehicle's forward direction is typically along the positive X axis.
    /// </summary>
    /// <param name="tangentX">Road direction tangent X component (normalized)</param>
    /// <param name="tangentY">Road direction tangent Y component (normalized)</param>
    private static double[] CreateRotationFromDirection(float tangentX, float tangentY)
    {
        // Normalize the tangent just in case
        var length = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
        if (length < 0.001) return CreateIdentityRotation();

        var forwardX = tangentX / length;
        var forwardY = tangentY / length;

        // The rotation matrix for 2D rotation around Z axis:
        // [ cos(?)  -sin(?)  0 ]
        // [ sin(?)   cos(?)  0 ]
        // [   0        0     1 ]
        //
        // Where cos(?) = forwardX and sin(?) = forwardY (tangent direction)

        return new[]
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