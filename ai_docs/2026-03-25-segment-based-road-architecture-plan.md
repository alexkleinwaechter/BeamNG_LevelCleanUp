# Segment-Based Road Architecture — Implementation Plan

**Date:** 2026-03-25
**Branch:** `feature/relation-protected-junction-blocking` (continuation)
**Status:** Part 1 implemented (2026-03-25), Part 1 bugfixes (2026-03-26), Part 2 not yet implemented
**Prerequisite reading:** `ai_docs/2026-03-25-relation-protected-junction-blocking.md`

---

## Part 1 Bugfixes — 2026-03-26 Debug Session

### Bugs Found and Fixed

#### Bug 1: `RoadCorridorBuilder` and `DecalRoadGenerator` unconditionally skip bridges
**Files:** `RoadCorridorBuilder.cs:28`, `DecalRoadGenerator.cs:39`
**Root cause:** Both had `if (spline.IsBridge || spline.IsTunnel) continue;` — ignoring the `ExcludeBridgesFromTerrain` parameter. Even with `ExcludeBridgesFromTerrain=false`, no DecalRoad was generated for bridges.
**Fix:** Changed to `if ((spline.IsBridge && spline.Parameters.ExcludeBridgesFromTerrain) || (spline.IsTunnel && spline.Parameters.ExcludeTunnelsFromTerrain)) continue;` — matching the pattern already used in `MaterialPainter.cs` and `UnifiedRoadSmoother.cs`.
**Tests:** 7 new tests in `BridgeDecalRoadFilterTests.cs`.

#### Bug 2: `FilterShortSplines` drops short bridge splines
**File:** `UnifiedRoadNetworkBuilder.cs:31`
**Root cause:** `MinCrossSectionsPerPath = 10` meant any spline shorter than ~5m (at 0.5m interval) or ~3m (at 0.3m interval) was silently removed. Short bridges (2 OSM nodes) were filtered before entering the network, breaking topology.
**Fix:** Lowered to `MinCrossSectionsPerPath = 2`. Short splines chain with adjacent splines for elevation smoothing context.
**Note:** The user also had `minPathLengthPixels=10` in their terrain preset which had the same effect — set to 0 to include all OSM ways.

#### Bug 3 (ROOT CAUSE): Roundabout fallback falsely flags bridges as roundabouts
**File:** `UnifiedRoadSmoother.cs:574-591` (Phase 1.5 `IdentifyRoundaboutSplines`)
**Root cause:** The fallback closed-loop detection used `closedLoopTolerance = 15.0f` meters. Any spline where start-end distance < 15m was marked `IsRoundabout=true`. Short bridges (7-20m) have start≈end positions, triggering false detection. Roundabout splines are excluded from the elevation graph (`if (spline.IsRoundabout) continue;`), so bridges were silently dropped from elevation chains.
**Impact:** This was the primary reason bridges didn't participate in network-chained elevation profiles. A 7.3m bridge was flagged as roundabout, excluded from the elevation graph, and got no chain-based smoothing.
**Fix:** Added `if (spline.IsStructure) continue;` before the closed-loop check. Bridges/tunnels are never roundabouts by geometric heuristic. Real bridge-roundabouts (tagged `junction=roundabout` in OSM) are detected by the first pass (`RoundaboutDetector`) which uses the authoritative OSM tag.
**Diagnostic evidence:** Log showed `spline 291 [BRIDGE] IsRoundabout=True` — once fixed, chain formed correctly: `Chain 61: 189→291[B]→290→255→254`.

#### Addition: Phase 1.9 — Junction Normal Alignment (experimental)
**File:** `UnifiedRoadSmoother.cs` (new method `AlignCrossSectionNormalsAtJunctions`)
**Purpose:** When spline merging is disabled, adjacent splines have independently computed tangent/normal directions at shared endpoints. Road surfaces fan apart at junctions even though centerlines meet. This pass averages tangent directions at each junction and re-interpolates normals for short splines (bridges), giving them inherited curvature from adjacent roads.
**Status:** ~~Implemented but effectiveness unclear~~ **REVERTED** — the 70/30 neighbor-biased tangent blending and internal normal re-interpolation corrupted cross-section normals at curves, causing DecalRoad surfaces to progressively narrow. The normal direction controls lateral node placement (`center ± normal * offset`), so rotating normals toward the tangent shrinks the visible road width. The approach is fundamentally too aggressive for general junctions; any future attempt should be scoped narrowly to bridge endpoints only.

#### Addition: Diagnostic logging
**Files:** `UnifiedRoadNetworkBuilder.cs`, `NetworkElevationGraph.cs`, `OsmGeometryProcessor.cs`
**Purpose:** Added `TerrainCreationLogger.Current?.Detail()` logging for:
- Bridge/tunnel spline endpoints with OSM node IDs and coordinates
- Pre-filter bridge/tunnel splines with estimated cross-section counts
- FilterShortSplines dropped splines with reason
- Elevation graph bridge/tunnel edge details (IsRoundabout, IsPaintOnly, crossSection count)
- Chain membership for bridge/tunnel-containing chains
**Note:** These diagnostics should be cleaned up before merging to develop.

### Open Issue: Elevation ditch at bridge boundaries
After fixing the three bugs above, bridges now correctly participate in elevation chains. However, a visible **elevation ditch** appears at bridge-road boundaries. This may be caused by:
- Junction harmonization (Phase 3) overwriting chain-smoothed elevations at bridge endpoints
- The junction plateau system creating incorrect elevation at bridge junctions
- The slope constraint or endpoint anchoring interfering at chain-internal junctions
**Status:** Not yet debugged. To be investigated in next session.

### Open Issue: DecalRoad visual gap at non-merged junctions
Road surfaces still fan apart at junctions between non-merged splines. This is the tangent discontinuity problem — each spline computes tangent/normal independently. The Phase 1.9 normal alignment is a partial mitigation. The full solution is Part 2 (junction geometry fill).

---

## Motivation

The spline merger (`NodeBasedPathConnector`) uses angle-first greedy matching to connect OSM ways into longer splines. At complex junctions (highway ramps, bridge approaches), it produces wrong connections — 180° turns, crossover merges, incorrect junction geometry. Disabling merging (`disableSplineMerging=true`) gives correct topology but breaks:

1. **Elevation smoothing** — the `OptimizedElevationSmoother` uses a ~75m window (301 samples × 0.5m). Short splines have too few cross-sections, producing unnatural elevation ramps at boundaries.
2. **DecalRoad visual continuity** — DecalRoads are generated per-spline with no junction fill, causing visible gaps where splines meet.

### Why Not Fix the Merger?

Route-relation-protected junction blocking (the current branch) is logically correct in unit tests but still produces wrong merges in real-world data. The fundamental problem: **merging at 3+ way junctions is inherently ambiguous**. OSM2World (a mature Java project) proves you can build accurate road geometry without any way merging, by treating junctions as first-class geometric entities.

### Architecture Shift

Decouple "spline length for smoothing" from "spline identity for topology":

