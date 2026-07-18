# Tunnel Banking — Follow-up Plan (v2)

> Date: 2026-07-18 · Baseline: `feature/tunnels` @ `8d25be5` (986 tests)
> Parent plan: `01-tunnel-implementation-plan.md` (§2b rule 5 deliberately shipped v1 unbanked).
> Agreed in-session 2026-07-18: "do the tunnels respect banking?" → no (v1 zeroes it) → "we do it later".

## Goal

Tunnel roadways honor the pipeline's superelevation instead of flattening it: the bank the chain
computed for the corridor flows THROUGH the span — floor slab tilted between the banked edge
elevations, apron terrain and portal collar following, portal seam bank-continuous with the
approach road. No tunnel-specific banking source is invented: whatever `BankAngleRadians` the
Phase 2.5 banking pass produced is honored (materials with banking off keep flat tunnels for free).

## 1. Current state (verified 2026-07-18)

### Where v1 flattens

| Site | What it does today |
|---|---|
| `TunnelProfileSolver.ApplyToSpan` | after writing the chord/Hermite Z: `BankAngleRadians = 0`, `BankedNormal3D = (0,0,1)`, `LeftEdgeElevation = RightEdgeElevation = z` — the single spot that erases the chain's bank |
| `TunnelDaeExporter` | builds `RoadCrossSection` with `BankAngleRadians = 0f` ("tunnels are not banked (v1)"), edge Zs carried explicitly (equal to center) |
| `TunnelMeshBuilder.BuildRing` | rings built from `s.CenterElevation` only — flat floor line; collar, headwall annuli and floor tongue all derive from these rings and inherit the flatness |
| `TunnelPortalApronStamper.StampRun` | stamps a flat surface across the width ("Tunnels are not banked (v1)") — the bridge counterpart follows `offset·sin(bank)` |
| `TunnelPortalHoleProvider.BuildSpanHoleMask` | floor/ceiling windows keyed off the flat `CenterZ`; the silhouette window ignores any cross-slope |
| Snapshot (`BridgeSpanSnapshot.Stations`) | `LeftEdgeZ`/`RightEdgeZ` fields EXIST and are captured — they just equal `CenterZ` today. No schema change needed. |

Consequence: on curved tunnels there is a bank→flat transition at each portal plane (eased by the
banking falloff, but real), and the bore never superelevates.

### Banking pipeline facts to build on

- Bank angles are computed in **Phase 2.5** (`UnifiedRoadSmoother` → `BankingOrchestrator.
  ApplyBankingPreCalculation`), which **always runs** (needed for junction awareness) — per-spline
  `BankingParameters` (`EnableAutoBanking` default false, `MaxBankAngleDegrees` 8, `BankStrength`
  0.5, curvature-driven). Roundabouts are excluded; junction behaviors fade banking near junctions.
- Phase 2.5 runs BEFORE the 3b-tunnel block, so span sections carry their bank when
  `TunnelProfileSolver.RefineSpans` executes — the solver only has to STOP erasing it and recompute
  the edges against the new floor Z (`edges = z ± halfWidth·sin(bank)`, the exact bridge formula in
  `BridgeProfileSolver.ApplyToSpan`).
- The tunnel chord shaping (`OptimizedElevationSmoother.ApplyTunnelChordToRaw`) replaces raw
  ELEVATIONS only; banking derives from plan-view curvature — no interaction.
- DecalRoads read `TargetElevation` for node Z and drape/project (`overObjects` onto the floor
  collision) — banked floor reaches them through the mesh, no decal change needed. Same for AI
  waypoints (centerline).

## 2. Design

### Flag

`TunnelRuleSystemOptions.EnableTunnelBanking` — default **off** (library/tests keep the current
flat-tunnel baseline byte-identical), included in `EnableAllRules()` so the app gets it. Off ⇒
today's zeroing path runs unchanged.

### Cross-section model: SHEAR, not rotation

The banked ring is the flat ring **sheared vertically** by the floor cross-slope:

```
z(u, v) = floorLine(u) + v      where floorLine(u) = centerZ + u·sin(bank)
```

- Floor line tilted between the banked edge Zs (identical to the driving surface the solver wrote).
- Walls stay **plumb** (world-vertical) — rising from their own tilted corner; wall tops tilt with
  the floor; the arch spans between the tilted wall tops, apex over the center at
  `centerZ + interiorHeight` (mid-slope).
- Rejected alternative — rotating the whole profile about the tangent: real tunnel bores don't roll
  with the roadway (the lining is fixed; the pavement tilts inside it), rotation tilts the
  headwall/collar visibly, and plumb walls keep the collision envelope predictable. At the real
  magnitudes involved (≤ 8° ⇒ ≤ ~0.5 m across a 16 m bore) shear and rotation are visually
  indistinguishable at the apex anyway.
