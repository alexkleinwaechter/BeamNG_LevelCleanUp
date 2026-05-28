# Blend Propagation Through Short Segments — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a spline is too short to accommodate its junction blend distances, propagate the unspent blend distance through the junction into neighboring splines, so the elevation transition happens where there IS room — not crammed into a 15m segment.

**Architecture:** After computing all junction constraints (Pass 1), a new "propagation pass" identifies short splines where blend distance exceeds road length. For each, it finds the neighboring spline at the far junction and attaches a propagated constraint to that neighbor's endpoint. The propagated constraint carries the remaining blend distance (original minus short segment length) and the same elevation/slope target. The short segment itself becomes a pass-through at constant junction elevation. This is universal — works for all junction types.

**Tech Stack:** C# / .NET 9, `System.Numerics`, existing `UnifiedJunctionProfileBlender` framework

**Spec:** `ai_docs/2026-03-26-junction-blending-regression-fix.md` + user requirement for universal blend propagation

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` | Modify | Add propagation pass, modify constraint pipeline |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` | Modify | Add `IsPropagated` flag and `OriginalSplineId` for diagnostics |

---

## Design

### The Problem

A 15m roundabout entry road has:
- Start: roundabout constraint (wants 50m blend)
- End: CrossRoads constraint (wants 50m blend)

Even with the 40% road-length cap, each end gets only 6m of blend. The Hermite correction crammed into 6m creates a visible bump. The road is too short for ANY smooth transition.

### The Insight

The 15m entry road isn't the problem — it's trying to solve the wrong problem. The road BEYOND the entry (the 200m residential street on the other side of the CrossRoads junction) has plenty of room. The roundabout's elevation influence should propagate through the short entry into that longer road.

### The Algorithm

```
For each constraint C on spline S at junction J:
  1. Compute road length of S
  2. If road length < blend distance needed (including flat zone):
     → S is "too short to blend"
     → Find the junction at the OTHER end of S (call it J2)
     → Find the neighboring spline(s) at J2 (call them N1, N2, ...)
     → For each neighbor Ni:
        - Compute propagated constraint:
          elevation = C.Elevation (same target)
          slope = adjusted for S's length contribution
          blend distance = C.BlendDistance - S.roadLength
          flat zone = 0 (the flat zone was consumed by S)
        - Attach propagated constraint to Ni at its J2 endpoint
     → Mark S for "constant elevation" mode (no Hermite blend, just
        interpolate linearly between its two junction elevations)
```

### Key Decisions

1. **Threshold:** A spline is "too short to blend" when `roadLength < flatZone + blendDistance * 0.5`. The 0.5 factor means we propagate when less than half the blend zone fits — not just when it's completely impossible. This prevents choppy half-blends.

2. **Short spline treatment:** Instead of Hermite blending, short splines get simple linear interpolation between their two endpoint constraint elevations. This is smooth because both endpoints are constrained to match their respective junctions.

3. **Propagation limit:** Only propagate one hop. If the neighbor is ALSO too short, don't chain further — just cap. Chaining adds complexity and the cases where two consecutive short segments exist are rare.

4. **Priority:** Propagated constraints have lower priority than direct constraints. If spline N already has a constraint at its J2 endpoint (from its own junction computation), the direct constraint wins. The propagated constraint only fills in if no direct constraint exists, OR it adds to an existing constraint via weighted blending.

5. **Universal application:** This works for ALL junction types. The constraint being propagated could come from a roundabout, T-junction, CrossRoads, multi-T, or endpoint. The propagation logic doesn't care about junction type — it only cares about "is this spline too short?"

---

## Phase 1: Build Spline-Junction Lookup

