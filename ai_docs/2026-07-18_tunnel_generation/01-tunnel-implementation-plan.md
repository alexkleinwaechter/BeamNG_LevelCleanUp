# Tunnel Generation — Implementation Plan

> Date: 2026-07-18 · Baseline: `develop` @ `a0054db` (924 tests)
> Read `00-current-state-and-reuse-map.md` first (verified survey + hazards).
> Hole cutter class design: `02-terrain-hole-cutter-design.md`.

## Goal

Procedurally generate a tunnel DAE asset per OSM tunnel span, respecting tunnel elevation rules
(portal-anchored profile, max grade, cover below terrain), with smooth road-surface transitions
between the ground road network and the tunnel roadway, and terrain holes cut at the portals so the
player can drive in. Mirror the bridge V2 architecture: default-off rule flags = byte-identical
baseline; the app enables everything.

## Non-goals (v1)

- Underground junctions between tunnels (interchange caverns) — diagnose and log, don't solve.
- Pylons/ventilation/lighting props, emissive materials.
- `BuildingPassage` / `Culvert` segments — excluded from tunnel mesh generation (building passages
  are already handled by building wall cutouts; culverts are too small to drive).
- Editor-plugin hole application (we write holes directly into the generated `.ter`).
- Railway tunnels (pipeline is road-material driven; whatever roads exist get tunnels).

---

## Architecture overview

```
OSM tunnel way ──▶ StructureSegment(Type=Tunnel) on corridor spline        [exists]
                    │
Phase 1.7  TagStructureSpans           + span TYPE on cross-sections        [Phase 0]
Phase 2    chain solve                 (approaches solved normally)         [exists]
           MarkStructureExclusions     + portal-end shrink for tunnels      [Phase 2]
                    │
post-solve TunnelProfileSolver.RefineSpans                                  [Phase 2]
           portal-anchored G0+G1 floor profile, grade rules, depth rule
           ──▶ network.TunnelSpans (frozen snapshots)
                    │
           TunnelPortalApronStamper    terrain meets road at portal mouth   [Phase 2]
                    │
           materialIndices grid ──▶ TunnelPortalHoleProvider ──▶ TerrainHoleCutter  [Phases 1+4]
                    │                                              (holes = byte 255)
           TunnelMeshBuilder (Procedural3D) ──▶ TunnelDaeExporter ──▶ MT_tunnels    [Phase 3]
           TunnelSceneWriter (TSStatic + material)                          [Phase 3]
                    │
           DecalRoads: tunnel runs project onto tunnel floor collision      [Phase 5]
           AI waypoints through tunnel                                      [exists]
```

Pipeline insertion points in `TerrainCreator.CreateTerrainFileAsync`:
- **3b-tunnel block** directly after the 3b-bridge block (~line 462): profile solve + portal apron
  stamper (height edits must finish before the DAM report at :469 and DecalRoads at :487).
- **Hole cutting** after `BridgeUnderDeckMaterialPainter` (:603), before terrain assembly (:608).
- **DAE export** next to `ExportBridgeDecksAsync` (:540) — after the heightmap is final.

---

## Phase 0 — Type-gate the span machinery (prerequisite, own PR)

Fixes the live hazard (doc 00 §2): tunnel spans currently flow into the bridge deck pipeline.

1. Add span type to `UnifiedCrossSection`: `public StructureType StructureSpanType` (default
   `None`), set in `TagStructureSpans` (`UnifiedRoadSmoother.cs:1196-1223`) from the segment in
   hand. Keep `StructureSpanId` semantics unchanged. (Alternative — resolving type via
   owner-spline segments at every consumer — rejected: N lookups, fragile station matching.)
2. Gate bridge consumers on `StructureSpanType == Bridge`:
   - `TerrainCreator.HasBridgeDeckWork` (:1401-1403) and `hasMergedSpans` (:369)
   - `BridgeProfileSolver.RefineSpans` span keys (:471-476)
   - `BridgeDeckExcavator.CollectDeckGroups` (:159-167)
   - `BridgeAbutmentOverlapStamper` (:38-41)
   - `RoadElevationDeviationReport` (:60) — keep skipping ALL structure spans (excluded from dams),
     just count tunnels separately.
