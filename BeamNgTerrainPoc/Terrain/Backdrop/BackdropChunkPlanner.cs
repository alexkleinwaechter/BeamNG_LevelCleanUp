using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Plans the backdrop ring's chunk grid (spec §9/§10): lattice-aligned cells sized towards
///     <see cref="BackdropGenerationParameters.ChunkTargetMeters"/>, never overlapping the terrain rect,
///     with derived world/source rects, WGS84 bounds and per-chunk texture size.
/// </summary>
public static class BackdropChunkPlanner
{
    public static BackdropChunkPlan Plan(BackdropGenerationParameters p)
    {
        var mapper = new BackdropCoordinateMapper(p.TerrainRect, p.TerrainSizePixels, p.TerrainMetersPerPixel);
        var u = (double)p.TerrainMetersPerPixel;
        var half = mapper.HalfSizeMeters;
        var size = p.TerrainSizePixels;

        // 1. Backdrop rect → lattice, snapped inward.
        var (wMinX, wMaxY) = mapper.SourcePixelToWorld(p.BackdropRect.X, p.BackdropRect.Y);
        var (wMaxX, wMinY) = mapper.SourcePixelToWorld(p.BackdropRect.Right, p.BackdropRect.Bottom);
        var latMinX = (int)Math.Ceiling((wMinX + half) / u - 1e-9);
        var latMinY = (int)Math.Ceiling((wMinY + half) / u - 1e-9);
        var latMaxX = (int)Math.Floor((wMaxX + half) / u + 1e-9);
        var latMaxY = (int)Math.Floor((wMaxY + half) / u + 1e-9);

        // 2. Grid lines per axis: margins + terrain span, each partitioned to ~ChunkTargetMeters.
        var xLines = BuildAxisLines(latMinX, latMaxX, terrainMin: 0, terrainMax: size, u, p.ChunkTargetMeters);
        var yLines = BuildAxisLines(latMinY, latMaxY, terrainMin: 0, terrainMax: size, u, p.ChunkTargetMeters);

        var margins = new[]
        {
            Math.Max(0, 0 - latMinX) * u, Math.Max(0, latMaxX - size) * u,
            Math.Max(0, 0 - latMinY) * u, Math.Max(0, latMaxY - size) * u
        };
        var maxMargin = Math.Max(1.0, margins.Max());

        var chunks = new List<BackdropChunkDefinition>();
        for (var cy = 0; cy < yLines.Count - 1; cy++)
        for (var cx = 0; cx < xLines.Count - 1; cx++)
        {
            int lx = xLines[cx], ly = yLines[cy];
            int lw = xLines[cx + 1] - lx, lh = yLines[cy + 1] - ly;
            if (lw <= 0 || lh <= 0) continue;
            var insideTerrain = lx >= 0 && ly >= 0 && lx + lw <= size && ly + lh <= size;
            if (insideTerrain) continue;                            // 3. drop terrain cells

            chunks.Add(CreateDefinition(p, mapper, u, half, maxMargin, cx, cy, lx, ly, lw, lh));
        }

        return new BackdropChunkPlan
        {
            Chunks = chunks,                                       // built (cy, cx)-ordered → stable
            MaxMarginMeters = maxMargin,
            LatticeMinX = latMinX, LatticeMinY = latMinY,
            LatticeMaxX = latMaxX, LatticeMaxY = latMaxY
        };
    }

    /// <summary>Sorted grid lines: interval [min,max] split at terrain edges, then each piece partitioned.</summary>
    private static List<int> BuildAxisLines(int min, int max, int terrainMin, int terrainMax,
        double u, double chunkTargetMeters)
    {
        var lines = new List<int> { min };
        void Partition(int from, int to)
        {
            var lattice = to - from;
            if (lattice <= 0) return;
            var count = Math.Max(1, (int)Math.Ceiling(lattice * u / chunkTargetMeters));
            var baseWidth = lattice / count;
            var remainder = lattice % count;
            var pos = from;
            for (var i = 0; i < count; i++)
            {
                pos += baseWidth + (i < remainder ? 1 : 0);
                lines.Add(pos);
            }
        }
        Partition(min, Math.Min(max, Math.Max(min, terrainMin)));                 // west/south margin
        if (terrainMin > min && terrainMin < max && !lines.Contains(terrainMin)) lines.Add(terrainMin);
        Partition(Math.Max(min, terrainMin), Math.Min(max, terrainMax));          // terrain span
        if (terrainMax > min && terrainMax < max && !lines.Contains(terrainMax)) lines.Add(terrainMax);
        Partition(Math.Max(min, Math.Min(max, terrainMax)), max);                 // east/north margin
        return lines.Distinct().OrderBy(v => v).ToList();
    }

