# Refactor Plan — Merged-Corridor Bridges (the real continuity fix)

**Date:** 2026-06-08
**Branch:** `feature/bridges`
**Status:** PLAN / investigation. No code written yet.
**Supersedes the deferred option in:** `04-handoff-road-continuity-implementation.md` §6/§10,
`2026-06-06_bridge_road_continuity_followup.md` §6, `05-…-plan.md` §6 (the "virtual merged corridor"
that every prior doc parked as "later, larger scope"). The user has now chosen to do it.
**Reads with:** `00-findings-and-decisions.md` (D1–D10), `05-…-elevation-and-continuity-plan.md`
(the BridgeProfileSolver this plan re-homes), `10-e-b-findings-and-followups.md` (the 3D deck mesh
this plan keeps, and the §2b terraforming-bleed regression this plan is the strategic fix for).

---

## 0. TL;DR

Today a bridge is its **own standalone spline**, deliberately held out of spline-merging
(`OsmGeometryProcessor.cs:817-825`). The deck is then built from that isolated spline, whose
endpoints only *approximately* meet the neighbouring road splines — so the deck reads as a separate
rectangle "stamped" between the roads, with a plan-view kink at each end (the screenshot).

**The fix the user asked for:** stop holding bridges out of merging. Merge each bridge **into the
through-road corridor** like any other way, but remember **which contiguous sub-range of the merged
spline is the bridge**. Run road smoothing + banking + terraforming on the *whole* merged corridor
(so plan-view and elevation are continuous **by construction**), but **exclude only the bridge
sub-range** from heightmap stamping and material painting. Build the deck mesh from that
sub-range — i.e. from the *merged, smoothed* centerline — so the deck **is** the road curve over the
span. There is then no seam to reconcile: the deck edges and the approach-road edges are sampled from
**one continuous centerline**.

This is a real refactor (merge stage, cross-section tagging, three whole-spline→per-sub-range
conversions, the profile solver, the deck/decal consumers), but every piece has a precedent already
in the codebase. The single most load-bearing precedent: **`LaneSegment`** already carries a
sub-range (`StartDistance`) that survives merging and Chaikin densification
(`LaneSegmentOps`, `OsmGeometryProcessor.PropagatePathLaneSegmentsToSpline:1021`). A bridge sub-range
is the same shape of data.

---

## 1. Why the current architecture produces the artifact

### 1.1 Bridges are separated *before* merging
`ConvertLinesToSplines` splits paths into `structurePathsMeta` (bridges/tunnels when their exclude
flag is on) and `regularPathsMeta` (everything else), and **only the regular paths go through the
merge connector** (`OsmGeometryProcessor.cs:805-865`):

```csharp
bool isProtectedStructure = (pm.IsBridge && excludeBridges) || (pm.IsTunnel && excludeTunnels);
if (isProtectedStructure) structurePathsMeta.Add(pm);   // NEVER merged
else                      regularPathsMeta.Add(pm);     // merged by NodeBasedPathConnector
```

Structure paths become splines directly (`:867-921`); regular paths merge first (`:923-975`). So a
bridge way is always a **separate `RoadSpline`** from its approaches.

### 1.2 The deck is built from that isolated spline
- `BridgeDeckDaeExporter.ShouldGenerateDeck(spline)` = `spline.IsBridge && ExcludeBridgesFromTerrain`
  (`BridgeDeckDaeExporter.cs:36-39`) — a **whole-spline** gate.
- The deck consumes **`CrossSectionConverter.ConvertSplineToWorldCoordinates(network, spline.SplineId,…)`**
  (`BridgeDeckDaeExporter.cs:92`) — the whole spline's cross-sections.
- Those cross-sections' `CenterPoint`/`NormalDirection`/`TargetElevation` were sampled from the
  **bridge's own** spline, *not* the through-road. The bridge spline's endpoint tangent at a shared
  OSM node is not guaranteed equal to the approach spline's tangent there.

### 1.3 Every prior fix attacked the *symptom* at the seam
- Endpoint **Z** reconciliation (removed) — matched only height at the two ends.
- `BridgeProfileSolver` — spans the gap with a G0+G1 cubic, but still derives the curve from a
  **cross-spline junction lookup** (`FindConnectedRoadContributor`, `BridgeProfileSolver.cs:280-281`)
  that is fragile against chain fragmentation (memory `continuation_seam_ditch`; doc 05 §1.4).