3. Tests (new `Elevation/StructureSpanTypeGateTests.cs` + extend `Export` tests):
   - tunnel span with `ExcludeTunnels=true` ⇒ no `BridgeSpans` capture, no deck DAE, no
     excavation, no abutment stamp;
   - bridge behavior byte-identical before/after (existing suites re-run).

Deliverable: safe to tag tunnel spans; everything below builds on this.

## Phase 1 — TerrainHoleCutter (standalone class, no behavior change yet)

Full design in doc 02. Summary:

- `Terrain/Processing/TerrainHoleCutter.cs`: static, source-agnostic stamping of hole cells
  (byte 255) into the flat `materialIndices` grid; overloads for cell lists, `bool[,]` masks, and
  (future) PNG hole maps. Providers live separately — `TunnelPortalHoleProvider` (Phase 4) now,
  preset hole-map import later (the currently-inert `TerrainPresetResult.HoleMapPath`).
- Harden the `TerrainCreator` fill loop (:711-716): `IsHole = materialIndices[i] == 255`,
  `Material = isHole ? 0 : materialIndices[i]`.
- Unify the three scattered `255` constants (`LayerMaskReader`, `TerrainPbrMapBuilder`, cutter) on
  one `TerrainHoleCutter.HoleMaterialIndex`.
- Validator guard: warn when material count > 254 (`TerrainValidator`).
- Tests: stamping/bounds/idempotence; full pipeline round-trip (stamp → Save → Grille Deserialize →
  `IsHole` true, height preserved).

No caller feeds it holes yet ⇒ byte-identical output; mergeable independently.

## Phase 2 — Tunnel elevation rules (profile solve + snapshots + portal aprons)

### 2a. `TunnelRuleSystemOptions` (`Terrain/Models/`)

Mirror `BridgeRuleSystemOptions` discipline: all flags default **off** (library/tests keep the
byte-identical baseline), `EnableAllRules()` used by the app and forced at preset import.

Flags (v1): `EnableTunnelProfile`, `EnableTunnelMesh`, `EnablePortalHoles`,
`EnablePortalAprons`, `EnableTunnelDepthProfile` (v2 rule, see 2b), `AnyEnabled` helper.

Tunables (harvest the orphan `TerrainCreationParameters` knobs' names/semantics, then delete the
orphans in Phase 7):
- `TunnelInteriorHeightMeters = 5.0` (floor→ceiling apex)
- `TunnelMinCoverMeters = 5.0` (terrain surface → ceiling outer shell; ex-`TunnelMinClearanceMeters`)
- `TunnelMaxGradePercent = 6.0`
- `TunnelWallThicknessMeters = 0.6` (interior → exterior shell)
- `TunnelSideClearanceMeters = 1.0` (roadway edge → interior wall, per side)
- `PortalApronMeters = 3.0` (span-end stretch that stays stamped ground road; mirrors
  `AbutmentOverlapMeters`)
- `PortalHoleMinLengthMeters = 8.0`, `PortalHoleLateralMarginMeters = 1.0` (Phase 4)
- Threaded exactly like `BridgeRules`: one shared instance on `TerrainCreationParameters` +
  every material's `RoadSmoothingParameters`.

`BuildingPassage`/`Culvert` segments: skipped by all tunnel rules (`seg.Type ==
StructureType.Tunnel` only).

### 2b. `TunnelProfileSolver` (`Terrain/Export/TunnelProfileSolver.cs`)

Post-smoothing, runs in the new 3b-tunnel block — the direct analogue of
`BridgeProfileSolver.RefineSpans`, and deliberately *simpler*:

- Span keys: cross-sections with `StructureSpanType == Tunnel`, grouped by
  `(OwnerSplineId, StructureSpanId)`.
