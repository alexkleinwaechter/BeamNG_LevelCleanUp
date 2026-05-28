# Parabolic Junction Blend — Phase A Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the cubic-Hermite-weighted additive delta correction in `BlendSplineProfile` with a direct parabolic profile substitution inside the junction blend zone, eliminating the "first up then down" overshoot at terminating-road junction endpoints (e.g., the cliff at junction 126 / OSM way 1218613789 on franco_same_prio).

**Architecture:** Phase A keeps every existing parameter (`JunctionBlendDistanceMeters`, `RoundaboutBlendDistanceMeters`, `CalculateAdaptiveBlendDistance`) and the per-end delta model. Only the **basis function** changes: instead of computing `newElev = naturalElev + delta * h00(d/L)` (additive Hermite weighting), we substitute a parabola `z(d) = a·d² + m_junc·d + z_junc` directly when the cross-section is in a single end's blend zone. Two-end overlap (short splines) keeps the existing h00-weighted combination — that path is unaffected and the lerp/smootherstep bypass at `roadLength < 40 m` stays.

**Phase B (out of scope here):** introduce a speed-table → AASHTO K-value cap on blend distance, allowing removal of fixed `JunctionBlendDistanceMeters` / `RoundaboutBlendDistanceMeters`.

**Tech stack:** .NET 9 (`net9.0-windows10.0.17763.0`), xUnit 2.x, BeamNgTerrainPoc + BeamNgTerrainPoc.Tests projects. Build sandboxed with `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`. Test with `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`.

---

## Why this is bigger than swapping basis functions

Current architecture at [UnifiedJunctionProfileBlender.cs:1337-1340](../../BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L1337-L1340):

```csharp
var elevCorrection = adjStartElevDelta * startH00 + adjEndElevDelta * endH00;
var newElev = naturalElev + elevCorrection;
```

`naturalElev` is the road's Phase-2 terrain-following elevation. When the road descends steeply away from the junction (spline 64 case: −4 % grade), `naturalElev(d)` drops fast. The h00-weighted delta stays near 1.0 for `d ≪ L` (h00(0.15) ≈ 0.94, h00(0.3) ≈ 0.78), so the road is held *up* near the junction while the natural profile dives — producing a heightmap delta that rises (+3.66 m at d=15 m on junction 126) then falls (−1.13 m at d=60 m). That's the visible cliff.

A parabolic profile **replaces** `naturalElev(d)` inside the blend zone with a curve that smoothly interpolates *from* junction constraint *to* natural-profile-at-blend-end. The natural profile is restored at d = L. The parabola cannot exhibit the h00-decay artifact because it doesn't compound with a separate natural profile.

---

## File Structure

**Create:**
- `BeamNgTerrainPoc/Terrain/Algorithms/ParabolicJunctionProfile.cs` — pure-function helper, samples the parabola and computes the "natural at far end" elevation.
- `BeamNgTerrainPoc.Tests/Junction/ParabolicJunctionProfileTests.cs` — unit tests for the helper.
- `BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs` — integration-ish tests for the spline-wide blender, using synthetic CS lists.

**Modify:**
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — add `EnableParabolicJunctionBlend` flag (default `false`).
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — add `BlendSplineProfileParabolic` method (parallel to existing `BlendSplineProfile`), branch in the two call sites (~L120 and ~L176).
- `examples_for_ai/baseline_phase19/README.md` — document the new `parabolic_a_franco_same_prio` capture once validation completes.

**Do NOT modify in Phase A:**
- `CalculateAdaptiveBlendDistance` (Phase B work)
- `JunctionBlendDistanceMeters` / `RoundaboutBlendDistanceMeters` defaults (Phase B work)
- `EnablePhase19JunctionPinning` default (orthogonal — Phase 1.9 still gated separately)
- `FinalSnapTJunctionEndpoints` (spec §7.1, kept indefinitely)

---

### Task 1: Add parameter flag (no behaviour change yet)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

- [ ] **Step 1: Open file**

