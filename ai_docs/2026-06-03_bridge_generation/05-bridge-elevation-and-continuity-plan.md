# Implementation Plan — Accurate Bridge Elevation Curve + Seam Continuity

**Date:** 2026-06-06
**Branch:** `feature/bridges`
**Supersedes / unifies:**
- `04-handoff-road-continuity-implementation.md` (plan-view seam continuity — folded in as §6)
- `ai_agent_md_files_history_some_outdated/BRIDGE_TUNNEL_ELEVATION_IMPLEMENTATION_PLAN.md`
  (the original structure-elevation-profile design — goals still valid, code retired; see §7)
**Related (still current):**
- `00-findings-and-decisions.md`, `01-spec-simple-bridge-deck.md`, `02-implementation-plan.md`

> **Scope decision (2026-06-06):** Bridges first. Tunnels are a clearly-marked future phase
> (§9) that reuses the same solver plus a below-terrain clearance constraint.
> **Approach decision (2026-06-06):** a *fresh grade-line + vertical-curve solver* that overrides
> the elevation of excluded bridge cross-sections (not a revival of the old curve-type taxonomy).

---

## 1. Current reality (verified in code, 2026-06-06)

Three mechanisms touch bridge elevation today. Only the first is live, and it is the wrong one.

### 1.1 The deck currently follows *smoothed terrain* (the core bug)

Bridge splines are flagged `IsBridge`; on the first smoothing iteration their cross-sections are
marked `IsExcluded = true` so terrain is **not** stamped under them
(`UnifiedRoadSmoother.cs` ~L1160-1178). But exclusion only controls terrain *painting* — the
excluded sections **still go through the Phase 2 chain solve**. That solve
(`OptimizedElevationSmoother.CalculateChainElevations`, `OptimizedElevationSmoother.cs:662-714`)
does, for every section including excluded bridge sections:

1. **samples the terrain heightmap** at the section centre (`:686-689`),
2. **low-pass filters** the whole chain (Butterworth/box, default window 301 × 0.5 m ≈ **150 m**),
3. assigns the filtered value to `TargetElevation` (`:712-713`).

So the deck centreline elevation is a **low-pass-filtered copy of the terrain underneath it**.
Consequences:

- Over a **short** gap the filter window keeps the deck near the approaches → looks acceptable
  (this is why the Step-0 spike saw the deck float ~15 m over a 60 m valley — the window bridged it).
- Over a **long / deep** span (a real valley, water, or ravine — exactly what bridges exist for),
  the window can no longer hold the line up and the deck **sags toward the terrain it should span**.
- The curve shape is an artifact of *filter window vs. span length*, not of any structural intent.
  There is **no grade matching** to the approaches beyond whatever the shared filter happens to do.

### 1.2 The purpose-built profile system is dead / write-only

`StructureElevationProfile` + `StructureElevationCalculator` + `StructureElevationIntegrator`
(curve types Linear/Sag/Arch/SCurve, clearance, grade) exist and contain sound math, and Phase 2.3
calls `IntegrateStructureElevationsSelective` — **but it deliberately never applies the profile to
cross-sections**:

```csharp
// StructureElevationIntegrator.cs:469-474
spline.ElevationProfile = profile;
// For excluded structures, we DON'T apply to cross-sections
// (they're excluded from terrain anyway)
// But we DO store the profile for future DAE generation
// The cross-sections remain excluded with their original terrain elevations
```

`spline.ElevationProfile` is **written once and never read** by any geometry-producing code. It is
the right *idea* (an independent structural profile) wired to nothing. We retire it (§7).

### 1.3 The export-time endpoint band-aid (and a latent divergence bug)

`BridgeDeckDaeExporter.ReconcileBridgeEndpointElevations` (`:172-266`) runs **at export time** and
linearly ramps a correction so the deck's first/last section Z matches the connected non-bridge
road (averaged). It only fixes the two endpoints (G0), on top of the smoothed-terrain curve, and it
recomputes edge elevations from the new Z + bank.

**Latent bug to fix here:** this reconciliation mutates the network cross-sections at *deck-export
time*, which runs **after** DecalRoad generation (`TerrainCreator` runs the DecalRoad block, then
`ExportBridgeDecksAsync`). So the **bridge DecalRoad markings (Step 8) use the un-reconciled
elevation while the deck mesh uses the reconciled one** — deck and lane markings can diverge at the
ends. Any real fix must run **before both consumers**.