- Normal-only seam pass (doc 05 §6, never built) — would rotate the deck ribs to match, but
  `CenterPoint` stays put so a centerline-heading kink is unreachable (doc 04 §2.5).

All of these are seam patches on **two geometries that were never the same curve.** The screenshot
artifact is *positional* (centerline heading + lateral offset), and doc 04 §2.5 already proved the
seam-patch family **cannot** fix a positional artifact. The only fix is to make the deck and the road
the **same curve** — which is what merging delivers.

---

## 2. The target architecture

```
OSM ways ──► [MERGE: bridges included, sub-range remembered] ──► merged corridor splines
                                                                  each carries StructureSegment[]
                                                                  (bridge spans, by arc-length)
   │
   ▼
[cross-section generation]  one continuous run of cross-sections per merged spline
   │                        bridge-span sections tagged: IsExcluded + StructureSpanId
   ▼
[road smoothing / banking / chain elevation]   runs over the WHOLE merged spline
   │   → plan-view (CenterPoint/Normal/Tangent) is continuous across the span by construction
   │   → elevation chain is continuous across the span by construction
   ▼
[BridgeProfileSolver]   per bridge span: override the span's elevation with a G0+G1 spanning curve,
   │                    fitted to the IN-SPLINE neighbours just outside the span (no junction lookup)
   │                    → "snapshot" the span's deck geometry here (§4.5)
   ▼
[terrain stamp + paint]   EXCLUDE only the bridge-span cross-sections (per-section, already mostly so)
   │                       three whole-spline skips converted to per-section (§5)
   ▼
[deck export + bridge DecalRoads + (future) AI path]   built from the bridge-span snapshot
                                                        one deck per StructureSpan, not per spline
```

**Why continuity is now free:** the approach and the span are the same `ParameterizedRoadSpline`,
sampled by one `RoadSpline.SampleByDistance`. `CenterPoint[i]→CenterPoint[i+1]` is continuous across
the span boundary; `NormalDirection` is the same 5-point-smoothed field
(`UnifiedRoadNetworkBuilder.SmoothCrossSectionNormals`). The deck's first rib and the approach's last
rib are **adjacent samples of one centerline.** No seam, nothing to reconcile.

---

## 3. The "snapshot" — what the user asked for, made concrete

The user: *"save a snapshot of the exact curvature of the merged result of the spline part … used
later for shaping the bridge."*

That snapshot already exists as data the moment merging is unified: it is **the contiguous run of
`UnifiedCrossSection`s of the merged spline whose `DistanceAlongSpline` lies inside the bridge
sub-range.** Each section carries everything the deck builder needs — `CenterPoint`, `NormalDirection`,
`TangentDirection`, `EffectiveRoadWidth`, `TargetElevation`, `BankAngleRadians`,
`Left/RightEdgeElevation`. Because those sections were sampled from the merged spline, the coordinates
are **already "translated to the merged" geometry** — no manual re-projection needed.

**Two ways to hold the snapshot (recommendation: B):**

- **A — sub-range identity only (live read).** Tag the span sections with a `StructureSpanId`; every
  consumer re-queries the network for that id at consume time. Minimal new state. Risk: a consumer
  that reads *after* a later mutating pass (e.g. the excavator) sees mutated data.

- **B — explicit captured snapshot (recommended).** Right after `BridgeProfileSolver` finalises the
  span elevation (and *before* `ApplyLowerRoadDips`/`Excavate` run), capture an immutable
  `BridgeSpanSnapshot { SplineId, SpanId, ordered list of {CenterXY, Normal, Tangent, Width, CenterZ,
  LeftEdgeZ, RightEdgeZ, DistanceAlongSpline} }` and stash it on the network (e.g.
  `UnifiedRoadNetwork.BridgeSpans`). This is literally the user's "save the coordinates in the spline
  parameters," and it cleanly decouples the deck mesh, the bridge DecalRoads, and the future AI
  waypoint generator (doc 10 tasks B/C) from pipeline ordering. It is also the natural home for the
  "what's under the span" rules-engine context (doc 08 §3).

Recommendation **B**: it matches the user's mental model, gives the deck/decal/AI consumers one stable
contract, and removes the deck/marking ordering hazard that doc 05 §1.3 fought.

---

