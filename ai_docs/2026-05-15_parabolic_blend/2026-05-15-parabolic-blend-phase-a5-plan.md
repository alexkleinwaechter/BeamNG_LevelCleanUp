# Parabolic Junction Blend — Phase A.5 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Step 5b propagated-mid-spline-influence post-overlay from dragging a directly-anchored junction's blend-zone elevation away from its parabolic profile, by tapering propagated-influence weight to zero at the contested junction's anchor node.

**Architecture:** Phase A.5 changes only the **application** of `_propagatedMidSplineInfluences` in `UnifiedJunctionProfileBlender.ApplyUnifiedProfiles` Step 5b (around L243-272). For each CS that has both (a) a propagated mid-spline influence from junction X and (b) sits inside a *direct* junction Y's blend zone (Y ≠ X), the propagated influence's weight is multiplied by a smoothstep taper `t(d_Y/L_Y)` derived purely from blend-zone geometry. Taper = 0 at Y's anchor node (Y wins outright); taper = 1 at Y's blend-zone boundary (no contest, influence has full say). The taper is gated by a new `EnablePropagationOverlapTaper` flag (default false until validation passes). `BlendSplineProfile`, `BlendSplineProfileParabolic`, `CalculateAdaptiveBlendDistance`, and `JunctionBlendDistanceMeters` are untouched. The legacy first-writer-wins behaviour is preserved when the flag is off.

**Tech Stack:** .NET 9 (`net9.0-windows10.0.17763.0`), xUnit 2.x, BeamNgTerrainPoc + BeamNgTerrainPoc.Tests projects. Build sandboxed with `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`. Test with `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`.

**Roadmap context:** This plan is one phase of a larger program. See [2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md) for the full backlog. **Sequencing: A.5 runs AFTER A.8.** Phase A.8 ([phase-a8-plan](2026-05-15-parabolic-blend-phase-a8-plan.md)) addresses the rasterizer-stage override that hides A.5's blender-stage improvements. Validating A.5 before A.8 is in place will produce muted or null results — the parabolic-tapered `cs.TargetElevation` won't reach the heightmap if `RoadMaskBuilder` stomps over the terminating-road surface pixels. The A.5 validation snapshot (Task 6) must be captured with both `EnableSurfaceWidthProtection = true` (from A.8) AND `EnablePropagationOverlapTaper = true` (this plan). The baseline to compare against is `surface_protection_a8_franco_same_prio/`, NOT `parabolic_a_franco_same_prio/`.

---

## Why a taper, not a hard masking

The handoff doc and the franco_same_prio snapshot together pinpoint the j126 cliff to a single line of code at [UnifiedJunctionProfileBlender.cs:256-258](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L256-L258):

```csharp
var weightedElev = influences.Sum(inf => inf.elevation * inf.weight) / totalWeight;
var influence = MathF.Min(totalWeight, 1.0f);
var newElev = weightedElev * influence + cs.TargetElevation * (1f - influence);
```

`cs.TargetElevation` is the parabolic profile written by Pass 1/2 of `BlendSplineProfileParabolic`. The propagated influence — j102 talking to spline 64 through short spline 52 with `remainingBlend = 72.4 m` — has weights `1.0` at the crossing point and decays via `1 - smoothstep(d/72.4)` outward. Spline 64's last ~70 m are *both* inside the propagated influence's 72.4 m zone *and* inside j126's 100 m end blend zone. The current formula gives the propagated influence dominance there.

