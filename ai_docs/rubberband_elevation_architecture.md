# Rubberband Elevation Architecture

## Problem Statement

The current elevation pipeline has a fundamental architectural conflict between Phase 2 (terrain-following smoothing + endpoint anchoring) and Phase 3 (junction harmonization). Phase 2 creates terrain-following profiles with anchoring that pulls toward terrain at junction centers. Phase 3 blends nearby cross-sections toward computed junction elevations. The interaction creates:

- **Ditch artifact**: Phase 2 anchoring creates a depression near junctions. Phase 3's weighted blend inherits part of this depression (it blends with the "original" depressed value), creating a valley 5-15m from the junction center.
- **Bump artifact**: When anchoring is removed, Phase 2 produces terrain-following values that may be very different from the junction target. Phase 3 creates a steep ramp to bridge the gap, producing a visible wall/cliff when blend distances are short.
- **Spike artifact**: Monotone enforcement with incorrect direction detection can clamp entire blend zones to wrong elevations.

Multiple incremental fix attempts (skip anchoring, smooth falloff, monotone enforcement) made the artifacts progressively worse because they address symptoms rather than the root architectural conflict.

## Root Cause: Per-Junction vs Per-Road

The current Phase 3 propagation works **per-junction**: for each junction, it walks outward along each contributing road, blending toward the junction's HarmonizedElevation. When a road connects two junctions, it receives independent influences from both ends that don't coordinate with each other. In the gap between blend zones, the terrain-following profile creates discontinuities.

The rubberband approach works **per-road**: each road's elevation profile is a single coordinated interpolation between its endpoint junctions, modulated by terrain in the middle. There is no gap, no independent competing influences, and no Phase 2/3 conflict.

## Important: Rubberband is a Blend Envelope, Not a Straight Line

The rubberband does NOT create a straight line between junctions. Roads still follow terrain shape — valleys, hills, slopes — everywhere. The rubberband is a **blend envelope** that only affects the near-junction zone (within `JunctionBlendDistanceMeters`, typically 15-40m). It ensures smooth connection to the junction elevation, then fades to let the terrain-following profile take over.

Even for short roads where blend zones overlap: the junction elevations themselves are computed from the roads' own terrain-following profiles (the continuous road's Phase 2 elevation at the junction point). So interpolating between two junction elevations still tracks the terrain slope — it's not floating in the air.

## Architecture: Junction-First Elevation

### Pipeline Overview

```
Phase 1:   Build network, detect junctions (topology only, no elevation)
Phase 1.5: Identify roundabouts
Phase 1.8: Early junction detection

  ┌─── ITERATION LOOP (up to 3, expect 1-2 with rubberband) ───┐
  │                                                              │
  │ Phase 2:   Terrain sampling → smoothing → slope constraints  │
  │            NO endpoint anchoring for multi-road junctions    │
  │                                                              │
  │ Phase 2.3: Structure profiles (bridges/tunnels)              │
  │ Phase 2.5: Banking pre-calculation                           │
  │ Phase 2.6: Roundabout harmonization                          │
  │                                                              │
  │ Phase 3:   Compute junction HarmonizedElevation              │
  │            *** RUBBERBAND PROFILES (replaces propagation) ***│
  │            MidSplineCrossing influences (kept as-is)         │
  │            Edge constraint propagation (kept as-is)          │
  │            Endpoint tapering, plateau polygons, IDW weights  │
  │                                                              │
  │ Convergence check: exit if correction < 0.01m               │
  └──────────────────────────────────────────────────────────────┘

Phase 3.5: Banking finalization
Phase 4:   Terrain blending (single-pass EDT)
Phase 5:   Material painting
```

### Rubberband Formula

For each cross-section on a spline with endpoint junction(s):

```
// Compute junction influence weights from each end
if (has start junction AND distFromStart < startBlendDist):
    startWeight = 1.0 - blendFunction(distFromStart / startBlendDist)
else:
    startWeight = 0.0

if (has end junction AND distFromEnd < endBlendDist):
    endWeight = 1.0 - blendFunction(distFromEnd / endBlendDist)
else:
    endWeight = 0.0

totalJunctionWeight = startWeight + endWeight

if (totalJunctionWeight > 1.0):
    // OVERLAP ZONE: both blend zones active (short road)
    // Pure rubberband interpolation between junction elevations
    t = endWeight / totalJunctionWeight
    junctionElev = startJunctionElev * (1 - t) + endJunctionElev * t
    newElevation = junctionElev  // 100% junction-driven

else if (totalJunctionWeight > 0.001):
    // TRANSITION ZONE: junction influence fading into terrain
    junctionElev = (startJunctionElev * startWeight + endJunctionElev * endWeight)
                   / totalJunctionWeight
    newElevation = junctionElev * totalJunctionWeight
                 + terrainFollowingElev * (1.0 - totalJunctionWeight)

else:
    // OUTSIDE ALL BLEND ZONES: terrain-following unchanged
    newElevation = terrainFollowingElev
```

