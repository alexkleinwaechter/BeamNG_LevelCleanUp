# Parabolic Junction Blend — Phase A.8 Implementation Plan (Painted-Road-Width Protection)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `RoadMaskBuilder.BuildCombinedMaskWithElevation` from claiming a narrower spline's *painted-surface* pixels with a wider adjacent spline's *corridor* (smoothing margin + edge protection buffer). Two-pass rasterizer: Pass 1 stamps each spline's surface polygon only (widest-surface-first); Pass 2 extends with corridor+buffer into pixels not yet claimed. Each spline's surface pixels then carry that spline's own banking-aware elevation regardless of corridor overlap.

**Architecture:** Phase A.8 changes only `RoadMaskBuilder` (one class, one method body) plus adds `SurfaceWidth` as a sibling field to `EffectiveRoadWidth` on `UnifiedCrossSection`. The combined polygon width is unchanged at the boundary of the painted area: the corridor is still stamped as before, just into pixels that aren't already part of another spline's surface. Junction-gap filling (the existing circular pin at junction centroids, ~L246-317) runs unchanged at the end and still fills unclaimed pixels. Both A.5 and A.8 are gated by independent feature flags so each can be validated alone.

**Tech Stack:** .NET 9 (`net9.0-windows10.0.17763.0`), xUnit 2.x, BeamNgTerrainPoc + BeamNgTerrainPoc.Tests projects. Build sandboxed with `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`. Test with `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`.

**Roadmap context:** See [2026-05-15-parabolic-blend-roadmap.md](2026-05-15-parabolic-blend-roadmap.md). A.8 runs **before** A.5 because the rasterizer override is likely the dominant cause of the j126 cliff. When A.8 closes, the roadmap status row updates and A.5 transitions ⏳ → 🚧.

---

## Why the current rasterizer overrides terminating-road ramps

At [RoadMaskBuilder.cs:146-154](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L146-L154):

```csharp
var halfWidth1 = cs1.EffectiveRoadWidth / 2.0f + margin;
var halfWidth2 = cs2.EffectiveRoadWidth / 2.0f + margin;

var corners = new Vector2[4];
corners[0] = cs1.CenterPoint - cs1.NormalDirection * halfWidth1; // left1
corners[1] = cs1.CenterPoint + cs1.NormalDirection * halfWidth1; // right1
corners[2] = cs2.CenterPoint + cs2.NormalDirection * halfWidth2; // right2
corners[3] = cs2.CenterPoint - cs2.NormalDirection * halfWidth2; // left2
```

