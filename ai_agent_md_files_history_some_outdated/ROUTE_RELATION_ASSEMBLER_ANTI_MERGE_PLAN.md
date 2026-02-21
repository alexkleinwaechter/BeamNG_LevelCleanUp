# Route Relation Assembler — Anti-Merge Improvements

## Context

`RouteRelationAssembler` (Tier 0) pre-assembles OSM ways that belong to the same route
relation (e.g. D 914, A1, B51) into longer splines before the general `NodeBasedPathConnector`
runs. It intentionally skips junction-valence checks because the relation ordering should be
authoritative.

**Problem**: The assembler merges ways that share a node but should NOT be connected — they
are separate carriageways of a divided road meeting at a divergence/convergence point.

## Discovered Problem Cases

### Case 1: Roundabout entry + exit (FIXED — partially)

**Example**: Ways 637699876 (exit) and 637699877 (entry) near Cerbère, France.
Both are `highway=primary, oneway=yes, ref=D914, name=Avenue de la Côte Vermeille`.
They share node **1804313643** which sits on the roundabout ring.

- Way 637699876: `[1804313758 → 1804313643]` — exits roundabout southbound
- Way 637699877: `[1804313643 → 1804313667]` — enters roundabout northbound

Merged result was a U-turn: roundabout → away → back to roundabout.

**Current fix**: U-turn angle check (`dot < -0.7`, i.e. >135°) guarded by
`AreBothOneway()`. This works for roundabouts but see open issues below.

### Case 2: 2-to-4-lane road splits (TODO)

Where a two-lane road widens to a 4-lane divided road, the single way splits into
two oneway ways at a divergence node. Same pattern: two `oneway=yes` ways share a
node with a sharp angle reversal. The current `AreBothOneway + angle` check should
already handle some of these, but needs verification with real data.

**Concern**: Short transition segments between the split point and the main road may
have gentle angles (<135°) that slip through the angle check.

## Available OSM Data Signals for Anti-Merge

Ranked by reliability and availability:

| Signal | Description | Availability | Strength |
|--------|-------------|--------------|----------|
| **Roundabout node membership** | Shared node belongs to a `junction=roundabout` way | Already in our data via `RoundaboutDetector` | **Strongest** for roundabout cases |
| **`oneway` + U-turn angle** | Both ways one-way + angle > 135° at connection | Always available (current impl) | **Good** for divided roads |
| **Turn restrictions** | `type=restriction` relations with `no_u_turn` | Requires extra Overpass query | Strong but sparse |
| **`destination` / `destination:ref`** | Different destination signs on diverging ways | Common on motorways, rare on secondaries | Strong where present |
| **`dual_carriageway=yes`** | Explicit divided-road tag | Rare in practice | Perfect but uncommon |
| **`placement=*`** | Indicates which side of road the centerline represents | Very rare | Perfect but uncommon |
| **Node valence (full index)** | Pass node→way counts into assembler | Requires plumbing changes | Good soft signal |

## Implementation Plan

### Phase 1: Roundabout node guard (NEXT)

Add a fast lookup: collect all node IDs that belong to `junction=roundabout` ways.
If the shared connection node is a roundabout node, **always reject** the merge
regardless of angle or oneway status.

**Data flow**:
- `RoundaboutDetector` already identifies roundabout ways and their node lists
- Build a `HashSet<long> roundaboutNodeIds` from those ways' node arrays
- Pass it into `PreAssembleByRouteRelation()` and down to the `TryXXX` methods
- Check: if connection node is in the set → return null (no merge)

**Why this is better than angle-only**:
- Zero false positives (a node on a roundabout ring is never a legitimate merge point)
- No angle threshold tuning
- Works even for gentle-angle roundabout approaches

**Keep the oneway + angle check as a secondary guard** for non-roundabout divergence
points (2-to-4-lane splits, motorway ramp forks).

### Phase 2: Investigate 2-to-4-lane splits (FUTURE)

- Collect real-world examples where the current check fails
- Consider adding `lanes` tag change detection (single way with `lanes=2` splitting
  into two `oneway=yes` ways)
- Consider node valence as additional signal
- May need to check if the shared node has ways going in 3+ distinct directions

### Phase 3: Motorway ramp divergence (FUTURE)