- **Splines stay short** — one per OSM way (no merging)
- **Elevation smoothing** operates on network-chained profiles (long virtual sequences)
- **DecalRoad generation** fills junction areas with explicit junction geometry
- **Junction blending** gets better inputs (correct topology, no wrong merges)

### Critical Quality Bar

- **Perfect elevation transitions** at junctions — bumps are a game-killer
- **Perfect endpoint-to-edge connectivity** — cross-section profiles (position, width, longitudinal slope, lateral slope, banking) must match at junction boundaries
- **Decent junction geometry** — the area where roads meet must look right
- Splines are 3D ribbons with width, not thin lines

---

## Part 1: Network-Aware Elevation Smoothing (Implement First)

### Problem

`OptimizedElevationSmoother.CalculateTargetElevations()` (Phase 2) operates per-spline. With merging disabled, many splines are 50-200m (single OSM ways). The 75m smoothing window works poorly on splines shorter than ~150m — especially in hilly terrain where the filter needs context from neighboring road segments to produce natural grades.

### Solution: Network-Chained Elevation Profiles

Before the per-spline elevation filter runs, build an **elevation graph** from the junction topology (already detected in Phase 1.8). Chain splines through the graph into long "elevation runs" for filtering, then write the smoothed elevations back to the individual splines' cross-sections.

### Step 1.1: Build Elevation Graph from Junction Topology

**New class: `NetworkElevationGraph`**
**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs`

**Input:** `UnifiedRoadNetwork` with junctions already detected (Phase 1.8)

**Data structure:**
```
ElevationNode:
  - JunctionId (or synthetic ID for dead-end endpoints)
  - Position (Vector2, junction center)
  - ConnectedEdges: List<ElevationEdge>

ElevationEdge:
  - SplineId (the ParameterizedRoadSpline this edge represents)
  - StartNode, EndNode: ElevationNode references
  - Priority (from spline)
  - Length (spline TotalLength)
  - CrossSectionCount
  - IsReversed (true if spline direction is EndNode→StartNode)
  - IsBridge / IsTunnel (from spline flags)
```

**Construction algorithm:**
1. For each junction in `network.Junctions`, create an `ElevationNode`
2. For each spline, find which junctions its start/end endpoints belong to (using the existing junction membership data from `NetworkJunctionDetector`)
3. Splines whose endpoints don't belong to any junction get synthetic endpoint nodes (dead ends)
4. Each spline becomes an `ElevationEdge` connecting two nodes

**Important:** The graph must handle:
- Dead-end splines (one endpoint not in any junction) — synthetic terminal node
- Roundabout ring splines — **excluded from graph construction** (see Step 1.2)
- Multiple splines between the same junction pair — parallel edges
- Bridge/tunnel splines — included in graph as normal edges (see Step 1.3a)

**Lifecycle:** The `NetworkElevationGraph` is ephemeral — built at the start of Phase 2, consumed during elevation filtering, and not retained for later phases. Phase 3's `DetectJunctions()` calls `network.Junctions.Clear()` which would invalidate graph references, so the graph must be fully consumed before Phase 3 runs.

### Step 1.2: Chain Splines into Elevation Runs

**New method: `NetworkElevationGraph.BuildElevationChains()`**

An "elevation chain" is an ordered sequence of edges (splines) through the graph that forms a long continuous path. The elevation smoother will filter each chain as one long profile.

**Chain-building algorithm (greedy longest-path):**

1. Sort all edges by priority descending, then by length descending
2. Mark all edges unvisited
3. For each unvisited edge (highest priority first):
   a. Start a chain with this edge
   b. **Extend forward:** At the end node, if there is exactly one unvisited edge of the same or compatible highway type, append it. If multiple candidates, pick the one with smallest deflection angle (most straight). If the node has 3+ edges (true junction), **stop extending** — don't chain through complex junctions.
   c. **Extend backward:** Same logic from start node
   d. Record the chain; mark all edges as visited
4. Any remaining unvisited edges become single-edge chains

**Chain-through rules at nodes:**
- **Degree-2 node (simple continuation):** Always chain through — this is a non-junction node where two splines meet (e.g., where an OSM way was split at a node)
- **Degree-3+ node (true junction):** Only chain through if there is a clear "through-road" pair:
  - Same `OsmRoadType` on both sides
  - Same or shared route relation
  - Deflection angle < 30°
  - Width ratio within 2:1 (a 4-lane road narrowing to 2 lanes at a junction should not chain through — the terrain surface under different widths has different sampling characteristics)
  - If ambiguous, don't chain through — let the junction blender handle the transition
- **Dead-end node (degree 1):** Chain terminates here

**Exclusions from chaining:**
- **Roundabout ring splines** (`IsRoundabout=true`): Excluded from chain building entirely. They become standalone single-edge chains. Roundabouts are closed-loop splines with their own Phase 2.6 harmonization; including them in chain extension could cause infinite loops (start ≈ end node) and would conflict with the dedicated roundabout elevation logic.
- **Bridge/tunnel splines**: **NOT excluded** from chaining. They participate in chains normally to provide elevation context for approach ramps. The distinction between "smooth through" and "paint/affect terrain" is handled in Step 1.3a.

**Key insight:** This is NOT the same as the old merger. The merger permanently fuses splines (losing topology). Chaining is temporary and only affects how the elevation filter sees the data. Each spline retains its identity, endpoints, and metadata. Wrong chains are far less damaging than wrong merges — they only affect smoothing quality, not junction geometry.

**Diagnostic output:** Emit chain membership as a debug property on cross-sections (`ChainId`, `ChainIndex`) so chains can be color-coded in the 3D viewer. Also log average chain length (in cross-sections) vs average spline length — if chains aren't significantly longer than individual splines, the chaining effort has low impact and the chain-through rules may need loosening.

### Step 1.3: Filter Elevation on Chains

**Modify: `OptimizedElevationSmoother.CalculateTargetElevations()`**

Currently (Phase 2 in `UnifiedRoadSmoother.CalculateNetworkElevations`, lines 724-810):
```
foreach spline in network:
    sample heightmap → rawElevations[]
    filter(rawElevations, windowSize) → smoothed[]
    assign smoothed[i] → crossSections[i].TargetElevation
```

**New approach:**
```
// Build chains from the elevation graph
var graph = new NetworkElevationGraph(network);
var chains = graph.BuildElevationChains();

foreach chain in chains:
    // Concatenate raw elevations from all splines in chain order
    chainRawElevations = []
    chainCrossSections = []
    for each edge in chain:
        splineCS = getCrossSections(edge.SplineId)
        if edge.IsReversed: splineCS = reverse(splineCS)

        // Dedup: skip first CS if co-located with last appended CS
        if chainCrossSections.length > 0:
            lastCS = chainCrossSections.last()
            firstCS = splineCS[0]
            if distance(lastCS.CenterPoint, firstCS.CenterPoint) < crossSectionSpacing / 2:
                splineCS = splineCS[1:]  // skip duplicate endpoint

        chainRawElevations.append(sampleHeightmap(splineCS))
        chainCrossSections.append(splineCS)

    // Filter the entire chain as one long profile
    smoothed = filter(chainRawElevations, windowSize)

    // Write back to individual cross-sections
    for i in range(len(chainCrossSections)):
        chainCrossSections[i].TargetElevation = smoothed[i]
