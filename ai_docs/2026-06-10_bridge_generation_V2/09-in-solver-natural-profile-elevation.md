# Doc 09 — In-solver bridge elevation: the natural-profile anchor

**Date:** 2026-07-06 · **Status:** Phases 1+2 IMPLEMENTED (`8b22255`…`e892277`, 720 tests) ·
**Phase 3 VALIDATED** (§9: over-max 115 215 → 5 547, runaway raises 6 → 0) — two revealed
follow-up problems in §9.2/§9.3 ·
**Branch:** `feature/bridge_embankment_containment`
**Supersedes the post-solve raise path of doc 08** (containment take-3 stays as the seed for the
generalized decay). Read doc 08 §7/§7c first — this doc is the architectural answer to the same
"Damm" problem, driven by the Manhattan render where it turns catastrophic.

---

## 0. TL;DR

On flat maps (Manhattan: GeoTIFF max height **41.95 m**) the bridge machinery drives roads to
**58–130 m** and decks to **~81 m**, overflowing the terrain height ceiling. 31 811 heightmap pixels
came back over-max in one 2048 run → the "PRE-SAVE SPIKE PREVENTION" clamp/quantization overflow
renders as a field of jagged spikes and gouges (user screenshot 2026-07-06).

Root cause is **not** a bad clamp and **not** the last commits' containment code (the affine decay
can only shorten a dam, never create one). It is a two-stage failure:

1. **The solve lifts non-bridge roads far above their natural elevation** (the dam). Road 50's plan
   elevation is ~16 m; the solve delivers up to **69 m** because a junction it terminates at was
   inflated by nearby bridge elevation but **not flagged** as bridge-raised, so the affine junction
   leveling spread its +58 m endpoint error over the whole 1485 m road (`meanAbs 28.98 m`).
2. **A post-solve pass then amplifies the dam into a feedback loop.**
   `GradeSeparationResolver.ApplyApproachRaiseRamps` measures each bridge's clearance against the
   **already-lifted solved** lower road (`clearBefore=-30.24 m` — the road sits *above* the deck) and
   raises the deck 36.94 m at a 104 % ramp, with a deliberate no-grade-clamp policy.

**The fix (this doc):** enforce one invariant *inside* the solve —

> When the solve ends, every road-surface and bridge-deck elevation is final and correct. A non-bridge
> road sits on its **A0 natural profile** except where it *genuinely connects* to a raised road at a
> junction, where it climbs over a class-slope ramp and returns to natural. Nothing after the solve
> may change a road or deck elevation; post-solve may only shape **bare terrain** to match.

Once lower roads hold ~16 m, deck clearance holds by construction (the planner already pinned decks to
clear the A0 lower road) and `ApplyApproachRaiseRamps` is **deleted**, not relocated.

---

## 1. Evidence (Manhattan 2048, log `…_145856_Info.txt` / `…_Warnings.txt` / `…_Timing.txt`)

Level: `manhattan`. Bridge summary:
`spans=68 raised=17 softPinnedSections=28678 junctionRaises=11 onDeckJunctionPins=22
dipPinnedSections=6535 crossings=179 railWater=29 mode=sparse-soft`.

**Terrain ceiling & overflow (the visible destruction):**
```
Timing:   Max height: 41,95215
Warnings: PRE-SAVE SPIKE PREVENTION: Fixed 31811 problematic height values:
            - Over-max values (>= 41,95215m): 31811 [GeoTIFF only]
          SPIKE VALIDATION WARNING: 1 potential spikes detected after pre-save fix.
```
The spike-prevention is a downstream band-aid; it can only clamp to 42 m and cannot recover a surface
that was asked to sit at 58–130 m.

**Roads driven over the ceiling (`[DAM-REPORT]`, final TargetElevation vs A0 estimate):**
```
spline=44  primary     maxDev=+130,65 @s=99m  meanAbs=21,10  nearestSpan=25564664 d=82m
spline=50  primary     maxDev= +58,21 @s=2m   meanAbs=28,98  len>1m=1485m(whole)  nearestSpan=25562442(raised) d=33m
spline=162 residential  maxDev= +57,96        meanAbs=32,69  nearestSpan=25562442(raised)
```

