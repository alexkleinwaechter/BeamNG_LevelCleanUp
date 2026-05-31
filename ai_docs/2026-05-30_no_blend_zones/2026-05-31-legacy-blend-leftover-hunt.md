# Hunting leftovers from the old blend-zone mechanisms — a search guide

- **Date:** 2026-05-31
- **Branch:** `experimental/noblendzones_code_cleanup`
- **Purpose:** the road pipeline grew through many iterations of junction-smoothing ("blend zones"). The
  no-blend rewrite replaced that philosophy, and the cleanup pass (T1–T6) already deleted several flags/methods.
  But abandoned/half-wired attempts likely remain — code that still runs on the no-blend path, or dead code that
  confuses future readers. This doc is a **method** for finding them, plus a **live inventory** of confirmed and
  suspected leftovers to triage. It is a working list, not a finished audit.
- **Related:** `2026-05-31-propagated-mid-spline-influences-findings.md` (one confirmed leftover),
  `2026-05-31-edge-elevation-desync-bug.md`, `2026-05-30-no-blend-zones-followup.md` §7 (cleanup checklist),
  `ai_docs/code_cleanup_no_blend_zones/2026-05-31-cleanup-and-ui-overhaul-plan.md`.

---

## 0. The mental model — three states a leftover can be in

1. **Active-and-harmful:** still runs on the no-blend path and moves roads against the no-blend rule.
   *(e.g. `PropagatedMidSplineInfluences` — it was ungated.)* **Highest priority.**
2. **Dead-but-present:** gated off by a `const false`, an always-true blend-off branch, or a default-off flag;
   never executes, but clutters reading and rots. **Remove for clarity.**
3. **Orphaned data:** fields/properties that are written but never read by anything that reaches the renderer
   (or read only by other dead code). **Remove the field + its writers.**

The danger order is 1 ≫ 2 ≈ 3. Find category-1 first (it changes output), then sweep 2 and 3.

## 1. How to search (techniques)

**Exact symbol / flag liveness — ripgrep (authoritative for "is this used"):**
- A flag that is only ever *declared* and read in one dead branch is dead: `rg -n "EnableX"` → if all hits are
  the declaration + one `if (EnableX)` whose body is unreachable, it's dead.
- A method with no callers: `rg -n "MethodName\b"` → one hit (the definition) = dead.
- A field written-but-not-read: count `rg -n "\.FieldName\s*="` (writes) vs `rg -n "\.FieldName\b"` minus those
  (reads). Reads only inside dead code ⇒ orphaned.

**Concept discovery — ChunkHound semantic search** (installed for this repo; see CLAUDE.md / memory
`chunkhound_setup`): query for *ideas*, not symbols — "blend zone ramp elevation near junction", "snap road
endpoint to primary surface", "taper elevation toward junction", "hermite/parabolic vertical curve",
"grade clamp / max slope". Surfaces code that doesn't share a keyword with what you grepped.

**Suspicious-comment sweep:**
`rg -ni "legacy|deprecated|obsolete|old (system|blend|path)|no longer|TEMP|HACK|TODO|for now|used to|previously"`
in `BeamNgTerrainPoc/Terrain`. Blend-era code is heavily commented with its own history.

**Writers of the things that matter (the real test of "does it move roads"):**
- `rg -n "\.TargetElevation\s*="` — every centerline write. For each, ask: is it gated by the blend-off path?
  Does it run unconditionally? `PropagatedMidSplineInfluences` was found exactly this way.
- `rg -n "\.BankAngleRadians\s*="` — banking writes.
- `rg -n "LeftEdgeElevation\s*=|RightEdgeElevation\s*=|Constrained\w*EdgeElevation\s*="` — edge writes
  (cross-reference the edge-desync doc).

**Gate audit:** the no-blend path is selected by the blend flags being off and the affine path being active.
Grep the dispatch points and list every method that is *not* behind that gate but still touches elevation/bank
near junctions. Those are category-1 candidates.

## 2. Confirmed leftovers (this session)

- **`PropagatedMidSplineInfluences` (Step 5b) — category 1.** Ungated through-road nudge. Removal plan in its
  own doc. **Decision: remove.**
- **Edge/centerline desync — category-1 *bug* (not a single dead method).** Affine/§3 move the centerline,
  edges go stale, the painter splits. See the edge-desync doc.

## 3. Suspected leftovers to triage (verify each — do NOT assume)

> Several blend-era flags (Hermite/parabolic junction blend, grade-skip, max-grade clamp, the
> `EnableAffineJunctionLeveling` master flag, `AffineJunctionTargetMode`) appear to have **already been removed**
> in T1–T6 — a quick `rg` for them in `BeamNgTerrainPoc` returned no parameter declarations. Confirm they are
> fully gone (no stale references in tests/presets/DTOs) and cross them off, rather than re-hunting them.