```

**Cross-section deduplication at chain joints:** When two splines meet at a degree-2 node, their endpoint cross-sections are co-located (within the junction clustering radius). Without dedup, the filter sees a duplicate sample at the same distance — harmless for box filtering but can cause ringing with Butterworth IIR (`UseButterworthFilter`). The dedup step skips the first CS of the next edge if it's within `crossSectionSpacing/2` of the last appended CS.

**Handling chain boundaries:**
- At the start/end of a chain (dead ends or complex junctions), the filter window extends beyond the data. Current behavior: mirror-pad or clamp. This is unchanged — the same edge effects exist today at spline endpoints, but now they only occur at chain endpoints (which are real road terminators or complex junctions, not arbitrary OSM way boundaries).

**Handling reversed splines in chains:**
- When a spline is traversed in reverse direction within a chain, its cross-sections must be iterated in reverse order for elevation assignment
- The cross-section indices within the spline remain unchanged — only the iteration order during chain concatenation is reversed
- After filtering, write back to the original cross-section objects (they're references, not copies)

**Re-smooth iterations must also use chains:** The iterative loop in `CalculateNetworkElevations` runs up to 3 iterations. Iteration 0 samples the heightmap; iterations 1+ call `ReSmoothFromExistingElevations()` per-spline. If re-smoothing is per-spline while iteration 0 was chain-based, iteration 1 re-introduces boundary artifacts that chaining eliminated. **Both paths (initial sample and re-smooth) must concatenate chains before filtering.** The chain structure is built once and reused across iterations.

### Step 1.3a: Bridge/Tunnel Elevation — Smooth but Don't Paint

**Design change from current behavior:** Currently, bridge/tunnel splines with `ExcludeBridgesFromTerrain`/`ExcludeTunnelsFromTerrain` are skipped entirely in Phase 2 — their cross-sections get `IsExcluded=true` and no `TargetElevation` is computed (`UnifiedRoadSmoother.cs` lines 752-768). This loses elevation data that is needed later for bridge ramp generation.

**New behavior:** Bridge/tunnel splines participate fully in elevation smoothing (both chaining and filtering). Their `TargetElevation` is computed like any other spline. **However, this elevation is virtual data only — it must NEVER modify the terrain surface under the structure.**

- `IsExcluded` remains `true` — this flag means **"don't modify terrain under bridges/tunnels"** (the user-facing helptext). All downstream consumers that write to the heightmap or generate visible road geometry MUST check `IsExcluded` and skip:
  - **Phase 4 terrain blending (IDW):** Already checks `IsExcluded` — excluded cross-sections do not contribute to heightmap modification. The terrain under a bridge stays at its natural elevation.
  - **DecalRoad generation:** Already skips excluded splines — no road surface painted under bridges.
  - **Any future heightmap writer:** Must respect `IsExcluded`. This is the contract.
- The computed `TargetElevation` on excluded cross-sections is **read-only virtual data**, available for:
  - Bridge mesh generation (approach ramp elevation matching)
  - Junction harmonization (Phase 3) where a bridge endpoint meets a non-bridge road
  - Future structure elevation profile computation
- **Key invariant:** `IsExcluded=true` + valid `TargetElevation` means "we know what elevation the road would have here, but we don't touch the terrain." Previously `IsExcluded=true` implied `TargetElevation=NaN` (never computed). After this change, both fields are set independently.

**Implementation:** In `CalculateNetworkElevations`, remove the early `continue` for bridge/tunnel splines. Keep the `IsExcluded = true` marking (on iteration 0 only). Let the spline flow through the normal chain-based elevation path.

```
// Before (current):
if (spline.IsBridge && parameters.ExcludeBridgesFromTerrain) {
    cs.IsExcluded = true;
    continue;  // ← skips elevation computation entirely
}

