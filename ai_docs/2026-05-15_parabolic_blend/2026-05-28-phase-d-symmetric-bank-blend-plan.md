# Phase D — Symmetric Bank Blend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `BlendSplineProfileParabolic` and `BlendShortConnectorCompositional` write Hermite-h00-blended `BankAngleRadians` through the blend zone, mirroring the legacy h00 path's symmetric (elevation, bank) behavior, so terminating-road edges sit flush with the primary's surface at the junction anchor.

**Architecture:** Mirror legacy `BlendSplineProfile`'s symmetric bank+elevation write (lines 2005-2009 in UnifiedJunctionProfileBlender.cs) inside the two parabolic paths. Bank uses Hermite h00 (`2t³ − 3t² + 1`) regardless of whether elevation goes parabolic or cubic — h00 gives C1 at both ends with no new constraint fields needed. Behind `EnableParabolicBankBlend` flag, default true.

**Tech Stack:** .NET 9, C#, xUnit. No new libraries.

**Spec:** [2026-05-28-phase-d-symmetric-bank-blend-design.md](2026-05-28-phase-d-symmetric-bank-blend-design.md)

**Branch:** `feature/parabolic_blend_phase_c_wip` (continues current branch — Phase C work already committed).

---

## File Structure

| File | Action | Responsibility |
|---|---|---|
| [BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs) | Modify | Add `EnableParabolicBankBlend` flag |
| [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) | Modify | Add bank-write inside `BlendSplineProfileParabolic` and `BlendShortConnectorCompositional`; thread `originalBankAngles` into compositional helper; update both call sites |
| [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs) | Create | All Phase D tests (6) |
| Existing Phase A/B/C tests | Audit | Update any that assert pre-blend bank is preserved through parabolic path |

---

## Conventions used throughout the plan

