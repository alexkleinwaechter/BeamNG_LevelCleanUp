# Curve Constraint Enhancement: Replace in Curve

**Date:** 2026-03-19
**Status:** Approved
**Relates to:** DecalRoad layer generation pipeline

---

## Problem

The current `CurveOnly` boolean on `DecalRoadLayerDefinition` supports only one curve behavior: show a layer exclusively in curves. Real-world road markings need a second mode: replacing the main material with a different material in curves. The primary use case is overtaking prohibition — a dashed center line becomes a solid line through curves.

## Solution

Replace `bool CurveOnly` with an enum `CurveConstraintMode { None, CurveOnly, ReplaceInCurve }`. In `ReplaceInCurve` mode, a single layer definition produces two sets of DecalRoad segments: straights use the main material/width, curves use a replacement material/width. Hard cuts at curve boundaries (no fade/overlap between main and replacement).

---

## Model Changes

### New Enum

```csharp
// BeamNgTerrainPoc/Terrain/Models/DecalRoad/CurveConstraintMode.cs
public enum CurveConstraintMode
{
    /// No curve constraint — layer generated everywhere.
    None,

    /// Layer generated only in curve sections (existing behavior).
    CurveOnly,

    /// Main material on straights, replacement material in curves.
    ReplaceInCurve
}
```

### DecalRoadLayerDefinition Changes

Remove:
```csharp
public bool CurveOnly { get; set; }
```

Add:
```csharp
/// Curve constraint mode. None = no constraint, CurveOnly = curves only,
/// ReplaceInCurve = swap material+width in curves.
public CurveConstraintMode CurveConstraint { get; set; } = CurveConstraintMode.None;

/// Material to use in curve sections when CurveConstraint == ReplaceInCurve.
public string CurveReplacementMaterial { get; set; } = string.Empty;

/// Width to use in curve sections when CurveConstraint == ReplaceInCurve.
/// 0 = use same width as main layer.
public float CurveReplacementWidth { get; set; }

/// Texture length for replacement material when CurveConstraint == ReplaceInCurve.
/// 0 = use same texture length as main layer.
public float CurveReplacementTextureLength { get; set; }
```

Existing properties unchanged:
- `CurveMinCurvature` (float, default 0.01)
- `CurveTransitionLength` (float, default 15.0)

---

## Backend Changes

### DecalRoadGenerator — ComputeFilteredRanges

Current behavior for `CurveOnly`:
1. Compute curve ranges via `DecalRoadLayerFilter.ApplyCurveFilter`
2. Return curve ranges → generate with main material

New behavior for `ReplaceInCurve`:
1. Compute curve ranges via `ApplyCurveFilter` (same filter, same parameters)
2. Invert curve ranges to get straight ranges within the full spline range
3. Return two sets of generation segments:
   - Straight ranges → main material + main width
   - Curve ranges → replacement material + replacement width (or main width if replacement width is 0)

### Generation Segment

```csharp
private record struct GenerationSegment(
    int Start,
    int End,
    string Material,
    float Width,
    float TextureLength
);
```

`ComputeFilteredRanges` return type changes from `List<(int Start, int End)>` to `List<GenerationSegment>`, sorted by `Start`. This is a single combined list containing both curve and straight segments.

For `None` and `CurveOnly` modes: returns segments using main material/width/textureLength (identical to current behavior, wrapped in the struct).

For `ReplaceInCurve`: returns interleaved straight + curve segments, each tagged with the appropriate material/width/textureLength. Replacement values of 0 fall back to the main layer's values.

**Validation:** If `CurveConstraint == ReplaceInCurve` and `CurveReplacementMaterial` is empty, fall back to the main material (effectively degrades to `None` mode). Log a warning during generation.

### Integration with GenerateForLayerRange

The callers of `ComputeFilteredRanges` in `GenerateForSpline` (both Phase A and Phase B) iterate over the returned `GenerationSegment` list and pass the segment's `Material`, `Width`, and `TextureLength` to `GenerateForLayerRange`. This requires adding optional override parameters to `GenerateForLayerRange` (or the caller creates a temporary layer clone with swapped values). The override approach is preferred to avoid allocating clones.

### Interaction with Randomizer

When `CurveConstraint == ReplaceInCurve` and `Randomize == true`:
- The randomizer applies **only to straight segments** (main material).
- Curve segments are never randomized — the replacement material always appears continuously through the full curve zone.
- This matches the primary use case: a solid no-overtaking line must be continuous through curves, while dashed lines on straights can have randomized gaps.

### Range Inversion

**Location:** New static method `InvertRanges` in `DecalRoadLayerFilter` (alongside `ApplyCurveFilter` and `ApplyRandomizer`).

Given curve ranges `[(10,20), (40,60)]` within full range `(0, 100)`:
- Straight ranges: `[(0,10), (20,40), (60,100)]`