## 4. Detailed design

### 4.1 New data: `StructureSegment` (mirror of `LaneSegment`)

A bridge/tunnel sub-range carried on the path through merging, anchored by **arc-length** (not point
index, because Chaikin densification at `OsmGeometryProcessor.cs:893,946` invalidates indices — this
is exactly why `LaneSegment` resolves through `StartDistance`, see `PropagatePathLaneSegmentsToSpline:1031-1038`).

```csharp
// new: Terrain/Models/RoadGeometry/StructureSegment.cs
public sealed class StructureSegment
{
    public int StartPointIndex { get; set; }      // bookkeeping during merge (like LaneSegment)
    public float StartDistance { get; set; }      // arc-length on the final spline (the stable anchor)
    public float EndDistance   { get; set; }
    public StructureType Type  { get; set; }      // Bridge / Tunnel / …
    public int Layer { get; set; }
    public string? BridgeStructureType { get; set; }
    public IReadOnlyDictionary<string,string>? OsmTags { get; set; }  // E-C bag, per-span now
    public HashSet<long> OsmWayIds { get; set; } = new();
}
```

- Carried on `PathWithMetadata.StructureSegments` (new list) and merged by a new
  `StructureSegmentOps.MergeSegments/ReverseSegments` that is a near-copy of `LaneSegmentOps`
  (`LaneSegmentOps.cs`) — same `StartPointIndex + pointOffset` shift on `MergeEndToStart` etc.
- Propagated to `RoadSpline.StructureSegments` and then `ParameterizedRoadSpline.StructureSegments`,
  computing `StartDistance`/`EndDistance` exactly as `PropagatePathLaneSegmentsToSpline` does.
- **`RoadSpline.IsBridge`/`IsTunnel` become *derived*** (`StructureSegments.Any(...)`), kept as
  read-only convenience so existing call-sites compile, but the source of truth is the segment list.
  (Per-whole-spline `IsBridge` was only ever correct because a bridge was its own spline.)

### 4.2 Merge stage changes (`OsmGeometryProcessor.ConvertLinesToSplines`)

1. **Seed a structure segment per structure path** when building `PathWithMetadata` (`:775-795`):
   if `feature.IsBridge || feature.IsTunnel`, add one `StructureSegment` spanning the whole path
   (`StartPointIndex=0`, type/layer/tags from the feature).
2. **Stop separating structures** (delete/guard `:805-825`): when the new
   `MergeStructuresIntoCorridor` mode is on, **all** paths go into `regularPathsMeta` and through the
   connector. (Keep the old separation behind the off-switch — §7.)
3. **Extend merge anti-rules for the bridge case (the "merge rules extended for bridges" the user
   means):** add a **layer-compatibility guard** to `NodeBasedPathConnector` so a bridge way
   (`layer≥1`) does **not** merge with a road it merely flies **over** at a grade-separated crossing.
   Grade-separated crossings don't share an OSM node, so the topological path won't merge them — but
   the **proximity fallback** (both node-ids null, within tolerance — `NodeBasedPathConnector.cs:314-322`)
   could. Gate: refuse a merge whose two sides have different `Layer` unless they share an OSM node.
   (Same-layer bridge↔approach — the normal case, e.g. `highway=primary` + `bridge=yes` both
   `layer=1` meeting `layer=0` approach at a shared node — needs a deliberate rule: **allow** the
   bridge-end node to merge across a 1-step layer change when it is a shared node, because that node
   *is* the abutment. Encode as: "share node ⇒ allow; else require equal layer.")
4. **Merge the structure segments** alongside lane segments in all four `Merge*` methods
   (`NodeBasedPathConnector.cs:495-565`) via `StructureSegmentOps`.
5. **Propagate** to the spline in both spline-creation loops (`:895-921` regular path now also owns
   structure segments; the dedicated structure-path loop `:867-921` is dead in the new mode).

Result: a corridor spline can be `road → bridge → road`, with one `StructureSegment` marking the
bridge sub-range by arc-length.

### 4.3 Cross-section generation + exclusion tagging

Today exclusion is stamped **whole-spline** in `UnifiedRoadSmoother.CalculateNetworkElevations`
(`:1117-1138`): for each spline, if `IsBridge && ExcludeBridgesFromTerrain`, set **every** section
`IsExcluded=true`.

Change to **per-section by sub-range**:

```csharp
foreach (var spline in network.Splines)
{
    foreach (var seg in spline.StructureSegments)
    {
        if (!(seg.Type==Bridge && p.ExcludeBridgesFromTerrain) &&
            !(seg.Type==Tunnel && p.ExcludeTunnelsFromTerrain)) continue;
        foreach (var cs in network.GetCrossSectionsForSpline(spline.SplineId))
            if (cs.DistanceAlongSpline >= seg.StartDistance &&
                cs.DistanceAlongSpline <= seg.EndDistance)
            { cs.IsExcluded = true; cs.StructureSpanId = spanId; }   // new field
    }
}
```

- Add `UnifiedCrossSection.StructureSpanId` (int, default −1) so consumers can group span sections and
  the deck exporter can build **one deck per span**. (`UnifiedCrossSection.cs`, near `IsExcluded:65`.)
- `DistanceAlongSpline` is already the per-section arc-length (`UnifiedCrossSection.cs:86`,
  `FromSplineSample` sets it from `sample.Distance`) — the range test is exact.

### 4.4 Smoothing & banking — **no change needed, and that's the point**

The whole merged spline is smoothed as one road. Per the §2-of-`00-findings` table this already does
the right thing per-section: the chain elevation solve **includes** excluded sections
(`OptimizedElevationSmoother.cs` chain path), banking runs on them (only roundabouts are exempt),
heightmap stamping **skips** them (`RoadMaskBuilder.cs:46,110` are already per-`cs.IsExcluded`). The
bridge-span sections get continuous plan-view + a continuous (but terrain-following / possibly
sagging) elevation, exactly like a mid-spline stretch of road.

### 4.5 `BridgeProfileSolver` — simplified to interior spans (big robustness win)

The span still must **span, not sag** (doc 05 §1.1). But now the span is **interior** to a single
spline, so the solver gets *simpler and more robust*:

- **Find span sections:** `cs.StructureSpanId == spanId` (was: all `IsExcluded` of a whole bridge
  spline, `BridgeProfileSolver.cs:276`).
- **Find the approach endpoints:** the cross-sections **immediately before `StartDistance` and after
  `EndDistance` on the same spline.** Read their Z and local grade directly. This **deletes the entire
  `FindConnectedRoadContributor` junction walk** (`BridgeProfileSolver.cs:280-281` and its helpers) —
  the single most fragile part of the current solver and the cause of chain-fragmentation skips
  (doc 05 §1.4, memory `continuation_seam_ditch`). A mid-corridor bridge can't be "unchained": its
  neighbours are the same spline.
- **Curve + sag-cap + interior-arch + edge recompute:** unchanged (`BridgeProfileSolver.cs:309-358`).
- **Capture the snapshot (§3 option B) here**, after the override, before any heightmap carve.
- **Isolated bridge** (bridge way reaches the terrain crop edge with no road beyond) → that end has no
  in-spline neighbour; reuse the existing isolated-end fallback (`:303-307`).

This keeps all the math doc 05 ratified, drops the brittle lookup, and rescues the fragmentation
class for free.

### 4.6 Deck export, bridge DecalRoads, AI path — consume spans, not splines

- `BridgeDeckDaeExporter.Export` (`:73,90-93`): iterate **spans** (`network.BridgeSpans` or sections
  grouped by `StructureSpanId`), not `network.Splines.Where(ShouldGenerateDeck)`. Build one
  `BridgeDeckMeshBuilder` deck per span from the span snapshot. File name keyed by span id
  (`bridge_{spline}_{span}.dae`) — note this changes the stable-id story (§8 open Q).
- `BridgeDeckExcavator.Excavate` (`:63`): iterate spans, carve under each span's footprint.
- `GradeSeparationResolver` (`:78,171`): "is the upper a bridge deck here?" becomes "does this crossing
  XY fall inside a bridge span?" — a span query instead of `ShouldGenerateDeck(spline)`.
- The 3D box mesh (`BridgeDeckMeshBuilder`), parapets, abutments — **unchanged**; they already take a
  cross-section run. They just receive a sub-range now.

### 4.7 Pipeline ordering (`TerrainCreator.cs:349-448`) — unchanged shape

