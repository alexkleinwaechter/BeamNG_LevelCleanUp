# 04 — Clearance Catch-up (resume here)

**Date:** written 2026-06-10 late night, after render #8 (log `…215954`). Session resumes 2026-06-11.
**Branch:** `feature/bridge_merged_corridor` @ `3c0acfb` (amendment 03 v3), 616 tests green.
**User verdict on render #8:** *"It's better now but the bridge has not enough clearance. We will work
tomorrow on a solution."*

---

## 1. Where we stand — what render #8 PROVED

Amendment 03 v3 ("give the bridge cross-sections", doc 03) **fixed the continuity problem for good**:

```
[BRIDGE-PROFILE] apply summary bridges=17 overridden=16 cubic=16 parabola=0 chord=0 maxSeamKink=6,2deg
spline=394  curve=Cubic L=285,0m z0=7,72 z1=4,87 bulge=1,53m seamKink=0,0/0,0deg
spline=395  curve=Cubic L=78,5m  bulge=3,11m seamKink=0,0/0,1deg  planClear=11,4/8,0m  ✅ clears
spline=396  curve=Cubic L=20,0m  seamKink=0,0/0,0deg
```

- Every span is a cubic again, **flush at BOTH abutments, with real vertical curvature** (394 bulges
  1.53 m, 395 crests 3.11 m). No crumple, no abutment walls, no z-fighting seams.
- The render-arc law that got us here (doc 03 v1→v3): **anything hard-held can step; anything the
  filter solves cannot.** Hard pins are gone from bridges entirely
  (`pinnedSections=0 softPinnedSections=779 mode=sparse-soft`).

## 2. The open problem — NOT ENOUGH CLEARANCE

```
spline=394: planClear=3,4/6,7m  (5 planned)  [LOW CLEARANCE (typed) 3,4m < 6,7m]   minClear=-6,3m
spline=396: planClear=5,0/5,7m  (1 planned)  [LOW CLEARANCE (typed) 5,0m < 5,7m]
[GRADE-SEP] sparse floors constraints=3 skippedNearAbutment=8
[GRADE-SEP] resolve crossings=4 dippedRoads=3 maxDip=0,95m
```

394 is short **3.3 m** at its binding crossing. The under-clearance is concentrated at the
**near-abutment crossings** (the kattenes pattern: t = 0.07–0.10 and 0.86–0.94 on spans 199/394/395):
394's road crossings at t=0.08/0.10/0.89, its water at t=0.86 (minZ 4.23), 199's + 395's rails at
t=0.07/0.86–0.94. (395 clears only because its approaches happen to arrive high.)

## 3. Root-cause analysis (verified against the code, not just the log)

The v3 system has **three clearance mechanisms, and each one has a hole exactly at the span ends**:

1. **Soft humps** (`SoftDeckRiseMeters` → `ApplySoftShapingToRaw`): the rise is written into the RAW
   filter input, but the box filter (window 301 samples ≈ 150 m) **dilutes a 30–150 m hump to a
   fraction of its height** (hump mass / window). The hoped-for iteration ratchet does NOT compound:
   the chord **re-anchors on the approach raw every iteration**, so the system reaches a fixed point
   far below the rise (evidence: 394's end approach z1 settled at 4.87 — in render #7 the hard pin had
   it at 8.92+). Soft shaping delivers continuity, only ~20–30 % of the clearance.
2. **Interior floors** (`PlanFloorConstraints` → `ComputeInteriorLift` arch in `ApplyToSpan`): exact
   and overshoot-friendly — but **floors with arch-shape < 0.25 (t outside ~[0.15, 0.85]) are
   SKIPPED** ("end deficits are approach territory"), and on this map that is 8 of 11 floors. The one
   global symmetric arch `16t²(1−t)²` cannot express an end deficit without a giant central hump.
3. **Resolver dips** (`ApplyLowerRoadDips`): work (3 roads dipped) but (a) rail/water can never be
   dipped, (b) the dip targets `minClearance + meshThickness` (user 5 m), not the typed budget, and
   (c) `planClear` in `[BRIDGE-PROFILE]` is logged BEFORE the dips run, so the printed shortfall
   overstates the final road-crossing gap (rail/water gaps are real).

