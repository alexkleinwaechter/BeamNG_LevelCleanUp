# Hermite C1 Junction Pipeline Summary (T-Junctions + Roundabouts)

**Date**: 2026-03-06 (roundabout adaptation: 2026-03-06)
**Branch**: `research_rubberband_idea`
**Purpose**: Document the complete junction profile blending pipeline for both T-junctions and roundabout connecting roads, using the unified Hermite C1 three-zone architecture.

---

## Problem Statement

T-junction terminating roads must seamlessly connect to a primary (continuous) road surface. This involves matching:
1. **Elevation** - The terminating road's centerline must reach the primary road's surface
2. **Banking** (lateral tilt) - The terminating road's cross-slope must match the primary road's surface slope at the junction
3. **Slope continuity** - No kinks or abrupt slope changes at zone boundaries

The primary road has its own longitudinal slope and banking that create a non-flat surface. The terminating road descends (or ascends) from terrain-following elevation toward this surface. The challenge: smoothly transition from terrain-following to surface-matching without bumps, cliffs, or depressions.

---

## Pipeline Architecture

### Two-Pass Strategy in `ApplyUnifiedProfiles`

**File**: `UnifiedJunctionProfileBlender.cs`

**Pass 1**: Process ALL splines EXCEPT deferred terminating roads (T-junction and roundabout connecting roads). This establishes primary roads' and roundabout rings' final elevations.

**Pass 2**: Recompute T-junction and roundabout constraints from post-Pass-1 primary/ring elevations, then apply Hermite blending to terminating roads only. This ensures constraints use actual (not estimated) surface values. The set of deferred splines is built from both `JunctionType.TJunction` and `JunctionType.Roundabout` terminating contributors.

### Three-Zone Model per Junction Endpoint

Each T-junction endpoint on a terminating road has three consecutive zones:

```
|--- Flat Zone (F) ---|--- Transition Zone (T) ---|--- Decay Zone (B-T) ---|
|  0 to flatZone      | flatZone to flatZone+T    | flatZone+T to blendDist|
|  h00 = 1.0          | h00 = 1.0                 | h00: 1 -> 0            |
|  analytical delta    | analytical -> handoff      | handoff * h00          |
|  (exact surface)     | (quintic blend)            | (Hermite decay)        |
```

- **F (Flat Zone)** = `primaryCS.EffectiveRoadWidth / 2` (typically 3-5m). The terminating road sits on top of the primary road here.
- **T (Transition Zone)** = `min(F, blendDist * 0.25)` (typically 3-5m). Bridges the slope discontinuity between analytical and constant deltas.
- **B (Blend Distance)** = configurable, default 30m. Total distance of junction influence.

---

## Zone-by-Zone Behavior

### 1. Flat Zone (0 to F)

**Goal**: Exact primary surface match at every cross-section.

**Elevation**: Per-CS analytical delta computed from the primary road's surface plane:
```
primarySurfaceElev = constraint.Elevation
    + constraint.Slope * dot(offset, primaryTangent)
    + sin(constraint.PrimaryBankAngle) * dot(offset, primaryNormal)
delta = primarySurfaceElev - naturalElev
```
Where `offset = cs.CenterPoint - endpoint.CenterPoint` and `naturalElev` is the terrain-following elevation from Phase 2.

**Banking**: Derived from the constraint's `BankAngleRadians` (computed by projecting terminating road edges onto primary surface and measuring the tilt).

**h00 weight**: 1.0 (full correction applied).

### 2. Transition Zone (F to F+T)

**Goal**: Smoothly ramp the slope from primary-road-following to zero, achieving C1 (and C2) continuity at the flat zone boundary.

**Why needed**: Inside the flat zone, the delta varies spatially (tracks primary road slope). Beyond the transition, a constant "handoff delta" is used. Switching abruptly from varying to constant creates a slope discontinuity. The transition zone blends between them.

**Elevation**: Both the analytical delta and the constant handoff delta are computed. They are blended using quintic smootherstep:
```
tTrans = (dist - flatZone) / transitionDist
blend = t^3 * (6t^2 - 15t + 10)   // quintic smootherstep: C2 at both ends
adjDelta = analytical * (1 - blend) + handoff * blend
```

**h00 weight**: 1.0 (still full correction).

### 3. Decay Zone (F+T to B)

