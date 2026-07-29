using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Explicit input contract for backdrop generation (spec §4/D7). The core consumes ONLY this —
///     no TerrainGenerationState, no MT_settings. A record class so tests can use `with`.
/// </summary>
public sealed record BackdropGenerationParameters
{
    // ---- Final terrain output (post quantization/erosion/smoothing) ----
    /// <summary>[y,x] row-major, y=0 = SOUTH edge, heights pre-base-height in meters.</summary>
    public required float[,] TerrainHeightMap { get; init; }
    public required int TerrainSizePixels { get; init; }
    public required float TerrainMetersPerPixel { get; init; }
    public required float TerrainBaseHeight { get; init; }
    /// <summary>Min elevation used for the terrain's own normalization (spec §7.3 vertical datum).</summary>
    public required double TerrainCropMinElevation { get; init; }

    // ---- Source raster space (combined GeoTIFF mosaic) ----
    /// <summary>Single GeoTIFF (or cached combined mosaic) covering the FULL uncropped source raster.</summary>
    public required string SourceGeoTiffPath { get; init; }
    public int? EpsgOverride { get; init; }
    public required int SourceRasterWidth { get; init; }
    public required int SourceRasterHeight { get; init; }
    /// <summary>6-parameter affine geotransform of the UNCROPPED mosaic (GDAL convention).</summary>
    public required double[] SourceGeoTransform { get; init; }
    public string? ProjectionWkt { get; init; }
    /// <summary>WGS84 bounds of the full mosaic — linear fallback for chunk bboxes when WKT is unusable.</summary>
    public GeoTiff.GeoBoundingBox? SourceWgs84Bounds { get; init; }

    /// <summary>Terrain crop rect in source pixels (the terrain selection).</summary>
    public required PixelRect TerrainRect { get; init; }
    /// <summary>Backdrop rect in source pixels; must contain <see cref="TerrainRect"/>.</summary>
    public required PixelRect BackdropRect { get; init; }

    // ---- Output ----
    public required string LevelPath { get; init; }
    public required string LevelName { get; init; }

    // ---- Tunables (defaults = spec §15) ----
    public double EdgeBandMeters { get; init; } = 200;
    public double MaxVerticalErrorNearMeters { get; init; } = 0.5;
    public double MaxVerticalErrorFarMeters { get; init; } = 8.0;
    public double ChunkTargetMeters { get; init; } = 2000;
    public double TexelDensityNearMPerPx { get; init; } = 1.0;
    public int MaxChunkTextureSize { get; init; } = 2048;
    public int MaxFarRasterDimension { get; init; } = 8192;
    public bool SeamSkirt { get; init; } = true;
    public double SeamSkirtDepthMeters { get; init; } = 2.0;
    /// <summary>Whether the backdrop is drivable. Purely scene-level: the TSStatic entries get
    /// <c>collisionType</c>/<c>decalType</c> "Visible Mesh Final" (game builds physics from the
    /// visual mesh — the DAE never embeds a Colmesh) vs "None". Off skips the game's load-time
    /// physics build; DAE size is unaffected either way.</summary>
    public bool CollisionMesh { get; init; } = true;

    /// <summary>V2 hook (spec §12) — unused in V1, reserved so the signature never changes.</summary>
    public UnifiedRoadNetwork? RoadNetwork { get; init; }

    /// <summary>Meters covered by one source pixel in X (derived from the terrain mapping, spec §7.4).</summary>
    public double MetersPerSourcePixelX => TerrainSizePixels * (double)TerrainMetersPerPixel / TerrainRect.Width;
    public double MetersPerSourcePixelY => TerrainSizePixels * (double)TerrainMetersPerPixel / TerrainRect.Height;

    public BackdropValidationResult Validate()
    {
        var result = new BackdropValidationResult();

        if (TerrainSizePixels <= 0 || TerrainMetersPerPixel <= 0)
            result.Errors.Add("Terrain size and meters-per-pixel must be positive.");
        if (TerrainHeightMap.GetLength(0) != TerrainSizePixels ||
            TerrainHeightMap.GetLength(1) != TerrainSizePixels)
            result.Errors.Add(
                $"TerrainHeightMap is {TerrainHeightMap.GetLength(1)}x{TerrainHeightMap.GetLength(0)}, expected {TerrainSizePixels}x{TerrainSizePixels}.");
        if (SourceGeoTransform is not { Length: 6 })
            result.Errors.Add("SourceGeoTransform must have exactly 6 elements.");
        if (TerrainRect.IsEmpty || BackdropRect.IsEmpty)
            result.Errors.Add("Terrain and backdrop rects must be non-empty.");
        if (EdgeBandMeters < 0 || ChunkTargetMeters <= 0 || TexelDensityNearMPerPx <= 0 ||
            MaxVerticalErrorNearMeters <= 0 || MaxVerticalErrorFarMeters <= 0 ||
            MaxChunkTextureSize < 256 || MaxFarRasterDimension < 256)
            result.Errors.Add("One or more tunables are out of range.");
        if (result.Errors.Count > 0)
            return result; // margin math below needs sane inputs

        var mosaic = new PixelRect(0, 0, SourceRasterWidth, SourceRasterHeight);
        if (!mosaic.ContainsRect(BackdropRect))
            result.Errors.Add("The backdrop rect must lie inside the loaded tile mosaic.");
        if (!BackdropRect.ContainsRect(TerrainRect))
            result.Errors.Add("The backdrop rect must fully contain the terrain rect.");
        if (result.Errors.Count > 0)
            return result;

        // Per-side margins in meters (spec §5: 0 allowed per side, but not on ALL sides;
        // 0 < margin < EdgeBandMeters → warning, band is clipped there).
        double west = (TerrainRect.X - BackdropRect.X) * MetersPerSourcePixelX;
        double east = (BackdropRect.Right - TerrainRect.Right) * MetersPerSourcePixelX;
        double north = (TerrainRect.Y - BackdropRect.Y) * MetersPerSourcePixelY;
        double south = (BackdropRect.Bottom - TerrainRect.Bottom) * MetersPerSourcePixelY;

        if (west <= 0 && east <= 0 && north <= 0 && south <= 0)
            result.Errors.Add("At least one side must have a margin > 0 — the backdrop ring is empty.");

        foreach (var (name, margin) in new[] { ("west", west), ("east", east), ("north", north), ("south", south) })
            if (margin > 0 && margin < EdgeBandMeters)
                result.Warnings.Add(
                    $"The {name} margin ({margin:F0} m) is narrower than the full-resolution edge band ({EdgeBandMeters:F0} m); the band is clipped there.");

        return result;
    }
}
