# Parabolic Junction Blend — Phase A.8.2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate T-junction surface-vs-surface elevation bumps by making Pass 1 of `RoadMaskBuilder` priority-aware on contested pixels: when two splines' painted-surface polygons geometrically overlap and the current spline has strictly higher `Priority` than the existing owner, take ownership of the pixel. Pass 2 (corridor stamps) stays width-first first-writer-wins — A.8's win is preserved.

**Architecture:** Phase A.8.2 changes only the contested-pixel branch of `RoadMaskBuilder.RasterizeSplinePolygons` ([L381-386](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L381-L386)) — the current silent-skip becomes a priority-comparison branch when both `useSurfaceWidthOnly == true` (Pass 1) AND a new `EnableSurfacePriorityOverride` flag is set. To make the decision testable, the per-pixel resolution is extracted into a pure helper `ContestedPixelResolver.Resolve`. Each contested pixel is resolved by comparing the candidate and existing owner via a **multi-key cascade** (`SplineOverlapMetadata` record): tier 1 is `Priority` (which already encodes `OSM type × 100 + materialOrderIndex`); tier 2 is `TotalLengthMeters` (longer = more-likely-continuous road); tier 3 is `SplineId` (lower wins, purely deterministic). Strict `>` on each tier — equal cascade keeps the existing claim, so behavior never depends on iteration order. The legacy first-writer-wins behaviour is preserved bit-for-bit when the flag is off.

**Tech Stack:** .NET 9 (`net9.0-windows10.0.17763.0`), xUnit 2.x, BeamNgTerrainPoc + BeamNgTerrainPoc.Tests projects. Build sandboxed with `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`. Test with `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true`.

**Roadmap context:** A.8.2 stacks on top of A.5 (commit `f79fb01`) and A.8 (commit `976d1f6`). All three flags will be on for validation:
- `EnableParabolicJunctionBlend = true`
- `EnableSurfaceWidthProtection = true` (A.8)
- `EnablePropagationOverlapTaper = true` (A.5)
- `EnableSurfacePriorityOverride = true` (A.8.2, this plan)

A.5's franco_same_prio snapshot is `examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio/`. A.8.2's validation baseline is THAT snapshot, not the A.8 one — A.8.2's job is to fix problems A.5 left untouched (specifically the j77/OSM 282534708 surface-vs-surface bump).

---

## Why a priority comparison, not a width comparison

Pass 1's processing order ([RoadMaskBuilder.cs:121-122](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L121-L122)):

```csharp
.OrderByDescending(id => splineLookup[id].Parameters.RoadWidthMeters)
.ThenByDescending(id => splineLookup[id].Priority)
```

At T-junction j77 (OSM 282534708), spline 40 (a terminating side road) iterates before the through road in Pass 1 because of its corridor width. When their **surface polygons** geometrically overlap, spline 40's stamp claims those pixels via the `if (mask[y, x] == 0)` branch ([L374](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L374)), with its (FinalSnap-iteration-drifted) elevation of 181.91 m. The through road's later stamp hits the silent `// else: different spline's claim — do not overwrite.` branch and skips those pixels.

Result: the through road's surface is bumped up where the side road overlaps it. The user sees the bump in `examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio/groundmodel_asphalt1_italy_painted_layer.png` near (819.68, 658.25).

**Why not just reorder Pass 1 by Priority first?** Two reasons:
1. The reordering would affect ALL Pass 1 stamps, not just contested ones. We'd lose width-first wherever it was working correctly (e.g., a wider primary road overlapping a narrower priority-equal feeder).
2. Priority is a stable invariant on the spline; corridor width can drift (cross-sections refine their `EffectiveRoadWidth` over iterations). Locking the priority comparison inside the contested-pixel branch keeps the decision narrow and easier to reason about.