    private static BackdropChunkDefinition CreateDefinition(BackdropGenerationParameters p,
        BackdropCoordinateMapper mapper, double u, double half, double maxMargin,
        int cx, int cy, int lx, int ly, int lw, int lh)
    {
        double wMinX = lx * u - half, wMinY = ly * u - half;
        double wMaxX = (lx + lw) * u - half, wMaxY = (ly + lh) * u - half;

        // Chunk-center distance to the terrain rect (Euclidean, 0 if touching).
        double centerX = (wMinX + wMaxX) / 2, centerY = (wMinY + wMaxY) / 2;
        double dx = Math.Max(Math.Abs(centerX) - half, 0), dy = Math.Max(Math.Abs(centerY) - half, 0);
        // Touching chunks: center may sit outside but the chunk borders the rect → use min corner distance 0
        var touches = wMinX <= half && wMaxX >= -half && wMinY <= half && wMaxY >= -half
                      && (wMinX <= -half || wMaxX >= half || wMinY <= -half || wMaxY >= half)
                      && (Math.Abs(wMinX) <= half || Math.Abs(wMaxX) <= half ||
                          Math.Abs(wMinY) <= half || Math.Abs(wMaxY) <= half);
        var distance = touches && (lx <= p.TerrainSizePixels && lx + lw >= 0 && ly <= p.TerrainSizePixels && ly + lh >= 0)
            ? DistanceRectToRect(wMinX, wMinY, wMaxX, wMaxY, half)
            : Math.Sqrt(dx * dx + dy * dy);

        var (srcNwX, srcNwY) = mapper.WorldToSourcePixel(wMinX, wMaxY);
        var (srcSeX, srcSeY) = mapper.WorldToSourcePixel(wMaxX, wMinY);

        var extent = Math.Max(wMaxX - wMinX, wMaxY - wMinY);
        var dNorm = Math.Clamp(distance / maxMargin, 0.0, 1.0);
        var density = p.TexelDensityNearMPerPx * (1.0 + 3.0 * dNorm);
        var texture = Math.Clamp(NextPow2((int)Math.Ceiling(extent / density)), 256, p.MaxChunkTextureSize);

        return new BackdropChunkDefinition
        {
            Cx = cx, Cy = cy,
            LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
            WorldMinX = wMinX, WorldMinY = wMinY, WorldMaxX = wMaxX, WorldMaxY = wMaxY,
            SourceRectX = srcNwX, SourceRectY = srcNwY,
            SourceRectWidth = srcSeX - srcNwX, SourceRectHeight = srcSeY - srcNwY,
            Wgs84Bounds = ComputeWgs84Bounds(p, srcNwX, srcNwY, srcSeX, srcSeY),
            DaeFileName = $"backdrop_{cx}_{cy}.dae",
            // ".color" suffix is required by the game's texture cooker: it marks the PNG as a color
            // map and compiles it to BC7 sRGB DDS. Without it the texture is sampled as linear data
            // and renders washed out in game.
            TextureFileName = $"backdrop_{cx}_{cy}.color.png",
            MaterialName = $"mt_backdrop_{cx}_{cy}",
            TextureSize = texture,
            DistanceToTerrainMeters = distance
        };
    }

    /// <summary>Euclidean distance between an axis-aligned rect and the centered terrain square (0 when touching).</summary>
    private static double DistanceRectToRect(double minX, double minY, double maxX, double maxY, double half)
    {
        var dx = Math.Max(Math.Max(-half - maxX, minX - half), 0);
        var dy = Math.Max(Math.Max(-half - maxY, minY - half), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static GeoBoundingBox? ComputeWgs84Bounds(BackdropGenerationParameters p,
        double srcMinX, double srcMinY, double srcMaxX, double srcMaxY)
    {
        var gt = p.SourceGeoTransform;
        if (!string.IsNullOrWhiteSpace(p.ProjectionWkt))
        {
            (double X, double Y) Native(double px, double py) =>
                (gt[0] + px * gt[1] + py * gt[2], gt[3] + px * gt[4] + py * gt[5]);
            var corners = new[] { Native(srcMinX, srcMinY), Native(srcMaxX, srcMinY),
                                  Native(srcMinX, srcMaxY), Native(srcMaxX, srcMaxY) };
            var native = new GeoBoundingBox(
                corners.Min(c => c.X), corners.Min(c => c.Y),
                corners.Max(c => c.X), corners.Max(c => c.Y));
            var wgs84 = GeoBoundingBox.TransformToWgs84(native, p.ProjectionWkt, quiet: true);
            if (wgs84 != null) return wgs84;
        }
        // Linear fallback over the mosaic bbox (same math as CropAnchorSelector.RecalculateSelectionBoundingBox).
        if (p.SourceWgs84Bounds is { } bbox && p.SourceRasterWidth > 0 && p.SourceRasterHeight > 0)
        {
            var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
            var latRange = bbox.MaxLatitude - bbox.MinLatitude;
            return new GeoBoundingBox(
                bbox.MinLongitude + lonRange * (srcMinX / p.SourceRasterWidth),
                bbox.MaxLatitude - latRange * (srcMaxY / p.SourceRasterHeight),
                bbox.MinLongitude + lonRange * (srcMaxX / p.SourceRasterWidth),
                bbox.MaxLatitude - latRange * (srcMinY / p.SourceRasterHeight));
        }
        return null;
    }

    private static int NextPow2(int v)
    {
        var result = 256;
        while (result < v && result < 1 << 24) result <<= 1;
        return result;
    }
}
