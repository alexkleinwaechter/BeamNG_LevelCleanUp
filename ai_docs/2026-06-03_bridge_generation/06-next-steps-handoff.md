# Handoff — Bridge Generation: What's Done, What's Next

**Date:** 2026-06-07
**Branch:** `feature/bridges`
**Last commit:** `2f98278` — *feat: Bridge deck vertical profile solver + terrain shave-under-deck*
**Reads with:** `05-bridge-elevation-and-continuity-plan.md` (the elevation/continuity plan + §5 tunables),
`01-spec-simple-bridge-deck.md` (deck mesh spec), `00-findings-and-decisions.md` (decisions D1–D9).

This document is the planning hand-off for the *next* chapter of bridge work. It records the shipped
state, inventories the open items, and recommends a sequence. **TL;DR recommendation:** do a short
**Phase 0 close-out** (visual-validate + 2 sliders + dead-wiring cleanup) first; then **"bridge height"
(a real 3D deck)** is the right next headline feature — but note it *reopens* the terrain-under-deck
decision, so it needs its own short spec before coding.

---

## 1. Where we are (shipped in `2f98278`)

The deck no longer follows sagging terrain, and natural terrain no longer pokes through it.

- **`BridgeProfileSolver.ApplyStructuralProfiles`** (run in `TerrainCreator` *before* DecalRoad gen + deck
  export — one source of truth): per-bridge **cubic Hermite** fitted to the connected approaches in height
  + grade (G0+G1); **sag cap** blends toward the endpoint chord so the deck can't bow below it by more than
  `MaxSagBelowChordMeters` (default 1 m); overshoot guard (parabola → chord) bounds arching; isolated-end +
  unchained-rescue fallbacks; banked-edge recompute. No grade clamps.
- **`BridgeDeckExcavator.Excavate`**: for every heightmap cell under the deck footprint whose terrain is
  *above* the deck, lower it to that section's `deckZ − undercut` (0.05 m); at/below-deck cells untouched.
  Per-section ceiling → the cut follows the deck slope (no flat pad, no kink).
- **Removed:** the export-time `ReconcileBridgeEndpointElevations` band-aid (job moved into the solver).
- **Diagnostics:** `[BRIDGE-PROFILE]` per-seam + `apply` lines (incl. `seamKink`, `minClear`, sag-cap
  factor) and `[BRIDGE-EXCAVATE]`, all via the `*_Info.txt` (`TerrainCreationLogger`) sink.
- **Tests:** `BridgeProfileSolverTests`, `BridgeDeckExcavatorTests` (375 green total).

**Validated by gate (franco_same_prio, log 231012):** artifact was vertical/grade-driven (`xyGap=0`,
sag from steep approaches); after fix, 8/10 bridges have <2.5° seam kink, bridge_82 ≈ 5° at the 1 m sag
tolerance. **Not yet re-validated in-game:** the final shave-to-deck excavator rewrite (the version that
replaced the rejected channel/pad). That's the first checkbox below.

---

## 2. Open items (the menu)

| # | Item | What | Effort | Risk | Depends on |
|---|------|------|--------|------|-----------|
| A | **Visual close-out** | ✅ DONE 2026-06-07 — good enough for now (per handoff). | XS | – | nothing |
| B | **Edge-teeth blur** (the "last exit") | Light gaussian/box smooth of the shaved boundary cells if teeth are visible on sloped/hillside abutments | S | low | A says it's needed |
| C | **Expose tunables in UI** | ✅ DONE 2026-06-07 — `BridgeMaxSagBelowChordMeters` + `BridgeDeckUndercutMeters` are now `TerrainCreationParameters`/`TerrainGenerationState` fields, surfaced as numeric fields in the "Bridge/Tunnel Structure Handling" panel + preset round-trip. | S | low | – |
| D | **§7 dead-wiring cleanup** | ✅ DONE 2026-06-07 — deleted `StructureElevationIntegrator.cs` + its `UnifiedRoadSmoother` wiring (field/ctor/`ConfigureStructureElevationParameters`/Phase 2.3 block) + the `TerrainCreator` call. `StructureElevationCalculator.SampleTerrainAlongStructure` kept (parked for tunnels). | S | low | – |
| E | **Bridge height / 3D deck** | Deck thickness (soffit), side fascia/parapets, optional piers, real gap underneath, abutment walls | L | med | short spec (§4) |
| F | **Step 5 — plan-view normal-only seam** | Blend bridge section `NormalDirection` to the approach at the seam (deck edge orientation). Gate showed `normalΔ` 12–32°, `xyGap=0` | M | med | – |
| G | **Isolated-bridge + approach-grade root cause** | Both-ends-isolated bridges left untouched; steep approach grades (bridge_82 −24%/+17%) come from approaches diving toward the gap (chain fragmentation). Fixing the approaches removes both sag *and* kink | M–L | med | research |
| H | **Tunnels** | Reuse the solver with a below-terrain clearance constraint (plan §12) | L | med | E patterns |

