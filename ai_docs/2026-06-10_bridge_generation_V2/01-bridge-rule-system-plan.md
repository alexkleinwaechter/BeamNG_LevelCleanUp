# Bridge Rule System — Implementation Plan

## Context

Procedurally-generated OSM bridges on DEM terrain currently produce visible artifacts: a near-vertical **terrain cliff** at every abutment (the stamped approach embankment meets the unstamped span in ~one heightmap cell), **lateral drift** of merged-corridor bridges, no awareness of **what a bridge crosses** (road/rail/water/terrain), and no principled **raise-vs-dip** decision. A parked branch (`feature/bridges`) tried an "always raise the deck" simplification and accumulated problems — which is why we reset to the clean merged-corridor base (`feature/bridge_merged_corridor` @ `adeeb9a` "Phase D").

We are implementing the rule system specified in **`examples_for_ai/Bridge Rule System/Bridge_Rule_System_EN.md`** (COMBRI + Kapu sources): for each bridge compute required vertical separation `S = Clearance(obstacle) + DeckDepth(span)` and distribute it between **R** (raise deck) and **L** (lower the obstacle road) by **priority** — *the more important road keeps its smooth profile; the less important one absorbs the deviation* — then stamp embankments/cuts so the terrain follows.

**Outcome:** bridges that clear what they cross by type-correct margins, ramp cleanly via the less-important road, and sit in graded terrain (no cliffs), with the deck **continuous with the road by construction** (it already is — bridges are arc-ranges inside the merged corridor).

### Architecture decisions (locked with the user)
- **Bridges are first-class early entities, like junctions.** Before the smoothing iterations, an early phase computes each bridge's **characteristic coordinates** — abutment stations, crossing point(s) + obstacle kind + obstacle surface Z, required clearance, the §3.5 R/L decision, the resulting required deck Z + lower-road dip targets, ramp lengths — producing a `BridgePlan`. This mirrors junctions (`detect → pin → smoother honors`). **Crucially, obstacle Z is read from the early road-elevation estimate the junction phase already establishes — NOT the raw DEM** (raw pre-smooth DEM ≈ embankment banks, which is what made the parked branch's §5a raise/dip gate misfire). Bridges and junctions become peer constraint-sources; a bridge abutment that is also a junction is reconciled in the 1.85/1.9 pin ordering.
- **Constraint-feed integration:** the rule engine feeds those decisions as **pins/constraints the EXISTING smoother + junction harmonizer solve** — not a separate late pass, not a joint re-solve. Reuse `UnifiedCrossSection.PinnedElevation` (already honored by every elevation pass). **Continuity is the #1 priority.**
- **Phasing:** decision engine **first** (integrated with smoothing), **then** R8 terrain stamping (must follow solved elevations).
- **Raise AND dip** by the §3.5 priority table (reverses the parked "always raise").
- **Max-slopes** apply ONLY to constructed approach ramps to a raised deck — **never** the natural through-road.
- **Deck depth:** use spec `clamp(span/20, 0.8, 6.5)` for the *clearance budget only*; keep the visible deck mesh thin (current `≤1.2 m`).
- **`*_link` roads:** no special-casing — priority + ramp feasibility already make them the natural absorber.
- **Lateral drift fix:** preserve original bridge geometry + re-project span distances; deck stays = merged curve (continuity preserved).
- **Clean slate:** revert the 6-file uncommitted abutment-wall diff first; keep `ai_docs/2026-06-03_bridge_generation/24-…-overlap-plan.md` marked superseded. On approval, persist this plan as **`ai_docs/2026-06-10_bridge_generation_V2/01-bridge-rule-system-plan.md`** (new V2 folder = clean rule-system restart, distinct from the 2026-06-03 series).

All new behavior is gated behind feature flags (Phase 0) so each rule is independently togglable and flag-off stays byte-identical.

---

## Phase 0 — Foundations

**0.1 Clean slate.** `git restore` the 6 changed files (`BridgeDeckMeshBuilder.cs`, `RoadCrossSection.cs`, `BridgeDeckMeshBuilderTests.cs`, `BridgeDeckDaeExporter.cs`, `BridgeProfileSolver.cs`, `BridgeSpanSnapshot.cs`). Add a "superseded by doc 25" note to doc 24.