| Candidate | Where | Suspected state | How to confirm / action |
|---|---|---|---|
| **Endpoint propagation** (`PropagateConstraintsThroughShortSplines`, the `propagated`/`[PROPAGATE]`/`[PROPAGATE-BLEND]` branch) | `UnifiedJunctionProfileBlender` | Feeds `constraints` consumed by the gated-off `BlendSplineProfile*` → likely **inert** on no-blend (category 2). | Trace where the added constraints are consumed; if only by gated-off blend methods, it's dead on no-blend. Decide remove vs keep-for-legacy. |
| **`FinalSnapTJunctionEndpoints`** | `UnifiedJunctionProfileBlender` (~2536), called from `UnifiedRoadSmoother` | Gated off per terminating spline on no-blend (logs `[NO-BLEND T-SNAP] SKIPPED`). Category 2. | Confirm it can *never* fire on no-blend; if so, remove or keep strictly for a legacy (blend-on) workflow. |
| **`ApplyEndpointTapering`** (Step 6) | `UnifiedJunctionProfileBlender:~228` | Skipped when `EnableEndpointTerrainSlopeMatch` (default true). Category 2 on no-blend. | Verify default path always skips; check the dead-end interaction (followup §6). |
| **`Constrained{Left,Right}EdgeElevation` + `ApplyEdgeConstraints`** | `UnifiedCrossSection:197/204`, `JunctionSurfaceCalculator:126/153`, `NetworkJunctionHarmonizer:437` | Written by `ApplyEdgeConstraints`; read inside `JunctionSurfaceCalculator.GetPrimarySurface*`. Followup §4 flagged these as "orphaned slop the renderer never reads." Nuance: they ARE read within JunctionSurfaceCalculator — confirm whether *that* read path reaches the no-blend renderer or is itself dead. Category 2/3. | Map the full read chain to Phase 4. If it dead-ends, remove the fields + writers + the constraint code. |
| **`NetworkJunctionHarmonizer` steps with side effects** | `NetworkJunctionHarmonizer` | Memory "Terrain Wall Bug" (2026-03-06): steps 4–7 leaked side effects even when "detection only". Some may still run. Category 1 risk. | Audit each step that runs on no-blend for writes to elevation/IDW/plateau; confirm `skipPropagation` covers them. |
| **`JunctionBlendDistanceMeters`** | `JunctionHarmonizationParameters:149` (default 50) | Should no longer affect *elevation* on no-blend; still used by IDW terrain-blend taper + roundabouts. Category 2 (partial). | Grep its consumers; document that elevation no longer depends on it; keep only the terrain-taper/roundabout uses. |
| **`JunctionBankingAdapter`** | comments/docs only (per followup §9c) | Believed deleted as code. | `rg` to confirm no live code; if only comments remain, scrub the comments. |
| **`EnablePropagationOverlapTaper` + `SplineClaimedZones`** | `JunctionHarmonizationParameters:72`, `SplineClaimedZones.cs` | Support for Step 5b only → dead once Step 5b is removed. Category 3. | Remove together with `PropagatedMidSplineInfluences` (see that doc's §7). |

## 4. Per-candidate triage checklist

For each candidate, before deleting:
1. **Liveness:** `rg` every reference. Classify category 1/2/3.
2. **Gate proof:** if "dead on no-blend," prove the gate — show the branch is unreachable when blend flags are
   off + affine active. A `const false` or an always-taken `continue` is proof; a runtime flag is not (it could
   be flipped by a preset — grep presets/DTOs/UI).
3. **Shared-helper check:** does anything *live* call into the same helper? (e.g. `CollectInfluencesFromCrossing`
   is shared by the legitimate Step 5 — must survive.) Don't remove shared leaf methods.
4. **Data reach:** for orphaned fields, trace the read chain all the way to Phase 4 / DAE export. "Read by
   another dead method" still counts as dead.
5. **Tests:** find tests pinned to the candidate (`rg` the symbol in `BeamNgTerrainPoc.Tests`). Delete tests that
   only exist for removed behaviour; keep/adjust tests that assert a still-valid invariant.
6. **Remove + verify:** build green, full terrain suite green, regenerate `franco_same_prio` (and one elevation
   map) with `EmitNoBlendDiagnostics=true`, diff the `[NO-BLEND DIAG]` against the pre-removal log — output must
   be identical for category-2/3 removals (they were dead) and *improved* for category-1.

## 5. Working notes / running tally (update as you go)

- [ ] Confirm Hermite/parabolic/grade-skip/clamp flags fully removed (T1–T6) — no stale refs.
- [ ] `PropagatedMidSplineInfluences` + `SplineClaimedZones` + `EnablePropagationOverlapTaper` — REMOVE (planned).
- [ ] Endpoint propagation branch — classify (likely inert on no-blend).
- [ ] `FinalSnapTJunctionEndpoints` — classify (skipped on no-blend).
- [ ] `ApplyEndpointTapering` — classify (skipped when slope-match on).
- [ ] `Constrained*EdgeElevation` + `ApplyEdgeConstraints` — map read chain to renderer.
- [ ] `NetworkJunctionHarmonizer` steps 4–7 side effects — re-audit for no-blend leakage.
- [ ] `JunctionBlendDistanceMeters` — document remaining (non-elevation) uses.
- [ ] `JunctionBankingAdapter` — confirm code-gone; scrub stale comments.

## 6. Guardrail

Removing dead blend code is low-risk **only if** the gate proof (step 2) is real. The most expensive past bugs
here (memory: "Terrain Wall Bug", "Roundabout Junction Preservation Bug") were *side effects of a legacy system
that ran "for detection only" but still mutated shared state*. So treat every "it's just detection / it's
gated" claim as a hypothesis to verify with the diff in step 6, not a given.
