# Roundabout no-blend connectors + tilted ring plane — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On the no-blend path, make roundabout connecting roads meet the ring with the existing §3/§4 affine technique, and replace the forced-uniform ring disk with a terrain-following tilted plane clamped to 6%.

**Architecture:** Two independent changes. (1) Lift the two skip-gates so `Roundabout` junctions flow through the existing `RetargetTerminatingRoadsToSettledThrough` (§3) and `MatchTerminatingBankingToThroughSurface` (§4) — the ring is already the `IsContinuous` "through" contributor, so no new junction logic is needed. (2) A new pure `RoundaboutPlaneFit` helper least-squares-fits a plane to terrain under the ring; `RoundaboutElevationHarmonizer` writes per-cross-section plane Z when `useTiltedPlane` is set, which `UnifiedRoadSmoother` passes only on the no-blend/affine path.

**Tech Stack:** C# / .NET 9, xUnit. Build: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`. Test: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true`.

**Spec:** `docs/superpowers/specs/2026-05-30-roundabout-no-blend-design.md`

---

### Task 1: Approach A — let §3/§4 process roundabout junctions

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (§3 gate ~1488–1491, §4 gate ~1550–1553)
- Test: `BeamNgTerrainPoc.Tests/Junction/RetargetTerminatingToSettledThroughTests.cs` (replace the skip test)
- Test: `BeamNgTerrainPoc.Tests/Junction/BankingMatchToThroughSurfaceTests.cs` (add a roundabout test)

- [ ] **Step 1: Replace the §3 "roundabout-skip" test with a "roundabout-processed" test**

In `RetargetTerminatingToSettledThroughTests.cs`, delete `Roundabout_Skipped_HarmonizedElevationUnchanged` (lines ~188–198) and replace with the two tests below. The helper `BuildStaleTJunction(JunctionType.Roundabout)` already builds a ring-as-through + connector-as-terminating junction; we additionally set `IsExcluded = true` to match the real roundabout pipeline (the harmonizer marks roundabout parent junctions excluded).

```csharp
    [Fact]
    public void Roundabout_NowProcessed_RetargetsConnectorToRingZ()
    {
        // Roundabout junction = ring (through) + connector (terminating), modelled exactly like a T.
        // Approach A: it must now be retargeted like any T-junction (was previously skipped).
        var (network, termEnd, termFar) = BuildStaleTJunction(JunctionType.Roundabout);

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(154.83f, network.Junctions[0].HarmonizedElevation, 2); // pinned to ring (through) Z
        Assert.Equal(154.83f, termEnd.TargetElevation, 2);                   // connector ring-end meets ring
        Assert.Equal(150.00f, termFar.TargetElevation, 2);                   // far end untouched (affine decay)
    }

    [Fact]
    public void Roundabout_ProcessedEvenWhenExcluded()
    {
        // Real roundabout parent junctions carry IsExcluded=true (set by RoundaboutElevationHarmonizer).
        // The §3 gate must let Roundabout through despite IsExcluded.
        var (network, termEnd, _) = BuildStaleTJunction(JunctionType.Roundabout);
        network.Junctions[0].IsExcluded = true;

        UnifiedRoadSmoother.RetargetTerminatingRoadsToSettledThrough(network);

        Assert.Equal(154.83f, termEnd.TargetElevation, 2); // still retargeted to ring Z
    }
```

- [ ] **Step 2: Run the §3 tests to verify the new tests FAIL**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~RetargetTerminatingToSettledThroughTests"`
Expected: FAIL — `Roundabout_NowProcessed_RetargetsConnectorToRingZ` asserts 154.83 but the current skip leaves 155.57; `Roundabout_ProcessedEvenWhenExcluded` also fails (IsExcluded skip).

- [ ] **Step 3: Change the §3 gate in `RetargetTerminatingRoadsToSettledThrough`**

In `UnifiedRoadSmoother.cs`, replace the two guard lines at the top of the `foreach (var junction in network.Junctions)` loop:

```csharp
                if (junction.IsExcluded) continue;
                if (junction.Type is JunctionType.Roundabout or JunctionType.MidSplineCrossing
                    or JunctionType.Continuation)
                    continue;
```

with:

```csharp
                // Roundabout junctions are modelled as T-junctions (ring = through, connector =
                // terminating) and are intentionally IsExcluded by RoundaboutElevationHarmonizer — but on
                // the no-blend path they still need the connector retargeted to the ring Z, so let them
                // through both gates. MidSplineCrossing/Continuation stay excluded (no through-road meet).
                if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;
                if (junction.Type is JunctionType.MidSplineCrossing or JunctionType.Continuation)
                    continue;
```

- [ ] **Step 4: Run the §3 tests to verify they PASS**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~RetargetTerminatingToSettledThroughTests"`
Expected: PASS (all, including the unchanged T-junction tests).

- [ ] **Step 5: Add a §4 roundabout banking test**

In `BankingMatchToThroughSurfaceTests.cs`, add a test using the existing `BuildSlopedThroughFlatTerminating` helper but flipping the junction to `Roundabout` + `IsExcluded`. Add a small helper overload first (place after `BuildSlopedThroughFlatTerminating`):

```csharp
    private static (UnifiedRoadNetwork network, UnifiedCrossSection termEnd)
        BuildSlopedThroughFlatTerminatingAs(JunctionType type, bool excluded)
    {
        var (network, termEnd) = BuildSlopedThroughFlatTerminating();
        network.Junctions[0].Type = type;
        network.Junctions[0].IsExcluded = excluded;
        return (network, termEnd);
    }

    [Fact]
    public void Roundabout_NowBankMatched_EvenWhenExcluded()
    {
        // The ring (through) is sloped; the connector (terminating) must pick up the bank at the seam,
        // exactly like a T-junction. Was skipped before approach A.
        var (network, termEnd) = BuildSlopedThroughFlatTerminatingAs(JunctionType.Roundabout, excluded: true);

        UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface(network);

        Assert.Equal(MathF.Asin(0.1f), termEnd.BankAngleRadians, 3); // bank matched
        Assert.Equal(100f, termEnd.TargetElevation, 3);              // centerline untouched (§4 invariant)
    }
```

- [ ] **Step 6: Run the §4 test to verify it FAILS**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BankingMatchToThroughSurfaceTests"`
Expected: FAIL — `Roundabout_NowBankMatched_EvenWhenExcluded` gets bank 0 (skipped today).

- [ ] **Step 7: Change the §4 gate in `MatchTerminatingBankingToThroughSurface`**

Replace:

```csharp
            if (junction.IsExcluded) continue;
            if (junction.Type is JunctionType.Roundabout or JunctionType.MidSplineCrossing
                or JunctionType.Continuation or JunctionType.Endpoint)
                continue;
```

with:

```csharp
            if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;
            if (junction.Type is JunctionType.MidSplineCrossing or JunctionType.Continuation
                or JunctionType.Endpoint)
                continue;
```

- [ ] **Step 8: Run the §4 tests to verify they PASS**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BankingMatchToThroughSurfaceTests"`
Expected: PASS (all).

- [ ] **Step 9: Verify the RoadMaskBuilder fill-disk does not conflict**

Read `BeamNgTerrainPoc/Terrain/Algorithms/RoadMaskBuilder.cs` around the junction-fill use of `HarmonizedElevation` (~line 217). Confirm it either skips `IsExcluded` junctions (so §3 setting `HarmonizedElevation` on a roundabout junction is inert there) or fills at the ring-plane Z harmlessly. Record the finding in the commit message. No code change expected; if a conflict exists, STOP and raise it before proceeding.

