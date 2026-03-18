# DecalRoad Layer Constraints — Design Specification

**Date:** 2026-03-18
**Status:** Design approved, pending implementation
**Depends on:** `2026-03-12-decalroad-generation-design.md` (DecalRoad generation pipeline)

---

## Goal

Add two optional constraint filters to DecalRoad layer generation:

1. **Curve-Only Constraint** — Layer is only generated in curves that exceed a curvature threshold, with configurable transition zones extending before and after the curve. Primary use case: tire marks that only appear in curves.

2. **Randomizer Constraint** — Layer is generated as random patches along the road rather than continuously. Configurable patch length, gap length, and reproducible seeding. Primary use case: scattered surface patches (potholes, wear marks, oil stains) that shouldn't cover the entire road.

Both constraints can be combined on a single layer: the curve filter determines eligible road sections, then the randomizer subdivides those sections into scattered patches (e.g., tire marks that appear randomly within curves only).

---

## Architecture: Sequential Filter Pipeline

Filters are inserted into `DecalRoadGenerator.GenerateForSpline` as an additional stage between layer expansion and node generation. Each filter operates on cross-section index ranges and produces a narrowed set of ranges. Filters compose sequentially.

### Pipeline Position

The actual `GenerateForSpline` method has a two-phase architecture for lane-change support:
- **Phase A**: All layers except `IsPerLane`/`DirectionDivider` (when lane changes exist) — processes full spline span or lane-change ranges for AI roads
- **Phase B**: `IsPerLane`/`DirectionDivider` layers — re-expanded per lane-change range with segment-specific lane counts

Constraint filters apply **within each phase's per-range loop**, intersecting with any existing lane-change ranges:

```
New flow — filters insert inside the per-range generation:
  1. Sub-sample cross-sections at node spacing
  2. Compute cumulative distances for cross-sections (csDistances[])
  3. Phase A / Phase B layer loops (unchanged structure):
     For each layer × range (full span or lane-change range):
       ★ Compute constraint-filtered sub-ranges within [rangeStart..rangeEnd]:
         a. Start with [(rangeStart, rangeEnd)] as eligible
         b. Apply curve filter (if CurveOnly) → narrow to curve zones + transitions
         c. Apply randomizer (if Randomize) → subdivide into random patches
       ★ For each filtered sub-range:
         - Calculate laterally offset nodes (only for this sub-range)
         - Junction interruption → segments
         - For each segment: world coords, chunk, create DecalRoad
```

This means constraint filters compose with lane-change boundaries naturally — the filter only sees and operates within the lane-change range it's given. A curve that spans a lane-change boundary produces separate filtered ranges in each phase range.

### Why This Approach

- **Reuses existing curvature data**: `UnifiedCrossSection.Curvature` is already computed by `CurvatureCalculator` during the banking phase. No new calculation needed.
- **Minimal pipeline changes**: Filters produce index ranges that wrap the existing per-range generation loop. The core node generation, junction interruption, chunking, and scene writing logic is unchanged.
- **Natural composition**: Curve filter outputs ranges, randomizer takes ranges as input. Both active = curve first, then randomize within curves. Only one active = the other passes through. Neither active = full span.

---

## Data Model

### New Properties on `DecalRoadLayerDefinition`

Eight new properties, all defaulting to disabled/inactive:

```csharp
// ========================================
// CURVE-ONLY CONSTRAINT
// ========================================

/// <summary>
/// When true, this layer is only generated in road sections where curvature
/// exceeds CurveMinCurvature. Straight sections are skipped.
/// </summary>
public bool CurveOnly { get; set; }

/// <summary>
/// Minimum curvature threshold (1/radius in 1/meters) for curve detection.
/// Default 0.01 = curves tighter than 100m radius.
/// Uses absolute value of UnifiedCrossSection.Curvature (both left and right curves qualify).
/// </summary>
public float CurveMinCurvature { get; set; } = 0.01f;

/// <summary>
/// Distance in meters to extend the generated zone before and after the detected curve.
/// Creates a lead-in/lead-out zone. The existing FadeIn/FadeOut properties on the layer
/// control the visual fade independently.
/// </summary>
public float CurveTransitionLength { get; set; } = 15.0f;

// ========================================
// RANDOMIZER CONSTRAINT
// ========================================

/// <summary>
/// When true, this layer is generated as random patches with gaps instead of continuously.
/// </summary>
public bool Randomize { get; set; }

/// <summary>
/// Minimum length of each generated patch in meters.
/// </summary>
public float RandomMinPatchLength { get; set; } = 10.0f;

/// <summary>
/// Maximum length of each generated patch in meters.
/// </summary>
public float RandomMaxPatchLength { get; set; } = 50.0f;

/// <summary>
/// Minimum gap between patches in meters.
/// </summary>
public float RandomMinGapLength { get; set; } = 20.0f;

/// <summary>
/// Maximum gap between patches in meters.
/// </summary>
public float RandomMaxGapLength { get; set; } = 100.0f;
```