The 3b-bridge block stays where it is (after smoothing, before DecalRoad gen + deck export so all
consumers read one finalised geometry). Its **gates** change from `Splines.Any(ShouldGenerateDeck)` to
`network.BridgeSpans.Count > 0`. Capture the snapshot inside `ApplyStructuralProfiles`.

---

## 5. The whole-spline → per-sub-range conversion inventory (the real work surface)

Heightmap stamping is **already** per-`cs.IsExcluded` and "just works" once §4.3 tags sub-range
sections. The work is the handful of places that branch on **whole-spline** `IsBridge`/`ShouldGenerateDeck`:

| # | Site | Today (whole-spline) | Change |
|---|------|----------------------|--------|
| 1 | `UnifiedRoadSmoother.cs:1117-1138` | mark every CS of a bridge spline excluded | mark only CS in a bridge **span** (§4.3); set `StructureSpanId` |
| 2 | `MaterialPainter.cs:75-85` | `if (spline.IsBridge && exclude) continue;` skips the **whole** spline from painting | paint the spline, but **skip the painted samples whose arc-length is inside a bridge span** (paint road, skip span). Painter samples the spline directly, so add a per-sample span test. |
| 3 | `RoadCorridorBuilder.cs:26-32` | `continue` skips whole spline's terrain corridor | build the corridor for the road parts, **omit the bridge-span arc-range** |
| 4 | `BridgeDeckDaeExporter.cs:36-39,73,90-93` | `ShouldGenerateDeck(spline)` + per-spline convert | per-**span** iterate + build (§4.6) |
| 5 | `BridgeDeckExcavator.cs:63` | `Where(ShouldGenerateDeck)` | per-**span** |
| 6 | `GradeSeparationResolver.cs:78,171` | `ShouldGenerateDeck(upper/lower)` | span-membership query |
| 7 | `DecalRoadGenerator.cs:148-150` (`IsGeneratedBridge`), `:427` (`OverObjects`) | whole-spline "is this a generated bridge" → forces `OverObjects` on every decal of the spline | per-**span**: force `OverObjects` only on decal nodes whose arc-length is inside a bridge span; road parts keep layer-driven behaviour |
| 8 | `NetworkJunctionHarmonizer.cs:~229` | excludes bridge-spline endpoints from harmonization | mostly **moot** — bridge ends are now interior to a corridor, not spline endpoints; audit and likely delete the special-case |
| 9 | `CrossSectionConverter.cs:110,155` (`includeExcluded`) | drop excluded unless opted in | unchanged mechanism; deck path passes a **span arc-range filter** instead of "whole spline includeExcluded" |

Items 1, 2, 3, 7 are genuine logic changes (whole-spline `continue` → per-arc-range skip). Items 4–6
are gate swaps. Item 8 is likely a deletion. The heightmap blenders
(`RoadMaskBuilder`, `DistanceFieldTerrainBlender`, `SinglePassBlender`, `ContestedPixelResolver`) need
**no change** — verified per-`cs.IsExcluded` already.