**Why a hard mask (drop the influence completely inside a direct zone) is wrong:** the j102 → spline 64 propagation exists for a real reason — j102's anchor on short spline 52 has to talk *to* spline 64 somehow, and at the *far* boundary of j126's zone (~100 m from j126's node) the j102 influence should still be visible because we're far from j126's claim. Hard-masking would create a step at the boundary.

**Why a smoothstep taper is right:** at distance d from j126's anchor along spline 64,

- `d = 0` (right at j126): taper = 0, j126's parabolic anchor wins. ✅
- `d = blendDist_j126 = 100`: taper = 1, propagated influence has full say. ✅
- Smooth C¹ transition in between → no kink, no boundary step.

Mathematically, the taper is `t(x) = x²·(3 − 2x)` for `x = clamp(d_Y / L_Y, 0, 1)`, applied per-influence as `weight_tapered = weight · t(d_Y / L_Y)`. This is the same smoothstep family the existing `CollectInfluencesFromCrossing` uses for its base weight curve (L1628, L1646), so we're not introducing new mathematics.

**Why j125 survives:** j125's start-zone occupies spline 64's first 100 m. j102 propagates through spline 52, attached on the *other* side of spline 64 (near j126). The propagated influence's weight at any CS in j125's start-zone (d_from_start < 100 m) is already 0 from the existing quintic smoothstep falloff in `CollectInfluencesFromCrossing` — the geometric distance from the crossing point to j125's start is ≫ 72.4 m. So Step 5b doesn't touch j125's zone at all, with or without taper. Phase A's 7.16σ → 2.75σ win is preserved by construction.

---

## File Structure

**Create:**
- `BeamNgTerrainPoc/Terrain/Algorithms/OverlapTaper.cs` — pure-function smoothstep helper.
- `BeamNgTerrainPoc/Terrain/Algorithms/SplineClaimedZones.cs` — per-spline lookup of "which junction claims this end and over what distance".
- `BeamNgTerrainPoc.Tests/Junction/OverlapTaperTests.cs` — unit tests for the smoothstep helper.
- `BeamNgTerrainPoc.Tests/Junction/SplineClaimedZonesTests.cs` — unit tests for the lookup builder.
- `BeamNgTerrainPoc.Tests/Junction/PropagationOverlapTaperTests.cs` — integration-ish tests covering Step 5b behaviour.

**Modify:**
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — add `EnablePropagationOverlapTaper` flag (default `false`).
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — add `_splineClaimedZones` field; populate it after `PropagateConstraintsThroughShortSplines`; replace Step 5b loop body to apply per-influence taper when flag is on.
- `examples_for_ai/baseline_phase19/README.md` — document the new `parabolic_a5_franco_same_prio` capture (Task 6).

**Do NOT modify:**
- `BlendSplineProfile` and `BlendSplineProfileParabolic` (handoff §"Hard constraints").
- `CollectInfluencesFromCrossing` (shared between Step 5 direct MidSplineCrossing and Step 5b propagation — changing it would couple unrelated paths).
- `PropagateConstraintsThroughShortSplines` core logic (the propagation discovery itself is correct; only its *application* needs tapering).
- `CalculateAdaptiveBlendDistance`, `JunctionBlendDistanceMeters`, `RoundaboutBlendDistanceMeters` defaults (Phase B).
- `FinalSnapTJunctionEndpoints` (spec §7.1, kept indefinitely).

---

### Task 1: Add parameter flag (no behaviour change yet)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

- [ ] **Step 1: Open file and locate `EnableParabolicJunctionBlend`**

Read [JunctionHarmonizationParameters.cs](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs) and find the line:

```csharp
public bool EnableParabolicJunctionBlend { get; set; } = true;
```

This was set to `true` in commit `1638ae2` (Phase A default flip).

- [ ] **Step 2: Insert new flag immediately below**

Append after the `EnableParabolicJunctionBlend` property:

```csharp
    /// <summary>
    ///     Phase A.5 — propagation/overlap taper. When true, propagated mid-spline
    ///     influences applied in <see cref="UnifiedJunctionProfileBlender.ApplyUnifiedProfiles" />
    ///     Step 5b are weight-tapered toward zero at any directly-anchored junction's
    ///     anchor node whose blend zone they overlap. Eliminates the j126-style cliff
    ///     where a propagated influence from a far-side junction overrides a
    ///     parabolic-blended end zone. Taper is C¹ smoothstep on the geometric
    ///     distance ratio; it never references terrain grade.
    ///     Default: false (opt-in until validation on franco_same_prio passes).
    /// </summary>
    public bool EnablePropagationOverlapTaper { get; set; } = false;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add EnablePropagationOverlapTaper flag (Phase A.5 scaffold)"
```

---

### Task 2: Create `OverlapTaper.Compute` smoothstep helper

**Mathematical contract:** `Compute(d, L)` returns the C¹ smoothstep `x²·(3 − 2x)` where `x = clamp(d/L, 0, 1)`.

- `d = 0` (at anchor) → returns `0`.
- `d = L` (at blend boundary) → returns `1`.
- `d > L` (outside zone) → returns `1` (no contest).
- `L ≤ 0` (degenerate) → returns `1` (no zone, no taper).

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/OverlapTaper.cs`
- Create: `BeamNgTerrainPoc.Tests/Junction/OverlapTaperTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `BeamNgTerrainPoc.Tests/Junction/OverlapTaperTests.cs`:

```csharp
using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class OverlapTaperTests
{
    [Fact]
    public void Compute_AtAnchor_ReturnsZero()
    {
        // d=0 → x=0 → smoothstep(0) = 0.
        var taper = OverlapTaper.Compute(distFromAnchor: 0f, blendLength: 30f);
        Assert.Equal(0f, taper, 4);
    }

    [Fact]
    public void Compute_AtBoundary_ReturnsOne()
    {
        // d=L → x=1 → smoothstep(1) = 1.
        var taper = OverlapTaper.Compute(distFromAnchor: 30f, blendLength: 30f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_AtMidPoint_ReturnsHalf()
    {
        // d=L/2 → x=0.5 → smoothstep(0.5) = 0.25 * (3 - 1) = 0.5.
        var taper = OverlapTaper.Compute(distFromAnchor: 15f, blendLength: 30f);
        Assert.Equal(0.5f, taper, 4);
    }

    [Fact]
    public void Compute_BeyondBoundary_ReturnsOne()
    {
        // d > L → clamp(d/L) = 1 → smoothstep(1) = 1.
        var taper = OverlapTaper.Compute(distFromAnchor: 100f, blendLength: 30f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_NegativeDistance_ReturnsZero()
    {
        // d < 0 → clamp(d/L) = 0 → smoothstep(0) = 0. Defensive against caller bugs.
        var taper = OverlapTaper.Compute(distFromAnchor: -5f, blendLength: 30f);
        Assert.Equal(0f, taper, 4);
    }

    [Fact]
    public void Compute_ZeroBlendLength_ReturnsOne()
    {
        // L=0 → no zone exists → no taper. Avoid divide-by-zero.
        var taper = OverlapTaper.Compute(distFromAnchor: 0f, blendLength: 0f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_NegativeBlendLength_ReturnsOne()
    {
        // L<0 is malformed input → defensive: behave as no zone.
        var taper = OverlapTaper.Compute(distFromAnchor: 5f, blendLength: -10f);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void Compute_Monotone_NonDecreasing()
    {
        // As d increases from 0 to L, taper must monotonically increase from 0 to 1.
        var prev = -1f;
        for (var d = 0f; d <= 30f; d += 0.5f)
        {
            var t = OverlapTaper.Compute(d, 30f);
            Assert.True(t >= prev, $"d={d}: t={t} < prev={prev}");
            prev = t;
        }
    }

    [Fact]
    public void Compute_C1AtEndpoints_NumericalDerivativeIsZero()
    {
        // Smoothstep has zero derivative at both endpoints (C¹). Numerical check.
        var eps = 0.001f;

        var t0 = OverlapTaper.Compute(0f, 30f);
        var tEps = OverlapTaper.Compute(eps, 30f);
        Assert.True(MathF.Abs(tEps - t0) < 0.001f,
            $"Derivative at d=0 should be ~0; observed (t(eps)-t(0))/eps = {(tEps - t0) / eps}");

        var tL = OverlapTaper.Compute(30f, 30f);
        var tLMinusEps = OverlapTaper.Compute(30f - eps, 30f);
        Assert.True(MathF.Abs(tL - tLMinusEps) < 0.001f,
            $"Derivative at d=L should be ~0; observed (t(L)-t(L-eps))/eps = {(tL - tLMinusEps) / eps}");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~OverlapTaperTests"`
Expected: FAIL — `OverlapTaper` type does not exist.

- [ ] **Step 3: Implement the helper**

Create `BeamNgTerrainPoc/Terrain/Algorithms/OverlapTaper.cs`:

```csharp
namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A.5 propagation-overlap taper. Pure helper used by
///     <see cref="UnifiedJunctionProfileBlender" /> Step 5b to attenuate
///     propagated-mid-spline-influence weights inside a directly-anchored
///     junction's blend zone. Returns 0 at the junction anchor node and 1 at
///     the blend-zone boundary, with C¹ smoothstep transition in between.
///     Outside the zone the taper is 1 (no contest). Geometric only — never
///     consults terrain elevation or grade.
/// </summary>
public static class OverlapTaper
{
    /// <summary>
    ///     Computes smoothstep(clamp(distFromAnchor / blendLength, 0, 1)).
    /// </summary>
    /// <param name="distFromAnchor">Distance from the contested junction's anchor node along the spline (m).</param>
    /// <param name="blendLength">The contested junction's blend distance (m).</param>
    /// <returns>0 at anchor, 1 at boundary, monotone smoothstep in between; 1 outside the zone or for non-positive blendLength.</returns>
    public static float Compute(float distFromAnchor, float blendLength)
    {
        if (blendLength <= 0.0001f)
            return 1f;

        var x = MathF.Max(0f, MathF.Min(distFromAnchor / blendLength, 1f));
        return x * x * (3f - 2f * x);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~OverlapTaperTests"`
Expected: PASS, 9/9 green.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/OverlapTaper.cs BeamNgTerrainPoc.Tests/Junction/OverlapTaperTests.cs
git commit -m "feat: add OverlapTaper.Compute smoothstep helper with TDD coverage"
```

---

### Task 3: Create `SplineClaimedZones` lookup

**Purpose:** A per-spline lookup of "which direct (non-propagated-mid-spline) junctions claim each end and how far inward". Built once after `PropagateConstraintsThroughShortSplines` from the same `constraints` dictionary the blender already consumes. Step 5b queries this per-CS to decide whether to taper a propagated influence.

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/SplineClaimedZones.cs`
- Create: `BeamNgTerrainPoc.Tests/Junction/SplineClaimedZonesTests.cs`

**Data model:** for each spline `s`,

```csharp
SplineEndClaim {
    int JunctionId;            // owner of this claim
    float BlendDistanceMeters; // L_Y in the taper formula
}

SplineClaimedZone {
    int SplineId;
    float RoadLength;          // for end-anchor distance math
    SplineEndClaim? StartClaim;  // null if no direct constraint at start
    SplineEndClaim? EndClaim;    // null if no direct constraint at end
    Dictionary<int, float> DistFromStartByCsIndex; // precomputed for O(1) lookup
}
```

The choice "either StartClaim or EndClaim, not arbitrary mid-spline claims" is correct because direct (anchored) junction constraints only ever attach at spline endpoints — `JunctionEndpointConstraint.IsSplineStart` is the binary discriminator. Mid-spline crossings (Step 5) are separate from anchored junctions and don't show up in `constraints`.

- [ ] **Step 1: Write the failing test file**

Create `BeamNgTerrainPoc.Tests/Junction/SplineClaimedZonesTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class SplineClaimedZonesTests
{
    private static List<UnifiedCrossSection> BuildLinearSpline(int id, int n, float spacing)
    {
        var sections = new List<UnifiedCrossSection>();
        for (var i = 0; i < n; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                Index = id * 1000 + i,
                LocalIndex = i,
                OwnerSplineId = id,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            });
        }
        return sections;
    }

    [Fact]
    public void Build_SplineWithStartAndEndConstraints_BothClaimsPopulated()
    {
        var sections = BuildLinearSpline(id: 64, n: 100, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (64, true), new JunctionEndpointConstraint
                {
                    Elevation = 184.4f, Slope = -0.084f, IsSplineStart = true,
                    BlendDistanceMeters = 100f,
                    Junction = new NetworkJunction { JunctionId = 125 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            },
            {
                (64, false), new JunctionEndpointConstraint
                {
                    Elevation = 158.98f, Slope = 0.0011f, IsSplineStart = false,
                    BlendDistanceMeters = 100f,
                    Junction = new NetworkJunction { JunctionId = 126 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.True(zones.TryGetValue(64, out var zone));
        Assert.Equal(99f, zone!.RoadLength, 2); // 100 CSes at 1m spacing = 99m total
        Assert.NotNull(zone.StartClaim);
        Assert.Equal(125, zone.StartClaim!.JunctionId);
        Assert.Equal(100f, zone.StartClaim.BlendDistanceMeters, 2);
        Assert.NotNull(zone.EndClaim);
        Assert.Equal(126, zone.EndClaim!.JunctionId);
        Assert.Equal(100f, zone.EndClaim.BlendDistanceMeters, 2);
    }

    [Fact]
    public void Build_StartOnly_EndClaimNull()
    {
        var sections = BuildLinearSpline(id: 7, n: 50, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 7, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (7, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 1 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.True(zones.TryGetValue(7, out var zone));
        Assert.NotNull(zone!.StartClaim);
        Assert.Null(zone.EndClaim);
    }

    [Fact]
    public void Build_NoConstraintsForSpline_SplineMissingFromResult()
    {
        var sections = BuildLinearSpline(id: 99, n: 10, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 99, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>();

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.False(zones.ContainsKey(99));
    }

    [Fact]
    public void Build_DistFromStartByCsIndex_MatchesCumulativeCenterPointDistances()
    {
        var sections = BuildLinearSpline(id: 5, n: 4, spacing: 2.5f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 5, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (5, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 10f,
                    Junction = new NetworkJunction { JunctionId = 1 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var zone = zones[5];

        Assert.Equal(0f, zone.DistFromStartByCsIndex[5 * 1000 + 0], 3);
        Assert.Equal(2.5f, zone.DistFromStartByCsIndex[5 * 1000 + 1], 3);
        Assert.Equal(5.0f, zone.DistFromStartByCsIndex[5 * 1000 + 2], 3);
        Assert.Equal(7.5f, zone.DistFromStartByCsIndex[5 * 1000 + 3], 3);
        Assert.Equal(7.5f, zone.RoadLength, 3);
    }

    [Fact]
    public void Build_PropagatedConstraintsAreIncluded()
    {
        // A propagated endpoint constraint (IsPropagated=true) is still anchored at the
        // spline's endpoint with a real blend distance. Treat it as a direct claim for
        // taper purposes — the taper attenuates propagated MID-SPLINE influences only,
        // and endpoint-anchored claims (direct or propagated) are equally "anchored".
        var sections = BuildLinearSpline(id: 12, n: 20, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 12, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (12, false), new JunctionEndpointConstraint
                {
                    Elevation = 50f, Slope = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 12f,
                    Junction = new NetworkJunction { JunctionId = 42 },
                    PrimaryTangentDirection = new Vector2(1f, 0f),
                    IsPropagated = true,
                    PropagatedThroughSplineId = 11
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.NotNull(zones[12].EndClaim);
        Assert.Equal(42, zones[12].EndClaim!.JunctionId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~SplineClaimedZonesTests"`
Expected: FAIL — `SplineClaimedZones` type does not exist.

- [ ] **Step 3: Implement the lookup builder**

Create `BeamNgTerrainPoc/Terrain/Algorithms/SplineClaimedZones.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A.5 — per-spline lookup of which directly-anchored junctions claim
///     each end and over what blend distance. Built once after constraint
///     propagation completes; consumed by Step 5b to taper propagated mid-spline
///     influence weights inside a contested claim's zone.
/// </summary>
public sealed class SplineClaimedZone
{
    public required int SplineId { get; init; }
    public required float RoadLength { get; init; }
    public SplineEndClaim? StartClaim { get; init; }
    public SplineEndClaim? EndClaim { get; init; }
    public required Dictionary<int, float> DistFromStartByCsIndex { get; init; }
}

public sealed class SplineEndClaim
{
    public required int JunctionId { get; init; }
    public required float BlendDistanceMeters { get; init; }
}

public static class SplineClaimedZones
{
    /// <summary>
    ///     Build the per-spline claimed-zones lookup from the constraints dictionary
    ///     produced by ComputeAllJunctionConstraints + PropagateConstraintsThroughShortSplines.
    /// </summary>
    public static Dictionary<int, SplineClaimedZone> Build(
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var result = new Dictionary<int, SplineClaimedZone>();

        // Collect every splineId that has at least one constraint
        var claimedSplineIds = new HashSet<int>();
        foreach (var key in constraints.Keys)
            claimedSplineIds.Add(key.splineId);

        foreach (var splineId in claimedSplineIds)
        {
            if (!crossSectionsBySpline.TryGetValue(splineId, out var sections) || sections.Count < 2)
                continue;

            var distFromStart = new Dictionary<int, float>(sections.Count);
            distFromStart[sections[0].Index] = 0f;
            var cumulative = 0f;
            for (var i = 1; i < sections.Count; i++)
            {
                cumulative += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
                distFromStart[sections[i].Index] = cumulative;
            }

            SplineEndClaim? startClaim = null;
            if (constraints.TryGetValue((splineId, true), out var startC))
                startClaim = new SplineEndClaim
                {
                    JunctionId = startC.Junction?.JunctionId ?? 0,
                    BlendDistanceMeters = startC.BlendDistanceMeters
                };

            SplineEndClaim? endClaim = null;
            if (constraints.TryGetValue((splineId, false), out var endC))
                endClaim = new SplineEndClaim
                {
                    JunctionId = endC.Junction?.JunctionId ?? 0,
                    BlendDistanceMeters = endC.BlendDistanceMeters
                };

            result[splineId] = new SplineClaimedZone
            {
                SplineId = splineId,
                RoadLength = cumulative,
                StartClaim = startClaim,
                EndClaim = endClaim,
                DistFromStartByCsIndex = distFromStart
            };
        }

        return result;
    }

    /// <summary>
    ///     For a given CS on a claimed spline, returns the strongest applicable
    ///     overlap taper to apply to a propagated influence from
    ///     <paramref name="sourceJunctionId" />. Returns 1 (no taper) when the CS
    ///     sits outside any claim, or when the only contested claim belongs to the
    ///     same junction as the propagated source.
    /// </summary>
    public static float GetTaperFor(
        SplineClaimedZone zone,
        int csIndex,
        int sourceJunctionId)
    {
        if (!zone.DistFromStartByCsIndex.TryGetValue(csIndex, out var d)) return 1f;

        var taper = 1f;

        if (zone.StartClaim != null && zone.StartClaim.JunctionId != sourceJunctionId)
        {
            var distFromStartAnchor = d; // start anchor is at d=0
            if (distFromStartAnchor < zone.StartClaim.BlendDistanceMeters)
            {
                var startTaper = OverlapTaper.Compute(distFromStartAnchor, zone.StartClaim.BlendDistanceMeters);
                if (startTaper < taper) taper = startTaper;
            }
        }

        if (zone.EndClaim != null && zone.EndClaim.JunctionId != sourceJunctionId)
        {
            var distFromEndAnchor = zone.RoadLength - d; // end anchor is at d=RoadLength
            if (distFromEndAnchor < zone.EndClaim.BlendDistanceMeters)
            {
                var endTaper = OverlapTaper.Compute(distFromEndAnchor, zone.EndClaim.BlendDistanceMeters);
                if (endTaper < taper) taper = endTaper;
            }
        }

        return taper;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~SplineClaimedZonesTests"`
Expected: PASS, 5/5 green.

- [ ] **Step 5: Add `GetTaperFor` unit tests**

Append to `SplineClaimedZonesTests.cs`:

```csharp
[Fact]
public void GetTaperFor_CsAtStartAnchor_DifferentJunction_ReturnsZero()
{
    var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
    var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
    var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
    {
        {
            (1, true), new JunctionEndpointConstraint
            {
                Elevation = 100f, Slope = 0f, IsSplineStart = true,
                BlendDistanceMeters = 30f,
                Junction = new NetworkJunction { JunctionId = 7 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        }
    };

    var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
    var csIndex = sections[0].Index;

    var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
    Assert.Equal(0f, taper, 4);
}

[Fact]
public void GetTaperFor_CsAtStartAnchor_SameJunction_ReturnsOne()
{
    var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
    var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
    var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
    {
        {
            (1, true), new JunctionEndpointConstraint
            {
                Elevation = 100f, Slope = 0f, IsSplineStart = true,
                BlendDistanceMeters = 30f,
                Junction = new NetworkJunction { JunctionId = 7 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        }
    };

    var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
    var csIndex = sections[0].Index;

    var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 7);
    Assert.Equal(1f, taper, 4);
}

[Fact]
public void GetTaperFor_CsOutsideAnyZone_ReturnsOne()
{
    // 100 CSes, blend zones at start=20m and end=20m. CS at index 50 (d=50m) is outside both.
    var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
    var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
    var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
    {
        {
            (1, true), new JunctionEndpointConstraint
            {
                Elevation = 100f, Slope = 0f, IsSplineStart = true,
                BlendDistanceMeters = 20f,
                Junction = new NetworkJunction { JunctionId = 7 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        },
        {
            (1, false), new JunctionEndpointConstraint
            {
                Elevation = 90f, Slope = 0f, IsSplineStart = false,
                BlendDistanceMeters = 20f,
                Junction = new NetworkJunction { JunctionId = 8 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        }
    };

    var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
    var csIndex = sections[50].Index;

    var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
    Assert.Equal(1f, taper, 4);
}

[Fact]
public void GetTaperFor_CsInBothZones_TakesMinimum()
{
    // Short spline (10 CSes at 1m): both start blend (8m) and end blend (8m) overlap at CS 5.
    var sections = BuildLinearSpline(id: 1, n: 10, spacing: 1f);
    var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
    var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
    {
        {
            (1, true), new JunctionEndpointConstraint
            {
                Elevation = 100f, Slope = 0f, IsSplineStart = true,
                BlendDistanceMeters = 8f,
                Junction = new NetworkJunction { JunctionId = 1 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        },
        {
            (1, false), new JunctionEndpointConstraint
            {
                Elevation = 90f, Slope = 0f, IsSplineStart = false,
                BlendDistanceMeters = 8f,
                Junction = new NetworkJunction { JunctionId = 2 },
                PrimaryTangentDirection = new Vector2(1f, 0f)
            }
        }
    };

    var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
    // CS at index 2 (d=2m): inside start zone (d=2, L=8 → taper(0.25)), outside end zone (distFromEnd=7, L=8 → taper(0.875)).
    // min of those two ≈ 0.15625 (smoothstep(0.25) = 0.0625 * (3-0.5) = 0.15625)
    var csIndex = sections[2].Index;
    var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
    Assert.Equal(0.15625f, taper, 4);
}
```

- [ ] **Step 6: Run tests to verify all pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~SplineClaimedZonesTests"`
Expected: PASS, 9/9 green.

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/SplineClaimedZones.cs BeamNgTerrainPoc.Tests/Junction/SplineClaimedZonesTests.cs
git commit -m "feat: add SplineClaimedZones lookup + GetTaperFor (Phase A.5)"
```

---

### Task 4: Wire taper into `ApplyUnifiedProfiles` Step 5b

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

- [ ] **Step 1: Add private field**

In the class field block at the top (around L23-30, near `_currentCrossSectionsBySpline` and `_propagatedMidSplineInfluences`), add:

```csharp
    /// <summary>
    ///     Phase A.5 — per-spline claimed-zones lookup, built once after constraint
    ///     propagation. Used by Step 5b to taper propagated mid-spline influences
    ///     inside contested directly-anchored junction blend zones. Cleared at the
    ///     end of <see cref="ApplyUnifiedProfiles" /> alongside _propagatedMidSplineInfluences.
    /// </summary>
    private Dictionary<int, SplineClaimedZone>? _splineClaimedZones;
```

- [ ] **Step 2: Populate the field after propagation**

In `ApplyUnifiedProfiles`, find the block at [UnifiedJunctionProfileBlender.cs:63-70](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L63-L70):

```csharp
        var constraints = ComputeAllJunctionConstraints(network, crossSectionsBySpline, heightMap, metersPerPixel);

        // Propagation pass: find short splines and extend constraints into neighboring splines
        _currentCrossSectionsBySpline = crossSectionsBySpline;
        PropagateConstraintsThroughShortSplines(constraints, network);
        _currentCrossSectionsBySpline = null;

        result.ConstraintsComputed = constraints.Count;
```

Append immediately after this block (before the `if (constraints.Count == 0)` check on L72), gated on the flag:

```csharp
        // Phase A.5: build per-spline claimed-zones lookup for Step 5b taper.
        // Only built when the feature flag is on AND there are propagation candidates
        // (no propagated influences ⇒ nothing to taper).
        if (jhParams.EnablePropagationOverlapTaper && _propagatedMidSplineInfluences is { Count: > 0 })
        {
            _splineClaimedZones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        }
```

- [ ] **Step 3: Replace the Step 5b body**

Find [UnifiedJunctionProfileBlender.cs:243-272](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L243-L272) — the current `if (_propagatedMidSplineInfluences is { Count: > 0 })` block. Replace the body so the per-influence weights are tapered when `_splineClaimedZones` is non-null:

Replace:

```csharp
        // Step 5b: Apply propagated mid-spline influences from short-segment propagation.
        // These nudge continuous roads near T-junctions where short terminating roads
        // couldn't accommodate their blend zones (e.g., roundabout → short entry → main road).
        if (_propagatedMidSplineInfluences is { Count: > 0 })
        {
            var propagatedModified = 0;
            foreach (var (csIndex, influences) in _propagatedMidSplineInfluences)
            {
                var cs = network.CrossSections.FirstOrDefault(c => c.Index == csIndex);
                if (cs == null || float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
                    continue;

                var totalWeight = influences.Sum(inf => inf.weight);
                if (totalWeight < 0.001f)
                    continue;

                var weightedElev = influences.Sum(inf => inf.elevation * inf.weight) / totalWeight;
                var influence = MathF.Min(totalWeight, 1.0f);
                var newElev = weightedElev * influence + cs.TargetElevation * (1f - influence);

                if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
                {
                    cs.TargetElevation = newElev;
                    propagatedModified++;
                }
            }

            if (propagatedModified > 0)
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Applied {propagatedModified} propagated mid-spline influences on continuous roads");

            _propagatedMidSplineInfluences = null;
        }
```

with:

```csharp
        // Step 5b: Apply propagated mid-spline influences from short-segment propagation.
        // These nudge continuous roads near T-junctions where short terminating roads
        // couldn't accommodate their blend zones (e.g., roundabout → short entry → main road).
        // Phase A.5: when EnablePropagationOverlapTaper is on and the CS sits inside a
        // directly-anchored junction's blend zone (and that junction != the influence's
        // source junction), the per-influence weight is multiplied by a smoothstep taper
        // → 0 at the contested anchor, 1 at the contested-zone boundary. Prevents a
        // propagated influence from overriding a parabolic blend's edge anchor.
        if (_propagatedMidSplineInfluences is { Count: > 0 })
        {
            var propagatedModified = 0;
            var taperApplied = 0;
            foreach (var (csIndex, influences) in _propagatedMidSplineInfluences)
            {
                var cs = network.CrossSections.FirstOrDefault(c => c.Index == csIndex);
                if (cs == null || float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
                    continue;

                // Build per-influence weights, applying overlap taper if enabled.
                var totalWeight = 0f;
                var weightedElevSum = 0f;
                foreach (var inf in influences)
                {
                    var w = inf.weight;
                    if (_splineClaimedZones != null
                        && _splineClaimedZones.TryGetValue(cs.OwnerSplineId, out var zone))
                    {
                        var taper = SplineClaimedZones.GetTaperFor(zone, cs.Index, inf.junctionId);
                        if (taper < 0.9999f) taperApplied++;
                        w *= taper;
                    }

                    totalWeight += w;
                    weightedElevSum += inf.elevation * w;
                }

                if (totalWeight < 0.001f)
                    continue;

                var weightedElev = weightedElevSum / totalWeight;
                var influence = MathF.Min(totalWeight, 1.0f);
                var newElev = weightedElev * influence + cs.TargetElevation * (1f - influence);

                if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
                {
                    cs.TargetElevation = newElev;
                    propagatedModified++;
                }
            }

            if (propagatedModified > 0)
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Applied {propagatedModified} propagated mid-spline influences on continuous roads" +
                    (_splineClaimedZones != null ? $" (overlap-taper applied to {taperApplied} influences)" : ""));

            _propagatedMidSplineInfluences = null;
            _splineClaimedZones = null;
        }
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds. If `SplineClaimedZone` is unresolved, ensure the `using BeamNgTerrainPoc.Terrain.Algorithms;` is already present (it is — the file is in that namespace).

- [ ] **Step 5: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. The new flag is `false` by default, so existing behaviour is unchanged. New Task 2/3 tests (9 + 9 = 18 new tests) plus the existing 264 = 282 expected.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: dispatch overlap taper in Step 5b when EnablePropagationOverlapTaper=true"
```

---

### Task 5: Integration test — Step 5b taper attenuates the j126-style influence

The single-spline single-end-zone synthetic test in Phase A's Task 5 was sufficient because Phase A only changes the per-spline blender. Phase A.5 is a Step 5b change, which exercises the *application* layer. The integration test needs to construct a populated `_propagatedMidSplineInfluences` dictionary and an `ApplyUnifiedProfiles` invocation, then read out `TargetElevation` for the contested CSes.

The cleanest harness is a focused unit test that calls `ApplyUnifiedProfiles` on a minimal `UnifiedRoadNetwork` containing one long spline (~60 m, descending from 159 m to ~156 m) with one directly-anchored end constraint at 158.95 m. We then post-inject a propagated mid-spline influence with high elevation (166 m) on the spline's last CSes and verify the taper attenuates it.

Reaching into the blender's private `_propagatedMidSplineInfluences` from a test is brittle. Instead, build the test against the public `Step5bApplyInfluences` extraction (see Step 1 below).

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — extract Step 5b body into an internal static method that takes the influences dict + claimed zones lookup as parameters.
- Create: `BeamNgTerrainPoc.Tests/Junction/PropagationOverlapTaperTests.cs`

- [ ] **Step 1: Extract Step 5b into a testable static method**

In `UnifiedJunctionProfileBlender.cs`, immediately above the existing `BlendSplineProfile` definition (around L1017), add:

```csharp
/// <summary>
///     Phase A.5 testable extraction of Step 5b. Applies propagated mid-spline
///     influences to <paramref name="crossSections" /> with optional overlap taper
///     via <paramref name="splineClaimedZones" />. Returns number of modified CSes.
/// </summary>
internal static int ApplyPropagatedMidSplineInfluences(
    IEnumerable<UnifiedCrossSection> crossSections,
    Dictionary<int, List<(float elevation, float weight, int junctionId)>> influencesByCsIndex,
    Dictionary<int, SplineClaimedZone>? splineClaimedZones)
{
    var modified = 0;
    var csIndexLookup = crossSections.ToDictionary(cs => cs.Index);

    foreach (var (csIndex, influences) in influencesByCsIndex)
    {
        if (!csIndexLookup.TryGetValue(csIndex, out var cs))
            continue;
        if (float.IsNaN(cs.TargetElevation) || cs.IsRoundaboutBlended)
            continue;

        var totalWeight = 0f;
        var weightedElevSum = 0f;
        foreach (var inf in influences)
        {
            var w = inf.weight;
            if (splineClaimedZones != null
                && splineClaimedZones.TryGetValue(cs.OwnerSplineId, out var zone))
            {
                w *= SplineClaimedZones.GetTaperFor(zone, cs.Index, inf.junctionId);
            }
            totalWeight += w;
            weightedElevSum += inf.elevation * w;
        }

        if (totalWeight < 0.001f) continue;

        var weightedElev = weightedElevSum / totalWeight;
        var influenceFactor = MathF.Min(totalWeight, 1.0f);
        var newElev = weightedElev * influenceFactor + cs.TargetElevation * (1f - influenceFactor);

        if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
        {
            cs.TargetElevation = newElev;
            modified++;
        }
    }

    return modified;
}
```

- [ ] **Step 2: Refactor Step 5b to call the extracted method**

Replace the body of the Step 5b `if` block (introduced in Task 4 Step 3) so it delegates:

```csharp
        if (_propagatedMidSplineInfluences is { Count: > 0 })
        {
            var propagatedModified = ApplyPropagatedMidSplineInfluences(
                network.CrossSections,
                _propagatedMidSplineInfluences,
                _splineClaimedZones);

            if (propagatedModified > 0)
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Applied {propagatedModified} propagated mid-spline influences on continuous roads" +
                    (_splineClaimedZones != null ? " (overlap-taper enabled)" : ""));

            _propagatedMidSplineInfluences = null;
            _splineClaimedZones = null;
        }
```

This collapses the inline loop into a single call. Behaviour is identical because the extraction was 1:1.

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds.

- [ ] **Step 4: Run full test suite to confirm refactor is behaviour-preserving**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green, same count as Task 4 Step 5.

- [ ] **Step 5: Write the integration test file**

Create `BeamNgTerrainPoc.Tests/Junction/PropagationOverlapTaperTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PropagationOverlapTaperTests
{
    /// <summary>
    ///     Mimics franco_same_prio spline 64 / junction 126 topology:
    ///       - Spline length 60 m (test compresses from real 311 m for speed)
    ///       - Descending natural profile: 159 m at CS 0 → 156 m at CS 60 (slope ≈ -0.05)
    ///       - Direct end constraint at j126 (CS 60): elevation 158.95, blend distance 30 m
    ///       - Propagated mid-spline influence from j102: elevation 166.54, attached at
    ///         CS 60 with blend distance 25 m (so the influence reaches back to CS 35).
    ///       - CS-by-CS influence weights follow the existing quintic smoothstep falloff,
    ///         but for test simplicity we set a single CS's influence and inspect it.
    /// </summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, List<(float elevation, float weight, int junctionId)>> influences,
                    Dictionary<int, SplineClaimedZone> claimedZones)
        BuildJunction126Scenario(bool taperEnabled)
    {
        // 61 CSes at 1 m spacing → road length 60 m.
        var sections = new List<UnifiedCrossSection>();
        for (var i = 0; i <= 60; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                Index = 64_000 + i,
                LocalIndex = i,
                OwnerSplineId = 64,
                CenterPoint = new Vector2(i, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                // Phase A.5 baseline: parabolic blend has already run, end zone is at 158.95
                // and CSes < 30 m from end follow a parabolic descent back to natural at d=30.
                // For test simplicity: the last 5 CSes (i=55..60) are at 158.95 m (we treat
                // them as "fully end-anchored" by the parabolic profile).
                TargetElevation = i >= 55 ? 158.95f : (159f - 0.05f * (60 - i)),
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            });
        }

        // Propagated influence on CS 58 (d=58 m from start, 2 m from end anchor):
        // - Source junction = j102 (id 102), elevation 166.54 m
        // - Weight 0.85 (mimics what CollectInfluencesFromCrossing would produce at d=2m
        //   into a 25 m propagated blend: weight = 1 - smoothstep(2/25) ≈ 0.96; we lower it
        //   slightly to 0.85 so the unmodified path produces a clearly-visible delta).
        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 58, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };

        Dictionary<int, SplineClaimedZone> claimedZones = new();
        if (taperEnabled)
        {
            var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
            {
                {
                    (64, false), new JunctionEndpointConstraint
                    {
                        Elevation = 158.95f, Slope = 0f, IsSplineStart = false,
                        BlendDistanceMeters = 30f,
                        Junction = new NetworkJunction { JunctionId = 126 },
                        PrimaryTangentDirection = new Vector2(1f, 0f)
                    }
                }
            };
            var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } };
            claimedZones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        }

        return (sections, influences, claimedZones);
    }

    [Fact]
    public void TaperOff_LegacyBehaviour_PropagatedInfluenceDragsCsToward166()
    {
        var (sections, influences, _) = BuildJunction126Scenario(taperEnabled: false);
        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, splineClaimedZones: null);

        var after = sections[58].TargetElevation;

        // Legacy formula: newElev = 166.54 * 0.85 + 158.95 * 0.15 ≈ 165.40 m
        // We assert the CS moved upward by at least 5 m (toward the propagated 166 m).
        Assert.True(after - beforeElev > 5f,
            $"Without taper, CS 58 should jump upward toward 166m; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_CsInsideContestedEndZone_InfluenceAttenuatedToNearZero()
    {
        var (sections, influences, claimedZones) = BuildJunction126Scenario(taperEnabled: true);
        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[58].TargetElevation;

        // CS 58: d_from_end = 2 m, j126 blend = 30 m → taper = smoothstep(2/30) ≈ 0.0129.
        // Tapered weight: 0.85 * 0.0129 ≈ 0.0110. newElev = 166.54 * 0.011 + 158.95 * 0.989 ≈ 159.03.
        // The CS should move upward by less than 0.2 m (taper kills the influence near the anchor).
        Assert.True(after - beforeElev < 0.2f,
            $"With taper, CS 58 (2m from j126 anchor) should barely move; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_CsAtFarEndOfBlendZone_InfluenceMostlyPreserved()
    {
        // Put the influence at CS 30 (d_from_end = 30 m, right at j126 blend boundary).
        var (sections, _, claimedZones) = BuildJunction126Scenario(taperEnabled: true);
        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 30, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };
        var beforeElev = sections[30].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[30].TargetElevation;

        // CS 30: d_from_end = 30 m = blend distance → taper = smoothstep(1) = 1.0 → no attenuation.
        // The influence moves the CS exactly as it would with taper off.
        var (sectionsBaseline, influencesBaseline, _) = BuildJunction126Scenario(taperEnabled: false);
        var baselineInfluences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 30, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };
        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sectionsBaseline, baselineInfluences, splineClaimedZones: null);
        var baselineAfter = sectionsBaseline[30].TargetElevation;

        Assert.Equal(baselineAfter, after, 2);
        Assert.True(after - beforeElev > 5f,
            $"At blend boundary, taper=1 → full influence; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_SameJunctionAsInfluenceSource_NoTaper()
    {
        // Defensive case: a propagated influence whose source junction IS the same as
        // the contested claim's junction must NOT be tapered (it would suppress the
        // junction's own propagation through itself, which is incoherent).
        var (sections, _, _) = BuildJunction126Scenario(taperEnabled: true);
        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            // Set source junction id to 126 — matches the claim
            { 64_000 + 58, new List<(float, float, int)> { (166.54f, 0.85f, 126) } }
        };
        // Build claimed-zones manually using junction id 126 to match the influence source
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (64, false), new JunctionEndpointConstraint
                {
                    Elevation = 158.95f, Slope = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 126 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };
        var claimedZones = SplineClaimedZones.Build(
            constraints, new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } });

        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[58].TargetElevation;

        // No taper (same junction) → full legacy behaviour. CS should jump upward.
        Assert.True(after - beforeElev > 5f,
            $"Same-junction case: taper should be 1, full influence applies; before={beforeElev}, after={after}");
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~PropagationOverlapTaperTests"`
Expected: PASS, 4/4 green.

