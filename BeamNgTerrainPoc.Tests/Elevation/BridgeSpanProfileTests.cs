using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Phase 4 of the "merged-corridor bridge" refactor (plan doc 11 §4.5): the structural-profile solver
///     re-homed onto interior spans. <see cref="BridgeProfileSolver.RefineSpans"/> must, when the
///     network carries cross-sections tagged with a <c>StructureSpanId</c>, override ONLY the span sections
///     with a G0+G1 curve fitted to the IN-SPLINE road neighbours (no junction walk), span a valley instead of
///     sagging, keep continuity with the road by construction, and capture a <see cref="BridgeSpanSnapshot"/>.
/// </summary>
public class BridgeSpanProfileTests
{
    /// <summary>
    ///     Builds a 40 m straight corridor: road [0,15) – bridge span [15,25] – road (25,40]. The road rises at
    ///     a constant 4% grade; the bridge sections' chain elevation sags 10 m into a valley. Returns the
    ///     network, the span's StructureSegment, and the ordered cross-sections.
    /// </summary>
    private static (UnifiedRoadNetwork network, StructureSegment seg, List<UnifiedCrossSection> cs) BuildValleyCorridor()
    {
        const float grade = 0.04f;
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 555L },
            OsmTags = new Dictionary<string, string> { ["bridge"] = "yes" }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        foreach (var c in cs)
        {
            var d = c.DistanceAlongSpline;
            var roadZ = 100f + grade * d;
            if (d >= 15f && d <= 25f)
            {
                c.StructureSpanId = seg.SpanId;
                c.IsExcluded = true;
                // Terrain-following valley: a parabola dipping 10 m below the road at mid-span.
                var t = (d - 15f) / 10f;
                c.TargetElevation = roadZ - 40f * t * (1f - t);
            }
            else
            {
                c.TargetElevation = roadZ;
            }
        }

        return (network, seg, cs);
    }

    [Fact]
    public void MergedSpan_SpansValley_NoSag_MatchesInSplineNeighbours()
    {
        const float grade = 0.04f;
        var (network, _, cs) = BuildValleyCorridor();

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        Assert.True(app.StartConnected);
        Assert.True(app.EndConnected);

        // Endpoints = the road sections just outside the span (d=14 and d=26 on the 4% line), grade = 4%.
        Assert.Equal(100f + grade * 14f, app.StartElevation, 0.2f);
        Assert.Equal(100f + grade * 26f, app.EndElevation, 0.2f);
        Assert.Equal(grade, app.StartGrade, 0.01f);
        Assert.Equal(grade, app.EndGrade, 0.01f);

        // The span no longer sags: every span section sits on (≈) the constant-grade chord, not the valley.
        var spanCs = cs.Where(c => c.DistanceAlongSpline >= 15f && c.DistanceAlongSpline <= 25f).ToList();
        foreach (var c in spanCs)
            Assert.Equal(100f + grade * c.DistanceAlongSpline, c.TargetElevation, 0.2f);

        // The valley (deepest ~91) is gone — minimum span Z is up on the road line.
        Assert.True(spanCs.Min(c => c.TargetElevation) > 100.5f);
    }

    [Fact]
    public void MergedSpan_IsContinuousWithTheRoadAcrossBothAbutments()
    {
        var (network, _, cs) = BuildValleyCorridor();
        BridgeProfileSolver.RefineSpans(network, log: false);

        var ordered = cs.OrderBy(c => c.DistanceAlongSpline).ToList();

        // No elevation step at either abutment: the jump from a road section to the adjacent span section is
        // only the local grade × spacing (~0.04 m), never a sag/kink. Check every road↔span neighbour pair.
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            var ds = cur.DistanceAlongSpline - prev.DistanceAlongSpline;
            var step = MathF.Abs(cur.TargetElevation - prev.TargetElevation);
            Assert.True(step <= 0.04f * ds + 0.05f,
                $"discontinuity at d={cur.DistanceAlongSpline:F1}: step={step:F3} over ds={ds:F2}");
        }
    }

    [Fact]
    public void MergedSpan_CapturesSnapshotWithFiniteStations()
    {
        var (network, seg, _) = BuildValleyCorridor();
        BridgeProfileSolver.RefineSpans(network, log: false);

        var snap = Assert.Single(network.BridgeSpans);
        Assert.Equal(1, snap.SplineId);
        Assert.Equal(seg.SpanId, snap.SpanId);
        Assert.Contains(555L, snap.OsmWayIds);
        Assert.NotNull(snap.OsmTags);
        Assert.Equal("yes", snap.OsmTags!["bridge"]);

        Assert.NotEmpty(snap.Stations);
        Assert.All(snap.Stations, st =>
        {
            Assert.True(float.IsFinite(st.CenterZ));
            Assert.True(float.IsFinite(st.LeftEdgeZ));
            Assert.True(float.IsFinite(st.RightEdgeZ));
            Assert.True(st.Width > 0f);
            Assert.InRange(st.DistanceAlongSpline, 15f, 25f);
        });

        // Stations carry the SOLVED (spanning) elevation, not the original valley sag.
        Assert.True(snap.Stations.Min(s => s.CenterZ) > 100.5f);
    }

    [Fact]
    public void LegacyMode_NoTaggedSpans_LeavesBridgeSpansEmpty()
    {
        // A separated whole-spline bridge (flag off): no StructureSpanId tags ⇒ the solver takes the legacy
        // junction-walk path and does NOT populate BridgeSpans.
        var network = new UnifiedRoadNetwork();
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(20, 0),
            isBridge: true, excludeBridges: true, excludeTunnels: true);
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge, crossSectionSpacing: 1f);
        foreach (var c in cs)
        {
            c.IsExcluded = true; // legacy: whole bridge spline excluded, but no StructureSpanId tag
            c.TargetElevation = 100f;
        }

        BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Empty(network.BridgeSpans);
    }
}