// After:
if (spline.IsBridge && parameters.ExcludeBridgesFromTerrain) {
    cs.IsExcluded = true;
    // DO NOT skip — fall through to chain-based elevation computation
    // IsExcluded controls terrain painting/DecalRoad generation, not smoothing
}
```

**Chain interaction:** Bridge/tunnel splines chain normally. A chain like `[road A → bridge B → road C]` gets one continuous elevation profile. This is ideal — the bridge approach ramps get natural grades from the filter seeing the full context. The bridge's `ElevationProfile` (if set) can override `TargetElevation` later during structure-specific processing (Phase 2.3), but the smooth baseline is always available.

### Step 1.4: Integrate with WI-6 Endpoint Anchoring

Current WI-6 anchoring (`ApplyEndpointAnchoring`, lines 215-271) biases spline endpoints toward terrain elevation at junction centers. With chained elevation:

- **Chain-internal junctions** (degree-2 nodes where two splines of the same chain meet): No anchoring needed — the filter sees through them naturally
- **Chain-boundary junctions** (degree-3+ where chain terminates): Apply anchoring as before — these are the points where the elevation profile needs to transition to whatever the junction blender decides

**Note:** `BuildEndpointAnchorLookup` currently only creates anchors for `JunctionType.Endpoint` (isolated dead-end) junctions (line 841: `if (junction.Type != JunctionType.Endpoint) continue`). Degree-2 continuation nodes at chain-internal joints are NOT `Endpoint` type, so they are already skipped. **No code change needed** in the anchor builder itself — the existing type filter achieves the desired behavior. Document this invariant with a comment referencing the chain system.

### Step 1.5: Slope Constraint Enhancement

Current `ApplySlopeConstraint` (enforces max grade, e.g., 6°) operates per-spline via `EnforceMaxSlopeConstraint` (forward/backward pass on a flat elevation array). With chains:

- Apply slope constraint on the full chain elevation profile, not per-spline
- This prevents the constraint from creating kinks at spline boundaries within a chain
- The chain profile is a flat `float[]` — the same forward/backward algorithm works unchanged, just on a longer array

**Junction slope exemption at chain boundaries:** The current `JunctionSlopeExemptionRadiusMeters=30m` skips slope clamping near junction nodes. At chain boundaries (complex junctions), this exemption continues to apply. At chain-internal joints (degree-2 continuations), no exemption is needed because the slope constraint sees a continuous profile — there is no boundary to create a kink. The `EnforceMaxSlopeConstraint` method operates on a position-less `float[]` with uniform spacing; the exemption logic is in the calling code and only needs to mark the chain-boundary cross-sections, not the internal ones.

### Step 1.6: Impact on Downstream Phases

**Phase 2.5 (Banking):** No change needed. Banking is computed from cross-section curvature, which is a local geometric property unaffected by how elevation was filtered.

**Phase 2.6 (Roundabout harmonization):** No change needed. Roundabout rings are excluded from chaining (Step 1.2) and processed as standalone splines with their own dedicated harmonization.

**Phase 3 (Junction harmonization + profile blending):** This is the key beneficiary:
- With correct topology (no wrong merges) and smoother incoming elevation profiles (chain-filtered), the junction blender gets **much better inputs**
- The Hermite-based blending should produce smoother results because the "natural elevation" it starts from is already well-filtered
- The iterative convergence loop (max 3 iterations, threshold 0.01m) should converge faster
- **No code changes needed** in the blender itself for Phase 1 — it already works on per-spline cross-sections with junction constraints

**Phase 4 (Terrain blending):** No change needed. IDW blend operates on final cross-section elevations regardless of how they were computed. Bridge/tunnel cross-sections remain `IsExcluded=true` and are skipped as before.

### Step 1.7: `disableSplineMerging` Default Change

Once network-chained smoothing is working:
- Change `disableSplineMerging` default to `true` for OSM pipeline
- Keep the flag available for testing/comparison
- The `NodeBasedPathConnector` and `RouteRelationAssembler` code stays in place but is bypassed by default

### Step 1.8: Validation & Testing

#### Unit Tests for `NetworkElevationGraph` (Graph Construction)

**Test class: `NetworkElevationGraphTests`**
**Location:** `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/NetworkElevationGraphTests.cs`

All tests use a helper that builds minimal `UnifiedRoadNetwork` with synthetic splines and junctions (no heightmap, no real OSM data).

| Test | Setup | Expected |
|------|-------|----------|
| `LinearChain_ThreeSplines_DegreeTwo_SingleChain` | A→B→C, 3 splines, degree-2 internal nodes, same road type | One chain [A,B,C] |
| `TJunction_ThreeSplines_ThreeSeparateChains` | A→J, B→J, C→J, degree-3 node J, different road types | Three single-edge chains |
| `ThroughRoad_ChainsAcrossJunction_SideRoadSeparate` | A→J→B same highway type <30° deflection + C→J side road | Chain [A,B] + chain [C] |
| `Roundabout_ExcludedFromChaining` | Ring spline (IsRoundabout=true) + 3 connecting splines | Ring = standalone chain; connectors = separate chains |
| `DeadEnd_SingleChain` | A→B where B is degree-1 | Single chain [A→B] |
| `BridgeSpline_IncludedInChain` | road A → bridge B → road C, same type, degree-2 joints | One chain [A,B,C]; bridge edge has IsBridge=true |
| `ParallelEdges_SeparateChains` | Two splines between same junction pair | Two separate single-edge chains |
| `WidthMismatch_BlocksChaining` | A→J→B same type but width ratio >2:1 | Two chains [A], [B] (not chained through J) |
| `AmbiguousJunction_NoChainThrough` | A→J→B and A→J→C, both same type, both <30° deflection | Three separate chains (ambiguous = don't chain) |
| `DisconnectedSpline_SyntheticNodes` | Spline with endpoints not in any junction | Single chain with synthetic terminal nodes |

#### Unit Tests for Chain Elevation Filtering

**Test class: `ChainElevationFilteringTests`**
**Location:** `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/ChainElevationFilteringTests.cs`

These tests create cross-sections with known positions, assign synthetic `OriginalTerrainElevation` values, run chain-based filtering, and verify `TargetElevation` output.

| Test | Setup | Expected |
|------|-------|----------|
| `SingleSplineChain_IdenticalToPerSpline` | One spline, one chain | Output matches per-spline `CalculateTargetElevations` exactly |
| `TwoSplineChain_SmoothAcrossBoundary` | Two splines, step function at boundary (terrain drops 5m at joint) | No kink at boundary; smooth transition spanning both splines |
| `TwoSplineChain_VsPerSpline_SmoothAtJoint` | Same terrain, compare chain-based vs per-spline | Chain-based has smaller elevation discontinuity at the joint |
| `ReversedSplineInChain_CorrectWriteBack` | Chain [A forward, B reversed], flat terrain | `TargetElevation` written to correct cross-sections respecting reversal |
| `ChainDedup_ColocatedEndpoints_NoDuplicateSample` | Two splines sharing endpoint within spacing/2 | Concatenated array has no duplicate; Butterworth filter produces no ringing |
| `ReSmoothIteration_UsesChains` | Run iteration 0 (sample + filter), then iteration 1 (re-smooth) | Re-smooth also operates on chain-concatenated profile, no boundary kink reintroduced |
| `BridgeSpline_GetsTargetElevation_ButTerrainUntouched` | Chain [road, bridge, road], bridge has `IsExcluded=true` | Bridge cross-sections have valid `TargetElevation` (not NaN); `IsExcluded` still true; verify Phase 4 IDW blend skips these cross-sections (terrain under bridge unchanged) |
| `SlopeConstraint_OnChain_NoKinkAtJoint` | Two-spline chain, steep terrain exceeding max slope at joint | Slope constraint applied to full chain array; no kink at spline boundary |
| `ShortSpline_BenefitsFromChainContext` | 30m spline (60 CS) chained with 200m spline, hilly terrain | Short spline's elevation profile is smoother than per-spline filtering would produce |

#### Unit Tests for Endpoint Anchoring Integration

**Test class: `ChainAnchoringTests`**
**Location:** `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/ChainAnchoringTests.cs`

| Test | Setup | Expected |
|------|-------|----------|
| `ChainInternalJoint_NoAnchor` | A→B→C chain, B is degree-2 node | No anchor created for B's endpoints; only chain-terminal anchors (if dead-end) |
| `ChainBoundary_DeadEnd_AnchorApplied` | A→B chain, B is degree-1 dead end | Anchor applied at B endpoint, biasing toward terrain |
| `ChainBoundary_ComplexJunction_NoAnchor` | A→J chain, J is degree-3+ | No anchor at J (Endpoint anchoring only applies to `JunctionType.Endpoint`) |

#### Integration Test (Kattenes Area)

- Generate with `disableSplineMerging=true` + chain-based smoothing
- Compare elevation profiles against current merged-spline output
- Check for bumps at junctions (max elevation discontinuity at any junction < 0.1m)
- Verify no regression in hilly terrain smoothness
- Verify bridge/tunnel splines have valid `TargetElevation` but `IsExcluded=true`
- Log chain statistics: average chain length vs average spline length, number of chains vs number of splines

**Key files to modify/create:**
| File | Change |
|------|--------|
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs` | **New** — graph + chain builder |
| `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` | Add chain-aware overload for both initial and re-smooth paths |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | Build graph after Phase 1.8, pass chains to Phase 2; remove bridge/tunnel elevation skip |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs` | No change |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Default `disableSplineMerging=true` |
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs` | Add `ChainId` and `ChainIndex` debug properties |
| `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/NetworkElevationGraphTests.cs` | **New** — graph construction + chaining tests |
| `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/ChainElevationFilteringTests.cs` | **New** — chain-based filtering tests |
| `Grille.BeamNG.Lib_Tests/Terrain/Algorithms/ChainAnchoringTests.cs` | **New** — anchoring integration tests |

