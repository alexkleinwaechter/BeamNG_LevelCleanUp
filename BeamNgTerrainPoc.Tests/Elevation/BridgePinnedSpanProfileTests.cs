using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     V2 plan A6.5 (review P0-1, doc 16 §3 headline bug): when the bridge planner pinned a span's deck and the
///     smoother hard-held it, <see cref="BridgeProfileSolver.RefineSpans"/> must NOT re-curve the deck from the
///     (lower) approach anchors — that re-fit is exactly what sagged the pinned ~16 m viaduct back to a 13.5 m
///     chord. With <c>EnablePinnedDeckProfile</c> the override is skipped (deck kept, edges + snapshot still
///     produced); with the flag off the legacy re-curve behaviour is byte-identical.
/// </summary>
public class BridgePinnedSpanProfileTests
{
    private const float ApproachZ = 100f;
    private const float PinZ = 110f; // an elevated viaduct deck, well above the approaches

    /// <summary>
    ///     40 m corridor: road [0,15) – bridge span [15,25] – road (25,40]. Approaches flat at 100 m; the span
    ///     sections were pinned by the planner at 110 m and the smoother held them there.
    /// </summary>
    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> cs) BuildPinnedCorridor(
        bool enablePinnedDeckProfile)
    {
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 777L },
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnablePinnedDeckProfile = enablePinnedDeckProfile,
        };

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);
        foreach (var c in cs)
        {
            var inSpan = c.DistanceAlongSpline >= 15f && c.DistanceAlongSpline <= 25f;
            if (inSpan)
            {
                c.StructureSpanId = seg.SpanId;
                c.IsExcluded = true;
                c.PinnedElevation = PinZ;
                c.TargetElevation = PinZ; // the smoother hard-held the pin
            }
            else
            {
                c.TargetElevation = ApproachZ;
            }
        }

        return (network, cs);
    }

    [Fact]
    public void FlagOn_PinnedSpanKeepsTheHeldDeck_NoSagToApproaches()
    {
        var (network, cs) = BuildPinnedCorridor(enablePinnedDeckProfile: true);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        Assert.Equal(BridgeProfileSolver.BridgeProfileCurve.Pinned, app.Curve);

        // The deck stands at the pin — the doc-16 §3 sag (re-fit to the 100 m approaches) must NOT happen.
        var spanCs = cs.Where(c => c.StructureSpanId >= 0).ToList();
        Assert.All(spanCs, c => Assert.Equal(PinZ, c.TargetElevation, 0.01f));

        // Edges recomputed from the held deck (flat banking here ⇒ edges == centerline).
        Assert.All(spanCs, c => Assert.Equal(PinZ, c.LeftEdgeElevation, 0.01f));

        // The snapshot is still captured for the deck exporter.
        var snapshot = Assert.Single(network.BridgeSpans);
        Assert.All(snapshot.Stations, s => Assert.Equal(PinZ, s.CenterZ, 0.01f));
    }

    [Fact]
    public void FlagOff_LegacyReCurve_DropsTheDeckToTheApproaches()
    {
        var (network, cs) = BuildPinnedCorridor(enablePinnedDeckProfile: false);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.True(app.Applied);
        Assert.NotEqual(BridgeProfileSolver.BridgeProfileCurve.Pinned, app.Curve);

        // Byte-identical legacy behaviour: the span is re-fitted between the 100 m approach anchors, so the
        // mid-span deck falls well below the 110 m pin (this IS the doc-16 bug, preserved behind the flag).
        var mid = cs.First(c => Math.Abs(c.DistanceAlongSpline - 20f) < 0.5f);
        Assert.True(mid.TargetElevation < PinZ - 5f,
            $"expected legacy re-curve to drop the deck toward the approaches, got {mid.TargetElevation:F2}");
    }

    [Fact]
    public void FlagOn_UnpinnedSpan_StillUsesTheNormalCurve()
    {
        // A span without planner pins must be untouched by A6.5 even when the flag is on.
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 778L },
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.BridgeRules = new BridgeRuleSystemOptions { EnablePinnedDeckProfile = true };

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);
        foreach (var c in cs)
        {
            var inSpan = c.DistanceAlongSpline >= 15f && c.DistanceAlongSpline <= 25f;
            if (inSpan)
            {
                c.StructureSpanId = seg.SpanId;
                c.IsExcluded = true;
            }

            c.TargetElevation = ApproachZ; // flat everywhere, no pins
        }

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.NotEqual(BridgeProfileSolver.BridgeProfileCurve.Pinned, app.Curve);
    }
}
