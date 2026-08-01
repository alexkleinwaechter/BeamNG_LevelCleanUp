# Backdrop Collision Toggle — Follow-up / New-Session Handoff

**Written:** 2026-07-28. **Status:** IMPLEMENTED 2026-07-28 (same day), 1144 core tests green.
**User decision on §"Decisions" 1:** default **disabled** — app-layer `BackdropSettings.CollisionMesh`
initializes `false`; core `BackdropGenerationParameters.CollisionMesh` stays `true` (caller-driven,
pinned by `CollisionMesh_DefaultsToTrue`). §"Decisions" 2: plain bool shipped; "simplified collision"
stays a V2 idea. Item 12 (persist `MtBackdropSettings.HasCollision`): DONE (additive nullable).
Implementation notes: mesher-level skip as preferred (`BackdropMesherOptions.CollisionMesh`,
`BackdropChunkMeshResult.CollisionMesh` now nullable + new `SurfaceVertexCount` replaces the
collision-vertex-count proxy in the invariant tests); `decalType` emitted alongside `collisionType`
("Collision Mesh"/"None", strings verified against a real map's prefab/items files). Manual
verification (load-time measurement, fall-through check, preset round-trip, editor save) still open —
see §Verification below.
**REWORKED same day (user finding):** an embedded Colmesh is unnecessary for drivable backdrops —
the TSStatic `collisionType`/`decalType` **"Visible Mesh Final"** scene properties make the game
build physics from the visual mesh. The colmesh machinery was removed entirely (mesher builds no
collision copy, DAEs never embed one — saves the ~2× DAE payload unconditionally); the toggle now
only switches the scene strings ("Visible Mesh Final" both / "None" both — user confirmed both
fields are "None" for disable). Tests re-pinned; suite 1142 green (two colmesh-machinery tests
removed with the machinery).
**Branch:** `feature/backdrop` (HEAD `253d7c5` at time of writing), 1140 core tests green.
**Motivation (user):** a UI switch to enable/disable collision-mesh generation for the backdrop —
**disabling improves level loading times a lot.** BeamNG builds physics collision data from the
`Colmesh-1` node at level load; backdrop chunk DAEs are large (11–160 MB in the rossfeldpanorama
bake), and the collision copy roughly doubles the geometry the game must compile (`.cdae`) and turn
into physics meshes. A backdrop used only as distant scenery doesn't need collision at all.
**Related:** `04-placement-followup.md` recorded a post-merge follow-up ("writer should emit
`collisionType` explicitly so editor saves can't silently kill collision") — THIS change is the
natural place to implement that too; fold it in.

---

## Decisions to confirm with the user before implementing

1. **Default value.** Recommendation: `true` (collision ON) — the approved design names a
   *drivable* backdrop as a feature pillar (spec D-decisions, full collision), and today's bakes
   all have collision; default-off would silently change regenerated levels. The switch makes the
   fast-loading option explicit. If the user prefers fast-by-default, flip the default in ONE place
   (`BackdropSettings.CollisionMesh` initializer) — core stays caller-driven.
2. **Plain bool vs 3-way.** A "Simplified" middle option (collision from a coarser mesh) would give
   drivability AND fast loads, but is real mesher work — out of scope. Ship the bool; note
   "simplified collision" as a V2 idea.

## Change list (quoted symbols are authoritative over line numbers — re-verify anchors on resume)

### Core — `BeamNgTerrainPoc/Terrain/Backdrop/` (has tests; keep 1140 green, TDD for new behavior)

1. `BackdropGenerationParameters`: new tunable `public bool CollisionMesh { get; init; } = true;`
   (place with the other §15 tunables next to `SeamSkirt`).
2. `BackdropQuadtreeMesher.MeshChunk`: the collision snapshot
   (`collision.Vertices.Clear(); collision.Vertices.AddRange(mesh.Vertices); …` around :160) —
   skip building/populating the collision mesh when disabled (null or empty
   `BackdropChunkMeshResult.CollisionMesh`; check how the result type models it and keep the
   existing invariant tests meaningful).
   NOTE: mesher options flow through `BackdropMesherOptions` — decide whether the flag rides on the
   options (mesher-level) or the generator passes it to the scene writer only (mesher always builds,
   writer drops). **Prefer mesher-level skip** — it also saves generation time/memory, which is the
   point.
3. `BackdropSceneWriter.ExportChunkDae`: only register the collision mesh with the
   `ColladaExporter` when enabled (the exporter hardcodes the `Colmesh-1` node name — Task 9
   deferred minor; without the node, the DAE simply has no collision source).
4. `BackdropSceneWriter.CreateTSStaticEntry` (:254-269): **always emit `collisionType` explicitly**
   — `"Collision Mesh"` when collision is on, `"None"` when off (and consider `decalType`
   likewise). This closes the 04-doc editor-round-trip gap in the same stroke: an editor save can
   no longer silently strip drivability, and a collision-off bake is honest about it in the scene
   file. Verify the exact BeamNG value strings against a vanilla level's TSStatics before pinning
   tests (repo reference: `ai_agent_md_files_history_some_outdated/BeamNG_Materials_Documentation.md`
   and any vanilla `items.level.json`).
5. `BackdropGenerator`: thread the flag from parameters to mesher/writer; `Estimate` may note the
   file-size effect (collision ≈ 2× DAE payload) — optional, UI already warns on triangles/bytes.
6. **New tests** (`BeamNgTerrainPoc.Tests/Backdrop/`): (a) toggle OFF ⇒ exported DAE contains no
   `Colmesh` geometry and the TSStatic entry says `collisionType:"None"`; (b) toggle ON ⇒ output
   identical to today INCLUDING the new explicit `collisionType:"Collision Mesh"` (this changes
   scene-writer test pins — update `BackdropSceneWriterTests` deliberately, it is the pinned
   convention changing, same as the `.color` rename was); (c) parameters default is `true`.

### App layer (no test project — `dotnet build`, only `error CS` counts)

7. `BlazorUI/State/BackdropSettings.cs`: `public bool CollisionMesh { get; set; } = true;`
8. `BackdropOrchestrator.BuildParameters`: map `state.Backdrop.CollisionMesh` → parameters.
9. `BackdropSettingsPanel.razor`: new switch next to Seam Skirt, **HelpAdornment pattern**
   (established convention — NEVER wrap MudNumericField/MudSelect/switch rows in a bare MudTooltip;
   use the flex row + `<HelpAdornment TooltipText="…"/>`, see the existing rows):
   Label "Collision Mesh"; help text suggestion: *"Lets you drive on the backdrop. Switching it off
   roughly halves the mesh data and makes the level load much faster — good when the backdrop is
   scenery only."*
10. Presets (same four files as Task 19): exporter block key `"collisionMesh"`, nullable
    `TerrainPresetResult.BackdropCollisionMesh`, importer parse line, `OnPresetImported` apply —
    follow the existing 13-field pattern exactly (absent-in-preset ⇒ state untouched).
11. Docs: `Backdrop-Tutorial.md` settings table row + a sentence in §7 (file layout: "with
    collision off the DAEs contain no Colmesh node"); `Backdrop-Performance-Improvement-Plan.md`
    gets a one-line cross-ref under user-side levers (loading-time, not generation-time).
12. Optional (decide in session): persist the baked state in `MtBackdropSettings` (e.g.
    `HasCollision`) so a future staleness/info line can say "backdrop baked without collision" —
    additive field, nullable-tolerant readers exist; skip if YAGNI wins.

## Verification

- Core: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj` — suite green with the
  deliberately re-pinned scene-writer tests; new toggle tests RED→GREEN (TDD).
- App: `dotnet build BeamNG_LevelCleanUp.sln` — zero `error CS` (DLL-lock MSB noise is normal).
- Manual (user): regenerate with collision OFF → measure level load time vs the collision-ON bake
  (record both in this doc); verify driving onto the backdrop falls through (expected!) and the
  switch's help text says so; regenerate ON → drivable again; preset export/import round-trips the
  switch; editor save + reload keeps the explicit `collisionType`.

## Rules (unchanged)

One conventional commit (e.g. `feat(backdrop): optional collision mesh generation with explicit
scene collision type`), NO AI attribution / Co-Authored-By; never stage `.claude/settings.json`;
backdrop stays default-off; existing non-backdrop outputs byte-identical.
