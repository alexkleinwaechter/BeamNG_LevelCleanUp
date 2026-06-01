# Junction Blending Regression Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix abrupt elevation steps at junctions caused by `disableSplineMerging=true` by (C) eliminating false junctions at degree-2 OSM way boundaries, and (A) upgrading multi-way junction blending with dominant-road detection and edge-anchored constraints.

**Architecture:** Phase C adds a `Continuation` junction type that the blender skips. Phase A splits `ComputeMultiWayConstraints` into two sub-paths: multi-T-junction (dominant road detected, reuses T-junction edge-anchored logic) and true peer junction (improved average-based blending with flat zones). Short-spline overlap protection prevents fighting Hermite corrections.

**Tech Stack:** C# / .NET 9, `System.Numerics`, existing `UnifiedJunctionProfileBlender` framework

**Spec:** `ai_docs/2026-03-26-junction-blending-regression-fix.md`

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs` | Modify | Add `Continuation` to `JunctionType` enum |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs` | Modify | Detect degree-2 continuations in `ClassifyJunctions()` |
| `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` | Modify | Skip `Continuation`, rewrite `ComputeMultiWayConstraints()`, add overlap protection |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs` | Modify | Render `Continuation` junctions in debug image |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs` | Read-only reference | `FindBestContinuation()` heuristics reused in detector |
| `BeamNgTerrainPoc/Terrain/Algorithms/JunctionSurfaceCalculator.cs` | Read-only reference | `GetPrimarySurfaceElevation()` reused in multi-T path |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` | Read-only reference | Constraint record structure |

---

## Phase C: Eliminate False Junctions at Degree-2 Continuations

### Task C.1: Add `Continuation` to JunctionType Enum

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs:8-50`

- [ ] **Step 1: Add Continuation enum value**

In `NetworkJunction.cs`, add the new enum value after `Roundabout` (line 49):

```csharp
public enum JunctionType
{
    Endpoint,
    TJunction,
    YJunction,
    CrossRoads,
    Complex,
    MidSplineCrossing,
    Roundabout,

    /// <summary>
    ///     Two splines share an OSM node but it's just a way boundary, not a real intersection.
    ///     Deflection angle is small (< 30°) and width ratio is within 2:1.
    ///     No constraint is computed — elevation is handled by chain-based smoothing.
    /// </summary>
    Continuation
}
```