> Note the **§2b terraforming-bleed regression** (doc 10): `GradeSeparationResolver.DipLowerRoad` and
> `BridgeDeckExcavator` carve the raw heightmap after blending and bleed onto neighbouring roads. This
> refactor is the *structural* setup doc 10 §2b called for ("integrate grade separation into the
> road-smoothing system"): once spans are corridor-interior and exclusion is per-section, the dip/
> excavator can be re-expressed as constraints the smoother/blender solve (inheriting road-surface
> protection). **Scope call:** fix the bleed as a *follow-up that this refactor unblocks*, not inside
> this PR, to keep the diff bounded (§7).

---

## 6. Phased implementation (each step builds + tests green)

**Phase 1 — data model & merge plumbing (no behaviour change yet). ✅ DONE 2026-06-08 (424 tests green, +12).**
1. ✅ Added `StructureSegment` + `StructureSegmentOps` (explicit [start,end] ranges; Merge/Reverse/Consolidate;
   Consolidate joins contiguous same-type spans + unions way-ids).
   `Terrain/Models/RoadGeometry/StructureSegment.cs` + `StructureSegmentOps.cs`.
2. ✅ `PathWithMetadata.StructureSegments`; seeded for structure features at the single original-creation
   site (`OsmGeometryProcessor.cs:~793`); merged in all four `Merge*` + `ClonePath` of **both**
   `NodeBasedPathConnector` and `RouteRelationAssembler`.
3. ✅ `RoadSpline.StructureSegments` + `ParameterizedRoadSpline.StructureSegments`; new
   `PropagatePathStructureSegmentsToSpline` resolves `StartDistance`/`EndDistance` (mirrors the lane
   propagation); copied into the `ParameterizedRoadSpline` ctor in `UnifiedRoadNetworkBuilder.cs:~112`.
   **`IsBridge`/`IsTunnel` left settable** (deferred to retirement phase — see §10 Phase-1 note).
   Tests: `BeamNgTerrainPoc.Tests/RoadGeometry/StructureSegmentOpsTests.cs` (ops bookkeeping) +
   `Osm/OsmStructureSegmentTests.cs` (end-to-end: merged corridor carries an interior bridge span;
   separated bridge carries a full-span segment; plain road carries none). Phase 1 is purely additive —
   nothing consumes `StructureSegments` yet — so existing merge/identity tests are unchanged (output-neutral).

**Phase 2 — flag + merge-inclusion + anti-merge guard. ✅ DONE 2026-06-08 (429 tests green, +5).**
4. ✅ Added `MergeStructuresIntoCorridor` toggle (default **off**) on `TerrainCreationParameters` +
   `TerrainGenerationState`, round-tripped through terrain presets (Result/Exporter/Importer) and surfaced
   in the "Bridge/Tunnel Structure Handling" UI panel. Threaded `mergeStructuresIntoCorridor` into
   `ConvertLinesToSplines`/`…WithRoundabouts` (both overloads) and the two orchestrators
   (`TerrainGenerationOrchestrator` ×2, `TerrainAnalysisOrchestrator` ×1). When on, the
   `isProtectedStructure` separation is forced false so structures go through the connector and merge.
5. ✅ Added the **layer-compatibility anti-merge guard** (`enforceLayerAntiMerge`, gated on the flag so
   flag-off is byte-identical) in `NodeBasedPathConnector.ScoreEndpoint`: refuse a merge whose two paths
   have a different `Layer` unless they share an OSM node. Tests in `NodeBasedPathConnectorTests`
   (grade-separated fly-over does NOT merge w/ guard on, DOES via proximity w/ guard off, shared-abutment
   merges across the layer change) + `OsmStructureSegmentTests` (flag forces merge even when
   `excludeBridges=true`; flag-off keeps the bridge separate).

**Phase 3 — per-section exclusion + span ids. ✅ DONE 2026-06-08 (432 tests green, +3).**
6. ✅ Added `UnifiedCrossSection.StructureSpanId` (default −1) + a reproducible `StructureSegment.SpanId`
   (folds the sorted OSM way-id set into a non-negative int — shared by Phases 3–5). Extracted the marking
   into testable `UnifiedRoadSmoother.MarkStructureExclusions`: when `MergeStructuresIntoCorridor` is on
   (threaded onto `RoadSmoothingParameters` via `BuildRoadSmoothingParameters`), it excludes ONLY the
   cross-sections in each excludable span's `[StartDistance, EndDistance]` arc-range and tags them with
   `SpanId`; flag-off keeps the byte-identical whole-spline path. Test
   `StructureExclusionMarkingTests`: interior span excludes only its sections (road stamps), legacy
   whole-spline still excludes all, plain corridor excludes nothing. The deck (Phase 4/5) reads the same
   tagged sections so hole+deck always share endpoints despite the Chaikin arc-length approximation.

**Phase 4 — profile solver to interior spans + snapshot. ✅ DONE 2026-06-08 (436 tests green, +4).**
7. ✅ Added `BridgeSpanSnapshot`/`BridgeStation` + `UnifiedRoadNetwork.BridgeSpans`. Re-homed
   `BridgeProfileSolver.ApplyStructuralProfiles`: when cross-sections carry `StructureSpanId` tags it runs the
   new `ApplyToSpan` per (spline, span) — approach endpoints are the IN-SPLINE neighbours just outside the
   span (no `FindConnectedRoadContributor` junction walk), only the span sections are overridden (road keeps
   chain Z ⇒ structural continuity), and the finalised span is captured into `network.BridgeSpans` (way-ids +
   OsmTags + stations) BEFORE any carve. Kept all the curve math (cubic Hermite, sag-cap, interior-arch, edge
   recompute) and the legacy whole-spline path (no tagged spans ⇒ junction walk, BridgeSpans empty —
   byte-identical). Tests `BridgeSpanProfileTests`: span over a 10 m valley spans without sag and matches the
   in-spline neighbours' Z+grade; no elevation step at either abutment; snapshot captured with finite
   stations; legacy mode leaves BridgeSpans empty.

**Phase 5 — consumers to spans. ✅ DONE 2026-06-08 (438 tests green, +2).**
8. ✅ All §5 sites converted, each branching on merged vs legacy so flag-off stays byte-identical:
   - **Deck exporter** — merged: iterate `network.BridgeSpans`, build one deck per span from the snapshot
     stations (`bridge_{SpanId}.dae`, way-id-derived); legacy whole-spline path kept.
   - **Excavator** — carve per deck-group: span sections (merged) or whole bridge spline (legacy).
   - **Grade-sep** — `IsGeneratedDeckAt(network, spline, XY)` (legacy `ShouldGenerateDeck` OR nearest span
     section) replaces the 3 `ShouldGenerateDeck` queries; `DeckThicknessOffset` measures the SPAN length.
   - **Material painter** — merged: paint the corridor, skip per-sample arc-ranges of excludable spans
     (`GetExcludableSpanRanges`); legacy whole-spline skip kept.
   - **Corridor builder** — merged: omit span sections from the overlap corridor (a deck doesn't overlap the
     road it flies over); legacy whole-spline skip kept.
   - **DecalRoad** — `OverObjects` forced true on nodes whose section is inside a span (`onDeck`); road
     nodes keep layer-driven behaviour.
   - **Harmonizer** — bridge-endpoint special-case guarded with `!MergeStructuresIntoCorridor` (moot in
     merged mode; full removal deferred to Phase 6 cleanup).
   - **TerrainCreator** — both bridge gates now `HasBridgeDeckWork` (legacy ShouldGenerateDeck OR any tagged
     span cross-section), so the profile/excavate block + deck export run in merged mode.
   Test `BridgeDeckSpanExportTests`: one deck per captured span keyed by span id; no spans ⇒ legacy empty.

**Phase 6 — switch the default + validate in-game. ⏳ DEFAULT FLIPPED 2026-06-08 (438 tests green); in-game validation PENDING (user).**
9. ✅ Flipped `MergeStructuresIntoCorridor` to **true** by default on `TerrainGenerationState` (+`Reset`) and
   `TerrainCreationParameters` — the switch the orchestrator reads to drive both the merge stage and
   `BuildRoadSmoothingParameters`. `RoadSmoothingParameters`'s own default stays false (set from state in the
   real path; direct-construction unit tests stay legacy). Full suite 438 green, app builds.
   **NEXT (user):** regenerate the screenshot bridge map (the UI checkbox is on by default) and confirm **no
   plan-view kink, no stamped-rectangle look, deck flush + grade-continuous, terrain/material only excluded
   under the span, markings on the deck**. Grab a short bridge between roads + a grade-separated crossing.
