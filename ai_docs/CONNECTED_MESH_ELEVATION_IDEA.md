# Connected Road Mesh — A Fundamentally Different Elevation Approach

## Motivation: Why the Current Approach Struggles

### The "Smooth Then Reconcile" Problem

The current pipeline works in two conceptually separate phases:

1. **Phase 2**: Each spline is smoothed independently — box/Butterworth filter, slope constraint, global leveling. Each spline gets a beautiful, smooth elevation profile in isolation.
2. **Phase 3**: `NetworkJunctionHarmonizer` discovers that where splines meet at junctions, their independently-computed elevations don't match. It then applies heuristic blending (priority-weighted averages, cosine interpolation, plateau smoothing) to reconcile the mismatch.

This is like building two highways in parallel, each with a perfect gradient, then discovering at the interchange that one is 2 meters higher than the other and trying to patch it with a ramp.

### Specific Failures of This Approach

**1. Single-pass reconciliation, no chain propagation**
When junction J1 modifies spline B's elevation to match spline A, and then junction J2 needs spline B's elevation to match spline C — J2 sees B's *already-modified* elevation, not B's natural profile. There's no iteration. In long chains (A→B→C→D→E), constraint propagation degrades as it passes through junctions.

**2. Heuristic blend zones fight each other**
The `blendDistance` (default 30m) determines how far junction corrections propagate along each spline. When two junctions are closer than 60m apart (common in urban areas), their blend zones overlap. The code handles this with a weighted-average accumulation capped at `totalInfluence = min(sum, 1.0)` — a heuristic that prevents over-correction but can under-correct.

**3. Priority winner-takes-all at T-junctions**
At T-junctions, the continuous (higher-priority) road "wins" and the terminating road must adapt. This produces good results for clear hierarchies (main road vs. side street) but fails for equal-priority roads or when the "wrong" road wins due to arbitrary priority assignment.

**4. Plateau smoothing is a post-hoc patch**
Multi-way junctions get a plateau smoothing step that biases toward the HIGHER elevation (70%/30% split) to reduce "dents." This overwrites what the propagation step computed. Two separate systems operating on the same cross-sections create unpredictable interactions.

**5. No topological reasoning**
The `UnifiedRoadNetwork` is a flat list of splines + flat list of junctions. There is **no graph/adjacency structure** — no way to ask "which junctions does spline X connect to?" or "what's the shortest path through the network from A to B?" Connectivity is discovered ad-hoc via spatial proximity queries.

### The "Avenue du Professeur Henri Mary" Case (Way 52033007)

This way is a **closed loop** — node 663313771 is both its start and end node. It's a `oneway=yes` divided carriageway that forms a tight U-turn. The road geometry is *correct* — the physical road really does curve back 180°.

But the per-spline smoothing approach struggles because:
- Cross-sections on opposite sides of the U-turn are far apart along the spline (say, 200m of road distance) but geographically close (maybe 10m apart)
- The smoothing window (101 cross-sections × 0.5m = ~50m) averages sequentially — it has no concept of geographic proximity between distant-along-spline cross-sections
- Banking calculations at the tight apex produce extreme values
- If this spline meets other roads at various points along its loop, junction harmonization must reconcile elevations at multiple points with no awareness of the loop topology

**This case illustrates that even _within_ a single spline, the sequential smoothing model breaks down when road geometry is non-trivial.**

---

## The Connected Mesh Idea

### Core Concept

Instead of smoothing each spline independently and then reconciling at junctions:

> **Build the entire road network as a single connected graph and solve elevation globally.**

Think of it this way: lay all the splines flat on a 2D plane. They meet at shared junction nodes. Now drape this flat network onto the terrain like a rubber sheet. The elevation of every point on every road is determined *simultaneously* by a single optimization that respects:

1. The terrain height below each road segment (soft constraint: "follow the terrain")
2. Smoothness along each road segment (hard constraint: "no sudden bumps")
3. **Shared elevation at junctions** (hard constraint: "roads meet cleanly")
4. Maximum slope limits (inequality constraint: "roads aren't too steep")

Junction continuity is **guaranteed by construction** — because junction nodes are shared vertices in the graph, they can never have different elevations on different roads. There is nothing to reconcile.

### Mental Model: Rubber Bands on a Landscape