### 1.4 Chain fragmentation makes some bridges worse or skipped

If a bridge does not join an elevation chain (synthetic degree-1 endpoint, incompatible
continuation at a junction, or a consumed continuation connector — see `NetworkElevationGraph`
`BuildElevationChains` / `BridgeMissingContinuationConnectors`, and the `[NO-BLEND CHAIN]`
diagnostic), it is smoothed in isolation with a narrow window → can spike/dip, or yield `< 2` usable
sections and be **skipped with a warning** by the exporter (`BridgeDeckDaeExporter.cs:93-100`).

---

## 2. Goals & non-goals

### Goals
1. **Span, don't sag.** Excluded bridge sections get a structural vertical profile that spans the gap
   instead of following filtered terrain.
2. **G0 + G1 continuity at both ends.** Deck endpoint elevation *and* longitudinal grade match the
   connected approach road, so vehicles cross with no vertical kink.
3. **One source of truth.** The corrected curve is written into the network **before** both DecalRoad
   generation and deck export, so markings and deck agree (fixes §1.3).
4. **Robust to chain fragmentation.** The solver derives the curve from the *approach endpoints*, not
   from the bridge being chained — so it also rescues unchained bridges (§1.4).
5. **Plan-view seam continuity** (heading/normal) folded in from doc 04, sharing one endpoint lookup.
6. **No grade clamping.** Per standing feedback, max-grade clamps distort and are not used as a
   mitigation. The profile reproduces the approach grades; it does not invent limits.

### Non-goals (this pass)
- Tunnels (future, §9). Real superelevated decks, deck thickness/piers/railings (separate spec).
- Multi-span bridges with intermediate PVIs.
- Centerline XY relocation beyond what §6 explicitly scopes.
- Changing regular-road elevation behavior in any way.

---

## 3. Chosen approach

A dedicated **`BridgeProfileSolver`** that, for each generated bridge spline
(`IsBridge && ExcludeBridgesFromTerrain`), **overrides** the excluded sections' `TargetElevation`
(and dependent edge elevations) with a smooth vertical curve fitted to the two approach endpoints in
**both position and grade**. It runs as a single network-level pass in `TerrainCreator`, **after road
smoothing/harmonization, before DecalRoad generation and deck export**.

Why this over the alternatives that were considered:
- *Detrend-the-chain* (replace terrain samples under the bridge before filtering) is the smallest
  change but leaves curve shape implicit in the filter and gives no explicit G1/clearance control.
- *Revive the curve-type system* reuses the most code but inherits a length-bucket taxonomy
  (Linear/Sag/Arch) that does not match the real artifact and carries dead wiring.
- *Fresh solver* (chosen) gives explicit G0+G1, is independent of chain success (robustness), and is
  trivially shared with the plan-view pass via one endpoint lookup.

---

## 4. Design — vertical profile solver

### 4.1 Where it runs

```
TerrainCreator (after ApplyRoadSmoothing has produced the solved network):
   ┌─────────────────────────────────────────────────────────────┐
   │ 1. BridgeProfileSolver.ApplyStructuralProfiles(network, …)   │  ← NEW, mutates network
   │      • vertical curve override (this §4)                     │
   │      • plan-view seam reconciliation (§6)                    │
   ├─────────────────────────────────────────────────────────────┤
   │ 2. DecalRoad generation   (reads corrected bridge CS Z)      │  ← already exists
   │ 3. ExportBridgeDecksAsync (reads corrected bridge CS Z)      │  ← already exists
   └─────────────────────────────────────────────────────────────┘
```

Because both consumers read the same mutated network, the §1.3 deck/marking divergence disappears.
The export-time `ReconcileBridgeEndpointElevations` becomes redundant → remove it (or reduce it to a
guarded no-op) so we don't double-correct.

> The solver is **not** placed inside `UnifiedRoadSmoother`'s phase loop: it must read the *final
> harmonized* approach elevations (post Phase 3), and keeping it in `TerrainCreator` isolates the
> bridge feature from the general smoother ordering.

### 4.2 Endpoint extraction — one shared lookup

Replace the elevation-only `FindConnectedRoadElevation` with a richer, shared helper used by **both**
the vertical solver and the plan-view pass:

```csharp
internal sealed record BridgeEndpointContributor(
    int RoadSplineId,
    float Elevation,          // approach centerline Z at the junction
    float GradeAlongBridge,   // dZ/ds, signed +ve = rising INTO the span
    Vector2 Tangent,          // approach plan tangent, oriented along the bridge
    Vector2 Normal,           // approach lateral normal (right-hand)
    float Width);

private static BridgeEndpointContributor? FindConnectedRoadContributor(
    UnifiedRoadNetwork network, int bridgeSplineId, bool isStart);
```

Lookup rules (same junction walk as the current code, `BridgeDeckDaeExporter.cs:217-244`):
- find the junction whose contributor is `(bridgeSplineId, IsEndpoint, IsSplineStart == isStart)`;
- among the other contributors pick the **best non-bridge** one: smallest plan-view XY gap to the
  bridge endpoint; tie-break by higher road priority. (Do **not** average — a pose/grade cannot be
  averaged; averaging was only acceptable for the old Z-only band-aid.)
- skip excluded junctions and other generated-bridge contributors.

**Grade estimation.** Take the approach spline's ordered sections
(`network.GetCrossSectionsForSpline(roadSplineId)`), the endpoint section at the junction and the
sections within ~`GradeSampleLengthMeters` (default 10 m) inward; least-squares (or simple
`ΔZ/Δs`) slope. Orient the sign with the bridge tangent: positive grade = elevation rising as you
move **from the approach into the bridge along the bridge's parameter direction**. The sign flip is
the same `Dot(approachTangent, bridgeTangent) < 0` test the plan-view pass uses (§6).

### 4.3 The span curve — cubic Hermite (exact G0+G1)

We want `P(s)` over `s ∈ [0, L]` with **four** endpoint constraints:
`P(0)=Z0, P'(0)=g0, P(L)=Z1, P'(L)=g1`.

> A single parabola has only 3 DOF and **cannot** independently satisfy four constraints. The unique
> low-order smooth curve that meets all four is a **cubic Hermite**. Where the two approach grades are
> equal and consistent with the chord, it degenerates to the expected straight grade line / symmetric
> parabola — which is the common case for real bridges.

```csharp
// t = s / L
float h00 =  2t³ - 3t² + 1;
float h10 =      t³ - 2t² + t;
float h01 = -2t³ + 3t²;
float h11 =      t³ -  t²;
P(s) = h00*Z0 + h10*(L*g0) + h01*Z1 + h11*(L*g1);
```

Assign `cs.TargetElevation = P(cs.DistanceAlongSpline - s0)` for every excluded section of the bridge.

**Overshoot guard (bounded, not clamped grade).** A cubic with strongly opposed endpoint grades can
bulge. This is *not* the short-ramp Hermite overshoot we've been bitten by before (that was a cubic
crammed into a tiny blend length against a through-grade — see the `feedback_b3_cubic_rejected` /
`feedback_hermite_blend_suspect` notes); here the cubic spans the **whole** bridge, where it is the
standard solution. Still, add a cheap sanity guard: if `max|P(s) − chord(s)|` exceeds
`MaxProfileBulgeMeters` (default e.g. 0.25·L capped at a few metres), fall back to a **two-grade +
symmetric parabola** (tangents g0, g1 meeting at a PVI) or, last resort, the straight chord. This
guards shape **without** clamping the approach grades themselves (honours the no-grade-clamp rule).

### 4.4 Isolated-end fallbacks

- **One end connected, one isolated:** use the connected end for `Z, g`; for the isolated end use the
  bridge's existing (chain-solve) endpoint Z and a grade estimated from the bridge's own first/last
  sections (or `g=0`). Document which was used in diagnostics.
