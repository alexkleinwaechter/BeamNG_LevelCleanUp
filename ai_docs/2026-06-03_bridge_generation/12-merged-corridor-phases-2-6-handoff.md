# Handoff — Merged-Corridor Bridges, Phases 2→6 (one session)

**Date:** 2026-06-08
**Branch:** `feature/bridges`
**Master plan:** `ai_docs/2026-06-03_bridge_generation/11-merged-corridor-bridge-continuity-plan.md` (read §2–§6 + §10)
**Phase 1:** ✅ DONE (uncommitted), 424 tests green. This session does Phases 2→6 and ends at a
**flag-on in-game visual checkpoint** where bridges merge into the through-road corridor and the deck is
built from the merged, smoothed sub-range — so plan-view + elevation continuity are structural (no seam).

Copy everything below this line into a fresh session.

---

## PROMPT

You are continuing the "merged-corridor bridge" refactor on branch `feature/bridges` in
`d:\Source\beamng_mapping_pro` (project `BeamNgTerrainPoc`, .NET 9, build with
`-p:EnableWindowsTargeting=true`). **Read the master plan first:**
`ai_docs/2026-06-03_bridge_generation/11-merged-corridor-bridge-continuity-plan.md` (§2 target
architecture, §3 snapshot, §4 design, §5 conversion inventory, §6 phases, §8 risks). Also skim doc 10
§2b (the terraforming-bleed regression — a follow-up, NOT in scope here) and memory
`merged_corridor_bridge_plan`.

### What's already done (Phase 1 — additive, output-neutral, inert)
A bridge sub-range is now tracked as a `StructureSegment` that survives merging (the structural twin of
`LaneSegment`). Files: `Terrain/Models/RoadGeometry/StructureSegment.cs` (+`StructureSegmentOps.cs`),
`PathWithMetadata.StructureSegments` (seeded at the one original-path-creation site in
`OsmGeometryProcessor.ConvertLinesToSplines`, merged in all four `Merge*`+`ClonePath` of BOTH
`NodeBasedPathConnector` and `RouteRelationAssembler`), `RoadSpline.StructureSegments` +
`ParameterizedRoadSpline.StructureSegments` (propagated via `PropagatePathStructureSegmentsToSpline`
with arc-length `StartDistance`/`EndDistance`, copied in `UnifiedRoadNetworkBuilder`). `IsBridge`/
`IsTunnel` are still settable fields (do NOT make them derived yet). Nothing consumes `StructureSegments`
yet. Tests: `RoadGeometry/StructureSegmentOpsTests.cs`, `Osm/OsmStructureSegmentTests.cs`.

### Accepted decisions (do not re-litigate)
- **Snapshot** = an explicit captured `BridgeSpanSnapshot` stored on the network (option B), captured
  right after the profile solver finalises span elevation and BEFORE any heightmap carve.
- **Deck identity** = key each deck by the span's **OSM way-id set** (stable across runs), not splineId.
- **Scope** = continuity refactor only. The doc-10 §2b dip/excavator bleed, "do both" (doc 08 §2),
  rules-engine (doc 08 §3), DecalRoad-on-bridge / AI paths (doc 10 B/C) are FOLLOW-UPS — don't build them.
- **Tunnels** = already in the data model; defer tunnel-specific elevation/geometry.

### Core idea you're implementing
Stop holding bridges out of merging. Merge a bridge INTO the corridor, mark only its arc-range
sections `IsExcluded` (+ a span id), smooth the whole corridor as one road (continuity by construction),
build one deck per span from those sections. Heightmap stamping is ALREADY per-`cs.IsExcluded`
(`RoadMaskBuilder`/`DistanceFieldTerrainBlender`) so it "just works" once sub-range sections are tagged;
the real work is a handful of whole-spline gates → per-sub-range (§5 table below).

---

### Phase 2 — flag + merge inclusion + layer anti-merge guard
1. Add `bool MergeStructuresIntoCorridor` to `TerrainCreationParameters` and `TerrainGenerationState`
   (default **false**), round-tripped through terrain presets like the other bridge knobs. Surface it in
   the "Bridge/Tunnel Structure Handling" UI panel (mirror `ExcludeBridgesFromTerrain`).
