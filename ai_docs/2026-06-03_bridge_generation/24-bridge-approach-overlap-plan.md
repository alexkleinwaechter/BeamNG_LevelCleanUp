# Plan — Bridge Approach Overlap ("apron") — kill the abutment kink

> **SUPERSEDED (2026-06-10):** replaced by the Bridge Rule System V2 plan
> (`ai_docs/2026-06-10_bridge_generation_V2/01-bridge-rule-system-plan.md`). The cosmetic
> abutment-wall implementation (6-file diff) was reverted; abutment treatment is now
> Phase B (R8 terrain stamping: embankments + abutment placement) of the rule system.

**Date:** 2026-06-10
**Branch:** `feature/bridge_merged_corridor` (HEAD `adeeb9a` Phase D)
**Status:** PLAN. No code written yet.
**Reads with:** `11-merged-corridor-bridge-continuity-plan.md` (the corridor architecture this builds on),
`16-phase-d-render-debugging-handoff.md` (the abutment-step symptom, §2 + §3b).
**Memory:** `merged_corridor_bridge_plan`. Supersedes the `BridgeAbutmentFiller` "overlap zone" idea from the
reverted `feature/bridges` branch — re-homed here onto the merged corridor where it's cleaner.

> **User decision (2026-06-10):** plan **both** the cosmetic (deck-mesh) overlap and the structural
> (terrain-fill) overlap, controlled by one shared config parameter. **Implement the cosmetic phase first.**
>
> **Implementation decisions (2026-06-10):**
> - **A — Geometry first, plumbing after.** Land the apron deck geometry with a hard-coded **4 m** overlap +
>   render check #1, THEN add the 8-site `BridgeApproachOverlapMeters` UI/preset plumbing (§2).
> - **C — Depress the apron deck-top ~3 cm below the road surface** (§3.4a) so road markings win and the deck
>   reads as emerging from under the wearing surface.
> - **B — Default overlap 4.0 m** (confirmed).

---

## 0. TL;DR

A bridge in merged-corridor mode is an arc-length sub-range `[StartDistance, EndDistance]` of the through-road
spline. The **road/deck driving surface is already continuous** across the span boundary (deck = road curve, by
construction — doc 11 §2). The remaining artifact is in the **terrain**: just *outside* the span the heightmap is
stamped up to the road embankment; just *inside* it the heightmap is natural (the daylight the bridge spans). So
the terrain has a **vertical cliff at each abutment**, and the deck box ends in a **vertical end-wall flush with
that cliff** — the "stamped rectangle between two roads" look.

The fix is to make the bridge **overlap the approach road** by a configurable `BridgeApproachOverlapMeters`. Two
halves, one parameter:

| Phase | What | Removes / hides | Fixes which bridges |
|------|------|-----------------|---------------------|
| **1 — cosmetic (ABUTMENT WALL)** *(do first)* | Drop the bridge end-block down from the soffit **to the natural under-span ground**, full road width — a solid "concrete" abutment face over the raw dirt cliff. Mesh-only (samples, never mutates, the heightmap). | **Hides** the cliff behind a wall (how real abutments look). | All bridges where the cliff is the eyesore; doesn't change the ground itself. |
| **2 — structural (TERRAIN FILL)** | A new `BridgeAbutmentFiller` heightmap pass that ramps the embankment **up to the deck end** over the overlap zone, with lateral falloff. | **Removes** the terrain cliff. | All bridges, incl. raised flyovers. |

> **Why not the original "horizontal apron"?** On working the geometry: the approach is stamped to ~deck height
> right up to the abutment (deck = road, one curve), so a deck extended *outward over the approach* buries itself
> in the embankment — invisible. The visible artifact is the embankment's **leading face** (a near-vertical drop
> from road level to the spanned ground in ~1 heightmap cell). A pure deck overlap can't help that; an **abutment
> wall** covers it, and the **terrain fill** removes it. (The plan-view / curve seam that "overlap by 2 sections"
> classically fixes is already gone on this branch — merging made deck and road one curve, doc 11.)