### New Property on `DecalRoadSettings`

```csharp
/// <summary>
/// Global seed for randomizer. Combined with spline ID for per-spline deterministic
/// randomization. Same seed + same settings = same output.
/// </summary>
public int RandomSeed { get; set; } = 42;
```

### Default Values Rationale

| Property | Default | Rationale |
|----------|---------|-----------|
| CurveMinCurvature | 0.01 (100m radius) | Moderate curve — covers typical highway/rural curves where tire marks appear |
| CurveTransitionLength | 15.0m | ~1-2 car lengths of lead-in, enough for visual smoothness without over-extending |
| RandomMinPatchLength | 10.0m | Short enough for a single wear patch |
| RandomMaxPatchLength | 50.0m | Long enough for an extended worn section |
| RandomMinGapLength | 20.0m | Ensures patches don't visually merge |
| RandomMaxGapLength | 100.0m | Keeps patches from being too sparse |
| RandomSeed | 42 | Arbitrary but deterministic |

---

## Filter Implementation

### New File: `DecalRoadLayerFilter`

Static class in `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs`.

### Curve Filter Algorithm

```
Input:
  - sections: IReadOnlyList<UnifiedCrossSection> (sub-sampled)
  - csDistances: IReadOnlyList<float> (cumulative distance per section, in meters)
  - minCurvature: float (threshold)
  - transitionLength: float (meters)
  - rangeStart: int (start of eligible range, for lane-change intersection)
  - rangeEnd: int (end of eligible range)

Output: List<(int Start, int End)> — index ranges within [rangeStart..rangeEnd]

Algorithm:
  1. Walk sections[rangeStart..rangeEnd], mark each as "in curve" if |cs.Curvature| >= minCurvature
  2. Group consecutive "in curve" indices into raw ranges
  3. For each raw range, extend by finding the nearest indices where
     csDistances[extended] is within transitionLength of the raw boundary:
     - extendedStart = last index i where csDistances[rawStart] - csDistances[i] <= transitionLength
     - extendedEnd = first index j where csDistances[j] - csDistances[rawEnd] <= transitionLength
  4. Clamp to [rangeStart, rangeEnd]
  5. Merge overlapping or adjacent ranges
  6. Return merged ranges
```

Uses absolute curvature — both left and right curves qualify. The transition extension is symmetric (same length before and after). Two nearby curves with overlapping transition zones merge into a single continuous range.

Uses cumulative `csDistances` (already computed in `GenerateForSpline`) for accurate transition extension. Sub-sampled cross-sections are not perfectly equally spaced (the last section is always included regardless of spacing), so index-based distance approximation would be inaccurate.

If no cross-sections exceed the threshold, an empty list is returned and no DecalRoad is generated for this layer on this spline.

### Randomizer Algorithm

```
Input:
  - inputRanges: List<(int Start, int End)> — eligible ranges (from curve filter or full span)
  - csDistances: IReadOnlyList<float> (cumulative distance per section, in meters)
  - minPatchLength, maxPatchLength: float (meters)
  - minGapLength, maxGapLength: float (meters)
  - seed: int (combined global + spline seed)

Output: List<(int Start, int End)> — patch ranges within input ranges

Algorithm:
  1. Clamp: effectiveMaxPatch = Max(maxPatchLength, minPatchLength),
            effectiveMaxGap = Max(maxGapLength, minGapLength)
  2. Create Random(seed)
  3. For each input range:
     a. rangeLength = csDistances[end] - csDistances[start] (actual meters)
     b. Walk from range start using cumulative distances, alternating gap → patch:
        - gap = Random.NextSingle() * (effectiveMaxGap - minGap) + minGap
        - patch = Random.NextSingle() * (effectiveMaxPatch - minPatch) + minPatch
        - Find index where csDistances[i] - csDistances[currentIndex] >= gap (gap end)
        - Find index where csDistances[j] - csDistances[gapEnd] >= patch (patch end)
        - Record patch as (gapEnd, patchEnd)
        - Advance currentIndex past the patch
        - Stop when remaining distance < minGap + minPatch
     c. Clamp final patch to range end
     d. Discard patches shorter than 2 indices
  4. Return all patches across all input ranges
```