**The lift is not the road's own doing (spline 50 `estimate-vs-final`, as the LOWER road):**
```
upper=56 lower=50  estZ=16,76  finalZ=69,38  delta=+52,62  action=AlreadyClears
upper=87 lower=50  estZ=16,95  finalZ=67,89  delta=+50,93  action=AlreadyClears
upper=19 lower=50  estZ=16,69  finalZ=40,92  delta=+24,24  action=RaiseBridge
upper=0  lower=50  estZ=18,65  finalZ=22,12  delta= +3,47  action=AlreadyClears
```
A0 (natural) ≈ 16–18 m everywhere; the solve lifts it to 22–69 m, tracking whichever raised deck is
nearby. The planner decided correctly against A0 ("AlreadyClears"); the solve then diverged from what
the planner assumed.

**The post-solve amplifier (`[BRIDGE-RAMP]`):**
```
crossing upper=87 lower=50 t=0,13 clearBefore=-30,24/6,70m deficit=36,94m
raise    upper=87 span=[365,1418] raise=36,94m uniform (worst 50) rampStart=35,4m@104,3% rampEnd=68,2m@54,2% [STEEP]
floor SKIPPED near abutment: upper=87 lower=50 t=0,13 minZ=74,59
```

## 2. Why the containment missed it (the mechanism, precisely)

`UnifiedRoadSmoother.ApplyAffineLeveling` (`UnifiedRoadSmoother.cs:2909`) levels each road's endpoints
to its junction targets. Doc 08 §7c added a **decay**: at a *bridge-raised* junction the endpoint
error is metres, so instead of tilting the whole road (`AffineJunctionLeveler.Apply`, full-length
spread) that end climbs over a class-slope run and returns to the solved profile beyond. The decay
fires only for endpoints in `network.BridgeRaisedJunctions` (`UnifiedRoadSmoother.cs:1321`) — the set
`RaiseJunctionsAlongApproachRamps` + `PinOnDeckJunctions` explicitly raised.

Road 50's inflated **start** junction is **not** in that set (it inherited a high Z indirectly — a
raised bridge-approach contributor sits at it, but it is outside the approach-ramp run the M1 pass
walks). So the decay never fires there, and the legacy full-length affine spread tilts the entire
1485 m road up ~29 m. The affine-decay log confirms road 50 got decay only at its *end* (small
errors), never at its +58 m start.

**Generalization:** the decay must key off "**is this junction inflated above its natural A0?**", not
"did a specific bridge pass flag it". A0 is the reference that both *detects* inflation and *defines*
where the road returns to.

## 3. The invariant (design contract)

Restated from §0, with the enforced consequences:

- **Solver owns road-surface + deck elevation.** `TargetElevation` on every road/deck cross-section is
  final when `SmoothAllRoads` returns.
- **Non-bridge roads = A0 natural profile**, except within a class-slope ramp of a *genuine* raised
  connection. "Genuine connection" = the road terminates/joins at a junction whose elevation is
  legitimately raised (a real embankment to a bridgehead). A road that merely passes **under** or
  **beside** a bridge (grade-separated, no shared junction) is **not** lifted at all.
- **Clearance & dip rules apply only at true crossings.** Deck clears the lower road by the configured
  minimum; a lower-priority lower road dips (doc 28 coherent underpass, already pre-pinned). These are
  decided pre-solve against A0 and honoured by the solve.
- **Post-solve may only shape bare terrain.** Owner-guarded stamps that never overwrite a solved road
  or deck surface are allowed after the solve; anything that would change a road/deck Z is not.

## 4. Components

### C1 — A0 as the natural reference (the "anchor")

`network.EarlyElevationEstimate` (built once in `SmoothAllRoads`, `UnifiedRoadSmoother.cs:270`;
per-cross-section smoothed centreline-DEM) is already the planner's A0 and the `[DAM-REPORT]`
baseline. Promote it to a first-class solve input:

- Provide `A0(section)` and `A0(junction)` (mean of the junction's contributors' A0 at the junction
  station) lookups to the elevation passes.
