# Bridge Generation — Findings & Decisions

**Date:** 2026-06-03
**Branch context:** master (feature work to branch from `develop`)
**Goal:** Generate a simple flat "pane" deck mesh (`.dae`) per bridge spline, placed as a `TSStatic`
in the level. Tunnels later reuse the same machinery.

This doc captures (a) the current state of the code relative to the two old plans, and (b) the
decisions the user made on 2026-06-03. It is the source of truth for `01-spec-simple-bridge-deck.md`
and `02-implementation-plan.md`.

---

## 1. Status of the two historical plans

Both live in `ai_agent_md_files_history_some_outdated/`. Verified against current code:

### `BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md` (detection) — **DONE** (doc is stale)
Tag-based detection shipped, but folded differently than written:
- `OsmFeature.IsBridge/IsTunnel/IsStructure/GetStructureType()/Layer/BridgeStructureType` — exist
  (`BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs`).
- `StructureType` enum `{None,Bridge,Tunnel,BuildingPassage,Culvert}` — exists, same file.
- Metadata flows `OsmFeature → RoadSpline → ParameterizedRoadSpline` — `RoadSpline.cs`,
  `ParameterizedRoadSpline.cs`, copied in `UnifiedRoadNetworkBuilder.BuildNetwork()`
  (`UnifiedRoadNetworkBuilder.cs:114-122`).
- The proposed `ConvertLinesToSplinesWithStructureMetadata()` + `MergeNonStructureSplines()` were
  **merged into the existing `ConvertLinesToSplines()`** with `excludeBridges`/`excludeTunnels` params
  (`OsmGeometryProcessor.cs:714`), plus extras the plan never anticipated (`RouteRelation`,
  `disableSplineMerging`, OSM node/way-id tracking).
- **Defaults flipped:** `ExcludeBridgesFromTerrain`/`ExcludeTunnelsFromTerrain` default to **`false`**,
  not `true` as the doc said.

→ Treat as historical. No open work.

### `BRIDGE_TUNNEL_ELEVATION_IMPLEMENTATION_PLAN.md` (elevation) — **DONE & WIRED**
- `StructureElevationProfile`, `StructureElevationCalculator`, `StructureElevationIntegrator` exist and
  run as **Phase 2.3** in `UnifiedRoadSmoother` (`UnifiedRoadSmoother.cs:277-309`); they populate
  `ParameterizedRoadSpline.ElevationProfile`.
- Runs in **"Selective"** mode: computes/stores the profile but does **not** write it onto the
  bridge's cross-sections.
- **Gap:** no unit tests for the elevation calculators themselves.

→ Available as a *fallback* elevation source, but not our primary one (see decision D2).

### Neither plan covers DAE/mesh generation
Both explicitly park geometry under "Future / Out of Scope." So the simple-pane feature is **new**;
this folder is its home.

---

## 2. How `IsExcluded` actually behaves (the load-bearing finding)

Bridge/tunnel cross-sections are flagged `IsExcluded = true` once, before the elevation solve, in
`UnifiedRoadSmoother.cs:1156-1178` (log: *"elevation still computed for ramp matching"*).

| Phase | Operation | Excluded behavior | Evidence |
|---|---|---|---|
| 2 | **Elevation solve (chain)** | **Included** — `TargetElevation` computed & continuous | `NetworkElevationGraph.cs:205-223`, `UnifiedRoadSmoother.cs:1209-1237` |
| 2 | Per-spline elevation smoother | Filtered out (`!cs.IsExcluded`) | `OptimizedElevationSmoother.cs:93,293` |
| 2.5 | Banking pre-calc | **RUNS** — only roundabouts are skipped, NOT bridges/excluded CS → bridge gets curvature, bank angle & edge elevations | `BankingOrchestrator.ShouldExcludeFromBanking` = `IsRoundabout` only (`BankingOrchestrator.cs:152`) |
| 3 | Junction harmonization | Skipped (`!cs.IsExcluded`) — **but D9 (2026-06-03) reverses this for bridge ends** | `NetworkJunctionHarmonizer.cs:76`, `JunctionElevationPinner.cs` |
| 4 | Road mask / terrain stamp | **NOT** written to heightmap | `RoadMaskBuilder.cs:46,110`, `DistanceFieldTerrainBlender.cs:108,365` |
| 5 | Material painting | **NOT** painted (no mask entry) | implicit via `RoadMaskBuilder` |
| DAE | `CrossSectionConverter` (DecalRoad) | `if (cs.IsExcluded) continue;` | `CrossSectionConverter.cs:102,140` |