**Goal**: Smoothly fade junction influence to zero, returning to terrain-following profile.

**Elevation**: Constant handoff delta multiplied by h00 Hermite basis:
```
effectiveBlendDist = blendDist - transitionDist
t = (dist - flatZone - transitionDist) / effectiveBlendDist
h00 = 2t^3 - 3t^2 + 1   // 1 at start, 0 at end, zero derivative at both
correction = handoffDelta * h00
```

**Banking**: Constant banking delta multiplied by h00 (same decay curve).

---

## Handoff Delta Computation

The "handoff delta" is the bridge between the analytical per-CS computation (flat zone) and the constant-delta Hermite decay (decay zone). It is sampled at the **transition zone end** (not the flat zone edge), ensuring value continuity:

```
Sample the first CS at dist >= flatZone + transitionDist:
  offset = cs.CenterPoint - endpoint.CenterPoint
  primarySurfElev = constraint.Elevation + slope*dot(offset,tangent) + bankSin*dot(offset,normal)
  handoffDelta = primarySurfElev - naturalElev[cs]
```

This ensures the constant delta used in the decay zone matches exactly what the analytical formula produces at the transition zone boundary.

---

## Constraint Computation (`ComputeTJunctionConstraints`)

For each T-junction:
1. Identify the primary (continuous) road and its cross-section at the junction
2. Calculate primary road slope via central difference (+-3 CSes)
3. Project terminating road's left/right edges onto primary surface plane
4. Derive: `centerElev = avg(leftElev, rightElev)`, `bankAngle = asin(edgeDelta / halfWidth)`
5. Store as `JunctionEndpointConstraint` with: Elevation, Slope, BankAngleRadians, FlatZoneDistance, BlendDistanceMeters, PrimaryTangentDirection, PrimaryBankAngleRadians

---

## Post-Iteration Correction (`FinalSnapTJunctionEndpoints`)

**Problem**: The iterative convergence loop (Phases 2-3 repeat up to 3x) can cause the primary road's final elevation to drift from the values used during constraint computation. The flat zone might target a stale elevation.

**Solution**: After all iterations complete, `FinalSnapTJunctionEndpoints` re-reads the CURRENT primary surface and applies corrections:

### Pass 1: Snap Zone (flat zone + transition zone)

For each CS within `flatZone + transitionDist`:
- Recompute primary surface elevation at this CS position (same analytical projection)
- Recompute primary surface banking by projecting CS edges onto primary surface
- Directly set `cs.TargetElevation = surfElev` and `cs.BankAngleRadians = surfBank`
- Measure elevation and banking drift at the outermost snapped CS (boundary with decay zone)

### Pass 2: Drift Propagation (decay zone)

For each CS in the decay zone:
- Apply `cs.TargetElevation += elevDrift * h00(localDist)`
- Apply `cs.BankAngleRadians += bankDrift * h00(localDist)`
- This corrects for stale constraint drift WITHOUT pulling the road toward the primary surface (which would destroy the terrain transition)

**Key insight**: Pass 2 uses a uniform drift value (measured at the boundary), not per-CS error. This preserves the shape of the terrain-following ramp while shifting it to match the corrected snap zone.

---

## Continuity Properties

| Boundary | Value Continuity | Slope Continuity | Method |
|----------|-----------------|------------------|--------|
| Junction endpoint -> flat zone | C0+ (exact surface match) | Analytical delta tracks surface | Per-CS analytical delta |
| Flat zone -> transition zone | C2 (quintic) | Slope ramps from surface-slope to zero | Quintic smootherstep blend |
| Transition zone -> decay zone | C1 (h00) | Zero slope at both ends | h00 starts at 1.0, derivative = 0 |
| Decay zone -> terrain | C1 (h00) | Zero slope at boundary | h00 ends at 0, derivative = 0 |

---

## Key Files

