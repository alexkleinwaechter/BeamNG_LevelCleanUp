# Doc 14 — Deck-to-deck continuity: kinks at bridge→bridge merges (handoff)

**Date:** 2026-07-07 · **Status:** IMPLEMENTED (same day, session 14) behind `EnableDeckToDeckContinuity`
(preset `bridgeRules` node, default off = byte-identical) — awaiting regen + render verification. 766 tests
(753 + 13 new in `DeckToDeckContinuityTests`). What landed, mapped to §3:

- **(a) authority:** `UnifiedRoadSmoother.PinOnDeckJunctionsWithAuthority` — junction-first gather; the span
  the junction is INTERIOR to (>10 m from its span ends, `BridgeToBridgeContinuity.DeckEndEpsilonMeters`)
  owns the Z (ties: priority, then span length); landing spans' plan caps suppressed + logged; the authority
  pin is EXACT (lowers an inflated value); single-span junctions keep legacy raise-only.
- **(b) anchor:** `BridgeProfileSolver.ApplyToSpan` reads `Start/EndDeckLanding` → `TrySampleDeckSurface`
  (trunk center Z lerped at the landing station + `offset·sin(bank)`, grade = surface directional derivative
  along the ramp +s); `RefineSpans` orders spans priority-desc then longer-span (circular landings warned);
  `FindConnectedRoadContributor` gains a flag-gated deck-candidate fallback (endpoint decks + span-tagged
  mid-spline contributors, local grade via `EstimateLocalGrade`).
- **(c) records:** `StructureSegment.Start/EndDeckLanding` (`DeckLandingRecord(DeckSplineId, DeckStation,
  JunctionId?)`) — junction-driven detection resolves ends the radius test misses (j103 start case); records
  are ALWAYS written when doc-13 suppression is on, only the flag flip is gated on the new flag.
- **(d) diagnostics:** `BridgeProfileSolver.DiagnoseDeckToDeckSeams` after `RefineSpans` in `TerrainCreator` —
  `[BRIDGE-PROFILE] deck-seam … zGap gradeΔ headingΔ` per recorded landing + summary; emits on BASELINE runs
  too (flag off), so regen once before enabling to capture the ≈+1.5 m j106 baseline.

Verification: §5 recipe unchanged — regen flag-off (baseline deck-seam lines), regen flag-on (zGap→≈0, j106
single write ≈24.9, j103 chain gone, `[DAM-REPORT]` 156/148/262 not worse), then render drive-through.

Original handoff below, unchanged.

---
**Branch:** `feature/bridge_embankment_containment` @ `530f34f` (753 tests green)
**Read this alone — self-contained.** Follow-up to doc 13 (bridge-to-bridge abutment suppression, VERIFIED: terrain no longer terraforms at deck-deck joints). Doc 13 fixed the TERRAIN at these joints; this doc is about the DECKS themselves.

---

## 0. The prompt (user, 2026-07-07)

> We need a followup document for bridge continuity between bridges. Sometimes we have kinks because
> of different elevation at the ends. It must be handled like in junction harmonization for roads.
> **Drivability is king!** See as example continuity for bridge_904452323 and bridge_1546435469.

Log: `…\manhattan\MT_TerrainGeneration\logs\Log_TerrainGen_4096_20260707_135439_Info.txt`
Preset: `d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json` (flags incl.
`EnableBridgeToBridgeAbutmentSuppression`, `EnableContiguousSpanConsolidation`,
`EnableNaturalProfileAnchor`, `EnableSpanSolveOrder` all on).

## 1. The example, decoded (all numbers from log `135439`)

bridge_904452323 = ramp span on **spline 58** (motorway_link, p9500), ≈[190, 797].
bridge_1546435469 = trunk deck span on **spline 2** (motorway, p10000), [1220, 3562].
The ramp's END lands mid-span on the trunk deck at **junction 106** (58 station 796.6 ↔ 2 station 2702).

