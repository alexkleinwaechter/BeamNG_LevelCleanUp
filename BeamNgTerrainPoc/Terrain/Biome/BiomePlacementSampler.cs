namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Terrain data needed by the sampler: raw ushort heights plus the TerrainBlock
/// parameters required to decode them into world elevations.
/// </summary>
public sealed class BiomeTerrainContext
{
    public required int Size { get; init; }
    public required float MetersPerPixel { get; init; }
    public required ushort[] HeightData { get; init; }
    public required float MaxHeight { get; init; }
    /// <summary>TerrainBlock position[2] — added to decoded heights for absolute world Z.</summary>
    public required float TerrainBaseHeight { get; init; }

    public float DecodeHeight(int x, int y) => HeightData[y * Size + x] / 65535f * MaxHeight;

    /// <summary>
    /// Bilinear height sample at a continuous terrain-space position (meters from the
    /// bottom-left corner). Returns terrain-local height (without TerrainBaseHeight).
    /// </summary>
    public float SampleHeightBilinear(float terrainXMeters, float terrainYMeters)
    {
        var px = terrainXMeters / MetersPerPixel;
        var py = terrainYMeters / MetersPerPixel;

        var x0 = Math.Clamp((int)MathF.Floor(px), 0, Size - 2);
        var y0 = Math.Clamp((int)MathF.Floor(py), 0, Size - 2);
        var fx = Math.Clamp(px - x0, 0f, 1f);
        var fy = Math.Clamp(py - y0, 0f, 1f);

        var h00 = DecodeHeight(x0, y0);
        var h10 = DecodeHeight(x0 + 1, y0);
        var h01 = DecodeHeight(x0, y0 + 1);
        var h11 = DecodeHeight(x0 + 1, y0 + 1);

        var hx0 = h00 + (h10 - h00) * fx;
        var hx1 = h01 + (h11 - h01) * fx;
        return hx0 + (hx1 - hx0) * fy;
    }

    /// <summary>Terrain slope in degrees at a pixel, from central differences (clamped at edges).</summary>
    public float SlopeDegreesAt(int x, int y)
    {
        var xm = Math.Max(x - 1, 0);
        var xp = Math.Min(x + 1, Size - 1);
        var ym = Math.Max(y - 1, 0);
        var yp = Math.Min(y + 1, Size - 1);

        var dzdx = (DecodeHeight(xp, y) - DecodeHeight(xm, y)) / ((xp - xm) * MetersPerPixel);
        var dzdy = (DecodeHeight(x, yp) - DecodeHeight(x, ym)) / ((yp - ym) * MetersPerPixel);

        var gradient = MathF.Sqrt(dzdx * dzdx + dzdy * dzdy);
        return MathF.Atan(gradient) * (180f / MathF.PI);
    }
}

/// <summary>
/// One selectable item type inside a zone, with the placement parameters taken from
/// its ForestBrushElement (defaults where the element omits them).
/// </summary>
public sealed class BiomeItemSpec
{
    public required string TypeName { get; init; }
    /// <summary>UI slider 0–100 (0.5 steps); 0 = excluded.</summary>
    public double DensityPercent { get; init; }
    /// <summary>Footprint radius from managedItemData (fallback 0.5 m).</summary>
    public double RadiusMeters { get; init; } = BiomeDensityModel.DefaultRadiusMeters;
    public double ScaleMin { get; init; } = 0.8;
    public double ScaleMax { get; init; } = 1.2;
    public double SinkMin { get; init; } = 0.0;
    public double SinkMax { get; init; } = 0.1;
    /// <summary>Random yaw range in degrees (BeamNG rotationRange semantics).</summary>
    public double RotationRangeDeg { get; init; } = 360.0;
    public double? SlopeMinDeg { get; init; }
    public double? SlopeMaxDeg { get; init; }
    /// <summary>Absolute world elevation limits (element elevationMin/Max).</summary>
    public double? ElevationMin { get; init; }
    public double? ElevationMax { get; init; }
}