- [ ] **Step 10: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs BeamNgTerrainPoc.Tests/Junction/RetargetTerminatingToSettledThroughTests.cs BeamNgTerrainPoc.Tests/Junction/BankingMatchToThroughSurfaceTests.cs
git commit -m "feat(no-blend): §3/§4 now process roundabout junctions (approach A)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `RoundaboutPlaneFit` — pure least-squares plane with 6% tilt clamp

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutPlaneFit.cs`
- Test: `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutPlaneFitTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutPlaneFitTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

public class RoundaboutPlaneFitTests
{
    private static List<(Vector2 Xy, float Z)> Pts(params (float x, float y, float z)[] p)
    {
        var list = new List<(Vector2, float)>();
        foreach (var (x, y, z) in p) list.Add((new Vector2(x, y), z));
        return list;
    }

    [Fact]
    public void FlatTerrain_ZeroTilt_MeanElevation()
    {
        var pts = Pts((0, 0, 100), (10, 0, 100), (0, 10, 100), (10, 10, 100));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0f, tilt, 4);
        Assert.Equal(100f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(5, 5)), 3);
    }

    [Fact]
    public void TiltedTerrain_PlaneFollowsTilt()
    {
        // z = 0.02*x  → tilt 0.02 along +x, within the 6% cap.
        var pts = Pts((0, 0, 0f), (100, 0, 2f), (0, 100, 0f), (100, 100, 2f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0.02f, tilt, 3);
        Assert.Equal(0.02f, a, 3);
        Assert.Equal(0f, b, 3);
        Assert.Equal(1f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(50, 50)), 2);
    }

    [Fact]
    public void SteepTerrain_TiltClampedTo6Percent_ThroughCentroid()
    {
        // z = 0.20*x  → wants 20% tilt, must clamp to 6%; plane stays through the centroid.
        var pts = Pts((0, 0, 0f), (100, 0, 20f), (0, 100, 0f), (100, 100, 20f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0.06f, MathF.Sqrt(a * a + b * b), 3); // clamped magnitude
        Assert.Equal(0.20f, tilt, 2);                       // pre-clamp tilt reported
        // Centroid (50,50,10) stays on the plane → cut/fill balanced.
        Assert.Equal(10f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(50, 50)), 2);
    }

    [Fact]
    public void DegenerateInput_FallsBackToFlatMean()
    {
        // All points identical (rank-deficient) → flat plane at the mean, no NaN.
        var pts = Pts((5, 5, 7f), (5, 5, 7f), (5, 5, 7f));
        var (a, b, c, tilt) = RoundaboutPlaneFit.FitClamped(pts, 0.06f);
        Assert.Equal(0f, tilt, 4);
        Assert.Equal(7f, RoundaboutPlaneFit.Evaluate(a, b, c, new Vector2(5, 5)), 3);
    }
}
```

- [ ] **Step 2: Run to verify FAIL**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~RoundaboutPlaneFitTests"`
Expected: FAIL to compile — `RoundaboutPlaneFit` does not exist.

- [ ] **Step 3: Implement `RoundaboutPlaneFit`**