```
[BRIDGE-PROFILE] spline=58 end connected=no z=26,43 gBridge=-0,5% (isolated endpoint)
[BRIDGE-PLAN] junction-on-deck junction=106 spline=2  span=1546435469 station=2702,0m z=18,92->24,90 contributors=2[2:p10000,58:p9500]
[BRIDGE-PLAN] junction-on-deck junction=106 spline=58 span=904452323  station=796,6m  z=24,90->34,53 contributors=2[2:p10000,58:p9500]
```

**The kink:** the ramp deck end solves in ISOLATION to z=26.43 while the trunk deck at the landing
station is ≈24.90 → a ~1.5 m step + grade break exactly at the merge — undrivable seam. (The doc-13
suppression removed the terrain pillar there; the two DECK MESHES now simply disagree in mid-air.)

**Counter-example proving the mechanism shape:** the 70→14 handoff (endpoint meets endpoint) is
already perfect —

```
[BRIDGE-PROFILE] spline=14 start road=70 connected=yes z=28,26 approachZ=28,26 zGap=0,00 gradeΔ=1,6deg
[BRIDGE-PROFILE] spline=462 end road=70 connected=yes z=28,26 approachZ=28,26 zGap=0,00 gradeΔ=3,4deg
```

Deck-deck G0 continuity happens automatically wherever the profile solver's endpoint anchor is
ALLOWED to see the neighbour deck. The gap is a filter, not a missing subsystem.

## 2. Root causes (verified in code)

1. **Seam anchor filter drops mid-span landings.** `BridgeProfileSolver.FindEndpointContributor`
   (BridgeProfileSolver.cs ~1133) selects junction contributors with
   `SplineId != bridge && !BridgeDeckDaeExporter.ShouldGenerateDeck(c.Spline) && c.IsEndpoint`.
   At j106 the trunk contributor (spline 2) is MID-SPLINE (station 2702 of 6970) ⇒ fails
   `IsEndpoint` ⇒ `connected=no` ⇒ the ramp span's end Hermite has NO anchor and re-curves from its
   own approach only. (The `!ShouldGenerateDeck` filter is ALSO wrong for this case — for merged
   corridors it evaluates per-spline and lets 70 through by luck; a separated bridge spline neighbour
   would be dropped even endpoint-to-endpoint.)