/// <summary>
/// One accepted placement in terrain space. TerrainX/TerrainY are meters from the
/// bottom-left corner; WorldZ is absolute (decoded height + base height − sink).
/// The caller converts XY to centered BeamNG world coordinates.
/// </summary>
public sealed record BiomePlacement(
    string TypeName,
    float TerrainX,
    float TerrainY,
    float WorldZ,
    float Scale,
    float YawRadians);

public sealed class BiomeSamplerOptions
{
    public double SpacingFactor { get; init; } = BiomeDensityModel.DefaultSpacingFactor;
    /// <summary>Attempt budget per item = target count × this factor.</summary>
    public int OversampleFactor { get; init; } = 4;
    public double? ZoneSlopeMinDeg { get; init; }
    public double? ZoneSlopeMaxDeg { get; init; }
    /// <summary>Called periodically with (acceptedSoFar, totalTarget).</summary>
    public Action<int, int>? Progress { get; init; }
}

/// <summary>
/// Seeded dart-throwing sampler: uniform candidates over the zone's pixels with
/// sub-pixel jitter, rejected by slope/elevation filters and a spatial-hash spacing
/// rule, interleaved fairly across item types. Single-threaded by design —
/// determinism (same seed → identical forest) outranks speed here.
/// </summary>
public static class BiomePlacementSampler
{
    /// <summary>List-building convenience wrapper around the streaming core.</summary>
    public static List<BiomePlacement> SampleZone(
        BiomeTerrainContext terrain,
        int[] zonePixels,
        IReadOnlyList<BiomeItemSpec> items,
        ulong seed,
        BiomeSamplerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var placements = new List<BiomePlacement>();
        SampleZoneStreaming(terrain, zonePixels, items, seed, placements.Add, options, cancellationToken);
        return placements;
    }

