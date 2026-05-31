# Roundabout no-blend connectors + terrain-following ring plane — design

- **Date:** 2026-05-30
- **Branch:** `experimental/switch_off_blend_zones`
- **Predecessors:** `ai_docs/no_blend_zones/2026-05-30-roundabout-no-blend-handoff.md`,
  `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md` (§3/§4 T-junction no-blend technique).
- **Status:** design approved (brainstorming); awaiting spec review → implementation plan.

## Goal

On the no-blend path, roundabout **connecting roads** must meet the ring and the ongoing roads with the
same no-blend technique used at T-junctions — affine linear tilt (curvature preserved), banking matched at
the seam, no hermite/parabolic blend-zone elevation curve. Additionally, the **ring itself** must sit closer
to terrain (as a realistic tilted plane) so the connector↔ring meeting does not require a large embankment.

## Evidence (franco_same_prio, log 20260530_205107, `[NO-BLEND RAB]`)

One roundabout: ring = spline 35, forced **uniform Z 100.59** (spread 0.00). Seven connectors. Ring Z is the
through-road authority; the connector's ring-side endpoint floats off it:

| J# | connector | len | ring-seam step (connEnd − ring) | far end | ongoing Z | authority step (ring − ongoing) | body |
|---|---|---|---|---|---|---|---|
| 218 | 86 | 443 m | −1.65 | dead-end | — | — | smooth |
| 219 | 100 | 431 m | −0.50 | dead-end | — | — | smooth |
| 220 | 106 | **16 m** | −1.59 | TJ→sp100 | 100.59 | 0.00 | smooth |
| 221 | 110 | 689 m | +0.51 | dead-end | — | — | smooth |
| 222 | 111 | **19 m** | +0.72 | TJ→sp110 | 100.59 | 0.00 | smooth |
| 223 | 112 | **23 m** | +1.26 | TJ→sp113 | 100.85 | −0.26 | smooth |
| 224 | 113 | 2811 m | +0.18 | dead-end | — | — | smooth |

**Findings:**
1. Every connector's ring-side endpoint floats off the ring (−1.65…+1.26 m) → a **step at every ring↔connector
   seam** (the roundabout bumpiness). Connector bodies are smooth; `blended=False` everywhere (the legacy
   connector blend is already off via `skipConnectingRoadBlending:true`).
2. Short connectors' far ends are **already flush** with their ongoing roads (far Z == ongoing Z exactly) —
   those far junctions are normal T-junctions §3 already handles. Only the *ring* seam is unhandled.
3. The two authorities (ring 100.59, ongoing ~100.6–100.85) **agree** within 0.26 m → once the ring end is
   pinned, a short connector tilts gently between near-equal Z's (no steep ramp, no back-propagation; the
   ongoing roads are *through* at the far junction so §3 never moves them).
4. Diagnosis per the handoff's tree = **endpoints DISAGREE → targeting**, not transition-blend, not
   propagation. Root cause: roundabout junctions are doubly gated out of §3/§4 — explicit `Type==Roundabout`
   skip **and** `IsExcluded=true` set by `RoundaboutElevationHarmonizer`.

Separately, the uniform disk at 100.59 sits ~+1.3 m above terrain on most of the loop and ~−2 m below on the
SE side (terrain 99.0→103.1 across the ~30 m ring) → the embankment the user wants reduced.

## Civil standard (user-provided, applied to the ring)

- **Standardquerneigung 2.5%** outward crown (Dachprofil) — ring carriageway drainage banking. **Follow-up,
  not this pass.**
- **Max Querneigung / Gefälle 6% absolute** — the tilted ring **plane is clamped to ≤6%**; terrain demanding
  more becomes unavoidable cut/fill.
- **Längsneigung Zufahrt 4–5% (max 7%)** — connector longitudinal-grade target; **reported**, not enforced
  (enforcing would break the flush meet).

> **Grade-clamp reconciliation:** `feedback_no_grade_clamp` rejects clamps that distort natural
> terrain-following road profiles. The 6% clamp here applies **only to the engineered roundabout ring plane**,
> where 6% is a real geometric limit — modeling reality, not distorting it. No other road is clamped.

## Decisions

1. **Connectors:** approach A — feed roundabout junctions through the existing §3/§4 passes (lift both gates).
2. **Ring:** replace the uniform horizontal disk with a **tilted plane least-squares-fit to terrain** under the
   ring footprint, **clamped to ≤6%** tilt.
3. **Crown (2.5% Dachprofil):** out of scope this pass (follow-up).
4. **Connector grade:** report in diagnostics, flag >7%; do not enforce.
5. **Gating:** the tilted plane and approach-A behavior apply on the **no-blend/affine path only**; the legacy
   (blend-on) path keeps the uniform disk and the existing roundabout exclusion.