- **Both ends isolated:** do **not** override — leave the chain-solve result and emit a warning. An
  isolated generated bridge has no approach truth to honour; inventing a span risks worse than the
  status quo. (Matches the harmonizer's existing "isolated bridges excluded" stance, Step 7.)

### 4.5 Edge elevations & banking

After overriding `TargetElevation`, recompute edges exactly as the current code does
(`ApplyEndpointCorrection`, `BridgeDeckDaeExporter.cs:261-264`):
```csharp
halfWidth = cs.EffectiveRoadWidth / 2;
edgeDelta = halfWidth * sin(cs.BankAngleRadians);
cs.LeftEdgeElevation  = cs.TargetElevation - edgeDelta;
cs.RightEdgeElevation = cs.TargetElevation + edgeDelta;
```
Bank stays flat for excluded sections (per the spike); this keeps deck mesh, colmesh, and DecalRoad
nodes mutually consistent.

### 4.6 Robustness vs. chain fragmentation (free rescue)

Because the curve is derived **only** from the two approach endpoints (whose elevations come from the
approaches' own chains, not the bridge's), the solver works even when the bridge itself never chained
— the exact case that today produces spikes or a skipped deck (§1.4). Requirement: the solver must
populate `TargetElevation` for **all** excluded sections of the span from `P(s)`, overwriting NaN /
garbage left by a failed chain. This converts "unchained ⇒ skipped/spiky" into "unchained ⇒ clean
straight-ish span", as long as at least one end is connected (§4.4).

### 4.7 Clearance — diagnostic only (v1)

A bridge should sit **above** the terrain it spans. After solving, sample terrain under each section
and warn if `P(s) < terrain(s) + BridgeMinClearanceMeters` anywhere (reuse
`StructureElevationCalculator.SampleTerrainAlongStructure`). v1 only reports; reshaping the curve to
force clearance is a follow-up.

---

## 5. Parameters / tunables

> **Status (2026-06-07):** the tunables below are `public const` defaults on the solver/excavator method
> signatures, each with an XML-doc comment at the source. The **two starred knobs are now exposed in the
> Generate-Terrain UI** (handoff item C): `BridgeMaxSagBelowChordMeters` + `BridgeDeckUndercutMeters` are
> fields on `TerrainCreationParameters` and on `TerrainGenerationState`, surfaced as numeric fields in the
> "Bridge/Tunnel Structure Handling" panel and round-tripped through terrain presets. `TerrainCreator` passes
> them to `ApplyStructuralProfiles(maxSagBelowChordMeters:)` / `Excavate(undercutMeters:)`; the consts remain
> the defaults. The non-starred knobs stay const-only.

### 5.1 Vertical profile — `BridgeProfileSolver`

| Constant | Default | Meaning |
|----------|---------|---------|
| `DefaultGradeSampleLengthMeters` | 10 m | Approach length used to estimate the endpoint grade (§4.2) |
| `DefaultMaxProfileBulgeCapMeters` | 4 m | Overshoot-guard cap; bulge threshold = `min(0.25·L, this)` → parabola → chord fallback (§4.3) |
| **`DefaultMaxSagBelowChordMeters`** ★ | 1 m | Max the deck may bow **below** the endpoint chord before the curve is blended toward the chord. The **U-vs-seam-kink lever**: lower = flatter span + larger abutment kink; higher = more sag + smaller kink. No grade clamp — the curve family is blended, grades are not capped. |
| `DefaultMinClearanceMeters` | 5 m | Deck-above-terrain **warn** threshold — diagnostic only (§4.7); does not reshape anything |

The solver also **reports** (does not tune) a per-bridge seam grade kink (`seamKink=start/end deg`) so the
sag-cap trade-off is measurable in the `[BRIDGE-PROFILE] apply` log.

### 5.2 Terrain shave under deck — `BridgeDeckExcavator`

Rule (decided 2026-06-06 after the channel/pad approaches were rejected): for every heightmap cell under the
deck footprint whose terrain is **above** the deck, lower it to that section's `deckZ − undercut`; leave
at/below-deck cells untouched. The cut follows the deck slope (per-section), so there is **no flat pad and no
slope-mismatch kink**.

| Constant | Default | Meaning |
|----------|---------|---------|
| **`DefaultUndercutMeters`** ★ | 0.05 m | How far poking terrain is set below the deck (deck stays the visible driving surface; no z-fight) |
| `DefaultEdgeMarginMeters` | 0.5 m | Extra lateral reach beyond the deck half-width so the deck never overhangs an un-shaved lip |

No max-grade parameter is introduced anywhere (no grade clamping — standing feedback).

### 5.3 Plan-view (§6, Step 5 — NOT yet implemented)

`BridgeEndpointHeadingThresholdDeg` (≈1.0) and `BridgeEndpointHeadingWarnDeg` (≈3.0) from the original plan
belong to the normal-only seam pass (§6), which is deferred. The gate (2026-06-06) showed `xyGap=0` with
`normalΔ` 12–32°, so this is a real but secondary orientation-skew item, not the vertical artifact that was
fixed.

### 5.4 Superseded / not built

- **End-taper** and **abutment pad/overlap** (a transient excavation approach) were implemented then **removed**
  — a flat pad cannot carry the road slope (adds its own kink) and the channel walls produced rasterization
  teeth. Replaced by the §5.2 shave-to-deck rule.
- The export-time `ReconcileBridgeEndpointElevations` band-aid and its result fields were **deleted** (its job
  moved into `ApplyStructuralProfiles`, run before both DecalRoad gen and deck export — §1.3 / §4.1).

---

## 6. Plan-view seam continuity (folded in from doc 04, corrected)

Doc 04's review (its §2.5) established the key fact: **the deck mesh ignores `TangentDirection`**;
deck vertices come only from `CenterPoint ± NormalDirection·halfWidth`
(`RoadMeshBuilder.BuildRoadSurface`). So the plan-view pass here is **normal-only**, gated on
diagnostics, and shares §4.2's `FindConnectedRoadContributor`:

1. **Phase A diagnostics (hard gate).** Per bridge end, log heading delta (bridge vs. approach
   tangent), XY gap, width delta, normal delta, and Z gap before/after. Decide from the dominant term:
   - **XY gap / centerline-heading dominates →** positional artifact; orientation-only won't fix it.
     Escalate to bounded `CenterPoint` welding (deferred; see doc 04 §10) — but now we *know* before
     writing code.
   - **Normal delta dominates, small XY gap →** orientation artifact; proceed with normal-only blend.
2. **Normal-only correction.** Target normal = contributor's `NormalDirection`, sign-aligned
   (`Dot < 0 ⇒ flip`); `smoothstep`-blend the bridge sections' `NormalDirection` from target at the
   seam to original at `min(20 m, 20 % L)`; renormalize; keep `CenterPoint`, width, and the §4 Z
   override unchanged.
3. **Tests assert geometry**, not the stored tangent scalar: the deck endpoint edge points
   (`GetLeftEdgePosition`/`GetRightEdgePosition`) are collinear within tolerance with the approach
   endpoint edge points.

The vertical (§4) and plan-view (§6) corrections are orthogonal and compose; both run inside
`BridgeProfileSolver.ApplyStructuralProfiles` off the one shared lookup.

---

## 7. Retiring the dead profile system

> **Status (2026-06-07): DONE (handoff item D).** The dead wiring was removed and the whole
> `StructureElevationIntegrator` class was deleted (its only consumer was the Phase 2.3 call; it stored a
> never-read `ElevationProfile`). `UnifiedRoadSmoother` lost the field, the ctor init, the
> `ConfigureStructureElevationParameters` method, and the Phase 2.3 block (replaced by a comment pointing
> here); `TerrainCreator` lost the `ConfigureStructureElevationParameters` call. 375 tests still green.

- ~~**Remove the dead wiring:** delete the Phase 2.3 `IntegrateStructureElevationsSelective` call in
  `UnifiedRoadSmoother` and the no-op "store profile, don't apply" path.~~ Done — the entire
  `StructureElevationIntegrator.cs` was deleted (both `IntegrateStructureElevations` and
  `…Selective` were the only write-only `ElevationProfile` paths reachable from live code).
- **Keep `StructureElevationCalculator.SampleTerrainAlongStructure`** — kept. The whole
  `StructureElevationCalculator` is parked (now unreferenced) for the §4.7 clearance diagnostic and future
  tunnels, as agreed.
- **`ParameterizedRoadSpline.ElevationProfile`:** property left in place; no live code writes it anymore
  (the calculator's writes are only reachable from the parked calculator).
- The historical design doc (`BRIDGE_TUNNEL_ELEVATION_IMPLEMENTATION_PLAN.md`) already carries a status
  banner pointing here; its *goals* (independent profile, entry/exit, clearance) remain the goals — only
  the curve-type implementation is retired.

---

## 8. Implementation steps (ordered, each buildable/testable)

1. **Diagnostics first (gate).** Add `[BRIDGE-PROFILE]` per-endpoint logging: connected? Z0/Z1,
   g0/g1, XY gap, heading/normal delta, deck-vs-terrain clearance min. Run on the problem map, read
   the numbers. This validates §1.1 (sag) and §6 Phase A (heading vs. position) *before* building
   correctors.
2. **Shared lookup.** Implement `FindConnectedRoadContributor` (§4.2); re-express the existing Z-only
   path on top of it to prove parity.
3. **`BridgeProfileSolver` — vertical override** (§4.3–4.6): cubic Hermite, overshoot guard,
   isolated-end fallbacks, unchained rescue, edge recompute.
4. **Wire into `TerrainCreator`** before DecalRoad gen + deck export; **remove** the redundant
   export-time `ReconcileBridgeEndpointElevations`. Verify deck and DecalRoad now read identical Z.
5. **Plan-view normal-only pass** (§6), gated on Step-1 diagnostics, sharing the lookup.
6. **Tests** (§ below).
7. **In-game validation** on a real spanning bridge (valley/water): deck spans flat-to-graded, no sag,
   flush + grade-continuous at both ends, markings track the deck, no kink in top-down.

---

## 9. Tests

`BeamNgTerrainPoc.Tests/Export/BridgeProfileSolverTests.cs` (+ extend existing bridge tests):

**Vertical:**
1. Connected both ends, valley between → deck section elevations follow `P(s)`, **stay above** the
   synthetic valley floor (no sag), and `P(0)=Z0,P(L)=Z1` within epsilon.
2. Endpoint **grade** matches the approach grade within tolerance (numerical `P'` at 0 and L).
3. Opposed endpoint grades that would bulge → overshoot guard triggers parabola/chord fallback.
4. One end isolated → connected end exact, isolated end uses fallback; no exception.
5. Both ends isolated → no override (chain-solve values unchanged) + warning emitted.
6. **Unchained-rescue:** a bridge with NaN/garbage chain elevations but one connected end → all
   excluded sections get finite `P(s)`; deck is no longer skipped.
7. Edges recomputed: `Left/RightEdgeElevation == Target ∓ halfWidth·sin(bank)`.

**Plan-view (§6):**
8. Normal-mismatch at start/end → deck endpoint edge points collinear with approach edge points ≤ 1°.
9. `CenterPoint` byte-unchanged by the plan-view pass.

**Regression:**
10. Non-bridge splines untouched (Z and edges identical with/without the solver).
11. **Deck/DecalRoad consistency:** bridge DecalRoad node Z == deck section Z at the same distance.
12. No qualifying bridges → network byte-identical (solver is a no-op).

---

## 10. Risks & guardrails

- **Overshoot:** bounded by §4.3 guard; never via grade clamps.
- **Wrong endpoint chosen at multi-way junctions:** "smallest XY gap, tie-break priority" rule; logged
  per seam so a bad pick is visible.
- **Double correction:** removing the export-time reconciler (Step 4) is mandatory, not optional.
- **Isolated bridges:** explicitly left alone (§4.4) — matches harmonizer Step 7.
- **Determinism:** no `Date.now`/random; pure function of network → reproducible.
- **Off-switch:** gate the whole pass on `ExcludeBridgesFromTerrain` (same flag as deck generation);
  with no qualifying bridges the network is unchanged (test 12).

## 11. Definition of done

1. On a real spanning bridge, the deck **spans** the gap (no terrain sag) and is **flush + grade-
   continuous** with both approaches in-game.
2. Bridge DecalRoad markings sit on the deck (same Z), not on the old smoothed-terrain line.
3. Unchained bridges that previously spiked/were skipped now produce a clean span when at least one
   end is connected.
4. Plan-view kink is materially reduced (or diagnostics show it's positional and the escalation is
   documented).
5. Full suite green; the dead profile wiring is removed; no grade clamps introduced.

---

## 12. Future — tunnels (reuse this machinery)

Tunnels reuse `BridgeProfileSolver` with one added constraint: the profile must stay **below** terrain
by `TunnelMinClearanceMeters` (+ interior height). Sample the terrain ceiling along the path
(existing `SampleTerrainAlongStructure`), and where the G1 curve would surface, push it down (a
sag/S-curve) — the inverse of the bridge clearance check. No deck/DAE for tunnels yet, so this is
profile-data + (future) portal/floor geometry only.