Cosmetic is a pure mesh change (no heightmap mutation) → fast to ship and visually judge. Phase 2 then closes the
terrain cliff for tall abutments. Both key off the **same** `[StartDistance, EndDistance] ± BridgeApproachOverlapMeters`
arc-range, so the parameter and the geometry are shared.

---

## 1. The kink, precisely (code-grounded)

In the correct merged-corridor result the deck-top **is** the smoothed road centerline — there is no plan-view or
driving-surface kink (that was the whole point of doc 11). What's left:

1. **Terrain stamping skips the span.** `RoadMaskBuilder.RasterizeSplinePolygons` (`Terrain/Algorithms/Blending/
   RoadMaskBuilder.cs:~313-344`) stitches a heightmap quad between every list-consecutive kept pair, but **skips the
   span** via the `LocalIndex` gap guard:
   ```csharp
   if (cs2.LocalIndex - cs1.LocalIndex > 1) continue; // straddles an excluded (bridge) run
   ```
   So the approach sections build an embankment up to the road; the span sections leave terrain natural. The
   boundary between the two **is the cliff**.

2. **The deck box ends in a vertical wall at that boundary.** `BridgeDeckMeshBuilder.BuildBoxShell` end caps
   (`BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs:~98-104`) draw a vertical face at the first/last station;
   `AddEndStamp` (`:~145-166`) adds a solid abutment block that extends **inward** (toward the span) by
   `EndStampLengthMeters` and **down** by `AbutmentDepthMeters`. The block does not reach into the approach
   embankment, so the abutment sits at the lip of the cliff rather than buried in fill — the "stamped rectangle"
   read.

The user's instinct ("overlap the road by ~2 cross-sections") is right: bury the abutment in the approach and let
the deck emerge from the embankment, the way a real abutment does.

> **Units note.** "2 cross-sections" is only ~1 m: `CrossSectionIntervalMeters` defaults to **0.5 m**
> (`RoadSmoothingParameters.cs:~118`). The knob is therefore **meters-based** (`BridgeApproachOverlapMeters`,
> default **4.0 m** — a typical approach-slab length), resolved to whatever section count that implies at run time.

---

## 2. Shared config parameter — `BridgeApproachOverlapMeters`

Float, default **4.0 m**, range 0–20 (0 = today's behaviour, byte-identical). Mirror the existing
`MinBridgeClearanceMeters` / `BridgeMaxSagBelowChordMeters` plumbing **exactly** — here is the complete touch-list
(verified against both reference params):

| # | File | What to add |
|---|------|-------------|
| 1 | `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs` (`~277-297`) | `public float BridgeApproachOverlapMeters { get; set; } = 4.0f;` + XML doc |
| 2 | `BlazorUI/State/TerrainGenerationState.cs` (`~79-92` + `Reset()` `~455`) | field default 4.0 + reset to 4.0 |
| 3 | `BlazorUI/Components/TerrainPresetResult.cs` (`~119-129`) | `public float? BridgeApproachOverlapMeters { get; set; }` |
| 4 | `BlazorUI/Components/TerrainPresetExporter.razor` (`~74-76` param, `~482` write) | `[Parameter]` + `["bridgeApproachOverlapMeters"] = ...` |
| 5 | `BlazorUI/Components/TerrainPresetImporter.razor` (`~692-697`) | read-back `if (terrainOptions["bridgeApproachOverlapMeters"] != null) ...` |
| 6 | `BlazorUI/Services/TerrainGenerationOrchestrator.cs` `BuildTerrainCreationParameters` (`~1016`) | `BridgeApproachOverlapMeters = state.BridgeApproachOverlapMeters,` |
| 7 | `BlazorUI/Pages/GenerateTerrain.razor.cs` (`~136-152` proxy, `~2170` import-apply) | `_bridgeApproachOverlapMeters` get/set proxy + apply-from-preset |
| 8 | `BlazorUI/Pages/GenerateTerrain.razor` (`~686-733`, "Bridge/Tunnel Structure Handling" panel) | `MudNumericField` (Min 0, Max 20, Step 0.5) + help tooltip |

**Sequencing choice (open Q A):** wire all 8 in Phase 1, *or* land Phase 1 with a hard-coded default first to
iterate on the look, then add UI/preset plumbing once the geometry is right. Recommend the latter for speed —
plumbing is mechanical and risk-free; the geometry is where the iteration is.

---

## 3. Phase 1 — cosmetic ABUTMENT WALL (DO FIRST)

> **Redirected 2026-06-10** from "horizontal apron" to "abutment wall" — see the TL;DR note (the apron buries
> itself in the approach embankment; the wall covers the cliff). Hard-coded geometry first; the
> `BridgeApproachOverlapMeters` plumbing in §2 belongs to Phase 2 (the apron knob is not used by the wall).

### 3.1 The idea

Today the deck box ends in a short solid "end-stamp" block under each end: from the soffit edges down by a **fixed**
`AbutmentDepthMeters` (1.0 m), extruded `EndStampLengthMeters` (3.0 m) toward the span
(`BridgeDeckMeshBuilder.AddEndStamp`, `BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs:~145-166`). Its outer
face (at the abutment, facing the approach) is therefore only 1 m tall and floats above the spanned ground — so the
raw embankment cliff shows below it.

**Change:** drop the block's bottom to a level base **at the natural ground under the abutment** (plus a small
embedment), instead of a fixed 1 m. The outer face becomes a full-height solid wall from the soffit down to the
valley floor — covering the cliff. Never *shallower* than today (`min(soffit − AbutmentDepthMeters, ground − embed)`),
so it's a strict improvement and legacy-safe.