Create `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutPlaneFit.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Least-squares fit of a plane z = a·x + b·y + c to a set of (x,y,z) points, with the plane's tilt
///     magnitude clamped to a maximum (the roundabout max Querneigung, civil limit 6%). Pure — no side
///     effects. Used to make a roundabout ring follow terrain as a single drivable tilted disk instead of
///     a forced-uniform horizontal disk, minimizing cut/fill.
/// </summary>
public static class RoundaboutPlaneFit
{
    public static float Evaluate(float a, float b, float c, Vector2 xy) => a * xy.X + b * xy.Y + c;

    /// <summary>
    ///     Fits z = a·x + b·y + c by least squares, then clamps tilt = sqrt(a²+b²) to <paramref name="maxTilt" />
    ///     (scaling a,b about the centroid so the plane still passes through (x̄,ȳ,z̄) → balanced cut/fill).
    ///     Returns the coefficients and the PRE-clamp tilt (for diagnostics). Degenerate/rank-deficient input
    ///     falls back to a flat plane at the mean z.
    /// </summary>
    public static (float A, float B, float C, float PreClampTilt) FitClamped(
        IReadOnlyList<(Vector2 Xy, float Z)> points, float maxTilt)
    {
        var n = points.Count;
        if (n == 0) return (0f, 0f, 0f, 0f);

        double sx = 0, sy = 0, sz = 0, sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
        foreach (var (xy, z) in points)
        {
            double x = xy.X, y = xy.Y;
            sx += x; sy += y; sz += z;
            sxx += x * x; sxy += x * y; syy += y * y;
            sxz += x * z; syz += y * z;
        }

        var meanZ = (float)(sz / n);
        var meanX = (float)(sx / n);
        var meanY = (float)(sy / n);

        // Solve the 3×3 normal equations via Cramer's rule (centered to improve conditioning).
        // Use the covariance form: subtract the means so the system is [Cxx Cxy; Cxy Cyy][a;b] = [Cxz; Cyz].
        double cxx = sxx - sx * sx / n;
        double cxy = sxy - sx * sy / n;
        double cyy = syy - sy * sy / n;
        double cxz = sxz - sx * sz / n;
        double cyz = syz - sy * sz / n;

        var det = cxx * cyy - cxy * cxy;
        float a, b;
        if (System.Math.Abs(det) < 1e-9)
        {
            a = 0f; b = 0f; // rank-deficient (collinear / coincident points) → flat
        }
        else
        {
            a = (float)((cxz * cyy - cyz * cxy) / det);
            b = (float)((cyz * cxx - cxz * cxy) / det);
        }

        var preTilt = MathF.Sqrt(a * a + b * b);
        if (preTilt > maxTilt && preTilt > 1e-9f)
        {
            var scale = maxTilt / preTilt;
            a *= scale;
            b *= scale;
        }

        // Plane passes through the centroid (x̄,ȳ,z̄): c = z̄ − a·x̄ − b·ȳ.
        var c = meanZ - a * meanX - b * meanY;
        return (a, b, c, preTilt);
    }
}
```

- [ ] **Step 4: Run to verify PASS**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~RoundaboutPlaneFitTests"`
Expected: PASS (all 4).

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutPlaneFit.cs BeamNgTerrainPoc.Tests/Roundabout/RoundaboutPlaneFitTests.cs
git commit -m "feat(no-blend): RoundaboutPlaneFit — least-squares tilted plane clamped to 6%

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Wire the tilted plane into `RoundaboutElevationHarmonizer`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs` (signature + apply path)
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` (new tilt-cap param)
- Test: `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutTiltedPlaneTests.cs`

- [ ] **Step 1: Add the tilt-cap parameter**

In `JunctionHarmonizationParameters.cs`, after `ForceUniformRoundaboutElevation` (line ~409), add:

```csharp
    /// <summary>
    ///     Maximum tilt (Querneigung) of the terrain-following roundabout ring plane, as a gradient
    ///     (rise/run). Civil absolute limit is 6% → 0.06. Terrain demanding more becomes unavoidable
    ///     cut/fill. Only used on the no-blend tilted-plane path. Not exposed to UI.
    ///     Default: 0.06
    /// </summary>
    public float RoundaboutMaxPlaneTilt { get; set; } = 0.06f;
```

- [ ] **Step 2: Write the failing test**

Create `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutTiltedPlaneTests.cs`. It exercises the new internal apply method `RoundaboutElevationHarmonizer.ApplyTiltedRingPlane`, which takes ring cross-sections + a terrain sampler lambda (so no heightMap array is needed):

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

public class RoundaboutTiltedPlaneTests
{
    private static UnifiedCrossSection RingCs(int idx, Vector2 center) => new()
    {
        OwnerSplineId = 1,
        LocalIndex = idx,
        Index = 100 + idx,
        CenterPoint = center,
        TangentDirection = new Vector2(1f, 0f),
        NormalDirection = new Vector2(0f, 1f),
        TargetElevation = 0f
    };

