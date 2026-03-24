# Per-Segment Road Width from OSM Lane Data

**Date:** 2026-03-24
**Status:** Approved

## Problem

Road width parameters (`RoadWidthMeters`, `RoadSurfaceWidthMeters`, `MasterSplineWidthMeters`) are currently global per-spline, set once on `RoadSmoothingParameters`. Real-world roads change width when lane counts change (e.g., 2-lane residential widening to 4-lane at an intersection approach). OSM lane data already flows through the pipeline via `LaneSegment` but is only used for DecalRoad layer expansion (Phase B in `DecalRoadGenerator`), not for width variation.

## Goal

Derive road width dynamically from OSM lane counts and layerset-defined lane width, so that all pipeline consumers (elevation smoothing, material painting, master spline export, DecalRoad generation) use per-segment widths that reflect actual lane count changes.

## Width Derivation Formula

For a given spline segment with `laneCount` lanes:

- **RoadSurfaceWidth** = `laneCount * DefaultLaneWidth` (from layerset)
- **SmoothingCorridorWidth** = `RoadSurfaceWidth + 2 * SmoothingCorridorMargin` (from layerset)
- **MasterSplineWidth** = `RoadSurfaceWidth + 2 * MasterSplineMargin` (from layerset)

## Priority Chain (Fallback Order)

| Priority | Source | When Used |
|----------|--------|-----------|
| 1 (highest) | Per-segment calculated | OSM `LaneSegments` exist on spline AND layerset resolved |
| 2 | LayerSet defaults | No OSM lane data, but layerset resolved (`DefaultLaneCount * DefaultLaneWidth` + margins) |
| 3 (lowest) | `RoadSmoothingParameters` | No layerset resolved (`EffectiveRoadSurfaceWidthMeters`, `RoadWidthMeters`, `EffectiveMasterSplineWidthMeters`) |

## Approach: Pre-computed Width Segments

Width is resolved once during network construction and stored as a pre-computed profile on the spline. Consumers query width at any distance via a lookup method. This decouples width resolution from width consumption.

## New Data Models

### WidthSegment

New class in `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/`:

```csharp
public class WidthSegment
{
    public float StartDistance { get; set; }           // meters along spline
    public float RoadSurfaceWidth { get; set; }        // laneCount * laneWidth
    public float SmoothingCorridorWidth { get; set; }  // surfaceWidth + 2 * margin
    public float MasterSplineWidth { get; set; }       // surfaceWidth + 2 * masterMargin
    public int LaneCount { get; set; }                 // for reference/debugging
}
```

### RoadWidthProfile

New class in `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/`:

```csharp
public class RoadWidthProfile
{
    public List<WidthSegment> Segments { get; }
    public float TransitionLengthMeters { get; set; } = 15f;

    public (float surface, float corridor, float masterSpline) GetWidthsAtDistance(float distance)
    // Binary search on Segments by StartDistance
    // Linear interpolation within TransitionLengthMeters of segment boundaries
    // Transition centered on boundary (half before, half after)
    // No transition at spline start/end
}
```

### ParameterizedRoadSpline addition

```csharp
public RoadWidthProfile? WidthProfile { get; set; }
```

When `WidthProfile` is null, all consumers fall back to existing `RoadSmoothingParameters` width fields (backward-compatible).

### DecalRoadLayerSet additions

Two new fields on the existing model:

```csharp
public float SmoothingCorridorMargin { get; set; } = 2.0f;  // per-side, meters
public float MasterSplineMargin { get; set; } = 0.0f;       // per-side, meters
```

## Width Profile Construction

### Location

In `UnifiedRoadNetworkBuilder`, after `ParameterizedRoadSpline` creation and lane segment attachment.

### Algorithm

For each `ParameterizedRoadSpline`:

1. Resolve layerset via `DecalRoadLayerSetResolver.Resolve(osmRoadType, materialName, settings, appDataDefaults)`
2. If layerset found:
   - If `spline.LaneSegments` is non-empty: create one `WidthSegment` per `LaneSegment` using `laneInfo.TotalLanes * layerSet.DefaultLaneWidth`
   - If no lane segments: create single `WidthSegment` at distance 0 using `layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth`
   - For each segment: apply margin formulas
   - Attach `RoadWidthProfile` to spline