- [ ] **Step 7: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. Total count: 264 (baseline) + 9 (OverlapTaper) + 9 (SplineClaimedZones) + 4 (PropagationOverlapTaper) = 286.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc.Tests/Junction/PropagationOverlapTaperTests.cs
git commit -m "test: junction-126 reproduction asserts Step 5b taper attenuates contested influence"
```

---

### Task 6: End-to-end validation (user-driven; no code)

This task is **user-executed** on Windows. The agent's job is to copy artefacts, analyze the CSVs, and write the README.

- [ ] **Step 1: Flip flag to true (uncommitted local edit)**

User opens `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`, changes:

```csharp
public bool EnablePropagationOverlapTaper { get; set; } = false;
```

to:

```csharp
public bool EnablePropagationOverlapTaper { get; set; } = true;
```

`EnableParabolicJunctionBlend` stays `true` (Phase A.5 composes with parabolic). Build in Visual Studio (Release).

- [ ] **Step 2: Run terrain generation in BeamNG.drive**

User regenerates `franco_same_prio` from BeamNG.drive desktop app. Artefacts overwrite `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\`.

- [ ] **Step 3: Snapshot results**

Agent runs (bash via the Bash tool):

```bash
mkdir -p "d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio"
SRC="C:/Users/aklei/AppData/Local/BeamNG/BeamNG.drive/current/levels/franco_same_prio/MT_TerrainGeneration"
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio"
cp "$SRC/junction_residuals.csv" "$DST/"
cp "$SRC/w_test_summary.csv" "$DST/"
cp "$SRC/quadratic_growth.csv" "$DST/"
cp "$SRC/delta_three_band.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug_legend.png" "$DST/"
cp "$SRC/logs"/Log_TerrainGen_*_Info.txt "$DST/terrain_gen_info.log"
```

- [ ] **Step 4: Extract j125 and j126 rows + W1 aggregate**

```bash
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio"