- **Build:** `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
- **Test single file:** `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests"`
- **Test full suite:** `dotnet test BeamNgTerrainPoc.Tests`
- **Run main app build** (sanity check, app may be running and DLLs locked — that's normal per memory):
  `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
- **Commit style:** match recent log — `feat:`, `test:`, `refactor:`, `docs:`. Phase tag in subject when scope-relevant.

---

## Task 1: Add `EnableParabolicBankBlend` flag

**Files:**
- Modify: [BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs:74-75](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs#L74-L75)

- [ ] **Step 1: Add the flag immediately after `EnableParabolicJunctionBlend`**

Find the line:
```csharp
public bool EnableParabolicJunctionBlend { get; set; } = true;
```

Insert directly below it:
```csharp

    /// <summary>
    ///     Phase D — when true, BlendSplineProfileParabolic and
    ///     BlendShortConnectorCompositional write Hermite-h00-blended
    ///     BankAngleRadians through the blend zone, mirroring the legacy h00
    ///     path's symmetric (elevation, bank) behavior. When false, the
    ///     parabolic paths leave BankAngleRadians at its pre-blend (natural)
    ///     value — historical pre-fix behavior which produces cross-slope
    ///     wedge artefacts at primary-vs-terminating bank mismatches. Default
    ///     true; the flag exists only as a regression escape hatch.
    /// </summary>
    public bool EnableParabolicBankBlend { get; set; } = true;
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: build succeeds. No tests need run yet — this property has no readers.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add EnableParabolicBankBlend flag (Phase D)"
```

---

## Task 2: TDD cycle 1 — bank-write in parabolic path, single-end constraint

**Files:**
- Create: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs)
- Modify: [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) — `BlendSplineProfileParabolic`

- [ ] **Step 1: Create the test file with the first failing test**

Create `BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs` with:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PhaseDBankBlendTests
{
    private static NetworkJunction StubJunction() =>
        new() { Position = Vector2.Zero, JunctionId = 0, Type = JunctionType.TJunction };

    /// <summary>Builds a spline whose CS at index i sits at (i·spacing, 0) with supplied elevation and natural bank.</summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> elev,
                    Dictionary<int, float> bank)
        BuildSpline(int n, float spacing, Func<int, float> elevAt, Func<int, float>? bankAt = null)
    {
        bankAt ??= _ => 0f;
        var sections = new List<UnifiedCrossSection>();
        var elev = new Dictionary<int, float>();
        var bank = new Dictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var z = elevAt(i);
            var b = bankAt(i);
            var cs = new UnifiedCrossSection
            {
                Index = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = z,
                BankAngleRadians = b,
                EffectiveRoadWidth = 6f
            };
            sections.Add(cs);
            elev[i] = z;
            bank[i] = b;
        }
        return (sections, elev, bank);
    }

    [Fact]
    public void BankBlendOn_StartConstraint_BankAtAnchorEqualsConstraint_DecaysToNaturalAtL()
    {
        // 100-CS straight spline at z=100, natural bank = 0 everywhere.
        // Start constraint imposes bank = 4.5° (0.0785 rad) at the junction anchor.
        // Blend distance L = 30m. With EnableParabolicBankBlend = true:
        //   bank at d=0  → constraint bank (0.0785 rad)
        //   bank at d=L  → natural bank (0)
        //   monotone decay in between (h00 is monotone on [0,1]).
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var constraintBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = constraintBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Anchor bank exact match.
        Assert.Equal(constraintBank, sections[0].BankAngleRadians, 4);

        // At d=L (index 30) bank should be back to natural (0).
        Assert.Equal(0f, sections[30].BankAngleRadians, 3);

        // At d=15 (midpoint) bank should be between 0 and constraint, strictly.
        Assert.InRange(sections[15].BankAngleRadians, 0.001f, constraintBank - 0.001f);

        // Past L bank stays untouched.
        Assert.Equal(0f, sections[50].BankAngleRadians, 4);
    }
}
```

- [ ] **Step 2: Run the test, expect failure**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests"`
Expected: COMPILATION FAILURE — `BlendSplineProfileParabolic` does not yet accept `enableBankBlend` parameter.

This is a real-fail before implementation. Proceed to add the parameter and impl.

- [ ] **Step 3: Add `enableBankBlend` parameter to `BlendSplineProfileParabolic`**

In `UnifiedJunctionProfileBlender.cs`, locate the `BlendSplineProfileParabolic` signature (around line 1127). Append a new parameter at the end:

```csharp
    internal static int BlendSplineProfileParabolic(
        List<UnifiedCrossSection> sections,
        JunctionEndpointConstraint? startConstraint,
        JunctionEndpointConstraint? endConstraint,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles,
        bool enableC1 = false,
        SplineClaimedZone? claimedZone = null,
        bool enableShortConnectorBlend = false,
        bool enableStretchL = false,
        float stretchLMaxCap = float.PositiveInfinity,
        IReadOnlyList<float>? otherJunctionDistancesOnSpline = null,
        float midCrossingSafetyMarginMeters = 2.0f,
        bool enableBankBlend = false)
```

The default is `false` so existing tests continue to pass unchanged. Production callers will pass `true` when the flag is on.

- [ ] **Step 4: Compute bank deltas once before the main loop**

Inside `BlendSplineProfileParabolic`, locate the main per-CS loop (currently starts around line 1328 with `for (var i = 0; i < sections.Count; i++)`). Immediately **before** that loop, insert:

```csharp
        // Phase D — bank deltas (computed once; written per-CS inside the loop).
        // Mirror of the legacy h00 path's startBankDelta / endBankDelta at line 1725-1728.
        var startBankDelta = 0f;
        var endBankDelta = 0f;
        if (enableBankBlend)
        {
            if (startConstraint != null)
            {
                var startEndpointBank = originalBankAngles.GetValueOrDefault(
                    sections[0].Index, sections[0].BankAngleRadians);
                startBankDelta = startConstraint.BankAngleRadians - startEndpointBank;
            }
            if (endConstraint != null)
            {
                var endEndpointBank = originalBankAngles.GetValueOrDefault(
                    sections[^1].Index, sections[^1].BankAngleRadians);
                endBankDelta = endConstraint.BankAngleRadians - endEndpointBank;
            }
        }
```

- [ ] **Step 5: Add bank-write inside the per-CS loop**

Inside the same loop body, **after** the elevation write block (`if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f) { cs.TargetElevation = newElev; modified++; }`), insert:

```csharp
            // Phase D — symmetric bank correction. Bank ramps from natural at d=L
            // to constraint bank at d=0 via Hermite h00, C1 at both ends.
            if (enableBankBlend && (inStartZone || inEndZone))
            {
                float startH00 = 0f, endH00 = 0f;
                if (inStartZone && startBlendDist > 0.01f)
                {
                    var t = d / startBlendDist;
                    startH00 = 2f * t * t * t - 3f * t * t + 1f;
                }
                if (inEndZone && endBlendDist > 0.01f)
                {
                    var t = distFromEnd / endBlendDist;
                    endH00 = 2f * t * t * t - 3f * t * t + 1f;
                }

                var naturalBank = originalBankAngles.GetValueOrDefault(cs.Index, cs.BankAngleRadians);
                var newBank = naturalBank + startBankDelta * startH00 + endBankDelta * endH00;

                if (MathF.Abs(newBank - cs.BankAngleRadians) > 0.0001f)
                {
                    cs.BankAngleRadians = newBank;
                    // Do not increment `modified` again — elevation already accounted for it.
                }
            }
```

- [ ] **Step 6: Run the test, expect pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_StartConstraint_BankAtAnchorEqualsConstraint_DecaysToNaturalAtL"`
Expected: PASS.

If it fails, check:
- Is `enableBankBlend: true` actually being passed through?
- Is `startBankDelta` non-zero (natural=0, constraint=0.0785 → delta=0.0785)?
- Is the h00 formula correct at t=0 (should be 1)?

- [ ] **Step 7: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs
git commit -m "feat: write bank in parabolic blend, single-end (Phase D)"
```

---

## Task 3: TDD cycle 2 — bank-write at both ends

**Files:**
- Modify: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs) — append test

- [ ] **Step 1: Append the both-ends test**

Append inside the `PhaseDBankBlendTests` class:

```csharp
    [Fact]
    public void BankBlendOn_BothEnds_MatchEachConstraintAtItsAnchor()
    {
        // 100-CS straight spline, natural bank = 0. Constraints at both ends with
        // different banks. The two zones do not overlap (30 + 30 = 60 < 99).
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var startBank = 4.5f * MathF.PI / 180f;   // 4.5°
        var endBank   = -2.0f * MathF.PI / 180f;  // -2.0° (opposite tilt)
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = startBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = endBank,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(-1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Each anchor matches its own constraint exactly.
        Assert.Equal(startBank, sections[0].BankAngleRadians, 4);
        Assert.Equal(endBank, sections[^1].BankAngleRadians, 4);

        // Middle (d=50, outside both blend zones) untouched.
        Assert.Equal(0f, sections[50].BankAngleRadians, 4);
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_BothEnds"`
Expected: PASS (the loop from Task 2 already handles both ends independently).

If it fails, the per-CS bank computation in Task 2 likely has an asymmetry — both `startH00` and `endH00` paths must be reached.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs
git commit -m "test: Phase D both-ends bank constraint coverage"
```

---

## Task 4: Plumb `originalBankAngles` into `BlendShortConnectorCompositional`

**Files:**
- Modify: [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) — `BlendShortConnectorCompositional` signature + the two call sites inside `BlendSplineProfileParabolic`

This task is a pure refactor: signature change, parameter threading, no behavior change. Bank write into compositional comes in Task 5.

- [ ] **Step 1: Add `originalBankAngles` and `enableBankBlend` parameters to `BlendShortConnectorCompositional`**

Locate the `BlendShortConnectorCompositional` signature (around line 1489). Change it to:

```csharp
    private static int BlendShortConnectorCompositional(
        List<UnifiedCrossSection> sections,
        float[] distFromStart,
        float roadLength,
        JunctionEndpointConstraint startConstraint,
        JunctionEndpointConstraint endConstraint,
        Dictionary<int, float> originalElevations,
        Dictionary<int, float> originalBankAngles,
        bool enableC1,
        SplineClaimedZone? claimedZone,
        bool enableBankBlend)
```

- [ ] **Step 2: Update the call site inside `BlendSplineProfileParabolic`**

The compositional helper is called from one place inside `BlendSplineProfileParabolic` (around line 1165). Change:

```csharp
            if (enableShortConnectorBlend)
            {
                return BlendShortConnectorCompositional(
                    sections, distFromStart, roadLength,
                    startConstraint, endConstraint,
                    originalElevations, enableC1, claimedZone);
            }
```

to:

```csharp
            if (enableShortConnectorBlend)
            {
                return BlendShortConnectorCompositional(
                    sections, distFromStart, roadLength,
                    startConstraint, endConstraint,
                    originalElevations, originalBankAngles,
                    enableC1, claimedZone, enableBankBlend);
            }
```

- [ ] **Step 3: Build, run full Phase D test set, expect green**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Then: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests"`
Expected: 2/2 still green (no behavior change to existing tests).

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "refactor: thread originalBankAngles into BlendShortConnectorCompositional"
```

---

## Task 5: TDD cycle 3 — bank-write in compositional path

**Files:**
- Modify: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs) — append test
- Modify: [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs) — bank computation in `BlendShortConnectorCompositional`

- [ ] **Step 1: Add the failing test**

Append inside the `PhaseDBankBlendTests` class:

```csharp
    [Fact]
    public void BankBlendOn_ShortConnectorCompositional_BothAnchorsMatch()
    {
        // 20-CS short spline (19m long), natural bank = 0. Both blend zones
        // are 15m each, so startBlendDist + endBlendDist = 30 > 19 — dispatch
        // hits the compositional path when enableShortConnectorBlend=true.
        var (sections, elev, bank) = BuildSpline(20, 1f, _ => 100f, _ => 0f);
        var startBank = 4.5f * MathF.PI / 180f;
        var endBank   = -2.0f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = startBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 15f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };
        var endConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = endBank,
            IsSplineStart = false, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 15f,
            PrimaryTangentDirection = new Vector2(-1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: true,   // dispatch to compositional
            enableStretchL: false,
            enableBankBlend: true);

        // Each anchor matches its own constraint within tolerance.
        // Tolerance is looser than the long-spline case because OverlapTaper
        // composition is not perfectly localized at the endpoints.
        Assert.Equal(startBank, sections[0].BankAngleRadians, 3);
        Assert.Equal(endBank, sections[^1].BankAngleRadians, 3);
    }
```

- [ ] **Step 2: Run the test, expect failure**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_ShortConnectorCompositional"`
Expected: FAIL — `BlendShortConnectorCompositional` still leaves `BankAngleRadians` untouched.

- [ ] **Step 3: Compute bank deltas once at the top of `BlendShortConnectorCompositional`**

Inside `BlendShortConnectorCompositional`, locate the start of the per-CS loop (around line 1572, `for (var i = 0; i < sections.Count; i++)`). Immediately **before** that loop, insert:

```csharp
        // Phase D — bank deltas (computed once; per-CS write inside the loop).
        var startBankDelta = 0f;
        var endBankDelta = 0f;
        if (enableBankBlend)
        {
            var startEndpointBank = originalBankAngles.GetValueOrDefault(
                sections[0].Index, sections[0].BankAngleRadians);
            var endEndpointBank = originalBankAngles.GetValueOrDefault(
                sections[^1].Index, sections[^1].BankAngleRadians);
            startBankDelta = startConstraint.BankAngleRadians - startEndpointBank;
            endBankDelta   = endConstraint.BankAngleRadians   - endEndpointBank;
        }
```

- [ ] **Step 4: Add bank composition inside the per-CS loop**

Inside the same loop, after the elevation write (`if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f) { cs.TargetElevation = newElev; modified++; }`), insert:

```csharp
            // Phase D — bank composition. Each end contributes a per-anchor h00 profile;
            // the two are composed with the same OverlapTaper weights as elevation.
            if (enableBankBlend)
            {
                float startH00 = 0f, endH00 = 0f;
                if (startBlendDist > 0.01f && d < startBlendDist)
                {
                    var t = d / startBlendDist;
                    startH00 = 2f * t * t * t - 3f * t * t + 1f;
                }
                if (endBlendDist > 0.01f && distFromEnd < endBlendDist)
                {
                    var t = distFromEnd / endBlendDist;
                    endH00 = 2f * t * t * t - 3f * t * t + 1f;
                }

                var naturalBank = originalBankAngles.GetValueOrDefault(cs.Index, cs.BankAngleRadians);
                var bankFromStart = naturalBank + startBankDelta * startH00;
                var bankFromEnd   = naturalBank + endBankDelta   * endH00;

                // Reuse the same wStart/wEnd/wTotal already computed for elevation above.
                var newBank = (bankFromStart * wStart + bankFromEnd * wEnd) / wTotal;

                if (MathF.Abs(newBank - cs.BankAngleRadians) > 0.0001f)
                    cs.BankAngleRadians = newBank;
            }
```

- [ ] **Step 5: Run the test, expect pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_ShortConnectorCompositional"`
Expected: PASS.

If the start-anchor or end-anchor assertion fails by a small margin, check that `wStart`/`wEnd`/`wTotal` are in scope (defined earlier in the loop). If they were renamed at any point during the elevation write, reuse the exact local names.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs
git commit -m "feat: write bank in compositional blend (Phase D)"
```

---

## Task 6: TDD cycle 4 — Phase C stretched-L interaction

**Files:**
- Modify: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs)

- [ ] **Step 1: Append the stretched-L test**

```csharp
    [Fact]
    public void BankBlendOn_StretchedL_BankZoneExtendsWithElevationZone()
    {
        // Reuse the franco junction 20 geometry from PhaseCStretchLBlendTests:
        // natural -16.7% descent, anchor at z=98.807 with slope -6.8%. With
        // stretchL on, L extends from 30 to ~40m. Bank constraint is 4.5°.
        // After Phase D, the bank zone follows the stretched L: at d=35 the
        // bank should still be inside the (now-longer) blend zone, NOT back
        // at natural 0.
        var (sections, elev, bank) = BuildSpline(100, 1f,
            i => 96.703f - 0.16725f * i,
            _ => 0f);
        var constraintBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 98.807f, Slope = -0.06805f, BankAngleRadians = constraintBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: true,
            enableBankBlend: true);

        // d=35 is inside the stretched zone (~[0, 40]) → bank still nonzero.
        Assert.True(MathF.Abs(sections[35].BankAngleRadians) > 0.001f,
            $"Expected bank at d=35 still inside stretched blend zone; got {sections[35].BankAngleRadians:F4}");

        // d=45 is past the stretched zone → bank back to natural (0).
        Assert.Equal(0f, sections[45].BankAngleRadians, 3);
    }
```

- [ ] **Step 2: Run the test, expect pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_StretchedL"`
Expected: PASS (no new code needed — Task 2's `startBlendDist` reference in the bank block picks up the stretched value automatically).

If it fails at "d=35 nonzero", suspect that `startBlendDist` was captured *before* the stretch logic ran; move the bank-delta computation in Task 2 Step 4 to **after** the stretch-L block. (Look for `// Phase C — stretch L` comment around line 1237; Phase D's bank-delta init must come after stretch-L's `startBlendDist = stretched` assignment.)

- [ ] **Step 3: If test failed in step 2, fix and re-run**

Move the Phase D bank-delta init block from Task 2 Step 4 to immediately before the main per-CS loop, AFTER the two `if (enableStretchL && …)` blocks at lines ~1237 and ~1280. Re-run, confirm PASS.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "test: Phase D bank zone extends with stretched-L"
```

(If Step 3 was a no-op, omit the .cs file from the add.)

---

## Task 7: TDD cycle 5 — flag-off regression guard

**Files:**
- Modify: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs)

- [ ] **Step 1: Append the flag-off guard**

```csharp
    [Fact]
    public void BankBlendOff_ParabolicPathLeavesBankUntouched()
    {
        // Escape hatch: with enableBankBlend = false, bank values must remain
        // exactly the natural pre-blend value (parabolic path's current behavior).
        // Protects callers that pin the flag off to avoid the new behavior.
        var (sections, elev, bank) = BuildSpline(100, 1f, _ => 100f, _ => 0f);
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f,
            BankAngleRadians = 4.5f * MathF.PI / 180f,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: false);   // OFF

        for (var i = 0; i < sections.Count; i++)
            Assert.Equal(0f, sections[i].BankAngleRadians, 6);
    }
```

- [ ] **Step 2: Run the test, expect pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOff"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs
git commit -m "test: Phase D flag-off regression guard"
```

---

## Task 8: TDD cycle 6 — franco junction-20 regression guard

**Files:**
- Modify: [BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs](../../BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs)

- [ ] **Step 1: Append the franco regression guard**

```csharp
    [Fact]
    public void BankBlendOn_FrancoJunction20Like_AnchorEdgesMatchPrimarySurface()
    {
        // Synthetic stand-in for the franco junction 20 cross-slope artefact:
        // - Terminating road's natural bank = 0.8° (its own curvature-driven value).
        // - Primary road's bank at the junction = 4.5° (the constraint target).
        // After Phase D the terminating road's per-CS bank at d=0 must equal the
        // constraint (4.5°), so that Step 4's edge derivation (TargetElevation ±
        // halfWidth × sin(bank)) lines its edges up with the primary's surface.
        var (sections, elev, bank) = BuildSpline(100, 1f,
            _ => 100f,
            _ => 0.8f * MathF.PI / 180f);   // natural bank = 0.8° everywhere
        var primaryBank = 4.5f * MathF.PI / 180f;
        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = primaryBank,
            IsSplineStart = true, Junction = StubJunction(), FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank,
            enableC1: false, claimedZone: null,
            enableShortConnectorBlend: false,
            enableStretchL: false,
            enableBankBlend: true);

        // Anchor CS bank == primary's bank exactly (this is the contract).
        Assert.Equal(primaryBank, sections[0].BankAngleRadians, 4);

        // Derived anchor edge elevations must match what Step 4 would produce
        // for the primary's surface at the terminating road's edge positions.
        var halfWidth = sections[0].EffectiveRoadWidth / 2f;
        var anchorLeftEdge  = sections[0].TargetElevation - halfWidth * MathF.Sin(sections[0].BankAngleRadians);
        var anchorRightEdge = sections[0].TargetElevation + halfWidth * MathF.Sin(sections[0].BankAngleRadians);
        var primarySin = MathF.Sin(primaryBank);
        var expectedLeftEdge  = 100f - halfWidth * primarySin;
        var expectedRightEdge = 100f + halfWidth * primarySin;
        Assert.Equal(expectedLeftEdge,  anchorLeftEdge,  3);
        Assert.Equal(expectedRightEdge, anchorRightEdge, 3);

        // At d=L (index 30) bank decays back to natural 0.8°.
        var naturalBank = 0.8f * MathF.PI / 180f;
        Assert.Equal(naturalBank, sections[30].BankAngleRadians, 3);
    }
```

- [ ] **Step 2: Run the test, expect pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~PhaseDBankBlendTests.BankBlendOn_FrancoJunction20Like"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/PhaseDBankBlendTests.cs
git commit -m "test: Phase D franco junction-20 anchor-edge guard"
```

---

## Task 9: Wire `EnableParabolicBankBlend` into the two production call sites

**Files:**
- Modify: [BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:161-168 and 225-232](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L161-L232)

Until this task, the flag exists but isn't read — the production pipeline still uses the default `enableBankBlend: false`. This wires it through.

- [ ] **Step 1: Update the pass-1 call site**

Locate the first call to `BlendSplineProfileParabolic` inside `ApplyUnifiedProfiles` (around line 162). Add a new named argument at the end:

```csharp
            result.ModifiedCrossSections += jhParams.EnableParabolicJunctionBlend
                ? BlendSplineProfileParabolic(
                    sections, startConstraint, endConstraint, originalElevations, originalBankAngles,
                    enableC1: jhParams.EnableBlendZoneEndC1,
                    claimedZone: _splineClaimedZones?.GetValueOrDefault(splineId),
                    enableShortConnectorBlend: jhParams.EnableShortConnectorBlend,
                    enableStretchL: jhParams.EnableBlendDistanceStretchToMatchSlope,
                    otherJunctionDistancesOnSpline: _midSplineCrossingDistancesBySpline?.GetValueOrDefault(splineId),
                    enableBankBlend: jhParams.EnableParabolicBankBlend)
                : BlendSplineProfile(
                    sections, startConstraint, endConstraint, originalElevations, originalBankAngles);
```

- [ ] **Step 2: Update the pass-2 call site**

The pass-2 call to `BlendSplineProfileParabolic` is around line 226 (inside the `if (deferredTerminatingSplines.Count > 0)` branch). Apply the same `enableBankBlend: jhParams.EnableParabolicBankBlend` addition.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests`
Expected: all Phase D tests still green; existing Phase A/B/C tests — most still green, but expect 0-N failures from tests that asserted pre-blend bank was preserved. Note the failing tests by name; Task 10 audits and fixes them.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: wire EnableParabolicBankBlend into production call sites (Phase D)"
```

---

## Task 10: Audit existing tests for stale "bank unchanged" assertions

**Files:**
- Audit: all files under [BeamNgTerrainPoc.Tests/Junction/](../../BeamNgTerrainPoc.Tests/Junction/)

- [ ] **Step 1: Find candidate assertions**

Use Grep across the test project:
```
pattern: BankAngleRadians
path:    BeamNgTerrainPoc.Tests
glob:    *.cs
```

For each match, inspect the assertion:
- If it asserts `cs.BankAngleRadians == 0f` AND the test calls `BlendSplineProfileParabolic` AND the constraint has non-zero `BankAngleRadians` → that assertion asserted the BUG. Fix.
- If the assertion is about non-blender concerns (banking calculator unit tests, geometry tests) → leave alone.
- If the test passes `enableBankBlend: false` explicitly → leave alone (escape-hatch test).
- If the test was already green in Task 9 Step 3 → leave alone (the test setup avoided the new behavior).

For each test that needs fixing, update the assertion to reflect Phase D's contract: anchor bank equals constraint bank; mid-zone bank is between natural and constraint; past-zone bank equals natural.

- [ ] **Step 2: Run the full suite again**

Run: `dotnet test BeamNgTerrainPoc.Tests`
Expected: all tests green.

- [ ] **Step 3: Commit if changes were made**

```bash
git add BeamNgTerrainPoc.Tests/Junction/<changed-files>
git commit -m "test: update parabolic-blend tests for Phase D bank-write contract"
```

If no tests required updating, skip the commit.

---

## Task 11: Update roadmap + handoff doc

**Files:**
- Modify: [ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md)
- Modify: [ai_docs/2026-05-15_parabolic_blend/2026-05-26-phase-c-handoff-and-banking-followup.md](2026-05-26-phase-c-handoff-and-banking-followup.md) (mark §"New issue raised today" as Phase D, complete)

- [ ] **Step 1: Add Phase D row to the roadmap**

Open the roadmap. Find the table of phases (B, C, …). Add a new row:

```
| D | Symmetric bank blend | ✅ Done YYYY-MM-DD | Adds Hermite h00 bank correction to parabolic + compositional paths; `EnableParabolicBankBlend` default true. Spec + plan + tests in `ai_docs/2026-05-15_parabolic_blend/2026-05-28-phase-d-*.md`. |
```

(Use the actual date the implementation completes.)

- [ ] **Step 2: Mark the banking-followup section as resolved in the handoff doc**

In `2026-05-26-phase-c-handoff-and-banking-followup.md`, at the top of the "## New issue raised today — bank/cross-slope mismatch at junctions" section, add a status line:

```markdown
**Status (updated YYYY-MM-DD):** Resolved by Phase D — see
[2026-05-28-phase-d-symmetric-bank-blend-design.md](2026-05-28-phase-d-symmetric-bank-blend-design.md)
and the corresponding plan. Awaiting franco visual validation.
```

- [ ] **Step 3: Commit**

```bash
git add ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-roadmap.md ai_docs/2026-05-15_parabolic_blend/2026-05-26-phase-c-handoff-and-banking-followup.md
git commit -m "docs: mark Phase D complete in parabolic-blend roadmap and handoff"
```

---

## Task 12: Franco visual validation (manual, post-implementation)

This task is **not automatable** — it requires running the main app on franco_same_prio and comparing the junction with the screenshot the user captured 2026-05-26.

- [ ] **Step 1: Build the main app**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: build succeeds. DLL-lock errors from the running app are normal (per memory).

- [ ] **Step 2: Regenerate franco_same_prio terrain**

Launch the app, load the franco_same_prio map (or whichever scene the user uses for this validation), and run terrain generation with `EnableParabolicBankBlend = true` (the new default).

- [ ] **Step 3: Inspect junctions 20 and the connecting-road-to-primary cases**

Compare against the 2026-05-26 screenshot. Cross-slope wedge at the merge point should be gone.

- [ ] **Step 4: Hand back to user for sign-off**

If the wedge is gone → Phase D ships. If a different artefact appears → STOP, do not push fixes; report to the user with the new symptom (likely a Phase D follow-up, possibly the `PrimaryBankSlope` field discussed in the spec's "Out of scope" section).

---

## Self-Review Summary

| Spec section | Plan coverage |
|---|---|
| Problem | Tasks 2/3/5/8 collectively demonstrate the bug + fix |
| Goal | Tasks 2/3/5 implement; Task 8 validates the franco-shaped case |
| Architecture | Tasks 2/4/5 implement at exactly the locations the spec names |
| Algorithm | Task 2 Step 4-5, Task 5 Step 3-4 contain the formula verbatim |
| Phase C interaction | Task 6 |
| Phase B.3 caveat | Plan does not touch cubic — spec deliberately defers |
| Flag | Task 1 adds it; Task 9 wires it in |
| Tests #1-6 | Tasks 2/3/5/6/7/8 (one test per task, in spec order) |
| Risks: stale existing tests | Task 10 |
| Risks: FinalSnap double-write | No-op by construction (FinalSnap writes the same value the blend already wrote); covered implicitly by Task 9 full-suite run |
| Risks: compositional `originalBankAngles` plumbing | Task 4 (pure refactor, no behavior change) |

Type consistency: `enableBankBlend` parameter name is consistent across Tasks 2, 4, 5, 9. `EnableParabolicBankBlend` flag name is consistent across Tasks 1 and 9. The bank delta formula (`constraint.BankAngleRadians - originalBankAngles[endpoint.Index]`) is identical in Task 2 Step 4 and Task 5 Step 3.

Placeholder scan: no TBDs, no "implement later", no "similar to". Every code step contains the actual code.