---

## Part 2: Phantom Node DecalRoad Continuity (Implement Second)

### Problem

With merging disabled, each OSM way becomes its own spline with its own set of DecalRoads. Where splines meet at junctions, two problems occur simultaneously:

1. **Tangent discontinuity (kink):** Each DecalRoad's Catmull-Rom interpolation has no context from the adjacent segment's nodes. The tangent at the boundary is computed only from the segment's own last 2 nodes, causing the curve to bend away from where the adjacent road continues. This creates visible angular breaks at every junction.

2. **Physical gap:** The DecalRoad surfaces simply end at their last node, leaving untextured terrain visible between segments. Combined with the tangent divergence, the gap widens because the curves pull apart.

Both problems are visible in practice at bridge↔road boundaries, T-junctions, and roundabout connections. Width transitions between splines of different widths also appear abrupt rather than smoothly tapered.

### Research: BeamNG DecalRoad Spline Internals

**Spline type:** BeamNG DecalRoad uses **Catmull-Rom** spline interpolation (confirmed in `geometry.lua` line 184: "Catmull-Rom is fitted through X and Y, monotonic Steffen preconditioning is applied for Z"). The `improvedSpline=true` flag enables an enhanced variant. The engine computes tangent at node `i` from `(P_{i+1} - P_{i-1}) / 2`. At endpoints, it clamps to available nodes.

**`startTangent` / `endTangent`:** These are **scalar floats** (always `"0"` in all BeamNG prefab examples), not vectors. They control tangent *magnitude* at endpoints, not *direction*. They **cannot solve directional tangent mismatches** between adjacent segments.

**`breakAngle`:** Controls angle threshold for spline discontinuity (typically 3°). Not useful for cross-segment continuity.

**Width interpolation:** Catmull-Rom interpolates the 4th node component (width) alongside X, Y, Z. Width transitions between nodes with different widths are smoothed automatically by the spline.

### Solution: Phantom Node Overlap System

Instead of generating new junction-fill geometry classes, extend each DecalRoad beyond its spline boundary with **"phantom nodes"** borrowed from adjacent splines. This solves both problems with one mechanism:

- **Tangent continuity** — Catmull-Rom gets adjacent-spline context, computing correct tangent at the boundary
- **Gap filling** — overlapping extensions cover the junction area
- **Width tapering** — phantom nodes carry the adjacent spline's width, and Catmull-Rom smoothly interpolates between widths

**Fallback (Plan B):** If phantom node overlap doesn't produce satisfactory results (z-fighting, texture seams, insufficient tangent quality), the fallback is to **merge adjacent DecalRoads into single continuous node sequences** — concatenate node lists through the junction point, dedup the shared endpoint. This gives perfect continuity by definition but complicates per-spline identity, the layer system, chunking, and junction interruption. Try Plan A first because it's minimally invasive; Plan B is the documented nuclear option.

### Step 2.1: Junction Adjacency Map

**New class: `JunctionAdjacencyMap`**
**Location:** `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionAdjacencyMap.cs`

Built once from `network.Junctions`, consumed by `DecalRoadGenerator`. For each spline endpoint at a junction, stores:

- Which junction it belongs to
- The list of adjacent spline IDs at that junction
- Whether the spline's **start** or **end** touches the junction
- The junction's degree (number of connected splines)
- Each adjacent spline's approach direction (tangent vector toward junction)

**Data structure:** Lookup indexed by `(splineId, isStart)` → `JunctionEndpointInfo`:
```
JunctionEndpointInfo:
  - JunctionId: int
  - JunctionCenter: Vector2
  - JunctionDegree: int
  - AdjacentSplines: List<AdjacentSplineInfo>

AdjacentSplineInfo:
  - SplineId: int
  - IsAdjacentStart: bool  // which end of the adjacent spline touches this junction
  - ApproachDirection: Vector2  // unit vector toward junction center
  - RoadWidth: float
  - OsmRoadType: string
```

**Construction:** Reuses existing `NetworkJunction` data from `network.Junctions` — no new detection logic. Built at the same time as `BuildContinuityLookup()` in `DecalRoadGenerator.Generate()`.

### Step 2.2: Phantom Node Selection at Multi-Way Junctions

At a degree-2 junction (simple continuation or bridge boundary), selection is trivial — there's only one adjacent spline.

At degree-3+ junctions, each spline picks **which** adjacent spline to borrow phantom nodes from:

**Scoring algorithm (per adjacent candidate):**
1. **Deflection angle** — angle between current spline's approach direction and adjacent spline's departure direction. Smaller = more "straight through" = better. Weight: high.
2. **Road type compatibility** — same `OsmRoadType` preferred. Weight: medium.
3. **Width ratio** — closer to 1:1 = better visual match. Weight: low.

**Pick the best-scoring adjacent spline.** If no good candidate exists (all angles > 90°, all different road types), still pick the best available — even a suboptimal phantom is better than no context, because `startEndFade` blends out the tip.

**Roundabout connections:** A road approaching a roundabout has the ring spline as its adjacent. The phantom nodes curve along the ring's centerline — exactly the right visual behavior.

**Every spline at the junction gets phantom nodes independently.** At a T-junction with splines A, B, C: each picks its own best neighbor. They may pick each other or the same spline — overlapping surfaces blend.

### Step 2.3: Phantom Node Generation

**Where it happens:** Inside `DecalRoadGenerator.GenerateForSpline()`, after normal node generation but before adding to `results`.

**Algorithm per DecalRoad:**

1. Check if this spline's **start** endpoint is at a junction (via adjacency map)
2. If yes, get the best adjacent spline (Step 2.2)
3. Get the adjacent spline's cross-sections near the shared junction point
4. **Prepend 2-3 nodes** sampled from the adjacent spline's cross-sections, using the **same lateral offset logic** as the current layer:
   ```
   offset = position * 0.5f * adjacentSectionRoadWidth
   offsetPos = adjacentCS.CenterPoint + adjacentCS.NormalDirection * offset
   ```
5. Transform to world coordinates with elevation from `adjacentCS.TargetElevation`
6. Repeat for the **end** endpoint (append phantom nodes)

**All layers get phantom nodes uniformly.** The layer's `position` value (normalized -1 to +1) determines lateral offset — surface at 0, edge lines at ±1.0, tread marks at lane centers, direction divider at the direction boundary. The existing junction interruption system (`JunctionConstraint == Interrupt`) removes marking layers in the overlap zone **after** generation, just as it does today.

**Node count:** 2-3 phantom nodes is the sweet spot. Catmull-Rom needs at minimum 1 node beyond the junction to compute a correct tangent, but 2-3 gives smoother curvature. Phantom distance ≈ `nodeSpacingMeters * 2-3` (typically 6-15m).

**Width matching:** Phantom nodes carry the width from the adjacent spline's cross-sections. At a road↔bridge boundary where widths differ, the phantom nodes bridge between widths — Catmull-Rom's 4th-component interpolation produces smooth visual tapering.