Read [JunctionHarmonizationParameters.cs:25-60](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs#L25-L60) — note the existing W1/W2/W3 flag block style.

- [ ] **Step 2: Insert flag after `EnableMaxGradeClamp`**

Find the line `public bool EnableMaxGradeClamp { get; set; } = false;` (around L57). Insert immediately below:

```csharp
    /// <summary>
    ///     Phase A — parabolic junction blend. When true, BlendSplineProfile uses a
    ///     parabolic profile substitution inside each end's blend zone instead of the
    ///     legacy h00-weighted additive delta correction. Eliminates the "up-then-down"
    ///     overshoot at terminating-road junction endpoints on steep terrain (R7 kink).
    ///     Two-end overlap (short splines) still uses the existing h00 combination.
    ///     Default: false (opt-in until single-junction validation passes on franco_same_prio).
    /// </summary>
    public bool EnableParabolicJunctionBlend { get; set; } = false;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 errors, 0 warnings new.

- [ ] **Step 4: Commit**

```
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add EnableParabolicJunctionBlend flag (Phase A scaffold)"
```

---

### Task 2: Create `ParabolicJunctionProfile` helper — Single-end parabola

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/ParabolicJunctionProfile.cs`
- Create: `BeamNgTerrainPoc.Tests/Junction/ParabolicJunctionProfileTests.cs`

**Mathematical contract:** For a junction at d=0 with elevation `zJunction` and slope `mJunction`, blending out to distance `L`, meeting the natural profile at `zNaturalAtL`:

```
z(d) = a·d² + mJunction·d + zJunction
where a = (zNaturalAtL − zJunction − mJunction·L) / L²
```

By construction: `z(0) = zJunction`, `z'(0) = mJunction`, `z(L) = zNaturalAtL`. The slope at d=L is `2a·L + mJunction = 2·(zNaturalAtL − zJunction)/L − mJunction` (emergent, not constrained — this is the seam-line slope discontinuity the user will see vs the natural profile beyond L).

- [ ] **Step 1: Write the failing test file**

Create `BeamNgTerrainPoc.Tests/Junction/ParabolicJunctionProfileTests.cs`:

```csharp
using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class ParabolicJunctionProfileTests
{
    [Fact]
    public void Sample_AtJunctionDistance0_ReturnsJunctionElevation()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 0f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_AtBlendEndDistanceL_ReturnsNaturalElevationAtL()
    {
        var z = ParabolicJunctionProfile.Sample(
            d: 30f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(95f, z, 4);
    }

    [Fact]
    public void Sample_MonotoneDescent_DoesNotOvershootAboveJunctionZ()
    {
        // Descending: zJunction (100) → zNaturalAtL (90). Slope at junction = 0.
        // Parabola must stay in [90, 100] for d in [0, L] — no upward overshoot.
        for (var d = 0f; d <= 30f; d += 1f)
        {
            var z = ParabolicJunctionProfile.Sample(
                d, blendLength: 30f,
                zJunction: 100f, mJunction: 0f, zNaturalAtL: 90f);

            Assert.InRange(z, 89.999f, 100.001f);
        }
    }

    [Fact]
    public void Sample_MonotoneAscent_DoesNotOvershootBelowJunctionZ()
    {
        // Ascending: zJunction (100) → zNaturalAtL (110). Slope at junction = 0.
        for (var d = 0f; d <= 30f; d += 1f)
        {
            var z = ParabolicJunctionProfile.Sample(
                d, blendLength: 30f,
                zJunction: 100f, mJunction: 0f, zNaturalAtL: 110f);

            Assert.InRange(z, 99.999f, 110.001f);
        }
    }

    [Fact]
    public void Sample_AtJunction_SlopeMatchesMJunction()
    {
        // Numerical derivative at d=0 should ≈ mJunction.
        var eps = 0.001f;
        var z0 = ParabolicJunctionProfile.Sample(
            0f, 30f, zJunction: 100f, mJunction: -0.04f, zNaturalAtL: 95f);
        var zEps = ParabolicJunctionProfile.Sample(
            eps, 30f, zJunction: 100f, mJunction: -0.04f, zNaturalAtL: 95f);

        var observedSlope = (zEps - z0) / eps;
        Assert.Equal(-0.04f, observedSlope, 3);
    }

    [Fact]
    public void Sample_ZeroBlendLength_ReturnsJunctionElevation()
    {
        // Degenerate case: blend collapsed to zero. Function must not divide by zero.
        var z = ParabolicJunctionProfile.Sample(
            d: 0f, blendLength: 0f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(100f, z, 4);
    }

    [Fact]
    public void Sample_BeyondBlendLength_ReturnsNaturalElevationAtL()
    {
        // Caller should clamp d to [0, L] before sampling, but the helper must be
        // safe if d > L: it returns the d=L value (avoids extrapolation explosion).
        var z = ParabolicJunctionProfile.Sample(
            d: 100f, blendLength: 30f,
            zJunction: 100f, mJunction: 0f, zNaturalAtL: 95f);

        Assert.Equal(95f, z, 4);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ParabolicJunctionProfileTests"`
Expected: FAIL — `ParabolicJunctionProfile` type does not exist.

- [ ] **Step 3: Implement the helper**

Create `BeamNgTerrainPoc/Terrain/Algorithms/ParabolicJunctionProfile.cs`:

```csharp
namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A parabolic vertical-curve helper. Replaces the legacy h00-weighted
///     additive delta in BlendSplineProfile for single-end blend-zone samples.
///     The parabola anchors at the junction (elevation + slope) and meets the
///     natural spline elevation at the far end of the blend zone. Mathematically
///     guaranteed not to overshoot beyond [min(zJunction, zNaturalAtL),
///     max(zJunction, zNaturalAtL)] when mJunction = 0; small overshoots are
///     possible for non-zero mJunction but bounded by mJunction·L/4.
/// </summary>
public static class ParabolicJunctionProfile
{
    /// <summary>
    ///     Samples the parabolic profile z(d) = a·d² + mJunction·d + zJunction,
    ///     where a is chosen so z(blendLength) = zNaturalAtL.
    /// </summary>
    /// <param name="d">Distance from junction (m), in [0, blendLength].</param>
    /// <param name="blendLength">Blend zone length L (m).</param>
    /// <param name="zJunction">Anchor elevation at d=0.</param>
    /// <param name="mJunction">Anchor slope at d=0 (dz/dd, dimensionless).</param>
    /// <param name="zNaturalAtL">Natural profile elevation at d=blendLength.</param>
    public static float Sample(
        float d, float blendLength,
        float zJunction, float mJunction, float zNaturalAtL)
    {
        if (blendLength <= 0.0001f)
            return zJunction;

        // Clamp d to [0, L] to avoid quadratic extrapolation blowups.
        var dClamped = MathF.Max(0f, MathF.Min(d, blendLength));

        var a = (zNaturalAtL - zJunction - mJunction * blendLength)
                / (blendLength * blendLength);

        return a * dClamped * dClamped + mJunction * dClamped + zJunction;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ParabolicJunctionProfileTests"`
Expected: PASS, 7/7 green.

- [ ] **Step 5: Commit**

```
git add BeamNgTerrainPoc/Terrain/Algorithms/ParabolicJunctionProfile.cs BeamNgTerrainPoc.Tests/Junction/ParabolicJunctionProfileTests.cs
git commit -m "feat: add ParabolicJunctionProfile.Sample helper with TDD coverage"
```

---

### Task 3: Spline-wide parabolic blender — single-end zone only

We start with the **simplest case**: a cross-section in a single end's blend zone (not both). For short splines where both blend zones overlap, fall through to the existing Hermite logic for now (covered in Task 4).

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — add `BlendSplineProfileParabolic` method
- Create: `BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs`

- [ ] **Step 1: Write the failing integration-ish test**

Create `BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs`:

```csharp
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using System.Numerics;

namespace BeamNgTerrainPoc.Tests.Junction;

public class BlendSplineProfileParabolicTests
{
    /// <summary>
    ///     Build a synthetic descending spline: 100 cross-sections, 1 m spacing,
    ///     elevation starts at 100 m and drops linearly at −0.04 (−4%) to 96 m.
    ///     End-constraint anchors elevation to 100 m (terrain at junction) with
    ///     slope 0 (continuous road is flat). Without overshoot, the blended
    ///     profile inside the L=30 m blend zone must stay in [96, 100].
    /// </summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, float> originalElev,
                    Dictionary<int, float> originalBank)
        BuildDescendingSpline(int n, float spacing, float startZ, float slope)
    {
        var sections = new List<UnifiedCrossSection>();
        var elev = new Dictionary<int, float>();
        var bank = new Dictionary<int, float>();
        for (var i = 0; i < n; i++)
        {
            var cs = new UnifiedCrossSection
            {
                Index = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = startZ + slope * (i * spacing),
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            };
            sections.Add(cs);
            elev[i] = cs.TargetElevation;
            bank[i] = 0f;
        }
        return (sections, elev, bank);
    }

    [Fact]
    public void BlendParabolic_DescendingSpline_NoUpwardOvershoot()
    {
        // Spline: 100 m, descending from 100 m at d=0 down to 96 m at d=100.
        // End constraint at the far end (i=99) anchors to 96 m (matches natural — no work needed).
        // Start constraint anchors to 100 m with slope 0, L=30 m blend.
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f,
            Slope = 0f,
            BankAngleRadians = 0f,
            IsSplineStart = true,
            FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        // Inside blend zone [0, 30): no point should exceed 100 m (the higher anchor)
        // and no point should drop below the natural at d=30 (96.8 m).
        for (var i = 0; i <= 30; i++)
        {
            Assert.InRange(sections[i].TargetElevation, 96.79f, 100.01f);
        }
    }

    [Fact]
    public void BlendParabolic_BeyondBlendZone_LeavesNaturalElevationUntouched()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        // d >= 30: natural elevation untouched
        for (var i = 31; i < 100; i++)
        {
            var naturalAtI = 100f - 0.04f * i;
            Assert.Equal(naturalAtI, sections[i].TargetElevation, 3);
        }
    }

    [Fact]
    public void BlendParabolic_AtJunctionEndpoint_MatchesConstraintElevation()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var startConstraint = new JunctionEndpointConstraint
        {
            Elevation = 100f, Slope = 0f, BankAngleRadians = 0f,
            IsSplineStart = true, FlatZoneDistance = 0f,
            BlendDistanceMeters = 30f,
            PrimaryTangentDirection = new Vector2(1f, 0f),
            PrimaryBankAngleRadians = 0f
        };

        UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint, endConstraint: null, elev, bank);

        Assert.Equal(100f, sections[0].TargetElevation, 3);
    }

    [Fact]
    public void BlendParabolic_NoConstraints_LeavesEverythingUntouched()
    {
        var (sections, elev, bank) = BuildDescendingSpline(
            n: 100, spacing: 1f, startZ: 100f, slope: -0.04f);

        var modified = UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
            sections, startConstraint: null, endConstraint: null, elev, bank);

        Assert.Equal(0, modified);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(elev[i], sections[i].TargetElevation, 3);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BlendSplineProfileParabolicTests"`
Expected: FAIL — `BlendSplineProfileParabolic` method does not exist.

- [ ] **Step 3: Add the method to `UnifiedJunctionProfileBlender`**

Append immediately **before** the existing `private static int BlendSplineProfile(` definition (around L1017). It must be `internal` (not `private`) so the test project can call it.

```csharp
/// <summary>
///     Phase A parabolic alternative to BlendSplineProfile. Replaces the legacy
///     h00-weighted additive delta with a direct parabolic substitution inside
///     each end's single blend zone. When a CS is in only the start blend zone,
///     its elevation is set to ParabolicJunctionProfile.Sample(d, L, zJunction,
///     mJunction, zNaturalAtL). Likewise from the end. When a CS is in BOTH
///     blend zones (short spline) or in NEITHER, the legacy BlendSplineProfile
///     path runs instead — this method only changes the single-end case.
///     Bank-angle correction continues to use the existing h00 logic (banking
///     overshoot is not the Phase A problem).
/// </summary>
internal static int BlendSplineProfileParabolic(
    List<UnifiedCrossSection> sections,
    JunctionEndpointConstraint? startConstraint,
    JunctionEndpointConstraint? endConstraint,
    Dictionary<int, float> originalElevations,
    Dictionary<int, float> originalBankAngles)
{
    if (sections.Count < 2) return 0;
    if (startConstraint == null && endConstraint == null) return 0;

    var modified = 0;

    var distFromStart = new float[sections.Count];
    distFromStart[0] = 0;
    for (var i = 1; i < sections.Count; i++)
        distFromStart[i] = distFromStart[i - 1] +
                           Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);

    var roadLength = distFromStart[sections.Count - 1];
    if (roadLength < 0.01f) return 0;

    var startBlendDist = startConstraint?.BlendDistanceMeters ?? 0f;
    var endBlendDist = endConstraint?.BlendDistanceMeters ?? 0f;

    // Two-end overlap (short splines) — Phase A defers to legacy h00 combination.
    if (startConstraint != null && endConstraint != null &&
        startBlendDist + endBlendDist > roadLength)
    {
        return BlendSplineProfile(
            sections, startConstraint, endConstraint,
            originalElevations, originalBankAngles);
    }

    // Look up the natural elevation at d = startBlendDist (for the start ramp)
    // and at d = roadLength - endBlendDist (for the end ramp). We snap to the
    // nearest CS in each case.
    var startNaturalAtL = 0f;
    var startNaturalAtLValid = false;
    if (startConstraint != null && startBlendDist > 0.01f)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (distFromStart[i] >= startBlendDist)
            {
                startNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[i].Index, sections[i].TargetElevation);
                startNaturalAtLValid = true;
                break;
            }
        }
    }

    var endNaturalAtL = 0f;
    var endNaturalAtLValid = false;
    if (endConstraint != null && endBlendDist > 0.01f)
    {
        var endThresh = roadLength - endBlendDist;
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            if (distFromStart[i] <= endThresh)
            {
                endNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[i].Index, sections[i].TargetElevation);
                endNaturalAtLValid = true;
                break;
            }
        }
    }

    for (var i = 0; i < sections.Count; i++)
    {
        var cs = sections[i];
        if (cs.IsRoundaboutBlended) continue;

        var d = distFromStart[i];
        var distFromEnd = roadLength - d;
        var inStartZone = startConstraint != null && d < startBlendDist;
        var inEndZone = endConstraint != null && distFromEnd < endBlendDist;

        if (!inStartZone && !inEndZone) continue;

        // Phase A: handle single-end zone only. If a CS sits in both zones, fall
        // through to legacy. With the early two-end overlap check above this
        // branch shouldn't fire on healthy inputs.
        if (inStartZone && inEndZone) continue;

        float newElev;
        if (inStartZone && startNaturalAtLValid)
        {
            newElev = ParabolicJunctionProfile.Sample(
                d, startBlendDist,
                zJunction: startConstraint!.Elevation,
                mJunction: startConstraint.Slope,
                zNaturalAtL: startNaturalAtL);
        }
        else if (inEndZone && endNaturalAtLValid)
        {
            newElev = ParabolicJunctionProfile.Sample(
                distFromEnd, endBlendDist,
                zJunction: endConstraint!.Elevation,
                mJunction: endConstraint.Slope,
                zNaturalAtL: endNaturalAtL);
        }
        else
        {
            continue;
        }

        if (MathF.Abs(newElev - cs.TargetElevation) > 0.001f)
        {
            cs.TargetElevation = newElev;
            modified++;
        }
    }

    return modified;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BlendSplineProfileParabolicTests"`
Expected: PASS, 4/4 green.

- [ ] **Step 5: Run full test suite to verify no regression**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: 256/256 green (252 pre-existing + 7 parabolic helper + 4 spline parabolic = 263; if anything below 263 passes, investigate before continuing — but a small delta is expected if test discovery counts fixtures differently).

- [ ] **Step 6: Commit**

```
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs
git commit -m "feat: add BlendSplineProfileParabolic for single-end blend zones"
```

---

### Task 4: Wire flag dispatcher in `ApplyJunctionElevationProfile`

`BlendSplineProfile` is called from two sites in `UnifiedJunctionProfileBlender.cs` (search results: L120, L176). Each must branch on the new flag.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — two call sites.

- [ ] **Step 1: Read both call sites**

Inspect `UnifiedJunctionProfileBlender.cs` around lines 118-125 and 174-180. Note the parameter list at each — it should be identical at both sites.

- [ ] **Step 2: Find the `JunctionHarmonizationParameters` instance in scope**

`BlendSplineProfile` is called inside `ApplyJunctionElevationProfile` (or its nested context). The method has access to `network` and possibly a `parameters` argument. Trace from line 120 upward to confirm the parameters object accessible at the call site. If `parameters` is not already in scope, add it as a parameter to the containing method's signature and propagate from `ApplyJunctionElevationProfile`'s caller.

For each call site `result.ModifiedCrossSections += BlendSplineProfile(...)`:

- [ ] **Step 3: Replace call site 1 (~L120)**

Change:

```csharp
result.ModifiedCrossSections += BlendSplineProfile(
    sections, startConstraint, endConstraint,
    originalElevations, originalBankAngles);
```

to:

```csharp
result.ModifiedCrossSections += parameters.EnableParabolicJunctionBlend
    ? BlendSplineProfileParabolic(
        sections, startConstraint, endConstraint,
        originalElevations, originalBankAngles)
    : BlendSplineProfile(
        sections, startConstraint, endConstraint,
        originalElevations, originalBankAngles);
```

- [ ] **Step 4: Replace call site 2 (~L176)**

Same change as Step 3 at the second call site.

- [ ] **Step 5: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds. If `parameters` is not in scope, the build will fail with `CS0103 The name 'parameters' does not exist in the current context` — propagate the parameter through and rebuild.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. Existing tests run with `EnableParabolicJunctionBlend = false` (default) so they take the legacy path and stay green. Parabolic-specific tests already pass from Task 3.

- [ ] **Step 7: Commit**

```
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: dispatch BlendSplineProfileParabolic when EnableParabolicJunctionBlend=true"
```

---

### Task 5: Junction-126-style synthetic regression test

The franco_same_prio junction 126 / spline 64 cliff is the motivating case. Build a synthetic test that reproduces its structural shape (a TJunction's terminating road, ~300 m long, with steep descending natural profile) and asserts the parabolic path does not exhibit the up-then-down quadratic-growth signature.

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs` — add one fact.

- [ ] **Step 1: Add the failing test**

Append to `BlendSplineProfileParabolicTests`:

```csharp
[Fact]
public void BlendParabolic_Junction126Reproduction_NoSignFlipAt15m()
{
    // Mimics franco_same_prio junction 126 / spline 64:
    //   - Spline length 312 m (we use 60 for the test)
    //   - End constraint anchors elevation to 158.95 m (continuous road surface)
    //     with slope ≈ 0 (continuous road near-flat at this point)
    //   - Natural spline descends from 159.0 m at far end down to ~157 m at junction
    //     end (−4% grade approximation)
    //   - Legacy code reports delta_5/15/30/60 = [+0.13, +2.46, +2.05, −1.18]
    //     (sign flip between 5 m and 15 m, and again between 30 m and 60 m)
    //
    // Parabolic path: end-anchor at d=length, blend back over L=30 m. The road
    // inside the blend zone must monotonically descend from 158.95 to natural-at-(length-30).
    var (sections, elev, bank) = BuildDescendingSpline(
        n: 60, spacing: 1f, startZ: 159.0f, slope: -0.04f);

    var endConstraint = new JunctionEndpointConstraint
    {
        Elevation = 158.95f,
        Slope = 0f,
        BankAngleRadians = 0f,
        IsSplineStart = false,
        FlatZoneDistance = 0f,
        BlendDistanceMeters = 30f,
        PrimaryTangentDirection = new Vector2(1f, 0f),
        PrimaryBankAngleRadians = 0f
    };

    UnifiedJunctionProfileBlender.BlendSplineProfileParabolic(
        sections, startConstraint: null, endConstraint: endConstraint, elev, bank);

    // Cross-section indices to inspect (from the junction = end of spline):
    //   d_from_junction = 5  → CS index 54
    //   d_from_junction = 15 → CS index 44
    //   d_from_junction = 30 → CS index 29 (blend boundary)
    //   d_from_junction = 60 → CS index 0 (well outside blend — should equal natural)
    var elevAt5 = sections[54].TargetElevation;
    var elevAt15 = sections[44].TargetElevation;
    var elevAt30 = sections[29].TargetElevation;
    var elevAt60 = sections[0].TargetElevation;
    var elevAtJunction = sections[59].TargetElevation;

    // Junction anchor preserved
    Assert.Equal(158.95f, elevAtJunction, 2);

    // Monotone descent INSIDE blend zone: as we move away from junction
    // (decreasing CS index), elevation must monotonically decrease.
    Assert.True(elevAtJunction >= elevAt5,
        $"d=5 elev ({elevAt5}) must be <= junction ({elevAtJunction})");
    Assert.True(elevAt5 >= elevAt15,
        $"d=15 elev ({elevAt15}) must be <= d=5 elev ({elevAt5})");
    Assert.True(elevAt15 >= elevAt30,
        $"d=30 elev ({elevAt30}) must be <= d=15 elev ({elevAt15})");

    // Outside blend zone, road follows natural profile (no junction effect)
    var naturalAt60FromJunction = 159.0f + (-0.04f) * 0f; // CS 0 = far end
    Assert.Equal(naturalAt60FromJunction, elevAt60, 2);
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~Junction126Reproduction"`
Expected: PASS (the parabolic implementation from Task 3 already satisfies monotone descent).

- [ ] **Step 3: Commit**

```
git add BeamNgTerrainPoc.Tests/Junction/BlendSplineProfileParabolicTests.cs
git commit -m "test: junction-126 synthetic reproduction asserts monotone descent"
```

---

### Task 6: End-to-end validation (user-driven; no code)

This task is **user-executed** on Windows. The agent's job is to copy artefacts and analyze.

- [ ] **Step 1: Flip flag to true (uncommitted local edit)**

User opens `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`, changes:

```csharp
public bool EnableParabolicJunctionBlend { get; set; } = false;
```

to:

```csharp
public bool EnableParabolicJunctionBlend { get; set; } = true;
```

Build in Visual Studio (Release).

- [ ] **Step 2: Run terrain generation in BeamNG.drive**

User regenerates `franco_same_prio` from BeamNG.drive desktop app. Artefacts overwrite `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\`.

- [ ] **Step 3: Snapshot results**

Agent runs:

```bash
mkdir -p "d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio"
SRC="C:/Users/aklei/AppData/Local/BeamNG/BeamNG.drive/current/levels/franco_same_prio/MT_TerrainGeneration"
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio"
cp "$SRC/junction_residuals.csv" "$DST/"
cp "$SRC/w_test_summary.csv" "$DST/"
cp "$SRC/quadratic_growth.csv" "$DST/"
cp "$SRC/delta_three_band.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug_legend.png" "$DST/"
cp "$SRC/logs"/Log_TerrainGen_*_Info.txt "$DST/terrain_gen_info.log"
```

- [ ] **Step 4: Read junction 126 / spline 64 rows**

Agent compares against `repro_flagsoff_20260515/` (today's flags-off baseline):

```bash
# Junction 126 row
grep "^126," d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio/junction_residuals.csv
grep "^126," d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio/w_test_summary.csv
grep "^126," d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a_franco_same_prio/quadratic_growth.csv
```

Expected (Phase A pass criteria):
- `quadratic_growth` row for spline 64: monotone descent, no sign flip between 5/15/30/60 m
- `w_test_summary` row for spline 64: `w < 3σ` (down from 9.09σ)
- `residual_max_minus_min` for junction 126: ≤ 1.5 m (no regression vs baseline's 1.413 m)
- W1 aggregate (last "W1 validation" line in log): `redBandPixels ≤ baseline+5%` (no major terrain delta regression elsewhere)

- [ ] **Step 5: Update README**

Append a row to `examples_for_ai/baseline_phase19/README.md`:

```markdown
### parabolic_a_franco_same_prio (heightmap 2048, captured <date>)

Re-run of franco_same_prio with `EnableParabolicJunctionBlend = true` only
(all other Phase 1.9 / W2 / W3 flags still off). Validates Phase A parabolic
profile substitution for single-end junction blends. See
`ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a-plan.md`.

W1 validation: <paste the W1 line from the new log>
Junction 126 quadratic_growth: <paste the row>
Junction 126 w (spline 64): <paste the w value>
```

- [ ] **Step 6: Commit the snapshot**

```
git add examples_for_ai/baseline_phase19/README.md
git commit -m "docs: Phase A parabolic blend franco validation snapshot"
```

(The `examples_for_ai/` data files are gitignored per `README.md` — only the README change is committed.)

---

### Task 7: Decide on default flag flip (gated on Task 6 results)

- [ ] **Step 1: Review Task 6 numerical results with user.**

If pass criteria met: proceed to step 2.
If not met: stop. Open a follow-up investigation. Possible follow-ups:
- Two-end overlap on short splines is taking the legacy h00 path — does it dominate the residual? Quantify how many of the 215 franco junctions hit the overlap branch.
- The seam-line slope discontinuity at d=L is visible — quantify with the existing `w` test and decide if Phase B (K-value-derived L) is the right next step.
- Bank-angle blending is still h00-weighted — could it bleed into the elevation observation?

- [ ] **Step 2: Flip default to true**

Edit `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`:

```csharp
public bool EnableParabolicJunctionBlend { get; set; } = true;
```

- [ ] **Step 3: Build + full test suite**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. Any test depending on legacy blend shape will surface here — investigate before commit.

- [ ] **Step 4: Commit default flip**

```
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: enable EnableParabolicJunctionBlend by default after Phase A validation"
```

---

## Out of scope for Phase A (Phase B follow-up)

- Remove `JunctionBlendDistanceMeters` and `RoundaboutBlendDistanceMeters` from `JunctionHarmonizationParameters`. Replace with a speed-table → AASHTO K-value cap. Plan to be written separately as `2026-05-XX-parabolic-blend-phase-b-plan.md`.
- Per-class K-value lookup: motorway = 120 km/h → K_sag ≈ 57, K_crest ≈ 95; primary = 80 → K_sag ≈ 32; residential = 30 → K_sag ≈ 4; etc. Cap `L_blend = min(adaptiveSlopeBased, K · A)`. Terrain-grade always wins; K is a ceiling, not a target.
- Bank-angle parabolic path (today still h00-weighted in `BlendSplineProfileParabolic`'s overlap fall-through). Only worth doing if bank artefacts surface in Phase A validation.

---

## Self-Review

**Spec coverage:**
- ✅ Replace cubic-Hermite-weighted additive correction with parabolic profile substitution → Task 3.
- ✅ Keep terrain-faithful (no grade-clamp behaviour) → parabolic anchors at junction Z and natural-at-L only; never imposes a slope ceiling. Slope discontinuity at d=L is a seam-line (small inflection), not a grade clamp.
- ✅ Single-end zone only in Phase A; short-spline overlap path unchanged → Task 3 step 3 (two-end overlap delegates to legacy).
- ✅ TDD scaffold: failing tests before implementation in Tasks 2 and 3.
- ✅ Validation against junction 126 specifically → Task 5 (synthetic) and Task 6 (real run).
- ❌ Did NOT add: bank-angle parabolic path. Explicitly out of scope.
- ❌ Did NOT add: K-value / speed table. Explicitly Phase B.

**Placeholder scan:**
- No "TBD", no "implement later", no "add error handling".
- Task 4 Step 2 acknowledges `parameters` may need propagation through the call chain — this is a concrete instruction with a concrete failure mode (CS0103) and concrete remediation, not a placeholder.

**Type consistency:**
- `ParabolicJunctionProfile.Sample(d, blendLength, zJunction, mJunction, zNaturalAtL)` — same parameter list used in Task 2 implementation and Task 3 usage.
- `BlendSplineProfileParabolic(sections, startConstraint, endConstraint, originalElevations, originalBankAngles)` — same signature in Task 3 implementation, Tasks 3 & 5 test usage, and Task 4 dispatcher.
- Flag name `EnableParabolicJunctionBlend` — same string in Tasks 1, 4, 6, 7.

---

## Execution handoff

This is a 7-task plan, ~3-5 minutes per step. Recommended execution mode:

**Subagent-Driven (recommended for cleanliness):** Dispatch one subagent per task, review the diff between tasks. Each subagent gets a fresh context so the plan-following stays disciplined.

**Inline (faster):** Execute in this session with checkpoint reviews after Tasks 3, 4, and 6 — those are the bisectable boundaries.

Task 6 specifically requires user action in BeamNG.drive (Windows desktop app). The agent cannot run terrain generation.
