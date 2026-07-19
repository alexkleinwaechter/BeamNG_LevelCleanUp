# 05 — Pre-smooth junction room widening (raise for ramps, lower for dips)

**Date:** 2026-06-12. **Branch:** `feature/bridge_merged_corridor` (after `709e481` uniform span raise +
`42e4d87` typed-clearance UI). **Status:** ratified with user 2026-06-12, implementing.

## 1. Problem

Render #11 confirmed the post-solve uniform span raise (doc 04 §4.A as amended: "bridges must be equally
raised with equally distributed cross-sections"). Two leftovers, both caused by the same thing — the
**junction clamp** on late ramps/wells:

1. **Room-limited steep ramps**: 394's start junction sits ~31 m from the abutment → 29.3 m of room for a
   3.34 m raise = 11.4 % `[STEEP]` (primary absolute is 6 %).
2. **Boxed spans**: 199's bridgehead junction sits within the 2 m margin → no raise at all, rail deficit
   9.60 m unresolved.
3. **The §8 mirror (dips)**: `ApplyLowerRoadDipPins` clamps the well half-length to the junction room the
   same way — a junction inside the desired well squeezes the dip (steeper edges) or boxes it entirely
   ("junction-boxed — no dip pins emitted"), pushing the whole deficit onto the deck.

## 2. Why the clamp exists, and what that implies

A junction is an **agreement point**: all contributor roads were solved to one Z. A late unilateral edit
of one road through it = a guaranteed step on the other roads' seams. So the fix is never "ignore the
junction" — it is **change the agreement before smoothing**, so every contributor is solved to the new
height. The smoother is the only component that can deliver that consistently (render-arc law: anything
the filter solves cannot step).

## 3. The lever (already exists)

`NetworkJunction.HarmonizedElevation` + `IsPinned`. Every write site in `UnifiedJunctionProfileBlender` /
`NetworkJunctionHarmonizer` guards with `if (!junction.IsPinned)` — a pinned junction is never recomputed,
and the blender adapts ALL contributor roads to its Z. Phase 1.9 (`JunctionElevationPinner`) already pins
Endpoint/TJunction to terrain. We re-pin selected junctions to the **ramp line** (bridge approaches, up)
or the **well line** (dip ramps, down) in Phase 1.85, after the planner. Estimate error is benign here:
a junction ±0.3 m off ideal is still *consistent* for every road — no step — and the post-solve passes
(`ApplyApproachRaiseRamps`, A7 verify/carve) measure REAL residuals and top up exactly.

## 4. Design

All sparse-mode-gated (`EnableSparseDeckConstraints`); flag-off byte-identical. Roundabout junctions are
never re-pinned (flat-ring machinery owns them).

### 4.1 Uniform soft span lift (planner)

Per the uniform law, sparse pins change from per-crossing humps (`BuildSoftHumpPins`) to ONE uniform lift:
`lift = max over raise/veto/split crossings of (target − chordAt(station))`; every span pin =
`chord + lift`, `SoftRiseMeters = lift`. Bonus: a span-wide lift is wider than the 150 m box-filter
window, so it survives dilution far better than the 30–150 m humps (doc 04 §3.1) — the approaches get
pulled up from both sides.

### 4.2 Junction RAISE along the approach ramp line (new, Phase 1.85)

For each raised span end: `delta = deckEndZ − natural(abutment)` (estimate chain: TargetElevation → A0 →
DEM), ramp length `|delta|/§3.3-class-slope` (clamp 10..150, NOT junction-clamped — that's the point).
Every non-roundabout junction with a contributor on the corridor within that run gets
`HarmonizedElevation = max(current, naturalAtJunction + delta·w(u))` capped at `deckEndZ`, `IsPinned =
true` (`w = (1−u)²(1+2u)`). The smoother + blender then grow continuous climbs on the corridor AND the
side roads. 199's bridgehead junction (u≈0) rises toward deck height — the realistic outcome; side-road
grades may be steep and are visible in the render (warn-only philosophy, no grade clamp).

### 4.3 Junction LOWER along the dip well (the §8 mirror, user request)

`ApplyLowerRoadDipPins` (sparse only): the well half-length is no longer clamped by junctions — only by
way ends / structure spans (`MeasureRampLength(ignoreJunctions: true)`) and the 60 m default. When the
junction room WAS the binding clamp, every non-roundabout junction inside the well gets
`HarmonizedElevation = min(current, base − dip·w(u))`, `IsPinned = true`, and the well pins extend their
full length. Side roads follow the lowered junction via the blender. Non-sparse `EnableDipAsPin` keeps
the old clamped behaviour (no silent change to the legacy flag).

### 4.4 Conflicts

A junction claimed by a raise (4.2) is never lowered by a dip (4.3) — clearance is mandatory; the dip's
residual is reconciled post-solve (A7). Logged as a conflict skip.

### 4.5 Post-solve passes unchanged

`ApplyApproachRaiseRamps` (uniform, doc 04 §4.A amended) and the A7 verify/carve stay exactly as shipped —
now they top up estimate-error-sized residuals instead of delivering whole deficits, so their junction
gates rarely bind. They remain the safety net and the source of exact typed budgets.

## 5. Logging

- `[BRIDGE-PLAN] junction-raise junction=… spline=… d=…m z=a→b (deckEnd=…)` per raise; count in the
  `[BRIDGE-PLAN] spans=…` summary as `junctionRaises=N`.
- `[BRIDGE-PLAN] dip junction-lower junction=… spline=… d=…m z=a→b` per lower; conflict skips logged.
- Render check: `[BRIDGE-RAMP]` post-solve raises should shrink to ≲0.5 m; `[STEEP]` on 394-start should
  disappear; 199 should stop being SKIPPED-with-9.6 (deficit mostly absorbed pre-smooth).

## 6. Risks / watch

- Side roads at raised bridgehead junctions get real climbs (steepness visible in render; acceptable IRL).
- Estimate error moves a junction slightly off natural — consistent, hence step-free; A v2 tops up the deck.
- A lowered junction passes the dip one segment outward (side road solves pin-to-pin) — normal filter work.
- Watch render #12 for: junction seams flush (all contributors agree), no blender tug-of-war (IsPinned
  guards verified present at all write sites), dip edges still smooth.