### 3.2 The one piece of data needed: ground Z at the abutment

The natural terrain elevation under each section is already on the cross-section as
`UnifiedCrossSection.OriginalTerrainElevation` (used for the clearance metric at `BridgeProfileSolver.cs:~436-438`).
Thread it to the mesh builder:

1. **`BridgeStation.GroundZ`** (new `float`, NaN if unknown) — set from `cs.OriginalTerrainElevation` in the
   snapshot capture (`BridgeProfileSolver.ApplyToSpan`, `~451-461`).
2. **`RoadCrossSection.TerrainElevation`** (new `float?`, world-Z, null if unknown) — set in
   `BridgeDeckDaeExporter.StationToWorldCrossSection` from `st.GroundZ + terrainBaseHeight` (matching how
   `CenterElevation`/edge Z get the base-height offset); null when `GroundZ` is not finite.

The **legacy whole-spline path** builds `RoadCrossSection`s via `CrossSectionConverter` which never sets
`TerrainElevation` → null → the mesh builder keeps today's fixed 1 m block. Byte-identical for legacy + any test
that doesn't set it.

### 3.3 The mesh change (`AddEndStamp`)

Replace the fixed `drop` with a level base plane:

```csharp
float soffitMinZ = MathF.Min(oTL.Z, oTR.Z);
float baseZ = soffitMinZ - p.AbutmentDepthMeters;                 // legacy floor (≥1 m below soffit)
if (s.TerrainElevation is float g && float.IsFinite(g))
    baseZ = MathF.Min(baseZ, g - AbutmentGroundEmbedMeters);     // …but reach the ground when known
// bottom corners at flat baseZ (oBL/oBR/iBL/iBR), same 5 faces, same winding
```

`AbutmentGroundEmbedMeters` = a hard-coded const (~0.5 m) so the wall keys slightly below grade with no gap. Same
vertex *count* and face topology as today — only the four bottom corners' Z changes — so DAE structure and the
collision clone are unaffected; only positions move.

### 3.4 Excavator / everything else — no change

The wall is pure mesh. The excavator, painter, grade-sep, DecalRoads are untouched. No heightmap mutation, no new
config wiring in Phase 1.

### 3.5 Phase-1 honest limitation