**Plain-English summary:** `IsExcluded` is a misnomer for *"don't terraform/paint here."* The road
network still solves the bridge's elevation as part of the connected chain, which is exactly what makes
the approach road ramp into the deck and back out. Proven by
`BeamNgTerrainPoc.Tests/Elevation/BridgeElevationChainingTests.cs` (asserts road↔bridge elevation gap
< 1.0m, and that road1→bridge→road2 form a single chain).

### Consequence for the user's stated "crucial change" (decision D1)
The requirement *"smoothing must go on for the virtual cross-sections so the road continues into/out of
the bridge"* is **already implemented** for the elevation profile. Our job is to:
1. **Not regress it.**
2. **Consume** those cross-section elevations to build the deck (decision D2).
3. Handle two edge cases that are NOT guaranteed today:
   - ~~**Banking is likely flat** for excluded cross-sections (banking phases skip them).~~
     **CORRECTED 2026-06-03:** banking **does** run on bridges (only roundabouts skip banking — see
     §2 table + `BankingOrchestrator.cs:152`). A curved bridge superelevates automatically and the deck
     inherits real edge elevations. The Step-0 spike's `bank=0`/NaN edges were a **test-harness artifact**
     (`RunChainSmoothing` runs only the chain solve, never Phase 2.5). No code change — the deck builder
     already passes non-NaN edge elevations through and falls back to flat (`center ± width/2`) only when
     they're unset (straight bridge).
   - **An unchained bridge** (chain fragmentation — a known fragility, see memory
     `continuation_seam_ditch`) could leave `TargetElevation` unset. Need a fallback
     (`ElevationProfile`, or terrain sample) + a diagnostic.

---

## 3. Reusable infrastructure (no need to build from scratch)

| Need | Reuse | Location |
|---|---|---|
| Ribbon mesh from stations | `RoadMeshBuilder` (2 verts/station → quads, UV, normals) | `BeamNG.Procedural3D/RoadMesh/RoadMeshBuilder.cs` |
| Station input model | `RoadCrossSection` (center, elevation, tangent, normal, width, bank, edge elevs) | `BeamNG.Procedural3D/RoadMesh/RoadCrossSection.cs` |
| BeamNG-compatible `.dae` writer | `ColladaExporter.Export(BeamNgDaeScene, path)` — required for `base00/start01/Colmesh-1/collision-1` hierarchy so object projection/collision works | `BeamNG.Procedural3D/Exporters/ColladaExporter.cs` |
| Closest existing exporter | `RoadNetworkDaeExporter` (network → world coords → mesh → dae) | `BeamNgTerrainPoc/Terrain/Export/RoadNetworkDaeExporter.cs` |
| World-coord conversion | `CrossSectionConverter.ConvertNetworkToWorldCoordinates()` (currently skips excluded) | `BeamNgTerrainPoc/Terrain/Export/CrossSectionConverter.cs` |
| `.dae` + LOD + materials per asset | `BuildingDaeExporter` | `BeamNgTerrainPoc/Terrain/Building/` |
| TSStatic + SimGroup NDJSON writer | `BuildingSceneWriter` (`CreateTSStaticEntry`, `EnsureSimGroupInParent`, `WriteMaterials`) | `BeamNgTerrainPoc/Terrain/Building/BuildingSceneWriter.cs` |
| Scene primitives | `TSStatic`, `SimItemsJsonSerializer`, `JsonDict` | `Grille.BeamNG.Lib/SceneTree/Main/TSStatic.cs`, `Grille.BeamNG.Lib/IO/Text/` |
| Pipeline hook precedent | `ExportRoadMeshDae` block | `TerrainCreator.cs:299-303`, `ExportRoadMeshDaeAsync` `:1060-1125` |

---

## 4. Decisions locked on 2026-06-03 (from the user)

- **D1 — Parameter semantics.** `ExcludeBridgesFromTerrain = true` currently means *"don't terraform /
  don't paint under the bridge so the user can drop their own bridge asset."* It will be **renamed to
  a "generate bridges" concept later** — not in this first step. For now, "generate a deck" is gated on
  the same flag being true. The smoothing must keep running on the invisible cross-sections (it already
  does — see §2/D1).
- **D2 — Deck elevation source.** Use the **virtual cross-section data** (`TargetElevation` + width +
  normal/tangent, and banking/edge elevations when present) as the deck source. `ElevationProfile` is a
  fallback only.
- **D3 — Reuse building logic** for DAE export + scene writing. **Placeholder material** for now;
  material fine-tuning is a later step.
- **D4 — Folder layout:**
  - Shapes: `art/shapes/MT_bridges/`
  - Scene objects: `main/MissionGroup/MT_bridges/`
- **D5 — No chunking.** Unlike buildings, **every bridge = one `.dae` + one `TSStatic`.**
- **D6 — Banking & width** come from the cross-sections automatically (per D2); no special handling
  expected. ~~(Caveat: banking may be flat for excluded sections — accepted for v1.)~~ **CORRECTED
  2026-06-03:** banking runs on bridges (only roundabouts are exempt), so curved decks superelevate
  automatically — the flat pane is just the straight-bridge case. No caveat.