**Elevation:** Phantom nodes use `TargetElevation` from the adjacent spline's cross-sections. Since Part 1 already chains elevation across boundaries, phantom nodes inherit smooth elevation naturally.

**Critical detail:** Phantom nodes use the **adjacent spline's** `NormalDirection`, not the current spline's. Two splines meeting at a junction approach from different angles — the normal vectors diverge. Using the adjacent normal ensures phantom nodes are positioned correctly relative to the adjacent geometry.

### Step 2.4: StartEndFade Auto-Adjustment

When phantom nodes are added, `startEndFade` is adjusted to cover the phantom region:

- **Prepending phantoms:** `StartEndFade[0]` = phantom extension length in meters (fade-in over phantom region). Original start fade preserved on the non-phantom end.
- **Appending phantoms:** `StartEndFade[1]` = phantom extension length (fade-out over phantom region). Original end fade preserved.

This ensures the texture fades to transparent at the extension tip rather than having a hard edge. The original spline portion retains its configured fade values.

### Step 2.5: Metadata on GeneratedDecalRoad

**Add to `GeneratedDecalRoad`:**
```
PhantomNodeCountStart: int  // how many phantom nodes were prepended (default 0)
PhantomNodeCountEnd: int    // how many phantom nodes were appended (default 0)
```

This metadata lets the post-processor and debug tools know which nodes are extensions. It also enables Plan B (merge) to strip phantom nodes before concatenation if needed.

### Step 2.6: Post-Processor Compatibility

**No changes needed to `DecalRoadOverlapPostProcessor`:**

The overlap post-processor detects spatial overlap between DecalRoads using `SurfaceFootprintIndex`. Phantom extensions naturally create overlap at junctions:

- For layers with `JunctionConstraint == Interrupt`: post-processor removes nodes in the overlap zone — including phantom nodes. This is correct: the phantom nodes still guide Catmull-Rom's tangent computation (the engine evaluates the spline through ALL nodes before visual clipping).
- For layers with `JunctionConstraint == None` (surface, wear): phantom nodes survive, creating the visual overlap that fills the junction area.
- `BuildContinuityLookup` unchanged: continuous road pairs at junctions already get overlap exemption.

**Key insight:** Catmull-Rom computes the curve through all nodes in the list *before* any visual clipping. `startEndFade` and post-processor trimming affect visibility, not curve shape. The tangent-guiding effect persists even when the phantom portion is faded out or interrupted.

### Step 2.7: Plan B Fallback — DecalRoad Merging

If phantom node overlap doesn't produce satisfactory results after visual testing:

**Trigger conditions:**
- Z-fighting between overlapping phantom extensions is visually distracting
- `startEndFade` blending doesn't produce clean enough transitions
- Width interpolation through Catmull-Rom's 4th component isn't smooth enough

**Merge approach:**
- After generating all per-spline DecalRoads, run a merge pass
- For each junction, find DecalRoad pairs sharing the same layer (same material, same lateral position, same layer type)
- Concatenate node lists through the junction point (dedup shared endpoint)
- Result: one longer DecalRoad with perfect continuity

**Why Plan A first:**
- Merging complicates per-spline identity (`SplineId` on merged road is ambiguous)
- Merging across lane-change boundaries requires resolving conflicting lane counts
- Very long merged chains stress the 80-node chunker
- Junction interruption can't easily operate "in the middle" of a merged road

### Step 2.8: Validation & Testing

**Unit tests:**

| Test | Setup | Expected |
|------|-------|----------|
| `Degree2_PhantomNodesAdded` | Two splines sharing a degree-2 junction | Each DecalRoad gets 2-3 phantom nodes from the other |
| `PhantomNodes_CorrectLateralOffset` | Surface layer (pos=0) + edge layer (pos=1.0) at a junction | Phantom nodes offset by `pos * 0.5 * adjacentWidth` using adjacent normal |
| `WidthInterpolation_DifferentWidths` | 8m road meets 6m road | Phantom nodes carry adjacent width; node[4] transitions from 8→6 |
| `MultiWay_BestAdjacentByAngle` | T-junction: A→J←B (straight), C→J (side) | A picks B (smallest deflection), C picks A or B (best angle) |
| `DeadEnd_NoPhantomNodes` | Degree-1 endpoint | Zero phantom nodes added |
| `PostProcessor_InterruptsPhantoms` | Edge line layer with `Interrupt` constraint at junction | Phantom nodes trimmed by post-processor; tangent still smooth |
| `StartEndFade_AdjustedForPhantoms` | Phantom nodes prepended | `StartEndFade[0]` equals phantom extension length |
| `Roundabout_PhantomFollowsRing` | Road connecting to roundabout ring | Phantom nodes curve along ring centerline |

**Visual tests (in BeamNG):**
- Degree-2: road↔bridge↔road — no gap, smooth curvature through bridge
- T-junction: side road meets through-road with continuous surface
- Roundabout connections: roads blend into ring (matching screenshot scenario)
- Width transitions: narrow road meeting wide road — smooth taper, no abrupt edge
- Drive-through test: no elevation bumps at any junction boundary

**Key files to modify/create:**

| File | Change |
|------|--------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/JunctionAdjacencyMap.cs` | **New** — junction adjacency lookup from network junctions |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/PhantomNodeGenerator.cs` | **New** — phantom node selection, sampling, and generation |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | Integrate phantom node generation into `GenerateForSpline()`, build adjacency map alongside continuity lookup |
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs` | Add `PhantomNodeCountStart`, `PhantomNodeCountEnd` properties |

---

## Implementation Order

### Phase A: Network-Chained Elevation Smoothing (Part 1)

```
A1. NetworkElevationGraph — graph construction from junctions
A2. Chain builder — greedy longest-path with junction-awareness + roundabout exclusion
A3. Chain-aware elevation filtering — concatenate (with dedup), filter, write-back
A3a. Bridge/tunnel elevation inclusion — remove early skip, smooth through structures
A4. Verify WI-6 anchoring — confirm no change needed (Endpoint type filter already correct)
A5. Chain-aware slope constraint — full chain array, exemption at chain boundaries only
A6. Chain-aware re-smooth iterations — reuse chain structure for iterations 1+
A7. Debug diagnostics — ChainId/ChainIndex on cross-sections, chain length statistics
A8. Unit tests — NetworkElevationGraphTests, ChainElevationFilteringTests, ChainAnchoringTests
A9. Switch disableSplineMerging default to true
A10. Integration test with Kattenes area
```

**Expected outcome:** Correct junction topology (no wrong merges) with smooth elevation profiles comparable to current merged-spline quality. Bridge/tunnel splines have valid elevation data for ramp generation.

### Phase B: Phantom Node DecalRoad Continuity (Part 2)

```
B0. JunctionAdjacencyMap — build lookup from network.Junctions
B1. PhantomNodeGenerator — core algorithm: select adjacent spline,
    sample cross-sections, compute laterally offset nodes, prepend/append