The wall **covers** the cliff; it does not flatten the ground. Driving beside/under the bridge you'll see a clean
abutment face instead of dirt, but the terrain still steps. Removing the step is **Phase 2**. The two compose: with
Phase 2's fill the wall is partly buried in graded embankment, which is exactly right.

### 3.6 Phase-1 tests

- `BridgeAbutmentWallTests` (mesh builder): given end cross-sections with a low `TerrainElevation`, the end-stamp's
  lowest vertex reaches `≈ ground − embed` (deep wall); with `TerrainElevation = null` the block stays the legacy
  `AbutmentDepthMeters` below the soffit (byte-identical depth); with ground *above* `soffit − AbutmentDepthMeters`
  the depth clamps to the legacy floor (no inverted/short wall).
- Snapshot: `ApplyToSpan` captures finite `GroundZ` from `OriginalTerrainElevation` on each station.
- Regression: existing `BridgeDeckSpanExportTests` / `BridgeDeckDaeExporterTests` still pass (vertex/triangle counts
  unchanged).

---

## 4. Phase 2 — structural terrain fill (`BridgeAbutmentFiller`)

### 4.1 The change

A new heightmap pass that, over the overlap zone `[StartDistance − overlap, StartDistance]` and
`[EndDistance, EndDistance + overlap]`, **ramps the terrain up to the deck end**: at the span boundary the fill
reaches the deck soffit; at the apron tip it blends to natural ground / the existing embankment. This converts the
abutment cliff into a graded embankment the deck emerges from — removing (not hiding) the terrain kink.

### 4.2 Integration point

In `TerrainCreator` (`Terrain/TerrainCreator.cs:~352-400`), the bridge block runs:
`DiagnoseSeams → BuildBridgeDeckProfile → (PlanConstraints, legacy) → RefineSpans → ApplyLowerRoadDips →
BridgeDeckExcavator.Excavate`. Add `BridgeAbutmentFiller.FillApproaches(...)` **immediately after `Excavate`**
(`~399`) and **before** DecalRoad generation, so it mutates the final heightmap once the deck Z is settled and the
under-span carve is done.

```csharp
BridgeDeckExcavator.Excavate(network, heightMap2D, mpp, undercutMeters: p.BridgeDeckUndercutMeters);
if (p.BridgeApproachOverlapMeters > 0f)
    BridgeAbutmentFiller.FillApproaches(network, heightMap2D, mpp,
        overlapMeters: p.BridgeApproachOverlapMeters,
        lateralFalloffMeters: p.BridgeApproachLateralFalloffMeters, // new sibling knob, default ~4
        deckProfile: bridgeDeckProfile);
```

### 4.3 Algorithm (per deck span, per end)

For each span group (same grouping the excavator uses, `StructureSpanId >= 0`):

1. Identify the **boundary station** (first/last span section) and its deck soffit Z
   (`deckZ − thickness`) — the height the fill must reach at the abutment.
2. Walk the approach cross-sections outward up to `overlapMeters`. For each, target
   `z_fill = lerp(soffitZ_at_boundary, naturalZ_at_tip, d / overlapMeters)` where `d` is arc-distance from the
   boundary.