echo "=== j125 quadratic_growth ==="
grep "^125,64," "$DST/quadratic_growth.csv"
echo "=== j125 w_test_summary ==="
grep "^125,64," "$DST/w_test_summary.csv"
echo "=== j126 quadratic_growth ==="
grep "^126,64," "$DST/quadratic_growth.csv"
echo "=== j126 w_test_summary ==="
grep "^126,64," "$DST/w_test_summary.csv"
echo "=== j126 residuals ==="
grep "^126," "$DST/junction_residuals.csv"
echo "=== W1 aggregate ==="
grep "W1 validation" "$DST/terrain_gen_info.log" | tail -1
```

- [ ] **Step 5: Compare against A.8 baseline (NOT parabolic_a)**

A.5 stacks on top of A.8. The fair comparison is `surface_protection_a8_franco_same_prio/` — the A.8 snapshot — not `parabolic_a_franco_same_prio/` (which predates A.8 and shows the rasterizer override masking the cliff). Also pull the A.8 + parabolic_a numbers for context.

```bash
A8="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/surface_protection_a8_franco_same_prio"
PA="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio"

echo "--- A.8 j125 ---"
grep "^125,64," "$A8/quadratic_growth.csv"
grep "^125,64," "$A8/w_test_summary.csv"
echo "--- A.8 j126 ---"
grep "^126,64," "$A8/quadratic_growth.csv"
grep "^126,64," "$A8/w_test_summary.csv"
grep "W1 validation" "$A8/terrain_gen_info.log" | tail -1

