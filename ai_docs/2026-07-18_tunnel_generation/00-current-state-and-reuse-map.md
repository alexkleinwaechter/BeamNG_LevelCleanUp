# Tunnel Generation — Current State & Bridge-Machinery Reuse Map

> Date: 2026-07-18 · Branch baseline: `develop` @ `a0054db` (924 tests green)
> Companion docs: `01-tunnel-implementation-plan.md` (the plan), `02-terrain-hole-cutter-design.md` (hole cutter class).
> Historical intent doc: `ai_agent_md_files_history_some_outdated/BRIDGE_TUNNEL_ELEVATION_IMPLEMENTATION_PLAN.md`
> (goals still valid; its implementation is dead code — see §3).

This document is the verified survey of (a) what already exists for tunnels, (b) what the bridge
pipeline provides that tunnels can mirror, and (c) the hazards that must be fixed before tunnels can
be turned on safely.

---

## 1. What already exists for tunnels (live code)

### 1.1 OSM detection & structure segments — COMPLETE, reusable as-is

- `OsmFeature.IsTunnel` (`Terrain/Osm/Models/OsmFeature.cs:216-234`): any `tunnel=*` except
  `tunnel=no`, **plus `covered=yes`**. `GetStructureType()` (244-269) distinguishes
  `Tunnel` / `BuildingPassage` (`tunnel=building_passage`, `covered=yes`) / `Culvert`.
  Note: `IsBridge` wins first (249) — a way tagged bridge+tunnel classifies as Bridge.
- `Layer` (275-286): OSM layer tag (tunnels conventionally negative).
- Every tunnel path is seeded with a whole-way `StructureSegment` at path creation
  (`OsmGeometryProcessor.ConvertLinesToSplines`, 803-822) — identical to bridges: `Type`, `Layer`,
  `OsmTags`, `OsmWayIds`, endpoints. Carried through **all** merge operations
  (NodeBasedPathConnector, RouteRelationAssembler, LateralCarriagewayMerger) and resolved to
  arc-length ranges on the final splines (`PropagatePathStructureSegmentsToSpline`).
- `MergeStructuresIntoCorridor` (always true in-app) forces tunnels to merge into their through-road
  corridor (852-853) — tunnels arrive as `StructureSegment` arc-ranges on corridor splines, exactly
  like bridge spans. `SpanId` = stable hash of the OSM way-id set (`StructureSegment.cs:143-155`).
- `StructureSegmentOps.Consolidate`/`ConsolidateByStation` joins contiguous tunnel segments but never
  mixes Bridge+Tunnel (tested).

### 1.2 ExcludeTunnelsFromTerrain — what the flag does today

Flag path: `GenerateTerrain.razor:672-687` checkbox → `TerrainGenerationState.ExcludeTunnelsFromTerrain`
(default **false**; the XML doc claiming "Default: true" lies) → `RoadSmoothingParameters:260` +
`TerrainCreationParameters:249`.

When ON, a tunnel span gets:
1. Tagged with `StructureSpanId` (`UnifiedRoadSmoother.TagStructureSpans`, 1209-1210 — same path as bridges).
2. `IsExcluded = true` on its cross-sections (`MarkStructureExclusions`, 2217-2254) → terrain is
   **not stamped/carved** over the span (mountain surface stays natural DEM) and material painting
   skips the arc-range (`MaterialPainter.GetExcludableSpanRanges`, 196-208).
3. Elevation is still solved by the normal chain low-pass (needed for ramp matching) — i.e. the
   "tunnel roadway" today follows smoothed terrain **over** the mountain; there is no depth solve.
4. DecalRoads: whole-`IsTunnel` splines skipped entirely (`DecalRoadGenerator.Generate:44-45` —
   "Generated tunnels stay skipped until tunnel mesh/decal behavior is designed").

When OFF (current default): tunnel merges like a normal road and is stamped into the terrain surface
— renders as a surface road cutting across the mountain.

### 1.3 Structure runs, decal scoping, AI waypoints — ALREADY TUNNEL-AWARE