Imagine each road spline as a rubber band laid across a landscape contour model:
- Each band wants to follow the terrain (gravity/attachment)
- Each band resists sharp bends (elasticity)
- Where bands are pinned together at junctions, they share the same pin height
- The system reaches equilibrium when total energy is minimized

The "elasticity" of each band is controlled by smoothing parameters per material — a highway has stiff rubber (smooth, gentle gradients), a mountain trail has flexible rubber (follows terrain closely).

### What Changes Architecturally

| Current ("Smooth Then Reconcile") | Proposed ("Connected Mesh") |
|---|---|
| Phase 2: Per-spline elevation smoothing | Graph construction + global elevation solve |
| Phase 3: Junction detection + harmonization (1800 lines of heuristics) | *Eliminated entirely* — continuity is structural |
| No adjacency data structure | Explicit road graph with junction nodes |
| Sequential processing, single pass | Iterative solver converges globally |
| Priority determines "winner" at junctions | Priority becomes weighting in the energy function |
| Blend zones with heuristic caps | Smooth transitions emerge naturally from optimization |
| Plateau smoothing as post-hoc patch | Flat junction areas emerge if that's the energy minimum |

---

## Technical Approaches

### Approach A: Iterative Relaxation (Gauss-Seidel on a Graph)

**Concept**: Repeatedly visit each junction node, compute a weighted average of the terrain-constrained elevations of all incident road segments at that node, update the junction elevation, then re-smooth the affected segments. Repeat until convergence.

**Algorithm**:
```
1. Build road graph: nodes = junctions, edges = road segments between junctions
2. Initialize: smooth each edge independently (current Phase 2)
3. For each iteration (until convergence):
   a. For each junction node J:
      - Collect the elevation that each incident edge "wants" at J
        (based on its terrain-following profile)
      - Set J.elevation = weighted average (weight by priority, road width)
   b. For each edge:
      - Re-smooth with boundary conditions: h(startJunction) = fixed, h(endJunction) = fixed
      - Use current smoothing filters but anchored at junction constraint elevations
4. Converged when max |elevation change| < threshold (e.g., 0.01m)
```

**Pros**: Simple to implement, reuses existing smoothing code, easy to debug
**Cons**: May converge slowly for long chains, not guaranteed to find global optimum
**Estimated convergence**: 5–15 iterations for typical networks (sparse, mostly chains)

### Approach B: Global Linear Solve (Sparse Matrix)

**Concept**: Formulate the entire elevation problem as a linear system Ax = b where x is the vector of all cross-section elevations across the entire network.

**Energy to minimize**:

$$E = \sum_{\text{edges}} \left[ \alpha \int \left(\frac{d^2h}{ds^2}\right)^2 ds + \beta \int (h(s) - h_{\text{terrain}}(s))^2 ds \right]$$

Subject to: $h_i(J) = h_j(J)$ for all edges $i, j$ meeting at junction $J$

Where:
- $\alpha$ = smoothness weight (per material — high for highways, low for trails)
- $\beta$ = terrain-following weight (per material)
- $h(s)$ = road elevation at distance $s$ along road
- $h_{\text{terrain}}(s)$ = sampled terrain elevation

Discretized over cross-sections, this becomes a sparse banded system. The second derivative $d^2h/ds^2$ becomes a finite-difference stencil `h[i-1] - 2h[i] + h[i+1]`, and the junction constraints link cross-sections from different edges.

**Pros**: Finds the true global optimum, handles all junctions simultaneously, mathematically principled
**Cons**: More complex to implement, requires sparse matrix library, harder to add nonlinear constraints (slope limits need iterative projection)

**Scale estimate**: A typical terrain (2048×2048) might have 200 splines × 500 cross-sections = 100,000 unknowns. A sparse banded system of this size solves in <100ms with conjugate gradient. This is **very feasible**.

### Approach C: Diffusion-Based (Heat Equation on Road Graph)

**Concept**: Start with the independently-smoothed per-spline elevations (current Phase 2 output). Then run an iterative "diffusion" process that propagates elevation information through junction nodes.

**Algorithm**:
```
1. Initialize elevations as current Phase 2 output
2. For each iteration:
   For each junction node J:
     target = weighted_average(incident_edge_elevations_at_J)
     correction = target - current_J_elevation
     For each incident edge:
       Apply correction * decay_factor along the edge
       (decay_factor decreases with distance from J)
3. Re-apply smoothing filters after diffusion (preserve smoothness)
4. Repeat until corrections < threshold
```