- `highway=motorway_link` ways splitting from `highway=motorway`
- These are already type-incompatible in `NodeBasedPathConnector` but may share
  a route relation (e.g. national route continuing via motorway and link)
- May need special handling in route relation assembly

## Tier Architecture Analysis: Do the Tiers Really Work Together?

### Overview of the 4-Tier Pipeline

The merging logic runs sequentially in `OsmGeometryProcessor.cs`:

```
Step 3: RouteRelationAssembler.PreAssembleByRouteRelation(paths, routeRelations)
         → mutates the path list (merges consumed ways, returns leftovers)
Step 4: NodeBasedPathConnector.Connect(paths, tolerance)
         → runs Tier 1, 2, 3 in a loop until no more merges
```

| Tier | Class | Merge Criterion | Valence Check | Anti-Merge Guards |
|------|-------|----------------|---------------|-------------------|
| 0 | RouteRelationAssembler | Route relation ordering + shared node | **No** (trusts relation ordering) | Oneway + U-turn angle |
| 1 | NodeBasedPathConnector | Shared OSM node ID at endpoints | **Yes** (valence >= 3 blocks) | Highway type, conflicting names |
| 2 | NodeBasedPathConnector | Same name/ref + proximity <= tolerance | **No** | Highway type, conflicting names, oneway + U-turn angle |
| 3 | NodeBasedPathConnector | Proximity only (requires >=1 null node ID) | **No** | Highway type, conflicting names |

### The Fundamental Design Flaw: Tiers Don't Communicate Rejections

Inside `NodeBasedPathConnector.Connect`, for each pair of paths, the tiers are tried
sequentially:

```csharp
var tier1Result = TryMergeByNodeId(path1, path2, nodeValence);      // may look at shared node
if (tier1Result != null) { merge; break; }

var tier2Result = TryMergeBySameNameAndProximity(path1, path2, ...); // ignores Tier 1's decision
if (tier2Result != null) { merge; break; }

var tier3Result = TryMergeByProximityWithCropped(path1, path2, ...); // ignores Tier 1's decision
if (tier3Result != null) { merge; break; }
```

**The problem**: `TryMergeByNodeId` returns `null` for two completely different reasons:
1. **"Not applicable"** — the paths don't share a node ID (no opinion)
2. **"Explicitly rejected"** — the paths DO share a node but it's a junction (valence >= 3)

Both return `null`. Tier 2 has no way to distinguish between the two. When Tier 1
explicitly rejects a pair because their shared node is a junction, Tier 2 happily
merges them anyway because they share the same `name` or `ref` tag and happen to be
geometrically close.

**This is exactly what happened in the Cerbère bug**:
- Ways 637699876 and 637699877 share node 1804313643 (valence 3)
- Tier 1 correctly says: "shared node, but it's a junction → reject"
- Tier 2 says: "same name + ref, distance = 0 → merge!" — undoing Tier 1's protection

### Is Tier 2 Reached at All? Yes, Frequently

Tier 2 exists for a legitimate reason: OSM ways that form a continuous named road but
were split at points where they DON'T share a node ID in our data. This happens when:
- A way was cropped at the terrain boundary (loses one node ID)
- Two ways were split at a point that ended up with slightly different coordinates
  due to terrain projection
- An intermediate way was filtered out (e.g. a bridge segment was separated), leaving
  gaps in the node chain

Without Tier 2, named roads would be left as disconnected fragments. The console output
typically shows `Tier2/Name+Prox: N` with N > 0 for most areas, confirming it's active.

### Does Tier 0 (RouteRelationAssembler) Undermine Tier 1?

Partially. Tier 0 intentionally skips valence checks because route relations provide
explicit ordering. The assumption is: "if the relation says way A comes before way B,
they should merge even at a 3-way junction."

This is mostly correct — route relations are curated by mappers and generally reliable.
But:
- **A route may include both carriageways** of a divided road. For example, the D 914
  relation near Cerbère includes both the exit and entry ways. The relation ordering
  alone doesn't distinguish "continuation" from "opposite carriageway."
- After Tier 0 merges what it can, the remaining unmerged ways still have their original
  `StartNodeId`/`EndNodeId`. If Tier 0 fails to merge them (because our U-turn guard
  blocked it), they flow into Tier 1/2/3 with intact node IDs — no information is lost.