10. **DEFERRED until in-game sign-off (per §7 "keep flag-off working until validated"):** retiring the old
    separated-spline path + dedicated structure-spline loop, `BridgeProfileSolver.FindConnectedRoadContributor`
    + junction walk, the deferred normal-only seam pass, the harmonizer bridge-endpoint special-case, and
    making `IsBridge`/`IsTunnel` derived from `StructureSegments`. Toggle the UI checkbox off for legacy.

---

## 7. Backward-compat, off-switch, and what this retires

- **Gate the whole refactor** behind `MergeStructuresIntoCorridor` (mirrors how every prior bridge
  step shipped behind a flag). Off ⇒ byte-identical to today's separated-spline behaviour. This lets
  Phases 1–5 land without changing output, then Phase 6 flips it.
- **Retired once validated (Phase 6+):** the deferred normal-only seam pass (doc 05 §6 — unnecessary,
  continuity is structural now); `BridgeProfileSolver.FindConnectedRoadContributor` and its junction
  walk; the structure-path separation branch + dedicated structure-spline loop
  (`OsmGeometryProcessor.cs:805-825,867-921`); the harmonizer bridge-endpoint special-case
  (`NetworkJunctionHarmonizer.cs:~229`).
- **Explicitly out of scope (follow-ups this unblocks):** the §2b dip/excavator terrain-bleed
  (doc 10 §2b strategic fix), "do both" clearance split (doc 08 §2), OSM-context rules engine
  (doc 08 §3), DecalRoad "on bridge" mode + AI waypoints (doc 10 B/C). All become cleaner once spans
  are corridor-interior and carry a snapshot + `OsmTags`.