- A junction is **elevation-inflated** when `harmonizedZ − A0(junction) > InflationThresholdMeters`
  (seed 1.5 m — the existing `AffineDecayMinErrorMeters`). This replaces the `BridgeRaisedJunctions`
  membership test as the decay trigger and catches indirectly-inflated junctions (road 50's start).

*Why A0 is safe as the reference:* for ordinary junctions on steep terrain (winningen) the harmonized
Z sits within ~1 m of A0, so the inflation gate does **not** fire — legitimate general smoothing
(tracks at ±8 m off A0) is untouched. Only bridge-induced lift exceeds the gate.

### C2 — Generalized connection-decay (the enforcement, primary fix)

Change the decay gate in `ApplyAffineLeveling` (`UnifiedRoadSmoother.cs:2938-2949`) from
"endpoint ∈ `BridgeRaisedJunctions`" to "endpoint's junction is **inflated** (C1) by ≥
`AffineDecayMinErrorMeters`". Everything else in the decay stays: run length
`AffineDecayRunMeters` = class-normal-slope-sized, clamped `[60, 300] m`; the eased
`(1−u)²(1+2u)` weight; both-null legacy path byte-identical. Result: any road whose endpoint must
jump to an inflated junction climbs over a class-slope embankment and returns to its A0-natural solved
profile beyond — road 50 holds ~16 m past the ramp instead of tilting 1485 m.

`BridgeRaisedJunctions` is retained only as an input to the inflation set (union with it, so
explicitly-raised junctions still qualify even if A0 lookup is missing).

### C3 — A0 section anchor (through-road safety net, gated)

The affine leveling only touches **endpoints**. A *through*-road inflated at a **mid-spline** junction
(the harmonizer writes a high Z at the interior station, the box filter smooths it into a hump) is not
an endpoint case. For those residual interior lifts, add a narrow anchor after harmonization: for a
non-structural section with `solvedZ − A0(section) > InflationThresholdMeters` **and** within
`AnchorReachMeters` of a raised structure, pull `TargetElevation` toward A0 with the same eased weight
away from the nearest genuine raised junction (0 at the junction, 1 = full A0 at reach end).

**Gate hard** so it never flattens legitimate smoothing: only fires on sections that are (a) above A0
by the threshold and (b) attributable to a raised structure. First implementation may ship C3
**disabled** and be enabled only if the Manhattan render still shows through-road humps after C2 —
road 50 is an endpoint case and is fixed by C2 alone.

### C4 — Retire the post-solve elevation writers

Current post-solve block in `TerrainCreator.cs:388-448`:

| Pass | Today | After |
|---|---|---|
| `BridgeProfileSolver.RefineSpans` (`:388`) | re-curves the deck (writes deck Z) post-solve | **Fold into the solve** (decided 2026-07-06) — call as the final in-solve deck-geometry step at the end of `SmoothAllRoads`, so the deck Z it produces is part of the solver's output (honours "solver owns deck Z"). The interior-constraint inputs it needs (`PlanFloorConstraints`/`PlanConstraints`, deck profile) move with it or are threaded in; deck mesh / DecalRoad / excavator read the finished network unchanged. No fallback — the fold is the design. |
| `GradeSeparationResolver.ApplyApproachRaiseRamps` (`:410`) | raises decks vs the dam-lifted solved road (the feedback loop) | **Delete.** With lower roads at A0, deficits vanish; the pass was correcting a dam that no longer exists. |
| `GradeSeparationResolver.ApplyLowerRoadDips` (`:421`) | dip road Z already pre-pinned (`ApplyLowerRoadDipPins`); this is verify-only + terrain carve | **Keep terrain carve only.** Audit it never re-decides road Z post-solve (doc 04 §8.3 says it is already "no double-dip"); if it does, that decision moves to the pre-solve dip pins. |
| `BridgeAbutmentOverlapStamper.Stamp` (`:431`) | bare-terrain tongues, owner-guarded | **Keep** (terrain only; never writes road/deck Z). |
| `BridgeDeckExcavator.Excavate` (`:444`) | shaves terrain above deck, owner-guarded | **Keep** (terrain only). |

The `RoadSurfaceOwnerRaster` (`TerrainCreator.cs:397`) guard that protects painted road surfaces from
the terrain passes stays and is the enforcement of "post-solve = bare terrain only".

### C5 — In-solve clearance assertion (diagnostic, not corrective)

Replace the deleted raise with a **read-only** check after the solve: for every crossing, log a
`[BRIDGE-CLEAR] WARN` if final `deckZ − lowerRoadZ < required`. It must never modify elevation. A
firing warning means the *planner's pre-solve decision* was wrong (it should have dipped / taken
reduced clearance / not raised) — fix it at the planner (in-solve), not with a post-hoc raise. This
keeps the "kill the demand" levers (doc 08 C1/C2) where they belong: pre-solve, against A0.

## 5. What genuinely climbs (so the anchor doesn't over-hold)

Exempt from C1/C2/C3 hold-down (these leave A0 legitimately):