- `DecalRoadGenerator.PartitionSectionsByStructure` (591-678) partitions sections into
  `StructureRunContext { Road, Bridge, Tunnel }` runs. Tunnel runs exist; `OnDeck` is always false
  for them (no collision mesh yet to project onto, 583-586).
- Per-layer render scope `RenderOnTunnels` exists (`DecalRoadLayerDefinition.cs:63`, UI checkbox in
  `DecalRoadLayerSetEditor.razor:443-446`); several built-in wear/edge layers already ship
  `RenderOnTunnels = false`.
- AIRoad decal runs are suppressed on tunnel runs, and `AiWaypointPathGenerator` already emits
  `MT_tunnel_{splineId}_{run}` waypoint segments with endpoints pinned on the ground AI decal
  boundary — same mechanism as bridges (tested: `AiWaypointGenerationTests:163-185`).
- Junction structure-state guard (commit `a0054db`) is tunnel-aware: a ground street cannot weld
  into a tunnel interior (`NetworkJunctionDetector`, `[JUNCTION-GUARD]`; tested).
- `DecalRoadNetworkSnapshot` persists `IsTunnel` per spline (standalone decal regen safe).

### 1.4 Existing tunnel-adjacent 3D code

`BeamNG.Procedural3D/Building/Facade/Passage.cs`: `tunnel=building_passage` already cuts wall
openings through generated buildings (outline cutout, no mesh booleans). Precedent for portal
openings; BuildingPassage segments should stay out of the terrain-tunnel pipeline (see plan §Phase 2).

---

## 2. HAZARD — the span machinery is type-blind (verified 2026-07-18)

`StructureSpanId >= 0` means "structure span" but every downstream consumer assumes **bridge**.
`TagStructureSpans` tags tunnel spans too, so **checking "Exclude Tunnels" in the app today sends
tunnel spans through the whole bridge deck pipeline**: flat deck chord solve, capture into
`network.BridgeSpans`, a spurious bridge-deck DAE export, and deck excavation shaving the mountain.

Verified type-blind sites (no `IsBridge` gate):

| Site | Effect on a tagged tunnel span |
|---|---|
| `TerrainCreator.cs:369` (`hasMergedSpans`) + `:1401-1403` (`HasBridgeDeckWork`) | activates the entire 3b-bridge block |
| `BridgeProfileSolver.cs:471-476` (`RefineSpans` span keys) | tunnel span solved as a deck, captured into `network.BridgeSpans` |
| `BridgeDeckDaeExporter.Export`/`ExportFromSpans` (iterates all `network.BridgeSpans`) | **tunnel exported as bridge deck DAE** |
| `BridgeDeckExcavator.cs:159-167` (`CollectDeckGroups`) | terrain above the tunnel "deck" excavated |
| `BridgeAbutmentOverlapStamper.cs:38-41` | abutment overlap tongue stamped at tunnel portals |
| `RoadElevationDeviationReport.cs:60` | tunnel sections counted as deck sections (benign, diagnostics) |

Only the *planning* side is gated: `BridgeElevationPlanner.cs:1284` and `BridgeSpanFootprint.cs:111`
require `seg.IsBridge && ExcludeBridgesFromTerrain`.

**No test covers any of this** — Phase 0 of the plan fixes it and adds coverage.

---

## 3. Dead code from the historical tunnel design (disposition)

- `Terrain/Osm/Processing/StructureElevationCalculator.cs` (~1,300 lines) — exists, compiles,
  **never called** by the live pipeline. Contains the tunnel elevation rules we want to *harvest
  conceptually*: `CalculateTunnelProfile` (linear if clearance suffices, else S-curve),
  `CalculateRequiredTunnelLowestPoint`, `ValidateTunnelGrade` (max-grade check),
  `CalculateSCurveElevation` (SmoothStep descent 25% / level 50% / ascent 25%),
  `SampleTerrainAlongStructure`.
- `Terrain/Osm/Models/StructureElevationProfile.cs` + `StructureElevationCurveType` — unused.
- `StructureElevationIntegrator` — already deleted (2026-06-07); tombstone comment at
  `UnifiedRoadSmoother.cs:311-316`.