B2. Integrate into GenerateForSpline — add phantom nodes to all layers
    uniformly using the existing position * 0.5 * roadWidth formula
B3. startEndFade auto-adjustment for phantom regions
B4. PhantomNodeCount metadata on GeneratedDecalRoad
B5. Verify post-processor compatibility — phantom nodes get interrupted
    correctly for marking layers, survive for surface layers
B6. Visual testing across junction types:
    - Degree-2: road↔bridge↔road transitions
    - T-junction: side road into through-road
    - Multi-way: roundabout connections, Y-junctions, crossroads
    - Width transitions: narrow road meeting wide road
B7. Evaluate results — if satisfactory, done. If not, Plan B (merge).
```

**Expected outcome:** No visible gaps or tangent kinks between road splines at any junction type. Smooth width transitions. Existing junction interruption system continues to work for marking layers.

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Chain builder picks wrong through-road at ambiguous junctions | Medium | Low — only affects smoothing, not topology | Conservative: don't chain through ambiguous junctions; let blender handle |
| Short unchained splines still get poor elevation | Low | Medium | Fallback: blend short splines toward junction-constrained elevations |
| Phantom node overlap causes z-fighting at junctions | Medium | Medium | `startEndFade` blends the extension tip; if insufficient, Plan B (merge DecalRoads) eliminates overlap entirely |
| Phantom nodes from wrong adjacent spline at multi-way junction | Low | Medium — visual kink | Conservative scoring: deflection angle + road type + width ratio; worst case is suboptimal curvature, not broken geometry |
| Width interpolation not smooth enough through Catmull-Rom 4th component | Low | Medium | Catmull-Rom naturally interpolates width; if abrupt, increase phantom node count for longer taper zone |
| Banking mismatch at junction fill boundaries | Medium | High — visible bumps | Phantom nodes use adjacent spline's cross-section data (elevation, normal, width) — banking is inherited naturally |
| PNG skeleton pipeline regression | Low | Medium | PNG pipeline doesn't use OSM merging; chains work on any spline source |
| Bridge/tunnel elevation inclusion causes terrain painting regression | Low | Low | `IsExcluded` flag unchanged; Phase 4 and DecalRoad gen already respect it |
| Re-smooth iteration divergence with chain-based filtering | Low | Medium | Chain structure is immutable across iterations; same concatenation order guarantees stability |

## Notes

- The `NodeBasedPathConnector` and `RouteRelationAssembler` code remain in the codebase but are bypassed when `disableSplineMerging=true`
- Roundabouts already work as closed-loop splines with their own harmonization (Phase 2.6) — this plan doesn't change roundabout handling
- The PNG skeleton pipeline uses `MedialAxisRoadExtractor` which is independent of OSM merging — chains work on skeleton-extracted splines too
- OSM2World reference code is at `examples_for_ai/OSM2World/` — useful for future junction polygon work if Plan B (merge) or explicit junction fill is needed
- BeamNG DecalRoad uses Catmull-Rom spline interpolation (`geometry.lua` line 184). `startTangent`/`endTangent` are scalar floats (magnitude only, not direction) — cannot solve directional tangent mismatches. Phantom nodes are the correct approach for G1 continuity.
- Yu 2019 (KTH thesis, "OSM-Based Automatic Road Network Geometry Generation in Unity") describes edge-line intersection for junction polygon computation, inscribed-circle buffer areas for smooth turning corners (equations 3.5-3.9), and clockwise road ordering via cross product (Algorithm 2). These techniques are available as future enhancements if polygon-based junction fill is needed beyond the phantom node approach.

---

## Part 1 Implementation Summary (2026-03-25)

**Commit:** `a76c4e3` on `feature/relation-protected-junction-blocking`
**Steps completed:** A1–A8 (graph, chains, filtering, bridge/tunnel inclusion, anchoring verification, slope constraint, re-smooth, diagnostics, tests)
**Steps remaining:** A9 (disableSplineMerging default), A10 (Kattenes integration test)

### New Files

| File | Description |
|------|-------------|
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs` | Elevation graph + greedy chain builder. `ElevationNode`, `ElevationEdge`, `ElevationChain` data structures. Roundabout exclusion, width ratio 2:1 check, 30° deflection limit, ambiguous junction blocking. Chain statistics logging. |
| `BeamNgTerrainPoc.Tests/Elevation/RoadNetworkTestHelpers.cs` | Synthetic network builders: `CreateParameterizedSpline`, `AddSplineWithCrossSections`, `BuildNetworkWithJunctions`, `CreateFlatHeightmap`, `CreateStepHeightmap`, `CreateSlopeHeightmap`, `RunChainSmoothing`. |
| `BeamNgTerrainPoc.Tests/Elevation/NetworkElevationGraphTests.cs` | 10 xUnit tests: linear chain, T-junction, through-road, roundabout exclusion, dead end, bridge inclusion, parallel edges, width mismatch, ambiguous junction, disconnected spline. |
| `BeamNgTerrainPoc.Tests/Elevation/ChainElevationFilteringTests.cs` | 9 xUnit tests: dedup, smooth across boundary, chain vs per-spline comparison, reversed spline, bridge elevation with IsExcluded, slope constraint, short spline context, ChainId/ChainIndex, re-smooth iteration. |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs` | Added `ChainId` (int, default -1) and `ChainIndex` (int, default -1) diagnostic properties for 3D viewer color-coding. |
| `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` | Added `CalculateChainElevations()` — samples heightmap + filters + slope constraint on full chain. Added `ReSmoothChainFromExistingElevations()` — re-smooth on chains for iterations 1+. Added `ConcatenateChainCrossSections()` — dedup at joints with `DedupPairs` tracking. Added `PropagateToDeduped()` — copies elevation to skipped cross-sections after filtering. |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | Replaced per-spline `CalculateNetworkElevations` with chain-based processing. Added `_cachedElevationChains`/`_cachedElevationGraph` fields (built once, reused across iterations). Bridge/tunnel splines now get `IsExcluded=true` marked but elevation is computed (no early `continue`). Roundabout splines processed per-spline as before (excluded from chains). Legacy fallback retained for non-`OptimizedElevationSmoother` implementations. |

### Bug Found and Fixed During Implementation

**Cross-section dedup propagation:** When concatenating chain segments, co-located endpoint cross-sections at spline joints are deduped (skipped) to avoid duplicate samples in the filter. The skipped cross-section's `TargetElevation` remained `NaN` because it was never in the filtered array. Fixed by tracking dedup pairs in `ElevationChain.DedupPairs` and calling `PropagateToDeduped()` after filtering to copy elevation from the kept neighbor.

### Key Design Decisions Made During Implementation

1. **`ElevationChain.DedupPairs`** — stored on the chain object rather than returned separately from concatenation, keeping the API clean while enabling post-filtering propagation.
2. **Cached chains across iterations** — `_cachedElevationChains` built once on iteration 0, reused for re-smooth iterations 1+. Cleared at the start of each `SmoothAllRoads()` call.
3. **Highest-priority spline parameters** — each chain uses the smoothing parameters from its highest-priority edge, since chains may span splines with different material configs.
4. **WI-6 anchoring unchanged** — confirmed that `BuildEndpointAnchorLookup` only anchors `JunctionType.Endpoint` (dead-end) junctions. Chain-internal degree-2 nodes are never `Endpoint` type, so no code change was needed.

### Test Results

All 19 new elevation tests pass (23 total including pre-existing tests in the project). Tests cover:
- Graph construction and chain building correctness
- Elevation filtering produces smooth profiles across spline boundaries
- Chain-based filtering outperforms per-spline filtering at joints
- Bridge splines get valid elevation while remaining `IsExcluded=true`
- Slope constraint operates on full chain without kinks at joints
- Short splines benefit from chain context (lower variance)
- Re-smooth iterations maintain chain-based smoothness

---

## Part 1 Bug Fixes — Bridge Elevation Chaining (2026-03-26)

**Commit:** `a566b6e` on `feature/relation-protected-junction-blocking`

### Bugs Found and Fixed

Three bugs causing elevation bumps/steps at bridge/tunnel boundaries when `disableSplineMerging=true`:

#### Bug 1: Bridge Identity Lost in OsmGeometryProcessor Step 6

**Root cause:** `OsmGeometryProcessor.ConvertLinesToSplines()` Step 6 (line 939) hardcoded `IsBridge=false, IsTunnel=false` for ALL regular (non-protected) paths. When `excludeBridges=false` with `disableSplineMerging=true`, bridge paths go through Step 6 (not Step 5) because they are not "protected structures" — but their identity was erased.

**Fix:** Step 6 now preserves `pm.IsBridge`, `pm.IsTunnel`, `pm.StructureType`, `pm.Layer`, `pm.BridgeStructureType` from the `PathWithMetadata`, matching Step 5's behavior.

**File:** `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` (lines 939-952)

#### Bug 2: Elevation Chaining Broken Without Junction Detection

**Root cause:** `NetworkElevationGraph.BuildFromNetwork()` created **separate synthetic nodes** for each spline endpoint not mapped to a junction. When `shouldHarmonize=false` (Phase 1.8 junction detection skipped), ALL endpoints got separate synthetic nodes → no shared nodes → no chaining → every spline became its own 1-segment chain → per-spline smoothing → elevation bumps at boundaries.

Even with `shouldHarmonize=true`, spline endpoints that the junction detector missed (edge cases) would also fail to chain.

**Fix:** Added `FindOrCreateNode()` method that reuses existing nodes within a 2m tolerance. When a synthetic node would be created, it first checks if an existing node (junction-based or synthetic) is already at that position. Co-located spline endpoints now share nodes regardless of whether junction detection ran.

**Tolerance choice:** 2m is appropriate — covers OSM GPS imprecision (~1m) and projection artifacts while being well below minimum road separation that should stay distinct. A test verifies 3m-apart endpoints are NOT clustered.

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs` (lines 220-243)

