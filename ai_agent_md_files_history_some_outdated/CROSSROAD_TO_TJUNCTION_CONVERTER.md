# Crossroad to T-Junction Converter

## Overview

The `CrossroadToTJunctionConverter` handles a specific road network scenario: **mid-spline crossings** where two roads cross each other without either road having an endpoint at the crossing point.

## The Problem

When two roads cross in the middle (neither terminates at the crossing), the junction detection system creates a `MidSplineCrossing` junction type. However, this creates challenges for elevation harmonization:

1. **Both roads are "continuous"** - neither has an endpoint, so there's no clear "terminating" vs "continuous" road
2. **No surface constraints** - the existing T-junction logic (which applies edge constraints to terminating roads) doesn't apply
3. **Elevation conflicts** - if the roads are at different elevations, there's no clear strategy for which road should adapt

### Visual Example

```
Before conversion:
        |
        |  Road B (continuous through crossing)
        |
========X========  Road A (continuous through crossing)
        |
        |
        |

Both roads pass through point X without terminating.
This is a MidSplineCrossing - both are "continuous".
```

## The Solution: Virtual T-Junctions

The converter transforms a single `MidSplineCrossing` into **two T-junctions** by:

1. **Identifying the primary road** (higher priority or longer road)
2. **Splitting the secondary road** at the crossing point into two segments
3. **Creating real endpoints** where the secondary road segments meet the primary road

### After Conversion

```
After conversion:
        |
        |  Road B segment 2 (STARTS at crossing)
        |
========X========  Road A (continuous, unchanged)
        |
        |  Road B segment 1 (ENDS at crossing)
        |

Two T-junctions are created:
- T-Junction 1: Road A continuous + Road B segment 1 terminating
- T-Junction 2: Road A continuous + Road B segment 2 terminating
```

## Algorithm Details

### Step 1: Determine Primary vs Secondary Roads

The converter uses a priority cascade to determine which road is "primary" (continuous) and which is "secondary" (to be split):

| Priority | Criterion | Rationale |
|----------|-----------|-----------|
| 1 | Higher `Priority` value | OSM road classification (motorway > primary > residential) |
| 2 | Longer road length | Main roads tend to be longer |
| 3 | Lower `SplineId` | Deterministic tiebreaker for reproducibility |

### Step 2: Split the Secondary Road

The secondary road's cross-sections are split into two segments at the crossing point:

- **Segment A**: From original start to crossing point (inclusive)
- **Segment B**: From crossing point to original end (inclusive)

The crossing cross-section is **included in both segments** - it becomes the new endpoint of each.

### Step 3: Mark Endpoint Flags

The cross-sections at the split point have their endpoint flags updated:

```csharp
// Segment A: ends at crossing
segmentA.Last().IsSplineEnd = true;

// Segment B: starts at crossing  
segmentB.First().IsSplineStart = true;
```

### Step 4: Create T-Junctions

Two new `NetworkJunction` objects are created with `JunctionType.TJunction`:

**T-Junction for Segment A:**
- Primary road contributor: `IsContinuous = true` (passes through)
- Secondary road contributor: `IsSplineEnd = true` (terminates here)

**T-Junction for Segment B:**
- Primary road contributor: `IsContinuous = true` (passes through)
- Secondary road contributor: `IsSplineStart = true` (starts here)

## Benefits

### 1. Reuses Existing T-Junction Logic

All existing code for T-junction handling works automatically:
- Edge constraint calculation (`JunctionSurfaceCalculator`)
- Elevation propagation along terminating roads
- Banking adaptation at junctions

### 2. Correct Surface Matching

The secondary road segments get proper surface constraints that follow the primary road's:
- Banking (lateral tilt)
- Longitudinal slope (grade)

### 3. No Special Cases

The conversion happens **before** elevation harmonization, so the rest of the pipeline sees normal T-junctions.

## When Is This Used?

The converter is called in `NetworkJunctionHarmonizer.HarmonizeNetwork()`:

```csharp
// CRITICAL: Convert mid-spline crossings to T-junctions
// This must happen BEFORE elevation harmonization
var crossingsConverted = _crossroadConverter.ConvertCrossroadsToTJunctions(network, globalBlendDistance);
```

## Configuration

The converter uses the global blend distance parameter but doesn't require any specific configuration. It processes all `MidSplineCrossing` junctions automatically.

## Logging

The converter logs detailed information to the file log (not UI):

```
Converting 3 mid-spline crossing(s) to T-junctions by splitting secondary roads...
Junction #42: Primary road = Spline 15 (priority=80, length=450m), Secondary roads = [23]
Junction #42: Split spline 23 at index 47. Segment A: 48 CS (ends at crossing), Segment B: 35 CS (starts at crossing)
Converted 3 mid-spline crossing(s) to 6 T-junction(s)
```

## Edge Cases

### Equal Priority Roads

When both roads have equal priority and similar lengths:
- Uses length as the first tiebreaker (longer road wins)
- Uses `SplineId` as the final tiebreaker for determinism
- Logs tiebreaker decisions for debugging

### Very Short Segments

If splitting would create segments with fewer than 2 cross-sections:
- The conversion is skipped for that crossing
- A warning is logged
- The original `MidSplineCrossing` junction is preserved

### Multiple Secondary Roads

If a crossing involves more than 2 roads (complex intersection):
- Each secondary road is split independently
- Multiple T-junctions are created
- The primary road remains continuous through all

## Related Files

- `NetworkJunctionDetector.cs` - Detects `MidSplineCrossing` junctions
- `NetworkJunctionHarmonizer.cs` - Calls the converter and processes resulting T-junctions
- `JunctionSurfaceCalculator.cs` - Calculates edge constraints for T-junction terminating roads
- `UnifiedCrossSection.cs` - Contains `IsSplineStart`/`IsSplineEnd` flags

## See Also

- [Junction Surface Constraint Implementation Plan](JUNCTION_SURFACE_CONSTRAINT_IMPLEMENTATION_PLAN.md)
- [Road Elevation Smoothing Documentation](../ROAD_ELEVATION_SMOOTHING_DOCUMENTATION.md)