**Why a multi-key cascade and not just `Priority` strict-`>`?** `Priority` at [ParameterizedRoadSpline.cs:264](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs#L264) is `osmPriority * 100 + materialOrderIndex`. Equal `Priority` therefore means the two splines have **the same OSM road type AND the same material index** — common at suburban T-junctions where two `residential` streets meet from the same painted layer. Strict `>` on `Priority` alone would silently fail in those cases (the wider/earlier-iterated spline keeps the pixel as before). The cascade adds two cheap, signal-bearing tiers:
- **`TotalLengthMeters`** ([ParameterizedRoadSpline.cs:167](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs#L167)) — at a T-junction, the terminating side road is virtually always shorter than the road it terminates into. Free signal, no extra plumbing needed; it's already on every spline.
- **`SplineId`** — purely deterministic final tiebreaker so the result doesn't depend on iteration order in the all-tied case. Lower id wins (arbitrary but stable across runs).

We considered using **junction-role context** (i.e., consult `NetworkJunction.Contributors` to see which spline is primary/terminating at the nearest junction) — that's the most semantically correct discriminator. We're not pursuing it in A.8.2 because the rasterizer doesn't have per-pixel junction context and computing it would meaningfully expand scope. The length proxy handles the common case at 95%+; if Task 5 validation shows a junction where the length tier picks the wrong side, we'll add junction-role plumbing in a follow-up. The cascade structure makes that future addition mechanical (insert a tier between Priority and Length).

**Why not change Pass 2?** Pass 2 stamps corridor + edge buffer. When two corridors overlap, width-first (the current behavior) is the *correct* policy — A.8's whole premise. The bug is specifically in Pass 1, where two **painted surfaces** overlap.

**What about j77's underlying 1.08 m pin upward?** That's a `FinalSnapTJunctionEndpoints` / iteration-drift issue (handoff §"snap-to-surface audit"). A.8.2 doesn't fix the pin; it stops the pinned elevation from contaminating the through road's surface pixels. The through road's CS sits at 181.573 m; with A.8.2, those pixels get 181.573, not 181.912. The bump shrinks dramatically. A future plan (B or a FinalSnap audit) can address the pin itself.

---

## File Structure

**Create:**
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ContestedPixelResolver.cs` — pure-function resolver.
- `BeamNgTerrainPoc.Tests/Junction/ContestedPixelResolverTests.cs` — unit tests for the helper.
- `BeamNgTerrainPoc.Tests/Junction/SurfacePriorityOverrideTests.cs` — integration test exercising `BuildCombinedMaskWithElevation` end-to-end at a synthetic T-junction.

**Modify:**
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — add `EnableSurfacePriorityOverride` flag (default `false`).
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`:
  - Build `priorityBySplineId` lookup in `BuildCombinedMaskWithElevation`.
  - Plumb it (plus the flag) into `RasterizeSplinePolygons` as new parameters.
  - Replace the contested-pixel else-branch with a call to `ContestedPixelResolver.Resolve` when flag is on and `useSurfaceWidthOnly == true`.
- `examples_for_ai/baseline_phase19/README.md` — document the new `parabolic_a82_franco_same_prio` capture (Task 5).

**Do NOT modify:**
- Pass 1 / Pass 2 processing order at L121-122 — width-first stays.
- `RasterizeSplinePolygons`'s polygon-rasterization core (scanline intersection logic) — only the per-pixel decision changes.
- The same-owner banking refinement branch at L381-385 — that's correct as-is.
- Pass 2 contested-pixel skip — unchanged.
- The junction-gap-fill code at L193-273 — orthogonal.
- `EnableParabolicJunctionBlend`, `EnableSurfaceWidthProtection`, `EnablePropagationOverlapTaper` defaults.
- `FinalSnapTJunctionEndpoints` (spec §7.1 keeps it indefinitely).

---

### Task 1: Add parameter flag (no behaviour change yet)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

- [ ] **Step 1: Open file and locate `EnablePropagationOverlapTaper`** (the A.5 flag added in commit `5a2be5b`).

- [ ] **Step 2: Insert new flag immediately below `EnablePropagationOverlapTaper`**

Append after the `EnablePropagationOverlapTaper` property:

```csharp
    /// <summary>
    ///     Phase A.8.2 — surface-pass priority override. When true, Pass 1 of
    ///     <see cref="BeamNgTerrainPoc.Terrain.Algorithms.Blending.RoadMaskBuilder" />
    ///     resolves contested pixels (where two splines' painted-surface polygons
    ///     geometrically overlap at a junction) by letting the strictly-higher-Priority
    ///     spline take ownership, instead of the legacy width-first first-writer-wins.
    ///     Pass 2 (corridor stamps) is unaffected and remains width-first.
    ///     Fixes the T-junction surface-vs-surface bump where a wider terminating side
    ///     road's pinned-up elevation contaminates a higher-priority through road.
    ///     Default: false (opt-in until validation on franco_same_prio passes).
    /// </summary>
    public bool EnableSurfacePriorityOverride { get; set; } = false;
```

- [ ] **Step 3: Build to verify**

PowerShell: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 `error CS*`.

- [ ] **Step 4: Run full test suite (sanity)**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --nologo --verbosity quiet`
Expected: still 289 passed (flag is defined but unused — no behaviour change).

- [ ] **Step 5: Commit**

Use the Bash tool for the commit (heredoc compatible):

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add EnableSurfacePriorityOverride flag (Phase A.8.2 scaffold)"
```

---

### Task 2: Create `ContestedPixelResolver.Resolve` pure helper with multi-key cascade

**Purpose:** A single pure function that decides what happens to a contested pixel given the existing claim, the candidate claim, both splines' overlap metadata, the pass-1-vs-pass-2 mode, and the feature-flag state. Unit-testable in isolation.

**Data shape (slim record, lives in the same file as the resolver):**

```csharp
public readonly record struct SplineOverlapMetadata(
    int SplineId,
    int Priority,
    float TotalLengthMeters);
```

**Contract:**

```
Resolve(existing: SplineOverlapMetadata, existingElev,
        candidate: SplineOverlapMetadata, candidateElev,
        useSurfaceWidthOnly, enableSurfacePriorityOverride)
    → ResolveOutcome { TakeOwnership: bool, NewElevation: float }
```

The resolver consults a separate internal helper `CompareForOverlap(candidate, existing)` that returns a positive int when the candidate strictly wins the cascade, zero or negative otherwise.

**Decision matrix:**

| Case | Outcome |
|---|---|
| `existing.SplineId == -1` (unclaimed; caller bug-safe defensive case) | TakeOwnership=true, NewElev=candidateElev |
| `existing.SplineId == candidate.SplineId` (same spline re-stamp) | TakeOwnership=false, NewElev=min(existingElev, candidateElev) — banking refinement, preserves L381-385 |
| different splines, flag OFF | TakeOwnership=false, NewElev=existingElev (legacy first-writer-wins) |
| different splines, flag ON, Pass 2 (`useSurfaceWidthOnly == false`) | TakeOwnership=false, NewElev=existingElev (A.8's Pass-2 win preserved) |
| different splines, flag ON, Pass 1, `CompareForOverlap(candidate, existing) > 0` | TakeOwnership=true, NewElev=candidateElev |
| different splines, flag ON, Pass 1, otherwise | TakeOwnership=false, NewElev=existingElev |

**`CompareForOverlap` cascade** — returns `+1` if `a` strictly wins, `-1` if `b` strictly wins, `0` if all tiers are tied:

1. **Priority**: if `a.Priority != b.Priority`, return `sign(a.Priority - b.Priority)`.
2. **TotalLengthMeters**: if `a.TotalLengthMeters != b.TotalLengthMeters`, return `sign(a.TotalLengthMeters - b.TotalLengthMeters)`. (Longer wins.)
3. **SplineId**: if `a.SplineId != b.SplineId`, return `sign(b.SplineId - a.SplineId)`. (Lower id wins; sign reversed.)
4. Else (everything tied — same spline metadata, only possible if SplineId is equal which contradicts the "different splines" branch — defensive): return 0.

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ContestedPixelResolver.cs`
- Create: `BeamNgTerrainPoc.Tests/Junction/ContestedPixelResolverTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `BeamNgTerrainPoc.Tests/Junction/ContestedPixelResolverTests.cs`:

```csharp
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;

namespace BeamNgTerrainPoc.Tests.Junction;

public class ContestedPixelResolverTests
{
    private static SplineOverlapMetadata Meta(int splineId, int priority = 0, float length = 0f) =>
        new(splineId, priority, length);

    [Fact]
    public void Resolve_FlagOff_DifferentSplines_KeepsExistingClaim()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_HigherPriorityCandidate_TakesOwnership()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_LowerPriorityCandidate_KeepsExisting()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 9),  existingElev: 100f,
            candidate: Meta(20, priority: 5), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriority_LongerCandidate_TakesOwnership()
    {
        // Tier 2 of the cascade: equal Priority but candidate is the longer spline →
        // candidate wins (proxy for "through road > terminating side road").
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 7, length: 80f),   existingElev: 100f,
            candidate: Meta(20, priority: 7, length: 250f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriority_ShorterCandidate_KeepsExisting()
    {
        // Symmetric tier-2 check: candidate is shorter → existing keeps the pixel.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 7, length: 250f), existingElev: 100f,
            candidate: Meta(20, priority: 7, length: 80f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriorityAndLength_LowerSplineIdCandidateWins()
    {
        // Tier 3 of the cascade: priority + length both tied → lower SplineId wins.
        // Deterministic tiebreaker; never depends on iteration order.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: 20, priority: 7, length: 100f), existingElev: 100f,
            candidate: Meta(splineId: 10, priority: 7, length: 100f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriorityAndLength_HigherSplineIdCandidateKeepsExisting()
    {
        // Symmetric tier-3 check: candidate has higher SplineId → existing keeps the pixel.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: 10, priority: 7, length: 100f), existingElev: 100f,
            candidate: Meta(splineId: 20, priority: 7, length: 100f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass2_HigherPriorityCandidate_StillKeepsExisting()
    {
        // Pass 2 (corridor stamp) is unaffected even when the flag is on.
        // A.8's invariant: surface pixels claimed in Pass 1 stay claimed; A.8.2 does NOT
        // weaken that. Same applies to Pass 2 vs Pass 2 — width-first wins.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: false,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_SameSpline_TakesMinElevation()
    {
        // Same-spline re-stamp: banking refinement preserves the lower elevation.
        // This mirrors RoadMaskBuilder L381-385.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(10, priority: 5), candidateElev: 95f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(95f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_SameSpline_KeepsLowerExistingIfCandidateHigher()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 95f,
            candidate: Meta(10, priority: 5), candidateElev: 100f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(95f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOff_SameSpline_StillTakesMinElevation()
    {
        // Same-spline lower-elevation rule applies regardless of the priority-override flag.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(10, priority: 5), candidateElev: 92f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(92f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_UnclaimedExisting_CandidateAlwaysWins()
    {
        // Defensive — caller's outer `if (mask[y,x] == 0)` handles this separately, but
        // Resolve should still return coherent values if called with existing.SplineId = -1.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: -1, priority: 0),  existingElev: float.NaN,
            candidate: Meta(20, priority: 0),           candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ContestedPixelResolverTests"`
Expected: FAIL — `ContestedPixelResolver` type does not exist (compile error).

- [ ] **Step 3: Implement the helper**

Create `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ContestedPixelResolver.cs`:

```csharp
namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
///     Slim spline metadata consumed by <see cref="ContestedPixelResolver" /> when
///     resolving a contested mask claim in Pass 1 of
///     <see cref="RoadMaskBuilder.RasterizeSplinePolygons" />. Carries the three signals
///     used by the comparison cascade: <see cref="Priority" /> (OSM type × 100 +
///     material order), <see cref="TotalLengthMeters" /> (longer ≈ more-likely-continuous),
///     and <see cref="SplineId" /> (deterministic final tiebreaker).
/// </summary>
public readonly record struct SplineOverlapMetadata(
    int SplineId,
    int Priority,
    float TotalLengthMeters);

/// <summary>
///     Phase A.8.2 — pure per-pixel resolver for contested mask claims in
///     <see cref="RoadMaskBuilder.RasterizeSplinePolygons" />. Decides whether the
///     candidate spline takes ownership of a pixel that is already claimed.
///     Geometric-only — does not consult terrain elevation or grade.
/// </summary>
public static class ContestedPixelResolver
{
    public readonly record struct ResolveOutcome(bool TakeOwnership, float NewElevation);

    /// <summary>
    ///     Resolves a contested pixel. Encodes the per-case decision; for the
    ///     priority-override branch, delegates to <see cref="CompareForOverlap" />.
    ///     Cases:
    ///       1. existing.SplineId == -1 (unclaimed; defensive): candidate always wins.
    ///       2. existing.SplineId == candidate.SplineId (same spline re-stamp): keep
    ///          lower elevation for banking refinement.
    ///       3. Different splines, flag off OR Pass 2: keep existing (legacy first-writer-wins).
    ///       4. Different splines, flag on AND Pass 1: candidate wins iff
    ///          <see cref="CompareForOverlap" />(candidate, existing) &gt; 0.
    /// </summary>
    public static ResolveOutcome Resolve(
        SplineOverlapMetadata existing, float existingElev,
        SplineOverlapMetadata candidate, float candidateElev,
        bool useSurfaceWidthOnly,
        bool enableSurfacePriorityOverride)
    {
        // Case 1: unclaimed → candidate wins
        if (existing.SplineId == -1)
            return new ResolveOutcome(TakeOwnership: true, NewElevation: candidateElev);

        // Case 2: same spline → keep lower (banking refinement)
        if (existing.SplineId == candidate.SplineId)
        {
            var lower = candidateElev < existingElev ? candidateElev : existingElev;
            return new ResolveOutcome(TakeOwnership: false, NewElevation: lower);
        }

        // Case 3: different splines, flag off OR Pass 2 → keep existing
        if (!enableSurfacePriorityOverride || !useSurfaceWidthOnly)
            return new ResolveOutcome(TakeOwnership: false, NewElevation: existingElev);

        // Case 4: different splines, flag on, Pass 1 → cascade comparison
        if (CompareForOverlap(candidate, existing) > 0)
            return new ResolveOutcome(TakeOwnership: true, NewElevation: candidateElev);

        return new ResolveOutcome(TakeOwnership: false, NewElevation: existingElev);
    }

    /// <summary>
    ///     Multi-key cascade comparator. Returns +1 if <paramref name="a" /> strictly wins,
    ///     -1 if <paramref name="b" /> strictly wins, 0 if all tiers are tied.
    ///     Tiers (strict ordering, first decisive tier wins):
    ///       1. Priority (higher wins) — encodes OSM type + material order
    ///       2. TotalLengthMeters (longer wins) — terminating side roads are typically shorter
    ///       3. SplineId (lower wins) — deterministic fallback, no semantic meaning
    /// </summary>
    internal static int CompareForOverlap(SplineOverlapMetadata a, SplineOverlapMetadata b)
    {
        if (a.Priority != b.Priority)
            return a.Priority > b.Priority ? 1 : -1;

        if (a.TotalLengthMeters != b.TotalLengthMeters)
            return a.TotalLengthMeters > b.TotalLengthMeters ? 1 : -1;

        if (a.SplineId != b.SplineId)
            return a.SplineId < b.SplineId ? 1 : -1;

        return 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ContestedPixelResolverTests"`
Expected: PASS, 12/12 green.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/Blending/ContestedPixelResolver.cs BeamNgTerrainPoc.Tests/Junction/ContestedPixelResolverTests.cs
git commit -m "feat: add ContestedPixelResolver with multi-key cascade (Phase A.8.2)"
```

---

### Task 3: Plumb priority lookup + dispatch resolver in `RasterizeSplinePolygons`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

The current contested-pixel branch in `RasterizeSplinePolygons` ([L374-386](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L374-L386)):

```csharp
if (mask[y, x] == 0)
{
    mask[y, x] = 255;
    elevation[y, x] = pixelElevation;
    splineOwner[y, x] = splineId;
    maskedPixels++;
}
else if (splineOwner[y, x] == splineId)
{
    if (pixelElevation < elevation[y, x])
        elevation[y, x] = pixelElevation;
}
// else: different spline's claim — do not overwrite.
```

becomes a single call into `ContestedPixelResolver.Resolve` after we plumb the priority lookup and flag into the method.

- [ ] **Step 1: Change `RasterizeSplinePolygons` from `private` to `internal` and extend its signature**

At [L287-298](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L287-L298), update:

```csharp
private static int RasterizeSplinePolygons(
    List<UnifiedCrossSection> sections,
    int splineId,
    float margin,
    bool useSurfaceWidthOnly,
    byte[,] mask,
    float[,] elevation,
    int[,] splineOwner,
    int width,
    int height,
    float metersPerPixel,
    Span<float> intersections)
```

to:

```csharp
internal static int RasterizeSplinePolygons(
    List<UnifiedCrossSection> sections,
    int splineId,
    SplineOverlapMetadata splineMetadata,
    Dictionary<int, SplineOverlapMetadata> metadataByOwnerId,
    bool enableSurfacePriorityOverride,
    float margin,
    bool useSurfaceWidthOnly,
    byte[,] mask,
    float[,] elevation,
    int[,] splineOwner,
    int width,
    int height,
    float metersPerPixel,
    Span<float> intersections)
```

`internal` is needed so the integration test in Task 4 can call it directly. `InternalsVisibleTo("BeamNgTerrainPoc.Tests")` is already present in `BeamNgTerrainPoc/BeamNgTerrainPoc.csproj:11` (verified by Phase A.5 Task 5).

`SplineOverlapMetadata` is the slim record introduced in Task 2 (`SplineId`, `Priority`, `TotalLengthMeters`); it lives in the same namespace as `ContestedPixelResolver` (`BeamNgTerrainPoc.Terrain.Algorithms.Blending`), so no new `using` is needed.

- [ ] **Step 2: Replace the contested-pixel branch body**

In the inner loop at [L369-386](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L369-L386), replace:

```csharp
for (var x = xStart; x <= xEnd; x++)
{
    var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
    var pixelElevation = BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos);

    if (mask[y, x] == 0)
    {
        mask[y, x] = 255;
        elevation[y, x] = pixelElevation;
        splineOwner[y, x] = splineId;
        maskedPixels++;
    }
    else if (splineOwner[y, x] == splineId)
    {
        if (pixelElevation < elevation[y, x])
            elevation[y, x] = pixelElevation;
    }
    // else: different spline's claim — do not overwrite.
}
```

with:

```csharp
for (var x = xStart; x <= xEnd; x++)
{
    var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
    var pixelElevation = BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos);

    if (mask[y, x] == 0)
    {
        mask[y, x] = 255;
        elevation[y, x] = pixelElevation;
        splineOwner[y, x] = splineId;
        maskedPixels++;
    }
    else
    {
        // Phase A.8.2: contested pixel — defer to ContestedPixelResolver.
        // Same-spline re-stamp keeps lower elevation (banking refinement).
        // Different-spline conflict: flag-off OR Pass-2 → keep existing; flag-on AND
        // Pass-1 → multi-key cascade (Priority → TotalLength → SplineId) decides.
        var existingOwnerId = splineOwner[y, x];
        var existingMeta = metadataByOwnerId.TryGetValue(existingOwnerId, out var em)
            ? em
            : new SplineOverlapMetadata(SplineId: existingOwnerId, Priority: 0, TotalLengthMeters: 0f);

        var outcome = ContestedPixelResolver.Resolve(
            existing: existingMeta, existingElev: elevation[y, x],
            candidate: splineMetadata, candidateElev: pixelElevation,
            useSurfaceWidthOnly,
            enableSurfacePriorityOverride);

        elevation[y, x] = outcome.NewElevation;
        if (outcome.TakeOwnership)
            splineOwner[y, x] = splineId;
    }
}
```

Note: the new structure folds the same-spline banking refinement, the legacy first-writer-wins, AND the new priority override into the single `else` branch via `ContestedPixelResolver.Resolve`. `mask[y, x]` is NOT cleared on ownership change — once claimed, the pixel stays claimed; only `elevation` and `splineOwner` may update.

- [ ] **Step 3: Build `metadataByOwnerId` in `BuildCombinedMaskWithElevation` and pass it through**

In `BuildCombinedMaskWithElevation`, immediately after the existing `splineLookup` line at [L118](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L118), insert:

```csharp
// Phase A.8.2: per-owner overlap metadata for contested-pixel resolution.
// Priority encodes OSM-type + material-order; TotalLengthMeters is the
// length-tier tiebreaker (terminating side roads are typically shorter).
var metadataByOwnerId = network.Splines.ToDictionary(
    s => s.SplineId,
    s => new SplineOverlapMetadata(s.SplineId, s.Priority, s.TotalLengthMeters));
```

Then update the three `RasterizeSplinePolygons` call sites inside `BuildCombinedMaskWithElevation` ([L143-148](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L143-L148), [L162-167](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L162-L167), [L181-186](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L181-L186) — Pass 1, Pass 2, legacy single-pass) to thread the new args:

```csharp
maskedPixels += RasterizeSplinePolygons(
    sections, splineId,
    splineMetadata: metadataByOwnerId[splineId],
    metadataByOwnerId: metadataByOwnerId,
    enableSurfacePriorityOverride: jhParams.EnableSurfacePriorityOverride,
    margin,
    useSurfaceWidthOnly: true,  // or false depending on which call site
    mask, elevation, splineOwner,
    width, height, metersPerPixel, intersections);
```

All three sites get the same new args. The `metadataByOwnerId[splineId]` lookup is safe — `processingOrder` is built from `splineLookup.ContainsKey(id)` at [L119-120](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L119-L120), so the spline is always present in `network.Splines` and therefore in the metadata dict.

- [ ] **Step 4: Build to verify**

PowerShell: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 `error CS*`.

- [ ] **Step 5: Run full test suite**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --nologo --verbosity quiet`
Expected: all 301 passed (289 baseline from A.5 + 12 new in Task 2). Flag default false → behaviour unchanged. The `ContestedPixelResolver` call inside the loop reduces to the legacy semantics when flag is off:
- Same-spline re-stamp → identical to old `else if` branch (lower elevation kept)
- Different-spline → identical to old "do not overwrite" (TakeOwnership=false, NewElev=existingElev so no change written)

If any existing test fails, the refactor changed behaviour somewhere — stop and diagnose before proceeding.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs
git commit -m "feat: dispatch ContestedPixelResolver in Pass 1 of RoadMaskBuilder (Phase A.8.2)"
```

---

### Task 4: Integration test — two-spline T-junction priority override

This test exercises `RoadMaskBuilder.BuildCombinedMaskWithElevation` end-to-end with a synthetic two-spline network constructed so that:
- Through road (`splineId=1`, `Priority=10`, narrower surface) runs east-west.
- Side road (`splineId=2`, `Priority=5`, wider surface) runs north and terminates at the through road's midpoint.
- Their surface polygons geometrically overlap at the junction.
- The side road has elevation 110 m at the overlap; the through road has 100 m.

With flag OFF: the side road (iterated first by width) claims the overlap pixels at 110 m (legacy bug).
With flag ON: the through road (Priority 10 > 5) takes the contested pixels back at 100 m via the cascade's tier-1 (Priority) — the length tier is not exercised here (covered by resolver unit tests in Task 2).

**Pre-flight read (BEFORE writing the test):**

Grep these types and confirm which properties are `required init`:
- `ParameterizedRoadSpline` (`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`) — known `required` includes `Spline` (RoadSpline), `Parameters` (RoadSmoothingParameters), `MaterialName` (string), `SplineId` (int). Other properties may also be required.
- `RoadSpline` (`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`) — find the constructor signature or check what's needed to make `TotalLength` resolve to a real value. The test doesn't *use* `TotalLength` (priorities differ → cascade tier 1 decides), but `Spline` must be set to a valid instance because it's `required`.
- `UnifiedRoadNetwork` — check for any required collections beyond `Splines`, `CrossSections`, `Junctions`.
- `RoadSmoothingParameters` — `JunctionHarmonizationParameters` is one of its properties; check whether the rest are required.

If `RoadSpline` is too complex to construct from primitives (e.g., requires curve fitting or a fully-sampled point list), prefer constructing one with a simple two-point segment so `TotalLength` is well-defined but otherwise minimal. If the constructor is genuinely heavy and there's no convenient builder, add a tiny `TestSplineFactory.MakeMinimal(int splineId, Vector2 start, Vector2 end)` helper in the test file to keep the test readable. Do NOT add a factory to the production code.

If you discover required properties not anticipated here (e.g., `OsmRoadType` becomes `required`), fill them with sensible test defaults (`null` for nullable strings, `0` for ints, `false` for bools, `default!` is acceptable for required nullable references when the property isn't exercised). The test must compile cleanly with zero `error CS*`.

**Files:**
- Create: `BeamNgTerrainPoc.Tests/Junction/SurfacePriorityOverrideTests.cs`

- [ ] **Step 1: Write the test file**

Create `BeamNgTerrainPoc.Tests/Junction/SurfacePriorityOverrideTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class SurfacePriorityOverrideTests
{
    private static UnifiedCrossSection MakeCs(
        int index, int localIndex, int ownerSplineId,
        Vector2 center, Vector2 tangent, Vector2 normal,
        float elevation, float surfaceWidth, float effectiveWidth)
    {
        return new UnifiedCrossSection
        {
            Index = index,
            LocalIndex = localIndex,
            OwnerSplineId = ownerSplineId,
            CenterPoint = center,
            TangentDirection = tangent,
            NormalDirection = normal,
            TargetElevation = elevation,
            BankAngleRadians = 0f,
            EffectiveRoadWidth = effectiveWidth,
            SurfaceWidth = surfaceWidth
        };
    }

    /// <summary>
    ///     Minimal 2-point RoadSpline. Used to satisfy the `required` Spline property on
    ///     ParameterizedRoadSpline; the test does not exercise the Akima curve, just the
    ///     end-to-end pass.
    /// </summary>
    private static RoadSpline MakeMinimalSpline(Vector2 start, Vector2 end) =>
        new(new List<Vector2> { start, end });

    /// <summary>
    ///     Builds a two-spline network forming a T-junction at (50, 50):
    ///       Spline 1 (through, Priority=10, narrower surface=6m):
    ///         CSes at (40,50), (60,50) — east-west
    ///       Spline 2 (side, Priority=5, wider surface=10m):
    ///         CSes at (50,40), (50,50) — south-to-north, terminates at through road
    ///     Spline 1 elevation = 100, Spline 2 elevation = 110.
    ///     Their surface polygons overlap in a 6x10 region around (50,50).
    /// </summary>
    private static UnifiedRoadNetwork BuildTJunctionNetwork(
        bool enableSurfacePriorityOverride)
    {
        var jhParams = new JunctionHarmonizationParameters
        {
            EnableSurfaceWidthProtection = true,
            EnableSurfacePriorityOverride = enableSurfacePriorityOverride
        };

        var throughCs0 = MakeCs(
            index: 1001, localIndex: 0, ownerSplineId: 1,
            center: new Vector2(40f, 50f),
            tangent: new Vector2(1f, 0f), normal: new Vector2(0f, 1f),
            elevation: 100f, surfaceWidth: 6f, effectiveWidth: 8f);
        var throughCs1 = MakeCs(
            index: 1002, localIndex: 1, ownerSplineId: 1,
            center: new Vector2(60f, 50f),
            tangent: new Vector2(1f, 0f), normal: new Vector2(0f, 1f),
            elevation: 100f, surfaceWidth: 6f, effectiveWidth: 8f);

        var sideCs0 = MakeCs(
            index: 2001, localIndex: 0, ownerSplineId: 2,
            center: new Vector2(50f, 40f),
            tangent: new Vector2(0f, 1f), normal: new Vector2(1f, 0f),
            elevation: 110f, surfaceWidth: 10f, effectiveWidth: 12f);
        var sideCs1 = MakeCs(
            index: 2002, localIndex: 1, ownerSplineId: 2,
            center: new Vector2(50f, 50f),
            tangent: new Vector2(0f, 1f), normal: new Vector2(1f, 0f),
            elevation: 110f, surfaceWidth: 10f, effectiveWidth: 12f);

        // NOTE: ParameterizedRoadSpline has required `Spline` (RoadSpline) and `MaterialName`
        // properties — see Task 4 pre-flight. The values below are placeholders; replace
        // `Spline = MakeMinimalSpline(...)` with whatever constructor / factory call your
        // pre-flight identified. `TotalLengthMeters` is computed from `Spline.TotalLength`
        // but is NOT exercised by these tests (priorities differ → cascade tier 1 decides),
        // so a minimal two-point RoadSpline is sufficient.
        var throughSpline = new ParameterizedRoadSpline
        {
            SplineId = 1,
            Priority = 10,
            Spline = MakeMinimalSpline(new Vector2(40f, 50f), new Vector2(60f, 50f)),
            MaterialName = "test_through",
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 6f,
                RoadEdgeProtectionBufferMeters = 1.0f,
                JunctionHarmonizationParameters = jhParams
            }
        };

        var sideSpline = new ParameterizedRoadSpline
        {
            SplineId = 2,
            Priority = 5,
            Spline = MakeMinimalSpline(new Vector2(50f, 40f), new Vector2(50f, 50f)),
            MaterialName = "test_side",
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 10f,
                RoadEdgeProtectionBufferMeters = 1.0f,
                JunctionHarmonizationParameters = jhParams
            }
        };

        return new UnifiedRoadNetwork
        {
            Splines = new List<ParameterizedRoadSpline> { throughSpline, sideSpline },
            CrossSections = new List<UnifiedCrossSection>
            {
                throughCs0, throughCs1, sideCs0, sideCs1
            },
            Junctions = new List<NetworkJunction>()
        };
    }

    [Fact]
    public void BuildMask_FlagOff_OverlapPixelsKeptBySideSplineFromWidthOrder()
    {
        // Side road (wider surface) iterates first in Pass 1; its 110m elevation
        // claims the overlap pixels around (50,50) regardless of through road's
        // higher Priority. This is the legacy bug.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: false);
        var builder = new RoadMaskBuilder();

        const int dim = 100;
        const float mpp = 1.0f;

        var result = builder.BuildCombinedMaskWithElevation(network, dim, dim, mpp);

        // Center pixel (50,50) is inside the overlap. Owner should be side road (2)
        // and elevation should be 110 (with flag off).
        Assert.Equal(2, result.SplineOwner[50, 50]);
        Assert.Equal(110f, result.Elevation[50, 50], 1);
    }

    [Fact]
    public void BuildMask_FlagOn_OverlapPixelsTakenByThroughRoadFromPriorityOverride()
    {
        // Same topology, flag on: through road (Priority 10 > 5) takes the overlap pixels.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: true);
        var builder = new RoadMaskBuilder();

        const int dim = 100;
        const float mpp = 1.0f;

        var result = builder.BuildCombinedMaskWithElevation(network, dim, dim, mpp);

        Assert.Equal(1, result.SplineOwner[50, 50]);
        Assert.Equal(100f, result.Elevation[50, 50], 1);
    }

    [Fact]
    public void BuildMask_FlagOn_NonOverlapPixelsUnchanged()
    {
        // Pixels far from the junction must be unaffected.
        var network = BuildTJunctionNetwork(enableSurfacePriorityOverride: true);
        var builder = new RoadMaskBuilder();

        const int dim = 100;
        const float mpp = 1.0f;

        var result = builder.BuildCombinedMaskWithElevation(network, dim, dim, mpp);

        // Through road only at (45,50) — outside side road's 10m corridor of (50,40)-(50,50)
        Assert.Equal(1, result.SplineOwner[50, 45]);
        // Side road only at (50,42) — outside through road's 6m corridor of (40,50)-(60,50)
        Assert.Equal(2, result.SplineOwner[42, 50]);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~SurfacePriorityOverrideTests"`
Expected: PASS, 3/3 green.

If `RoadSmoothingParameters` or `UnifiedRoadNetwork` have `required` properties not set in this test fixture, add `= ` defaults the way Phase A.5 Task 3 / Task 5 had to do for `JunctionEndpointConstraint.BankAngleRadians`. Use Grep on the type to find any `required` modifiers. If a property is required but conceptually orthogonal to this test (e.g., a metadata id), set it to `default`/`0`/`new()` to satisfy compile.

If `UnifiedCrossSection.SurfaceWidth` is not yet a settable property (Phase A.8 added the field; verify), STOP and report NEEDS_CONTEXT.

- [ ] **Step 3: Run full test suite**

PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --nologo --verbosity quiet`
Expected: 304 passed (301 from Task 3 + 3 new).

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/SurfacePriorityOverrideTests.cs
git commit -m "test: synthetic T-junction asserts Pass-1 priority override (Phase A.8.2)"
```

---

### Task 5: End-to-end validation (user-driven; no code)

This task is **user-executed** on Windows. The agent's job is to copy artefacts, analyze the CSVs, and update the README.

- [ ] **Step 1: User flips flag to true (uncommitted local edit)**

User opens `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`, changes:

```csharp
public bool EnableSurfacePriorityOverride { get; set; } = false;
```

to:

```csharp
public bool EnableSurfacePriorityOverride { get; set; } = true;
```

`EnableParabolicJunctionBlend`, `EnableSurfaceWidthProtection`, and `EnablePropagationOverlapTaper` stay `true`. Build in Visual Studio (Release).

- [ ] **Step 2: User regenerates terrain in BeamNG.drive**

User regenerates `franco_same_prio` from the BeamNG.drive desktop app. Artefacts overwrite `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\`.

User then notifies the agent: "Task 5 regen done."

- [ ] **Step 3: Agent snapshots results**

Bash via the Bash tool:

```bash
mkdir -p "d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a82_franco_same_prio"
SRC="C:/Users/aklei/AppData/Local/BeamNG/BeamNG.drive/current/levels/franco_same_prio/MT_TerrainGeneration"
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a82_franco_same_prio"
cp "$SRC/junction_residuals.csv" "$DST/"
cp "$SRC/w_test_summary.csv" "$DST/"
cp "$SRC/quadratic_growth.csv" "$DST/"
cp "$SRC/delta_three_band.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug.png" "$DST/"
cp "$SRC/unified_junction_harmonization_debug_legend.png" "$DST/"
cp "$SRC/groundmodel_asphalt1_italy_painted_layer.png" "$DST/"
cp "$SRC"/logs/Log_TerrainGen_*_Info.txt "$DST/terrain_gen_info.log"
```

- [ ] **Step 4: Extract j77 + j125 + j126 rows and W1 aggregate**

```bash
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a82_franco_same_prio"
A5="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/parabolic_a5_franco_same_prio"

echo "=== j77 (OSM 282534708) — junction_residuals (A.5 → A.8.2) ==="
grep "^77," "$A5/junction_residuals.csv"
grep "^77," "$DST/junction_residuals.csv"

echo "=== j77 — w_test_summary ==="
grep "^77," "$A5/w_test_summary.csv"
grep "^77," "$DST/w_test_summary.csv"

echo "=== j77 — quadratic_growth ==="
grep "^77," "$A5/quadratic_growth.csv"
grep "^77," "$DST/quadratic_growth.csv"

echo "=== j125 spline 64 (regression gate) ==="
grep "^125,64," "$A5/w_test_summary.csv"
grep "^125,64," "$DST/w_test_summary.csv"

echo "=== j126 spline 64 (A.5 target, NOT expected to move from A.8.2) ==="
grep "^126,64," "$A5/w_test_summary.csv"
grep "^126,64," "$DST/w_test_summary.csv"

echo "=== W1 aggregate ==="
grep "W1 validation" "$A5/terrain_gen_info.log" | tail -1
grep "W1 validation" "$DST/terrain_gen_info.log" | tail -1
```

- [ ] **Step 5: Evaluate pass criteria**

| Criterion | Target | A.5 baseline | A.8.2 result |
|---|---|---|---|
| j77 spline 40 `w` | < A.5's 15.06σ; ideally < 6σ | 15.06σ | (fill) |
| j77 `residual_max_minus_min` | ≤ A.5's 0.339 m | 0.339 m | (fill — must stay ≤) |
| j77 `residual_pinned_minus_terrain` | informational (pin issue is upstream of A.8.2) | +1.078 m | (fill — likely unchanged) |
| j125 spline 64 `w` (regression gate) | ≤ A.5's 5.01σ + 1σ | 5.01σ | (fill — must not regress > 1σ) |
| j126 spline 64 `w` (regression gate) | ≤ A.5's 17.39σ + 1σ | 17.39σ | (fill — must not regress > 1σ) |
| W1 `redBandPixels` | ≤ A.5's 355 378 + 10 % | 355 378 | (fill — must be ≤ 390 916) |

**Two interpretations if j77 metrics improve substantially:**
1. The bump *was* surface-vs-surface stamping; priority override fixes it cleanly.
2. j77 spline 40's `w` may *not* drop much because the 15.06° tangent kink is in spline 40's own profile (FinalSnap drift), not in the pixel stamp. What DOES change is the painted_layer.png: the contested overlap pixels visibly belong to the through road. The bump in the user's screenshot should be visibly gone or much smaller, even if spline 40's `w` is unchanged.

**Two interpretations if j77 metrics don't improve:**
1. The two splines at j77 have equal `Priority` (both `int` and possibly both 50 or both 70 if they share an OSM road class). A.8.2's strict `>` rule means equal-priority conflicts still go to first-writer. Diagnostic: log priorities at j77 by adding a one-off `Detail` log in `RasterizeSplinePolygons` for splineId in {40, through-road-id}, OR grep `ParameterizedRoadSpline` construction in the OSM importer.
2. The contested pixels are claimed by side road via *Pass 2 corridor* (not Pass 1 surface). A.8.2 doesn't override Pass 2. Diagnostic: temporarily flip the gate to `useSurfaceWidthOnly || !useSurfaceWidthOnly` (i.e., always-on) in a throwaway commit, regen, and observe — if j77 then moves, the contested pixels are corridor-stage, not surface-stage.

Discuss outcome with user before flipping default.

- [ ] **Step 6: Update baseline README**

Append a section to `examples_for_ai/baseline_phase19/README.md`:

```markdown
### parabolic_a82_franco_same_prio (heightmap 2048, captured <date>)

Re-run of franco_same_prio with `EnableParabolicJunctionBlend = true`,
`EnableSurfaceWidthProtection = true` (A.8),
`EnablePropagationOverlapTaper = true` (A.5), AND
`EnableSurfacePriorityOverride = true` (A.8.2). The A.8.2 comparison baseline is
`parabolic_a5_franco_same_prio/`. A.8.2 targets the j77 / OSM 282534708 T-junction
surface-vs-surface bump that A.5 left untouched. See
[`ai_docs/2026-05-15_parabolic_blend/2026-05-25-parabolic-blend-phase-a82-plan.md`](../../ai_docs/2026-05-15_parabolic_blend/2026-05-25-parabolic-blend-phase-a82-plan.md).

W1 validation: <paste from log>

Phase A.8.2 pass-criteria result:

| Criterion | Target | Observed | Pass? |
|---|---|---|---|
| j77 spline 40 `w` | < 15.06σ; ideally < 6σ | <fill> | <yes/no> |
| j77 `residual_max_minus_min` | ≤ 0.339 m | <fill> | <yes/no> |
| j125 spline 64 `w` (regression) | ≤ 6σ | <fill> | <yes/no> |
| j126 spline 64 `w` (regression) | ≤ 18.4σ | <fill> | <yes/no> |
| W1 `redBandPixels` | ≤ 390 916 (A.5 + 10 %) | <fill> | <yes/no> |

j77 detail (A.5 → A.8.2):

\`\`\`
junction_residuals — j77 A.5:    <paste>
junction_residuals — j77 A.8.2:  <paste>
w-test          — j77 A.5:       <paste>
w-test          — j77 A.8.2:     <paste>
quadratic_growth — j77 A.5:      <paste>
quadratic_growth — j77 A.8.2:    <paste>
\`\`\`
```

- [ ] **Step 7: Commit the README update**

```bash
git add examples_for_ai/baseline_phase19/README.md
git commit -m "docs: Phase A.8.2 surface-priority-override franco validation snapshot"
```

(`parabolic_a82_franco_same_prio/` data files are gitignored.)

---

### Task 6: Default flag flip (gated on Task 5 results)

- [ ] **Step 1: Review Task 5 numerical + visual results with user.**

If pass criteria met AND the user confirms the j77 bump is visually gone or substantially smaller in the regenerated painted_layer.png: proceed to step 2.

If pass criteria NOT met: stop. Two hypotheses for follow-up (in order of likelihood):

1. **Equal-priority T-junctions.** If j77's two splines share a Priority value, A.8.2's strict `>` rule doesn't fire. Decision: add a tie-breaker (lower `SplineId`? higher `RoadWidthMeters`? primary-contributor-at-junction?). Update the plan and re-execute Task 4/5.
2. **Pass-2 contested pixels.** If the contested overlap is in Pass 2 (corridor stamps), A.8.2's `useSurfaceWidthOnly` gate excludes them. Decision: extend the override to Pass 2 cautiously — this may regress A.8's intended width-first wins elsewhere. Investigate before changing.

- [ ] **Step 2: Flip default to true**

Edit `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`:

```csharp
public bool EnableSurfacePriorityOverride { get; set; } = true;
```

- [ ] **Step 3: Build + full test suite**

PowerShell: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
PowerShell: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --nologo --verbosity quiet`
Expected: all green.

- [ ] **Step 4: Commit default flip**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: enable EnableSurfacePriorityOverride by default after Phase A.8.2 validation"
```

---

## Out of scope for Phase A.8.2

- **`FinalSnapTJunctionEndpoints` audit / primary-selection rework** — the upstream cause of the 1.08 m pin at j77. Tracked separately; see [`memory/junction_elevation_debugging.md`](../../../C:/Users/aklei/.claude/projects/d--Source-beamng-mapping-pro/memory/junction_elevation_debugging.md) and the roadmap.
- **Pass-2 corridor overlap priority override** — only consider after A.8.2 validates and only if a specific case proves the need. Risks regressing A.8's win.
- **Equal-priority tie-breaker design** — defer until Task 5 shows whether equal-priority T-junctions are common enough at franco to warrant it.
- **Multi-contributor IDW at multi-way junctions** — roadmap §A.7.
- **AASHTO K-value blend distance** — roadmap §B.
- **`JunctionBankingAdapter` overwrites CG profiles** — roadmap §X1.

---

## Self-Review

**Spec coverage (the user's request):**
- ✅ "Make an easy improvement of code" (vs. rolling back A.8) → A.8.2 is a single-file primary change + one helper + tests. Smaller than A.5.
- ✅ "Treatment for intersecting protection zones on painted roads" → the contested-pixel branch is exactly that treatment.
- ✅ "Keep A.5" → A.8.2 stacks on top; A.5 isn't touched.
- ✅ Cost analysis: pure helper + per-pixel call → O(1) extra work per pixel; rasterizer is already the hot path; the change is negligible.

**Placeholder scan:**
- No "TBD" / "implement later".
- `<paste>` / `<fill>` slots in Task 5 README are explicit empirical-data placeholders, same pattern as Phase A.5 Task 6.
- Task 6 Step 1 lists concrete diagnostics, not "investigate further".

**Type consistency:**
- `ContestedPixelResolver.Resolve` signature defined in Task 2 Step 3 and consumed at Task 3 Step 2.
- `ResolveOutcome` record struct shape (`TakeOwnership`, `NewElevation`) consistent in Task 2 tests + Task 3 call site.
- `RasterizeSplinePolygons` extended signature consistent across Task 3 Step 1 declaration, Task 3 Step 3 call sites, and Task 4 integration test (which calls `BuildCombinedMaskWithElevation`, so the new signature is transitive).
- Flag name `EnableSurfacePriorityOverride` consistent in Tasks 1, 3, 5, 6.
- `priorityByOwnerId` parameter name consistent across declaration and call sites.

**TDD scaffold:**
- Task 2: failing test → impl → green.
- Task 3: existing test suite stays green (flag-off path is bit-identical via the resolver's Rule 3).
- Task 4: failing integration test → already passes after Task 3's wiring → green.

---

## Execution handoff

This is a 6-task plan, ~5-15 minutes per task. Task 4 (integration test) is the most likely to surface unexpected issues — the `UnifiedRoadNetwork` constructor surface may have `required` properties not anticipated in the test fixture. The Task 3 refactor is medium complexity (four call-site updates) but the `RasterizeSplinePolygons` change is mechanical.

**Subagent-driven (recommended):** Dispatch one subagent per task. Task 5 requires user action in BeamNG.drive (Windows desktop app).

**Inline (faster):** Execute in this session with checkpoint reviews after Tasks 2, 3, and 4 — those are the bisectable boundaries.

Task 5 specifically requires user action in BeamNG.drive. The agent cannot run terrain generation.