**0.2 Central config + feature flags.** New `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs` holding every rule param (clearances, deckDepth bounds, §3.3 slope table, cut limits, §3.5 shares, collar/feather) **plus a feature-flag block** (`EnableObstacleTyping`, `EnablePriorityDistribution`, `EnableRampFeasibility`, `EnableDipAsPin`, `EnableEmbankmentStamping`, `EnableCuts`, `EnableAbutmentPlacement`, `EnableBridgeBridge`). Nest it on `TerrainCreationParameters` and `RoadSmoothingParameters`; thread `state → options` in `TerrainGenerationOrchestrator.BuildTerrainParametersAsync`. Follows the existing `Enable*`/`Exclude*` toggle convention (e.g. `JunctionHarmonizationParameters`). `BridgeElevationPlannerOptions` reads from it.

**0.3 Lateral-drift fix (preserve geometry + re-project).** The original per-way polyline is discarded at merge today (only `OsmWayIds` survive); Chaikin corner-cutting at the join + Akima resample drift the centerline.
- Store the original bridge-way endpoint nodes (and polyline) on `StructureSegment` (extend the seed in `OsmGeometryProcessor` where structure segments are created).
- Re-resolve `StructureSegment.StartDistance/EndDistance` by **projecting those endpoint nodes onto the FINAL merged spline** (`RoadSpline.GetClosestDistanceAndNormalAt`-style), replacing the pre-Chaikin arc-length sums in `PropagatePathStructureSegmentsToSpline`.
- Damp Chaikin corner-cutting at merge joins (`PathSmoothing.ChaikinSmooth` call sites in `OsmGeometryProcessor`) so the corridor stops cutting the corner at abutments. Deck stays = merged curve. **Validate with a render.**

**0.4 OSM obstacle detection ("what's under the bridge").** Source = the in-memory `OsmQueryResult.Features` (full geometry + tags, retained) — railways/waterways are NOT splines but ARE here. New `BridgeObstacleClassifier`:
- Build a light spatial bucket (per terrain tile) of `OsmFeature`s by category (none exists today).
- For each bridge span footprint (`BridgeSpanFootprint`, already exists), query intersecting features → classify `Road` / `Rail` / `Water` / `Terrain` from `Category`/`Tags` (`railway=*`→Rail incl. `electrified`; `waterway=*`/`natural=water`→Water incl. navigability via `boat`/`CEMT`/`canal`/width; else Road; none→Terrain).
- Layer rasters (`OsmLayerExporter` PNGs) are the fallback sampler. (Future: railroad-as-placeable-road just adds a spline source.)

**0.5 Placeholders.** Add a bridge-to-bridge relationship marker in the junction model (`NetworkJunction` / `JunctionType`) — detection only, no resolution yet. Add a hook point for bespoke bridge-DecalRoad rules + future AI waypoints (documented seam, no logic).

---

## Phase A — Decision engine (early bridge characterization → constraint-feed)

Runs as an **early phase before the smoothing iterations**, peer to junction detection/pinning: it computes the per-bridge `BridgePlan` (characteristic coordinates) from the early road-elevation estimate, then emits deck + dip **pins** the existing solver honors. Pipeline insertion: `1.8 detect junctions+crossings → 1.8b compute BridgePlan → 1.85 pin deck+dip → 1.9 pin junctions → 2 smooth`. Each step builds + tests green; flag-off byte-identical.

A new `BridgePlan` model (per span: abutment stations, crossings[kind/Z/clearance/R/L], required deck Z, dip targets, ramp lengths) is the durable artifact — the bridge analogue of the junction set.