3. If no layerset found: leave `WidthProfile = null`

### Transition Zones

When distance falls within `TransitionLengthMeters / 2` of a segment boundary, `GetWidthsAtDistance()` linearly interpolates between the two adjacent segments' width values. This produces smooth width changes when lane counts change mid-spline.

**Future enhancement:** If transition logic proves complex to integrate with all consumers (particularly elevation smoothing mask building), it can be deferred to a follow-up. The initial implementation can use immediate step changes at segment boundaries, which is still a major improvement over global width.

## Consumer Updates

All consumers adopt the same pattern:

```csharp
// Before:
var surfaceWidth = spline.Parameters.EffectiveRoadSurfaceWidthMeters;

// After:
var surfaceWidth = spline.WidthProfile?.GetWidthsAtDistance(distance).surface
    ?? spline.Parameters.EffectiveRoadSurfaceWidthMeters;
```

### Affected Files

| Consumer | File | Current Width | Change |
|----------|------|---------------|--------|
| MedialAxisRoadExtractor | `Terrain/Algorithms/MedialAxisRoadExtractor.cs` | `parameters.RoadWidthMeters` | Query `WidthProfile` at each sample distance for corridor width. `CrossSection.WidthMeters` becomes per-point. |
| DistanceFieldTerrainBlender | `Terrain/Algorithms/DistanceFieldTerrainBlender.cs` | `parameters.RoadWidthMeters` | Use per-cross-section width from extractor (already varied). |
| MaterialPainter | `Terrain/Services/MaterialPainter.cs` | `EffectiveRoadSurfaceWidthMeters` | Query `WidthProfile` at each paint sample for surface width. |
| MasterSplineExporter | `Terrain/Services/MasterSplineExporter.cs` | `EffectiveMasterSplineWidthMeters` | Query `WidthProfile` at each exported node for master spline width. Per-node width in JSON. |
| DecalRoadGenerator | `Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | `EffectiveMasterSplineWidthMeters` | Query `WidthProfile` at sample points. Aligns with existing Phase B lane-segment splitting. |
| RoadCorridorBuilder | `Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | `EffectiveMasterSplineWidthMeters` | Per-sample query. |
| RoundaboutElevationHarmonizer | `Terrain/Algorithms/RoundaboutElevationHarmonizer.cs` | `parameters.RoadWidthMeters` | Query at junction point distance. Roundabouts typically have uniform lanes. |

### CrossSection Model

`UnifiedCrossSection.EffectiveRoadWidth` becomes set per-cross-section during extraction, reflecting the width at that specific distance along the spline.

## UI Changes

### DecalRoadLayerSetEditor

Add two fields in the header row, next to existing `DefaultLaneCount` and `DefaultLaneWidth`:

```
[ DefaultLaneCount: 2 ] [ DefaultLaneWidth: 3.5m ] [ Smoothing Margin: 2.0m ] [ Spline Margin: 0.0m ]
```

- **Smoothing Corridor Margin** — `MudNumericField<float>`, suffix "m", tooltip: "Added to each side of the road surface for terrain smoothing"
- **Master Spline Margin** — `MudNumericField<float>`, suffix "m", tooltip: "Added to/subtracted from road surface width for master spline export"

### No changes needed to

- **DecalRoadLayerSetEditorDialog** — passes `DecalRoadLayerSet` through, new fields included automatically
- **Per-material settings UI** — `RoadSmoothingParameters` width fields remain as fallback
- **Persistence** — JSON serialization handles new fields automatically. Existing saved layersets deserialize with defaults (2.0m and 0.0m)

### Default values in DecalRoadDefaultLayerSets

| Road Type | SmoothingCorridorMargin | MasterSplineMargin |
|-----------|------------------------|--------------------|
| All asphalt types (motorway through service) | 2.0m | 0.0m |
| Track | 1.0m | 0.0m |
| Roundabout | 2.0m | 0.0m |

## Non-Goals

- Varying `TerrainAffectedRangeMeters` or blending parameters per segment (stays global)
- OSM `width=*` tag parsing (width derived from lane count only)
- Per-layer width overrides based on lane count (layers already have `IsTrackWidth`/`IsLaneWidth` mechanisms)