#### Bug 3: Chain Concatenation Spatial Zigzag

**Root cause:** When `ExtendChain()` extended backward (prepending segments at position 0), the `traverseReversed` flag was computed identically to forward extension. This produced wrong cross-section ordering during concatenation: a backward-inserted segment's CS went End→Start instead of Start→End, creating a spatial U-turn zigzag.

**Example:** Chain `[road1(reversed), bridge, road2]` with road1 at x=10→100, bridge at x=100→200:
- **Before fix:** CS spatial order: `[100→10, 100→200, 200→290]` — jump from x=10 back to x=100
- **After fix:** CS spatial order: `[10→100, 100→200, 200→290]` — continuous flow

The zigzag was hidden in tests with flat/step heightmaps (smoothing averaged out the discontinuity) but produced a 9.5m elevation gap with a valley heightmap.

**Fix:** For backward-inserted segments, invert `traverseReversed`. Forward extension: CS must START at the connection point. Backward extension: CS must END at the connection point. The spatial requirement is opposite.

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkElevationGraph.cs` (lines 304-321)

### New Test Files

| File | Tests | Description |
|------|-------|-------------|
| `BeamNgTerrainPoc.Tests/Osm/OsmBridgeIdentityTests.cs` | 4 | Bridge/tunnel identity preserved through Step 6 with `excludeBridges=false`, tunnel variant, metadata preservation, backward-compat with `excludeBridges=true` |
| `BeamNgTerrainPoc.Tests/Elevation/BridgeElevationChainingTests.cs` | 7 | Co-located endpoints chain without junctions, bridge chains with adjacent roads, valley heightmap produces smooth profile, backward extension spatial coherence, very short 2-node bridge, negative cases (non-co-located stay separate, tight tolerance) |

### Test Results

All 181 tests pass (11 new + 170 existing) with zero regressions.

---

## Known Issue: Layer-Unaware Junction Detection at Bridge Crossings

**Status:** Not yet fixed — deferred to bridge mesh implementation phase.
**Discovered:** 2026-03-26 during bridge elevation debugging.

### Problem

`NetworkJunctionDetector` has no vertical layer awareness. When a ground-level road (layer=0) passes under a bridge (layer=1), the detector creates false junctions:

1. **Mid-spline crossing** (`DetectMidSplineCrossings`): bridge mid-spline and crossing road mid-spline are spatially close in 2D → false `MidSplineCrossing` junction created at the overlap point
2. **T-junction** (`DetectTJunctions`): crossing road's mid-spline found near an existing junction that the bridge participates in → crossing road added as continuous contributor

**Effect:** False junctions inflate the degree of bridge endpoint nodes from 2 to 3+, which:
- Breaks chain extension (ambiguous candidates at degree-3+ nodes)
- Causes Phase 3 harmonization to blend bridge-level and ground-level elevations → bumps

### Why Not Fixed Yet

The fix requires careful design because **endpoint clustering MUST still allow different-layer connections** — a road→bridge ramp is a real physical connection at a shared OSM node, even though road is layer=0 and bridge is layer=1. Only mid-spline proximity detections (T-junction and mid-spline crossing) should filter by layer.

An initial implementation was attempted and reverted because it was too aggressive — it blocked road→bridge endpoint junctions, breaking chain connectivity.

### Planned Fix (for bridge mesh phase)

When bridge meshes are implemented, new junction system rules will be needed anyway. The layer-aware fix should be part of that work:

1. **Endpoint clustering (`ClusterEndpointsIntoJunctions`):** NO layer filter — shared OSM nodes are physically connected across layers
2. **T-junction detection (`DetectTJunctions`):** Skip adding a spline as continuous contributor if its layer differs from ALL existing endpoint contributors in the junction
3. **Mid-spline crossing detection (`DetectMidSplineCrossings`):** Skip creating crossings between splines at different layers (`spline.Layer != otherSpline.Layer`)
4. **Elevation graph `FindOrCreateNode`:** Optionally add layer-awareness to prevent clustering of synthetic nodes from different layers (needs investigation — may not be needed if junction detection is fixed)
