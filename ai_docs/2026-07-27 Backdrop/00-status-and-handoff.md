# Backdrop Generation — Status & Handoff

**Purpose of this file:** living session log so work can continue across sessions without re-deriving
context. Update it at the end of every session (what happened, what's next, open questions).

Branch: `feature/backdrop` (created from `develop`)
Docs live here: `ai_docs/2026-07-27 Backdrop/` (all documents in English)

## Document index

| File | Content | Status |
|------|---------|--------|
| `00-status-and-handoff.md` | this file — session log | living |
| `01-design.md` | approved design/spec for Variant 1 | **approved by user 2026-07-27** (final read-through done) |
| `02-implementation-plan.md` | step-by-step implementation plan (20 TDD tasks) | **written 2026-07-27, awaiting user review** |

## Decision summary (details + rationale in 01-design.md §2)

- Variant 1 now (no roads); architecture prepared for Variant 2 (importance map, optional
  RoadNetwork input, texture compositor hooks).
- Mesh: pure C# restricted quadtree with importance map — **no CGAL, no native deps**.
- Backdrop box: free rectangle, must contain terrain box, clamped to tile mosaic; second resizable
  selection box in CropAnchorSelector(+Dialog); shared math extracted to `SelectionGeometry`.
- Full collision, chunked world-baked DAEs (`MT_backdrop`, bridge/tunnel conventions).
- BaseColorManager auto-rebakes backdrop chunk textures together with the terrain basecolor;
  contract = `MtBackdropSettings` block in `MT_settings.json`; `OverlayRequest` refactor of
  `MapTileOverlayService`; texture baking lives in the **app layer** (dependency direction).
- No size limit; cost estimator + warnings in the UI. Backdrop fully optional, default off,
  library defaults off (byte-identical baselines).

## Session log

### 2026-07-27 — Session 1 (brainstorming + design)

- Explored codebase (4 parallel agents): selection UI (`CropAnchorSelector`/`Dialog` duplication,
  fixed-size move-only box in source-pixel space), BaseColorManager
  (`MapTileOverlayService`/`TerrainPbrMapBuilder` reusable but level-folder-coupled), DAE infra
  (`ColladaExporter`/`BeamNgDaeScene`, no decimation code exists anywhere), pipeline seams
  (`TerrainGenerationOrchestrator.ExecuteInternalAsync`, `CachedHeightMap` regen mechanism).
- Clarified scope with user (7 decisions, see 01-design.md §2 D1–D8).
- Design presented in two parts, both approved; user addition: backdrop must be fully optional
  (already default-off; reinforced as D8).
- Wrote `01-design.md`.

- User reviewed and approved `01-design.md` (final read-through done, no changes requested).

**NEXT:** write `02-implementation-plan.md` (via superpowers:writing-plans skill) → user approves
plan → implement task-by-task on `feature/backdrop`.

### 2026-07-27 — Session 2 (implementation plan)

- Researched exact API surfaces with 4 parallel agents (BridgeSceneWriter/TunnelSceneWriter +
  BuildingSceneWriter material pattern, ColladaExporter/BeamNgDaeScene, orchestrator insertion seam
  at `TerrainGenerationOrchestrator.cs:206-224`, MapTileOverlayService internals incl. warp
  fingerprint, CropAnchorSelector(+Dialog) duplication map, preset round-trip, MtSettings I/O,
  `DecalRoadNetworkSnapshotLoader.LoadHeightmap` as `.ter` reconstruction precedent).
- Wrote `02-implementation-plan.md` via superpowers:writing-plans: 20 bite-sized TDD tasks —
  core (1–10: contracts → coordinate mapper → rasters → **seam height field (§7)** → chunk planner →
  quadtree mesher (3 tasks) → scene writer → generator+estimate) before app layer (11–15: state/
  MtBackdropSettings, OverlayRequest refactor, texture baker, BackdropOrchestrator + gated stage,
  BaseColorManager interlock) before UI (16–19: SelectionGeometry extraction, backdrop box,
  BackdropSettingsPanel, presets) before verification (20). Full test code per core task
  (spec §13 suite), exact file paths/line anchors, spec-coverage self-review table.
- Key plan-level decisions (documented in the plan header/tasks): global lattice (unit = terrain
  m/px, origin terrain SW corner) makes vertex welding + chunk-border bitwise identity exact by
  construction; center-fan triangulation with neighbor-vertex inclusion is crack-free without
  transition-pattern case analysis; seam line at world ±half with the "last half-cell" watch item;
  fixed LOD pixel size 2 + no nulldetail instead of `ComputeForBounds` (deviation with rationale,
  in-game validation item); texture adjustments baked into chunk PNGs guarded by an
  `ExtraFingerprint` on the new `OverlayRequest`.

**NEXT:** user reviews `02-implementation-plan.md` → then execute task-by-task on
`feature/backdrop` (superpowers:subagent-driven-development or executing-plans), keeping the
existing ~1069 tests green after every task.

### 2026-07-27 — Session 3 (implementation, Tasks 1–6)

- Executing `02-implementation-plan.md` via subagent-driven development (fresh implementer + reviewer
  per task; ledger in `.superpowers/sdd/02-implementation-plan/progress.md`).
- **Tasks 1–5 complete and reviewed** (commits `b8a63f4`, `13b77c7`, `3979adf`, `537b8c3`+`7b8ac7f`,
  `a1a3ea0`+`406a743`): contracts/validation, coordinate mapper, elevation raster, seam height field
  (§7 — review added delta-sign + row-polarity test pins), lattice chunk planner (review added strict
  coarsening test). Suite: 1069 → 1104 tests, green.
- **CORE COMPLETE — Tasks 1–10 done and reviewed** (17 commits `b8a63f4..2865d20`; suite 1069 → 1140,
  green). Every task got a fresh implementer + independent reviewer; every Important finding was fixed
  and re-reviewed (details per task in the SDD ledger `.superpowers/sdd/02-implementation-plan/progress.md`).
- **User decisions during core (both landed):**
  1. Task 6 border predicate: the plan's 1-D-only border-chord rule was a confirmed defect
     (border-locked leaves over tolerance, level-balance broken). User approved the 2-D symmetric
     dyadic-square predicate OR'd with the 1-D chord (`fcba101`) — determinism preserved, plan's
     verbatim test file unchanged.
  2. Task 8 skirt facing: plan's "face away from the terrain" was backface-culled from the player
     viewpoint; user approved flipping the seam skirt toward the terrain (`9d3adbb`) + winding test.
- **Other reviewed deviations (all flagged, none silent):** Balance rewritten to worklist+spatial-hash
  (plan's own prescribed remedy; O(n²)-restart would hang at production scale, `6c8f0e0`);
  `FlipUVVertical=true` in the DAE export — **shipped convention: chunk PNG row 0 = NORTH; Task 13
  must bake north-up** (pinned by a texcoord test, `3ff2c54`); band strips widened to cover the
  Euclidean band's corner lobes + robust nodata predicate (NaN/Inf/undeclared sentinels) + non-fatal
  debug artifacts (`2865d20`); brief's `LodLevel` sample had a CS0121 ambiguity (explicit `List<Mesh>`,
  same workaround as `TunnelDaeExporter`).
- **Production-scale watch items (cannot be unit-tested, first real run / Task 20):**
  `BackdropQuadtreeMesher.BucketSize = 64` (Balance neighbor-query constant — reviewer estimates
  minutes per edge chunk at defaults; lower to ~4 if a real run confirms), normal-pass DEM sampling
  cost in the band, `LastFallbackCount > 0` semantics (border-locked cells are counted, not silent),
  UV/PNG row-order in-game check, distant-chunk LOD visibility.

### 2026-07-27 — Session 4 (implementation, Tasks 11–19 + final branch review)

- **APP LAYER COMPLETE — Tasks 11–19 done and reviewed** (17 commits `8069525..bb0d7d3`; branch total
  34 commits `3d528a5..bb0d7d3`). Same discipline as core: fresh implementer + independent reviewer per
  task, every Important finding fixed + re-reviewed (per-task detail in the SDD ledger
  `.superpowers/sdd/02-implementation-plan/progress.md`). Core suite stays 1140/1140 by construction —
  **no BeamNgTerrainPoc file changed after `2865d20`** (verified). App layer verified per task via
  `dotnet build BeamNG_LevelCleanUp.sln` (zero `error CS`).
- Landed per task: `BackdropSettings` POCO + `MtBackdropSettings`/`MtBackdropChunk` persistence (11);
  `OverlayRequest` refactor — terrain overlay path byte-identical incl. warp-fingerprint sidecars,
  independently re-derived (12); per-chunk satellite `BackdropTextureBaker` — north-up, retry-once-then-
  flat-gray, false-cache-hit-on-retry fixed (13); `BackdropOrchestrator` — gated stage, standalone regen,
  Remove, shared `BuildParameters` (+ strict dims+geotransform probe on recombined mosaics) (14);
  BaseColorManager interlock — service extraction, backdrop rebake, staleness reason, thread-safe list
  swap (15); `SelectionGeometry` extraction + backdrop clamp/resize math (size-preserving Body move,
  anchored borders) (16); backdrop box in both crop selectors — dialog rerouted through its math
  hit-model so terrain dragging keeps working (17); `BackdropSettingsPanel` + cost estimator with
  provider-aware cached-tile count (18); preset export/import round-trip with deferred rect apply (19).
- **Final whole-branch review (most capable model):** all binding constraints verified clean (default-off
  byte-identity, core/app boundary, terrain-overlay preservation, failure isolation, commit hygiene);
  cross-task contracts (chunk plan, datum, north-up chain, parameter builder) consistent and test-pinned.
  1 Important found + fixed in one wave (`bb0d7d3`): cross-page stale `MtSettings` could erase the
  backdrop block / silently disable the rebake interlock — `MtSettings.Save` now grafts the on-disk
  block forward when in-memory is null (explicit `dropBackdropSettings: true` opt-out used only by
  Remove), and `RebakeBackdropTexturesAsync` grafts the disk block before its gate. Re-review clean.
  Ledger deferred-minors triage: **zero must-fix-before-merge**; residuals are post-merge hardening or
  Task 20 watch items (list in the ledger's final-review entries).
- **NEXT: Task 20 — user's in-app/in-game validation.** Manual-check lists live in the per-task reports
  (`.superpowers/sdd/02-implementation-plan/task-1{1..9}-report.md` + `final-fix-report.md`). Priority
  watch list from the final review:
  1. Wall-clock of the first real generation (`BucketSize=64` + in-app double-refine — known one-line
     fixes if it crawls);
  2. Seam band + far-field elevation continuity (grep `[BACKDROP] datum` log line);
  3. Chunk-texture seam alignment (per-chunk raster rounding, ~0.5 src px) and orientation (north-up);
  4. Distant-chunk visibility (fixed LOD 2 px deviation from spec §9, documented in scene writer);
  5. Task 12 parity line: BasecolorManager on a baked level must show "Using cached map tile overlay …";
  6. Cross-page flow: generate backdrop → open BasecolorManager (no reload) → Apply → backdrop block
     survives in MT_settings.json and textures rebake (the final-review fix);
  7. Preset-import: watch for a one-frame backdrop-box flicker (traced cosmetic);
  8. Reduce-then-backdrop shows the designed warning (ring needs full mosaic; UI gating = future work);
  9. Pre-flight note: the "single-line UV-flip fix" the plan's Task 20 references is Task 9's shipped
     `FlipUVVertical` — nothing left to apply.
- Branch state: `feature/backdrop` at `bb0d7d3`, NOT merged, NOT pushed. SDD workspace kept (resume map
  for Task 20 follow-ups). Known process hazard: `CropAnchorSelector.razor(.cs)` contain raw
  Windows-1252 bytes (×, °, ©) that naive editor round-trips corrupt — re-check bytes after any edit;
  the U+FFFD at `CropAnchorSelectorDialog.razor:143/:148` predates the branch (upstream fix candidate).

### 2026-07-28 — Session 5 (docs + first Task 20 results)

- Wrote user docs: `docs/Backdrop-Tutorial.md` (incl. §4 "BaseColorManager and the backdrop — how
  and when") and `docs/Backdrop-Performance-Improvement-Plan.md` (generation reported very slow —
  measure-first plan; top suspects `BucketSize=64` + in-app double-`RefineChunk`). Both uncommitted.
- **User ran Task 20 in game — two defects found** (screenshot in session): (1) backdrop textures
  render far too bright / washed out vs the terrain; (2) chunk textures must be named
  `backdrop_{cx}_{cy}.color.png` — the `.color` part triggers the game's texture cooker (PNG → BC7
  sRGB DDS). Working hypothesis: (2) causes most of (1) — without the sRGB cook the game samples
  the color PNG as linear ⇒ uniform gamma lift. **Debug handoff with anchors, test pins and fix
  order: `03-task20-debug-handoff.md`** (fix naming first, regen, re-evaluate brightness; then
  adjustment-desync / material-response diagnostics; §10 blend caveat is the documented floor).
- Positive Task-20 signals from the same screenshot: seam alignment, texture placement/orientation
  (north-up chain) and chunk continuity all look correct.
- **Naming fix LANDED `77c16a3`**: planner emits `backdrop_{cx}_{cy}.color.png` (TDD: 2 pinning
  assertions + 5 fixtures updated first, red confirmed, then source; 1140/1140 green; solution
  build zero `error CS`). User regenerates, then debugs any remaining brightness MANUALLY
  (H2/H3 diagnostics in `03-task20-debug-handoff.md`).
- **Tooltips LANDED `a7c5aac`**: every BackdropSettingsPanel control (switches, numeric fields,
  selects, all three buttons) wrapped in `MudTooltip` with plain-language explanations
  (`RootClass="w-100"` keeps field widths; parameter verified present in MudBlazor 8.14).
- Branch now at `a7c5aac` (36 commits), NOT merged, NOT pushed.

### 2026-07-28 — Session 6 (performance plan §0.1 + §1–§4 landed)

- Executed `Backdrop-Performance-Improvement-Plan.md` (now under `ai_docs/2026-07-27 Backdrop/`,
  moved from `docs/` together with the tutorial). Four separate commits, full suite 1140/1140
  green after each, no test edited:
  1. `cdb262c` **§0.1 timing**: Stopwatches in `BackdropGenerator.Generate` (raster load / mesh
     loop incl. worst chunk / debug artifacts, surfaced on `BackdropGenerationResult`) + texture
     bake timed in `BackdropOrchestrator`; combined `[BACKDROP] timing: rasters=…s mesh=…s
     (N chunks, worst=…s <dae>) debug=…s textures=…s` line goes through PubSub (invariant culture).
  2. `8184028` **§1**: Balance neighbor-index `BucketSize` 64 → 4 (edge band forces unit leaves; a
     64×64 bucket held ~4096 of them ⇒ near-linear scans per neighbor query).
  3. `9ab77f3` **§2**: internal `MeshChunk(chunk, out refinedLeaves)` overload; generator feeds the
     quadtree-levels debug artifact from it — the per-chunk second `RefineChunk` is gone (the app
     always passes a debug path, so this halves in-app mesh time).
  4. `fc9b6a6` **§3**: private `BorderSets` + `ComputeBorderSets` — the four
     `BackdropEdgeSubdivider.Subdivide` results are computed once per chunk and shared by
     refinement (split snapping) and triangulation (border-vertex registration).
  5. `4b7c3cd` **§4**: `BackdropHeightField._bandRasters` is now a concrete array walked by an
     indexed loop — no boxed enumerator per `SampleDemElevation` call (runs per probe/vertex,
     millions of times). Last-hit-raster cache deliberately skipped (measure first; mutable state
     would block §6 parallelization).
- Plan §5–§8 (normal pass, parallel chunk loop, raster/texture phases) stay measurement-gated.
- **NEXT: user regenerates a real map → paste the `[BACKDROP] timing:` line into the plan's §10
  table** (no pre-fix baseline exists — instrumentation and fixes landed together). Determinism:
  argued by construction + suite-pinned; a real-map before/after byte-check was not possible
  in-session (checklist status recorded in plan §9).

### 2026-07-28 — Session 7 (placement investigation)

- User reported backdrop textures "wrong coordinates or rotation" (in-game screenshot). Systematic
  offline audit of the rossfeldpanorama bake found **NO pipeline defect** — chunk PNGs proven
  correct by gradient-anisotropy measurement, DAE UVs analytically exact, world rects/datum/
  materials/cache all verified. Real finding: a **world-editor save (12:55:53) rewrote the backdrop
  TSStatics** → `isRenderEnabled:false` + `collisionType:"None"` persisted (backdrop hidden,
  collision killed); screenshot = mixed editor-session state.
- **Full handoff for the next session: `04-placement-followup.md`** — clean-load protocol +
  decision tree (A: closed / B: in-place mirror ⇒ drop FlipUVVertical / C: escalate with evidence)
  + reusable verification snippets. No code changed this session.
- **New feature follow-up: `05-collision-toggle-followup.md`** — UI switch for collision-mesh
  generation (disable ⇒ much faster level loads; collision ≈ 2× DAE payload + physics build).
  Full change list core+app+presets+docs, TDD notes, and it folds in the 04-doc follow-up of
  emitting `collisionType` explicitly (editor-save round-trip safety). Default recommendation:
  ON (spec's drivable-backdrop pillar) — confirm with user before implementing.

## Open questions / watch items

- In-game validation items (cannot be unit-tested): distant-chunk visibility (`nulldetail`/LOD pixel
  sizes), seam drivability on all four sides, hairline cracks at distance (seam skirt sufficiency),
  physics behavior on very coarse far-field triangles.
- Appearance seam at overlay blend < 100 % (recorded caveat, V1 ships with help note).
- V2 open problem: road smoothing on capped-resolution far raster (corridor-local resampling vs.
  coarse smoothing) — decide when V2 starts.