**So Tier 0 currently plays well with Tiers 1-3**: it pre-merges what it can, and
leftovers are correctly handled by the later tiers.

### What Should Be Fixed

**Option A: Explicit rejection propagation** (clean but more work)

Make `TryMergeByNodeId` return a 3-state result:
```csharp
enum MergeDecision { Merged, NoMatch, ExplicitlyRejected }
```

If `ExplicitlyRejected`, skip Tier 2 and 3 for this pair. This ensures that Tier 1's
junction detection is never bypassed by lower tiers.

**Option B: Junction blacklist** (simpler)

Before the merge loop, build a set of path-pair endpoint combinations that share a
junction node. Tier 2 and 3 check this set before merging:
```csharp
// If these two endpoints share a junction node, don't merge them by proximity either
if (shareJunctionNode(path1.EndNodeId, path2.StartNodeId, nodeValence))
    return null;
```

**Option C: Duplicate the valence check in Tier 2** (pragmatic, low-risk)

Add the junction valence check directly into `TryMergeByProximity`. Before accepting
a proximity merge, check: "do these endpoints have node IDs, and if so, is their shared
node a junction?" This is conceptually redundant with Tier 1 but prevents the bypass.

```csharp
// In TryMergeByProximity, before each merge:
if (path1.EndNodeId.HasValue && path2.StartNodeId.HasValue &&
    path1.EndNodeId.Value == path2.StartNodeId.Value &&
    IsJunctionNode(path1.EndNodeId.Value, nodeValence))
    return null;
```

**Recommendation**: Option C is the most pragmatic. It's a few lines in
`TryMergeByProximity`, it makes Tier 2 self-consistent (never merges at a known
junction), and it doesn't require restructuring the tier return types.

### Tier 3 Has the Same Vulnerability (Minor)

Tier 3 (`TryMergeByProximityWithCropped`) requires at least one null node ID, so it
can't bypass Tier 1 in the same way — if both endpoints have node IDs, Tier 3 won't
touch them. The only overlap is when one endpoint has a node ID and the other doesn't,
which is a different scenario (cropped path meeting a junction node). Low risk but
worth keeping in mind.

### Summary

| Question | Answer |
|----------|--------|
| Does Tier 2 undermine Tier 1? | **Yes** — Tier 1 rejects at junctions, Tier 2 re-merges same pair by name+proximity |
| Is Tier 2 needed? | **Yes** — handles legitimate gaps from terrain cropping and bridge separation |
| Does Tier 0 conflict with Tier 1? | **No** — Tier 0 pre-merges what it can, leftovers flow cleanly into later tiers |
| Does Tier 3 have the same problem? | **Mostly no** — the "at least one null node" requirement prevents most bypasses |
| Are the U-turn angle fixes sufficient? | **For now** — they catch divided carriageways but don't address the architectural flaw |
| Best fix? | Pass `nodeValence` map into `TryMergeByProximity` and check junction status there |

## Current Code State

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/RouteRelationAssembler.cs`

**Current guards in all four TryXXX merge methods**:
```csharp
if (AreBothOneway(path1, path2) &&
    pathX.Points.Count >= 2 && pathY.Points.Count >= 2 &&
    IsUturnAtConnection(prev, connection, next))
    return null;
```

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs`

**CRITICAL FIX**: The actual merge for the Cerbère roundabout case happened in
`NodeBasedPathConnector.TryMergeByProximity` (Tier 2: same name/ref + proximity),
NOT in `RouteRelationAssembler`. This was because:
- Tier 1 (shared node ID) correctly blocked the merge due to junction valence >= 3
- Tier 2 (same name/ref + proximity) had NO valence check and NO U-turn check
- Both ways share `name=Avenue de la Côte Vermeille` and `ref=D 914`
- Their endpoints at node 1804313643 are at distance 0

**Fix applied**: Added `AreBothOneway + IsUturnAtConnection` guard to all 4 endpoint
combinations in `TryMergeByProximity`. Same logic as RouteRelationAssembler.

**Shared helper methods** (duplicated in both classes, could be extracted):
- `AreBothOneway()` — checks `oneway` tag on both paths (yes/true/1/-1)
- `IsOneway()` — checks a single tag dictionary for oneway values
- `IsUturnAtConnection()` — dot product < -0.7 (angle > ~135°)