**Net: end-of-span clearance currently has NO working mechanism.** The deck end can only be high enough
if the APPROACH arrives high — i.e. real ramps — and nothing lifts the approaches any more (v2's
hard ramp pins crumpled #5; v2's soft feather was truncated by junction pins and removed in v3).

## 4. Candidate solutions for tomorrow (decide together, then implement)

**A. RECOMMENDED — post-solve approach-raise ramps (the upward mirror of `DipLowerRoad`).**
After `RefineSpans` the deck cubic and the solved approaches are FINAL and exact. Where the plan's
end-crossing budget is still short (planClear < required at a crossing with t outside the floor band):
raise the bridge's own approach + the span end with an eased ramp
(`(1−u)²(1+2u)`, length |delta|/§3.3-class-slope, junction-clamped via `MeasureRampLength`), applied to
`TargetElevation` + banked edges + a heightmap FILL (mirror of the dip carve, fill instead of cut),
then re-run the span cubic (or extend the ramp through the span end region). Why this does NOT
re-introduce the #5 crumple: the base is the **actual solved profile** (zero estimate error — the
crumple was estimate-shaped HARD pins fighting the smoother pre-solve), it runs after all smoothing
(nothing fights it), and it is exactly the machinery the dips already use successfully on the lower
road. This is also the natural partner of Phase B-1 embankment stamping (the fill under the raised
approach IS the embankment).
- Watch-outs: DecalRoad generation must read the post-ramp elevations (it already runs after the dips
  — same slot); junction inside ramp length ⇒ clamp (junction-in-sag analogue); deck mesh/excavator
  read the snapshot — re-snapshot the span if its end sections move (or apply the ramp BEFORE
  `CaptureSpanSnapshot` by folding it into RefineSpans).

**B. Per-floor LOCAL bumps in `ApplyToSpan`** (replace the one global arch with a max-envelope of
eased per-floor bumps, allowing floors at any t incl. ends). Fixes mid/“shoulder” floors cleanly, but
an end floor lifts the deck end ⇒ re-creates the abutment step unless combined with A. Worth doing
for the t∈[0.10,0.15] band regardless; NOT sufficient alone.

**C. Ratchet the soft shaping** (anchor on `max(previous solved, chord+rise)` so iterations compound).
Cheap, but fights the fixed-point math, risks overshoot/instability, and the 3-iteration budget +
junction blending make convergence unreliable. Fallback only.

**D. Honesty fixes regardless of A–C:**
- Log `planClear` AFTER `ApplyLowerRoadDips` (or log both) so the printed gap is the real one.
- Teach the resolver dip target the typed `RequiredSeparationMeters` (it dips to 5 m + thickness today).
- 394 `minClear=-6,3m` (deck-vs-DEM) is the informative terrain metric — under-deck terrain shaving is
  `BridgeDeckExcavator`'s job; check the excavator actually opened daylight under 394's low sections.

**Likely combination: A + B + D.** A delivers end clearance via real ramps, B makes floors complete in
the middle band, D makes the logs trustworthy for review #9.

## 5. How to verify tomorrow (render #9 checklist)

1. `[BRIDGE-PLAN] … mode=sparse-soft` still, `pinnedSections=0`.
2. `[BRIDGE-PROFILE]` 394: planClear (post-dip) ≥ required at ALL 5 crossings; seamKink stays ≈0;
   curve stays Cubic (ramps must not break the flush seams — that is the regression to watch).
3. `floor SKIPPED near abutment` count drops to 0 (B) or each skip is matched by a `[BRIDGE-RAMP]`-
   style raise log (A).
4. In-game: 394 deck visibly above the under-roads/water; approaches climb smoothly; no new steps.
5. 595/355/396 unchanged-or-better; `[GRADE-SEP] dippedRoads` still active.

## 6. Traps / context for the next session