### Task 1: Add IsPropagated flag to JunctionEndpointConstraint

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs`

- [ ] **Step 1: Add propagation tracking fields**

After the `BlendDistanceMeters` property (line 83), add:

```csharp
    /// <summary>
    ///     Whether this constraint was propagated from a neighboring short spline
    ///     rather than computed directly at this spline's own junction.
    ///     Propagated constraints have lower priority than direct constraints.
    /// </summary>
    public bool IsPropagated { get; init; }

    /// <summary>
    ///     SplineId of the short segment this constraint was propagated through.
    ///     Only set when IsPropagated is true. Used for diagnostics/logging.
    /// </summary>
    public int? PropagatedThroughSplineId { get; init; }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: Build succeeds. New properties are optional with defaults (false, null).

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs
git commit -m "feat: add IsPropagated and PropagatedThroughSplineId to JunctionEndpointConstraint"
```

---

### Task 2: Build spline-to-junction index and road length cache

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

We need two data structures for the propagation pass:
1. A lookup: given (splineId, isStart) → which junction is at that endpoint?
2. A cache: given splineId → what's the road length?

- [ ] **Step 1: Add helper method to build spline-endpoint-to-junction index**

Add after the `CapBlendDistanceToRoadLength` method:

```csharp
    /// <summary>
    ///     Builds a lookup from (splineId, isStart) to the junction at that endpoint.
    ///     Used by the propagation pass to find the junction at the "other end" of a short spline.
    /// </summary>
    private static Dictionary<(int splineId, bool isStart), NetworkJunction> BuildSplineEndpointJunctionIndex(
        UnifiedRoadNetwork network)
    {
        var index = new Dictionary<(int, bool), NetworkJunction>();
        foreach (var junction in network.Junctions)
        {
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.IsSplineStart)
                    index.TryAdd((contributor.Spline.SplineId, true), junction);
                else if (contributor.IsSplineEnd)
                    index.TryAdd((contributor.Spline.SplineId, false), junction);
                // IsContinuous contributors are at neither start nor end — skip
            }
        }

        return index;
    }