2. Thread it into `OsmGeometryProcessor.ConvertLinesToSplines` (new param `mergeStructuresIntoCorridor`)
   and `ConvertLinesToSplinesWithRoundabouts`; pass from `TerrainGenerationOrchestrator` (the call sites
   that pass `excludeBridges`/`disableSplineMerging`).
3. In `ConvertLinesToSplines`, the `isProtectedStructure` separation block (the
   `(pm.IsBridge && excludeBridges) || (pm.IsTunnel && excludeTunnels)` test): when
   `mergeStructuresIntoCorridor` is true, force `isProtectedStructure = false` so structures go into
   `regularPathsMeta` and merge. (Leave the old path intact for flag-off.)
4. **Layer anti-merge guard** in `NodeBasedPathConnector` (anti-merge rules region, near
   `GetHighwayGroup`): refuse a merge candidate whose two paths have different `Layer` UNLESS they share
   an OSM node id at the join (a shared node = a real abutment; different-layer + no shared node = a
   grade-separated fly-over that must NOT merge — it would only otherwise merge via the proximity
   fallback). `PathWithMetadata` already carries `Layer`.
   Tests (`NodeBasedPathConnectorTests` style): grade-separated bridge-over-road (distinct nodes,
   layer 1 vs 0) does NOT merge; bridge↔approach sharing the abutment node (layer 1 meeting layer 0)
   DOES merge.