echo "--- parabolic_a j126 (pre-A.8 reference) ---"
grep "^126,64," "$PA/quadratic_growth.csv"
grep "^126,64," "$PA/w_test_summary.csv"
```

- [ ] **Step 6: Evaluate pass criteria**

A.8 alone may already bring j126 below 3σ. If so, A.5's improvement on top will be incremental, not dramatic. If A.8 left j126 at 3–6σ (its stated intermediate target), A.5 should close the remaining gap.

| Criterion | Target | A.8 baseline | parabolic_a5 result |
|---|---|---|---|
| j126 spline 64 `w` | < 3σ | (fill from A.8) | (fill) |
| j126 quadratic_growth monotone | no sign flip at 5/15/30/60 m | (fill from A.8) | (fill) |
| j126 `residual_max_minus_min` | ≤ 1.5 m | (fill from A.8) | (fill — must stay ≤ 1.5) |
| j125 spline 64 `w` (regression gate) | < 3σ | (fill from A.8) | (fill — must stay < 3σ) |
| W1 redBandPixels | ≤ A.8 + 5 % | (fill from A.8) | (fill — must be ≤ A.8+5%) |

**If A.8 alone already achieved j126 < 3σ:** A.5 may surface zero observable improvement. Two interpretations:
1. The Step 5b propagation overlay's effect on j126 was masked by the rasterizer bug — with the rasterizer fixed, the overlay's effect is already small. A.5 still provides the *correctness* improvement (its synthetic test still asserts the right thing) but no franco_same_prio cliff to close.
2. A.5 fixes a different junction that wasn't on the j126 radar. Search for new wins by sorting w_test_summary.csv before/after A.5 and looking for top-mover junctions.
Discuss with user before flipping A.5's default.

- [ ] **Step 7: Update baseline README**

Append a new section to `examples_for_ai/baseline_phase19/README.md`:

```markdown
### parabolic_a5_franco_same_prio (heightmap 2048, captured <date>)