- **No Phase 1.85 planner pins needed (v1)**: unlike bridge decks (which must be *raised* above
  obstacles, forcing approach ramps up pre-smoothing), tunnel portals sit exactly where the
  approach roads already are — the chain solve leaves portal-adjacent elevations correct. Only the
  span interior (which today follows smoothed terrain *over* the mountain) must be overridden.
- Profile rules (the "tunnel elevation rules" — intent from the historical doc, modern mechanics):
  1. **G0 anchors**: floor Z at each portal = solved `TargetElevation` just outside the span
     (same outside-the-span sampling trick as `RefineSpans` — no fragile junction walk).
  2. **G1 continuity**: entry/exit grades sampled from the approaches over
     `gradeSampleLengthMeters`; Hermite between portals (same math as the pinned deck profile).
  3. **Grade rule**: if the resulting profile exceeds `TunnelMaxGradePercent`, log a
     `[TUNNEL] grade` warning with the actual value. **Do not clamp** (user feedback: no
     grade-clamp mitigations); grade violations mean bad OSM/DEM input, surfaced not hidden.
  4. **Depth rule** (`EnableTunnelDepthProfile`, v2): sample terrain along the span
     (`heightMap2D` bilinear, the live pipeline's sampler — not the dead calculator's). Where the
     Hermite profile leaves `terrainZ < ceilingOuterZ + TunnelMinCoverMeters` outside the portal
     zones, blend in an S-curve sag (descend / level / ascend, SmoothStep — harvested from dead
     `StructureElevationCalculator.CalculateSCurveElevation`) to gain depth, re-checking the grade
     rule. v1 ships without this: shallow stretches simply become holes (Phase 4), which is
     visually acceptable and never wrong geometrically.
  5. Write the profile into the span cross-sections' `TargetElevation` (banking zeroed across the
     span — tunnels are not banked in v1; `LeftEdgeElevation`/`RightEdgeElevation` = center).
- **Capture `network.TunnelSpans`**: reuse the `BridgeSpanSnapshot`/`BridgeStation` shape (it is
  structure-agnostic: Center/Normal/Tangent/Width/CenterZ/edge Zs/DistanceAlongSpline + SpanId,
  OsmWayIds, OsmTags). v1: reuse the existing types in a new `List<BridgeSpanSnapshot> TunnelSpans`
  property; renaming to a shared `StructureSpanSnapshot` is a later mechanical refactor.
  The snapshot is the **single frozen source** for mesh, holes, decals — exactly the bridge
  contract.

### 2c. Portal transitions (the "smooth transitions" requirement)

- **Exclusion shrink at portal ends**: in `MarkStructureExclusions`, mirror the bridge
  abutment-shrink (`UnifiedRoadSmoother.cs:2221-2253`) for tunnel spans — the first/last
  `PortalApronMeters` stay ordinary stamped road, so the ground road runs *into* the portal on real
  terrain. (Today the bridge shrink is `IsBridge`-only at :2229.)
- **`TunnelPortalApronStamper`** (`Terrain/Export/`, modeled on `BridgeAbutmentOverlapStamper`):
  stamps the terrain across the apron to the solved road surface (write-guarded by
  `RoadSurfaceOwnerRaster`), so terrain and tunnel-floor mesh meet at exactly the same Z at the
  portal mouth — no lip, no step. Log `[TUNNEL-PORTAL]`.
- Floor mesh starts at the apron end station with identical Z/width/tangent (same snapshot), which
  is what makes the driving transition seamless — same single-source principle that made bridge
  decks join their approaches.

### Tests (Phase 2)

`Elevation/TunnelProfileSolverTests.cs` (synthetic network via `RoadNetworkTestHelpers`):
- portal G0: span endpoint elevations equal approach elevations;
- G1: no grade discontinuity at portals beyond tolerance;
- interior override: mid-span floor no longer tracks smoothed terrain over the peak;
- grade warning emitted, elevations not clamped;
- flags off ⇒ cross-section elevations byte-identical to baseline.
`Elevation/TunnelPortalApronTests.cs`: apron cells stamped to road Z; owner-raster guard respected.

## Phase 3 — Tunnel mesh + DAE export

