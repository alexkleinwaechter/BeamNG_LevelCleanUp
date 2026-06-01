using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase B.4 — samples the natural terrain gradient at a 2D position and
///     projects it onto a tangent direction. Used by
///     <c>ComputeEndpointConstraints</c> to set dead-end anchor slope from the
///     actual terrain, eliminating the flat-platform artefact. Pure helper:
///     reads from the heightmap, never mutates anything.
/// </summary>
public static class HeightmapSlopeSampler
{
    /// <summary>
    ///     Returns dz/ds along <paramref name="tangent" /> at <paramref name="position" />,
    ///     computed by central difference on the heightmap. Positive = ascending in the
    ///     tangent direction. Sample points beyond the heightmap edges are clamped.
    /// </summary>
    /// <param name="heightMap">[y, x] indexed elevation grid.</param>
    /// <param name="metersPerPixel">Heightmap pixel size.</param>
    /// <param name="position">World position (X, Y) in metres.</param>
    /// <param name="tangent">Direction along which to project the gradient. Need not be normalised; will be normalised internally.</param>
    /// <param name="sampleDistanceMeters">Half-distance of the central difference. Default 2m.</param>
    public static float SampleAlongTangent(
        float[,] heightMap, float metersPerPixel,
        Vector2 position, Vector2 tangent,
        float sampleDistanceMeters = 2.0f)
    {
        if (tangent.LengthSquared() < 0.0001f) return 0f;
        var dir = Vector2.Normalize(tangent);

        var ahead = position + dir * sampleDistanceMeters;
        var behind = position - dir * sampleDistanceMeters;

        var zAhead = SampleHeight(heightMap, metersPerPixel, ahead);
        var zBehind = SampleHeight(heightMap, metersPerPixel, behind);

        return (zAhead - zBehind) / (2f * sampleDistanceMeters);
    }

    private static float SampleHeight(float[,] heightMap, float metersPerPixel, Vector2 worldPos)
    {
        var maxX = heightMap.GetLength(1) - 1;
        var maxY = heightMap.GetLength(0) - 1;

        var fpx = worldPos.X / metersPerPixel;
        var fpy = worldPos.Y / metersPerPixel;

        var x0 = (int)MathF.Floor(fpx);
        var y0 = (int)MathF.Floor(fpy);
        var x1 = x0 + 1;
        var y1 = y0 + 1;

        x0 = Math.Clamp(x0, 0, maxX);
        y0 = Math.Clamp(y0, 0, maxY);
        x1 = Math.Clamp(x1, 0, maxX);
        y1 = Math.Clamp(y1, 0, maxY);

        var tx = fpx - MathF.Floor(fpx);
        var ty = fpy - MathF.Floor(fpy);
        tx = Math.Clamp(tx, 0f, 1f);
        ty = Math.Clamp(ty, 0f, 1f);

        var h00 = heightMap[y0, x0];
        var h10 = heightMap[y0, x1];
        var h01 = heightMap[y1, x0];
        var h11 = heightMap[y1, x1];

        return h00 * (1f - tx) * (1f - ty)
             + h10 * tx         * (1f - ty)
             + h01 * (1f - tx) * ty
             + h11 * tx         * ty;
    }
}