---

## 8. Risks & mitigations

| Risk | Mitigation |
|------|-----------|
| **False merge** (bridge merged with the road it flies over) | layer-compat anti-merge guard (§4.2.3) + tests; proximity-fallback only when both node-ids null AND same layer |
| **Wrong merge of two different bridges meeting at a node** | each keeps its own `StructureSegment`; the merged spline simply has two spans — that's correct |
| **Chaikin invalidates segment indices** | anchor spans by `StartDistance`/`EndDistance`, never point index downstream (the existing `LaneSegment` lesson) |
| **Span boundary lands between cross-section samples** | inclusive arc-range test; optionally snap span bounds to nearest sample so the deck and the excluded set share exact endpoints |
| **Material painter / corridor now need per-sample span tests** (perf) | spans are few; precompute per-spline sorted span ranges; binary-search by arc-length |
| **Stable deck identity across runs** | key decks by `(splineId, spanOrdinal)` or by the span's OSM way-id set; document that merged `splineId` is less stable than the old per-bridge id (open Q) |
| **Big-bang regression** | flag-gated, phased; Phases 1–5 are output-neutral with the flag off |
| **Two bridges joined by a tiny at-grade connector** | they stay distinct spans separated by a road sub-range; the connector terraforms normally (this is the *correct* behaviour the user wants vs the "short stamped bridge" today) |

---

## 9. Tests (new + adapted)

- **Merge/segment math:** road+bridge+road → 1 spline, 1 span at the right arc-range; all four merge
  directions + reversal preserve the span (clone `LaneSegmentOps` tests).
- **Anti-merge:** grade-separated bridge-over-road (distinct nodes, different layer) does NOT merge;
  shared-abutment-node bridge↔approach DOES merge.
- **Exclusion:** merged corridor excludes only span sections; road sections present in road mask.
- **Profile solver:** interior span over a valley spans (no sag); endpoint Z+grade match in-spline
  neighbours; snapshot captured; no junction lookup invoked.
- **Continuity (the headline):** deck span endpoint edge points
  (`GetLeftEdgePosition`/`GetRightEdgePosition`) are collinear with the adjacent approach section's
  edge points within tight tolerance — **this is the assertion that tracks the screenshot**, now
  passing by construction rather than by reconciliation.
- **Regression:** flag off ⇒ network + output byte-identical; non-structure splines untouched.

---

## 10. Decisions (ACCEPTED 2026-06-08)

1. **Snapshot representation** — ✅ **§3 option B**: captured `BridgeSpanSnapshot` on the network.
2. **Deck file identity** — ✅ key decks by the span's **OSM way-id set** (stable across runs).
3. **Scope of this PR** — ✅ **continuity refactor only**; §2b bleed fix / "do both" / rules-engine /
   AI paths are follow-ups this unblocks.
4. **Tunnels** — ✅ include tunnel spans in the **Phase-1 data model** (cheap); defer tunnel-specific
   elevation/geometry.

**Phase-1 note (locked):** `IsBridge`/`IsTunnel` stay **settable fields** during Phase 1 (don't make
them derived yet — many object-initializers assign them; converting to derived read-only would break
compilation). Phase 1 is purely **additive**: `StructureSegments` are seeded + merged + propagated in
parallel, consumed by nothing. Making `IsBridge` derived from `StructureSegments` is deferred to the
retirement phase (§7) when the old separation path is removed.

---

## 11. One-paragraph rationale (for the commit / PR body)

Bridges were held out of spline-merging and rebuilt as isolated splines, so the deck was a different
curve from the road and met it only approximately — a positional seam no amount of endpoint
reconciliation can close. This refactor merges bridges into the through-road corridor like any other
way, remembers each bridge as an arc-length sub-range (a `StructureSegment`, the same shape as the
existing `LaneSegment` that already survives merging), smooths the whole corridor as one road so
plan-view and elevation are continuous by construction, excludes only the bridge sub-range from
terrain stamping/painting, and builds the deck from that merged, smoothed sub-range. The deck becomes
the road curve over the span; the seam disappears because there is no longer a seam.