**Pros**: Incremental improvement over current system (starts from same initial state), very stable, easy to tune
**Cons**: Not guaranteed to find optimal solution, convergence depends on decay factor tuning

### Approach D: Hybrid — Graph-Aware Iterative Smoothing

**Concept**: The most pragmatic approach. Instead of replacing Phase 2 entirely, enhance it:

1. **Build the explicit road graph** (this is needed regardless of approach)
2. **Phase 2 runs as today** — per-spline smoothing gives initial elevations
3. **Replace Phase 3** with iterative constraint propagation:
   - For each junction: compute target elevation from highest-priority incident road's natural profile
   - Re-smooth each edge as a boundary-value problem: "match the terrain between these two junction elevations"
   - Repeat 3–5 times until stable
4. **No junction harmonizer needed** — the boundary-value re-smoothing automatically creates smooth transitions

This keeps the existing smoothing infrastructure but uses it differently — as a boundary-value solver instead of a free-form smoother.

---

## What Gets Simpler vs. Harder

### Eliminated (Currently ~3000 lines of code)

- `NetworkJunctionHarmonizer` (1789 lines) — entirely gone
- `NetworkJunctionDetector` (1597 lines) — replaced by graph topology (junctions are nodes, not discovered by spatial proximity)
- `JunctionSurfaceCalculator` (331 lines) — surface matching is handled by shared vertices
- Plateau smoothing — no longer needed; flat junctions emerge naturally
- Blend zone overlap handling — no blend zones; smooth transitions from boundary-value solving
- Priority winner-takes-all logic — replaced by energy function weighting

### New Components Required

| Component | Complexity | Purpose |
|-----------|-----------|---------|
| `RoadGraph` data structure | ~200 lines | Nodes (junctions) + edges (segments) + adjacency lists |
| Graph builder from spline endpoints | ~300 lines | Cluster endpoints into junction nodes, create edges |
| Boundary-value elevation solver | ~400 lines | Smooth one edge with fixed elevations at both ends |
| Iterative convergence loop | ~200 lines | Repeat until stable, with convergence check |
| *Total new* | **~1100 lines** | vs. ~3700 lines eliminated |

### Gets Harder

1. **Per-material parameter differentiation**: Currently each spline has independent smoothing parameters. In a connected mesh, a junction between a highway and a residential road must decide whose smoothing stiffness to use. Solution: use the stiffer material at the junction, transition smoothly along the connecting edge.

2. **Bridge/tunnel segments**: Protected structure paths that bypass merging must also bypass the global solve — their elevations are set by `StructureElevationCalculator`. They become fixed-elevation boundary conditions in the system, not free variables.

3. **Roundabouts**: Currently handled by `RoundaboutElevationHarmonizer` as uniform-elevation rings. In the graph model, a roundabout is a cycle. The solver may need a special constraint: "all nodes on this cycle have the same elevation within ε."

4. **Banking at junctions**: Currently banking is calculated per-spline and then the junction harmonizer sets `ConstrainedLeftEdgeElevation`/`ConstrainedRightEdgeElevation`. In the connected mesh, where two roads of different widths meet, the lateral profile must transition. This is genuinely harder and may need a separate lateral-profile solve.

---

## Impact on the "Avenue du Professeur Henri Mary" Problem

This case has a **closed loop** — the road starts and ends at the same node. In the graph model:

- Node A = junction at node 663313771 (where the loop starts/ends and connects to other roads)
- Edge E = the entire loop road, with A as both its start and end node (a self-loop)
- The boundary-value problem becomes: smooth elevation along E such that h(start) = h(end) = h(A)

This is **perfectly well-defined** — it's a periodic boundary condition. The smoother finds the elevation profile along the loop that:
1. Follows the terrain
2. Is smooth (minimal second derivative)
3. Starts and ends at the same height

No U-turn detection needed. No junction reconciliation needed. The loop's elevation profile is continuous by construction. If other roads connect to nodes along the loop, those become additional junction constraints that the solver handles simultaneously.

---

## Implementation Feasibility Assessment

### Scale Numbers

| Metric | Typical Map | Large Map |
|--------|------------|-----------|
| Splines | 100–300 | 500–1000 |
| Junctions (graph nodes) | 50–150 | 200–500 |
| Cross-sections | 20k–80k | 100k–300k |
| Solver iterations (Approach A/D) | 5–15 | 10–30 |
| Time per iteration | ~50ms | ~200ms |
| Total solve time | 0.25–0.75s | 2–6s |