2. **`PinOnDeckJunctions` has no authority rule at deck-deck junctions.** Junction 106 is pinned
   TWICE: spline 2's span writes the correct 24.90 (its own deck), then spline 58's span OVERWRITES
   with 34.53 — which is span 904452323's PLAN deckEnd cap, ~9.6 m above BOTH final decks (plan
   estimate vs solved-profile drift; same 34.53 appears at j103/j251). Last-writer-wins, junction
   record ends up inconsistent with every real surface at the joint. (Doc 13's junction-fill skip
   means this no longer stamps terrain, but the value still poisons any reader of
   `HarmonizedElevation` — e.g. j103 then feeds 34.53→41.23 into span 281554390's raise chain.)
3. **Per-span independence.** `RefineSpans` re-curves each span from its own approaches; nothing
   makes a lower-priority span's end meet the already-solved higher-priority deck it lands on.
   `EnableSpanSolveOrder` (A5) already solves spans in DESCENDING owner priority and carries pinned
   deck sections into later spans as obstacles — the natural vehicle for a "trunk first, ramp
   adapts" constraint — but the carry contributes only clearance obstacles, not end anchors.
4. Heading at these merges is sharp (58 start headingΔ=24.4°, 462 end 40.2°, seams>3° = 4) — an
   endpoint-Z-only snap is necessary but not sufficient; the END REGION should meet the trunk deck
   SURFACE (z + longitudinal slope + bank at the landing offset), the deck analogue of
   `ComputeTJunctionElevation`'s surface-aware snap.

## 3. Design direction (user doctrine: like road junction harmonization; drivability first; in-solver)

Treat a deck-deck merge exactly like a road T-junction, with the trunk as the through road:

- **a. Authority rule:** at a junction whose contributors are all decks, the deck being LANDED ON
  (the continuous / higher-priority span — spline 2 at j106) owns the junction Z; the landing span
  (58) ADAPTS. `PinOnDeckJunctions` must never let a later span overwrite with its plan deckEnd; cap
  values must come from FINAL solved deck Z (or at minimum the through deck's pin), not the plan
  estimate.
- **b. End anchor for mid-span landings:** extend the seam-anchor search to accept a DECK
  contributor that is mid-spline: anchor z = the trunk deck SURFACE at the landing station and
  lateral offset (TargetElevation + longitudinal slope, + `offset·sin(bank)`), anchor grade = trunk
  deck's local dZ/ds projected onto the ramp's +s (the existing Hermite machinery, doc 15/`ApplyToSpan`,
  consumes exactly this). Solve order guarantees the trunk is final first (`EnableSpanSolveOrder`).
  Drop/loosen the `!ShouldGenerateDeck` exclusion for span-tagged contributors so endpoint-to-endpoint
  deck seams are anchored BY DESIGN, not by the merged-corridor luck that saved 70↔14.
- **c. Reuse doc-13 detection:** `StructureSegment.Start/EndContinuesOntoDeck` already marks these
  ends. Extend the detection to RECORD the landing (spline id + nearest station + junction id if
  any) on the segment, so the profile solver doesn't re-search. (Doc-13 note: span 904452323's START
  did NOT get flagged — station 190.5 at j103 on spline 14's deck, apparently just outside
  `halfWidth+1 m`; when the landing record is junction-driven, use the junction contributors rather
  than the radius test for junction-connected ends, and the start side comes in too.)
- **d. Diagnostics first (cheap, this session's first task):** extend `[BRIDGE-PROFILE]` to report
  deck-deck seams instead of `connected=no` — one line per `ContinuesOntoDeck` end with
  `deck=<spline> zGap=<endZ − trunkSurfaceZ> gradeΔ headingΔ`. That turns the kink into a measurable
  regression metric before any fix (58 end expected: zGap ≈ +1.53 m today → ≈ 0 after).

Flag-gate (e.g. `EnableDeckToDeckContinuity`, preset bridgeRules node, default off = byte-identical);
guard rails: 753 tests, `BridgeProfileTests`/`BridgeSeamTests` families, doc-13's
`RetargetPinLockedEndpointTests` (junction 106's z change must not re-awaken dam transplants — the
CORRECT j106 z ≈ 24.9 is LOWER than today's 34.53, so street-side effects should only improve).

## 4. Cautions

- Solve-order dependency: the ramp anchor needs the trunk's FINAL deck profile → b must read after
  the trunk's RefineSpans pass (A5 ordering gives this for descending priority; equal-priority
  deck-deck merges need a tiebreak — longer span / continuous-at-junction wins, mirroring
  `ComputeEqualPriorityJunctionElevation`).
- j103 is a THREE-deck junction (56, 58 land on 14): authority = 14 (the deck landed on), both
  ramps adapt; the 34.53→41.23 chain there disappears when caps read final z.
- Circular landings (A lands on B, B lands on A) shouldn't exist physically; detect and log-warn,
  keep first-solved authority.
- Doc 13's terrain suppression must stay untouched — this work is DECK PROFILE only; nothing here
  may write terrain.
- The FlattenSideRoadDams street containment (156/148/262 +6…+11 residuals) is STILL open and
  separate — but note overlap: fixing j106/j103's inflated z (root of some transplants) may shrink
  those residuals; re-read the DAM-REPORT after this lands before designing the street fix.

## 5. Verification recipe

1. Diagnostics first: regen → `[BRIDGE-PROFILE]` deck-deck lines exist; 58 end zGap ≈ +1.5 m
   recorded (the baseline).
2. After fix: 58 end zGap ≈ 0, gradeΔ small; j106 single consistent z ≈ trunk deck; j103 chain
   caps at final deck z; `[DAM-REPORT]` splines 156/148/262 not worse (expect same or better).
3. Render (user judges): drive the ramp 904452323 onto deck 1546435469 — no step, no kink; same for
   the j103 three-deck merge onto the Brooklyn Bridge trunk.
4. 753+ tests green; flag off byte-identical.

Log dir: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`.
History: doc 12 (§3 retarget divergence fix, verified), doc 13 (+13b plan) (b2b abutment
suppression, verified), sessions 1–13b in memory `bridge_rule_system_v2`.
