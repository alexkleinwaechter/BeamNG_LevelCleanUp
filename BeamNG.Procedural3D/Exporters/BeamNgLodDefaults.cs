namespace BeamNG.Procedural3D.Exporters;

/// <summary>
///     Default pixel-size thresholds for BeamNG LOD levels.
///     The pixel size determines when the object switches to a given LOD.
///     Higher number = more detail (object appears larger on screen).
///     Single buildings (~15m):
///     - at 500m distance ≈ 40px  → LOD0 (walls+roof only)
///     - at 200m distance ≈ 100px → LOD1 (textured window quads)
///     - at 80m distance  ≈ 250px → LOD2 (full 3D windows+doors)
///     Clusters (~100m cell):
///     - cover ~10× more screen area at same distance, so thresholds scale up
/// </summary>
public class BeamNgLodDefaults
{
    /// <summary>
    ///     Pixel size for LOD0 (lowest detail — walls + roof only).
    /// </summary>
    public int Lod0PixelSize { get; init; }

    /// <summary>
    ///     Pixel size for LOD1 (medium detail — textured window quads).
    /// </summary>
    public int Lod1PixelSize { get; init; }

    /// <summary>
    ///     Pixel size for LOD2 (highest detail — full 3D windows, doors, frames).
    /// </summary>
    public int Lod2PixelSize { get; init; }

    /// <summary>
    ///     Defaults for individual building DAE files.
    /// </summary>
    public static BeamNgLodDefaults SingleBuilding { get; } = new()
    {
        Lod0PixelSize = 100,
        Lod1PixelSize = 200,
        Lod2PixelSize = 400
    };

    /// <summary>
    ///     Defaults for building cluster DAE files (100m grid cells).
    ///     Clusters are physically larger so they fill more screen space at the same distance.
    /// </summary>
    public static BeamNgLodDefaults Cluster { get; } = new()
    {
        Lod0PixelSize = 200,
        Lod1PixelSize = 400,
        Lod2PixelSize = 800
    };

    /// <summary>
    ///     Returns the pixel sizes as an ordered array [LOD0, LOD1, LOD2].
    /// </summary>
    public int[] GetPixelSizes()
    {
        return [Lod0PixelSize, Lod1PixelSize, Lod2PixelSize];
    }

    /// <summary>
    ///     Computes LOD pixel size thresholds scaled to the object's bounding box size.
    ///     Torque3D estimates screen pixel coverage proportionally to bounding box radius,
    ///     so larger objects need proportionally higher LOD thresholds to keep transitions
    ///     at consistent camera distances.
    ///     Formula: pixelSize = baseThreshold × (maxBoundsDimension / referenceSize) × bias
    /// </summary>
    /// <param name="maxBoundsDimension">
    ///     Largest axis-aligned dimension of the object's bounding box in meters
    ///     (max of width, depth, height).
    /// </param>
    /// <param name="bias">
    ///     User-tunable multiplier (default 1.0). Values &gt; 1 increase thresholds
    ///     (detail drops sooner at distance). Values &lt; 1 decrease thresholds
    ///     (more detail retained at distance).
    /// </param>
    public static BeamNgLodDefaults ComputeForBounds(float maxBoundsDimension, float bias = 1.0f)
    {
        const float referenceSize = 15.0f;
        const int baseLod0 = 50;
        const int baseLod1 = 100;
        const int baseLod2 = 200;
        const int minimumPixelSize = 1;

        var scale = (maxBoundsDimension / referenceSize) * bias;

        return new BeamNgLodDefaults
        {
            Lod0PixelSize = Math.Max(minimumPixelSize, (int)MathF.Round(baseLod0 * scale)),
            Lod1PixelSize = Math.Max(minimumPixelSize, (int)MathF.Round(baseLod1 * scale)),
            Lod2PixelSize = Math.Max(minimumPixelSize, (int)MathF.Round(baseLod2 * scale))
        };
    }
}