| Step | What | Key files |
|---|---|---|
| **A1** | Add `BridgeObstacleKind {Road,Rail,Water,Terrain}` + `LowerKind`/`LowerNavigable`/`Electrified` to `GradeSeparatedCrossing`; populate from 0.4 at both crossing constructors. | `Models/RoadGeometry/GradeSeparatedCrossing.cs`, `Algorithms/NetworkJunctionDetector.cs` |
| **A2** | Per-type clearance (`ClearanceFor(kind,…)`: 4.70/6.0(5.0)/2.0/5.25/0) moved INSIDE the per-obstacle loop (envelope rule). Add `BridgeDeckProfile.ComputeStructuralDepthMeters(span)=clamp(span/20,0.8,6.5)` used by the planner only (mesh keeps `ComputeDeckThicknessMeters`). | `Algorithms/BridgeElevationPlanner.cs`, `BridgeElevationPlan.cs`, `BeamNG.Procedural3D/RoadMesh/BridgeDeckProfile.cs` |
| **A3** | Replace the binary `<`/`>`/`=` split with §3.5: `PriorityClassStep` (reverse `GetOsmPriority` bands) → `Δp` → share `r`; `R_ideal=D·r`, `L_ideal=D·(1−r)`. Rail/Water override `L=0,R=D`. | `Algorithms/BridgeElevationPlanner.cs` |
| **A4** | Ramp feasibility (R4.5): generalize `GradeSeparationResolver.ClampRampToJunctions` into `MeasureRampLength(network,spline,station,side)` (stops at junctions, next span, way end); `R_max=rampLen·maxSlope(class)`, `L_max=min(maxCut,rampLen·maxSlope)`, **junction-in-sag ⇒ L_max=0**; `Distribute`; escalate to absolute slopes + `maxCutHard`, else reduce clearance to 4.2 m + **warn**. | `Algorithms/BridgeElevationPlanner.cs`, `Export/GradeSeparationResolver.cs` |
| **A5** | Order `EnumerateSpans` by descending owner `Priority`; accumulate already-pinned sections so later (lower-priority) bridges see fixed decks as constraints. | `Algorithms/BridgeElevationPlanner.cs` |
| **A6** | **Dip-as-pin (centerpiece):** for each Dip/Split crossing, pin the lower road's well sections (`PinnedElevation`, eased target via the existing `(1−u)²(1+2u)` weight) PRE-smooth so the dip ramps are smoothed continuously — mirror of the deck pin. Emit from `ApplyBridgeDeckPins`. Demote `GradeSeparationResolver.ApplyLowerRoadDips`/`DipLowerRoad` to **heightmap-carve + verify only** (no longer the source of the dip profile). | `Services/UnifiedRoadSmoother.cs` (~`ApplyBridgeDeckPins`), `Export/GradeSeparationResolver.cs`, `TerrainCreator.cs` |
| **A7** | Post-smooth read-only clearance check per crossing; if short, a **bounded local eased** heightmap carve only (never re-smooth/re-pin). Keep `BridgeDeckExcavator.Excavate` as the above-deck safety shave. | `Export/GradeSeparationResolver.cs`, `TerrainCreator.cs` |

**Reuse:** `UnifiedCrossSection.PinnedElevation` + `OptimizedElevationSmoother` pin honoring (no change); affine/slope-clamp pin exemption; `ClampRampToJunctions`/`LateralFalloff`/eased-well weight; `BridgeSpanFootprint`; `EffectiveStructureAt`; station-local clearance pattern (`Obstacle.NaturalDeckZ` idea, cherry-pick from parked branch).

---

## Phase B — R8 terrain stamping

Runs in `TerrainCreator` immediately after `BridgeDeckExcavator.Excavate` (before DecalRoad gen) — entirely heightmap/mesh, so it cannot break smoother continuity.

- **B1 Embankments:** cherry-pick `BridgeAbutmentFiller.cs` from `feature/bridges` (raise-only fillet, lateral feather, groups by `StructureSpanId`), adapt batter to constant `cutSideSlope = 1:1.5` (taper length = `fillHeight·cutSideSlope`).
- **B2 Cuts:** replace the dip carve's smoothstep lateral falloff with a constant-slope side wall (1:1.5), width = carriageway + shoulder. New `BridgeRoadCutStamper` or extend `DipLowerRoad`'s carve.
- **B3 Abutment placement (your "grow/shrink the bridge"):** compute an effective bridge sub-range where deck underside ≥ 1.5 m over (graded) terrain; below that → embankment, not bridge. v1 **shrinks only** (defer growth). The deck mesh/excavator/abutment key off this effective range.
- **B4 Level-area blend:** localized one-directional smoothing of the abutment collar so road/embankment/deck meet without a ridge.

**Reuse:** `BridgeAbutmentFiller` (parked); `UnifiedCrossSection.OriginalTerrainElevation` (natural ground Z); `EffectiveRoadWidth`/`NormalDirection`/`CenterPoint`; `BridgeDeckExcavator` one-directional convention.

---