| File | Role |
|------|------|
| `UnifiedJunctionProfileBlender.cs` | `BlendSplineProfile` (three-zone Hermite), `ComputeTJunctionConstraints`, `ComputeRoundaboutConstraints`, `ApplyUnifiedProfiles` (two-pass), `FinalSnapTJunctionEndpoints` (post-iteration correction for both T-junctions and roundabouts) |
| `JunctionEndpointConstraint.cs` | Constraint record: Elevation, Slope, BankAngle, FlatZoneDistance, BlendDistance, PrimaryTangentDirection, PrimaryBankAngle |
| `JunctionSurfaceCalculator.cs` | `GetPrimarySurfaceElevation()` - projects a world position onto the primary road's/ring's surface plane |
| `JunctionHarmonizationParameters.cs` | Configurable parameters: blend distance, auto-calculation, blend function type, roundabout-specific blend distance |
| `BlendFunctions.cs` | `ApplyCubic` (smoothstep), `ApplyQuintic` (smootherstep) |
| `RoundaboutElevationHarmonizer.cs` | Phase 2.6 ring elevation harmonization (connecting road blending skipped when unified system active) |

---

## Failed Approaches (for historical context)

These approaches were tried and abandoned during development. Understanding why they failed is important for avoiding the same mistakes when adapting this to roundabouts.

1. **Post-processing surface snap** (`FindNearestPrimaryCS` + surface projection): Discrete CS lookups create noise at distance; crumpled bumps in the ramp.
2. **h10 slope basis over full blend distance** (30m): Hump magnitude = slopeDelta * blendDist * 0.148. For 5% slope over 30m = 0.22m hump. Unacceptable.
3. **Full analytical delta over entire blend distance**: Linear extrapolation of primary surface diverges at 15-30m from junction. Road floats/digs.
4. **Residual decay from flat zone boundary**: Bump remains because snap and Hermite have different slopes at boundary. Need actual slope matching (which the transition zone now provides).

---

## Roundabout Adaptation (Implemented)

The Hermite C1 pipeline has been extended to handle roundabout connecting roads using the same three-zone architecture as T-junctions. The roundabout ring serves as the "primary road" (continuous surface), and connecting roads are treated as "terminating roads".

### Architecture: Split Responsibilities Between Phase 2.6 and Phase 3

| Phase | Responsibility |
|-------|---------------|
| **Phase 2.6** (`RoundaboutElevationHarmonizer`) | Detect roundabouts, compute harmonized ring elevation, apply uniform/terrain-following elevation to ring CSes, mark junctions `IsExcluded`. **Does NOT blend connecting roads** when unified system is active (`skipConnectingRoadBlending: true`). |
| **Phase 3** (`UnifiedJunctionProfileBlender`) | Treats roundabout connecting roads identically to T-junction terminating roads: two-pass constraint computation, three-zone Hermite blending, post-iteration FinalSnap. |

**Why this split works**: The `originalElevations` dictionary is captured AFTER Phase 2.6 completes. The ring's harmonized elevation appears as the "natural profile" to the unified blender. The ring spline has no constraint (only connecting roads get constraints), so it passes through Pass 1 untouched.

### Constraint Computation (`ComputeRoundaboutConstraints`)

Mirrors `ComputeTJunctionConstraints` with the ring as primary surface:

1. Find the ring (continuous) contributor via `junction.GetContinuousRoads()`
2. Look up all ring CSes and find the one closest to the junction position (more accurate than the contributor's CS alone, which may not be the nearest)
3. Calculate ring slope at connection via `CalculateSlopeAtIndex` (central difference +-3 CSes)
4. For each connecting (terminating) road:
   - Project left/right edges onto ring surface via `JunctionSurfaceCalculator.GetPrimarySurfaceElevation`
   - Derive `centerElev = avg(leftElev, rightElev)`, `bankAngle = asin(edgeDelta / halfWidth)`
   - `FlatZoneDistance = ringCS.EffectiveRoadWidth / 2` (ring half-width = overlap region)
   - `BlendDistanceMeters` from `CalculateAdaptiveBlendDistance` using `GetEffectiveRoundaboutBlendDistance` (default 50m, longer than T-junction 30m)
   - `PrimaryTangentDirection = ringCS.TangentDirection` (ring tangent at connection — enables per-CS analytical delta in flat zone)
   - `PrimaryBankAngleRadians = ringCS.BankAngleRadians` (ring banking at connection)
5. Store as `JunctionEndpointConstraint` — same record type, all fields applicable

### Two-Pass Integration

Roundabout connecting roads are added to the `deferredTerminatingSplines` set alongside T-junction terminating roads:

```
deferredTerminatingSplines = {
    T-junction terminating road spline IDs,
    Roundabout connecting road spline IDs
}
```

- **Pass 1**: Ring spline processed (no constraint → skipped). Other roads blended normally.
- **Pass 2**: Roundabout constraints recomputed from post-Pass-1 ring elevation, then Hermite blending applied.

The junction filter in Pass 2 bypasses `IsExcluded` for roundabout junctions:
```
(j.Type == TJunction && !j.IsExcluded) || j.Type == Roundabout
```

### Three-Zone Model for Roundabout Connecting Roads

Identical to T-junctions:

| Zone | Distance | Behavior for Roundabouts |
|------|----------|--------------------------|
| **Flat Zone** | 0 to `ringCS.EffectiveRoadWidth / 2` | Connecting road matches ring surface exactly (per-CS analytical delta tracks ring slope + banking) |
| **Transition Zone** | Flat zone to flat zone + T | Quintic smootherstep blends analytical delta to constant handoff delta (C2 continuous) |
| **Decay Zone** | Transition to blend distance | h00 Hermite decays handoff delta to zero (C1 continuous back to terrain) |

### Post-Iteration Correction (`FinalSnapTJunctionEndpoints`)

Extended to include `JunctionType.Roundabout` junctions. For roundabouts, finds the closest ring CS to the junction position before computing the snap. The two-pass snap+drift process is identical:

1. **Snap zone**: Re-read CURRENT ring surface elevation/banking, directly set CS values
2. **Drift propagation**: Measure drift at snap boundary, apply `drift * h00` in decay zone

### Flag Handling

| Flag | Ring CSes | Connecting Road CSes (unified active) |
|------|-----------|--------------------------------------|
| `IsRoundaboutBlended` | Set by `ApplyUniformRingElevation` (ring protected from blending) | NOT set (Phase 2.6 skips `BlendConnectingRoads`) — connecting roads flow through Hermite pipeline |
| `IsExcluded` (junction) | N/A | Set by Phase 2.6, but bypassed in unified blender's constraint computation and Pass 2 filters |
| `MaintainBanking` | N/A | Set by Pass 2 for CSes in blend zones (prevents `JunctionBankingAdapter` from overwriting) |

### Roundabout-Specific Parameters

- `RoundaboutBlendDistanceMeters` (default 50m) — longer than T-junction default (30m) for gentler transitions
- `ForceUniformRoundaboutElevation` (default true) — controls ring elevation model
- `RoundaboutConnectionRadiusMeters` (default 10m) — detection radius
- `EnableRoundaboutRoadTrimming` (default true) — trims overlapping road segments

### Legacy Fallback

When `UseUnifiedJunctionSystem = false`, Phase 2.6 handles connecting road blending with the original single-zone approach (`skipConnectingRoadBlending = false`). The unified pipeline changes are inactive.

### Key Differences from T-Junctions

| Aspect | T-Junction | Roundabout |
|--------|-----------|------------|
| Primary surface | Linear road (single tangent) | Ring (tangent varies per connection point) |
| Ring CS lookup | Use contributor's CS directly | Find closest ring CS to junction position |
| Blend distance | 30m default | 50m default (via `GetEffectiveRoundaboutBlendDistance`) |
| Ring processing | Primary road blended in Pass 1 | Ring elevation set in Phase 2.6, passes through Pass 1 untouched |
| `IsExcluded` handling | Not set on T-junctions | Set by Phase 2.6, bypassed in unified blender filters |

---

## Glossary

- **h00**: Cubic Hermite basis function `2t^3 - 3t^2 + 1`. Value: 1->0, derivative: 0 at both ends.
- **Quintic smootherstep**: `t^3(6t^2 - 15t + 10)`. Value: 0->1, first AND second derivative: 0 at both ends.
- **Analytical delta**: Per-CS elevation correction computed from primary road's/ring's surface plane formula.
- **Handoff delta**: Constant elevation correction sampled at transition zone boundary, used in decay zone.
- **Drift**: Difference between the actual (post-convergence) primary/ring surface and the stale constraint used during iteration.
- **Natural elevation**: Terrain-following elevation from Phase 2 (before any junction corrections).
- **Deferred terminating splines**: Splines that terminate at T-junctions or roundabouts, deferred to Pass 2 so their constraints use actual post-Pass-1 primary/ring elevations.
- **Ring CS**: Cross-section on the roundabout ring spline. Serves as the "primary surface" for roundabout connecting road constraints.