Re-run of franco_same_prio with `EnableParabolicJunctionBlend = true`,
`EnableSurfaceWidthProtection = true` (from A.8), AND
`EnablePropagationOverlapTaper = true` (this phase). All other Phase 1.9 / W2 / W3
flags off. A.5 stacks on top of A.8; the comparison baseline is
`surface_protection_a8_franco_same_prio/` rather than `parabolic_a_franco_same_prio/`.
Validates Phase A.5 — the Step 5b propagation/overlap taper that prevents
propagated mid-spline influences from overriding directly-anchored junction
parabolic profiles. See
[`ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a5-plan.md`](../../ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a5-plan.md).

W1 validation: <paste from log>

Phase A.5 pass-criteria result (strict, per plan Task 6):

| Criterion | Target | Observed | Pass? |
|---|---|---|---|
| Junction 126 spline 64 `w` | < 3σ | <fill> | <yes/no> |
| Junction 126 quadratic_growth monotone | no sign flip 5/15/30/60 m | <fill> | <yes/no> |
| Junction 126 `residual_max_minus_min` | ≤ 1.5 m | <fill> | <yes/no> |
| Junction 125 spline 64 `w` (regression gate) | < 3σ | <fill> | <yes/no> |
| W1 `redBandPixels` | ≤ parabolic_a + 5 % | <fill> | <yes/no> |