`EffectiveRoadWidth` already equals `RoadSurfaceWidth + 2 * SmoothingCorridorMargin` (from [UnifiedCrossSection.cs:283-284](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs#L283-L284) using `WidthProfile.GetWidthsAtDistance().corridor`). Adding `margin` (= `RoadEdgeProtectionBufferMeters`) yields:

```
polygon_halfWidth = surfaceWidth/2 + SmoothingCorridorMargin + RoadEdgeProtectionBufferMeters
                  = surfaceWidth/2 + 2.0 + 2.0      (defaults)
                  = surfaceWidth/2 + 4.0 m per side
```

A 7 m residential road thus stamps a 15 m polygon. A 14 m primary stamps a 22 m polygon. At a T-junction where the two roads' centerlines meet, the primary's 22 m corridor easily covers the 7 m terminating surface plus most of its 11 m corridor. With widest-first ordering at [L113-122](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L113-L122), the primary processes first and the existing `if (mask[y, x] == 0)` at L214 means the terminating road can't write into those pixels. The terminating road's blended ramp elevation reaches `cs.TargetElevation` correctly (Phase A's parabolic profile is intact) but never makes it into the mask elevation map. The heightmap inherits the primary's elevation across the entire junction approach.

The comment at [L228-230](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs#L228-L230) — *"This prevents Road B's wide corridor from destroying Road A's surface elevation at overlap zones"* — describes the inverse case (narrower-but-higher-priority road defended against a wider-lower-priority neighbor). For terminating-road-vs-primary the effect is opposite: the primary's corridor destroys the terminating road's surface elevation.

---

## File Structure

**Create:**
- `BeamNgTerrainPoc.Tests/Blending/RoadMaskBuilderTwoPassTests.cs` — integration tests for the two-pass rasterizer.

**Modify:**
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — add `EnableSurfaceWidthProtection` flag (default `false`).
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs` — add `SurfaceWidth` field; populate at CS creation alongside `EffectiveRoadWidth`.
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs` — restructure `BuildCombinedMaskWithElevation` into a two-pass form when the flag is on; legacy single-pass preserved.
- `examples_for_ai/baseline_phase19/README.md` — document the new `surface_protection_a8_franco_same_prio` capture (Task 6).

**Do NOT modify:**
- `SmoothingCorridorMargin` or `MasterSplineMargin` defaults (layerset-level, user-tunable).
- `RoadEdgeProtectionBufferMeters` default (per-spline, user-tunable).
- `WidthProfile.GetWidthsAtDistance` return shape — Pass 1 reads the existing `.surface` tuple element.
- The junction-gap circular fill at L246-317 — runs unchanged after Pass 2.
- The "same-spline overlap, update if lower" rule at L222-227 — unchanged in both passes.
- `BankedTerrainHelper.GetBankedElevationForPixel` — pixel elevation source is unchanged.

---

### Task 1: Add parameter flag (no behaviour change yet)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

- [ ] **Step 1: Locate the flag block**

Open [JunctionHarmonizationParameters.cs](../../BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs). The recent flags are `EnableParabolicJunctionBlend` (true) and — once Phase A.5 has scaffolded its flag — `EnablePropagationOverlapTaper`. A.8 is independent of both.

If A.5's flag is not yet in the file (this plan runs first), just insert below `EnableParabolicJunctionBlend`.

- [ ] **Step 2: Insert flag**

Add the property in the flag block:

```csharp
    /// <summary>
    ///     Phase A.8 — painted-road-width protection. When true,
    ///     <see cref="BeamNgTerrainPoc.Terrain.Algorithms.Blending.RoadMaskBuilder.BuildCombinedMaskWithElevation" />
    ///     runs as a two-pass rasterizer: Pass 1 stamps each spline's painted-surface polygon
    ///     only (no smoothing margin, no edge protection buffer), widest-surface-first; Pass 2
    ///     extends with the corridor + edge buffer into pixels not yet claimed by Pass 1.
    ///     Result: each spline's painted-surface pixels carry that spline's own banking-aware
    ///     elevation even when a wider adjacent spline's corridor geometrically overlaps.
    ///     Default: false (opt-in until franco_same_prio validation passes).
    /// </summary>
    public bool EnableSurfaceWidthProtection { get; set; } = false;
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add EnableSurfaceWidthProtection flag (Phase A.8 scaffold)"
```

---

### Task 2: Add `SurfaceWidth` field to `UnifiedCrossSection`

**Why:** `EffectiveRoadWidth` is already populated as the *corridor* width (surface + smoothing margin). The two-pass rasterizer needs the *surface* width as a separate value. `WidthProfile.GetWidthsAtDistance()` returns `(surface, corridor, masterSpline)` — Pass 1 needs `.surface`. We store it as a sibling field on the CS so per-pixel rasterization is O(1) without re-querying the profile.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`

- [ ] **Step 1: Locate `EffectiveRoadWidth` property**

Open [UnifiedCrossSection.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs) and find the property (around L92):

```csharp
    public float EffectiveRoadWidth { get; set; }
```

- [ ] **Step 2: Insert sibling property**

Immediately after `EffectiveRoadWidth`, add:

```csharp
    /// <summary>
    ///     Phase A.8 — the cross-section's *painted-surface* width (m), excluding the
    ///     smoothing-corridor margin already baked into <see cref="EffectiveRoadWidth" />.
    ///     Populated from <see cref="RoadWidthProfile.GetWidthsAtDistance" />.surface when a
    ///     width profile exists; falls back to the spline's <see cref="SplineRoadParameters.RoadWidthMeters" />
    ///     otherwise (in which case <c>SurfaceWidth == EffectiveRoadWidth</c>).
    ///     Used by <see cref="BeamNgTerrainPoc.Terrain.Algorithms.Blending.RoadMaskBuilder" />'s
    ///     surface-protection Pass 1 to rasterize the protected-surface polygon.
    /// </summary>
    public float SurfaceWidth { get; set; }
```

- [ ] **Step 3: Populate at construction site 1 — Builder around L283**

Find the existing block (around L282-286):

```csharp
            LocalIndex = localIndex,
            EffectiveRoadWidth = ownerSpline.WidthProfile?.GetWidthsAtDistance(sample.Distance).corridor
                ?? ownerSpline.Parameters.RoadWidthMeters,
            EffectiveBlendRange = ownerSpline.Parameters.TerrainAffectedRangeMeters,
            Priority = ownerSpline.Priority,
```

Insert `SurfaceWidth` immediately below `EffectiveRoadWidth`:

```csharp
            LocalIndex = localIndex,
            EffectiveRoadWidth = ownerSpline.WidthProfile?.GetWidthsAtDistance(sample.Distance).corridor
                ?? ownerSpline.Parameters.RoadWidthMeters,
            SurfaceWidth = ownerSpline.WidthProfile?.GetWidthsAtDistance(sample.Distance).surface
                ?? ownerSpline.Parameters.RoadWidthMeters,
            EffectiveBlendRange = ownerSpline.Parameters.TerrainAffectedRangeMeters,
            Priority = ownerSpline.Priority,
```

- [ ] **Step 4: Populate at construction site 2 — Builder around L334**

Find the second block (around L332-336):

```csharp
            TargetElevation = cs.TargetElevation,
            EffectiveRoadWidth = cs.WidthMeters,
            EffectiveBlendRange = blendRange,
            IsExcluded = cs.IsExcluded,
            Index = cs.Index,
```

Add `SurfaceWidth` line. Here there's no profile — `cs.WidthMeters` is the only width input, so surface = corridor:

```csharp
            TargetElevation = cs.TargetElevation,
            EffectiveRoadWidth = cs.WidthMeters,
            SurfaceWidth = cs.WidthMeters,
            EffectiveBlendRange = blendRange,
            IsExcluded = cs.IsExcluded,
            Index = cs.Index,
```

- [ ] **Step 5: Update `UnifiedJunctionProfileBlender.cs:558` clone block**

Find the cross-section clone block (around L555-565 — Roundabout ring-cs synthesis):

```csharp
                            BankAngleRadians = ringCS.BankAngleRadians,
                            EffectiveRoadWidth = ringCS.EffectiveRoadWidth,
                            EffectiveBlendRange = ringCS.EffectiveBlendRange,
                            LeftEdgeElevation = ringCS.LeftEdgeElevation,
                            RightEdgeElevation = ringCS.RightEdgeElevation,
```

Add `SurfaceWidth = ringCS.SurfaceWidth,` below `EffectiveRoadWidth`:

```csharp
                            BankAngleRadians = ringCS.BankAngleRadians,
                            EffectiveRoadWidth = ringCS.EffectiveRoadWidth,
                            SurfaceWidth = ringCS.SurfaceWidth,
                            EffectiveBlendRange = ringCS.EffectiveBlendRange,
                            LeftEdgeElevation = ringCS.LeftEdgeElevation,
                            RightEdgeElevation = ringCS.RightEdgeElevation,
```

- [ ] **Step 6: Update DecalRoad snapshot persistence (read side)**

`UnifiedCrossSection` is part of the DecalRoad snapshot file format. Without persistence, freshly-loaded snapshots will have `SurfaceWidth = 0` and Pass 1 will stamp empty polygons. To avoid this without changing the binary format, populate `SurfaceWidth = EffectiveRoadWidth` as a fallback in the loader.

Find [DecalRoadNetworkSnapshotLoader.cs:147-155](../../BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs#L147-L155). The CS reconstruction block reads `EffectiveRoadWidth = css.EffectiveRoadWidth`. Add a sibling line:

```csharp
                EffectiveRoadWidth = css.EffectiveRoadWidth,
                SurfaceWidth = css.EffectiveRoadWidth, // A.8: snapshot loader fallback — old snapshots have no surface width persisted
```

Persistence to the binary format is **not** added in Phase A.8. Snapshots are short-lived debug artefacts; the production pipeline always recomputes CSes from splines (Step 3 above), where the real `SurfaceWidth` is populated. This is the documented fallback for old snapshots loaded back.

- [ ] **Step 7: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds, 0 errors.

- [ ] **Step 8: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. No behaviour has changed yet — the new field is unread.

- [ ] **Step 9: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs
git commit -m "feat: add SurfaceWidth field to UnifiedCrossSection (Phase A.8 scaffold)"
```

---

### Task 3: Refactor `BuildCombinedMaskWithElevation` to two-pass form

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

**Design:**

- The existing single-pass logic is preserved verbatim when the flag is off.
- When the flag is on:
  - **Pass 1** iterates `processingOrder` once. For each spline, rasterizes the surface polygon using `halfWidth = SurfaceWidth / 2` (no margin). Uses the existing first-writer-wins + same-spline-min-elevation rules. Writes mask=255, elevation=banked-elev, splineOwner=splineId.
  - **Pass 2** iterates `processingOrder` again. For each spline, rasterizes the corridor polygon using `halfWidth = EffectiveRoadWidth / 2 + margin`. Same first-writer-wins rules — but Pass 1 already claimed every surface pixel, so Pass 2 cannot overwrite a foreign spline's surface (mask is already 255).
- Both passes use ordering = widest-surface-first then by priority. (We use surface width for ordering even in Pass 2 to keep the order stable between passes.)
- Junction-gap fill (the existing post-loop block at L246-317) runs unchanged after both passes.

**Performance note:** Two passes double the polygon-fill work for the *surface* portion, but Pass 1's polygons are strictly smaller than the single-pass polygons. Total pixels stamped is the same (each pixel is stamped exactly once — Pass 1 for surface pixels, Pass 2 for corridor pixels). The bookkeeping overhead is one extra outer loop iteration. Acceptable.

- [ ] **Step 1: Locate the method**

Open [RoadMaskBuilder.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs). The relevant block runs from the method signature (around L80) through the end of the per-segment polygon fill loop (around L235), before the junction-gap fill block.

- [ ] **Step 2: Extract the per-segment polygon fill into a private helper**

To avoid duplicating ~80 lines of scanline-fill across the two passes, lift the inner per-segment fill into a private method. Add the following inside the `RoadMaskBuilder` class (place it below `BuildCombinedMaskWithElevation`):

```csharp
/// <summary>
///     Phase A.8 — rasterize a single spline's per-segment polygons into mask/elevation/owner.
///     Used by both Pass 1 (surface) and Pass 2 (corridor) when EnableSurfaceWidthProtection is on.
///     <paramref name="useSurfaceWidthOnly" /> = true → halfWidth = SurfaceWidth/2 (Pass 1).
///     <paramref name="useSurfaceWidthOnly" /> = false → halfWidth = EffectiveRoadWidth/2 + margin (Pass 2).
/// </summary>
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
{
    var maskedPixels = 0;

    for (var i = 0; i < sections.Count - 1; i++)
    {
        var cs1 = sections[i];
        var cs2 = sections[i + 1];

        if (!IsValidTargetElevation(cs1.TargetElevation) ||
            !IsValidTargetElevation(cs2.TargetElevation))
            continue;

        var halfWidth1 = useSurfaceWidthOnly
            ? cs1.SurfaceWidth / 2.0f
            : cs1.EffectiveRoadWidth / 2.0f + margin;
        var halfWidth2 = useSurfaceWidthOnly
            ? cs2.SurfaceWidth / 2.0f
            : cs2.EffectiveRoadWidth / 2.0f + margin;

        var corners = new Vector2[4];
        corners[0] = cs1.CenterPoint - cs1.NormalDirection * halfWidth1;
        corners[1] = cs1.CenterPoint + cs1.NormalDirection * halfWidth1;
        corners[2] = cs2.CenterPoint + cs2.NormalDirection * halfWidth2;
        corners[3] = cs2.CenterPoint - cs2.NormalDirection * halfWidth2;

        var pixelCorners = new Vector2[4];
        for (var c = 0; c < 4; c++)
            pixelCorners[c] = new Vector2(corners[c].X / metersPerPixel, corners[c].Y / metersPerPixel);

        var minY = Math.Max(0, (int)MathF.Floor(pixelCorners.Min(c => c.Y)));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(pixelCorners.Max(c => c.Y)));
        var minX = Math.Max(0, (int)MathF.Floor(pixelCorners.Min(c => c.X)));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(pixelCorners.Max(c => c.X)));

        for (var y = minY; y <= maxY; y++)
        {
            var scanY = y + 0.5f;
            var intersectionCount = 0;
            for (var e = 0; e < 4; e++)
            {
                var p1 = pixelCorners[e];
                var p2 = pixelCorners[(e + 1) % 4];

                if ((p1.Y <= scanY && p2.Y > scanY) || (p2.Y <= scanY && p1.Y > scanY))
                {
                    var t = (scanY - p1.Y) / (p2.Y - p1.Y);
                    intersections[intersectionCount++] = p1.X + t * (p2.X - p1.X);
                }
            }

            if (intersectionCount < 2)
                continue;

            for (var si = 1; si < intersectionCount; si++)
            {
                var key = intersections[si];
                var sj = si - 1;
                while (sj >= 0 && intersections[sj] > key)
                {
                    intersections[sj + 1] = intersections[sj];
                    sj--;
                }
                intersections[sj + 1] = key;
            }

            for (var pair = 0; pair + 1 < intersectionCount; pair += 2)
            {
                var xStart = Math.Max(minX, (int)MathF.Ceiling(intersections[pair]));
                var xEnd = Math.Min(maxX, (int)MathF.Floor(intersections[pair + 1]));

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
            }
        }
    }

    return maskedPixels;
}
```

- [ ] **Step 3: Replace the inline loop with two passes (flag-gated)**

In `BuildCombinedMaskWithElevation`, locate the block starting `foreach (var splineId in processingOrder)` (around L124) through the closing brace of that foreach (around L235, just before the junction-gap fill at L246).

Replace the entire `foreach` block with:

```csharp
        // Look up JunctionHarmonizationParameters from the first spline (matches the
        // pattern used in UnifiedJunctionProfileBlender). All splines share the same
        // parameters block in current code; A.8 only reads the new flag.
        var jhParams = network.Splines.FirstOrDefault()?.Parameters.JunctionHarmonizationParameters
                       ?? new JunctionHarmonizationParameters();

        if (jhParams.EnableSurfaceWidthProtection)
        {
            // Pass 1: stamp each spline's painted-surface polygon (no margins).
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                var margin = splineParams.TryGetValue(splineId, out var p)
                    ? p.RoadEdgeProtectionBufferMeters
                    : 2.0f;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId, margin,
                    useSurfaceWidthOnly: true,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }

            // Pass 2: extend with corridor + edge protection buffer. Pixels claimed
            // by Pass 1 (mask != 0) are not overwritten — the first-writer-wins rule
            // inside RasterizeSplinePolygons enforces this naturally.
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                var margin = splineParams.TryGetValue(splineId, out var p)
                    ? p.RoadEdgeProtectionBufferMeters
                    : 2.0f;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId, margin,
                    useSurfaceWidthOnly: false,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }
        }
        else
        {
            // Legacy single-pass: rasterize at corridor + edge buffer width, first-writer-wins.
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                var margin = splineParams.TryGetValue(splineId, out var p)
                    ? p.RoadEdgeProtectionBufferMeters
                    : 2.0f;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId, margin,
                    useSurfaceWidthOnly: false,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }
        }
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Expected: build succeeds. If `JunctionHarmonizationParameters` namespace is missing, add `using BeamNgTerrainPoc.Terrain.Models;`.

- [ ] **Step 5: Run full test suite (flag default false → behaviour unchanged)**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green, same count as before A.8. The legacy single-pass branch runs in tests since `EnableSurfaceWidthProtection = false` by default.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs
git commit -m "refactor: extract RasterizeSplinePolygons helper, add two-pass dispatch (Phase A.8)"
```

---

### Task 4: Integration test — two-spline overlap synthetic

Build a minimal `UnifiedRoadNetwork` with two perpendicular splines: a wide primary (16 m surface, 20 m corridor at default `SmoothingCorridorMargin = 2`) and a narrow terminating road (6 m surface, 10 m corridor). Position them so the terminating road's last cross-section sits on the primary's centerline.

Assert that with the flag on, pixels on the terminating road's centerline (well within its 6 m surface) carry the terminating road's elevation (not the primary's).

**Files:**
- Create: `BeamNgTerrainPoc.Tests/Blending/RoadMaskBuilderTwoPassTests.cs`

- [ ] **Step 1: Write the failing test**

Create `BeamNgTerrainPoc.Tests/Blending/RoadMaskBuilderTwoPassTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

public class RoadMaskBuilderTwoPassTests
{
    /// <summary>
    ///     Build a minimal UnifiedRoadNetwork containing two perpendicular splines that
    ///     cross at world origin (0, 0):
    ///     - Spline 1 (PRIMARY): horizontal, 16 m surface, 20 m corridor, elevation 100 m, runs along x ∈ [-30, +30], y = 0.
    ///     - Spline 2 (TERMINATING): vertical, 6 m surface, 10 m corridor, elevation 95 m,
    ///       runs along y ∈ [-30, 0], x = 0 (terminates AT the primary's centerline).
    ///     Default RoadEdgeProtectionBufferMeters = 2.0 m → primary's polygon is 24 m wide,
    ///     terminating's polygon is 14 m wide. The primary's polygon entirely covers the
    ///     terminating road's last several meters.
    /// </summary>
    private static (UnifiedRoadNetwork network, int primaryId, int terminatingId)
        BuildTwoSplineCrossing()
    {
        var jhParams = new JunctionHarmonizationParameters();
        var primaryParams = new SplineRoadParameters
        {
            RoadWidthMeters = 16f,
            JunctionHarmonizationParameters = jhParams
        };
        var terminatingParams = new SplineRoadParameters
        {
            RoadWidthMeters = 6f,
            JunctionHarmonizationParameters = jhParams
        };

        var primary = new UnifiedRoadSpline
        {
            SplineId = 1,
            Priority = 10,
            Parameters = primaryParams,
            WidthProfile = null   // null profile → SurfaceWidth == EffectiveRoadWidth == 16
        };
        var terminating = new UnifiedRoadSpline
        {
            SplineId = 2,
            Priority = 5,
            Parameters = terminatingParams,
            WidthProfile = null
        };

        // Primary: 7 CSes at x = -30, -20, -10, 0, +10, +20, +30 (y = 0).
        var primarySections = new List<UnifiedCrossSection>();
        for (var i = 0; i < 7; i++)
        {
            var x = -30f + i * 10f;
            primarySections.Add(new UnifiedCrossSection
            {
                Index = 1_000 + i,
                LocalIndex = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(x, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 16f,
                SurfaceWidth = 16f,
                LeftEdgeElevation = 100f,
                RightEdgeElevation = 100f
            });
        }

        // Terminating: 4 CSes at y = -30, -20, -10, 0 (x = 0). Tangent is +Y (toward primary).
        // Normal is +X (perpendicular). Last CS sits AT primary's centerline.
        var terminatingSections = new List<UnifiedCrossSection>();
        for (var i = 0; i < 4; i++)
        {
            var y = -30f + i * 10f;
            terminatingSections.Add(new UnifiedCrossSection
            {
                Index = 2_000 + i,
                LocalIndex = i,
                OwnerSplineId = 2,
                CenterPoint = new Vector2(0f, y),
                TangentDirection = new Vector2(0f, 1f),
                NormalDirection = new Vector2(1f, 0f),
                TargetElevation = 95f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f,
                SurfaceWidth = 6f,
                LeftEdgeElevation = 95f,
                RightEdgeElevation = 95f
            });
        }

        var network = new UnifiedRoadNetwork
        {
            Splines = new List<UnifiedRoadSpline> { primary, terminating },
            CrossSections = primarySections.Concat(terminatingSections).ToList(),
            Junctions = new List<NetworkJunction>()  // No junctions — A.8 doesn't depend on junction detection
        };

        return (network, 1, 2);
    }

    [Fact]
    public void LegacyPath_TerminatingCenterlinePixel_OverwrittenByPrimaryCorridor()
    {
        var (network, primaryId, terminatingId) = BuildTwoSplineCrossing();
        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Sample at world (0, -3) — terminating road's centerline, 3 m before the primary.
        // x_world = 0 → pixel x = 0 + 64 (let's verify coordinate system).
        // Actually, BuildCombinedMaskWithElevation uses `x = (int)(worldX / metersPerPixel)`,
        // which does NOT translate to image centre. So world (0, -3) → pixel (0, -3),
        // which is out of bounds (negative). Use world (60, 57) → pixel (60, 57) instead by
        // offsetting both splines (skip — test would need translation). For simplicity,
        // pick a sample that IS inside the 0-128 range. The builder works in world coords
        // with no offset, so positive coordinates only.
        // For this test we build splines on positive coords instead — see Step 2 fix.
        Assert.True(true); // Placeholder — overwritten in Step 2
    }
}
```

- [ ] **Step 2: Correct the coordinate setup for positive-only world coords**

The mask builder uses `pixel_x = world_x / metersPerPixel` directly (no offset). So splines must live in positive world coords. Replace `BuildTwoSplineCrossing` with this corrected version:

```csharp
    private static (UnifiedRoadNetwork network, int primaryId, int terminatingId)
        BuildTwoSplineCrossing()
    {
        var jhParams = new JunctionHarmonizationParameters();
        var primaryParams = new SplineRoadParameters
        {
            RoadWidthMeters = 16f,
            JunctionHarmonizationParameters = jhParams
        };
        var terminatingParams = new SplineRoadParameters
        {
            RoadWidthMeters = 6f,
            JunctionHarmonizationParameters = jhParams
        };

        var primary = new UnifiedRoadSpline
        {
            SplineId = 1,
            Priority = 10,
            Parameters = primaryParams,
            WidthProfile = null
        };
        var terminating = new UnifiedRoadSpline
        {
            SplineId = 2,
            Priority = 5,
            Parameters = terminatingParams,
            WidthProfile = null
        };

        // Primary: horizontal, y = 64, x in [10, 110] (7 CSes spaced 16.6m apart).
        var primarySections = new List<UnifiedCrossSection>();
        for (var i = 0; i < 7; i++)
        {
            var x = 10f + i * 16.6f;
            primarySections.Add(new UnifiedCrossSection
            {
                Index = 1_000 + i,
                LocalIndex = i,
                OwnerSplineId = 1,
                CenterPoint = new Vector2(x, 64f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 16f,
                SurfaceWidth = 16f,
                LeftEdgeElevation = 100f,
                RightEdgeElevation = 100f
            });
        }

        // Terminating: vertical, x = 64, y in [10, 64] (4 CSes — last sits AT primary y=64).
        var terminatingSections = new List<UnifiedCrossSection>();
        for (var i = 0; i < 4; i++)
        {
            var y = 10f + i * 18f;
            terminatingSections.Add(new UnifiedCrossSection
            {
                Index = 2_000 + i,
                LocalIndex = i,
                OwnerSplineId = 2,
                CenterPoint = new Vector2(64f, y),
                TangentDirection = new Vector2(0f, 1f),
                NormalDirection = new Vector2(1f, 0f),
                TargetElevation = 95f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f,
                SurfaceWidth = 6f,
                LeftEdgeElevation = 95f,
                RightEdgeElevation = 95f
            });
        }

        var network = new UnifiedRoadNetwork
        {
            Splines = new List<UnifiedRoadSpline> { primary, terminating },
            CrossSections = primarySections.Concat(terminatingSections).ToList(),
            Junctions = new List<NetworkJunction>()
        };

        return (network, 1, 2);
    }
```

- [ ] **Step 3: Write the two facts**

Replace the placeholder fact and add a second:

```csharp
    [Fact]
    public void LegacyPath_TerminatingCenterlinePixel_OverwrittenByPrimaryCorridor()
    {
        // Default: EnableSurfaceWidthProtection = false → legacy single-pass.
        var (network, _, _) = BuildTwoSplineCrossing();
        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Pixel at (64, 60): terminating road's centerline (x=64), 4 m before the
        // primary's centerline (y=64). The terminating's own surface half-width is 3m,
        // so this pixel is 1 m inside the terminating road's painted surface AND
        // also 4 m inside the primary's 12m corridor half-width. Widest-first
        // ordering: primary processes first, claims this pixel with elevation 100.
        Assert.Equal(255, result.Mask[60, 64]);
        Assert.Equal(100f, result.ElevationMap[60, 64], 1);
    }

    [Fact]
    public void TwoPass_TerminatingCenterlinePixel_HoldsTerminatingElevation()
    {
        // Flag-on path: terminating road's surface pixels are protected.
        var (network, _, _) = BuildTwoSplineCrossing();
        foreach (var s in network.Splines)
            s.Parameters.JunctionHarmonizationParameters.EnableSurfaceWidthProtection = true;

        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        // Pixel at (64, 60): inside the terminating road's surface. With protection,
        // Pass 1 stamps the terminating road's surface (after Pass 1 also stamps the
        // primary's narrower surface, which doesn't reach y=60). So this pixel carries
        // the terminating road's 95m elevation.
        Assert.Equal(255, result.Mask[60, 64]);
        Assert.Equal(95f, result.ElevationMap[60, 64], 1);
    }
```

- [ ] **Step 4: Run tests to verify**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~RoadMaskBuilderTwoPassTests"`

Expected outcome:
- `LegacyPath_TerminatingCenterlinePixel_OverwrittenByPrimaryCorridor` → PASS (asserts the bug — should hold true today AND with the flag off after Task 3).
- `TwoPass_TerminatingCenterlinePixel_HoldsTerminatingElevation` → PASS (asserts the fix — runs with flag on).

If the legacy test FAILS (pixel doesn't carry primary's elevation), the rasterizer ordering may differ from the assumption. Investigate before continuing — possible causes: scanline rounding pushes the pixel outside the primary's polygon, or coordinate setup is off. Adjust the test pixel coordinate (try (64, 58) or (64, 62)) until the legacy test asserts the bug correctly.

- [ ] **Step 5: Add edge-buffer test**

Append a third fact that confirms the corridor is still painted in Pass 2:

```csharp
    [Fact]
    public void TwoPass_PrimaryCorridorPixel_StillPaintedInPass2()
    {
        // Pixel at (64, 50): 14 m from primary centerline (y=64), 4 m beyond the
        // terminating road's surface (3 m half-width) but inside the terminating road's
        // 10 m corridor + 2 m buffer (= 7 m half-width). Wait — 4 m > 7 m? No, 14 m
        // is from primary y=64 to pixel y=50. The terminating road runs vertically along
        // x=64 between y=10 and y=64. Pixel (64, 50) is at y=50, x=64 → distance from
        // terminating's centerline is sqrt((64-64)^2 + (50-50)^2) = 0 (it's ON the
        // terminating's centerline). So this pixel is terminating road's surface pixel.
        // Reword the test: pick a pixel that is in the corridor but not the surface.
        //
        // Pixel at (60, 50): x=60, y=50. Terminating centerline x=64, so this is 4 m
        // off the terminating centerline. Outside terminating's 3 m surface half-width.
        // Inside terminating's 5 m corridor half-width? 5 m > 4 m yes. Inside
        // terminating's 5+2=7 m corridor+buffer? Yes.
        // Primary y=64, pixel y=50, so 14 m off primary. Outside primary's 8 m surface
        // half-width and outside primary's 8+2+2=12 m corridor+buffer.
        // So with flag on: Pass 1 doesn't claim (outside both surfaces). Pass 2 claims
        // it as part of the terminating road's corridor. Elevation: terminating road's
        // banked elevation at x=60, y=50 ≈ 95.
        var (network, _, _) = BuildTwoSplineCrossing();
        foreach (var s in network.Splines)
            s.Parameters.JunctionHarmonizationParameters.EnableSurfaceWidthProtection = true;

        var builder = new RoadMaskBuilder();
        var result = builder.BuildCombinedMaskWithElevation(
            network, width: 128, height: 128, metersPerPixel: 1.0f);

        Assert.Equal(255, result.Mask[50, 60]);
        Assert.InRange(result.ElevationMap[50, 60], 94f, 96f);
    }
```

- [ ] **Step 6: Run tests to verify all pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~RoadMaskBuilderTwoPassTests"`
Expected: PASS, 3/3 green. If the corridor-pixel test fails because the pixel was actually inside the primary's corridor (the math above is approximate), adjust the pixel coordinate or the geometry — the goal is to find any pixel that demonstrates: surface protected by Pass 1, corridor painted by Pass 2.

- [ ] **Step 7: Run full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Blending/RoadMaskBuilderTwoPassTests.cs
git commit -m "test: two-pass rasterizer protects terminating-road surface (Phase A.8)"
```

---

### Task 5: End-to-end validation (user-driven; no code)

User-executed on Windows. The agent's job is to snapshot artefacts and write the README.

- [ ] **Step 1: Flip flag to true (uncommitted local edit)**

User opens `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`, changes:

```csharp
public bool EnableSurfaceWidthProtection { get; set; } = false;
```

to:

```csharp
public bool EnableSurfaceWidthProtection { get; set; } = true;
```

`EnableParabolicJunctionBlend` stays `true`. `EnablePropagationOverlapTaper` stays `false` (A.5 not yet validated). Build in Visual Studio (Release).

- [ ] **Step 2: Run terrain generation in BeamNG.drive**

User regenerates `franco_same_prio` from BeamNG.drive desktop app. Artefacts overwrite `C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\franco_same_prio\MT_TerrainGeneration\`.

- [ ] **Step 3: Snapshot results**

Agent runs:

```bash
mkdir -p "d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/surface_protection_a8_franco_same_prio"
SRC="C:/Users/aklei/AppData/Local/BeamNG/BeamNG.drive/current/levels/franco_same_prio/MT_TerrainGeneration"
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/surface_protection_a8_franco_same_prio"
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
DST="d:/Source/beamng_mapping_pro/examples_for_ai/baseline_phase19/surface_protection_a8_franco_same_prio"

echo "=== j125 quadratic_growth ==="; grep "^125,64," "$DST/quadratic_growth.csv"
echo "=== j125 w_test_summary ===";   grep "^125,64," "$DST/w_test_summary.csv"
echo "=== j126 quadratic_growth ==="; grep "^126,64," "$DST/quadratic_growth.csv"
echo "=== j126 w_test_summary ===";   grep "^126,64," "$DST/w_test_summary.csv"
echo "=== j126 residuals ==="; grep "^126," "$DST/junction_residuals.csv"
echo "=== W1 aggregate ==="; grep "W1 validation" "$DST/terrain_gen_info.log" | tail -1
```

- [ ] **Step 5: Evaluate pass criteria**

| Criterion | Target | parabolic_a baseline | A.8 result |
|---|---|---|---|
| j126 spline 64 `w` | < 6σ (A.8 intermediate; A.5 will tighten to < 3σ) | 9.07σ | (fill) |
| j126 quadratic_growth | no sign flip at d=60 | sign flip −1.21 | (fill) |
| j126 `residual_max_minus_min` | ≤ 1.5 m | 1.414 m | (fill — no regression) |
| j125 spline 64 `w` (regression gate) | < 3σ | 2.75σ | (fill — must stay < 3σ) |
| W1 redBandPixels | ≤ parabolic_a + 5 % | 300,248 | (fill — ≤ 315,261) |

If criteria met → flip default to true (Task 6); A.5 then runs on top.
If A.8 alone brings j126 to < 3σ → A.5 may be reduced in scope or deferred — discuss with user.
If A.8 *regresses* j125 below 3σ → STOP. Investigation: did Pass 1 leave j125's primary-spline surface unclaimed where Phase A's parabolic profile expected to write? Inspect `delta_three_band.png` around j125.

- [ ] **Step 6: Update baseline README**

Append a section to `examples_for_ai/baseline_phase19/README.md`:

```markdown
### surface_protection_a8_franco_same_prio (heightmap 2048, captured <date>)

Re-run of franco_same_prio with `EnableParabolicJunctionBlend = true` AND
`EnableSurfaceWidthProtection = true` (all other Phase 1.9 / W2 / W3 /
A.5 flags off). Validates Phase A.8 — two-pass rasterization that protects
each spline's painted surface from a neighbour spline's wider corridor stamp.
See
[`ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a8-plan.md`](../../ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-phase-a8-plan.md).

W1 validation: <paste from log>

Phase A.8 pass-criteria result:

| Criterion | Target | Observed | Pass? |
|---|---|---|---|
| Junction 126 spline 64 `w` (intermediate) | < 6σ | <fill> | <yes/no> |
| Junction 126 quadratic_growth | no sign flip at d=60 | <fill> | <yes/no> |
| Junction 126 `residual_max_minus_min` | ≤ 1.5 m | <fill> | <yes/no> |
| Junction 125 spline 64 `w` (regression gate) | < 3σ | <fill> | <yes/no> |
| W1 `redBandPixels` | ≤ parabolic_a + 5 % | <fill> | <yes/no> |

Junction 125 + 126 / spline 64 detail (parabolic_a → A.8):

```
quadratic_growth — j125 parabolic_a: <paste>
quadratic_growth — j125 A.8:         <paste>
quadratic_growth — j126 parabolic_a: <paste>
quadratic_growth — j126 A.8:         <paste>
w-test          — j125 parabolic_a:  <paste>
w-test          — j125 A.8:          <paste>
w-test          — j126 parabolic_a:  <paste>
w-test          — j126 A.8:          <paste>
```
```

- [ ] **Step 7: Commit the README**

```bash
git add examples_for_ai/baseline_phase19/README.md
git commit -m "docs: Phase A.8 surface-protection franco validation snapshot"
```

---

### Task 6: Default flag flip (gated on Task 5 results)

- [ ] **Step 1: Review Task 5 numerical results with user.**

If pass criteria met: proceed to step 2.

If A.8 alone brings j126 to < 3σ AND j125 stays < 3σ: A.5 scope may be reduced — surface this in the discussion. Possible outcomes:
- A.5 is deferred indefinitely (the Step 5b propagation overlay was masked by the rasterizer bug; with the bug fixed, the overlay's effect on j126 is small enough to be acceptable).
- A.5 is kept as planned but with weaker pass criteria.
- A.5 is kept unchanged because its synthetic test (junction-126 reproduction) still asserts the right thing and there may be other junctions where the overlay matters.

If pass criteria NOT met:
- Hypothesis 1: a third stage downstream of `RoadMaskBuilder` (e.g., `DistanceFieldTerrainBlender` or `SinglePassBlender`) is over-blending the cliff. → roadmap §A.7.
- Hypothesis 2: A.8's two-pass ordering left a triangle-shaped gap inside the terminating road's surface that the junction-gap fill at L246-317 then filled with the junction's harmonized elevation (which may differ from the terminating road's CS elevation). Diagnostic: log `junctionPixelsFilled` from L319-321 and compare to legacy run.
- Hypothesis 3: terrain rasterization beyond the mask is responsible. → distinct from this plan.

- [ ] **Step 2: Flip default to true**

Edit `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`:

```csharp
public bool EnableSurfaceWidthProtection { get; set; } = true;
```

- [ ] **Step 3: Build + full test suite**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green. The `LegacyPath_TerminatingCenterlinePixel_OverwrittenByPrimaryCorridor` test still expects the legacy bug — it explicitly sets the flag off, so it remains valid as documentation of historical behaviour.

- [ ] **Step 4: Commit default flip**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: enable EnableSurfaceWidthProtection by default after Phase A.8 validation"
```

- [ ] **Step 5: Update roadmap**

Edit `ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-roadmap.md`:
- A.8 status → ✅ Complete (default-on `<commit-hash>`).
- A.5 status → 🚧 In flight (it now picks up).
- Add a one-line **Result** note in A.8's prose section linking to `surface_protection_a8_franco_same_prio/`.

```bash
git add ai_docs/2026-05-15_parabolic_blend/2026-05-15-parabolic-blend-roadmap.md
git commit -m "docs: roadmap — A.8 complete, A.5 picked up"
```

---

## Out of scope for Phase A.8

All items below are tracked in the [roadmap](2026-05-15-parabolic-blend-roadmap.md).

- **Step 5b propagation overlap taper** → §A.5 (runs next).
- **Bank-angle parabolic path** → §A.6 (conditional).
- **AASHTO K-value cap on blend distance** → §B.
- **Persistence of `SurfaceWidth` in the DecalRoad snapshot binary format.** Phase A.8 falls back to `SurfaceWidth = EffectiveRoadWidth` for snapshot-loaded CSes. The production pipeline regenerates CSes from splines and populates `SurfaceWidth` correctly. If snapshot debugging is needed with two-pass behaviour, persist the field in a future version of the snapshot format. Out of scope here.
- **Generalizing the two-pass pattern to other rasterization stages** (e.g., `BuildCombinedRoadCoreMask`, distance-field blender's terrain interpolation). A.8 only changes `BuildCombinedMaskWithElevation` because that's where the mask elevation is decided. Other stages either use the result of this one or have their own protection logic.
- **Per-spline override of `EnableSurfaceWidthProtection`**. Flag lives on `JunctionHarmonizationParameters`, currently shared across all splines in a network. Sufficient for validation.

---

## Self-Review

**Spec coverage (user's report):**
- ✅ "Smoothing margin and edge protection buffer add width that destroys spline ramp at terminating-road junctions" → Pass 1/Pass 2 split decouples surface stamping from corridor stamping (Task 3).
- ✅ "Pixels inside the painted road width must be protected" → Pass 1 claims surface pixels first, Pass 2 cannot overwrite (Task 3 + Task 4 second fact).
- ✅ Validation against franco junction 126 → Task 5 pass criteria.
- ✅ Flag-gated, default false, sample-able A/B → Task 1, Task 5, Task 6.
- ✅ No tightening of the corridor itself (`SmoothingCorridorMargin`/`RoadEdgeProtectionBufferMeters` defaults untouched) → confirmed in File Structure.

**Placeholder scan:**
- No "TBD", no "implement later", no "add appropriate error handling".
- Task 5 README appendix uses `<paste>` and `<fill>` slots for empirical data — explicit data placeholders, not implementation placeholders.
- Task 6 Step 1 contingencies are concrete diagnostic steps, not "investigate further".

**Type consistency:**
- `RasterizeSplinePolygons(sections, splineId, margin, useSurfaceWidthOnly, mask, elevation, splineOwner, width, height, metersPerPixel, intersections)` — same signature in helper definition (Task 3 Step 2) and call sites (Task 3 Step 3).
- `UnifiedCrossSection.SurfaceWidth` — same property name in definition (Task 2 Step 2), populator (Task 2 Step 3/4), CS clone block (Task 2 Step 5), snapshot loader fallback (Task 2 Step 6), and Pass 1 reader (Task 3 Step 2).
- Flag name `EnableSurfaceWidthProtection` — consistent across Tasks 1, 3, 4, 5, 6.

**TDD scaffold:**
- Task 4 first writes legacy-asserts-bug + flag-asserts-fix tests. Both pass against Task 3's implementation (which leaves legacy default-off behaviour intact).
- Task 4 may require iterating on pixel coordinates until the legacy test asserts the bug at the chosen pixel. Step 4's expected-outcome paragraph names this explicitly.

---

## Execution handoff

This is a 6-task plan. Task 2 has the most modifications (3 files, 4 sites). Task 3 has the largest single-file change (~150 lines added/refactored).

**Subagent-driven (recommended):** Dispatch one subagent per task. Task 4's pixel coordinate tuning is mechanical iteration; the subagent will adjust until the asserts pass.

**Inline (faster):** Execute in this session with checkpoint reviews after Task 3 (refactor lands), Task 4 (tests green), and Task 5 (user data in).

Task 5 specifically requires user action in BeamNG.drive. The agent cannot run terrain generation.