**Starting with a gap** ensures patches don't always begin at the road/curve start. If a range is shorter than `minGap + minPatch`, no patches are produced for that range.

**Min/max clamping**: If `maxPatchLength < minPatchLength` (or same for gaps), the implementation clamps `max = Math.Max(max, min)` to prevent negative random ranges. The UI shows a visual warning but doesn't block generation.

### Seed Composition

Per-spline seed is computed as:

```csharp
int splineSeed = settings.RandomSeed ^ spline.SplineId.GetHashCode();
```

This ensures:
- Same global seed + same road = same pattern (reproducible)
- Different roads get different patterns (variety)
- Changing global seed reshuffles all roads (user control)

**Note on combined filter determinism:** When both curve filter and randomizer are active, the randomizer's `Random` instance is created once and iterates through all eligible ranges sequentially. If curve parameters change (producing different ranges), the random sequence for later ranges shifts because earlier ranges consumed different amounts of random state. This is expected behavior — changing curve parameters may also change the random patch pattern within surviving ranges.

### Composition Logic

In `DecalRoadGenerator.GenerateForSpline`, inside the per-layer generation (within each phase's range loop). The `csDistances` array is already computed earlier in the method:

```csharp
// Within Phase A or Phase B, for each layer × lane-change range [rangeStart..rangeEnd]:

// Start with current range as single eligible span
var eligibleRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };

// Apply curve filter (narrows to curve zones within this range)
if (layer.CurveOnly)
{
    eligibleRanges = DecalRoadLayerFilter.ApplyCurveFilter(
        sampledSections, csDistances, layer.CurveMinCurvature,
        layer.CurveTransitionLength, rangeStart, rangeEnd);
}

// Apply randomizer within eligible ranges (uses cumulative distances for accuracy)
if (layer.Randomize)
{
    int splineSeed = settings.RandomSeed ^ spline.SplineId.GetHashCode();
    eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
        eligibleRanges, csDistances,
        layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
        layer.RandomMinGapLength, layer.RandomMaxGapLength,
        splineSeed);
}

// Skip if no eligible ranges after filtering
if (eligibleRanges.Count == 0) continue;

// Process each eligible sub-range through the existing generation pipeline
foreach (var (subStart, subEnd) in eligibleRanges)
{
    // ... existing lateral offset, junction interruption, chunking logic
    // but operating on sections[subStart..subEnd] instead of full range
}
```

---

## UI Changes

### Layer Set Editor (`DecalRoadLayerSetEditor.razor`)

Two new collapsible sections in the expanded layer card, after the existing flag checkboxes row:

**Curve Constraint section:**
- `MudCheckBox`: "Curve Only" toggle → binds to `CurveOnly`
- When checked, reveals:
  - `MudNumericField`: "Min Curvature" (step 0.001, min 0.001) with helper text: `$"= {1.0f / value:F0}m radius"`
  - `MudNumericField`: "Transition Length" (meters, step 5, min 0)

**Randomizer section:**
- `MudCheckBox`: "Randomize" toggle → binds to `Randomize`
- When checked, reveals:
  - Two `MudNumericField` side by side: "Min Patch Length" / "Max Patch Length" (meters)
  - Two `MudNumericField` side by side: "Min Gap Length" / "Max Gap Length" (meters)

### GenerateTerrain.razor — DecalRoad Section

Add `RandomSeed` field next to existing NodeSpacing and JunctionMargin:

```
Node Spacing: [2.0] m    Junction Margin: [0.0] m    Random Seed: [42]
```

### Validation Rules

| Property | Min | Max | Step |
|----------|-----|-----|------|
| CurveMinCurvature | 0.001 | 1.0 | 0.001 |
| CurveTransitionLength | 0.0 | 200.0 | 5.0 |
| RandomMinPatchLength | 1.0 | 500.0 | 5.0 |
| RandomMaxPatchLength | 1.0 | 500.0 | 5.0 |
| RandomMinGapLength | 1.0 | 500.0 | 5.0 |
| RandomMaxGapLength | 1.0 | 500.0 | 5.0 |
| RandomSeed | int.MinValue | int.MaxValue | 1 |

Cross-validation (non-blocking, visual warning): `MaxPatchLength` should be >= `MinPatchLength`, `MaxGapLength` should be >= `MinGapLength`. If inverted, the UI shows a warning but does not block. The filter implementation clamps `max = Math.Max(max, min)` defensively to prevent negative random ranges.

---

## Preset Serialization

No explicit changes needed. The new properties on `DecalRoadLayerDefinition` and `DecalRoadSettings` are automatically included via the existing `System.Text.Json` serialization used by `TerrainPresetExporter`/`TerrainPresetImporter` and `DecalRoadDefaultsManager`.

**Backward compatibility:** Old presets without these properties deserialize with default values (`CurveOnly = false`, `Randomize = false`), keeping all layers continuous — identical to current behavior.

---

## Files Affected

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs` | Static filter class: ApplyCurveFilter + ApplyRandomizer |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs` | Unit tests for both filters and composition |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Add 8 constraint properties (CurveOnly, CurveMinCurvature, CurveTransitionLength, Randomize, RandomMinPatchLength, RandomMaxPatchLength, RandomMinGapLength, RandomMaxGapLength) |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadSettings.cs` | Add `RandomSeed` property |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Insert filter pipeline between layer expansion and node generation |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Add Curve Constraint and Randomizer sections to expanded layer card |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Add RandomSeed field to DecalRoad section |

### Unchanged Files (Already Support This)

| File | Why Unchanged |
|------|---------------|
| `DecalRoadLayerSetEditorDialog.razor` | Dialog wrapper — inner editor handles new fields |
| `DecalRoadDefaultLayerSets.cs` | Defaults keep CurveOnly/Randomize=false (current behavior) |
| `DecalRoadDefaultsManager.cs` | JSON serialization picks up new properties automatically |
| `TerrainPresetExporter/Importer` | Serialization picks up new properties automatically |
| `DecalRoadSceneWriter.cs` | Writes GeneratedDecalRoad objects — unaware of how they were filtered |
| `JunctionInterrupter.cs` | Runs after filtering — no changes needed |

---

## Testing Strategy

All tests in `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs`:

### Curve Filter Tests

- **Straight road**: All curvature below threshold → empty ranges
- **Single curve**: One section exceeds threshold → one range with transition extensions on both sides
- **Two nearby curves**: Overlapping transition zones → ranges merge into single range
- **Entire road is a curve**: All curvature above threshold → single range spanning full spline
- **Curve at start of spline**: Transition extension clamps to index 0
- **Curve at end of spline**: Transition extension clamps to last index
- **Transition length = 0**: No extension, raw curve ranges only

### Randomizer Tests

- **Range shorter than minGap + minPatch**: No patches produced
- **Deterministic with same seed**: Identical input + same seed → identical output
- **Different seeds**: Same input + different seed → different output
- **All patches within bounds**: Every patch length is in [minPatch, maxPatch] range
- **All gaps within bounds**: Every gap length is in [minGap, maxGap] range
- **Multiple input ranges**: Patches generated independently per range
- **Patches don't exceed range boundaries**: No patch extends past its input range

### Composition Tests

- **Curve + Randomizer**: Patches only appear within curve zones
- **Randomizer only (no curve filter)**: Patches span full road
- **Curve only (no randomizer)**: Continuous coverage within curve zones
- **Neither active**: Full span passed through unchanged

---

## Example Configurations

### Tire Marks in Curves Only
```json
{
  "name": "TireMarks",
  "layerType": "TreadMarks",
  "material": "m_tire_marks",
  "width": 0.3,
  "position": 0.0,
  "isMirrored": true,
  "curveOnly": true,
  "curveMinCurvature": 0.008,
  "curveTransitionLength": 20.0,
  "randomize": false
}
```
Result: Tire marks appear on both sides of roads in curves tighter than ~125m radius, with 20m lead-in/lead-out.

### Scattered Potholes
```json
{
  "name": "Potholes",
  "layerType": "Custom",
  "material": "m_pothole_patches",
  "width": 1.5,
  "position": 0.3,
  "curveOnly": false,
  "randomize": true,
  "randomMinPatchLength": 5.0,
  "randomMaxPatchLength": 20.0,
  "randomMinGapLength": 30.0,
  "randomMaxGapLength": 150.0
}
```
Result: Scattered pothole patches along the road, each 5-20m long, with 30-150m gaps between them.

### Tire Marks in Curves, Randomized
```json
{
  "name": "TireMarks",
  "layerType": "TreadMarks",
  "material": "m_tire_marks",
  "width": 0.3,
  "position": 0.0,
  "isMirrored": true,
  "curveOnly": true,
  "curveMinCurvature": 0.01,
  "curveTransitionLength": 15.0,
  "randomize": true,
  "randomMinPatchLength": 8.0,
  "randomMaxPatchLength": 30.0,
  "randomMinGapLength": 10.0,
  "randomMaxGapLength": 40.0
}
```
Result: Scattered tire mark patches that only appear within curve zones. Not every curve section gets full coverage.
