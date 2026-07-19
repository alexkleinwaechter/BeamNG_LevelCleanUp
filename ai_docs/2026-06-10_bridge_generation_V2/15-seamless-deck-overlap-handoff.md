# Doc 15 — Seamless intersecting decks: the overlap region of merging bridges (handoff)

**Date:** 2026-07-07 · **Status:** IMPLEMENTED (same day) — §3a/§3b/§3c/§3d + §5.1 all in, 786 tests
green, awaiting regen + render validation (§5.2/§5.4). Flag `EnableSeamlessDeckOverlap` (PascalCase in
the preset `bridgeRules` node). Conformance zone: `BridgeProfileSolver.ConformDeckOverlapZone` (the
footprint test terminates the walk; length cap min(span/2, 250 m) is a safety only — the original 60 m
capped five shallow Manhattan merges MID-overlap, run 230330; ease run max(width, 10 m, 10 m per metre
of boundary Δ so the ease never adds a kink); generalized sampler `TrySampleDeckSurfaceAt`. Mesh trims:
`BridgeDeckTrim` → `BridgeDeckMeshBuilder` (masked parapet runs get interior cap faces; end stamps
skipped at ContinuesOntoDeck ends), computed in `BridgeDeckDaeExporter.ComputeDeckTrim` — criterion is
GEOMETRIC COPLANARITY (edge point inside the other deck's plan footprint AND |Δz| ≤ 0.5 m), NOT the
landing graph: §3b's "conforming pairs" gating both over-opened (a pair's footprints also overlap where
the decks cross at other layers → holes on stacked decks, render 2026-07-07) and under-opened (two
ramps conformed onto the same trunk share a roadway without being a pair → walls across the gore).
Area metric: `DeckSeamDiagnostic.OverlapStations/OverlapMaxGapMeters` + `overlapMaxGap` in the
deck-seam summary.
**Branch:** `feature/bridge_embankment_containment` @ `4b93dcf` (769 tests green)
**Read this alone — self-contained.** Follow-up to doc 14 (deck-to-deck continuity, IMPLEMENTED +
LOG-VERIFIED runs 214227/220209: landing span ENDS now meet the trunk deck at G0+G1 — j106 zGap
1,82→0,00). Doc 14 fixed the seam POINT; this doc is about the seam AREA.

---

## 0. The prompt (user, 2026-07-07, after run 220209)

> There is one thing we didn't take into account: The whole intersecting part of two or more bridges
> must produce a seamless deck. If not we have a problem when elevation is in play.

## 1. The problem, concretely

A deck-deck merge (ramp 904452323 landing on trunk 1546435469 at j106; the j103 gore of 56/58/14) is
built as **independent deck meshes, one per span** (`BridgeDeckDaeExporter` → `BridgeDeckMeshBuilder`
from each span's `BridgeSpanSnapshot`). In the intersecting part of their footprints, TWO surfaces
coexist. Doc 14 conforms exactly ONE point of the landing span (end-station center Z + longitudinal
grade + the center's `offset·sin(bank)`), so across the rest of the overlap:

1. **Surface mismatch grows with elevation.** The ramp's end cross-section is oriented by ITS OWN
   bank; the trunk surface plane at the landing has cross-slope `gTrunk·dot(tTrunk, nRamp)` plus its
   own bank. On a graded/banked trunk the ramp's end edges sit centimeters-to-decimeters off the
   trunk surface, and every EARLIER ramp station still inside the trunk footprint (a shallow-angle
   merge overlaps for tens of meters) follows the ramp's own Hermite, not the trunk plane →
   steps/z-fighting exactly where a vehicle changes lanes across the gore. Flat decks hide this;
   "when elevation is in play" it scales with grade × overlap length.
2. **Parapets cross the roadway.** `BridgeDeckMeshBuilder.BuildParapet` extrudes a solid trapezoid
   along BOTH edges over the FULL span length, unconditionally (`ParapetHeightMeters > 0`). At a
   merge: the ramp's parapets run to its end ON the trunk centerline (a 0.9 m wall standing across
   the trunk deck), and the trunk's edge parapet cuts across the ramp deck where the ramp footprint
   crosses the trunk edge. Undrivable, independent of any Z mismatch.
3. **End-stamp under the merge end.** `AddEndStamp` puts a solid block (drop `AbutmentDepthMeters`,
   length `EndStampLengthMeters`) under BOTH span ends unconditionally — at a merge end it hangs
   through the trunk deck mid-air. (Doc 13 suppressed the TERRAIN abutment package at
   `ContinuesOntoDeck` ends; the MESH layer never learned about them.)

## 2. What doc 14 already provides (reuse, don't rebuild)

- `StructureSegment.Start/EndDeckLanding` (`DeckLandingRecord(DeckSplineId, DeckStation, JunctionId?)`)
  — WHERE each merge end lands; junction-driven detection included. Records exist whenever doc-13
  suppression is on.
- `BridgeProfileSolver.TrySampleDeckSurface` — trunk surface Z + directional grade at a landing
  (station + lateral offset + bank). Needs generalizing from "sample at the landing end's center" to
  "sample at an arbitrary XY near a station" (project point onto the trunk polyline, lerp, add
  `lateral·sin(bank)`).
- Landing-dependency solve order + cycle re-pass (`OrderSpansByLandingDependencies`, the stale-anchor
  re-pass) — guarantees the landed-on deck is FINAL when the landing span reads it.
- `MaxLandingAnchorZGapMeters = 6` — the merge-vs-crossing classifier: everything below only applies
  to ends whose landing anchor APPLIED (a plan-view crossing must not conform, its overlap is
  legitimate stacking).
- `DiagnoseDeckToDeckSeams` — extend as the metric (see §5).
- Doc-13 `Start/EndContinuesOntoDeck` — the mesh-layer suppressions in §3b/§3c key off the same ends.

## 3. Design direction

**a. Deck conformance zone (in-solver, the core).** After a landing span's profile is anchored
(`ApplyToSpan`, post-Hermite), walk its span sections backward from the landed end. For every section
still overlapping the landed-on deck footprint (any of center / left edge / right edge within the
trunk's half-width + small margin of the trunk centerline), set the surface EXACTLY onto the trunk
plane: sample the trunk surface independently at the section's center AND both edge points
(generalized `TrySampleDeckSurface`), overwriting `TargetElevation` / `LeftEdgeElevation` /
`RightEdgeElevation`. This makes the intersecting part coplanar by construction — bank included,
because the edges are sampled, not offset. Past the last overlapping section, ease the correction out
over a transition run (`(1−u)²(1+2u)`, run ≈ max(own width, 10 m)) so no new kink appears where
conformance ends. The snapshot is captured after (`CaptureSpanSnapshot`), so deck mesh, excavator and
bridge DecalRoads all inherit the conformed geometry for free. Cap total walk length (e.g. min(span/2,
60 m)) — a merge overlap is an end phenomenon, not half the bridge.

**b. Parapet openings (mesh layer).** The union roadway must have parapets only on its OUTER
boundary. Per span and per edge, compute a boolean mask per station: suppress the parapet segment
where that edge point lies INSIDE another span's deck footprint (use `network.BridgeSpans` — all
snapshots are final at export time; `BridgeDeckDaeExporter.Export` already has them). Landing span:
this naturally opens the inner parapet through the gore and kills both walls at the merge end; trunk:
it opens exactly the gore-mouth segment the ramp drives through. `BuildParapet` gains segment
start/stop from the mask (it extrudes station-by-station — split into runs of unsuppressed stations).
Only pairs where at least one span CONFORMS to the other (§3a applied, or an end-to-end handoff)
count — stacked crossings (the 6 m cap cases, e.g. 51 under the Brooklyn deck) keep full parapets.

**c. End-stamp + end-cap suppression at merge ends.** `AddEndStamp` skips ends whose segment has
`Start/EndContinuesOntoDeck` (mirror of doc-13's terrain suppression, now for the mesh). The vertical
end CAP face of the deck box at a conformed merge end is flush under the trunk surface — harmless;
keep it (it closes the mesh).

**d. Flag.** New `EnableSeamlessDeckOverlap` in `BridgeRuleSystemOptions` (preset `bridgeRules` node,
PascalCase!, default off = byte-identical), consumed only when `EnableDeckToDeckContinuity` is also
on (conformance without end anchoring is meaningless). NOT in `AnyEnabled`.

## 4. Cautions

- **Conform only merge ends** — an anchor skipped by the 6 m cap (crossing) must never conform;
  the overlap of stacked decks is correct as-is.
- Multi-deck chains (56 lands on 58 lands on 2): conformance target is each span's OWN landing deck;
  the solve order + re-pass already make targets final in dependency order, so chains compose.
- j103's two-interior-deck situation (58 and 14 both continue through the junction, ~7 m apart
  vertically per run 220209): they do NOT merge — no landing record between them, no conformance,
  no parapet opening. Only 56 (landing on 58) conforms there. Verify in render.
- The trunk surface under the gore is authoritative and UNTOUCHED — conformance is one-directional
  (landing span adapts), like the junction authority rule.
- DecalRoad layers on the ramp inherit conformed elevations via the snapshot; check the gore area
  doesn't double-paint markings from both spans (existing behavior, observe in render).
- Terrain: nothing here writes terrain. Doc-13 suppressions stay untouched.

## 5. Verification recipe

1. Extend `DiagnoseDeckToDeckSeams` (or add `[BRIDGE-PROFILE] deck-overlap` lines): per landing end,
   max |landing surface − trunk surface| over ALL overlapping stations × {center,L,R} — the AREA
   metric (today only the end center is ≈0). Baseline first (flag off), then expect ≈0 with flag on.
2. Parapet: regen → deck DAEs at the j106 merge and j103 gore have no wall across a roadway
   (render/screenshot judgement); end-stamp absent at merge ends.
3. 769+ tests; both flags off byte-identical; DAM-REPORT unchanged (no terrain writes).
4. Render (user judges): drive trunk→ramp and ramp→trunk across the gore at speed — no step, no
   wall, no z-fighting shimmer.

Log dir: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`.
History: doc 14 + same-day fixes `5e3163b`/`355abf4`/`e18dbfa`/`4b93dcf` (sessions in memory
`bridge_rule_system_v2`); mesh layer: `BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs`
(BuildParapet ~L111, AddEndStamp ~L151), exporter `BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs`.
