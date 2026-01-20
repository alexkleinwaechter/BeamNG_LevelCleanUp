namespace BeamNG.Procedural3D.Builders;

using System.Numerics;

/// <summary>
/// Provides UV coordinate generation utilities for common mapping scenarios.
/// </summary>
public static class UVMapper
{
    /// <summary>
    /// Planar projection onto the XZ plane (horizontal surfaces like roads).
    /// </summary>
    /// <param name="position">World position.</param>
    /// <param name="scaleU">Meters per texture repeat in U direction.</param>
    /// <param name="scaleV">Meters per texture repeat in V direction.</param>
    public static Vector2 PlanarXZ(Vector3 position, float scaleU = 1f, float scaleV = 1f)
    {
        return new Vector2(position.X / scaleU, position.Z / scaleV);
    }

    /// <summary>
    /// Planar projection onto the XY plane (vertical surfaces facing Z).
    /// </summary>
    /// <param name="position">World position.</param>
    /// <param name="scaleU">Meters per texture repeat in U direction.</param>
    /// <param name="scaleV">Meters per texture repeat in V direction.</param>
    public static Vector2 PlanarXY(Vector3 position, float scaleU = 1f, float scaleV = 1f)
    {
        return new Vector2(position.X / scaleU, position.Y / scaleV);
    }

    /// <summary>
    /// Planar projection onto the YZ plane (vertical surfaces facing X).
    /// </summary>
    /// <param name="position">World position.</param>
    /// <param name="scaleU">Meters per texture repeat in U direction.</param>
    /// <param name="scaleV">Meters per texture repeat in V direction.</param>
    public static Vector2 PlanarYZ(Vector3 position, float scaleU = 1f, float scaleV = 1f)
    {
        return new Vector2(position.Y / scaleU, position.Z / scaleV);
    }

    /// <summary>
    /// Box mapping that selects the best planar projection based on surface normal.
    /// </summary>
    /// <param name="position">World position.</param>
    /// <param name="normal">Surface normal.</param>
    /// <param name="scale">Meters per texture repeat.</param>
    public static Vector2 Box(Vector3 position, Vector3 normal, float scale = 1f)
    {
        float absX = MathF.Abs(normal.X);
        float absY = MathF.Abs(normal.Y);
        float absZ = MathF.Abs(normal.Z);

        if (absY >= absX && absY >= absZ)
        {
            // Top/bottom face - use XZ
            return PlanarXZ(position, scale, scale);
        }
        else if (absX >= absZ)
        {
            // Left/right face - use YZ
            return PlanarYZ(position, scale, scale);
        }
        else
        {
            // Front/back face - use XY
            return PlanarXY(position, scale, scale);
        }
    }

    /// <summary>
    /// Cylindrical mapping around the Y axis.
    /// </summary>
    /// <param name="position">World position.</param>
    /// <param name="center">Center of the cylinder.</param>
    /// <param name="scaleU">Meters per texture repeat around circumference.</param>
    /// <param name="scaleV">Meters per texture repeat along height.</param>
    public static Vector2 Cylindrical(Vector3 position, Vector3 center, float scaleU = 1f, float scaleV = 1f)
    {
        float dx = position.X - center.X;
        float dz = position.Z - center.Z;
        float angle = MathF.Atan2(dz, dx);

        // Convert angle to [0, 2?] range
        if (angle < 0) angle += MathF.PI * 2f;

        float circumference = MathF.Sqrt(dx * dx + dz * dz) * MathF.PI * 2f;
        float u = (angle / (MathF.PI * 2f)) * circumference / scaleU;
        float v = (position.Y - center.Y) / scaleV;

        return new Vector2(u, v);
    }

    /// <summary>
    /// Road surface UV mapping: U = distance along road, V = position across road.
    /// </summary>
    /// <param name="distanceAlongRoad">Distance along the road centerline in meters.</param>
    /// <param name="positionAcrossRoad">Position across the road from center (-width/2 to +width/2).</param>
    /// <param name="textureRepeatU">Meters per texture repeat along road.</param>
    /// <param name="textureRepeatV">Meters per texture repeat across road.</param>
    public static Vector2 RoadSurface(float distanceAlongRoad, float positionAcrossRoad, float textureRepeatU = 4f, float textureRepeatV = 4f)
    {
        return new Vector2(
            distanceAlongRoad / textureRepeatU,
            (positionAcrossRoad + textureRepeatV / 2f) / textureRepeatV);
    }

    /// <summary>
    /// Road surface UV mapping with centered V coordinates (0 at center, 0.5 at edges for symmetric textures).
    /// </summary>
    /// <param name="distanceAlongRoad">Distance along the road centerline in meters.</param>
    /// <param name="positionAcrossRoad">Position across the road from center (-width/2 to +width/2).</param>
    /// <param name="roadWidth">Total road width in meters.</param>
    /// <param name="textureRepeatU">Meters per texture repeat along road.</param>
    public static Vector2 RoadSurfaceCentered(float distanceAlongRoad, float positionAcrossRoad, float roadWidth, float textureRepeatU = 4f)
    {
        return new Vector2(
            distanceAlongRoad / textureRepeatU,
            0.5f + positionAcrossRoad / roadWidth);
    }

    /// <summary>
    /// Normalizes UV coordinates to a specific range, useful for tiling control.
    /// </summary>
    /// <param name="uv">Original UV coordinates.</param>
    /// <param name="minU">Minimum U value.</param>
    /// <param name="maxU">Maximum U value.</param>
    /// <param name="minV">Minimum V value.</param>
    /// <param name="maxV">Maximum V value.</param>
    public static Vector2 NormalizeToRange(Vector2 uv, float minU, float maxU, float minV, float maxV)
    {
        return new Vector2(
            minU + (uv.X % 1f) * (maxU - minU),
            minV + (uv.Y % 1f) * (maxV - minV));
    }

    /// <summary>
    /// Creates UV coordinates for a rectangular region with specified corner positions.
    /// Returns a function that maps normalized coordinates (0-1, 0-1) to the specified UV region.
    /// </summary>
    public static Func<float, float, Vector2> CreateRectangularRegion(Vector2 uvMin, Vector2 uvMax)
    {
        return (u, v) => new Vector2(
            uvMin.X + u * (uvMax.X - uvMin.X),
            uvMin.Y + v * (uvMax.Y - uvMin.Y));
    }
}
