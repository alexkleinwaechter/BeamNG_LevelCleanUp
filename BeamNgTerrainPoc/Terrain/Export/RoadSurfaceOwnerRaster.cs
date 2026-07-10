using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// A per-cell map of which spline's PAINTED road surface (SurfaceWidth) owns each heightmap cell, or
/// <see cref="NoOwner"/>. The post-blend bridge terrain passes — approach-raise embankment fill, lower-road
/// dips, the abutment overlap tongue, deck excavation — sculpt the heightmap to match the bridge deck
/// profile. At an abutment that sits beside/over another road they would otherwise overwrite that road's
/// protected surface (the gouge/bury artifact). Each pass consults this raster and SKIPS any cell owned by a
/// DIFFERENT spline than the one it is shaping; the structure's OWN cells (owner == self) and bare terrain
/// (owner == <see cref="NoOwner"/>) stay writable. Same surface-protection intent as
/// <c>RoadMaskBuilder</c> Pass 1 (EnableSurfaceWidthProtection), carried into the post-blend stage.
/// </summary>
public static class RoadSurfaceOwnerRaster
{
    /// <summary>Sentinel for a cell that is not part of any road's painted surface (bare terrain).</summary>
    public const int NoOwner = -1;

    /// <summary>
    /// Rasterizes every spline's painted-surface footprint (SurfaceWidth/2 + <paramref name="marginMeters"/>)
    /// into an owner map [y, x]. Excluded (deck/structure) sections are skipped — they are not terrain road
    /// surface. Where surfaces overlap the HIGHER-priority spline owns the cell (winningen render
    /// 2026-07-02 #2: a sparse span's non-excluded 3 m tongue zone lies OVER the crossing road, and
    /// first-writer-wins let the lower-priority structure steal the through-road's lane cells — the
    /// abutment tongue then stamped a deck-level patch across the underpass road). Ties keep the first
    /// writer (deterministic at junctions).
    /// </summary>
    public static int[,] Build(
        UnifiedRoadNetwork network,
        int mapHeight,
        int mapWidth,
        float metersPerPixel,
        float marginMeters = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(network);
        var owner = new int[mapHeight, mapWidth];
        for (var y = 0; y < mapHeight; y++)
        for (var x = 0; x < mapWidth; x++)
            owner[y, x] = NoOwner;

        if (metersPerPixel <= 0f)
            return owner;

        var priorityBySpline = network.Splines.ToDictionary(s => s.SplineId, s => s.Priority);
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);
        foreach (var spline in network.Splines)
        {
            var sections = network.GetCrossSectionsForSpline(spline.SplineId)
                .Where(c => !c.IsExcluded)
                .OrderBy(c => c.DistanceAlongSpline)
                .ToList();
            for (var i = 0; i + 1 < sections.Count; i++)
                StampSegment(owner, sections[i], sections[i + 1], spline.SplineId, spline.Priority,
                    priorityBySpline, metersPerPixel, mapWidth, mapHeight, lateralStep, marginMeters);
        }

        return owner;
    }

    /// <summary>
    /// Sub-stepped march over one cross-section pair, stamping owner across SurfaceWidth/2 + margin so the
    /// footprint is contiguous regardless of the section interval (same march as the overlap stamper).
    /// </summary>
    private static void StampSegment(
        int[,] owner,
        UnifiedCrossSection a,
        UnifiedCrossSection b,
        int splineId,
        int splinePriority,
        Dictionary<int, int> priorityBySpline,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        float lateralStep,
        float marginMeters)
    {
        var segLen = MathF.Abs(b.DistanceAlongSpline - a.DistanceAlongSpline);
        var steps = Math.Max(1, (int)MathF.Ceiling(segLen / lateralStep));
        for (var s = 0; s <= steps; s++)
        {
            var t = (float)s / steps;
            var center = a.CenterPoint + (b.CenterPoint - a.CenterPoint) * t;
            var normal = a.NormalDirection + (b.NormalDirection - a.NormalDirection) * t;
            var normalLen = normal.Length();
            if (normalLen > 1e-4f)
                normal /= normalLen;
            var surfaceWidth = a.SurfaceWidth + (b.SurfaceWidth - a.SurfaceWidth) * t;
            var half = surfaceWidth / 2f + marginMeters;

            for (var offset = -half; offset <= half; offset += lateralStep)
            {
                var worldX = center.X + normal.X * offset;
                var worldY = center.Y + normal.Y * offset;
                var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                var existing = owner[py, px];
                if (existing == NoOwner)
                    owner[py, px] = splineId; // bare terrain — claim it
                else if (existing != splineId &&
                         priorityBySpline.TryGetValue(existing, out var existingPriority) &&
                         splinePriority > existingPriority)
                    owner[py, px] = splineId; // higher-priority surface wins the overlap
            }
        }
    }

    /// <summary>
    /// True if a cell may be written while shaping spline <paramref name="selfSplineId"/>: bare terrain
    /// (no owner) or owned by self. False = it is another road's protected painted surface. A null raster
    /// (legacy / unit tests) always returns true → byte-identical behaviour.
    /// </summary>
    public static bool CanWrite(int[,]? owner, int py, int px, int selfSplineId)
    {
        if (owner == null)
            return true;
        var o = owner[py, px];
        return o == NoOwner || o == selfSplineId;
    }
}