---

## 3. Recommended sequence (and why)

**Phase 0 — close the elevation/continuity chapter (small, do now):** A → C → D (+ B only if A shows teeth).
- Cheap, low-risk, and it leaves the shipped feature clean and tunable for non-developers.
- D removes actively-misleading dead code (two prior agents misread it as the live elevation source).

**Phase 1 — "bridge height" (the next headline feature): E.**
- This is the natural next visual leap: turn the flat ribbon into something that *reads* as a bridge.
- **Important coupling:** E reopens the terrain-under-deck decision. We deliberately *shave terrain to just
  under the deck* (no gap) because a 1 m channel produced rasterization teeth at the open edges. A real 3D
  deck **with side fascia + abutment walls hides those edges**, so E lets us go back to a **proper visible
  gap with clearance + piers** and the teeth become a non-issue (covered by structure). So E isn't just
  cosmetic — it's the "right" version of the excavation, which is why it deserves its own spec (§4).

**Phase 2 — polish: F, then G.**
- F (normal-only seam) is independent; do it when the edge-orientation skew actually bothers visually.
- G is the deepest fix (regrading approaches) but out-of-scope-creep risk; treat as research.

**Phase 3 — H (tunnels).**

---

## 4. "Bridge height" — what to spec before coding (E)

Naming it now so the next session starts from questions, not a blank page. Decisions needed:

1. **Deck thickness / soffit.** Driving surface stays at `TargetElevation`; add a bottom face at
   `TargetElevation − deckThickness`. New tunable `BridgeDeckThicknessMeters`.
2. **Side fascia / parapet / railings.** Vertical faces at the deck edges (and optional railing geometry).
   These are what hide the terrain-cut edges → unlocks reopening the gap.
3. **Underside gap + excavation revisited.** With thickness + fascia, switch `BridgeDeckExcavator` from
   "shave to just under the deck" back to "clear to `deckZ − deckThickness − underClearance`" so there's
   daylight under the span. The teeth that killed the gap before are now behind the fascia. (Keep the
   shave-to-deck path as the fallback for "no-height" bridges.)
4. **Piers / pillars (optional, harder).** Sample terrain along the centerline; drop columns from the soffit
   to terrain at an interval; skip over water/very-short spans. Pure visual; needs a column mesh + placement.
5. **Abutment walls.** Short retaining faces where the deck meets the cut, closing the end gap cleanly
   (replaces the rejected flat pad with actual geometry).
6. **Mesh generation.** Currently `BridgeDeckDaeExporter` builds a single flat ribbon (top surface only).
   E means extending it to a box/soffit + fascia (and the collision mesh) — biggest code surface in E.

Non-goals to keep E bounded: superelevated/banked decks, multi-span PVIs, suspension/cable geometry.

---

## 5. Pointers

- **Code:** `BeamNgTerrainPoc/Terrain/Export/BridgeProfileSolver.cs`, `…/BridgeDeckExcavator.cs`,
  `…/BridgeDeckDaeExporter.cs`; wiring in `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` (3b-bridge block).
- **Tunables:** plan doc `05-…-plan.md` §5 (synced to shipped code 2026-06-07).
- **Dead wiring (item D):** `UnifiedRoadSmoother.cs` (Phase 2.3 `IntegrateStructureElevationsSelective`
  call) + `Osm/Processing/StructureElevationIntegrator.cs` (the "store profile, don't apply" no-op).
- **Log markers:** `[BRIDGE-PROFILE]` (seam + apply + summary), `[BRIDGE-EXCAVATE]` — in
  `levels/<lvl>/MT_TerrainGeneration/logs/*_Info.txt` (only on bridges-enabled runs).
- **Gate method:** regen a bridges-on map, read the `apply` lines; `seamKink` = drivability, `minClear`
  (pre-excavation) = how much terrain pokes, `sag-capped (f=…)` = how hard the sag cap engaged.

---

## 6. Definition of done for Phase 0 (so the chapter can be called closed)

1. ✅ In-game: deck spans flush + grade-continuous at both ends, no terrain poke-through (A, validated
   2026-06-07 — good enough). Edge-teeth blur (B) stays bucket-list unless a future map shows teeth.
2. ✅ Sag tolerance + undercut adjustable from the UI without a rebuild (C).
3. ✅ Dead `StructureElevationProfile` wiring removed; 375 tests green; no new grade clamps (D).

**Phase 0 is closed** (A done/accepted, C + D shipped 2026-06-07; B deferred as bucket-list). Next headline
feature is **E — bridge height / 3D deck** (spec in §4).
