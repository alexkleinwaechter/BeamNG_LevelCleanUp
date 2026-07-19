using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     V2 typed-budget LOW-CLEARANCE diagnostic (render #4 follow-up): the legacy <c>[BRIDGE-PROFILE]</c>
///     warn compared the deck against the NATURAL DEM and the 5 m constant — in typed mode terrain is not
///     an obstacle (the excavator shaves what pokes above the deck), so a graded deck legitimately reported
///     <c>minClear=-1.0</c> while the resolver's dip-as-pin verify said everything cleared. Obstacle typing
///     is unconditional (doc 17 §4a), so the warn measures each planned crossing against its
///     <see cref="CrossingPlan.RequiredSeparationMeters"/> using FINAL solved surfaces (the lower road's Z
///     includes its dip-as-pin well).
/// </summary>
public class BridgeProfileTypedClearanceTests
{
    private const float ApproachZ = 100f;
    private const float PinZ = 110f;

    /// <summary>
    ///     40 m corridor along +x: road [0,15) – pinned bridge span [15,25] – road (25,40], deck held at
    ///     110 m. A perpendicular lower road crosses under the span at (20,0) with the given final Z.
    /// </summary>
    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> upperCs, GradeSeparatedCrossing crossing)
        BuildCrossedCorridor(float lowerFinalZ = float.NaN)
    {
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Bridge,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 777L },
        };
        var upper = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            isBridge: false, excludeBridges: true, excludeTunnels: true,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        upper.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnablePinnedDeckProfile = true,
        };

        var upperCs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, upper, crossSectionSpacing: 1f);
        foreach (var c in upperCs)
        {
            var inSpan = c.DistanceAlongSpline >= 15f && c.DistanceAlongSpline <= 25f;
            if (inSpan)
            {
                c.StructureSpanId = seg.SpanId;
                c.StructureSpanType = seg.Type;
                c.IsExcluded = true;
                c.PinnedElevation = PinZ;
                c.TargetElevation = PinZ;
            }
            else
            {
                c.TargetElevation = ApproachZ;
            }
        }

        var hasLowerSpline = !float.IsNaN(lowerFinalZ);
        if (hasLowerSpline)
        {
            var lower = RoadNetworkTestHelpers.CreateParameterizedSpline(
                splineId: 2, start: new Vector2(20, -20), end: new Vector2(20, 20));
            var lowerCs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, lower, crossSectionSpacing: 1f);
            foreach (var c in lowerCs)
                c.TargetElevation = lowerFinalZ; // the FINAL solved Z (incl. any dip-as-pin well)
        }

        var crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = 1,
            LowerSplineId = hasLowerSpline ? 2 : -1,
            CrossingXY = new Vector2(20, 0),
            UpperLayer = 1,
            LowerLayer = 0,
            UpperPriority = 8000,
            LowerPriority = 7500,
            UpperIsBridge = true,
            LowerIsBridge = false,
        };

        return (network, upperCs, crossing);
    }

    private static void StashPlan(UnifiedRoadNetwork network, params CrossingPlan[] crossings)
    {
        network.BridgeElevationPlan = new BridgeElevationPlan { Crossings = crossings };
    }

    [Fact]
    public void TypedMode_PlannedCrossingShort_WarnsAgainstTypedBudget()
    {
        // Lower road's final Z gives 5.0 m daylight; the typed budget demands 6.0 m → warn.
        var (network, _, crossing) = BuildCrossedCorridor(lowerFinalZ: 105f);
        StashPlan(network, new CrossingPlan
        {
            Crossing = crossing,
            Action = BridgeElevationAction.RaiseBridge,
            RequiredSeparationMeters = 6f,
        });

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.Equal(BridgeProfileSolver.BridgeProfileCurve.Pinned, app.Curve);
        Assert.Equal(1, app.PlanCrossingsChecked);
        Assert.Equal(5f, app.PlanMinClearanceMeters, 0.01f);
        Assert.Equal(6f, app.PlanRequiredSeparationMeters, 0.01f);
        Assert.Contains("LOW CLEARANCE (typed)", app.Note);
    }

    [Fact]
    public void TypedMode_DeckBelowNaturalTerrain_NoFalseLowClearanceWarn()
    {
        // The render-#4 394 repro: natural terrain pokes 1 m ABOVE the deck mid-span (minClear=-1.0) but
        // the planned crossing clears its typed budget — terrain is not an obstacle in typed mode (the
        // excavator shaves it), so NO warn may fire.
        var (network, upperCs, crossing) = BuildCrossedCorridor(lowerFinalZ: 100f);
        var mid = upperCs.First(c => Math.Abs(c.DistanceAlongSpline - 20f) < 0.5f);
        mid.OriginalTerrainElevation = PinZ + 1f;
        StashPlan(network, new CrossingPlan
        {
            Crossing = crossing,
            Action = BridgeElevationAction.AlreadyClears,
            RequiredSeparationMeters = 6f,
        });

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.Equal(-1f, app.MinClearanceMeters, 0.01f); // the misleading terrain metric, kept informative
        Assert.Equal(10f, app.PlanMinClearanceMeters, 0.01f); // the honest one
        Assert.DoesNotContain("LOW CLEARANCE", app.Note);
    }

    [Fact]
    public void TypedMode_SyntheticRailCrossing_MeasuresAgainstObstacleEstimate()
    {
        // Rail/water crossings have no lower spline — the diagnostic falls back to the planner's obstacle
        // estimate. Rail top at 106, deck 110, electrified budget 6 → 4.0 m < 6.0 m → warn.
        var (network, _, crossing) = BuildCrossedCorridor();
        StashPlan(network, new CrossingPlan
        {
            Crossing = crossing,
            Action = BridgeElevationAction.RaiseBridge,
            RequiredSeparationMeters = 6f,
            ObstacleZEstimate = 106f,
        });

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.Equal(1, app.PlanCrossingsChecked);
        Assert.Equal(4f, app.PlanMinClearanceMeters, 0.01f);
        Assert.Equal(6f, app.PlanRequiredSeparationMeters, 0.01f);
        Assert.Contains("LOW CLEARANCE (typed)", app.Note); // note text is current-culture formatted
    }

    [Fact]
    public void TypedMode_NoPlanCrossingsUnderSpan_NoWarnAtAll()
    {
        // Typed mode with an empty plan (e.g. pure terrain bridge): the legacy terrain warn must NOT
        // resurface — terrain is simply not an obstacle.
        var (network, upperCs, _) = BuildCrossedCorridor(lowerFinalZ: 100f);
        var mid = upperCs.First(c => Math.Abs(c.DistanceAlongSpline - 20f) < 0.5f);
        mid.OriginalTerrainElevation = PinZ + 2f;
        network.BridgeElevationPlan = new BridgeElevationPlan();

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications);
        Assert.Equal(0, app.PlanCrossingsChecked);
        Assert.DoesNotContain("LOW CLEARANCE", app.Note);
    }
}
