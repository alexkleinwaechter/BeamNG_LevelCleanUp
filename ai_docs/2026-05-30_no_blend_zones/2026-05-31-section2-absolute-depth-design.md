# §2 — Through-road absolute depth (the no-blend "trench / berm / stub-gouge") — design doc

- **Date opened:** 2026-05-31
- **Branch:** `experimental/switch_off_blend_zones`
- **Status:** STILL OPEN. Approach B was implemented + render-tested on 2026-05-31, **did not improve the
  result, and was reverted** (§7c). Design history below; the still-valid findings (merging amplifies §2; a
  smoothing-stage detrend is a proven no-op) are in §7a/§7b. **Decide a new approach jointly; do not pre-pick.**
- **Predecessors / source of truth:**
  - `2026-05-30-no-blend-zones-followup.md` §2 (the parked "absolute depth" issue + the two discarded
    target-source attempts — read its §2 verdict block first).
  - This doc is opened because §2 now has a **second, independent reproduction** on a different map
    (`_generated_terrain`), which sharpens the symptom set: it is not just a trench under a long road, it
    also produces a **stub gouge** and a **berm** at a junction in a ravine.

---

## 0. Why this doc exists (what changed)

§2 was parked on `franco_same_prio` as "a fundamental no-blend conflict, a future design call." A new map
(`_generated_terrain`, log `Log_TerrainGen_4096_20260531_012220`) reproduces the same root cause at a
**single T-junction in a local ravine** and makes three user-visible symptoms that are all the same defect:

1. **The main-road bump** (way [478416593](https://www.openstreetmap.org/way/478416593), spline 386): a long
   asphalt road carries a near-flat profile across a sharp terrain dip and floats up to **+4.8 m above** the
   terrain there → renders as an unnatural high embankment berm.
2. **The short track sinks** (way [288481169](https://www.openstreetmap.org/way/288481169), spline 74; OSM
   node [264064974](https://www.openstreetmap.org/node/264064974) = J#148): an 8.6 m dirt stub is pinned (§3)
   to the floating road Z at the junction and tilts to it, so its free end buries **−3.6 m** into a knoll.
3. **"The stub doesn't really connect"**: the seam is geometrically found and flush in Z (§3 did its job),
   but the surrounding gouge/berm makes it *look* like a disconnected endpoint. **Not a junction-radius
   problem** — verified: `JunctionDetectionRadiusMeters=5`, and the only junctions within ~20 m of the stub
   free end are its own dead-end (J#147) and the T (J#148); there is no second road node in range to merge.

All three dissolve if the road stops floating over the ravine — i.e. if §2 is solved. The user chose
"do §2 first."

---

## 1. Confirmed mechanism (from logs, two maps)

### 1a. The data — `_generated_terrain`, J#148 (node 264064974)

```
J#147  Endpoint   node=2807280246  pos=(95.8,1096.3)  terrainZ@center=77.12   (stub free end — a knoll)
J#148  TJunction  node=264064974   pos=(88.7,1101.0)  terrainZ@center=72.16   (road×stub — a dip)
```
Terrain drops 77.12 → 72.16 over 8.6 m (~58% local slope). The junction sits in a hole between two knolls.

Post-§3 contributor state at J#148:

| contributor | role | roadZ | terrainZ@pt | delta | meaning |
|---|---|---|---|---|---|
| spline 386 (way 478416593) | THROUGH | 76.95 | 72.15 | **+4.80** | road floats above the dip → berm |
| spline 74 end (way 288481169) | ENDPOINT(end) | 76.95 | 72.18 | +4.77 | stub junction end pinned to the float |
| spline 74 start | ENDPOINT(start) | 73.52 | 77.12 | **−3.61** | stub free end (untouched by §3) buried in the knoll |

`[NO-BLEND OWN] through=386 term=74 dZ=+0.00 … maskWinner=through(ok)` — they MEET correctly; the problem is
purely **absolute depth**, not junction meeting.

`[NO-BLEND PROFILE] spline=386 len=3457 n=6916 dPoint max=+4.91  dRaw max=+2.97  dCorr max=+2.08`
— at the ravine the float decomposes ≈ +3.0 m honest wide-low-pass (`dRaw`) + ≈ +1.8 m affine drag (`dCorr`),
**fill sign** (franco was cut sign). The road's smooth profile legitimately rides over a narrow sharp dip
(the part the user wants KEPT) PLUS the affine drag overshoot (the part to drive toward 0).

### 1b. Why the stub buries (affine math)

`AffineJunctionLeveler.Apply` leaves a **free end untouched** (correction decays to 0 there). So the stub's
free-end Z of 73.52 is its *natural* smoothed elevation — already ~3.6 m below the knoll terrain before §3
runs (the whole 8.6 m stub's natural profile sits under the convex knoll). §3 then pulls **only** the junction
end up to the floating through Z (76.95). Net: an 8.6 m stub forced to ramp 73.52 → 76.95 (~56% grade) while
the terrain underneath runs the opposite way. Start buries, end berms. **Both ends are the same §2 float,
opposite signs.**

### 1c. The general driver (from franco, followup §2 VERDICT)

The dominant sink is **not** inter-pass target chasing (that was the discarded "decide-once" lever). It is the
**first honest-target application**: each road must drop its endpoint to meet a neighbor through-road that is
*itself* below terrain, and affine spreads that drop over the whole body (its job, no local ramp). The
neighbor is deep for the same reason recursively → a **coupled downward cascade with no upward restoring
force**. Junctions are flush (`|dZ|≈0` network-wide); the whole road network sits in a shared cut/fill below/
above the convex terrain.

**Two things must be preserved by any fix:**
- The honest wide-low-pass-over-convex component (`dRaw`) — the user explicitly wants roads to ride smoothly
  over sharp terrain, not to follow every bump.
- **No grade clamps** (`feedback_no_grade_clamp`) and **no cubic/Hermite ramps** (`feedback_hermite_blend_suspect`,
  `feedback_b3_cubic_rejected`). Affine = linear tilt only; it preserves curvature and never kinks a road.

---

## 2. The architectural conflict (why this is hard, not a bug)

No-blend simultaneously demands all three of:

1. **Roads meet at junctions** (shared junction Z).
2. **Endpoint error is spread over the whole body, no local ramp** (affine, the no-blend promise).
3. **Junction meeting-heights are set by the wide low-pass over convex terrain** (smooth, doesn't chase bumps).

Together these force a **collective sink/float**: junction heights are picked smooth-but-off-terrain, every
road affines its whole body to hit those heights, and there is no force pulling the heights back toward
terrain. You cannot have all three without the network drifting off the terrain surface in
cut/convex regions. **Something in {1,2,3} must bend.** That is the design call.

---

## 3. Candidate approaches (trade-offs — DO NOT pre-pick)

### A. Pull junction meeting-heights back toward terrain (bend #3)
Bias each junction's harmonized Z toward `terrainZ@center` by some fraction, or cap how far a junction Z may
sit from local terrain, then let affine spread the (now smaller) endpoint errors as usual.
- **Pro:** directly attacks the driver — shallower junction heights → shallower whole-network drift. Keeps
  affine (no kinks).
- **Con:** a *cap* is a clamp by another name (the user rejects grade clamps; a Z-cap is at least adjacent —
  must be framed as "how close to terrain should a junction sit," not "max deviation"). Risk of re-introducing
  the bump if the junction is pulled to terrain but the through road body is not. Interacts with the honest
  `dRaw` we want to keep — must bias only the `dCorr` part, which is not separable per-junction at decision
  time.

### B. Don't affine through-carrying roads at all; only tilt genuine terminating stubs (bend #2, partially)
A through road keeps its honest low-pass profile (rides the terrain smoothly), and is **never** tilted to meet
a neighbor. Only true terminating stubs tilt — and they tilt to the *through road's honest* Z, which is now on
terrain. (The `IsEndpoint` filter already skips through roads at *mid* junctions; this would also skip them at
their *own* endpoints, breaking the cascade at the root.)
- **Pro:** removes the cascade's transmission path — through roads stay flush with terrain, so terminating
  stubs targeting them also land near terrain. Affine still used for stubs (no kinks). Matches followup §2
  candidate-fix-1.
- **Con:** at a junction of two through roads of different honest Z, *someone* must move or there's a step.
  Need a rule for through×through meets (highest-priority wins? both keep own Z and accept a small step?).
  Could reintroduce steps where the cascade previously hid them by sinking everyone together. The stub case
  (this map) is clean under B (stub follows an on-terrain through road), but franco's through×through chains
  need the meet rule specified and tested.

### C. Accept the cut; only fix the *stub* locally (bend nothing — scope reduction)
Declare the through-road trench/berm an accepted no-blend characteristic (it's partly honest `dRaw` anyway),
and only stop a **short** stub from being dragged onto a far-floating junction: a short terminating road whose
junction-Z − terrainZ@junction is large follows terrain instead, accepting a small seam step.
- **Pro:** smallest change, no cascade surgery, fixes the most jarring on-screen artifact (the stub gouge).
- **Con:** leaves the berm/trench (the user called the bump out explicitly — so C alone won't satisfy them).
  Trades gouge for a seam step (the "doesn't connect" complaint could persist in a different form). This is the
  "Relax pin for short stubs" option the user already declined in favor of "do §2 first."

### Recommendation to discuss
**B** is the most principled (it removes the cascade's transmission rather than masking the result) and stays
within the no-clamp / no-ramp constraints, at the cost of needing a through×through meet rule. **A** is the
smallest lever that still attacks the global float but flirts with the clamp the user rejects. **C** is ruled
out as a *standalone* §2 fix because it leaves the berm. Likely the real answer is **B with a small, explicit
through×through meet rule**, validated on both maps.

---

## 4. Validation handles

- **`_generated_terrain`** (this map): J#148 / node 264064974 (stub gouge + berm); through spline 386 /
  way 478416593 (the long bump). Success = at J#148 the through `delta` drops from +4.8 m toward `dRaw`-only
  (~+3.0 m or less), AND the stub free-end `delta` rises from −3.6 m toward ~0, with `dZ` at the seam staying
  ≈0. Grep `[NO-BLEND DIAG] J#148`, `spline=386`, `spline=74`.
- **`franco_same_prio`** (predecessor map): through spline 55 / 42 / 105; node 282534707 (J#126). Success =
  `dCorr` mean driven toward 0 on through-carrying roads, `dPoint` pulled back toward honest `dRaw`, with all
  `[NO-BLEND OWN]` `|dZ|` staying ≈0.
- Both must hold — a fix that helps one map must not re-open the other.

## 5. Constraints (hard — from user feedback memory)

- No grade clamps (`feedback_no_grade_clamp`).
- No cubic/Hermite blend ramps (`feedback_hermite_blend_suspect`, `feedback_b3_cubic_rejected`).
- Affine = linear tilt; it preserves curvature exactly, so removing/keeping it never kinks a road — it only
  changes a road's absolute height. (Proven in §3; this is what makes B safe to consider.)
- Keep the honest wide-low-pass-over-convex cut/fill (`dRaw`); only attack the affine drag / global float
  (`dCorr`).
- The user cannot run the app — validation is by user-run render + log/screenshot.

---

## 6. Open questions for the decision session

1. For approach **B**, what is the through×through meet rule (priority-wins keeping the higher road's Z?
   length? accept a bounded step?)? This is the crux.
2. Is biasing a junction Z toward terrain (approach **A**) acceptable, or does it read as the clamp the user
   rejects? Where is the line between "junction sits near terrain" and "max-deviation clamp"?
3. Should the honest `dRaw` ride-over be *bounded* on very narrow/sharp dips (the ravine here is ~8 m wide), or
   is a +3 m honest fill over a one-pixel-narrow ravine actually desired? (i.e. is part of `dRaw` itself too
   aggressive on sharp features, separate from `dCorr`?)

---

## 7. Attempt log (2026-05-31) — merging finding, the detrend dead-end, and approach B (tried & reverted)

> **Outcome: approach B did NOT improve the result visually and was REVERTED** (code + tests removed; not
> committed). The two analysis results below (7a merging, 7b the detrend no-op) are kept because they are
> independent of B and still constrain the solution space. 7c records what B was and why it didn't land, so we
> don't re-tread it.

### 7a. The merging tradeoff (what reopened this)
User observation: §2 is **almost gone if spline merging is disabled in the UI** (`DisableSplineMerging`,
`TerrainGenerationState.cs`), but merging gives smoother roads. Tradeoff: merge → longer continuous
splines → smoother, but bigger terrain float; don't merge → each OSM way hugs terrain, but worse continuity.

Why merging amplifies the float (traced in code):
- The elevation smoother chains connected splines and low-passes the whole chain (`OptimizedElevationSmoother`,
  ~150 m box/Butterworth window). Merging (`OsmGeometryProcessor.cs` Step 3-4 connect) makes longer continuous
  splines through *complex* junctions that the chain builder's strict <30° rule would stop at, and
  `AffineJunctionLeveler` tilts **per spline** — a merged road tilts its whole long body as one unit; unmerged
  ways each re-level to local targets, so the float can't accumulate across the whole road.
- Net: merging lengthens the span over which both `dRaw` (low-pass) and `dCorr` (affine cascade) act.

### 7b. Why a smoothing-stage fix ("detrend / D") is a no-op — proof
We considered detrended smoothing (`road = baseline + lowpass(terrain − baseline)`). It **cannot** move the
float, by two arguments:
1. **Algebra:** filters are linear and the baseline is already smooth, so the expression collapses to
   `lowpass(terrain)` — the same profile. The gap `smoothed − terrain` is *by construction* the high-frequency
   content the low-pass removed; low-passing that again with a bigger window ≈ 0.
2. **Decisive:** the problem float is `dCorr = final − lowpass`, **injected after smoothing by the affine**.
   The smoothed (low-pass) profile already tracks terrain's low frequencies; its only deviation is the
   *desired* narrow ride-over `dRaw`. The data confirms it: `spline=386 dRaw=+2.97 (keep) dCorr=+2.08 (kill)`.
   Nothing in `OptimizedElevationSmoother` can remove something it never put there.
⇒ The lever must be at the **affine** stage. That is approach **B** (or A).

### 7c. Approach B — what was tried, and that it didn't help (REVERTED)
Implemented behind a flag `EnableThroughRoadNoAffine` (default off): in
`RetargetTerminatingRoadsToSettledThrough`, a "protected" spline (the priority-winning continuous/through road
at ≥1 junction) was **never** affine-tilted — kept its honest low-pass profile — and the junction target Z used
a priority-wins policy (highest-priority through road's Z, not the average; meet rule = priority-wins). Only
genuine stubs tilted. No clamp, no ramp; flag-off was byte-identical legacy; 7 unit tests passed (335 green).

**Result: user render showed no visual improvement → reverted.** Lesson for the next attempt: cutting
through-road affine alone is not sufficient — either the residual float is dominated by something else at the
suspect junctions (re-measure `dRaw` vs `dCorr` on the actual render before the next try), or the protection
set / priority-wins meet rule didn't cover the splines that actually float there. **Do not re-implement B as-is
without first re-confirming on a render which component (`dRaw` honest low-pass vs `dCorr` affine) actually
dominates the remaining float** — the §1 numbers were from an earlier build and may have shifted.

Practical interim: `DisableSplineMerging` in the UI noticeably reduces §2 (at the cost of road continuity).
That remains the only lever that empirically helps, pending a better-targeted fix.