### 3a. `TunnelMeshBuilder` (`BeamNG.Procedural3D/RoadMesh/TunnelMeshBuilder.cs`)

Input: station list (from the snapshot, converted to world coordinates by the exporter — same
`StationToWorldCrossSection` pattern), `TunnelProfile` (interior height, wall thickness, side
clearance, ceiling shape).

Cross-section (per station, in the station's Normal/up frame):

```
        ___████████████___            outer shell (arch), offset by wallThickness
      _█░░░░░░░░░░░░░░░░█_
     █░░   interior    ░░█            interior: walls + arched ceiling, faces point INWARD
     █░░  (drivable)   ░░█            arch = segmented semi-ellipse, apex = interiorHeight
     █░░░______________░░█            (TunnelCeilingArchSegments ≈ 8)
     ████████████████████             floor slab: top face drivable (inward=up), bottom face down
       |—— roadWidth ——|
     |—— + 2·sideClearance ——|
```

- Interior width = station `Width` + 2·`TunnelSideClearanceMeters` (station width already carries
  the road width; keep per-station so merged-corridor width changes flow through).
- Build with the `AddFace` outward-flat-normal discipline (shared `internal` helper from
  `BridgeDeckMeshBuilder` — hoist to a common home): the "outward" direction for interior surfaces
  is *into the bore* (toward the driver). `MeshBuilder.AddExtrusion` can build the outer shell in
  one call; interior ring is cleaner via explicit faces to control the floor/wall/ceiling split
  and per-surface UV scale.
- **Portal headwalls**: at both span ends, a watertight ring face connecting the outer shell
  perimeter to the interior perimeter (the visible "portal face" that masks the hole's jagged
  terrain edge). Slightly oversize (flare by `PortalHoleLateralMarginMeters`) so it always covers
  the hole cells.
- Anti-fold: reuse the `BuildAntiFoldEdges` miter-limit weld for tight curves (inner-edge
  backtracking clamps), applied to all four profile corner paths.
- Watertight solids, overlapping rather than coplanar-shared faces (pier-mesh lesson).
- One mesh per span; collision = full clone via the `CloneAsCollisionMesh` pattern (floor is the
  drivable surface; walls/ceiling collide too, which is desired).

### 3b. `TunnelDaeExporter` + `TunnelSceneWriter` (`Terrain/Export/`)

Straight clones of the bridge pair:
- `tunnel_{SpanId}.dae` in `art/shapes/MT_tunnels/` (SpanId already reproducible across runs);
  world-coordinate mesh, `BeamNgDaeScene` (`base00→start01→{Colmesh-1, tunnel_aNNN}`),
  `ColladaExporter` with the same options.
- `TunnelSceneWriter`: idempotent `MT_tunnels` SimGroup in `main/MissionGroup/items.level.json`;
  NDJSON TSStatic per span (`position=[0,0,0]`, identity rotation, `shapeName=/levels/{level}/art/
  shapes/MT_tunnels/tunnel_{id}.dae`); placeholder material `mt_tunnel_concrete` in
  `art/shapes/MT_tunnels/main.materials.json` (same `ArtItemsJsonSerializer` idempotent write,
  darker base color than bridge concrete, e.g. `[0.42,0.42,0.42,1]`).
- `CleanTunnelOutputDirectories` mirroring the bridge cleanup (:1410-1424).
- `TerrainCreator.ExportTunnelsAsync` invoked next to `ExportBridgeDecksAsync` (:540), after the
  final heightmap (portal aprons already stamped).
- Log `[TUNNEL-MESH]` per span: station count, length, bore dimensions, headwall spans.

### Tests (Phase 3)

`Export/TunnelMeshBuilderTests.cs` (vertex/tri count formulas per segment count, headwall
watertightness, inward normals: sampled interior faces have `normal·(center−vertex) > 0`),
`Export/TunnelDaeExportTests.cs` (flag-off ⇒ no files, no scene entries; flag-on delta additive;
temp-dir DAEs, deleted in `finally` — `BridgePierExportTests` pattern),
`Export/TunnelSceneWriterTests.cs` (NDJSON entries, idempotence).

## Phase 4 — Portal hole cutting (`TunnelPortalHoleProvider`)

`Terrain/Export/TunnelPortalHoleProvider.cs` — computes hole cells from `network.TunnelSpans` +
final `heightMap2D`, feeds `TerrainHoleCutter` (doc 02) in the materialIndices window
(:603→:608), after `BridgeUnderDeckMaterialPainter` so nothing repaints over holes.

Per-cell criterion (cells rasterized over the span corridor, half-width = interiorWidth/2 +
wallThickness + `PortalHoleLateralMarginMeters`):

- Let `floorZ(s)`, `roofOuterZ(s)` be the tube profile at the cell's station.
- **Clip rule**: hole iff `terrainZ(cell) < roofOuterZ(s) + ε` **and**
  `terrainZ(cell) > floorZ(s) − ε` — i.e. the heightmap surface passes *through* the tube. Deep
  sections (terrain above the roof) keep intact mountain; the player drives under real terrain.
- **Portal rule**: within `PortalHoleMinLengthMeters` of each portal mouth, hole every corridor
  cell with `terrainZ > floorZ + ε` — this removes the "terrain wall across the road" that the
  heightmap necessarily forms between apron level and mountain flank (a heightmap cannot overhang).
- Dilate the result by one cell (jagged edges hide behind the shell + headwall).
- Guard: never hole a cell owned by another road's painted surface
  (`RoadSurfaceOwnerRaster.CanWrite`) — protects crossing surface roads above the tunnel.
- Log `[TUNNEL-HOLE]` per span (cell count, station range); write a debug hole-mask PNG next to
  the other debug layers.

Interaction note: hole cells lose their material ⇒ groundcover/billboards vanish there
automatically (same reason `LayerMaskReader` excludes 255 from every mask) — no under-deck-painter
analogue needed.

### Tests (Phase 4)

`Export/TunnelPortalHoleProviderTests.cs`: synthetic peak over a span — deep mid-span cells NOT
holed; portal-wall cells holed; lateral margin respected; owner-raster protection; flag-off ⇒ zero
holes (grid untouched).

## Phase 5 — DecalRoads & AI through the tunnel

- Remove the whole-spline tunnel skip (`DecalRoadGenerator.Generate:44-45`) when
  `EnableTunnelMesh` is on for that spline's parameters; keep it when off (today's behavior).
  This skip is also a latent bug (a merged corridor whose base way was the tunnel loses ALL its
  decals, ground stretches included) — fixing it is part of this phase.