## Later / placeholders (separate planning passes)
Bespoke bridge DecalRoad rules + AI waypoint system (replacing AI decals on bridge-over-bridge) · R6 multi-level (`layer`, bottom-up) · R9 adjacent-bridge merge (shared high-point) · navigable-water refinement · railroad-as-placeable-road · **roundabout ring bridges (elevated roundabout interchanges)** — ring ways bypass `PathWithMetadata` seeding entirely (`CreateRoundaboutRingSpline`, `RoundaboutMerger.cs:~347`, copies only lane info: no `IsBridge`/`Layer`/`OsmTags`/`StructureSegments`), so an elevated ring gets no terrain exclusion, no deck, and grade-sep sees `Layer=0` (pre-existing gap, independent of 0.3a). Needs: seed a partial-ring `StructureSegment` from the `bridge=yes` member ways (re-resolve from `OsmQueryResult.Features` via `OsmRoundabout.WayIds`, match coordinates into the merged ring), propagate structure metadata onto the ring spline, and reconcile with flat-ring harmonization (a fully elevated ring suits the flat default held at deck Z; a PARTIALLY elevated ring conflicts with it) — own pass after Phase A.

---

## Config plumbing
New params live in `BridgeRuleSystemOptions` (0.2). Per project convention (docs 14/24): land **engine + xUnit tests first with hard-coded defaults**, then do the mechanical 8-site UI/preset wiring (mirror `MinBridgeClearanceMeters`: `TerrainCreationParameters` → `TerrainGenerationState`+`Reset` → `TerrainPresetResult` → `TerrainPresetExporter.razor` → `TerrainPresetImporter.razor` → `TerrainGenerationOrchestrator` → `GenerateTerrain.razor.cs` → `GenerateTerrain.razor`). The "Bridge/Tunnel Structure Handling" UI panel grows the feature-flag toggles.

## Critical files
- `BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs` + `BridgeElevationPlan.cs` (rule engine: typing, §3.5, R4.5, solve order, dip-pin emission)
- `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (`ApplyBridgeDeckPins`: deck + dip pins)
- `BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs` (demote to carve+verify; `MeasureRampLength`; constant-slope cut)
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs` (obstacle typing; bridge-bridge placeholder)
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/GradeSeparatedCrossing.cs`, `StructureSegment.cs` (kinds; original geometry)
- `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs` (new central config)
- `BeamNgTerrainPoc/Terrain/Osm/…/BridgeObstacleClassifier.cs` (new; OSM feature spatial query)
- `BeamNgTerrainPoc/Terrain/Export/BridgeAbutmentFiller.cs` (cherry-pick), `TerrainCreator.cs` (wiring)

## Verification
- **Unit (xUnit, ~479 today):** typing→clearance (rail 6.0/5.0, navigable water 5.25, terrain 0); §3.5 shares (Δp=+2→20/80, 0→50/50, −2→80/20; envelope = max over obstacles); R4.5 (ramp clamp, junction-in-sag⇒L=0, escalation, reduced-clearance warning); deckDepth bounds; solve-order constraint carry; **dip-as-pin survives 3 smoother iterations** with no V/kink and clearance ≥ min (mirror `BridgeDeckPinTests`); flag-off byte-identical; obstacle classifier on a rail/water/road/terrain crossing.
- **End-to-end render:** regenerate `_generated_terrain` (4096²). Checks: lateral drift gone (bridge sits on its OSM way); deck continuous with approaches; rail/water crossings get correct clearance; lower roads dip (not always-raise) per priority; embankments graded at 1:1.5 (no cliff); cuts have side-slopes; abutment buried in graded fill. Grep `[BRIDGE-PLAN]`/`[BRIDGE-PROFILE]`/`[GRADE-SEP]`/`[BRIDGE-ABUTMENT-FILL]` for per-crossing R/L/clearance + warnings. Compare bridge `762726404` (the screenshot) before/after.

## Risks
- **Smoother stability with deck-pin + dip-pin families** at a crossing (different splines) — A6 is the riskiest step; land it with the 3-iteration survival test before Phase B.
- **Pre-smooth decision accuracy** (parked §5a lesson: pre-smooth DEM ≈ embankment banks): keep the decision driven by clearance requirement + station-local obstacle Z, with A7 as the post-smooth backstop.
- **Lateral re-projection** must not move the deck off the continuous corridor — validate with a render before trusting it.
- **Rail/water detection** depends on OSM coverage + classifier accuracy — flag low-confidence obstacles in logs.
- Scope is multi-session: **Phase 0 + Phase A are the first milestone** (decision engine correct + continuous); Phase B is the visible polish.