    /// <summary>
    /// Streaming core: accepted placements go straight to <paramref name="sink"/>
    /// (e.g. a file writer) and are never accumulated here — only the compact
    /// position/footprint arrays needed for the spacing rule stay in memory.
    /// Returns the number of accepted placements.
    /// </summary>
    public static int SampleZoneStreaming(
        BiomeTerrainContext terrain,
        int[] zonePixels,
        IReadOnlyList<BiomeItemSpec> items,
        ulong seed,
        Action<BiomePlacement> sink,
        BiomeSamplerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BiomeSamplerOptions();

        var active = items.Where(i => i.DensityPercent > 0).ToList();
        if (active.Count == 0 || zonePixels.Length == 0)
        {
            return 0;
        }

        var spacingFactor = options.SpacingFactor;
        var mpp = terrain.MetersPerPixel;

        var targets = new int[active.Count];
        var budgets = new int[active.Count];
        var placed = new int[active.Count];
        var attempts = new int[active.Count];
        var totalTarget = 0;
        for (var i = 0; i < active.Count; i++)
        {
            var item = active[i];
            targets[i] = (int)Math.Min(
                BiomeDensityModel.EstimateCount(
                    zonePixels.Length, mpp, item.DensityPercent,
                    item.RadiusMeters, item.ScaleMin, item.ScaleMax),
                int.MaxValue / 2);
            budgets[i] = (int)Math.Min((long)targets[i] * Math.Max(options.OversampleFactor, 1), int.MaxValue / 2);
            totalTarget += targets[i];
        }

        if (totalTarget == 0)
        {
            return 0;
        }

        // Spatial hash for the spacing rule. Min distance between accepted a and b is
        // spacingFactor * (r_a*s_a + r_b*s_b); the largest possible value is
        // 2 * spacingFactor * max(r*scaleMax), so with that cell size a 3×3
        // neighborhood always contains every potential conflict.
        var maxInteraction = active.Max(i => spacingFactor * i.RadiusMeters * i.ScaleMax);
        var cellSize = (float)Math.Max(2 * maxInteraction, 0.1);
        var occupancy = new Dictionary<long, List<int>>();
        var acceptedX = new List<float>();
        var acceptedY = new List<float>();
        var acceptedRs = new List<float>(); // spacingFactor * radius * scale, precomputed per accepted item

        var rng = new BiomeRandom(seed);
        var iteration = 0;
        var acceptedCount = 0;

        while (true)
        {
            // Fair interleave: next attempt goes to the active item with the lowest
            // fill fraction that still has target and budget left.
            var best = -1;
            var bestFraction = double.MaxValue;
            for (var i = 0; i < active.Count; i++)
            {
                if (placed[i] >= targets[i] || attempts[i] >= budgets[i])
                {
                    continue;
                }
                var fraction = (double)placed[i] / targets[i];
                if (fraction < bestFraction)
                {
                    bestFraction = fraction;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            if ((++iteration & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                options.Progress?.Invoke(acceptedCount, totalTarget);
            }

            attempts[best]++;
            var item = active[best];

            var pixelIndex = zonePixels[rng.NextInt(zonePixels.Length)];
            var px = pixelIndex % terrain.Size;
            var py = pixelIndex / terrain.Size;
            var tx = (px + (float)rng.NextDouble()) * mpp;
            var ty = (py + (float)rng.NextDouble()) * mpp;

            // Draw the per-item randomness before any rejection so the random stream
            // consumed per attempt is constant — keeps results stable when filters change.
            var scale = (float)rng.NextRange(item.ScaleMin, item.ScaleMax);
            var sinkDepth = (float)rng.NextRange(item.SinkMin, item.SinkMax);
            var yawDeg = rng.NextRange(0, Math.Max(item.RotationRangeDeg, 0));

            var slope = terrain.SlopeDegreesAt(px, py);
            if (options.ZoneSlopeMinDeg.HasValue && slope < options.ZoneSlopeMinDeg.Value) continue;
            if (options.ZoneSlopeMaxDeg.HasValue && slope > options.ZoneSlopeMaxDeg.Value) continue;
            if (item.SlopeMinDeg.HasValue && slope < item.SlopeMinDeg.Value) continue;
            if (item.SlopeMaxDeg.HasValue && slope > item.SlopeMaxDeg.Value) continue;

            var groundZ = terrain.SampleHeightBilinear(tx, ty) + terrain.TerrainBaseHeight;
            if (item.ElevationMin.HasValue && groundZ < item.ElevationMin.Value) continue;
            if (item.ElevationMax.HasValue && groundZ > item.ElevationMax.Value) continue;

            var ownRs = (float)(spacingFactor * item.RadiusMeters) * scale;
            if (HasSpacingConflict(occupancy, acceptedX, acceptedY, acceptedRs, cellSize, tx, ty, ownRs))
            {
                continue;
            }

            var index = acceptedX.Count;
            acceptedX.Add(tx);
            acceptedY.Add(ty);
            acceptedRs.Add(ownRs);
            var cell = EncodeCell((int)MathF.Floor(tx / cellSize), (int)MathF.Floor(ty / cellSize));
            if (!occupancy.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                occupancy[cell] = list;
            }
            list.Add(index);

            sink(new BiomePlacement(
                item.TypeName, tx, ty, groundZ - sinkDepth, scale, (float)(yawDeg * Math.PI / 180.0)));
            acceptedCount++;
            placed[best]++;
        }

        options.Progress?.Invoke(acceptedCount, totalTarget);
        return acceptedCount;
    }

    private static long EncodeCell(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    private static bool HasSpacingConflict(
        Dictionary<long, List<int>> occupancy,
        List<float> acceptedX,
        List<float> acceptedY,
        List<float> acceptedRs,
        float cellSize,
        float tx,
        float ty,
        float ownRs)
    {
        var cx = (int)MathF.Floor(tx / cellSize);
        var cy = (int)MathF.Floor(ty / cellSize);

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!occupancy.TryGetValue(EncodeCell(cx + dx, cy + dy), out var list))
                {
                    continue;
                }
                foreach (var idx in list)
                {
                    var ddx = acceptedX[idx] - tx;
                    var ddy = acceptedY[idx] - ty;
                    var minDist = ownRs + acceptedRs[idx];
                    if (ddx * ddx + ddy * ddy < minDist * minDist)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