    // Four ring cross-sections around a ~10 m circle centered at (50,50).
    private static List<UnifiedCrossSection> Ring() => new()
    {
        RingCs(0, new Vector2(60f, 50f)),
        RingCs(1, new Vector2(50f, 60f)),
        RingCs(2, new Vector2(40f, 50f)),
        RingCs(3, new Vector2(50f, 40f)),
    };

    [Fact]
    public void TiltedTerrain_RingFollowsPlane()
    {
        var ring = Ring();
        // Terrain tilts +0.02 along x: z = 0.02*(x-50) + 100.
        float Terrain(Vector2 p) => 0.02f * (p.X - 50f) + 100f;

        var preTilt = RoundaboutElevationHarmonizer.ApplyTiltedRingPlane(ring, Terrain, 0.06f);

        Assert.Equal(0.02f, preTilt, 3);
        // East cs (x=60) sits 0.2 m above the west cs (x=40); not uniform.
        var east = ring.First(c => c.CenterPoint.X == 60f).TargetElevation;
        var west = ring.First(c => c.CenterPoint.X == 40f).TargetElevation;
        Assert.Equal(0.4f, east - west, 2);
    }

    [Fact]
    public void FlatTerrain_RingUniform()
    {
        var ring = Ring();
        float Terrain(Vector2 _) => 100f;

        RoundaboutElevationHarmonizer.ApplyTiltedRingPlane(ring, Terrain, 0.06f);

        foreach (var cs in ring) Assert.Equal(100f, cs.TargetElevation, 3);
    }
}
```

- [ ] **Step 3: Run to verify FAIL**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~RoundaboutTiltedPlaneTests"`
Expected: FAIL to compile — `ApplyTiltedRingPlane` does not exist.

- [ ] **Step 4: Implement `ApplyTiltedRingPlane` and thread `useTiltedPlane`**

In `RoundaboutElevationHarmonizer.cs`:

(a) Add `using System;` / `using System.Numerics;` if not present (file already uses `System.Numerics`). Add the new method (place near `ApplyUniformRingElevation`):

```csharp
    /// <summary>
    ///     No-blend path: fit a single tilted plane to terrain under the ring (clamped to
    ///     <paramref name="maxTilt" />) and write it to every ring cross-section, so the ring follows the
    ///     hillside as a drivable disk instead of a forced-uniform horizontal disk. Returns the pre-clamp
    ///     tilt (for diagnostics). Pure except for writing the cross-sections' TargetElevation.
    /// </summary>
    internal static float ApplyTiltedRingPlane(
        List<UnifiedCrossSection> ringCrossSections,
        Func<Vector2, float> sampleTerrain,
        float maxTilt)
    {
        var points = new List<(Vector2, float)>(ringCrossSections.Count);
        foreach (var cs in ringCrossSections)
            points.Add((cs.CenterPoint, sampleTerrain(cs.CenterPoint)));

        var (a, b, c, preTilt) = RoundaboutPlaneFit.FitClamped(points, maxTilt);
        foreach (var cs in ringCrossSections)
            cs.TargetElevation = RoundaboutPlaneFit.Evaluate(a, b, c, cs.CenterPoint);
        return preTilt;
    }
```

(b) Add `bool useTiltedPlane = false` to the `HarmonizeRoundaboutElevations` signature (after `skipConnectingRoadBlending`):

```csharp
    public RoundaboutHarmonizationResult HarmonizeRoundaboutElevations(
        UnifiedRoadNetwork network,
        List<RoundaboutJunctionInfo> roundaboutJunctionInfos,
        float[,] heightMap,
        float metersPerPixel,
        bool skipConnectingRoadBlending = false,
        bool useTiltedPlane = false)
```

(c) In the per-roundabout loop, replace **Step 1 (CalculateRoundaboutElevation) + Step 2 (ApplyUniformRingElevation)** with a branch. Find the existing block (current lines ~76–106) and wrap it:

```csharp
            float ringElevation;
            int ringModified;
            if (useTiltedPlane)
            {
                var mapH = heightMap.GetLength(0);
                var mapW = heightMap.GetLength(1);
                var maxTilt = network.GetSplineById(ringSplineId)?.Parameters
                                  .JunctionHarmonizationParameters?.RoundaboutMaxPlaneTilt
                              ?? 0.06f;
                var preTilt = ApplyTiltedRingPlane(
                    ringCrossSections,
                    p =>
                    {
                        var px = (int)(p.X / metersPerPixel);
                        var py = (int)(p.Y / metersPerPixel);
                        if (px < 0 || px >= mapW || py < 0 || py >= mapH) return float.NaN;
                        return heightMap[py, px];
                    },
                    maxTilt);
                ringElevation = ringCrossSections.Count > 0
                    ? ringCrossSections.Average(cs => cs.TargetElevation)
                    : float.NaN;
                ringModified = ringCrossSections.Count;
                TerrainCreationLogger.Current?.Detail(
                    $"  [NO-BLEND RAB PLANE] roundabout {ringSplineId}: tilted plane, " +
                    $"preClampTilt={preTilt * 100f:F1}% cap={maxTilt * 100f:F1}% meanZ={ringElevation:F2}");
            }
            else
            {
                ringElevation = CalculateRoundaboutElevation(
                    roundaboutInfo, ringCrossSections, heightMap, metersPerPixel, mapWidth, mapHeight, network);
                if (float.IsNaN(ringElevation))
                {
                    TerrainLogger.Warning($"  Roundabout {ringSplineId}: Could not calculate ring elevation");
                    continue;
                }
                var maxElevChangeUniform = result.MaxElevationChange;
                ringModified = ApplyUniformRingElevation(
                    ringCrossSections, ringElevation, roundaboutInfo, network, ref maxElevChangeUniform);
                result.MaxElevationChange = maxElevChangeUniform;
            }

            roundaboutInfo.HarmonizedElevation = ringElevation;
            result.RoundaboutElevations[ringSplineId] = ringElevation;
            result.RingCrossSectionsModified += ringModified;
```

> Note: this replaces the **entire existing block from "Step 1: Calculate the harmonized elevation" through the `Roundabout {ringSplineId}: elevation=…` Detail log** (current lines ~76–109: the `CalculateRoundaboutElevation` call, the `if (float.IsNaN(ringElevation))` guard, the `roundaboutInfo.HarmonizedElevation`/`result.RoundaboutElevations`/`ApplyUniformRingElevation`/`result.RingCrossSectionsModified` lines, and that Detail log). Delete all of them so nothing is duplicated; the NaN guard now lives inside the `else`. Leave Step 3 (connecting-road blending — still gated by `skipConnectingRoadBlending`) and Step 4 (mark junctions excluded), which follow at ~111+, unchanged.

- [ ] **Step 5: Run the new tests + the full roundabout suite to verify PASS**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~Roundabout"`
Expected: PASS — `RoundaboutTiltedPlaneTests` (2) green; existing `RoundaboutBlendingTests` still green (uniform path untouched by default).

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs BeamNgTerrainPoc.Tests/Roundabout/RoundaboutTiltedPlaneTests.cs
git commit -m "feat(no-blend): tilted ring plane in RoundaboutElevationHarmonizer (gated by useTiltedPlane)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Activate the tilted plane on the no-blend path

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (Phase 2.6 call ~335–340; predicate ~462–468)

- [ ] **Step 1: Extract the `affineThroughActive` predicate into a helper**

In `UnifiedRoadSmoother.cs`, add a private static method (place near the other private helpers, e.g. above `RetargetTerminatingRoadsToSettledThrough`):