- Render logs: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\_generated_terrain\MT_TerrainGeneration\logs\`.
  `[BRIDGE-REPROJECT]`/`[BRIDGE-OBSTACLES]` fire in the ANALYSIS phase, before the perf-log session.
- The kattenes preset (`D:\temp\TestMappingTools\__preset_kattenes\theTerrain_terrainPreset.json`)
  carries `EnableSparseDeckConstraints: true` (hand-inserted; backups `.bak`/`.bak2`). **Preset values
  override code defaults on import** — re-repair if options change shape.
- PS 5.1: don't use Get-Content/Set-Content for renames (mojibake'd 2 files tonight — repaired via
  cp1252→UTF8 roundtrip; use the Edit tool). Double quotes inside `git commit -m` here-strings break
  arg passing.
- The app is usually open in the VS debugger — DLL-lock build errors (MSB3027) are not code errors;
  the user closes + rebuilds before regen.
- Flag-off must stay byte-identical; commit per step; build + 616 tests green before each commit.
- After clearance is solved → **Phase B** (cherry-pick `BridgeAbutmentFiller` from `feature/bridges`
  @ `822b045`, constant 1:1.5 slopes; B2 cut side-walls; B3 shrink-only abutments; B4 collar blend) —
  B-1 pairs naturally with solution A's heightmap fill.

## 7. The render arc in one paragraph (for cold start)

Dense planner-authored hard pins crumpled the roads (#5) → no pins + floors sank the decks because 8/11
floors sat near abutments and were skipped (#6) → hard-held chord pins stepped one abutment by 1.3 m and
had zero curvature while every unpinned cubic span was seamKink≈0 (#7) → v3 made the bridge ordinary
road in the filter (soft relative humps, boundary-anchored chord, nothing hard-held): #8 is flush both
ends with real curvature, but the filter dilutes the clearance humps and nothing lifts the approaches —
**clearance at near-abutment crossings is the one remaining gap.** The decision engine (typing, budgets,
§3.5, feasibility) is sound and untouched throughout; only the geometry-delivery mechanism evolved.

---

## 8. The OTHER open problem — HARSH DIPS step the lower road (2026-06-11 session)

Sections 1–7 are about the deck being too LOW (lift things up). This is the mirror complaint on the
**lower road**: where the system *does* dip a road under a bridge, the dip is harsh. **User symptom
(observed): a STEP/KINK at the dip's EDGES** — not a steep V in the middle — and it appears "on roads
as well, not all the time, I guess when the clearance height is just okay." Plus one anomaly to chase:
**a motorway (highest priority) under a high bridge looked *affected* (raised/dipped) — which is a veto
violation, never allowed.**

### 8.1 Root cause (verified in code, same render-arc law as the deck)

The edge-step is the **late post-solve carve**. The pre-smooth dip-pin machinery already exists
(`UnifiedRoadSmoother.ApplyLowerRoadDipPins`, emits a full eased well into `PinnedElevation` BEFORE
smoothing so the filter grows continuous ramps to it) — **but it is silently switched off in exactly
the mode we run.** The gate:

```csharp
// GradeSeparationResolver.cs:271
var dipAsPin = dipRules?.EnableDipAsPin == true && dipRules.EnableSparseDeckConstraints != true;
```

kattenes has `EnableSparseDeckConstraints = true` ⇒ `dipAsPin = false` ⇒ dips fall to
`ApplyLowerRoadDips` (called from `TerrainCreator` AFTER all smoothing). A well subtracted from the
already-solved profile is the lower-road twin of the deck law: **anything late-stamped / hard-held can
step; anything the filter solves cannot.** The smoother never knew about the dip, so it cannot grow a
continuous ramp into it → the edge steps.

Note the existing dip-pin is **not** a clean escape either: it writes a **hard** `PinnedElevation` well
(`UnifiedRoadSmoother.cs:1450`) off the **estimate** chain (`baseZ = TargetElevation → A0 early →
raw DEM`), so even enabled it can step at the well's *outer* edge — the same #5/#7 estimate-error
failure we deleted from the deck. Hard is not the answer; **filter-solved is.**

### 8.2 The principle (user, this session)

> *"The dip pins should, like junctions, be placed in front of the road smoothing process."*
> *"We should see the dip as ongoing junctions which we place."*
> *"It's really hard to set the parameters right with hidden knowledge."*

So the fix has two halves:
1. **A dip is a constraint we PLACE pre-smooth and the filter SOLVES — an "ongoing junction."** To obey
   the render-arc law it must be **soft (filter-solved), not hard-held.**
2. **Remove hidden knowledge, don't add a knob.** The silent `Sparse ⇒ no dip-as-pin` interaction is
   the single worst trap (no log, no UI hint). The fix makes continuous dips the *default* behaviour
   and **logs which dip path ran** — it must not introduce another flag combo to memorise.

### 8.3 Chosen fix — A (with B as a same-session sanity check first)

**B — proof-first (one-line-ish, run BEFORE building A).** Drop the `&& EnableSparseDeckConstraints
!= true` clause so the *existing* dip-pin emits in sparse mode. This proves "pre-smooth placement kills
the edge kink" by getting the smoother to grow ramps to the well. Expected: the worst mid-edge kink
goes; a residual *outer*-edge step may remain (hard pin on estimate base). **Diagnostic only** — it
violates the render-arc law, so it is not the keeper.

**A — RECOMMENDED keeper: dip as a pre-smooth *soft* constraint (the downward mirror of v3's
soft-humps-plus-floors).** Emit the dip well through the **`sparse-soft` pin machinery the deck already
uses and we trust** (not hard `PinnedElevation`), enabled in sparse mode, plus a **one-sided
"road-ceiling" enforcement** (road ≤ obstacle clearZ at the crossing) so the box filter cannot dilute
the well away the way it dilutes the deck humps (§3.1, 20–30 %). Result: the filter solves a continuous
well → no edge kink; nothing is hard-held → no estimate step; junction-like by construction; depth held
by the one-sided floor-equivalent. It is the exact symmetric partner of what made #8's deck flush both
ends. Plumbing to confirm in the plan: that the lower road's smoother (`OptimizedElevationSmoother`)
honours a soft / one-sided constraint on a general (non-bridge) spline — if only hard `PinnedElevation`
is supported there, A's first task is to add the soft path (mirror the deck's).

**Hidden-knowledge fixes that ship with A regardless:**
- Delete (or invert) the silent gate; **log the active dip path** per crossing
  (`[GRADE-SEP] dip path=pre-smooth-soft|hard-pin|late-carve`).
- Teach the resolver dip target the typed `RequiredSeparationMeters` (today it dips to user 5 m +
  mesh thickness — doc §3 item 3b, §4-D).

### 8.4 Separate correctness item — the motorway veto (do NOT fold into A)

A high-priority road under a bridge must be **vetoed** (Action = `RaiseBridge…`, road untouched). The
veto *is* re-checked in both carve branches (`GradeSeparationResolver.cs:303-309`, 345-…), so a *full*
dip should not reach it — **but** if the planner under-raised the deck, the A7 **residual** carve
(`GradeSeparationResolver.cs:332`) can still nick a vetoed road locally. Action: pull the **specific
crossing's log** for the motorway case, confirm whether it was a residual carve or a mis-priority, and
ensure a vetoed road is never carved (even residually) — the deficit belongs entirely on the deck.

### 8.5 Render #9 checklist — dip additions (on top of §5)

1. `[GRADE-SEP] dip path=pre-smooth-soft` for every dipped crossing (B test: `=hard-pin`); **zero
   `late-carve`** in sparse mode.
2. Dip EDGES: seam continuity where the well rejoins the road — no step at either end, on tight
   *and* open-road dips. (The regression to watch is an outer-edge step from estimate base — A must not
   show it; B may.)
3. The motorway crossing: road profile **untouched** (no residual carve); deck carries the full deficit.
4. In-game: dipped roads ease in/out smoothly under the deck; no plough furrow at the trough ends.
