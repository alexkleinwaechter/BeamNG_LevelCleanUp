using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Doc 09 §9.2 — deck-footprint RAISE guard.
/// A per-cell map of which bridge deck owns each heightmap cell, or <see cref="NoOwner" />.
/// Only RAISING passes consult this raster (<see cref="BridgeAbutmentOverlapStamper" />,
/// <see cref="GradeSeparationResolver.ApplyApproachRaiseRamps" />); LOWERING passes
/// (<see cref="GradeSeparationResolver.ApplyLowerRoadDips" />, <see cref="BridgeDeckExcavator" />)
/// intentionally do NOT — cutting below a foreign deck is harmless, and blocking the underpass
/// dip-carve would shear dip wells (a known past regression class).
/// <para>
/// The existing <see cref="RoadSurfaceOwnerRaster" /> (painted road surfaces) is NOT modified — the
/// lower road under a deck keeps owning its lane cells so its dip carve still works.
/// </para>
/// </summary>
public static class BridgeDeckFootprintRaster
{
    /// <summary>Sentinel: cell is not part of any deck's footprint.</summary>
    public const int NoOwner = -1;

    /// <summary>
    /// Rasterizes every bridge deck footprint into an owner map [y, x] at
    /// <c>EffectiveRoadWidth/2 + <paramref name="marginMeters" /></c>.
    /// Only <see cref="UnifiedCrossSection.IsExcluded" /> cross-sections are stamped — those are the
    /// 3-D mesh spans; ordinary stamped road is invisible to this guard.
    /// <para>
    /// Decks are grouped by <c>(OwnerSplineId, StructureSpanId)</c> (legacy whole-spline decks have
    /// <c>StructureSpanId == -1</c> and form one group per spline). Groups with at least one finite
    /// <see cref="UnifiedCrossSection.TargetElevation" /> are stamped in ASCENDING mean-Z order so the
    /// LOWER deck owns contested cells — an upper deck's raising tongue is blocked from burying a lower
    /// one, while the lower deck's tongue to its own level stays below the upper deck and is safe.
    /// Groups with no finite Z are stamped last (they can only claim still-unclaimed cells).
    /// </para>
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

        // Collect all excluded cross-section groups (deck footprints).
        var groups = network.CrossSections
            .Where(c => c.IsExcluded)
            .GroupBy(c => (c.OwnerSplineId, c.StructureSpanId))
            .Select(g =>
            {
                var sections = g.OrderBy(c => c.DistanceAlongSpline).ToList();
                var finite = sections
                    .Select(c => c.TargetElevation)
                    .Where(z => float.IsFinite(z))
                    .ToList();
                var meanZ = finite.Count > 0 ? finite.Average() : float.NaN;
                return (SplineId: g.Key.OwnerSplineId, sections, meanZ);
            })
            // Lower deck first (claim-only-if-NoOwner ensures lower wins shared cells).
            // Groups without any finite Z sort after all finite ones.
            .OrderBy(g => float.IsNaN(g.meanZ) ? float.MaxValue : g.meanZ)
            .ToList();

        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        foreach (var (splineId, sections, _) in groups)
        {
            for (var i = 0; i + 1 < sections.Count; i++)
                StampSegment(owner, sections[i], sections[i + 1], splineId,
                    metersPerPixel, mapWidth, mapHeight, lateralStep, marginMeters);
        }

        return owner;
    }

    /// <summary>
    /// Sub-stepped march over one cross-section pair, claiming every cell within
    /// <c>EffectiveRoadWidth/2 + marginMeters</c> for <paramref name="splineId" />.
    /// A cell is claimed only if still <see cref="NoOwner" /> — so where two decks overlap the
    /// first stamped (lower mean Z) wins.
    /// </summary>
    private static void StampSegment(
        int[,] owner,
        UnifiedCrossSection a,
        UnifiedCrossSection b,
        int splineId,
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
            var effectiveWidth = a.EffectiveRoadWidth + (b.EffectiveRoadWidth - a.EffectiveRoadWidth) * t;
            var half = effectiveWidth / 2f + marginMeters;

            for (var offset = -half; offset <= half; offset += lateralStep)
            {
                var worldX = center.X + normal.X * offset;
                var worldY = center.Y + normal.Y * offset;
                var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                // Claim-only-if-NoOwner: lower deck (stamped first) wins shared cells.
                if (owner[py, px] == NoOwner)
                    owner[py, px] = splineId;
            }
        }
    }

    /// <summary>
    /// True when a raising pass may write to cell <c>[py, px]</c> while shaping
    /// <paramref name="selfSplineId" />: null raster (legacy / unit tests) → always true;
    /// <see cref="NoOwner" /> cell → true; owned by self → true; owned by a FOREIGN deck → false.
    /// </summary>
    public static bool CanRaise(int[,]? deckOwner, int py, int px, int selfSplineId)
    {
        if (deckOwner == null)
            return true;
        var o = deckOwner[py, px];
        return o == NoOwner || o == selfSplineId;
    }
}