3. Across the section width (± half-width + `lateralFalloffMeters`), **raise** heightmap cells toward `z_fill`,
   with a lateral cosine falloff beyond the road edge so the embankment shoulders blend to natural ground.
   **Raise-only** (never lower — mirror the excavator's one-directional rule) so it can't gouge.
4. Log per bridge: cells raised, max fill, mean approach grade — for review parity with `[BRIDGE-EXCAVATE]`.

This is the re-homed `BridgeAbutmentFiller` from the reverted `feature/bridges` branch (it "grades a fill fillet
from approach embankment down to natural ground under each connected abutment"), but simpler here because the
abutment is corridor-interior: the boundary station and the approach neighbours are the **same spline**, no
junction lookup.

### 4.4 Interaction with the stamp skip

`RoadMaskBuilder` left the span unstamped (§1.1) and the immediate approach **was** stamped to the road surface,
not up to the deck. The filler runs **after** all stamping/blending and only raises ground in the overlap collar —
so it composes cleanly: stamping makes the road embankment, the filler lifts the last few metres up to the soffit.
Watch the seam at `overlap` distance (filler ↔ existing embankment) — the lerp endpoint is `naturalZ`/embankment Z
so it should join continuously; add a render check.

### 4.5 Phase-2 tests

- `BridgeAbutmentFillerTests`: over a flat-approach span the collar cells rise monotonically from natural at the
  tip to soffit at the boundary; raise-only (cells already above target untouched); lateral falloff zeroes out past
  `halfWidth + lateralFalloff`; `overlap = 0` ⇒ no-op.
- Regression: a normal (non-bridge) corridor is untouched; spans with no daylight (deck ≈ ground) produce ~0 fill.

---

## 5. Sequencing & validation

1. **Phase 1a** — `BridgeStation.IsApron` + apron-station capture in `ApplyToSpan` (hard-coded 4 m), thickness/
   feather/depress in the mesh builder. Build + unit tests green.
2. **Render check #1** — regenerate the bridge map; confirm: short bridges read as deck-emerging-from-embankment
   (no floating rectangle, no visible end-wall); driving surface still continuous; no z-fight on the deck/road
   overlap. Note any flyovers where a ground cliff remains (→ Phase 2 candidates).
3. **Phase 1b** — wire the 8-site `BridgeApproachOverlapMeters` plumbing + UI/preset (mechanical).
4. **Phase 2** — `BridgeAbutmentFiller` + `BridgeApproachLateralFalloffMeters`, integrated after `Excavate`.
5. **Render check #2** — confirm the abutment cliff is gone on the flyovers from check #1; embankment-to-natural
   join is smooth; under-span daylight preserved (filler didn't bleed into the span).

Everything is gated by `BridgeApproachOverlapMeters > 0`; default 0 during dev for byte-identical baselines, flip to
4.0 once validated.

---

## 6. Risks & open questions

| Risk | Mitigation |
|------|-----------|
| z-fight deck-top vs road decal over the apron | §3.4(a) depress apron top a few cm below the road surface |
| Apron soffit pokes out of the embankment far end | feather thickness → 0 at the tip (§3.3.2) |
| Deck thickness inflates because span length grew | compute thickness from the **true** span bounds (§3.3.1) |
| Overlap longer than the approach (tiny connector to another bridge) | clamp overlap to the available approach arc-length before the next junction/span; two near bridges keep distinct aprons |
| Phase-2 filler bleeds into the span (kills daylight) | clamp the fill to the overlap collar only; raise-only; render check #2 |
| Filler ↔ existing embankment seam at `overlap` distance | lerp endpoint = natural/embankment Z; verify in render |

**Open questions for the user:**
- **A. Plumbing now or later?** Land Phase 1 with a hard-coded 4 m to iterate the look, then add the 8-site UI/
  preset plumbing — or wire it all up front? *(Recommend: geometry first, plumbing after render check #1.)*
- **B. Default overlap length?** 4.0 m proposed (typical approach slab). Comfortable, or prefer a different default?
- **C. Apron deck-top handling** — depress a few cm below road (§3.4a, recommended) vs skirt-only no-top (§3.4b)?

---

## 7. One-paragraph rationale (for the eventual PR)

In merged-corridor mode the bridge deck already shares the road's smoothed centerline, so the driving surface is
continuous — but the heightmap is stamped up to the road outside the span and left natural inside it, leaving a
terrain cliff at each abutment that the deck box ends flush against ("stamped rectangle"). This change overlaps the
bridge onto its approaches by a configurable `BridgeApproachOverlapMeters`: Phase 1 extends the deck mesh (feathered,
abutment buried) so the deck emerges from the embankment instead of ending in a wall — the cosmetic fix that fully
resolves near-grade bridges; Phase 2 adds a `BridgeAbutmentFiller` heightmap pass that ramps the embankment up to
the deck soffit over the overlap collar, removing the terrain cliff itself for raised flyovers. One parameter, two
halves, both keyed off the same overlap arc-range.