- `ParameterizedRoadSpline.ElevationProfile` (line 193) — write-never/read-never property.
- Orphan knobs on `TerrainCreationParameters`: `TunnelMinClearanceMeters:356`,
  `TunnelInteriorHeightMeters:363`, `TunnelMaxGradePercent:370`, `ShortTunnelMaxLengthMeters:399`
  — declared, read by nothing. **Plan re-uses these names/semantics** in the new
  `TunnelRuleSystemOptions` and deletes the orphans.

Disposition: harvest the *rules* (clearance = terrain − interiorHeight − cover; linear-vs-S-curve
selection; grade validation) into the modern solver architecture, then delete the dead classes
(plan Phase 7). Do not resurrect the old API.

---

## 4. Bridge machinery → tunnel analogue map

The bridge pipeline (V2 rule system) is the template. Symmetry: a bridge pins its **deck above**
obstacles; a tunnel pins its **floor below** terrain.

| Bridge component (file) | Role | Tunnel analogue |
|---|---|---|
| `BridgeRuleSystemOptions` (default-off flags, `EnableAllRules()` in app/preset import) | config | `TunnelRuleSystemOptions`, same baseline discipline |
| Phase 1.7 `TagStructureSpans` | span tagging | reuse; add span **type** (Phase 0) |
| Phase 1.85 `ApplyBridgeDeckPins` / `BridgeElevationPlanner` | pre-smoothing deck pins | **not needed v1** — tunnel portals sit at approach/terrain level; post-solve override suffices (plan §Phase 2) |
| `MarkStructureExclusions` + abutment-end shrink (2221-2253) | keep terrain off deck; span ends stay stamped road | reuse; portal ends stay stamped road (portal apron) |
| `BridgeProfileSolver.RefineSpans` → `network.BridgeSpans` (`BridgeSpanSnapshot`/`BridgeStation`) | post-smoothing G0+G1 span re-curve from solved approaches, frozen snapshot for all consumers | `TunnelProfileSolver.RefineSpans` → `network.TunnelSpans` (same snapshot shape) |
| `BridgeDeckMeshBuilder` (`AddFace` outward-normal discipline, anti-fold weld, watertight parapets/end stamps) | deck mesh | `TunnelMeshBuilder`: swept tube (floor/walls/ceiling inward-facing, outer shell, portal headwalls) |
| `MeshBuilder.AddExtrusion` (`Builders/MeshBuilder.cs:99-205`) | generic 2D-profile sweep along 3D path with per-point up vectors | directly reusable for tube shell; interior faces need inverted orientation |
| `BridgePierPlanner` + `BridgePierMeshBuilder` | supports | n/a (v1); portal headwall is the only "structure" |
| `BridgeDeckDaeExporter` (world-coord mesh, `CloneAsCollisionMesh`, `bridge_{SpanId}.dae`, `art/shapes/MT_bridges`) | DAE export | `TunnelDaeExporter`: `tunnel_{SpanId}.dae`, `art/shapes/MT_tunnels`, collision clone (floor drivable) |
| `BridgeSceneWriter` (idempotent `MT_bridges` SimGroup, TSStatic at origin/identity, placeholder material) | scene emission | `TunnelSceneWriter`, `MT_tunnels`, `mt_tunnel_concrete` placeholder |
| `CleanBridgeOutputDirectories` | fresh output | `CleanTunnelOutputDirectories` |
| `BridgeAbutmentOverlapStamper` (raise-only tongue, span first/last ~3 m, `[BRIDGE-OVERLAP]`) | terrain meets deck at abutment | **portal apron stamper**: terrain meets road exactly at portal mouth |
| `BridgeDeckExcavator` (lower cells poking above deck, `RoadSurfaceOwnerRaster.CanWrite` guard) | clearance carve | inverse: portal hole cutting via `TerrainHoleCutter` (doc 02), same owner-raster guard |
| `BridgeUnderDeckMaterialPainter` (mutates `materialIndices` post-paint) | material repaint | precedent for the hole-cut insertion point (same grid, same phase) |
| `[PIER]`/`[BRIDGE-EXCAVATE]`/`[DAM-REPORT]` logs, `TerrainCreationLogger` | diagnostics | `[TUNNEL]`, `[TUNNEL-HOLE]`, `[TUNNEL-PORTAL]` tags, same logger (`Current` is null at spline-creation time — use fallback) |
| Test pattern: flag-off ⇒ byte-identical output; flag-on ⇒ exact additive delta (`BridgePierExportTests`) | baselines | same discipline for every tunnel flag |