### Phase 3 — per-section exclusion + `StructureSpanId`
5. Add `int StructureSpanId` (default −1) to `UnifiedCrossSection` (near `IsExcluded`).
6. Rewrite the exclusion marking in `UnifiedRoadSmoother.CalculateNetworkElevations` (the
   `if ((spline.IsBridge && p.ExcludeBridgesFromTerrain) || …) { foreach c: c.IsExcluded = true }`
   block, ~L1117-1138): when the spline has `StructureSegments`, iterate the segments and mark only the
   cross-sections whose `DistanceAlongSpline ∈ [seg.StartDistance, seg.EndDistance]`, setting
   `IsExcluded = true` and `StructureSpanId = <stable id>` (derive the id from the span's sorted
   OSM way-id set so it's reproducible). Keep the whole-spline path for flag-off / no-StructureSegments.
   Test: a merged corridor excludes ONLY its span sections; road sections remain in the road mask.

### Phase 4 — `BridgeProfileSolver` → interior spans + capture `BridgeSpanSnapshot`
7. Add `UnifiedRoadNetwork.BridgeSpans` (`List<BridgeSpanSnapshot>`). `BridgeSpanSnapshot` =
   `{ int SplineId; int SpanId; HashSet<long> OsmWayIds; IReadOnlyDictionary<string,string>? OsmTags;
   List<BridgeStation> Stations }` where `BridgeStation` = `{ Vector2 Center; Vector2 Normal; Vector2
   Tangent; float Width; float CenterZ; float LeftEdgeZ; float RightEdgeZ; float DistanceAlongSpline }`.
8. Re-home `BridgeProfileSolver.ApplyStructuralProfiles`/`ApplyToBridge` (~L260) onto **spans**:
   - Span sections = `cs.StructureSpanId == spanId` (was: all `IsExcluded` of a bridge spline, L276).
   - Approach endpoints = the in-spline cross-sections immediately BEFORE `seg.StartDistance` and AFTER
     `seg.EndDistance` on the SAME spline — read their Z + local grade directly. **Delete the
     `FindConnectedRoadContributor` junction walk (L280-281 + helpers)** for the merged case; keep it
     only behind flag-off. (Isolated-end fallback still applies when the span touches the spline end.)
   - Keep the curve math (cubic Hermite, sag-cap, interior-arch, edge recompute, L309-358) unchanged.
   - After overriding span Z + edges, **capture the `BridgeSpanSnapshot`** (one per span) into
     `network.BridgeSpans`. This is BEFORE `ApplyLowerRoadDips`/`Excavate` run.
   Tests: interior span over a synthetic valley spans (no sag); endpoint Z+grade equal the in-spline
   neighbours; snapshot captured with finite stations; deck-edge points collinear with the adjacent
   approach section's edge points (THE continuity assertion — should pass by construction).

### Phase 5 — consumers → spans (the §5 conversion inventory)
Convert these whole-spline gates to per-span / per-arc-range (anchor by `StructureSpanId` or the
`[StartDistance,EndDistance]` arc-range). Heightmap blenders need NO change.
| Site | Change |
|---|---|
| `BridgeDeckDaeExporter` `ShouldGenerateDeck`/`Export` + `Where(ShouldGenerateDeck)` | iterate `network.BridgeSpans`; build one deck per span from the snapshot; file name = `bridge_<wayIdHash>.dae` |
| `BridgeDeckExcavator.Excavate` (`Where(ShouldGenerateDeck)`) | iterate spans; carve under each span footprint |
| `GradeSeparationResolver` (`ShouldGenerateDeck(upper/lower)`, ~L78,171) | "is the upper a deck here?" → span-membership query at the crossing XY |
| `MaterialPainter.PaintMaterials` (whole-spline `continue`, ~L75-85) | paint the spline, skip painted samples whose arc-length is inside a span |
| `RoadCorridorBuilder.BuildCorridors` (whole-spline `continue`, ~L26-32) | build corridor for road parts, omit span arc-ranges |
| `DecalRoadGenerator` `IsGeneratedBridge`/`OverObjects` (~L148,427) | force `OverObjects=true` only on decal nodes inside a span; road parts keep layer-driven behaviour |
| `NetworkJunctionHarmonizer` bridge-endpoint special-case (~L229) | likely DELETE — bridge ends are interior now, not spline endpoints; audit then remove |
| `TerrainCreator` 3b-bridge + deck-export gates (~L349-448) | gate on `network.BridgeSpans.Count > 0` instead of `Splines.Any(ShouldGenerateDeck)` |

The 3D box mesh (`BridgeDeckMeshBuilder`, parapets, abutments) is UNCHANGED — it already takes a
cross-section/station run; just feed it a span's stations.

### Phase 6 — flip default + in-game validation
9. Flip `MergeStructuresIntoCorridor` default to **true**.
10. Build, run the FULL test suite (must stay green). Then regenerate a bridge map and visually confirm:
    no plan-view kink at bridge ends, no "stamped rectangle" look, deck flush + grade-continuous with
    approaches, terrain excluded ONLY under the span, markings on the deck. Grab top-down shots of a
    short bridge between roads and a grade-separated crossing.
11. (If time) retire the now-dead scaffolding behind flag-on: the deferred normal-only seam pass,
    `FindConnectedRoadContributor`, the structure-separation branch — but keep flag-off working until
    in-game sign-off. Make `IsBridge`/`IsTunnel` derived from `StructureSegments` only in this cleanup.

### Invariants / gotchas
- Anchor spans by **arc-length** (`StartDistance`/`EndDistance`) downstream — point indices die at Chaikin.
- Heightmap stamping is already per-`cs.IsExcluded`; DON'T touch `RoadMaskBuilder`/`DistanceFieldTerrainBlender`/`SinglePassBlender`.
- Capture the snapshot AFTER the profile override, BEFORE dip/excavator (so the deck reads pre-carve geometry).
- Determinism: no `Date.now`/random; span ids derived from sorted way-id sets.
- Keep every phase building + green; flag default stays OFF until Phase 6 so Phases 2–5 are output-neutral.

### Commands
```
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
dotnet test  BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~Bridge|FullyQualifiedName~Structure|FullyQualifiedName~NodeBasedPathConnector|FullyQualifiedName~Osm"
dotnet test  BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true   # full suite before Phase 6
```

### Definition of done (this session)
Flag-on: bridges merge into corridors; only span sections excluded; one deck per span built from the
snapshot; full suite green; a regenerated bridge map shows the seam/kink gone in top-down. Update plan
doc 11 §6 statuses + the `merged_corridor_bridge_plan` memory. Commit per-phase with messages like
`feat(merged-bridge): Phase 2 — merge structures into corridor + layer guard`.