- [ ] **Step 2: Build to verify no compilation errors**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds. The new enum value is additive — existing switch statements will fall through to default/`_` arms.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs
git commit -m "feat: add Continuation junction type for degree-2 OSM way boundaries"
```

---

### Task C.2: Detect Degree-2 Continuations in ClassifyJunctions

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs:512-540`
- Reference: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs:381-420` (heuristics to reuse)

The existing `ClassifyJunctions()` classifies junctions AFTER endpoint clustering. A degree-2 endpoint cluster (2 splines, both endpoints, no continuous contributor) currently becomes `YJunction`. We add a check: if the two splines form a near-straight continuation with similar width, reclassify as `Continuation`.

- [ ] **Step 1: Add continuation detection to ClassifyJunctions**

Replace the `ClassifyJunctions` method at line 512 with:

```csharp
private void ClassifyJunctions(List<NetworkJunction> junctions, UnifiedRoadNetwork network)
{
    foreach (var junction in junctions)
    {
        // Skip junctions that already have a specific type assigned (e.g., MidSplineCrossing, Roundabout)
        if (junction.Type == JunctionType.MidSplineCrossing || junction.Type == JunctionType.Roundabout)
            continue;

        var uniqueSplineIds = junction.Contributors
            .Select(c => c.Spline.SplineId)
            .Distinct()
            .Count();

        if (uniqueSplineIds == 1 && junction.Contributors.Count == 1)
        {
            // Single endpoint, no connection to other roads
            junction.Type = JunctionType.Endpoint;
        }
        else if (junction.Contributors.Any(c => c.IsContinuous))
        {
            // At least one contributor passes through (not an endpoint) = T-junction
            junction.Type = JunctionType.TJunction;
        }
        else if (uniqueSplineIds == 2 && IsDegree2Continuation(junction))
        {
            // Two splines meet at near-straight angle with similar width = OSM way boundary
            junction.Type = JunctionType.Continuation;
        }
        else
        {
            // All contributors are endpoints
            junction.Type = uniqueSplineIds switch
            {
                2 => JunctionType.YJunction,
                3 or 4 => JunctionType.CrossRoads,
                _ => JunctionType.Complex
            };
        }
    }
}
```

- [ ] **Step 2: Add the IsDegree2Continuation helper method**

Add this method after `ClassifyJunctions` (after line 540):

```csharp
/// <summary>
///     Checks if a degree-2 junction is a simple continuation (OSM way boundary)
///     rather than a real Y-junction. Uses the same heuristics as
///     NetworkElevationGraph.FindBestContinuation: deflection angle < 30°
///     and width ratio within 2:1.
/// </summary>
private static bool IsDegree2Continuation(NetworkJunction junction)
{
    var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
    if (endpoints.Count != 2) return false;

    var a = endpoints[0];
    var b = endpoints[1];

    // Width ratio check (same as NetworkElevationGraph.IsCompatibleForChaining)
    var widthA = a.Spline.WidthProfile
            ?.GetWidthsAtDistance(a.CrossSection.DistanceAlongSpline).corridor
        ?? a.Spline.Parameters.RoadWidthMeters;
    var widthB = b.Spline.WidthProfile
            ?.GetWidthsAtDistance(b.CrossSection.DistanceAlongSpline).corridor
        ?? b.Spline.Parameters.RoadWidthMeters;

    if (widthA > 0 && widthB > 0)
    {
        var ratio = widthA > widthB ? widthA / widthB : widthB / widthA;
        if (ratio > 2.0f) return false;
    }

    // Deflection angle check: the two splines should point in roughly the same direction.
    // Get tangent directions pointing AWAY from the junction for each spline.
    var tangentA = a.IsSplineStart
        ? -a.CrossSection.TangentDirection   // start endpoint: tangent points into spline, negate for "away"
        : a.CrossSection.TangentDirection;    // end endpoint: tangent points away from spline
    var tangentB = b.IsSplineStart
        ? -b.CrossSection.TangentDirection
        : b.CrossSection.TangentDirection;

    // For a continuation, the two "away" tangents should point in OPPOSITE directions
    // (one road goes left, the other goes right from the junction).
    // So we check the angle between tangentA and -tangentB (should be < 30°).
    var dot = Vector2.Dot(tangentA, -tangentB);
    dot = Math.Clamp(dot, -1f, 1f);
    var deflectionDegrees = MathF.Acos(dot) * 180f / MathF.PI;

    return deflectionDegrees < 30f;
}
```

- [ ] **Step 3: Add the `using System.Numerics;` import if not already present**

Check the top of `NetworkJunctionDetector.cs` — it already has `using System.Numerics;` at line 1. No change needed.

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs
git commit -m "feat: detect degree-2 continuations as Continuation junction type

Degree-2 endpoint clusters where both splines have deflection < 30°
and width ratio < 2:1 are now classified as Continuation instead of
YJunction. This prevents false junction constraints at OSM way boundaries
when disableSplineMerging is enabled."
```

---