- **Deck sections** — `StructureSpanId >= 0` / `IsExcluded` (the deck rides at its planned Z).
- **The bridge's own connected approach ramps** — the in-spline road beyond the span that carries the
  climb to the deck end (the real embankment). These are the sections `ApplyApproachRampPins` /
  `RaiseJunctionsAlongApproachRamps` legitimately raise.
- **Planned lower-road dip wells** — `ApplyLowerRoadDipPins` sections (they leave A0 downward by design).

Everything else non-structural is anchored to A0.

## 6. Validation

Feature-flag the change (seed `EnableNaturalProfileAnchor`, default off until validated) so a regen
pair is comparable, mirroring doc 08's method.

1. **Manhattan 2048** (the failing case): `[WARN] Over-max values` count → ~0; `[DAM-REPORT]` deltas
   for 44/50/162 → small (< a few m, concentrated at genuine bridgeheads); render: spikes/gouges gone,
   real bridges (Manhattan/Brooklyn arches) intact with graded approaches; no `[BRIDGE-RAMP]` raises
   (pass deleted).
2. **No regression on the working set:** winningen (dam excess should stay ≤ doc-08's +683 m or
   improve; **no new class-slope ramps on ordinary steep roads** — the inflation gate must not fire on
   them), `franco_same_prio`, `_generated_terrain`. Compare `[DAM-REPORT]` before/after.
3. **Tests:** the 707-test suite green; extend `BridgeSideRoadContainmentTests` /
   `AffineJunctionLevelerTests` for the A0-inflation gate (a junction inflated *without* being in
   `BridgeRaisedJunctions` must now decay); add a Manhattan-shaped regression (flat DEM + a raised
   span beside a long primary → primary holds ~DEM beyond the ramp).

## 7. Risks & open questions

- **A0 quality on flat maps.** The whole design trusts `EarlyElevationEstimate`. If A0 itself is noisy
  near water/edges the anchor inherits it. Confirm A0 for road 50 ≈ 16 m (the `estZ` column already
  shows it does) before relying on it map-wide.
- **Through-road interior lift (C3).** Whether C2 alone clears Manhattan or C3 is needed is an
  empirical question answered by the first regen. Ship C2 first.
- **Inflation threshold tuning.** 1.5 m reuses `AffineDecayMinErrorMeters`; verify it separates
  bridge lift from steep-terrain smoothing on winningen (expected: ordinary junctions < 1.5 m off A0).
- **`RefineSpans` fold** (decided: fold into `SmoothAllRoads`). It is mechanical but changes call
  order and must carry its interior-constraint inputs (`PlanFloorConstraints`/`PlanConstraints`, the
  bridge deck profile) across the module boundary. The remaining risk is read-order: deck mesh /
  DecalRoad / excavator must still see the finished, refined network. Verify those consumers run after
  the fold and read the same elevations.
- **No grade clamp (user feedback).** The class-slope *ramp length* at a genuine connection is an
  embankment, not a whole-road grade clamp — the user endorsed it and it shipped in doc 08 take-3. C5
  keeps the "deck must clear / never shorten a real ramp" spirit by pushing the decision pre-solve
  rather than clamping.

## 9. Phase 3 validation — Manhattan 4096 A/B regen (2026-07-06 ~18:00)

Logs: `…\levels\manhattan\log_comparision\Log_EnableNaturalProfileAnchor_{false,true}.txt`
(4096 preset, bridges refreshed — an earlier 17:30 pair had the bridge roads missing and only
`spans=3`; disregard it except as a mild-scenario note, see the threshold caveat below).
Flag set via the preset JSON `bridgeRules` node (`TerrainPresetImporter` deserializes the whole
options object; there is no UI checkbox yet).

### 9.1 The anchor contains the dam — VALIDATED

Identical plan in both runs (`spans=68 raised=20 junctionRaises=11 onDeckJunctionPins=25
crossings=169`), then:

| | anchor OFF | anchor ON |
|---|---|---|
| `[BRIDGE-RAMP] raise` (runaway post-solve raises) | **6** | **0** (pass skipped) |
| over-max heightmap pixels (PRE-SAVE SPIKE PREVENTION) | **115 215** | **5 547** (−95 %) |
| `[BRIDGE-PLAN] affine-decay` firings | 92 | **138** |

User render verdict: "the bridges look way cleaner." The residual 5 547 over-max are the known
DEM-data clamp (negative source values), not bridge output. The C5 assertion emitted **14
`[BRIDGE-CLEAR]` warnings** — honest, real deficits (see §9.3), not dam artifacts.

