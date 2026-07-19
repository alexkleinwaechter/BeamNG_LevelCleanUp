# Doc 17 — De-legacy: hardcode the winning bridge combination, delete the flag/legacy harness (handoff / prompt)

**Date:** 2026-07-08 · **Status:** IN PROGRESS — 2 of the collapses landed (see Progress log). Continue
respecting the render-validation gate in §1 for the pending items. **Branch:**
`feature/bridge_embankment_containment`.
**Read this alone — self-contained**, but read `bridge-parameters-reference.md` (Parts A2/A3/A4 +
the per-parameter wiring verdicts) first; it is the map this task collapses.

## Progress log

- ✅ **Collapse #1 — `Dip As Pin` removed** (commit `7890ba4`). Strictly subsumed by Sparse Floor
  Constraints (every site OR'd the two); byte-identical on the sparse-on preset. 785 tests green.
- ✅ **Collapse #2 — Obstacle Typing unconditional + `Min Bridge Clearance` removed** (§4a below, this
  branch). Typed budget everywhere (road 4.7 + structural depth); resolver road-vs-road fallback 5.0→4.7;
  legacy terrain-max path deleted; two UI boxes + plumbing gone (14 V2 checkboxes left). **NOT
  byte-identical — awaiting the user's Manhattan + steep-map render check.** 780 tests green.
- ⏳ **Remaining:** §4b flags (priority, sparse, span-order, pinned-deck, early-estimate, reprojection,
  ramp-feasibility, graded) one commit each; `DeckToDeckContinuity`/`SeamlessDeckOverlap` still gated on
  their doc 14/15 render sign-off (§1); keep `EnableBridgeBridge`.

---

## 0. The goal (user, 2026-07-08)

> The checkboxes are used for development only. Once it's all up and running we hardcode the working
> combination in code. I don't want legacy stuff.

The Bridge Rule System V2 (`BridgeRuleSystemOptions`) is deliberately built as **flag-gated additions
that are byte-identical to the old behaviour when the flag is off**. Every V2 flag therefore carries a
parallel *legacy* code path. That "flag on vs flag off" harness was always meant to be temporary — an
A/B comparison tool during development. This task **removes the harness**: pick the validated
combination, make it the one unconditional code path, and delete both the legacy branches and the
dev flags.

## 1. STOP — the gate before any removal

Legacy is the current safety net and the only A/B tool. Do NOT delete it while any part of the
winning combination is still being validated:

- `EnableNaturalProfileAnchor`, `EnableContiguousSpanConsolidation` — **VALIDATED** (docs 09/10,
  Manhattan A/B: over-max pixels 115k→5.5k, runaway raises 6→0).
- `EnableDeckToDeckContinuity`, `EnableSeamlessDeckOverlap` — **awaiting render sign-off** (docs 14/15).
  Their legacy paths MUST stay until the user confirms the render.
- Everything else (typing, priority, sparse, span-order, pinned-deck, early-estimate, reprojection,
  ramp-feasibility) — established, but confirm against the shipped preset (§2) before deleting.
- `EnableBridgeBridge` — detection-only; KEEP (doc 16 will build real resolution onto it).

**Rule: never remove the legacy branch of a flag whose feature is still "awaiting render validation."**
Removing legacy is a one-way door — it discards the byte-identical fallback and the ability to A/B.

## 2. Step 0 — establish the winning combination (do NOT assume "all flags on")

The combination is NOT every checkbox on. Two reasons:

1. **Supersession:** `EnableSparseDeckConstraints` fully subsumes `EnableDipAsPin` (every site OR'd the
   two) — **removed 2026-07-08**, see `bridge-parameters-reference.md` changelog. **CORRECTION:** it does
   NOT subsume `EnableGradedDeck`. Graded is live under sparse — `BridgeElevationPlanner.cs` `sparse &&
   graded` selects `BuildUniformSoftPins` (the render #7 cross-sections) vs the flat `BuildPins`, and it
   sets the chord deck reference. The user confirmed the shipped preset is **sparse + graded together**,
   so Graded is part of the final combo; only its `!sparse` legacy parts (approach-ramp pins,
   `BuildGradedPins`) are dead. Removing the Graded *flag* means hardcoding it ON, which is the sparse
   collapse itself (§5 order), not a dead-code delete.
2. **Preset truth:** the real enabled set lives in the validated preset `bridgeRules` node
   (`__preset_Manhattan\theTerrain2_terrainPreset.json`, `__preset_underwood`), NOT in code defaults.

**Deliverable of Step 0:** read the shipped preset(s), write the exact final flag set as a checklist,
and get it confirmed by the user before deleting a single branch. Everything below keys off that list.

## 3. What "legacy" is, concretely (the branches to delete)

Each row = one flag whose OFF path is dead once the flag is hardcoded on. File:line are the branch
points, verified 2026-07-08.

| Concern | Keep (flag-on path) | Delete (legacy off path) | Branch at |
|---|---|---|---|
| **Headroom** | per-kind `ClearanceFor` + structural depth | single `MinBridgeClearanceMeters` for all kinds | `BridgeElevationPlanner.cs:114` (`typed` bool), `:107`, `:173`, `:333` |
| **Deck profile** | graded chord + lift | flat pin at span-average | `BridgeElevationPlanner.cs:132/146/366` |
| **Pin hardness** | sparse soft raw-input shaping | hard-held pins | `UnifiedRoadSmoother.cs:1272/1303`; planner ~L388 |
| **Who yields** | §3.5 `RaiseShareFor` shares | fixed 50/50 `GradeSepSplitRatio` | `BridgeElevationPlanner.cs:463` vs `:508`; `:430` |
| **Corridor** | merged corridor + planner | whole-spline exclusion | `UnifiedRoadSmoother.cs:1203/2199` |
| **Resolver** | `PlanFloorConstraints` | `PlanConstraints` (self-labelled "retired Phase F") | `TerrainCreator.cs:377-383`; `GradeSeparationResolver.cs` class doc |
| **Post-solve** | `AssertCrossingClearances` (read-only) | `ApplyApproachRaiseRamps` (the dam-builder) | `TerrainCreator.cs:417-424` |
| **Dips** | pre-smooth eased well (`DipAsPin`/sparse) | late post-solve carve | `GradeSeparationResolver.cs:275`; `UnifiedRoadSmoother.cs:1345` |
| **Planner inputs** | smoothed A0 estimate | raw DEM samples | `UnifiedRoadSmoother.cs:1243` |
| **Post-smooth deck** | keep pinned deck | pins discarded/re-curved | `BridgeProfileSolver.cs:703` |
| **Span order** | descending-priority + deck carry | per-spline enumeration | `BridgeElevationPlanner.cs:64/252/450` |
| **Station** | reprojected to OSM way | Chaikin arc-length drift | orchestrator `:901/929` |

Note the **A3/A4 subtlety** on headroom: `MinBridgeClearanceMeters` is not *purely* legacy — even with
typing on it is still the road-vs-road dip target in the resolver (`ApplyLowerRoadDips`,
`GradeSeparationResolver.cs:102/340/378`) and a terrain-max term. So deleting the legacy headroom means
**rerouting those road-vs-road uses to the typed Road Clearance (4.7)**, not just deleting a box.

## 4. Design direction — one flag at a time, safest first

**a. ✅ DONE — the headroom-typing slice (least risky, independent of the pending deck-merge work).**
Made `EnableObstacleTyping` unconditional: deleted the `typed` branch, always `ClearanceFor` +
structural depth (null `BridgeRules` → a shared default-clearance instance); rerouted the resolver's
road-vs-road dip target from `MinBridgeClearanceMeters` to the typed Road Clearance (4.7); **deleted**
the legacy terrain-max term outright (terrain is not an obstacle — §3.1 = 0, doc-20 floating-deck rule)
rather than rerouting it; removed the `Min Bridge Clearance (m)` UI box + the `Obstacle Typing` checkbox
and their state/params/preset plumbing (mirroring the Under-Deck removal `107396d`). Also simplified the
profile-solver low-clearance diagnostic to the typed branch. Old presets keep importing. **Render check
still owed by the user** (not byte-identical).

**b. Then collapse each remaining validated flag in its own commit.** Inline the on-path, delete the
off/legacy branch from §3, then remove the flag property + its UI checkbox + preset key. Retire the
"Phase F" `PlanConstraints` path, the `GradeSepSplitRatio` fallback, the flat-pin path,
`ApplyApproachRaiseRamps`, and the late-carve dip as their owning flags are collapsed.

**c. Simplify `AnyEnabled` as the surface shrinks.** Once a flag is gone, drop it from the
`AnyEnabled` OR-chain; when the harness is fully collapsed the gate itself may become always-true and
removable.

**d. Hold the pending ones.** Leave `DeckToDeckContinuity`/`SeamlessDeckOverlap` (and their legacy
paths) until §1 clears them. Keep `EnableBridgeBridge`.

## 5. Cautions

- **One flag = one commit = one Manhattan A/B regen** that must be byte-identical to that flag being
  ON pre-refactor. If it differs, the "legacy" branch was doing something the on-path didn't — stop
  and reconcile before deleting.
- **Doctrine unchanged:** nothing post-solve writes deck/road elevations (`ApplyApproachRaiseRamps`
  is deleted, not relocated). Post-solve shapes bare terrain only.
- **Supersession order:** collapse `SparseDeckConstraints` before touching `GradedDeck`/`DipAsPin`, so
  you delete the graded/dip branches as already-dead code rather than reasoning about live interplay.
- **Don't strand the preset importer:** removing a flag key is fine (ignored on import), but if a
  preset RELIED on a flag being off to get legacy behaviour, that scenario disappears — call it out.
- **Keep the reference doc in lockstep:** update `bridge-parameters-reference.md` as each path/knob is
  removed (it is the human-facing source of truth for what still exists).

## 6. Verification recipe

1. Step 0 checklist confirmed by the user before any deletion.
2. Per flag: full test suite green; a Manhattan 4096 regen A/B (`log_comparision` pair) identical to
   that flag ON before the change; `[DAM-REPORT]` unchanged.
3. After the sweep: `BridgeRuleSystemOptions` has no dev toggle for any collapsed feature; the pipeline
   has ONE code path per concern in §3; the reference doc reflects the reduced surface; a final
   Manhattan regen matches the pre-refactor all-(validated)-flags-on output.
4. Render (user judges): unchanged bridges on Manhattan + one steep map (winningen) — the collapse is
   a pure refactor, so the render must not move.

History: cleanup commits `107396d` (removal template), `d961cd9` (dead flags), constant consolidation
`08b16fd`. Key files: `BridgeElevationPlanner.cs` (headroom/graded/priority/order branches),
`GradeSeparationResolver.cs` (legacy whole-spline `PlanConstraints`, dip target),
`UnifiedRoadSmoother.cs` (sparse/graded/corridor/anchor/A0 branches), `TerrainCreator.cs` (~L377 mode
select, ~L417 anchor gate), `BridgeRuleSystemOptions.cs` (the flags + `AnyEnabled`), and the UI/preset
plumbing in `BlazorUI` (checkboxes in `GenerateTerrain.razor`, `TerrainPreset{Exporter,Importer,Result}`).