### Task C.3: Skip Continuation Junctions in the Blender

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:208-234`

- [ ] **Step 1: Add Continuation to the skip guard and switch statement**

At line 208, the junction loop filters with `!j.IsExcluded || j.Type == JunctionType.Roundabout`. The `Continuation` type will naturally reach the switch statement. Add a case for it.

Replace the switch block at lines 210-234:

```csharp
switch (junction.Type)
{
    case JunctionType.TJunction:
        ComputeTJunctionConstraints(junction, crossSectionsBySpline, constraints);
        break;

    case JunctionType.Roundabout:
        ComputeRoundaboutConstraints(junction, crossSectionsBySpline, constraints);
        break;

    case JunctionType.YJunction:
    case JunctionType.CrossRoads:
    case JunctionType.Complex:
        ComputeMultiWayConstraints(junction, constraints);
        break;

    case JunctionType.Endpoint:
        ComputeEndpointConstraints(junction, heightMap, metersPerPixel,
            mapWidth, mapHeight, constraints);
        break;

    case JunctionType.Continuation:
        // No constraint — elevation handled by chain-based smoothing (Phase 2).
        // These are OSM way boundaries, not real junctions.
        break;

    case JunctionType.MidSplineCrossing:
        // Handled separately in ApplyMidSplineCrossingInfluences
        break;
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: skip Continuation junctions in profile blender

Continuation junctions (degree-2 OSM way boundaries) get no elevation
constraint. Their elevation is already handled by network-chained
smoothing in Phase 2."
```

---

### Task C.4: Render Continuation Junctions in Debug Image

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs:855-871`

- [ ] **Step 1: Add Continuation color and radius to debug image rendering**

At line 855, add `Continuation` to the radius switch:

```csharp
var radius = junction.Type switch
{
    JunctionType.Complex => 6,
    JunctionType.CrossRoads => 5,
    JunctionType.Roundabout => 7,
    JunctionType.Continuation => 3,
    _ => 4
};
```

At line 863, add `Continuation` to the color switch:

```csharp
var junctionColor = junction.Type switch
{
    JunctionType.TJunction => new Rgba32(255, 165, 0, 200),
    JunctionType.CrossRoads => new Rgba32(255, 0, 0, 200),
    JunctionType.Complex => new Rgba32(255, 0, 255, 200),
    JunctionType.Roundabout => new Rgba32(0, 255, 255, 200),
    JunctionType.MidSplineCrossing => new Rgba32(255, 255, 0, 200),
    JunctionType.Continuation => new Rgba32(100, 100, 100, 150),
    _ => new Rgba32(0, 255, 0, 200)
};
```

- [ ] **Step 2: Update the junction breakdown log**

In `NetworkJunctionDetector.cs` at line 92-101, the junction breakdown log already uses `GetValueOrDefault` for each type. Add `Continuation`:

Replace lines 94-101:

```csharp
TerrainCreationLogger.Current?.InfoFileOnly($"Junction breakdown: " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.TJunction)} T, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.YJunction)} Y, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.CrossRoads)} X, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.Complex)} Complex, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.Endpoint)} Isolated, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.MidSplineCrossing)} MidCrossing, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.Roundabout)} Roundabout, " +
                                            $"{junctionsByType.GetValueOrDefault(JunctionType.Continuation)} Continuation");
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs
git commit -m "feat: render Continuation junctions as gray dots in debug image

Continuation junctions appear as small gray dots (radius 3) in the
junction debug image. Also adds Continuation count to junction breakdown log."
```

---

### Task C.5: Verify Phase C — Build and Manual Test

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeds with no errors. Warnings about unused variables are acceptable.

- [ ] **Step 2: Manual verification checklist**

Generate terrain with `disableSplineMerging=true` and `ExportJunctionDebugImage=true`. Check:
1. Debug image shows gray dots at OSM way boundaries where Y-junctions used to be
2. Junction breakdown log shows `N Continuation` count (should be significant with merging disabled)
3. No elevation changes at former degree-2 Y-junction nodes
4. Degree-3+ junctions (CrossRoads, etc.) are UNCHANGED — still red/orange in debug image
5. T-junctions and Roundabout junctions work exactly as before

---

## Phase A: Fix Multi-Way Blending with Dominant Road Detection

### Task A.1: Add Dominant Road Detection to ComputeMultiWayConstraints

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:565-611`

This is the core change. We split the existing `ComputeMultiWayConstraints` into two sub-paths:
1. **Multi-T-junction** (dominant road detected) — reuses T-junction edge-anchored logic
2. **True peer junction** (no dominant) — improved average-based blending with flat zones

- [ ] **Step 1: Replace ComputeMultiWayConstraints with dominant road detection**

Replace the entire method at lines 565-611 with:

```csharp
/// <summary>
///     Computes constraints for multi-way junctions (Y, X, Complex).
///     First attempts to detect a dominant road (significantly wider or higher priority).
///     If found: treats as multi-T-junction (dominant passes through, others snap to it).
///     If not: computes peer-to-peer average with flat zone and analytical deltas.
/// </summary>
private void ComputeMultiWayConstraints(
    NetworkJunction junction,
    Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
{
    var endpointContributors = junction.Contributors.Where(c => c.IsEndpoint).ToList();
    if (endpointContributors.Count == 0) return;

    // Try to detect a dominant road
    var dominant = DetectDominantRoad(endpointContributors);

    if (dominant != null)
    {
        ComputeMultiTJunctionConstraints(junction, dominant, endpointContributors, constraints);
    }
    else
    {
        ComputePeerJunctionConstraints(junction, endpointContributors, constraints);
    }
}

/// <summary>
///     Detects a dominant road at a multi-way junction.
///     A road is dominant if its width × priority score is >= 1.5× the average of the others,
///     OR it has strictly higher priority than all other contributors.
/// </summary>
private static JunctionContributor? DetectDominantRoad(List<JunctionContributor> endpointContributors)
{
    if (endpointContributors.Count < 2) return null;

    // Sort by (priority descending, width descending)
    var sorted = endpointContributors
        .OrderByDescending(c => c.Spline.Priority)
        .ThenByDescending(c => c.Spline.WidthProfile
            ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
            ?? c.Spline.Parameters.RoadWidthMeters)
        .ToList();

    var candidate = sorted[0];
    var candidateWidth = candidate.Spline.WidthProfile
            ?.GetWidthsAtDistance(candidate.CrossSection.DistanceAlongSpline).corridor
        ?? candidate.Spline.Parameters.RoadWidthMeters;

    // Check 1: Strictly higher priority than all others
    var candidatePriority = candidate.Spline.Priority;
    if (sorted.Skip(1).All(c => c.Spline.Priority < candidatePriority))
        return candidate;

    // Check 2: Width >= 1.5× average of others
    var otherWidths = sorted.Skip(1).Select(c =>
        c.Spline.WidthProfile
            ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
        ?? c.Spline.Parameters.RoadWidthMeters).ToList();

    if (otherWidths.Count > 0 && otherWidths.Average() > 0)
    {
        var avgOtherWidth = otherWidths.Average();
        if (candidateWidth >= avgOtherWidth * 1.5f)
            return candidate;
    }

    return null;
}
```

- [ ] **Step 2: Build to verify — expect compilation error (missing methods)**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Fails with `ComputeMultiTJunctionConstraints` and `ComputePeerJunctionConstraints` not found. This is expected — we implement them in the next steps.

- [ ] **Step 3: Commit (partial — will complete in next tasks)**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "refactor: split ComputeMultiWayConstraints with dominant road detection

WIP: ComputeMultiTJunctionConstraints and ComputePeerJunctionConstraints
not yet implemented."
```

---

### Task A.2: Implement Multi-T-Junction Constraints (Dominant Road Path)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` (add method after `ComputeMultiWayConstraints`)

When a dominant road is detected, all other roads are treated as terminators that snap to the dominant road's surface — identical to `ComputeTJunctionConstraints` but the "continuous" road is detected by width/priority rather than by `IsContinuous`.

- [ ] **Step 1: Add ComputeMultiTJunctionConstraints method**

Add after the `DetectDominantRoad` method:

```csharp
/// <summary>
///     Computes constraints for a multi-way junction with a detected dominant road.
///     The dominant road gets NO constraint (passes through like a T-junction primary).
///     All other roads get edge-anchored constraints snapping to the dominant road's surface.
///     Uses the same calculation pattern as ComputeTJunctionConstraints.
/// </summary>
private void ComputeMultiTJunctionConstraints(
    NetworkJunction junction,
    JunctionContributor dominant,
    List<JunctionContributor> allEndpoints,
    Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
{
    var dominantCS = dominant.CrossSection;
    var dominantHalfWidth = dominantCS.EffectiveRoadWidth / 2f;

    // Calculate dominant road's local slope (same pattern as ComputeTJunctionConstraints)
    var dominantSlope = 0f;
    // Use a few cross-sections around the endpoint for slope calculation
    var dominantSections = _currentCrossSectionsBySpline?.GetValueOrDefault(dominant.Spline.SplineId);
    if (dominantSections != null)
    {
        var idx = dominantSections.FindIndex(cs => cs.Index == dominantCS.Index);
        if (idx >= 0)
            dominantSlope = CalculateSlopeAtIndex(dominantSections, idx);
    }
    if (float.IsNaN(dominantSlope)) dominantSlope = 0f;

    junction.HarmonizedElevation = dominantCS.TargetElevation;

    TerrainCreationLogger.Current?.Detail(
        $"Multi-T Junction #{junction.JunctionId}: dominant=Spline {dominant.Spline.SplineId} " +
        $"(width={dominantCS.EffectiveRoadWidth:F1}m, priority={dominant.Spline.Priority}), " +
        $"{allEndpoints.Count - 1} terminator(s)");

    foreach (var terminating in allEndpoints)
    {
        // Skip the dominant road — it gets no constraint
        if (terminating.Spline.SplineId == dominant.Spline.SplineId
            && terminating.IsSplineStart == dominant.IsSplineStart)
            continue;

        var terminatingCS = terminating.CrossSection;
        var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;

        // Edge-anchored constraint: compute exit point and surface elevation
        // (same logic as ComputeTJunctionConstraints lines 292-337)
        var awayDirection = terminating.IsSplineStart
            ? terminatingCS.TangentDirection
            : -terminatingCS.TangentDirection;
        var edgeCenterPoint = terminatingCS.CenterPoint + awayDirection * dominantHalfWidth;

        var edgeCenterElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeCenterPoint, dominantCS, dominantSlope);

        // Bank angle from edge projections
        var edgeLeftPos = edgeCenterPoint - terminatingCS.NormalDirection * halfWidth;
        var edgeRightPos = edgeCenterPoint + terminatingCS.NormalDirection * halfWidth;
        var edgeLeftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeLeftPos, dominantCS, dominantSlope);
        var edgeRightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeRightPos, dominantCS, dominantSlope);
        var edgeDelta = (edgeRightElev - edgeLeftElev) / 2f;
        var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
        var edgeBankAngle = MathF.Asin(sinBank);

        var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();
        var terminatingWidth = terminating.Spline.WidthProfile
                ?.GetWidthsAtDistance(terminating.CrossSection.DistanceAlongSpline).corridor
            ?? terminating.Spline.Parameters.RoadWidthMeters;
        var blendDist = CalculateAdaptiveBlendDistance(
            junctionParams.GetEffectiveBlendDistance(terminatingWidth),
            edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters);

        var key = (terminating.Spline.SplineId, terminating.IsSplineStart);
        constraints.TryAdd(key, new JunctionEndpointConstraint
        {
            Elevation = edgeCenterElev,
            Slope = dominantSlope,
            BankAngleRadians = edgeBankAngle,
            IsSplineStart = terminating.IsSplineStart,
            Junction = junction,
            FlatZoneDistance = dominantHalfWidth,
            BlendDistanceMeters = blendDist,
            PrimaryTangentDirection = dominantCS.TangentDirection,
            PrimaryBankAngleRadians = 0f
        });

        TerrainCreationLogger.Current?.Detail(
            $"  Multi-T terminator Spline {terminating.Spline.SplineId}: " +
            $"edgeElev={edgeCenterElev:F2}m, slope={dominantSlope:F4}, " +
            $"flatZone={dominantHalfWidth:F2}m, blendDist={blendDist:F1}m");
    }
}
```

- [ ] **Step 2: Store crossSectionsBySpline reference for slope calculation**

The method needs access to `crossSectionsBySpline` which is a local variable in `ComputeAllJunctionConstraints`. Add a field to the class to store it temporarily.

At the top of the class (around line 21), add a field:

```csharp
private Dictionary<int, List<UnifiedCrossSection>>? _currentCrossSectionsBySpline;
```

In `ComputeAllJunctionConstraints` (line 198), store the reference at the beginning of the method (after line 204):

```csharp
_currentCrossSectionsBySpline = crossSectionsBySpline;
```

And clear it at the end (before the return at line 240):

```csharp
_currentCrossSectionsBySpline = null;
```

Also update `ComputeMultiTJunctionConstraints` — the code above already uses `_currentCrossSectionsBySpline`.

- [ ] **Step 3: Build to verify — expect error for missing ComputePeerJunctionConstraints only**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Fails only on `ComputePeerJunctionConstraints` not found.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: implement multi-T-junction constraints for dominant road path

When a dominant road is detected at a multi-way junction (wider or
higher priority), it passes through unmodified. Other roads get
edge-anchored constraints matching the dominant road's surface,
identical to T-junction treatment."
```

---

### Task A.3: Implement Peer Junction Constraints (No Dominant Road)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` (add method after `ComputeMultiTJunctionConstraints`)

For junctions where all roads are equal peers (e.g., three residential streets meeting), compute a shared average elevation with proper flat zone and `PrimaryTangentDirection` for analytical delta mode.

- [ ] **Step 1: Add ComputePeerJunctionConstraints method**

```csharp
/// <summary>
///     Computes constraints for multi-way junctions where no dominant road exists.
///     All roads are equal peers: they all blend toward a shared average elevation.
///     Improved over the original ComputeMultiWayConstraints by adding:
///     - FlatZone based on max half-width of all contributors
///     - PrimaryTangentDirection from weighted average slope for analytical delta mode
///     - Slope from priority-weighted average of contributor slopes
/// </summary>
private void ComputePeerJunctionConstraints(
    NetworkJunction junction,
    List<JunctionContributor> endpointContributors,
    Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
{
    // Compute harmonized elevation using priority-weighted average
    var totalPriority = 0f;
    var weightedElevation = 0f;
    var weightedSlope = 0f;
    var weightedTangentX = 0f;
    var weightedTangentY = 0f;
    var maxHalfWidth = 0f;

    foreach (var c in endpointContributors)
    {
        if (float.IsNaN(c.CrossSection.TargetElevation))
            continue;

        float priority = c.Spline.Priority;
        totalPriority += priority;
        weightedElevation += c.CrossSection.TargetElevation * priority;

        // Calculate contributor slope
        var slope = 0f;
        var sections = _currentCrossSectionsBySpline?.GetValueOrDefault(c.Spline.SplineId);
        if (sections != null)
        {
            var idx = sections.FindIndex(cs => cs.Index == c.CrossSection.Index);
            if (idx >= 0)
                slope = CalculateSlopeAtIndex(sections, idx);
        }
        if (float.IsNaN(slope)) slope = 0f;
        weightedSlope += slope * priority;

        // Accumulate tangent direction (pointing away from junction)
        var tangent = c.IsSplineStart
            ? -c.CrossSection.TangentDirection
            : c.CrossSection.TangentDirection;
        weightedTangentX += tangent.X * priority;
        weightedTangentY += tangent.Y * priority;

        // Track maximum half-width for flat zone
        var width = c.Spline.WidthProfile
                ?.GetWidthsAtDistance(c.CrossSection.DistanceAlongSpline).corridor
            ?? c.Spline.Parameters.RoadWidthMeters;
        maxHalfWidth = MathF.Max(maxHalfWidth, width / 2f);
    }

    var harmonizedElev = totalPriority > 0
        ? weightedElevation / totalPriority
        : endpointContributors.FirstOrDefault()?.CrossSection.TargetElevation ?? 0f;

    var harmonizedSlope = totalPriority > 0 ? weightedSlope / totalPriority : 0f;

    // Average tangent direction (may be zero if roads cancel out — that's fine)
    var avgTangent = new Vector2(weightedTangentX, weightedTangentY);
    Vector2? primaryTangent = null;
    if (avgTangent.LengthSquared() > 0.0001f)
        primaryTangent = Vector2.Normalize(avgTangent);

    junction.HarmonizedElevation = harmonizedElev;

    foreach (var contributor in endpointContributors)
    {
        var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();
        var contributorWidth = contributor.Spline.WidthProfile
                ?.GetWidthsAtDistance(contributor.CrossSection.DistanceAlongSpline).corridor
            ?? contributor.Spline.Parameters.RoadWidthMeters;
        var blendDist = CalculateAdaptiveBlendDistance(
            junctionParams.GetEffectiveBlendDistance(contributorWidth),
            harmonizedElev, contributor.CrossSection.TargetElevation, contributor.Spline.Parameters);

        var key = (contributor.Spline.SplineId, contributor.IsSplineStart);
        constraints.TryAdd(key, new JunctionEndpointConstraint
        {
            Elevation = harmonizedElev,
            Slope = harmonizedSlope,
            BankAngleRadians = 0f, // flatten at peer junction
            IsSplineStart = contributor.IsSplineStart,
            Junction = junction,
            FlatZoneDistance = maxHalfWidth,
            BlendDistanceMeters = blendDist,
            PrimaryTangentDirection = primaryTangent,
            PrimaryBankAngleRadians = 0f
        });
    }
}
```

- [ ] **Step 2: Build to verify full compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds — all three methods (`ComputeMultiWayConstraints`, `ComputeMultiTJunctionConstraints`, `ComputePeerJunctionConstraints`) now exist.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: implement peer junction constraints with flat zone and analytical deltas

True peer junctions (no dominant road) now get:
- FlatZoneDistance = max half-width of all contributors
- PrimaryTangentDirection from weighted average slope direction
- Harmonized slope from priority-weighted average
This enables analytical delta mode in BlendSplineProfile for
smoother elevation transitions."
```

---

### Task A.4: Add Multi-T-Junction to Two-Pass Deferral

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:60-68`

Multi-T-junction terminating roads should be deferred to pass 2, just like T-junction and Roundabout terminators. This ensures the dominant road gets its correct elevation in pass 1 before terminators snap to it.

- [ ] **Step 1: Add multi-T-junction terminators to deferral set**

At lines 60-68 in `ApplyUnifiedProfiles`, after the existing deferral logic, add detection of multi-T junctions:

```csharp
// Build set of splines that terminate at T-junctions or roundabouts (they need pass 2).
// These roads are deferred so their constraints use ACTUAL post-pass-1 primary/ring elevations.
var deferredTerminatingSplines = new HashSet<int>();
foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.TJunction && !j.IsExcluded))
foreach (var t in junction.GetTerminatingRoads())
    deferredTerminatingSplines.Add(t.Spline.SplineId);
foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.Roundabout))
foreach (var t in junction.GetTerminatingRoads())
    deferredTerminatingSplines.Add(t.Spline.SplineId);

// Multi-way junctions with a dominant road also need deferral:
// terminating roads should wait for the dominant road to get its pass-1 elevation.
foreach (var junction in network.Junctions.Where(j =>
    (j.Type == JunctionType.YJunction || j.Type == JunctionType.CrossRoads || j.Type == JunctionType.Complex)
    && !j.IsExcluded))
{
    var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
    var dominant = DetectDominantRoad(endpoints);
    if (dominant != null)
    {
        foreach (var t in endpoints.Where(c =>
            c.Spline.SplineId != dominant.Spline.SplineId || c.IsSplineStart != dominant.IsSplineStart))
        {
            deferredTerminatingSplines.Add(t.Spline.SplineId);
        }
    }
}
```

- [ ] **Step 2: Add multi-T-junction recomputation in pass 2**

At lines 93-101 in `ApplyUnifiedProfiles`, where pass 2 recomputes T-junction and Roundabout constraints, also recompute multi-T junction constraints:

Replace:
```csharp
foreach (var junction in network.Junctions.Where(j =>
                     (j.Type == JunctionType.TJunction && !j.IsExcluded) ||
                     j.Type == JunctionType.Roundabout))
{
    if (junction.Type == JunctionType.TJunction)
        ComputeTJunctionConstraints(junction, crossSectionsBySpline, refinedConstraints);
    else
        ComputeRoundaboutConstraints(junction, crossSectionsBySpline, refinedConstraints);
}
```

With:
```csharp
foreach (var junction in network.Junctions.Where(j =>
                     (j.Type == JunctionType.TJunction && !j.IsExcluded) ||
                     j.Type == JunctionType.Roundabout))
{
    if (junction.Type == JunctionType.TJunction)
        ComputeTJunctionConstraints(junction, crossSectionsBySpline, refinedConstraints);
    else
        ComputeRoundaboutConstraints(junction, crossSectionsBySpline, refinedConstraints);
}

// Also recompute multi-T junction constraints using post-pass-1 dominant road elevations
_currentCrossSectionsBySpline = crossSectionsBySpline;
foreach (var junction in network.Junctions.Where(j =>
    (j.Type == JunctionType.YJunction || j.Type == JunctionType.CrossRoads || j.Type == JunctionType.Complex)
    && !j.IsExcluded))
{
    var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
    var dominant = DetectDominantRoad(endpoints);
    if (dominant != null)
        ComputeMultiTJunctionConstraints(junction, dominant, endpoints, refinedConstraints);
}
_currentCrossSectionsBySpline = null;
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: defer multi-T-junction terminators to pass 2

Terminating roads at multi-way junctions with a dominant road are now
deferred to pass 2, just like T-junction and roundabout terminators.
This ensures the dominant road has its correct post-pass-1 elevation
before terminators snap to its surface."
```

---

### Task A.5: Add Short-Spline Overlap Protection to BlendSplineProfile

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:667-720`

When a short spline has constraints from both ends and their blend zones overlap, the two Hermite corrections fight. Cap each blend distance so they don't cover more than half the spline.

- [ ] **Step 1: Add overlap protection after blend distance calculation**

In `BlendSplineProfile`, after the blend distances are read at lines 722-723, add overlap protection:

```csharp
var startBlendDist = startConstraint?.BlendDistanceMeters ?? 30f;
var endBlendDist = endConstraint?.BlendDistanceMeters ?? 30f;

// Short-spline overlap protection: when both ends have constraints and their
// total blend zones (flat + blend) would cover > 80% of the road, reduce
// proportionally so each covers at most half the remaining road length.
if (startConstraint != null && endConstraint != null)
{
    var startTotal = startFlatZone + startBlendDist;
    var endTotal = endFlatZone + endBlendDist;
    var totalCoverage = startTotal + endTotal;

    if (totalCoverage > roadLength * 0.8f && roadLength > 0.1f)
    {
        // Scale both blend distances proportionally to fit within 80% of road length
        var maxTotal = roadLength * 0.8f;
        var availableForBlend = maxTotal - startFlatZone - endFlatZone;
        if (availableForBlend > 0)
        {
            var totalBlend = startBlendDist + endBlendDist;
            if (totalBlend > 0)
            {
                startBlendDist = availableForBlend * (startBlendDist / totalBlend);
                endBlendDist = availableForBlend * (endBlendDist / totalBlend);
            }
        }
        else
        {
            // Flat zones alone exceed 80% — minimize blend distances
            startBlendDist = MathF.Max(1f, roadLength * 0.1f);
            endBlendDist = MathF.Max(1f, roadLength * 0.1f);
        }

        TerrainCreationLogger.Current?.Detail(
            $"  [OVERLAP-PROTECT] Spline {sections[0].OwnerSplineId}: " +
            $"roadLength={roadLength:F1}m, reduced blendDists to " +
            $"start={startBlendDist:F1}m end={endBlendDist:F1}m");
    }
}
```

Insert this block right after the `startBlendDist` / `endBlendDist` assignments (after line 723) and before the transition distance calculation (line 727).

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: add short-spline overlap protection to BlendSplineProfile

When a spline has junction constraints at both ends and their blend
zones would cover > 80% of the road, both blend distances are scaled
down proportionally. This prevents fighting Hermite corrections on
short entry/exit roads between two junctions."
```

---

### Task A.6: Verify Phase A — Full Build and Manual Test

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 2: Manual verification checklist**

Generate terrain with `disableSplineMerging=true` and `ExportJunctionDebugImage=true`. Focus on:

1. **Roundabout entry/exit split/merge nodes**: No cliff/bump. The main road should continue at natural elevation, entry/exit roads blend smoothly to its surface.
2. **All CrossRoads junctions in debug image**: Should show minimal elevation change (gray or faint color instead of bright red/blue).
3. **Drive test at roundabout approach**: Car should not launch off a ramp at the split/merge point.
4. **T-junctions**: UNCHANGED behavior (they use their own code path).
5. **Roundabout ring junctions**: UNCHANGED behavior.
6. **Degree-2 continuations**: Gray dots in debug image, no elevation effect.
7. **Log output**: Look for `Multi-T Junction #N` and `OVERLAP-PROTECT` log entries confirming the new code paths are active.

- [ ] **Step 3: Final commit with any fixups**

If manual testing reveals issues, fix and commit. Otherwise, the implementation is complete.

```bash
git add -A
git commit -m "fix: address any issues found during Phase A manual testing"
```

---

## Summary

| Task | Description | Key Files |
|------|-------------|-----------|
| C.1 | Add `Continuation` enum value | `NetworkJunction.cs` |
| C.2 | Detect degree-2 continuations | `NetworkJunctionDetector.cs` |
| C.3 | Skip Continuation in blender | `UnifiedJunctionProfileBlender.cs` |
| C.4 | Debug image + log for Continuation | `NetworkJunctionHarmonizer.cs`, `NetworkJunctionDetector.cs` |
| C.5 | Verify Phase C | Manual testing |
| A.1 | Dominant road detection | `UnifiedJunctionProfileBlender.cs` |
| A.2 | Multi-T-junction constraints | `UnifiedJunctionProfileBlender.cs` |
| A.3 | Peer junction constraints | `UnifiedJunctionProfileBlender.cs` |
| A.4 | Two-pass deferral for multi-T | `UnifiedJunctionProfileBlender.cs` |
| A.5 | Short-spline overlap protection | `UnifiedJunctionProfileBlender.cs` |
| A.6 | Verify Phase A | Manual testing |