Junction 125 + 126 / spline 64 detail (parabolic_a → parabolic_a5):

```
quadratic_growth — j125 parabolic_a:  <paste>
quadratic_growth — j125 parabolic_a5: <paste>
quadratic_growth — j126 parabolic_a:  <paste>
quadratic_growth — j126 parabolic_a5: <paste>
w-test          — j125 parabolic_a:   <paste>
w-test          — j125 parabolic_a5:  <paste>
w-test          — j126 parabolic_a:   <paste>
w-test          — j126 parabolic_a5:  <paste>
```
```

- [ ] **Step 8: Commit the README**

```bash
git add examples_for_ai/baseline_phase19/README.md
git commit -m "docs: Phase A.5 overlap-taper franco validation snapshot"
```

(The `parabolic_a5_franco_same_prio/` data files are gitignored.)

---

### Task 7: Default flag flip (gated on Task 6 results)

- [ ] **Step 1: Review Task 6 numerical results with user.**

If pass criteria met: proceed to step 2.

If pass criteria NOT met: stop. Hypotheses for follow-up (in order of likelihood):

1. **`refinedConstraints` not reflected in claimed zones.** Pass 2 recomputes T-junction terminating-road constraints. Phase A.5 builds `_splineClaimedZones` from the original `constraints` (which already contains those keys, but with pre-refinement blend distances). Verify Pass-2 refinement doesn't *change* `BlendDistanceMeters` (it shouldn't — only Elevation/Slope/banking are refined). If it does change blend distance, lift the build of `_splineClaimedZones` until after `refinedConstraints` is merged at L189.
2. **Multi-contributor IDW (downstream of the blender) is the real source of the cliff.** The baseline README's working hypothesis. If j126 has `n_contributors = 2` and the continuous road carries an elevation that the Phase-4 IDW heightmap rasterization can't ignore, the cliff is in the rasterization, not the blender. Diagnostic: inspect the `delta_three_band.png` at j126 — does the band match the spline-64 centerline shape or a junction-wide blob?
3. **Bank-angle contribution.** Phase A.5 only changes elevation. If the cliff has a banking component (bank-angle disagreement between the continuous primary and spline 64's end zone), the heightmap delta would persist even with elevation fixed. Diagnostic: inspect `cs.BankAngleRadians` near j126 in a log dump.

- [ ] **Step 2: Flip default to true**

Edit `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`:

```csharp
public bool EnablePropagationOverlapTaper { get; set; } = true;
```

- [ ] **Step 3: Build + full test suite**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green.

- [ ] **Step 4: Commit default flip**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: enable EnablePropagationOverlapTaper by default after Phase A.5 validation"
```

