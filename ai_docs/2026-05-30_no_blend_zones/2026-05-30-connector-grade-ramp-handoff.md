# Handoff — simple grade ramp at the connector seam (steep-connector artifact)

- **Date:** 2026-05-30
- **Branch:** `experimental/switch_off_blend_zones`
- **Predecessors (read first):**
  - `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md` — the no-blend technique (§1–§7).
  - Commit `f2b4426` — the §4 fix (banking only, centerline left flush). This task builds directly on it.
  - Memory: `feedback_b3_cubic_rejected` (extend length, don't change curve shape — **cubic overshoots, rejected**),
    `feedback_hermite_blend_suspect` (cubic hermite overshoots into junctions),
    `feedback_no_grade_clamp` (no max-grade clamps), `blend_propagation_architecture`.

---

## Complexity verdict: EASILY DOABLE — a single parabolic vertical curve

This is a small, well-bounded change, **not** a research problem like §2. It is the simplest smooth grade
transition that exists. Keep it to ONE parabola and it stays simple. The only real engineering care is a tiny
elevation-offset bookkeeping (see "The one subtlety"), already solvable with the existing §3 settle iteration.

**Do NOT** reach for a cubic / 4-constraint Hermite (overshoots — rejected before), per-layer blend distances,
or grade clamps. None are needed.

---

## HANDOFF PROMPT (paste to start the session)

> Continuing the no-blend work on `experimental/switch_off_blend_zones`. Read
> `ai_docs/no_blend_zones/2026-05-30-connector-grade-ramp-handoff.md`, then the §4 section of
> `…/2026-05-30-no-blend-zones-followup.md` and commit `f2b4426`.
>
> **Problem:** after the §4 fix (terminating/connector roads get banking matched but the centerline is left
> flush = a straight affine tilt to the seam), a **steep** connector meets the through/main road at a **grade
> discontinuity** at the seam (the connector arrives at a constant steep grade, the main road is near-level).
> That kink causes render artifacts on the connector and lets the connector influence the main road's edge at
> the connection.
>
> **Wanted (user):** a **calculated smooth ramp — a very easy curve** — that eases the connector's grade so
> that **at the seam it is "plain": tangent to (co-planar with) the through-road surface**. It must be a
> **simple construction**, and it must work for connectors that run **higher OR lower** than the through road.
>
> **Solution shape:** a **single parabolic vertical curve** on the connector centerline, over a short ramp
> zone measured from the seam. Tangent to the through-road grade at the seam (the "plain" connection), tangent
> to the connector's natural (§3 affine) grade at the zone end. Parabola = constant curvature, **no overshoot**,
> sign-agnostic (handles higher/lower automatically). Length is the ONLY knob (one new param). No cubic, no
> clamp.
>
> Systematic-debugging (instrument → data → fix) + TDD (pure network-level tests). I run the WinForms+Blazor
> GUI and share logs/screenshots; you cannot run it. Validate visually on the steep connector that showed the
> artifact (capture which junction/OSM node).

---

## Why a parabola is the right (and simple) curve

A parabolic vertical curve connects two constant grades `g1` and `g2` over a length `L`:

```
z(s) = z0 + g1·s + (g2 − g1)/(2L) · s²      for s ∈ [0, L]
grade(s) = g1 + (g2 − g1)·s/L               (linear in s ⇒ constant curvature)
```

- **Tangent at both ends** (grade = g1 at s=0, g2 at s=L) ⇒ no kink at the seam, no kink where it rejoins the
  straight connector body ⇒ G1 continuous.
- **No overshoot** — grade is monotone between g1 and g2; if the grades have opposite signs you simply get a
  correct crest/sag, still smooth. This is exactly why it beats the rejected cubic.
- **Higher/lower symmetric** — only the sign of `(g2 − g1)` changes; the formula is identical.

This *is* the "very easy curve" in the screenshot: steep far from the connection, flattening to match the main
road right at the green-box connection zone.

## Mapping to our pipeline

- Anchor at the **seam** (the §3-settled, flush junction Z) — that elevation must stay fixed (don't break §3).
- `g_seam` = the grade the connector must have at the seam to be **co-planar with the through surface** =
  directional derivative of the through-road tilted plane along the **connector's tangent**:
  `g_seam = throughSlope·cos θ + sin(throughBank)·sin θ`, where θ is the angle between the connector tangent
  and the through tangent (for a perpendicular T this is mostly the bank term; reuse the projection logic in
  `JunctionSurfaceCalculator.GetPrimarySurfaceElevationClamped` / `CalculateLocalSlope`).
- `g_natural` = the connector's straight §3 grade just outside the ramp zone (constant — affine is linear).
- Apply the parabola from the seam (grade `g_seam`, elev = seam Z) back to the zone end at distance `L`
  (grade `g_natural`). Write **only** the connector centerline `TargetElevation` inside `[0, L]`; recompute its
  edges around the new centerline using the §4 banking that's already set (`center ± halfW·sin(bank)`), so
  banking is preserved. **Do not touch the through road.**
- New param e.g. `ConnectorGradeRampLengthMeters` (or `…SurfaceWidthMultiplier`), TEMP/not-for-UI like
  `BankingRunoffSurfaceWidthMultiplier`. Slot it as its own post-loop pass AFTER §4 in the `affineThroughActive`
  block (so it runs on the flush centerline + matched bank), or fold into §4 — decide once you see the data.

## The one subtlety (the only thing to get right)

A parabola tangent to `g_seam` at the seam sits at a slightly different height than §3's straight line at the
BVC (zone end) — the curve "cuts the corner." Max deviation of a parabola from the corner is `L·|g2−g1|/8`
(tiny for small grade change + short `L`). That offset must **not** march down the connector and move its far
junction. Options (pick with data):
1. Keep `L` short and **re-run the existing §3 settle iteration** so the far end re-converges (preferred —
   the machinery already exists and converges).
2. Confine the curve so the body beyond the zone is untouched and accept a sub-cm G0 step at the zone end
   (usually invisible; verify).

For **very short connectors (~20 m)** `L` must be a fraction of the connector length
(`L = min(fixed, frac·connectorLength)`), so the ramp can't eat the whole connector or reach the far junction.
This length budget is the headline constraint — and since length is the only knob, it stays the "simple
construction" the user asked for.

## TDD (pure, network-level — no GUI)

1. **Tangent at seam** — connector grade at the seam == through-surface directional grade (the "plain"
   connection); seam **elevation unchanged** (§3 invariant holds).
2. **Tangent at zone end** — grade there == connector's natural §3 grade (no kink rejoining the body).
3. **No overshoot / monotone curvature** — centerline within the zone never exceeds the envelope of its two
   tangents (sample a few points; assert no bump beyond `L·|Δg|/8`).
4. **Higher AND lower** — run with `g_natural` steeper-up and steeper-down than `g_seam`; both produce a smooth
   curve, seam stays flush.
5. **Short connector** — with a 20 m connector, `L` is clamped to a fraction of its length and the far junction
   Z does not move beyond tolerance (propagation guard).

## Validation handles

- Capture the steep-connector junction (OSM node/way) that showed the artifact; reuse `[NO-BLEND …]`
  diagnostics, e.g. `[NO-BLEND RAMP]` logging `g_natural`, `g_seam`, `L`, and the resulting centerline so a
  bump shows as non-monotone.
- Acceptance = visual: steep connector eases to plain at the seam, no render artifact, main road undisturbed at
  the edge; works for both an uphill and a downhill connector.

## REVIEW/FIX 2026-05-31 — local end-weld, not full-body regrade

Visual feedback: roads were getting destroyed because the first implementation kept the far junction fixed by
computing a new `g_body` and rewriting the entire connector beyond the ramp on that new grade. That made a
"20 m ramp" capable of changing hundreds of meters of connector body. The intended behavior is only the last
meters near the seam, like welding the curve at the end.

Fix direction now used in code: keep the existing settled connector profile as the baseline, apply only a local
correction over `[0,L]`, and leave every cross-section past `L` untouched:

```
dz(s) = (g_seam - g_natural) * s * (1 - s/L)^2,  s in [0,L]
```

This gives `dz(0)=0`, `dz'(0)=g_seam-g_natural`, `dz(L)=0`, `dz'(L)=0`: seam Z stays fixed, seam grade matches
the through surface, and the weld rejoins the unchanged body with matching grade. Default length is now 6 m.

Short-connector rule for this weld: short connectors are included, but `L` is clamped to at most 25% of total
connector length `D`, so even a 20 m connector only receives at most a 5 m end-weld. The default is genuinely
"last meters" behavior.

## IMPLEMENTED 2026-05-31 (superseded by local end-weld review above)

Chosen construction: **one seam-anchored parabola + closed-form body re-tilt** (the doc's preferred option 1,
solved analytically instead of iterating — no propagation, no mid-connector step).

- `UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface(network)` — per T-junction (same filter as §3/§4),
  for each terminating connector:
  - `g_natural = (zFar − zSeam)/D` (constant affine grade).
  - `g_seam = primarySlope·(into·T) + sin(primaryBank)·(into·N)` (directional derivative of the through plane
    along the connector's into-body tangent — the "plain"/co-planar grade).
  - `L = min(ConnectorGradeRampLengthMeters, 0.4·D)` (the 0.4 frac cap is the short-connector guard, not a knob).
  - `g_body = (g_natural·D − g_seam·L/2)/(D − L/2)`; parabola `z = zSeam + g_seam·s + (g_body−g_seam)/(2L)·s²`
    on `[0,L]`, straight `g_body` body on `[L,D]` → lands on `zFar` EXACTLY (seam **and** far junction both
    fixed). Edges re-derived as `center ± halfW·sin(bank)` so §4 banking is preserved. No-ops when
    `|g_natural − g_seam| < 1e-4`.
  - Wired in `SmoothAllRoads` immediately AFTER the §4 `MatchTerminatingBankingToThroughSurface` call, inside
    the `if (affineThroughActive)` block (runs last). Logs `[NO-BLEND] ramp: eased N …`.
- New param `JunctionHarmonizationParameters.ConnectorGradeRampLengthMeters` (default **20**, 0 = off,
  not-for-UI). No new enable flag — gated by `affineThroughActive` like §3/§4.
- Tests: `BeamNgTerrainPoc.Tests/Junction/ConnectorGradeRampTests.cs` (6) — seam tangent + seam-Z fixed; zone-end
  G1 (no kink); no-overshoot/monotone + straight body; higher-AND-lower (Theory ±); short-connector clamp +
  far-junction unmoved.
- TEMP `[NO-BLEND RAMP]` diagnostic (per connector): `g_natural`, `g_seam`, `L`, `g_body`, `Δbody`, `zSeam/zFar`.
  Remove per the followup §7 cleanup. `|Δbody|` large ⇒ ramp too long for the connector (shorten the param).

**NEXT = user render of `franco_same_prio`.** Validate at the steep-connector junction from the screenshot
(capture its OSM node/way). Grep `[NO-BLEND RAMP]` to confirm it ran + read the per-connector grades. Acceptance:
connector eases to plain at the seam, no render artifact, main road undisturbed at its edge; works uphill AND
downhill. If the "main road influenced at the edge" persists after the grade is tangent → that residue is §5
mask-ownership (separate; note, don't bundle). If the ramp feels too long/abrupt, tune `ConnectorGradeRampLengthMeters`.

## Relationship to other open items

- This refines §4 (it's still "match the through surface," now with a smooth *grade* approach, not just bank).
- It is **distinct** from the parked §2 (absolute depth) — don't reopen that here.
- If the "main road influenced at the edge" persists *after* the grade is tangent, that residue is §5
  mask-ownership (the connector claiming through-road pixels), a separate fix — note it, don't bundle.
- The roundabout handoff (`2026-05-30-roundabout-no-blend-handoff.md`) will want this same ramp on its short
  entry/exit connectors — build it general enough to reuse there.
