using System;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     Edge/centerline desync guard (see
///     ai_docs/no_blend_zones/2026-05-31-edge-elevation-desync-bug.md).
///     Several no-blend passes move a cross-section's centerline <see cref="UnifiedCrossSection.TargetElevation" />
///     after the last edge re-derivation (blender Step 4) ran — most notably the post-loop affine §3
///     <c>RetargetTerminatingRoadsToSettledThrough</c>, which tilts a whole through road (moving its
///     mid-spline junction CSes) but never recomputes <see cref="UnifiedCrossSection.LeftEdgeElevation" /> /
///     <see cref="UnifiedCrossSection.RightEdgeElevation" />. The painted core reads the centerline while the
///     shoulder/embankment reads the stale edges → a step at the road edge (the raised-shoulder / berm look).
///     <c>ReconcileEdgeElevationsToCenterline</c> is the single final pass that re-derives every
///     non-roundabout CS's edges from its CURRENT (TargetElevation, BankAngleRadians).
/// </summary>
public class EdgeElevationReconciliationTests
{
    private static UnifiedCrossSection Cs(
        float target, float bank, float width, float leftEdge, float rightEdge,
        bool roundaboutBlended = false) => new()
    {
        OwnerSplineId = 1,
        CenterPoint = new Vector2(0f, 0f),
        TangentDirection = new Vector2(1f, 0f),
        NormalDirection = new Vector2(0f, 1f),
        TargetElevation = target,
        BankAngleRadians = bank,
        EffectiveRoadWidth = width,
        SurfaceWidth = width,
        LeftEdgeElevation = leftEdge,
        RightEdgeElevation = rightEdge,
        IsRoundaboutBlended = roundaboutBlended
    };

    private static UnifiedRoadNetwork NetworkWith(params UnifiedCrossSection[] css)
    {
        var network = new UnifiedRoadNetwork();
        foreach (var cs in css) network.AddCrossSection(cs);
        return network;
    }

    [Fact]
    public void StaleEdges_ReDerivedFromCurrentCenterlineAndBank()
    {
        // Centerline moved up to 198.28 (by §3 affine) but edges still read the old, lower ~196.66 values.
        // bank +1.8° → halfW·sin(bank). width 8 → halfW 4.
        var bank = 1.8f * MathF.PI / 180f;
        var cs = Cs(target: 198.28f, bank: bank, width: 8f, leftEdge: 196.50f, rightEdge: 196.81f);
        var network = NetworkWith(cs);

        var fixed_ = UnifiedRoadSmoother.ReconcileEdgeElevationsToCenterline(network);

        var delta = 4f * MathF.Sin(bank);
        Assert.Equal(1, fixed_);
        Assert.Equal(198.28f - delta, cs.LeftEdgeElevation, 3);
        Assert.Equal(198.28f + delta, cs.RightEdgeElevation, 3);
        // Edges now straddle the centerline (no 1.6 m desync).
        Assert.Equal(198.28f, (cs.LeftEdgeElevation + cs.RightEdgeElevation) / 2f, 3);
    }

    [Fact]
    public void AlreadyConsistentEdges_AreIdempotent()
    {
        // A §4/ramp-refreshed CS already has edges = centerline ± halfW·sin(bank); the pass must not move them.
        var bank = 2f * MathF.PI / 180f;
        var delta = 3f * MathF.Sin(bank); // width 6 → halfW 3
        var cs = Cs(target: 100f, bank: bank, width: 6f, leftEdge: 100f - delta, rightEdge: 100f + delta);
        var network = NetworkWith(cs);

        UnifiedRoadSmoother.ReconcileEdgeElevationsToCenterline(network);

        Assert.Equal(100f - delta, cs.LeftEdgeElevation, 4);
        Assert.Equal(100f + delta, cs.RightEdgeElevation, 4);
    }

    [Fact]
    public void RoundaboutBlendedCrossSection_LeftUntouched()
    {
        // Roundabout-blended edges are authoritative from Phase 2.6 — the pass must skip them.
        var cs = Cs(target: 50f, bank: 0.05f, width: 10f, leftEdge: 47.3f, rightEdge: 48.9f,
            roundaboutBlended: true);
        var network = NetworkWith(cs);

        var fixed_ = UnifiedRoadSmoother.ReconcileEdgeElevationsToCenterline(network);

        Assert.Equal(0, fixed_);
        Assert.Equal(47.3f, cs.LeftEdgeElevation, 4);
        Assert.Equal(48.9f, cs.RightEdgeElevation, 4);
    }

    [Fact]
    public void NaNCenterline_Skipped()
    {
        var cs = Cs(target: float.NaN, bank: 0.05f, width: 8f, leftEdge: 10f, rightEdge: 11f);
        var network = NetworkWith(cs);

        var fixed_ = UnifiedRoadSmoother.ReconcileEdgeElevationsToCenterline(network);

        Assert.Equal(0, fixed_);
        Assert.Equal(10f, cs.LeftEdgeElevation, 4);
        Assert.Equal(11f, cs.RightEdgeElevation, 4);
    }
}