- **D7 — Tunnels** reuse the same rules in a later iteration.

### Decisions added 2026-06-03 (re-thinking "exclude bridges from special processes")
Trigger: user questioned the blanket exclusion. The only processes we genuinely want suppressed are
**terrain stamping** (`RoadMaskBuilder`/`DistanceFieldTerrainBlender`) and **material painting**.
Everything else should run, with one elevation special-rule. Findings: `cs.IsExcluded` already gates
mostly the right things — banking already runs (above), and the per-spline elevation smoother skip
(`OptimizedElevationSmoother.cs:93,293`) **is** the desired elevation special-rule (bridge spans the
valley via the chain solve instead of following terrain down). New decisions:

- **D8 — Lane markings on the deck.** Generate DecalRoads for bridge spans (currently suppressed outright
  at `DecalRoadGenerator.cs:41`), draped at deck elevation. **Critical:** every DecalRoad that lies over
  a `.dae` (the deck) **must** get `OverObjects = true`, otherwise BeamNG renders it on the terrain
  surface beneath the bridge instead of on the deck. Needs code to set this property on bridge decals.
- **D9 — Junction harmonization runs on bridge endpoints.** Let `NetworkJunctionHarmonizer` process the
  bridge↔approach junctions for tighter blending (not just chain-solve continuity). Risk: the harmonizer
  can pull endpoints toward terrain — must validate it doesn't drag the deck end down. Mid-bridge has no
  junctions, so only the two ends are affected.
- **D10 — Keep the single flag for now; correct docs only.** `cs.IsExcluded` /
  `ExcludeBridgesFromTerrain` already gate close to the right set. Do NOT refactor/split the flag into
  named concerns yet (deferred per spec §7); just correct the docs and proceed with the feature.

---

## 4b. Step 0 spike result (2026-06-03) — D2 confirmed

Ran a faithful spike (`BeamNgTerrainPoc.Tests/Elevation/BridgeDeckElevationSpikeTests.cs`): build
road→bridge→road over a valley, **mark bridge cross-sections `IsExcluded` exactly as
`UnifiedRoadSmoother.cs:1156-1178` does**, then run the same chain solve. Result (PASS):

| Observation | Value | Verdict |
|---|---|---|
| chain count / bridge CS | 1 / 101 | bridge chained with both approaches |
| all bridge CS `IsExcluded` | true | marking persists through solve |
| all bridge CS `TargetElevation` set | **true (not NaN)** | **deck elevation is populated** |
| `TargetElevation` min/max | 73.73 / 81.02 m | real rising profile |
| `EffectiveRoadWidth` | 8.0 m | usable directly |
| `BankAngleRadians` | 0.0 | flat — banking skips excluded sections |
| `LeftEdgeElevation`/`RightEdgeElevation` | NaN (101/101) | **must derive edges from center ± width/2** |
| terrain vs deck (mid) | 60.0 vs 74.75 m | deck floats ~15 m above valley, didn't follow terrain |

**Conclusions for the build:**
- **D2 holds:** drive the deck from cross-section `TargetElevation` + `EffectiveRoadWidth`. No need to
  promote `ElevationProfile` to primary (it stays the fallback for unchained bridges).
- ~~**Banking is flat** for v1 … must NOT read `Left/RightEdgeElevation`.~~ **CORRECTED 2026-06-03:**
  the flat/NaN result here is a **test-harness artifact** — the spike's `RunChainSmoothing` runs only the
  chain elevation solve, not the Phase-2.5 banking pass that runs in the real pipeline. In production,
  bridges **do** get banking + edge elevations (only roundabouts are exempt). So the deck builder **may**
  read `Left/RightEdgeElevation` when set (it does, via the converter) and falls back to
  `center ± width/2` at `TargetElevation` only when they're NaN (straight bridge). Both paths are correct.
- The "smoothing continues for invisible cross-sections" requirement (D1) is empirically confirmed.

---

## 5. Open questions still worth confirming (low-stakes, can default)

1. **Deck width** — full road width (`EffectiveRoadWidth`) for v1? (Assumed yes.)
2. **Deck thickness** — single-surface pane (zero thickness) for v1, or a thin slab? (Assumed
   single-surface; slab/sides are a later step.)
3. **Verification spike** — before writing the exporter, confirm on a real run that bridge
   cross-sections carry non-`NaN` `TargetElevation` and a sane width. (See plan Step 0.)
4. **Placeholder material name** — reuse an existing road material, or emit a dedicated
   `bridge_placeholder` material? (Leaning: dedicated, written via the building material path so the
   deck renders without manual setup.)