- Tunnel runs: set the deck-equivalent projection — `overObjects=true` for runs with
  `StructureRunContext.Tunnel` when the span has a mesh (mirror of `OnDeck`; lift the "only decks
  have a collision mesh" restriction at :583-586). Layer scoping via existing `RenderOnTunnels`.
- Verify the DecalRoad interrupt z-awareness (`e13b3ea`) treats tunnel spans like bridge crossings
  (markings on the surface road above must not be cut by the tunnel below, and vice versa).
- AI waypoints: already emitted (`MT_tunnel_*`) with endpoints pinned on ground AI decal ends —
  once the floor collision exists, in-game AI traffic should path through; validation item.
- Tests: extend `BridgeDecalRoadFilterTests` — tunnel run WITH mesh gets `overObjects`; without
  mesh keeps today's behavior; whole-spline skip removed only when flag on.

## Phase 6 — UI & presets

- "Generate Tunnels" switch in the **Bridges & Tunnels** panel (pattern: "Generate Bridges" at
  `GenerateTerrain.razor:697`): sets `ExcludeTunnelsFromTerrain=true` + `TunnelRules.EnableAllRules()`.
  Numeric knobs (CSS-hidden unless on, like bridges): interior height, wall thickness, side
  clearance, min cover, max grade, portal apron. German-culture safe (invariant culture is set in
  Program.cs — do not touch).
- `TerrainGenerationState`: `TunnelRules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled()`
  (init + reset), fix the lying XML doc on `ExcludeTunnelsFromTerrain`.
- Orchestrators (`TerrainGenerationOrchestrator` + `TerrainAnalysisOrchestrator`): thread
  `TunnelRules` onto `TerrainCreationParameters` + every material's `RoadSmoothingParameters`
  (single shared instance — the `BridgeRules` comment at :1081-1084 applies).
- Preset exporter/importer: write/read the tunnel keys; **force `EnableAllRules()` at import**
  exactly like `BridgeRules` (`TerrainPresetImporter.razor:716`) so old presets get tunnels.
- Material-count guard surfaced in UI validation (> 254 materials ⇒ warning, index 255 reserved).

## Phase 7 — Cleanup

- Delete `StructureElevationCalculator`, `StructureElevationProfile`,
  `ParameterizedRoadSpline.ElevationProfile`, and the four orphan `TerrainCreationParameters`
  tunnel knobs (superseded by `TunnelRuleSystemOptions`). Grep-verify zero references first.
- Update `CLAUDE.md` (tunnel feature note) and memory.

---

## 8. Validation plan (manual, per the project's no-auto-E2E reality)

1. Synthetic: unit suites per phase (target: all existing 924 + new stay green; flag-off runs
   byte-identical — assert via existing baseline patterns).
2. Real map with true mountain tunnels (e.g. an Alps preset; Manhattan has few road tunnels —
   Park Row / Battery Park Underpass are `layer=-1` covered ways worth checking as edge cases).
   Regen with Generate Tunnels on, then:
   - `[TUNNEL]` log blocks vs `osm_layer` PNGs (span count = OSM tunnel count for selected roads);
   - drive-through: portal transition smoothness (no lip at apron), AI traffic follows;
   - visual: portal headwall covers hole edges; mountain intact above deep sections; no spurious
     bridge decks over tunnels (Phase 0 regression);
   - `.ter` re-open in BeamNG editor: holes visible in terrain, hole export
     (`tb:exportHoleMaps`) round-trips.

## 9. Open questions / risks

| # | Question | Default until answered |
|---|---|---|
| 1 | Hole-map PNG polarity (game tutorial: black=hole; our preset exporter comment: black=no-hole) | Verify in-game before the PNG-import feature (doc 02 §6); portal cutting is unaffected (writes bytes, not PNGs) |
| 2 | Ceiling shape: arch vs rectangular box | Arch (segmented semi-ellipse); box is a cheap option flag later |
| 3 | Tunnels crossing under other roads' stamped surfaces near portals | Owner-raster guard skips those cells; if a real map shows a blocked portal, resolve in a follow-up (grade-separation dip analogue) |
| 4 | Underground tunnel↔tunnel junctions (both interiors admit welding per junction guard) | Log `[TUNNEL] interior junction` diagnostic, exclude such spans from mesh (v1), own doc later |
| 5 | Self-crossing tunnel legs (spiral tunnels) | SpanId per way-set already separates legs; hole criterion is per-station so overlaps union — expected OK, watch in validation |
| 6 | Snapshot type naming (`BridgeSpanSnapshot` reused for tunnels) | Reuse now, mechanical rename to `StructureSpanSnapshot` after v1 lands |
| 7 | Should `covered=yes` ways (BuildingPassage) ever get tunnel meshes (e.g. galleries)? | No in v1; revisit with real-map evidence |

## 10. Suggested branch/PR slicing

1. `bugfix/structure_span_type_gate` — Phase 0 (independent bugfix, land first).
2. `feature/terrain_hole_cutter` — Phase 1 (independent, no behavior change).
3. `feature/tunnel_profile` — Phase 2 (rules + solver + aprons).
4. `feature/tunnel_mesh` — Phase 3 + 4 (mesh/export + holes; first user-visible tunnels).
5. `feature/tunnel_decals_ui` — Phase 5 + 6.
6. Cleanup (Phase 7) rides with 5 or separately.

Each branch: flag-off byte-identical baseline enforced by tests before merge to `develop`.