```csharp
    /// <summary>
    ///     The no-blend/affine path is active when blends are off and affine ThroughRoad mode is selected.
    ///     Drives §3/§4 and the tilted roundabout ring plane. Depends only on spline parameters (constant
    ///     across iterations).
    /// </summary>
    private static bool IsAffineThroughActive(UnifiedRoadNetwork network) =>
        network.Splines.Any(s =>
        {
            var jh = s.Parameters.JunctionHarmonizationParameters;
            return jh is { EnableParabolicJunctionBlend: false, EnableHermiteJunctionBlend: false,
                       EnableAffineJunctionLeveling: true }
                   && jh.AffineJunctionTargetMode == AffineJunctionTargetMode.ThroughRoad;
        });
```

- [ ] **Step 2: Use the helper at the existing §3/§4 site**

Replace the inline `var affineThroughActive = network.Splines.Any(s => { ... });` block (currently ~462–468) with:

```csharp
        var affineThroughActive = IsAffineThroughActive(network);
```

- [ ] **Step 3: Pass `useTiltedPlane` at the Phase 2.6 call**

In the Phase 2.6 block, change the `HarmonizeRoundaboutElevations` call (currently ~335–340) to add the new argument:

```csharp
                        var roundaboutHarmonizationResult = _roundaboutHarmonizer.HarmonizeRoundaboutElevations(
                            network,
                            roundaboutJunctionInfos,
                            heightMap,
                            metersPerPixel,
                            skipConnectingRoadBlending: true,
                            useTiltedPlane: IsAffineThroughActive(network));
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Run the full test suite to verify nothing regressed**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true`
Expected: PASS (all; the prior green count + the new tests).

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat(no-blend): activate tilted roundabout ring plane on the affine/blend-off path

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Diagnostics — connector grade + post-fix verification

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (the `[NO-BLEND RAB]` block, ~700–810)

- [ ] **Step 1: Add the connector longitudinal grade + >7% flag to the RAB dump**

In the `[NO-BLEND RAB]` per-connector output, the `CONNECTOR` line currently logs `RING-SEAM-STEP` and `slope`. Add an explicit longitudinal grade (Δroad-Z / Δlength over the connector) and a `>7%` flag. After the line that computes `ringSeamStep`/`authorityStep`, add:

```csharp
                // Connector longitudinal grade (end-to-end), civil target ≤4-5% (max 7% drivable).
                var connGrade = connLen > 0.01f
                    ? MathF.Abs(connRingEndZ - (farCs?.TargetElevation ?? connRingEndZ)) / connLen
                    : 0f;
                var gradeFlag = connGrade > 0.07f ? " >7%!" : "";
```

Then append to the `CONNECTOR` AppendLine (extend its interpolated string):

```csharp
                    $"grade={connGrade * 100f:F1}%{gradeFlag} " +
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "chore(no-blend): RAB diagnostic logs connector grade + >7% flag

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Validation (user-driven; agent cannot run generation)

After all tasks: the user regenerates `franco_same_prio` and shares the log. Expected in `[NO-BLEND RAB]`:
- `RING-SEAM-STEP` ≈ 0 at every connector (connectors now meet the ring flush — approach A).
- `[NO-BLEND RAB PLANE]` shows the ring tilted (preClampTilt reported; clamped at 6% if terrain steeper).
- Connector `grade` within target (no `>7%!` on the short stubs).
- Visual: connectors meet the ring with no step/cliff; the ring sits closer to terrain (smaller embankment); ring stays planar/drivable; no new walls.

If a connector shows a residual `RING-SEAM-STEP` or a new step at the far (ongoing) seam, return to systematic-debugging: the §3 iteration may need the roundabout-plane Z to settle — inspect per-iteration `[NO-BLEND RAB]` and the far-junction `[NO-BLEND OWN]` lines.

### Out of scope (do not implement here)
- 2.5% Dachprofil crown banking on the ring (follow-up).
- Legacy blend-on path roundabout behavior (unchanged — `useTiltedPlane` defaults false).
- §2 network-wide absolute depth (parked).
- §7 TEMP-diagnostic + flag cleanup (tracked separately).