Coordinate conventions (from `BridgeDeckDaeExporter.StationToWorldCrossSection:431-445` +
`BeamNgCoordinateTransformer.TerrainToWorld2D:54-66`): mesh authored in BeamNG world coordinates
(Z-up, origin at terrain center; `world = terrainLocal − halfSize`, `Z += terrainBaseHeight`);
TSStatic placed at `[0,0,0]` with identity rotation. The `BeamNgDaeScene` export path writes
coordinates **verbatim** (no Assimp mirror) — normals must already point the right way in Z-up.

---

## 5. Terrain holes — facts for the hole cutter (verified)

- **Grille.BeamNG.Lib already supports holes end-to-end**: `TerrainData.IsHole` is first-class
  (`Terrain_Types.cs:11-26`); serializer writes `byte 255` for holes and reads 255 → `IsHole=true,
  Material=0` (`TerrainV9Serializer.cs:87-100, 124`); round-trip covered by lib test
  (`Grille.BeamNG.Lib_Tests/Sections/Terrain.cs:88-111`). Caveat: `Terrain.Draw` drops `IsHole`
  (not on our path).
- **Write path**: `TerrainCreator.CreateTerrainFileAsync` builds the mutable flat grid
  `byte[] materialIndices` at `TerrainCreator.cs:579` (`MaterialLayerProcessor.ProcessMaterialLayers`),
  lets `BridgeUnderDeckMaterialPainter` mutate it (:593-603), assembles `Grille.BeamNG.Terrain`
  at :608 with fill loop at :711-716 currently hardcoding `IsHole = false`, saves at :745.
  → Hole cutter inserts between :603 and :608; fill loop hardened to
  `IsHole = materialIndices[i] == 255`.
- **Grid convention**: row-major, `index = y*size + x`, **y=0 = bottom/south** (BeamNG space;
  `HeightmapProcessor` and `MaterialLayerProcessor.ProcessRow` both y-flip from image space).
  World origin = terrain center; terrain-local origin = bottom-left; pixel = terrainMeters ÷
  `MetersPerPixel`. Spline/cross-section positions are already terrain-local meters.
- **Material index budget**: nothing reserves 255 today — a level with 256 materials would silently
  produce holes. Effective usable range is 0–254; the plan adds a validator guard.
- **Existing read-side hole awareness**: `LayerMaskReader` (`HoleMaterialIndex = 255`, holes in no
  mask), `TerrainPbrMapBuilder` (renders holes transparent), `TerrainSpikeValidator` preserves
  `MaterialData` bytes. No code writes holes anywhere yet.
- **Inert preset plumbing**: `TerrainPresetExporter.razor:238-245` always writes an all-black
  `{TerrainName}_holemap.png` commented "black = no holes"; the importer stores `HoleMapPath` but
  nothing consumes it. ⚠ The BeamNG editor-plugin tutorial
  (`ai_agent_md_files_history_some_outdated/terrain_holes_editor_plugin_plan.md:46-53`) documents the
  game convention as **black = hole, white = solid** — the two contradict. Must be verified in-game
  before the PNG-import feature ships (open question in plan §9).

---

## 6. Existing tunnel test coverage

`OsmBridgeIdentityTests` (IsTunnel survives conversion), `OsmStructureSegmentTests`,
`StructureSegmentOpsTests` (no Bridge+Tunnel consolidation), `ContiguousSpanConsolidationTests`,
`BridgeDecalRoadFilterTests` (6 tunnel cases: corridors, runs, no OverObjects, layer scope),
`AiWaypointGenerationTests` (tunnel waypoint segment), `StructureStateTJunctionGuardTests`
(tunnel interior guard), `StructureExclusionMarkingTests` (exclusion with excludeTunnels),
`DecalRoadNetworkSnapshotTests` (IsTunnel round-trip).

**Not covered**: the §2 hazard (tunnel span → bridge deck), any tunnel elevation profile, holes.
