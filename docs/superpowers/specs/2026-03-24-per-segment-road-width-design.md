# Per-Segment Road Width from OSM Lane Data

**Date:** 2026-03-24
**Status:** Approved

## Problem

Road width parameters (`RoadWidthMeters`, `RoadSurfaceWidthMeters`, `MasterSplineWidthMeters`) are currently global per-spline, set once on `RoadSmoothingParameters`. Real-world roads change width when lane counts change (e.g., 2-lane residential widening to 4-lane at an intersection approach). OSM lane data already flows through the pipeline via `LaneSegment` but is only used for DecalRoad layer expansion (Phase B in `DecalRoadGenerator`), not for width variation.

## Goal

Derive road width dynamically from OSM lane counts and layerset-defined lane width, so that all pipeline consumers (elevation smoothing, material painting, master spline export, DecalRoad generation) use per-segment widths that reflect actual lane count changes.

## Width Derivation

### Road Surface Width Priority Chain (inspired by OSM2World)

For a given spline segment, the road surface width is resolved using this priority chain:

| Priority | Source | Condition | Surface Width |
|----------|--------|-----------|---------------|
| 1 (highest) | OSM `width=*` tag | Explicit width tag on the way | Parsed value directly (meters) |
| 2 | OSM `est_width=*` tag | Estimated width tag on the way | Parsed value directly (meters) |
| 3 | Lane count calculation | `LaneSegments` exist with `TotalLanes > 0` | `laneCount * DefaultLaneWidth` (from layerset) |
| 4 | LayerSet defaults | Layerset resolved, no OSM lane/width data | `DefaultLaneCount * DefaultLaneWidth` |
| 5 (lowest) | `RoadSmoothingParameters` | No layerset resolved | `EffectiveRoadSurfaceWidthMeters` (existing behavior) |

### Derived Widths

Once `RoadSurfaceWidth` is resolved from the chain above:

- **SmoothingCorridorWidth** = `RoadSurfaceWidth + 2 * SmoothingCorridorMargin` (from layerset)
- **MasterSplineWidth** = `RoadSurfaceWidth + 2 * MasterSplineMargin` (from layerset)

### OSM Width Tag Parsing

Add `WidthMeters` and `EstWidthMeters` fields to `OsmLaneInfo`:

```csharp
public float? WidthMeters { get; set; }      // parsed from width=*
public float? EstWidthMeters { get; set; }    // parsed from est_width=*
```

**Unit parsing** in `OsmLaneInfo.TryParse()` — parse the value string with unit support:

| Format | Example | Interpretation |
|--------|---------|----------------|
| Bare number | `7.5` | Meters (assumed) |
| Meters | `7.5 m` | 7.5 meters |
| Kilometers | `0.008 km` | 8.0 meters |
| Feet | `25'` or `25 ft` | 7.62 meters |
| Feet+inches | `25'6"` or `25'6''` | 7.77 meters |
| Miles | `0.005 mi` | 8.05 meters |

Add a static helper `TryParseWidth(string value, out float meters)` to `OsmLaneInfo` that handles these formats. Invalid or unparseable values return `false` and the tag is ignored (fall through to next priority).

**Parsing location:** In `OsmLaneInfo.TryParse()`, after lane tag parsing. Note: `TryParse()` currently returns `null` when no lane tags exist. With width tag support, it should return a non-null `OsmLaneInfo` even when only `width=*` or `est_width=*` is present (with `TotalLanes = 0` indicating no lane data). This allows width information to flow through even for ways without lane tags.

**`Reversed()` method:** Must carry the new `WidthMeters` and `EstWidthMeters` fields through (width is direction-independent, so values are copied as-is).

### Road-Type Width Estimates

When neither OSM width tags nor lane tags are available, the layerset's `DefaultLaneCount * DefaultLaneWidth` provides the fallback. The hardcoded defaults in `DecalRoadDefaultLayerSets` serve as road-type-based estimates:

| Road Type | DefaultLaneCount | DefaultLaneWidth | Effective Width |
|-----------|-----------------|------------------|-----------------|
| Motorway | 4 | 3.5m | 14.0m |
| Trunk | 4 | 3.5m | 14.0m |
| Primary | 2 | 3.5m | 7.0m |
| Secondary | 2 | 3.5m | 7.0m |
| Tertiary | 2 | 3.0m | 6.0m |
| Residential | 2 | 3.0m | 6.0m |
| Service | 2 | 2.75m | 5.5m |
| Track | 1 | 2.5m | 2.5m |
| Roundabout | 1 | 3.5m | 3.5m |

These defaults can be customized per road type through the layerset editor.

## Approach: Pre-computed Width Segments

Width is resolved once during network construction and stored as a pre-computed profile on the spline. Consumers query width at any distance via a lookup method. This decouples width resolution from width consumption.

## New Data Models

### WidthSegment

New class in `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/`:

```csharp
public class WidthSegment
{
    public float StartDistance { get; set; }           // meters along spline
    public float RoadSurfaceWidth { get; set; }        // resolved from priority chain
    public float SmoothingCorridorWidth { get; set; }  // surfaceWidth + 2 * margin
    public float MasterSplineWidth { get; set; }       // surfaceWidth + 2 * masterMargin
    public int LaneCount { get; set; }                 // for reference/debugging
    public WidthSource Source { get; set; }            // for debugging: how width was resolved
}

public enum WidthSource
{
    OsmWidthTag,        // width=* or est_width=*
    LaneCalculation,    // laneCount * laneWidth
    LayerSetDefault,    // DefaultLaneCount * DefaultLaneWidth
    ParameterFallback   // RoadSmoothingParameters
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
    // For closed-loop splines (roundabouts): clamp distance to [0, TotalLength]
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
2. If no layerset found: leave `WidthProfile = null` → Priority 5 fallback
3. If layerset found, resolve surface width per segment using the priority chain:
   - **Check Priority 1-2 (OSM width tags):** If any `LaneSegment` has `LaneInfo.WidthMeters` or `LaneInfo.EstWidthMeters` set, use that as the surface width for the segment. Note: when a way has a `width=*` tag, all lane segments from that way share the same width value (width is per-way, not per-segment in OSM). Source: `OsmWidthTag`.
   - **Check Priority 3 (lane calculation):** If no width tag but `LaneInfo.TotalLanes > 0`, use `Math.Max(laneInfo.TotalLanes, 1) * layerSet.DefaultLaneWidth`. Source: `LaneCalculation`.
   - **Check Priority 4 (layerset defaults):** If `LaneSegments` is null/empty or segment has neither width tags nor lane data, use `layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth`. Source: `LayerSetDefault`.
4. For each segment: compute `SmoothingCorridorWidth = surfaceWidth + 2 * SmoothingCorridorMargin` and `MasterSplineWidth = surfaceWidth + 2 * MasterSplineMargin`
5. Attach `RoadWidthProfile` to spline

**Mixed segments:** A single spline may have segments with different width sources (e.g., some segments from merged ways have `width=*` tags, others only have lane counts). Each segment resolves independently through the priority chain.

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
| **UnifiedCrossSection.FromSplineSample** | `Terrain/Models/RoadGeometry/UnifiedCrossSection.cs` | `ownerSpline.Parameters.RoadWidthMeters` | **Key bottleneck** — query `WidthProfile.GetWidthsAtDistance(sample.Distance).corridor` instead of global parameter. `EffectiveBlendRange` stays global (reads `TerrainAffectedRangeMeters`). |
| MedialAxisRoadExtractor | `Terrain/Algorithms/MedialAxisRoadExtractor.cs` | `parameters.RoadWidthMeters` | Query `WidthProfile` at each sample distance for corridor width. `CrossSection.WidthMeters` becomes per-point. |
| DistanceFieldTerrainBlender | `Terrain/Algorithms/DistanceFieldTerrainBlender.cs` | `parameters.RoadWidthMeters` | Use per-cross-section width from extractor (already varied). |
| SinglePassBlender | `Terrain/Algorithms/Blending/SinglePassBlender.cs` | `s.Parameters.RoadWidthMeters / 2.0f` | Use per-cross-section width from extractor. |
| PostProcessingSmoother | `Terrain/Algorithms/Blending/PostProcessingSmoother.cs` | `s.Parameters.RoadWidthMeters` | Use per-cross-section width for smoothing mask. |
| UnifiedJunctionProfileBlender | `Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` | `contributor.Spline.Parameters.RoadWidthMeters` (~8 call sites) | Query `WidthProfile` at junction endpoint distance for blend/taper distance calculations. |
| BankingOrchestrator | `Terrain/Services/BankingOrchestrator.cs` | `spline.Parameters.RoadWidthMeters / 2.0f` | Query `WidthProfile` at each cross-section for edge elevation calculation. See Known Limitations. |
| PriorityAwareJunctionBankingCalculator | `Terrain/Algorithms/Banking/PriorityAwareJunctionBankingCalculator.cs` | `s.Parameters.RoadWidthMeters` | Query `WidthProfile` at transition distance calculation. |
| UnifiedRoadSmoother | `Terrain/Services/UnifiedRoadSmoother.cs` | `Parameters.RoadWidthMeters` (multiple sites) | Query `WidthProfile` at relevant distances. |
| MaterialPainter | `Terrain/Services/MaterialPainter.cs` | `EffectiveRoadSurfaceWidthMeters` | Query `WidthProfile` at each paint sample for surface width. |
| MasterSplineExporter | `Terrain/Services/MasterSplineExporter.cs` | `EffectiveMasterSplineWidthMeters` | Query `WidthProfile` at each exported node for master spline width. Per-node width in JSON. Note: legacy single-material export paths (lines ~426, ~530) read from `RoadSmoothingParameters` directly — these need adaptation to accept width profile or pass through per-node widths. |
| DecalRoadGenerator | `Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | `EffectiveMasterSplineWidthMeters` | Query `WidthProfile` at sample points. Aligns with existing Phase B lane-segment splitting. |
| RoadCorridorBuilder | `Terrain/Services/DecalRoad/RoadCorridorBuilder.cs` | `EffectiveMasterSplineWidthMeters` | Per-sample query. |
| RoundaboutElevationHarmonizer | `Terrain/Algorithms/RoundaboutElevationHarmonizer.cs` | `parameters.RoadWidthMeters` | Query at junction point distance. Roundabouts typically have uniform lanes and may lack `LaneSegments` (created synthetically by `RoundaboutMerger`), so they fall through to Priority 2 (layerset defaults). |
| RoadDebugExporter | `Terrain/Services/RoadDebugExporter.cs` | `parameters.RoadWidthMeters` (multiple sites) | Update for accurate debug outlines. Lower priority. |
| DecalRoadNetworkSnapshotBuilder | `Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs` | `spline.Parameters.RoadWidthMeters` | See Binary Snapshot section below. |
| TerrainCreator | `Terrain/TerrainCreator.cs` | `material.RoadParameters.EffectiveRoadSurfaceWidthMeters` | Logging only — update for accurate width reporting. |

