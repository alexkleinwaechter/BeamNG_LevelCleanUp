using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// The XY footprint of one bridge span — the planar polygon swept by the span's cross-section centerline
/// ± half the effective road width along each section normal (plan doc 14 §5 / §4.1).
///
/// <para>This is the robust "what is under the bridge" test that replaces the unreliable ~100-sample
/// mid-spline-crossing loop: any other spline's cross-section whose center falls inside this footprint is
/// physically beneath (or on) the deck, regardless of whether the crossing detector happened to sample that
/// spot. It is geometry-only (XY, pre-elevation): <see cref="UnifiedCrossSection.CenterPoint"/>,
/// <see cref="UnifiedCrossSection.NormalDirection"/> and <see cref="UnifiedCrossSection.EffectiveRoadWidth"/>
/// are all set at network build, so the footprint is valid before any smoothing (banked widening is ignored,
/// acceptable for detection — review finding F5).</para>
/// </summary>
public sealed class BridgeSpanFootprint
{
    private readonly IReadOnlyList<(Vector2 left, Vector2 right)> _ribs;
    private readonly Vector2 _min;
    private readonly Vector2 _max;

    /// <summary>Spline id of the corridor that carries this bridge span.</summary>
    public int OwnerSplineId { get; }

    /// <summary>Stable id of the span (<see cref="StructureSegment.SpanId"/>).</summary>
    public int SpanId { get; }

    /// <summary>Effective vertical layer of the span (the deck's layer, typically ≥ 1).</summary>
    public int Layer { get; }

    /// <summary>Axis-aligned bounding box (min corner) of the footprint in world XY. Used to window terrain sampling.</summary>
    public Vector2 Min => _min;

    /// <summary>Axis-aligned bounding box (max corner) of the footprint in world XY.</summary>
    public Vector2 Max => _max;

    private BridgeSpanFootprint(
        int ownerSplineId, int spanId, int layer,
        List<(Vector2 left, Vector2 right)> ribs, Vector2 min, Vector2 max)
    {
        OwnerSplineId = ownerSplineId;
        SpanId = spanId;
        Layer = layer;
        _ribs = ribs;
        _min = min;
        _max = max;
    }

    /// <summary>
    /// Builds the footprint from the owner spline's cross-sections that fall inside the span's arc-range.
    /// Returns null if fewer than two in-range ribs exist (a degenerate, zero-area span).
    /// </summary>
    public static BridgeSpanFootprint? Build(
        int ownerSplineId, StructureSegment seg, IEnumerable<UnifiedCrossSection> ownerSections)
        => Build(ownerSplineId, seg.SpanId, seg.StartDistance, seg.EndDistance, seg.Layer, ownerSections);

    /// <summary>
    /// Arc-range/layer-explicit core of <see cref="Build(int, StructureSegment, IEnumerable{UnifiedCrossSection})"/> —
    /// used to emit one footprint per layer sub-range of a consolidated span (doc 10), so the
    /// grade-separation pass compares the RIGHT local layer at every crossing while the deck keeps one
    /// merged <paramref name="spanId"/>.
    /// </summary>
    public static BridgeSpanFootprint? Build(
        int ownerSplineId, int spanId, float startDistance, float endDistance, int layer,
        IEnumerable<UnifiedCrossSection> ownerSections)
    {
        var inSpan = ownerSections
            .Where(c => c.DistanceAlongSpline >= startDistance && c.DistanceAlongSpline <= endDistance)
            .OrderBy(c => c.DistanceAlongSpline)
            .ToList();

        if (inSpan.Count < 2) return null;

        var ribs = new List<(Vector2 left, Vector2 right)>(inSpan.Count);
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        foreach (var cs in inSpan)
        {
            var half = MathF.Max(0.5f, cs.EffectiveRoadWidth * 0.5f);
            var left = cs.CenterPoint - cs.NormalDirection * half;
            var right = cs.CenterPoint + cs.NormalDirection * half;
            ribs.Add((left, right));
            min = Vector2.Min(min, Vector2.Min(left, right));
            max = Vector2.Max(max, Vector2.Max(left, right));
        }

        return new BridgeSpanFootprint(ownerSplineId, spanId, layer, ribs, min, max);
    }

    /// <summary>
    /// Builds footprints for every merged-corridor bridge span in the network. Flag-gated: a spline only
    /// contributes when <see cref="RoadSmoothingParameters.MergeStructuresIntoCorridor"/> is on and the span
    /// is a terrain-excluded bridge (the same condition that tags its sections in
    /// <c>UnifiedRoadSmoother.TagStructureSpans</c>), so legacy mode yields no footprints.
    /// </summary>
    public static List<BridgeSpanFootprint> BuildAll(UnifiedRoadNetwork network)
    {
        var result = new List<BridgeSpanFootprint>();
        foreach (var spline in network.Splines)
        {
            var parameters = spline.Parameters;
            if (!parameters.MergeStructuresIntoCorridor) continue;
            if (spline.StructureSegments is not { Count: > 0 } segs) continue;

            var sections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            foreach (var seg in segs)
            {
                if (!(seg.IsBridge && parameters.ExcludeBridgesFromTerrain)) continue;

                if (seg.LayerRanges is { Count: > 1 } ranges)
                {
                    // Consolidated span (doc 10): one footprint per layer sub-range (same SpanId) so
                    // upper/lower classification uses the local layer at each crossing station.
                    foreach (var range in ranges)
                    {
                        var fp = Build(spline.SplineId, seg.SpanId,
                            range.StartDistance, range.EndDistance, range.Layer, sections);
                        if (fp != null) result.Add(fp);
                    }
                }
                else
                {
                    var fp = Build(spline.SplineId, seg, sections);
                    if (fp != null) result.Add(fp);
                }
            }
        }

        return result;
    }

    /// <summary>True if the plan-view point lies within the swept deck polygon.</summary>
    public bool Contains(Vector2 p)
    {
        if (p.X < _min.X || p.X > _max.X || p.Y < _min.Y || p.Y > _max.Y)
            return false;

        for (var i = 0; i + 1 < _ribs.Count; i++)
        {
            var (l0, r0) = _ribs[i];
            var (l1, r1) = _ribs[i + 1];
            // Quad l0 → r0 → r1 → l1, split into two triangles (ribs are near-parallel ⇒ convex enough).
            if (PointInTriangle(p, l0, r0, r1) || PointInTriangle(p, l0, r1, l1))
                return true;
        }

        return false;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var d1 = Sign(p, a, b);
        var d2 = Sign(p, b, c);
        var d3 = Sign(p, c, a);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos); // all same sign (or on an edge) ⇒ inside

        static float Sign(Vector2 p, Vector2 a, Vector2 b) =>
            (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
    }
}