```

- [ ] **Step 2: Add helper method to compute road length from cross-sections**

```csharp
    /// <summary>
    ///     Computes the total road length of a spline from its cross-sections.
    /// </summary>
    private static float ComputeRoadLength(List<UnifiedCrossSection> sections)
    {
        if (sections.Count < 2) return 0f;
        var length = 0f;
        for (var i = 1; i < sections.Count; i++)
            length += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
        return length;
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: Build succeeds (methods unused yet, may get warning).

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: add spline-endpoint-junction index and road length helper"
```

---

## Phase 2: Propagation Pass

### Task 3: Implement the constraint propagation pass

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

This is the core logic. After `ComputeAllJunctionConstraints` returns constraints, we scan for short splines and propagate their constraints to neighbors.

- [ ] **Step 1: Add the PropagateConstraintsThroughShortSplines method**

Add after `BuildSplineEndpointJunctionIndex`:

```csharp
    /// <summary>
    ///     Scans all constraints for splines that are too short to accommodate their blend zones.
    ///     For each such spline, propagates the constraint through the junction at the far end
    ///     into neighboring splines where there IS room for a smooth transition.
    ///
    ///     A spline is "too short" when roadLength &lt; flatZone + blendDistance * 0.5,
    ///     meaning less than half the blend zone fits within the spline.
    ///
    ///     The short spline itself gets both constraints set to constant elevation
    ///     (linear interpolation between its two junction elevations).
    /// </summary>
    private void PropagateConstraintsThroughShortSplines(
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        UnifiedRoadNetwork network)
    {
        var splineJunctionIndex = BuildSplineEndpointJunctionIndex(network);
        var propagated = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>();
        var shortSplineIds = new HashSet<int>();

        // Scan all constraints to find short splines
        foreach (var ((splineId, isStart), constraint) in constraints)
        {
            if (constraint.IsPropagated) continue; // Don't propagate already-propagated constraints

            var sections = _currentCrossSectionsBySpline?.GetValueOrDefault(splineId);
            if (sections == null || sections.Count < 2) continue;

            var roadLength = ComputeRoadLength(sections);
            var neededDistance = constraint.FlatZoneDistance + constraint.BlendDistanceMeters * 0.5f;

            if (roadLength >= neededDistance) continue; // Road is long enough — no propagation needed

            // This spline is too short. Find the junction at the OTHER end.
            var otherEnd = !isStart;
            if (!splineJunctionIndex.TryGetValue((splineId, otherEnd), out var farJunction))
                continue; // No junction at the other end (shouldn't happen, but be safe)

            // Find neighboring splines at the far junction (excluding this spline)
            var neighbors = farJunction.Contributors
                .Where(c => c.Spline.SplineId != splineId && c.IsEndpoint)
                .ToList();

            if (neighbors.Count == 0) continue; // Dead end at far junction — can't propagate

            // Calculate remaining blend distance after the short segment
            var remainingBlend = MathF.Max(1f, constraint.BlendDistanceMeters - roadLength);

            foreach (var neighbor in neighbors)
            {
                // Determine which end of the neighbor connects to the far junction
                var neighborIsStart = neighbor.IsSplineStart;
                var neighborKey = (neighbor.Spline.SplineId, neighborIsStart);

                // Don't overwrite direct constraints — they take priority
                if (constraints.ContainsKey(neighborKey)) continue;
                // Don't overwrite a previous propagation with higher blend distance
                if (propagated.TryGetValue(neighborKey, out var existing)
                    && existing.BlendDistanceMeters >= remainingBlend) continue;

                propagated[neighborKey] = new JunctionEndpointConstraint
                {
                    Elevation = constraint.Elevation,
                    Slope = constraint.Slope,
                    BankAngleRadians = 0f, // Flatten — we're far from the original junction
                    IsSplineStart = neighborIsStart,
                    Junction = constraint.Junction,
                    FlatZoneDistance = 0f, // Flat zone was consumed by the short segment
                    BlendDistanceMeters = remainingBlend,
                    PrimaryTangentDirection = null, // No analytical delta for propagated constraints
                    PrimaryBankAngleRadians = 0f,
                    IsPropagated = true,
                    PropagatedThroughSplineId = splineId
                };

                TerrainCreationLogger.Current?.Detail(
                    $"  [PROPAGATE] Constraint from Junction #{constraint.Junction.JunctionId} " +
                    $"propagated through short Spline {splineId} (len={roadLength:F1}m) " +
                    $"→ Spline {neighbor.Spline.SplineId} (blend={remainingBlend:F1}m)");
            }

            shortSplineIds.Add(splineId);
        }

        // Add propagated constraints to the main dictionary
        foreach (var (key, constraint) in propagated)
            constraints.TryAdd(key, constraint);

        if (shortSplineIds.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"Blend propagation: {shortSplineIds.Count} short spline(s), " +
                $"{propagated.Count} propagated constraint(s)");
    }
```

- [ ] **Step 2: Wire it into ApplyUnifiedProfiles**

In `ApplyUnifiedProfiles`, after the `ComputeAllJunctionConstraints` call (line 53) and before the deferral set construction (line 62), insert the propagation pass:

Find:
```csharp
        var constraints = ComputeAllJunctionConstraints(network, crossSectionsBySpline, heightMap, metersPerPixel);
        result.ConstraintsComputed = constraints.Count;

        if (constraints.Count == 0)
        {
            TerrainLogger.Detail("  UnifiedProfileBlender: No junction constraints to apply");
            return result;
        }

        // Build set of splines that terminate at T-junctions or roundabouts (they need pass 2).
```

Replace with:
```csharp
        var constraints = ComputeAllJunctionConstraints(network, crossSectionsBySpline, heightMap, metersPerPixel);

        // Propagation pass: find short splines and extend constraints into neighboring splines
        _currentCrossSectionsBySpline = crossSectionsBySpline;
        PropagateConstraintsThroughShortSplines(constraints, network);
        _currentCrossSectionsBySpline = null;

        result.ConstraintsComputed = constraints.Count;

        if (constraints.Count == 0)
        {
            TerrainLogger.Detail("  UnifiedProfileBlender: No junction constraints to apply");
            return result;
        }

        // Build set of splines that terminate at T-junctions or roundabouts (they need pass 2).
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: propagate junction constraints through short splines

When a spline is too short for its blend zone (roadLength < flatZone +
0.5 * blendDist), the constraint is propagated through the far junction
into neighboring splines. The remaining blend distance (original minus
short segment length) is attached to the neighbor. Direct constraints
always take priority over propagated ones.

Universal: works for all junction types (roundabout, T-junction,
CrossRoads, multi-T, peer, endpoint)."
```

---

## Phase 3: Remove Road-Length Cap (Superseded)

### Task 4: Remove CapBlendDistanceToRoadLength

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

The `CapBlendDistanceToRoadLength` method and its 5 call sites are now superseded by the propagation system. Short splines get their constraints propagated to neighbors instead of having blend distances capped. The overlap protection in `BlendSplineProfile` (A.5) remains as a safety net.

- [ ] **Step 1: Remove all 5 CapBlendDistanceToRoadLength calls**

Remove these lines (the `blendDist = CapBlendDistanceToRoadLength(...)` line only, NOT the `CalculateAdaptiveBlendDistance` line before it):

1. In `ComputeTJunctionConstraints` — the line after `CalculateAdaptiveBlendDistance` for the T-junction terminating road
2. In `ComputeRoundaboutConstraints` — the line after `CalculateAdaptiveBlendDistance` for the roundabout connecting road
3. In `ComputeMultiTJunctionConstraints` — the line after `CalculateAdaptiveBlendDistance` for multi-T terminators
4. In `ComputePeerJunctionConstraints` — the line after `CalculateAdaptiveBlendDistance` for peer contributors
5. In `ComputeEndpointConstraints` — the line after `CalculateAdaptiveBlendDistance` for endpoints

Each line looks like:
```csharp
            blendDist = CapBlendDistanceToRoadLength(blendDist, terminating.Spline.SplineId);
```
or:
```csharp
            blendDist = CapBlendDistanceToRoadLength(blendDist, contributor.Spline.SplineId);
```

- [ ] **Step 2: Remove the CapBlendDistanceToRoadLength method itself**

Delete the entire method (approximately 20 lines).

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "refactor: remove CapBlendDistanceToRoadLength, superseded by propagation

The per-spline 40% road-length cap is replaced by constraint propagation
through short segments. Short splines now propagate their constraints to
neighbors instead of capping blend distances. The overlap protection in
BlendSplineProfile remains as a safety net for edge cases."
```

---

## Phase 4: Verify

### Task 5: Full build and verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 2: Manual verification checklist**

Generate terrain with `disableSplineMerging=true`. Check:
1. **Log output**: Look for `[PROPAGATE]` entries showing constraint propagation through short splines
2. **Log output**: `Blend propagation: N short spline(s), M propagated constraint(s)` summary
3. **Roundabout entry/exit**: Smooth elevation transition, no bump on the short entry road
4. **Short segments between two junctions**: Linear elevation interpolation (no Hermite correction)
5. **Long roads**: Unchanged behavior (blend distance is not modified for roads that are long enough)
6. **Junction debug image**: Same as before (propagation doesn't change junction types or colors)

---

## Summary

| Task | Description | Key Files |
|------|-------------|-----------|
| 1 | Add `IsPropagated` flag to constraint record | `JunctionEndpointConstraint.cs` |
| 2 | Build spline-junction index and road length helper | `UnifiedJunctionProfileBlender.cs` |
| 3 | Implement constraint propagation pass | `UnifiedJunctionProfileBlender.cs` |
| 4 | Remove superseded `CapBlendDistanceToRoadLength` | `UnifiedJunctionProfileBlender.cs` |
| 5 | Full build + manual verification | All |