---

## Out of scope for Phase A.5

All items below are tracked in the [roadmap](2026-05-15-parabolic-blend-roadmap.md) — do not start any of them as part of A.5 execution.

- **Multi-contributor IDW blending at multi-way junctions** → roadmap §A.7 (conditional on A.5 validation result).
- **Bank-angle parabolic path / banking taper** → roadmap §A.6 (conditional on A.5 banking artefacts).
- **AASHTO K-value cap on blend distance** → roadmap §B (queued; orthogonal to A.5).
- **Generalizing the taper to all influence-application sites.** Step 5 (`ApplyMidSplineCrossingInfluences`) applies *direct* mid-spline crossing influences, which are by definition anchored at the crossing point and *shouldn't* be tapered against any directly-anchored junction (the crossing IS the constraint). Only Step 5b's *propagated* influences need the taper. Broader seam blending refactor → roadmap §X2.
- **`JunctionBankingAdapter` overwrites CG profiles (Phase 3.5)** → roadmap §X1.
- **Connected-road mesh solver: terrain-road elevation gap** → roadmap §X3.
- **Dead-end spike in `FinalSnapTJunctionEndpoints`** → roadmap §X4.

---

## Self-Review

**Spec coverage (against the handoff doc):**
- ✅ Suggested approach step 1: "Build a per-spline map of occupied blend zones" → Task 3 `SplineClaimedZones.Build`.
- ✅ Suggested approach step 2: "Attenuate weight by smooth taper based on distance to the nearest contested junction's blend boundary" → Task 4 + Task 5 wiring of `GetTaperFor`.
- ✅ Suggested approach step 3: "Taper = 1.0 outside other junctions' zones, falls to 0 at the contested junction's anchor node" → `OverlapTaper.Compute` and `GetTaperFor`.
- ✅ Re-validate franco_same_prio with j125 win preserved and j126 improved → Task 6 pass criteria.
- ✅ Hard constraint: no terrain-grade rules → taper uses only `(distFromAnchor, blendLength)`. No reference to grade/elevation in the taper math.
- ✅ Hard constraint: `BlendSplineProfile` / `BlendSplineProfileParabolic` untouched → confirmed in File Structure section ("Do NOT modify").
- ✅ Hard constraint: `FinalSnapTJunctionEndpoints` untouched → confirmed.
- ✅ Hard constraint: `EnableParabolicJunctionBlend` stays true by default → not modified by this plan.
- ✅ Hard constraint: change in propagation construction or Step 5b application, not per-spline blender → all changes in `ApplyUnifiedProfiles` Step 5b and the new helpers.

**Placeholder scan:**
- No "TBD", no "implement later", no "add error handling".
- Task 6 README appendix has `<paste>` and `<fill>` slots, but those are explicit placeholders for *empirical data* that doesn't exist until the user runs the build. They are not implementation placeholders — the agent fills them from the snapshot.
- Task 7 Step 1 lists three follow-up hypotheses with concrete diagnostics, not "investigate further".

**Type consistency:**
- `OverlapTaper.Compute(distFromAnchor, blendLength)` — same signature in Task 2 definition and Task 3 usage in `GetTaperFor`.
- `SplineClaimedZones.Build(constraints, crossSectionsBySpline)` — same signature in Task 3 definition, Task 4 wiring, and Task 5 test setup.
- `SplineClaimedZones.GetTaperFor(zone, csIndex, sourceJunctionId)` — same signature in Task 3 definition, Task 4 wiring (via `_splineClaimedZones`), and the Task 5 extracted method.
- `SplineClaimedZone` / `SplineEndClaim` property names (`SplineId`, `RoadLength`, `StartClaim`, `EndClaim`, `JunctionId`, `BlendDistanceMeters`, `DistFromStartByCsIndex`) — consistent across definition, tests, and consumer.
- `ApplyPropagatedMidSplineInfluences(crossSections, influencesByCsIndex, splineClaimedZones)` — same signature in extraction (Task 5 Step 1), refactor (Task 5 Step 2), and tests (Task 5 Step 5).
- Flag name `EnablePropagationOverlapTaper` — same string in Tasks 1, 4, 6, 7.

**TDD scaffold:**
- Task 2: failing test → impl → green (smoothstep helper).
- Task 3: failing test → impl → green (claimed-zones builder + `GetTaperFor`).
- Task 4: existing tests stay green (flag default false → behaviour unchanged).
- Task 5: failing integration test for taper-off/taper-on cases → impl already in place → green.

---

## Execution handoff

This is a 7-task plan, ~3-5 minutes per step. Task 5's extraction-then-test pattern is the only multi-step refactor; everything else is small.

**Subagent-driven (recommended):** Dispatch one subagent per task, review the diff between tasks. Each subagent gets a fresh context so plan-following stays disciplined.

**Inline (faster):** Execute in this session with checkpoint reviews after Tasks 3, 4, 5, and 6 — those are the bisectable boundaries.

Task 6 specifically requires user action in BeamNG.drive (Windows desktop app). The agent cannot run terrain generation.