**Threshold caveat (from the mild 17:30 spans=3 pair):** the 1.5 m inflation gate also decays
moderate 2–6 m junction errors that the full-length affine handled acceptably — DAM-REPORT splines
> 0.5 m rose 199 → 240 there, individual roads ±1–1.5 m worse (e.g. spline 73 meanAbs 0.93→1.32),
some better (195: 1.25→0.64). If ordinary-junction regressions show up on steep maps (winningen),
raise the threshold (~3–5 m) or make it a `BridgeRuleSystemOptions` knob. Not blocking.

### 9.2 REVEALED problem A — bridge-end terrain support buries neighbour bridges (root-caused)

User render: at interchanges where bridges connect / run over-under each other, the terrain support
at bridge ends overlaps the roads/decks of OTHER bridges → not drivable; spiky terrain around bridge
ends remains in BOTH flag states (pre-existing, unmasked by the anchor).

Mechanism (confirmed in code):
- `RoadSurfaceOwnerRaster.Build` (`RoadSurfaceOwnerRaster.cs:50`) rasterizes ONLY non-excluded
  sections — **bridge decks (`IsExcluded`) are invisible to the owner guard** (its doc comment even
  says so; the guard was built for *painted road surfaces*, doc 07).
- `BridgeAbutmentOverlapStamper` stamps corridor-width tongues and relies on that guard
  (`BridgeAbutmentOverlapStamper.cs:146` "corridor width is safe again"; guard consulted at `:165`)
  — so one bridge's end-tongue may raise terrain straight across a NEIGHBOUR bridge's deck
  footprint. Same for the approach-raise embankment fill (flag-off path).
- The spike texture: the tongue raises only cells within `AbutmentOverlapMaxLiftMeters` (2 m) of
  the deck and `continue`s otherwise (`:173`), plus skips foreign-owned cells (`:165`) → a jagged
  raised/not-raised patchwork at dense uneven bridge ends; the excavator adds cut walls beside it.

**Fix (designed, next commit): a deck-footprint RAISE guard.** New raster of deck footprints
(`IsExcluded` sections at `EffectiveRoadWidth/2 + margin`, decks stamped lowest-deck-Z-first,
claim-bare-only) + `CanRaise` check consulted ONLY by the RAISING passes (abutment tongue,
approach-raise fill): never raise terrain over a foreign deck. Deliberately NOT wired into the
LOWERING passes (`ApplyLowerRoadDips` carve, `BridgeDeckExcavator`) — cutting below a foreign deck
is harmless, and blocking the underpass dip carve / excavation under a deck would shear the doc-28
wells (the §7b/§7c lesson class). Painted-surface ownership (`roadSurfaceOwner`) is untouched, so
the lower road under a deck still owns its lane cells and its dip carve still works.

### 9.3 REVEALED problem B — real bridge-over-bridge clearance deficits (planner work, open)

The 14 `[BRIDGE-CLEAR]` warnings are genuine conflicts the old runaway raise used to paper over
(by building the dam): e.g. `upper=60 lower=72 clearance=-0,60m` (deck BELOW the crossing road),
`upper=134` short against 6 roads (0.8–3 m deficits). Resolving these is pre-solve planner work
(doc 08 C1 escalation: dip the lower / reduced clearance / split), and note
`EnableBridgeBridge` is still "detection marker only — no resolution", so bridge-over-bridge
crossings have no rule yet. Separate follow-up; do not re-introduce a post-solve raise for them.

## 10. Related

- Doc 08 (§7/§7b/§7c) — the dam problem, the three C3 takes, and the affine-decay this doc generalizes.
- Doc 05 — M1 junction raises (`RaiseJunctionsAlongApproachRamps`) and the room-widening the on-deck
  pins build on.
- Doc 03/04 — sparse floor constraints; why soft rises are uniform; "end deficits are approach
  territory" (the planner-side clearance levers C5 defers to).
- Doc 28 (`…2026-06-03_bridge_generation/28-…`) — coherent underpass / dip-if-lower-priority, the
  pre-solve dip machinery C4 keeps.
- Code hooks: `UnifiedRoadSmoother.cs` (`:270` A0 build, `:1321` raised-junction set, `:2909`
  affine leveling + decay), `AffineJunctionLeveler.cs`, `GradeSeparationResolver.cs:572`
  (`ApplyApproachRaiseRamps` — to delete), `TerrainCreator.cs:388-448` (post-solve block).