- The shear derives from the station's **edge Zs**, not the angle: `slope = (RightEdgeZ −
  LeftEdgeZ) / Width`. Robust (no angle plumbing into Procedural3D), and exactly what the snapshot
  already transports.

## 3. Implementation sites

1. **`TunnelRuleSystemOptions`** — add `EnableTunnelBanking` (default off; add to
   `EnableAllRules()`; `AnyEnabled` unchanged — banking alone activates nothing).
2. **`TunnelProfileSolver.ApplyToSpan`** — flag on: keep `BankAngleRadians`/`BankedNormal3D` and
   write `LeftEdgeElevation = z − halfWidth·sin(bank)`, `RightEdgeElevation = z + halfWidth·sin(bank)`
   (mirror `BridgeProfileSolver.ApplyToSpan`). Flag off: today's zeroing. Snapshot capture is
   already edge-aware — no change.
3. **`TunnelDaeExporter`** — stop hardcoding `BankAngleRadians = 0`; it already carries
   `LeftEdgeElevation`/`RightEdgeElevation` per station, which is all the builder needs.
4. **`TunnelMeshBuilder`** — `BuildRing` gains the shear: compute `slope` from the station's edge
   Zs and emit `z = centerZ + u·slope + v`. Everything downstream follows for free because it is
   ring-derived: outer shell, portal collar (flared rings from outer rings), headwall annuli. The
   **floor tongue** tilts its top plane between the end station's edge Zs (extrapolated with the
   end grade as today). Anti-fold clamp is plan-view only — unchanged.
5. **`TunnelPortalApronStamper.StampRun`** — `target = z + offset·bankSlope` with per-pair
   interpolated bank (copy the `bankSlope` handling from `BridgeAbutmentOverlapStamper.StampRun`).
6. **`TunnelPortalHoleProvider.BuildSpanHoleMask`** — shear both windows:
   `floorAtOffset = floorZ + offset·slope` (slope lerped from the bracketing stations' edge Zs);
   clip window = `floorAtOffset ± …`, portal rule `terrainZ > floorAtOffset + ε`. Sub-meter effect,
   but keeps hole edges hugging the sheared silhouette.
7. **No change**: DecalRoads, AI waypoints, chord shaping, `TunnelSceneWriter`, presets (the flag
   rides in the serialized `tunnelRules` object automatically; importer's `EnableAllRules()` turns
   it on for old presets like every other rule).

## 4. Portal seam

Continuity is automatic: the approach road and the span share one banking field (Phase 2.5 computed
it corridor-wide), the solver stops erasing it inside the span, and the apron stamper stamps the
same banked surface the floor mesh starts with. The v1 bank→flat easing at the portal plane simply
disappears. Nothing pins or blends bank at the portal — same single-source principle as elevation.

## 5. Tests

- `TunnelProfileSolverTests`: flag on ⇒ span sections keep the pre-set bank angle and edges =
  z ± half·sin(bank); flag off ⇒ zeroed exactly as today (baseline).
- `TunnelMeshBuilderTests`: sheared fixture (edge Zs ±0.4 m) ⇒ floor-top corner Zs match the edge
  Zs, apex stays over the center at `centerZ + interiorHeight`, walls plumb (wall-face normals have
  no Z component gain), quad-count formula unchanged; `Collar_OnCurvedTunnel_NeverIntrudesIntoTheBore`
  re-run with bank ≠ 0 (the intrusion margin test must hold under shear).
- `TunnelPortalApronTests`: banked station ⇒ stamped terrain tilts across the width (left ≠ right).
- `TunnelPortalHoleProviderTests`: sheared floor ⇒ silhouette hole follows the tilt (a cell solid on
  the high side, holed on the low side at the same |offset|).
- Baseline: full suite with flag off byte-identical.

## 6. Open questions

| # | Question | Default until answered |
|---|---|---|
| 1 | Cap superelevation inside tunnels (real-world tunnel design guides limit it to ~5% vs 8° max on open road)? A cap here would be a DESIGN rule, not a mitigation — but the project has a standing no-clamp stance, so ask before adding `TunnelMaxSuperelevationPercent` | No cap: honor the chain bank verbatim |
| 2 | Should the portal COLLAR face stay level (real portals are built plumb/level) while the floor inside banks? | Collar follows the sheared rings (simplest, no seam); revisit on render feedback |
| 3 | `BankedNormal3D` consumers beyond terrain stamping — audit before keeping it non-reset | Keep writing it (cheap, consistent) |

## 7. Slicing

One commit on the tunnel branch: flag + solver + exporter + mesh shear + stamper + holes + tests.
Default-off baseline enforced by the existing suites before merge. Estimated small — every site is
a localized formula swap; the schema (edge Zs end-to-end) already exists.