### Key Properties

| Scenario | Behavior |
|----------|----------|
| At junction center (dist=0) | 100% junction elevation, no ditch possible |
| Short road (blend zones overlap everywhere) | Pure interpolation between junctions, no terrain influence |
| Long road, near junction | Junction elevation dominates, smooth transition |
| Long road, middle | Terrain-following (unchanged from Phase 2) |
| One junction (dead-end at other end) | Junction fades to terrain toward free end |
| No junctions (isolated road) | Pure terrain-following, unchanged |

### Why This Eliminates All Three Artifacts

1. **No ditch**: Near the junction, `totalJunctionWeight >= 1.0`, so `originalElev` is never blended in. There is no anchoring depression to inherit.
2. **No bump**: The rubberband transitions smoothly from junction elevation to terrain. For short roads where blend zones overlap, the profile is a pure interpolation between junctions with no terrain influence at all — no steep ramp.
3. **No spikes**: No monotone enforcement needed. The rubberband is inherently monotone between junctions (it's an interpolation).

## Implementation Details (Actual Code)

### File 1: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

**Method**: `BuildEndpointAnchorLookup` (line ~815)

Added junction type filter after the `IsExcluded` check:
```csharp
if (junction.IsExcluded) continue;

// Only anchor isolated endpoints (dead-end roads) toward terrain.
// Multi-road junctions are handled by the rubberband blend envelope in Phase 3,
// which smoothly interpolates between junction elevations and terrain-following.
// Anchoring at multi-road junctions was the root cause of the "ditch" artifact.
if (junction.Type != JunctionType.Endpoint) continue;
```

Only isolated dead-end roads (`JunctionType.Endpoint`) get terrain anchoring. All multi-road junctions (T, Y, X, Complex, Roundabout) are handled by the rubberband.

### File 2: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Rewrote `PropagateJunctionConstraints`** (line ~835) as a thin orchestrator calling three methods:

```csharp
private int PropagateJunctionConstraints(
    UnifiedRoadNetwork network, List<NetworkJunction> junctions, float globalBlendDistance)
{
    var crossSectionsBySpline = /* group and order by LocalIndex */;
    var originalElevations = /* capture terrain-following elevations before modification */;

    // 1. Rubberband blend envelope (replaces old Pass 1 + Pass 2)
    var modifiedCount = ApplyRubberbandProfiles(
        network, junctions, crossSectionsBySpline, originalElevations, globalBlendDistance);

    // 2. MidSplineCrossings (kept as-is, orthogonal to rubberband)
    modifiedCount += ApplyMidSplineCrossingInfluences(
        network, junctions, crossSectionsBySpline, originalElevations, globalBlendDistance);

    // 3. Edge constraints for T-junction banking (kept as-is)
    var edgeCount = PropagateEdgeConstraintsForTJunctions(...);

    return modifiedCount;
}
```

#### New method: `ApplyRubberbandProfiles`

**Step 1 — Build spline-to-junction lookup**:

Maps `(splineId, isStart)` → `(junction, blendDist)` for all endpoint contributors. For T-junctions only terminating roads are mapped (continuous roads pass through unaffected). For Y/X/Complex, all endpoint contributors are mapped. MidSplineCrossings and isolated Endpoints are skipped.

```csharp
var splineEndJunctions = new Dictionary<(int splineId, bool isStart),
    (NetworkJunction junction, float blendDist)>();

foreach (var junction in sortedJunctions.Where(j =>
             j.Type != JunctionType.Endpoint &&
             j.Type != JunctionType.MidSplineCrossing && !j.IsExcluded))
{
    var contributors = junction.Type == JunctionType.TJunction
        ? junction.GetTerminatingRoads()
        : junction.Contributors.Where(c => c.IsEndpoint);

    foreach (var contributor in contributors)
    {
        var blendDist = CalculateAdaptiveBlendDistance(
            configuredBlendDist, junction.HarmonizedElevation,
            contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);
        splineEndJunctions.TryAdd((contributor.Spline.SplineId, contributor.IsSplineStart),
            (junction, blendDist));
    }
}
```

**Step 2 — Per-spline rubberband loop**:

For each spline with at least one endpoint junction, computes distances from both ends, then applies the rubberband formula. Key code:

```csharp
foreach (var (splineId, splineSections) in crossSectionsBySpline)
{
    var hasStart = splineEndJunctions.TryGetValue((splineId, true), out var startInfo);
    var hasEnd = splineEndJunctions.TryGetValue((splineId, false), out var endInfo);
    if (!hasStart && !hasEnd) continue;

    var distFromStart = CalculateDistancesFromEndpoint(splineSections, fromStart: true);
    var distFromEnd = CalculateDistancesFromEndpoint(splineSections, fromStart: false);

    for (var i = 0; i < splineSections.Count; i++)
    {
        // ... skip roundabout-blended, get terrainFollowingElev from originalElevations

        // Compute weights
        float startWeight = 0f, endWeight = 0f;
        if (hasStart && distFromStart[i] < startBlendDist)
            startWeight = 1.0f - ApplyBlendFunction(distFromStart[i] / startBlendDist, blendFunctionType);
        if (hasEnd && distFromEnd[i] < endBlendDist)
            endWeight = 1.0f - ApplyBlendFunction(distFromEnd[i] / endBlendDist, blendFunctionType);

        var totalWeight = startWeight + endWeight;
        if (totalWeight < 0.001f) continue;

        float newElev;
        if (totalWeight > 1.0f)
        {
            // OVERLAP: short road — pure interpolation between junction elevations
            var interpT = endWeight / totalWeight;
            newElev = startElev * (1.0f - interpT) + endElev * interpT;
        }
        else
        {
            // TRANSITION: blend junction elevation with terrain-following
            float junctionElev = /* weighted avg of startElev/endElev */;
            newElev = junctionElev * totalWeight + terrainFollowingElev * (1.0f - totalWeight);
        }
        cs.TargetElevation = newElev;
    }
}
```

#### New method: `ApplyMidSplineCrossingInfluences`

Extracted from the old Pass 1 + Pass 2 logic, but only processes `JunctionType.MidSplineCrossing` junctions. Uses the existing `CollectBidirectionalInfluences` method + weighted average application. These crossings are orthogonal to the rubberband (neither road terminates at the crossing).

### How Phase 2 Smoothing and Rubberband Don't Fight

Phase 2 and the rubberband run in sequence, not in parallel:

1. **Phase 2** produces terrain-following profiles. No anchoring at multi-road junctions.
2. **Phase 3 rubberband** captures Phase 2's output as `originalElevations` (read-only reference), then overrides near-junction cross-sections by blending toward junction elevation.

On **re-smooth iterations** (iteration 2+): Phase 2 re-smooths from the rubberband-corrected profile, then the rubberband re-applies. The gap shrinks each iteration because Phase 2's re-smoothed profile is already close to the junction elevation. Convergence is expected in 1-2 iterations.

## Parameter Impact

### Parameters That Become Unnecessary for Multi-Road Junctions

| Parameter/Mechanism | Status |
|---|---|
| `EndpointAnchor` exponential decay (Phase 2 anchoring) | Only used for dead-end endpoints |
| `DecayDistanceMeters` | Only for dead-end endpoints |
| Iterative loop (3 iterations) | Expect 1-2 iterations (faster convergence) |

### Parameters That Remain Important

| Parameter | Role in Rubberband |
|---|---|
| `JunctionBlendDistanceMeters` (per-material) | Controls how far rubberband extends from each junction |
| `BlendFunctionType` (Cosine/Cubic/Quintic) | Shape of the rubberband transition curve |
| `CalculateAdaptiveBlendDistance` | Extends blend for large elevation gaps |
| `globalJunctionBlendDistanceMeters` | Fallback when per-material not set |

### Future Simplification Opportunities

The `AutoCalculateBlendDistance` with `BlendDistanceMultiplier`, `BlendDistanceOffset`, `Min/MaxAutoBlendDistance` are tuning knobs that were needed to compensate for the Phase 2/3 conflict. With rubberband, a simple `JunctionBlendDistanceMeters` per material may suffice. The auto-calculation can be simplified or removed in a follow-up cleanup.

## Future Enhancements

1. **CubicHermiteC1 rubberband**: Match road slopes at junction and blend boundary using Hermite interpolation instead of flat junction elevation. Gives smoother curvature in the transition zone.

2. **Adaptive cross-section resolution**: The `CrossSectionIntervalMeters` and `SmoothingWindowSize` parameters currently give fixed values. Near junctions where higher resolution matters, these could adaptively decrease (parameters become maximum values). Not needed for initial rubberband — 0.5m default interval gives ~30 samples per 15m blend zone.

3. **Multi-junction spline segments**: For roads with mid-spline crossings AND endpoint junctions, the rubberband could be extended to treat mid-spline crossings as additional anchor points, creating a piecewise rubberband.