## Component 1 — tilted ring plane (`RoundaboutElevationHarmonizer`)

- New helper: least-squares plane `z = a·x + b·y + c` over the terrain samples already collected at each ring
  cross-section center (`CalculateRoundaboutElevation`, current lines ~186–196). Closed-form 3×3
  normal-equations solve (pure function; degenerate-safe — a ring spans 2-D so the system is well-conditioned).
- **Clamp tilt:** if `sqrt(a²+b²) > 0.06`, scale `(a,b)` down to magnitude 0.06 about the ring centroid
  (keep the centroid Z fixed so cut/fill stays balanced). The pre-clamp tilt is logged.
- `ApplyUniformRingElevation` writes `cs.TargetElevation = a·x + b·y + c` per ring cross-section when the tilted
  plane is active, instead of one constant. Uniform disk = the zero-tilt special case, so this strictly
  generalizes today's behavior.
- **Gating:** add a `useTiltedPlane` parameter to `HarmonizeRoundaboutElevations` (mirrors the existing
  `skipConnectingRoadBlending`). The caller (`UnifiedRoadSmoother`, Phase 2.6) passes the no-blend/affine
  predicate (the same `affineThroughActive` test currently computed before §3, hoisted earlier). When `false`,
  behavior is unchanged.

## Component 2 — approach A: connectors through §3/§4 (`UnifiedRoadSmoother`)

In both `RetargetTerminatingRoadsToSettledThrough` (§3) and `MatchTerminatingBankingToThroughSurface` (§4):
- Remove `Roundabout` from the `Type is …` skip (keep `MidSplineCrossing`/`Continuation`; keep `Endpoint` in §4).
- Change `if (junction.IsExcluded) continue;` →
  `if (junction.IsExcluded && junction.Type != JunctionType.Roundabout) continue;`.

No other change is needed: the ring is the `IsContinuous` contributor (verified
`NetworkJunctionDetector.CreateRoundaboutJunction`), so `ThroughRoadJunctionElevation.Compute` returns the
**local tilted-plane ring Z** at each junction; §3 pins the connector's ring-end target there and affine-tilts
the connector between {ring-plane Z} and {settled ongoing Z}; the ring is never moved (through everywhere);
§4 bank-matches the ring seam. The §3 iteration resolves the coupling where a spline is both a roundabout
connector and a through road at another junction (e.g. spline 100 — connector at J#219, through at J#201).

## Component 3 — diagnostics

Extend `[NO-BLEND RAB]` (already added): per connector log the post-fix ring-seam step (should be ≈0), the
ring-plane tilt (pre/post 6% clamp), and the connector longitudinal grade with a `>7%` flag. Remove with the
rest of the TEMP `[NO-BLEND]` diagnostics per followup §7.

## Risks (verify during implementation)

- **`RoadMaskBuilder` fill disk:** §3 sets `HarmonizedElevation` on roundabout junctions — confirm Phase 4
  either rasterizes a correct fill disk at the ring-plane Z or still skips excluded roundabouts (no conflict
  with the painted ring surface).
- **`RestoreRoundaboutJunctions` 15 m radius:** data shows all far T-junctions (J#201/208/209) survived, so
  connectors keep a valid far target — re-confirm none are eaten.
- **Plane vs. ongoing coupling:** tilting the ring changes the ring-plane Z at each connection point; confirm
  the new connector grades stay within target and no new step appears at the far (ongoing) seam (the §3
  iteration should absorb it; verify with the post-fix `[NO-BLEND RAB]`).

## Testing (TDD, network-level, no GUI)

Plane fit (new test class, e.g. `RoundaboutTiltedPlaneTests`):
1. Flat terrain → plane tilt ≈ 0; all ring Z equal the mean (uniform preserved).
2. Tilted terrain → ring Z follows the plane; cut+fill RMS reduced vs the uniform disk.
3. Terrain tilt > 6% → plane clamped to 6%; centroid Z unchanged.
4. `useTiltedPlane=false` → uniform disk preserved (legacy unchanged).

Approach A (extend `RetargetTerminatingToSettledThroughTests` / `BankingMatchToThroughSurfaceTests`):
5. Roundabout junction now retargets the connector's ring-end to the ring-plane Z (**replaces** the existing
   "roundabout-skip" test, which asserted the opposite).
6. Ring spline is never moved by §3 (it is the through contributor).
7. §4 sets banking at the ring seam (connector matches ring surface).
8. Short connector between ring-plane Z and ongoing Z → centerline monotone, no mid-connector bump.

## Out of scope

- 2.5% Dachprofil crown banking on the ring (follow-up).
- The legacy blend-on path's roundabout behavior (unchanged).
- §2 network-wide absolute depth (parked).
- §7 TEMP-diagnostic + flag cleanup (tracked separately).