Current Phase 3 (junction harmonization) takes comparable time for large maps. The global solve would not be slower.

### Required Infrastructure Changes

1. **Road graph data structure**: Add `RoadGraph` class to `UnifiedRoadNetwork` with adjacency lists. Populate during `BuildNetwork()` by clustering spline endpoints.

2. **Elevation solver refactor**: `OptimizedElevationSmoother` needs a new mode: "smooth this edge with boundary conditions h(start)=A, h(end)=B" in addition to current "smooth this edge freely." The existing Butterworth/box filters can be adapted — inject boundary values before filtering and pin them after.

3. **Remove Phase 3**: Delete `NetworkJunctionHarmonizer`, `NetworkJunctionDetector`, `JunctionSurfaceCalculator`, and related junction detection in `UnifiedRoadSmoother`.

4. **Bridge/tunnel integration**: Structure paths become fixed-elevation edges. `StructureElevationIntegrator` provides boundary conditions at structure endpoints that feed into the graph solve.

5. **Banking**: The existing `BankingOrchestrator` can run after elevation solve with no changes — banking depends on curvature (geometry, unchanged) and center elevation (now globally optimized), so edge elevations are better by construction.

### Risk Assessment

| Risk | Mitigation |
|------|------------|
| Regression in working cases | Run side-by-side on test maps before switching; keep old code behind feature flag |
| Nonlinear constraints (slope limits) hard in linear solver | Use iterative projection: solve linearly, clip slopes, re-solve. Converges in 2–3 rounds. |
| Dense urban grids may converge slowly | Start with current Phase 2 output as initial guess — already close to solution |
| Banking at junctions harder | Defer lateral profile transitions to Phase 2. Accept current banking at first |
| Roundabout cycles need special handling | Add "cycle elevation constraint" — average of all nodes on cycle, uniform within tolerance |

---

## Recommended Path Forward

### Phase 1: Build the Road Graph (Foundation)

Add `RoadGraph` to `UnifiedRoadNetwork` — this is useful regardless of which elevation approach we pick. It enables:
- Walking from junction to junction along splines
- Querying "which splines connect to this junction?"
- Topological reasoning for future features (route planning, signage, etc.)
- Better junction detection (topology-based instead of spatial-proximity)

**Estimated effort**: 2–3 days. Low risk. Can be added alongside existing code.

### Phase 2: Boundary-Value Smoothing Prototype (Approach D)

Implement the hybrid approach:
1. Phase 2 runs as today (initial estimation)
2. New iterative loop: extract junction elevations → re-smooth each edge with boundary conditions → repeat
3. Skip Phase 3 when new solver is active (feature flag)

**Estimated effort**: 5–7 days. Medium risk. Can A/B test against current output.

### Phase 3: Evaluate and Decide

Compare output quality on 3–5 test maps:
- Well-mapped area (clean OSM data, clear hierarchy)
- Dense urban grid (many junctions close together)
- Mountain roads (steep terrain, tight hairpins)
- Mixed priority (highway meets residential at multiple points)
- The Cerbère area (roundabouts, split carriageways, bridges)

If results are consistently better: remove old junction harmonization code.
If mixed: keep as configurable option with both paths available.

### Future: Full Global Solve (Approach B)

If the hybrid approach works well but we want even better results, upgrade to a sparse matrix solve. This is a drop-in replacement for the iterative loop — same graph, same constraints, mathematically optimal solution instead of iterative approximation.

---

## Summary

| Aspect | Current | Connected Mesh |
|--------|---------|---------------|
| Junction continuity | Reconciled post-hoc (fragile) | Guaranteed by construction (structural) |
| Smoothing scope | Per-spline independent | Global (entire network) |
| Chain propagation | Single pass, degrades | Iterative, converges globally |
| Code complexity | ~3700 lines of heuristics | ~1100 lines of solver logic |
| Tight curves/loops | Breaks (Ave. Henri Mary case) | Handles naturally (periodic BC) |
| Bridge/tunnel handling | Preserved (separate pipeline) | Preserved (fixed boundary conditions) |
| Implementation risk | N/A (current) | Medium — hybrid approach minimizes |
| Performance | Comparable | Comparable (both ~1s for typical maps) |