Edge cases:
- No curves detected → entire range is straight (main material everywhere)
- Entire road is curve → no straight segments (replacement material everywhere)
- Adjacent curve ranges (after merging) → no gap between them
- Zero-length segments are excluded

### DecalRoadLayerFilter

Add `InvertRanges` static method. `ApplyCurveFilter` and `ApplyRandomizer` are unchanged.

### Transition Behavior

Hard cuts at curve boundaries. No fade or overlap between main and replacement material segments. The `CurveTransitionLength` continues to control where the curve zone starts/ends (lead-in before the geometric curve), which provides the real-world realism of changing markings before entering the curve.

---

## Default Layer Sets

Update `DecalRoadDefaultLayerSets.cs`: replace all `CurveOnly = true` with `CurveConstraint = CurveConstraintMode.CurveOnly`. No default layers use `ReplaceInCurve` — users configure that per their needs.

Affected layers in `CreateAsphaltRoadSet`:
- HeavyTreadMarks
- Wear2
- Skidmarks

---

## UI Changes

### Collapsed Header Chip

In `DecalRoadLayerSetEditor.razor`, the layer card collapsed row:
- `CurveConstraintMode.None` → no chip (unchanged)
- `CurveConstraintMode.CurveOnly` → chip text: `curve`
- `CurveConstraintMode.ReplaceInCurve` → chip text: `crv-repl`

### Expanded Generation Constraints Section

Restructured from a single `CurveOnly` checkbox to a hierarchical layout (Layout B from brainstorming):

1. **Parent checkbox: "Curve Constraints"**
   - Checked = `CurveConstraint != None`
   - Unchecked = sets `CurveConstraint = None`

2. **When checked, reveal:**

   **Radio: "Curve Only"**
   - Description: "Layer appears only in curves, hidden on straight sections"
   - Sets `CurveConstraint = CurveConstraintMode.CurveOnly`

   **Radio: "Replace in Curve"**
   - Description: (none needed — fields are self-explanatory)
   - Sets `CurveConstraint = CurveConstraintMode.ReplaceInCurve`
   - Nested fields (shown only when this radio is selected):
     - `CurveReplacementMaterial` — text field, label "Replacement Material"
     - `CurveReplacementWidth` — numeric field, label "Replacement Width (m)", helper text "0 = same as main"
     - `CurveReplacementTextureLength` — numeric field, label "Replacement Tex Length (m)", helper text "0 = same as main"

   **Shared fields at bottom** (always visible when constraints enabled):
   - `CurveMinCurvature` — numeric field with radius helper text (unchanged)
   - `CurveTransitionLength` — numeric field (unchanged)

### Code-Behind Updates

- `DeepCopyLayer`: copy `CurveConstraint`, `CurveReplacementMaterial`, `CurveReplacementWidth`, `CurveReplacementTextureLength`
- Chip display helper: return mode-specific chip text
- Remove all `CurveOnly` boolean references

---

## Test Changes

### Existing Tests

`DecalRoadLayerFilterTests.cs`: Update `CurveOnly = true` references to `CurveConstraint = CurveConstraintMode.CurveOnly`. Filter behavior unchanged.

### New Tests

- **ReplaceInCurve generation**: Verify segments are tagged with correct material/width/textureLength
  - Curve segments get replacement material + width
  - Straight segments get main material + width
  - Replacement width 0 → uses main width
  - Replacement texture length 0 → uses main texture length
  - Empty replacement material → falls back to main material (degrades to None)
- **Randomizer interaction**: Randomizer applies only to straight segments, curve segments are continuous
- **Range inversion** (`DecalRoadLayerFilter.InvertRanges`): Given curve ranges and full range, verify correct straight ranges
  - No curves → full range returned as straight
  - Entire road is curve → empty straight ranges
  - Multiple curves with gaps → correct interleaving
  - Adjacent curves (no gap) → no zero-length straight segments

---

## Files Changed

| File | Change |
|------|--------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/CurveConstraintMode.cs` | **New** — enum |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/DecalRoadLayerDefinition.cs` | Replace `CurveOnly` bool with enum + replacement properties |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | `ComputeFilteredRanges` returns `List<GenerationSegment>`, callers in `GenerateForSpline` pass per-segment material/width/textureLength |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadLayerFilter.cs` | Add `InvertRanges` static method |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadDefaultLayerSets.cs` | `CurveOnly = true` → `CurveConstraint = CurveConstraintMode.CurveOnly` |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Restructure curve constraints UI |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Deep copy, chip logic updates |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadLayerFilterTests.cs` | Update `CurveOnly` refs to enum |
| Any other files referencing `CurveOnly` | Mechanical rename |

---

## What's NOT in Scope

- No JSON migration/backwards compatibility — project is in active development
- No fade/overlap between main and replacement segments — hard cuts only
- No per-curve-segment property overrides beyond material + width + texture length
- No material browser/picker — text field for material name (consistent with existing UI)