### CrossSection Model

`UnifiedCrossSection.EffectiveRoadWidth` becomes set per-cross-section during extraction via `FromSplineSample()`, reflecting the width at that specific distance along the spline. `EffectiveBlendRange` continues to use the global `TerrainAffectedRangeMeters` (not varied per segment).

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

Updated defaults to provide road-type-appropriate widths as fallback when no OSM data is available:

| Road Type | DefaultLaneCount | DefaultLaneWidth | SmoothingCorridorMargin | MasterSplineMargin |
|-----------|-----------------|------------------|------------------------|--------------------|
| Motorway | 4 | 3.5m | 2.0m | 0.0m |
| Trunk | 4 | 3.5m | 2.0m | 0.0m |
| Primary | 2 | 3.5m | 2.0m | 0.0m |
| Secondary | 2 | 3.5m | 2.0m | 0.0m |
| Tertiary | 2 | 3.0m | 2.0m | 0.0m |
| Residential | 2 | 3.0m | 2.0m | 0.0m |
| Service | 2 | 2.75m | 1.5m | 0.0m |
| Unclassified | 2 | 3.0m | 2.0m | 0.0m |
| Track | 1 | 2.5m | 1.0m | 0.0m |
| Roundabout | 1 | 3.5m | 2.0m | 0.0m |

## Binary Snapshot Format

`DecalRoadNetworkSnapshot` (versioned binary format, currently `FormatVersion = 2`) serializes width parameters per-spline and `LaneSegmentSnapshot` objects. Two options:

**Chosen approach:** Reconstruct the `RoadWidthProfile` on snapshot load from the already-serialized `LaneSegmentSnapshot` data plus layerset resolution. This avoids a format version bump and keeps the snapshot lean. The margin values come from layerset resolution at load time (same as during initial construction).

If layerset resolution context is not available during snapshot load, fall back to serialized `RoadWidthMeters`/`RoadSurfaceWidthMeters`/`MasterSplineWidthMeters` values (existing snapshot fields).

## Known Limitations

- **Banking discontinuity at width-change boundaries:** `BankingOrchestrator` computes edge elevations as `center +/- (halfWidth * sin(bankAngle))`. If width changes abruptly at a segment boundary (no transition or short transition), left and right edge elevations will have discontinuities even if center elevation is smooth. This produces visible terrain steps at road edges. Mitigation: the transition zone interpolation smooths this, but if transitions are deferred to a follow-up, banking artifacts at width-change points are expected.
- **Junction taper distance:** `UnifiedJunctionProfileBlender` uses `RoadWidthMeters` to compute taper distances. If a lane-count change happens near a junction, the taper uses the width at the junction endpoint, which may not perfectly match the visual road width at every point in the taper zone.
- **`SmoothingCorridorMargin` vs `RoadEdgeProtectionBufferMeters`:** These serve related but distinct purposes. `SmoothingCorridorMargin` defines the elevation smoothing corridor around the painted road surface. `RoadEdgeProtectionBufferMeters` (on `RoadSmoothingParameters`) prevents adjacent lower-priority roads from modifying the edge of a higher-priority road. Both remain independently configurable; users should be aware they interact when roads are close together.

## Non-Goals

- Varying `TerrainAffectedRangeMeters` or blending parameters per segment (stays global)
- Per-lane width tags (e.g., individual lane widths summed) — we use total road width, not per-lane widths
- Sidewalk/cycleway/kerb width parsing — only vehicle road surface width
- Per-layer width overrides based on lane count (layers already have `IsTrackWidth`/`IsLaneWidth` mechanisms)
