# No-Blend-Zones — code cleanup & Blazor UI overhaul plan

- **Date opened:** 2026-05-31
- **Branch:** `experimental/switch_off_blend_zones`
- **Status:** IN PROGRESS — T1, T2 committed; **T3 (a/b/c) + §3.4 committed**; **T4 committed
  (`86814fd`)**; **T5 committed (`3a05e7b`, polish `d879cf5`, degrees tilt `2736b63`)**;
  **T6 committed (`10a6cfe`)** on `experimental/noblendzones_code_cleanup` (322 tests green, was 402).
  Only **T7 remains**. **Next: a user render checkpoint** (franco_same_prio + _generated_terrain),
  then T7 (remove TEMP `[NO-BLEND]` diagnostics, LAST).
  T4 is behaviorally inert on the live no-blend path (the IDW modifier was already neutralized by the
  `UnifiedRoadSmoother` per-iteration reset to 1.0f, and Phase-4 never read it). T5 is behaviorally
  inert until a user moves a new control off its spine default (the 4 new params default to the spine
  values: 3 / 6 / off / 0.06).
- **Scope:** Retire the obsolete junction/blend machinery superseded by the no-blend (affine `ThroughRoad`)
  path, and overhaul the parameters exposed in the Blazor terrain-material UI / preset DTO.
- **Source of truth for the feature itself:** `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md`
  (§1–§7) and siblings (`…-investigation-and-plan.md`, `…-roundabout-no-blend-handoff.md`,
  `…-connector-grade-ramp-handoff.md`, `2026-05-31-section2-absolute-depth-design.md`).
- **Spine class:** `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` (42 members).
- **Tooling:** ChunkHound (semantic + regex code search) is installed and indexed for this repo
  (voyage-code-3 embeddings; MCP server `ChunkHound` registered). Use it + ripgrep for the trace work.

---

## 0. The live pipeline this cleanup targets

The "no blend zones" road-elevation path is gated by **`affineThroughActive`** =
`blendOff && EnableAffineJunctionLeveling`, where `blendOff` = `!EnableParabolicJunctionBlend && !EnableHermiteJunctionBlend`.
On this branch the default config is: both blend flags **false**, `EnableAffineJunctionLeveling` **true** (TEMP
hardcode), `AffineJunctionTargetMode` **ThroughRoad** (TEMP hardcode at
`BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs:1148-1149`).

Post-loop passes in `UnifiedRoadSmoother.SmoothAllRoads`, all inside `if (affineThroughActive)`:
1. **Affine ThroughRoad targeting** — `BuildEndpointTargetLookup` + `ThroughRoadJunctionElevation.Compute`;
   terminating roads tilt (linear) to the through road's Z, through road never dragged.
2. **§3 `RetargetTerminatingRoadsToSettledThrough`** — iterated; closes junction-fill bump + seam step (`9a0c4da`).
3. **§4 `MatchTerminatingBankingToThroughSurface`** — banking-only warp over a runoff zone (`7835ef3`).
4. **Connector grade ramp `EaseConnectorGradeToThroughSurface`** — local end-weld parabola (not yet visually
   validated).

The Hermite/parabolic blend, AASHTO K-cap, short-connector compositional blend, B.3 C1, Phase-C stretch,
multi-way dominant-road detection, and the legacy `FinalSnapTJunctionEndpoints` are all **off / skipped** on
this path. **Roundabouts** still run the legacy `RoundaboutElevationHarmonizer` (a back-door blend zone) —
slated for the same no-blend treatment per the roundabout handoff (separate work item, not this cleanup).

---

## 1. Locked decisions (user, 2026-05-31)

| # | Decision | Consequence |
|---|----------|-------------|
| D1 | `RoundaboutMaxPlaneTilt` → **expose in UI** | New UI control + preset key, paired with `EnableTiltedRoundaboutPlane`. |
| D2 | `AffineJunctionTargetMode` → **collapse to `ThroughRoad`, hardcoded, NOT in UI** | Remove the `Consensus`/`RawTerrain` branches in `BuildEndpointTargetLookup`; drop the TEMP hardcode in favor of a permanent value. (User initially said "Consensus"; code + docs proved every validated render ran `ThroughRoad` and that Consensus *ramps* — corrected.) |
| D3 | `EnableAffineJunctionLeveling` → **fold in / remove the flag** | Make affine leveling the implicit blend-off junction mechanism (dispatch: parabolic → hermite → **else affine**). `blendOff + affine-off` was a broken state, not a feature. |

---

## 2. Master parameter classification

Verdicts from a 4-way code trace (read sites verified with `file:line`; "no-blend path" = the live config in §0).
Action legend: **REMOVE** (delete member + its code) · **REMOVE-W/-BLEND** (delete when the blend code paths are
deleted) · **KEEP-HARD** (keep, hardcoded, not UI) · **NEW-UI** (add UI + preset) · **KEEP-UI** (already exposed,
retain) · **FOLD** (collapse/inline) · **DECIDE** (needs a user call in §3).

### 2a. Obsolete — REMOVE outright (dead, user-marked obsolete, no UI, no preset)

| Param (line) | Default | Evidence | Action |
|---|---|---|---|
| `EnableHermiteGradeSkip` (45) | false | only read in blend path; user-marked obsolete | REMOVE |
| `GradeSkipThresholdPercent` (51) | 0.5 | feeds the above only | REMOVE |
| `EnableMaxGradeClamp` (61) | false | grade clamp — user rejects clamps (`feedback_no_grade_clamp`) | REMOVE |
| `EnableBlendZoneEndC1` (242) | false | B.3 cubic — rejected (`feedback_b3_cubic_rejected`) | REMOVE |
| `EnableBlendDistanceStretchToMatchSlope` (257) | **true** | Phase-C stretch; obsolete on affine path (NB live file default is `true`, not `false`) | REMOVE |
| `EnablePhaseBDiagnostics` (276) | false | diagnostic CSVs for the rejected B-phase | REMOVE |

### 2b. Blend machinery — REMOVE when committing to "blend permanently off"

These are dead on the no-blend path because `ApplyUnifiedProfiles` never enters a blend branch when both blend
flags are false. Removing them = deleting `BlendSplineProfile`, `BlendSplineProfileParabolic`,
`BlendShortConnectorCompositional`, the AASHTO K-cap, and the dominant-road constraint code.

| Param (line) | Default | Verdict | Action |
|---|---|---|---|
| `EnableParabolicJunctionBlend` (79) | false | dispatch gate; off | REMOVE-W/-BLEND (D3 makes dispatch parabolic→hermite→else-affine; if blend fully retired, drop both gates) |
| `EnableHermiteJunctionBlend` (142) | false | dispatch gate; off | REMOVE-W/-BLEND |
| `EnableParabolicBankBlend` (154) | true | read only inside `if (EnableParabolicJunctionBlend)` (`UnifiedJunctionProfileBlender.cs:171,239`) → **DEAD** on no-blend | REMOVE-W/-BLEND |
| `EnableShortConnectorBlend` (229) | true | read only inside parabolic branch (`:96,168,236`) → **DEAD** | REMOVE-W/-BLEND |
| `EnableAashtoBlendDistanceCap` (205) | true | computes a blend distance never consumed when blend off (`:2810`) → **DEAD** | REMOVE-W/-BLEND |
| `DesignSpeedKmh` (291) | null | only feeds the AASHTO cap; has UI (`razor:952`) + preset-apply but **not** exported | REMOVE-W/-BLEND (also remove its UI control + preset-apply line) |

### 2c. Live on the no-blend path — KEEP hardcoded (not UI)

| Param (line) | Default | Evidence | Action |
|---|---|---|---|
| `EnablePhase19JunctionPinning` (36) | true | pins `HarmonizedElevation`, consumed by `RoadMaskBuilder` center-fill | KEEP-HARD |
| `EnableSurfacePriorityOverride` (180) | true | **LIVE** — `RoadMaskBuilder.cs:155,178,201` (Phase-4 rasterization, runs always) | KEEP-HARD |
| `EnableSurfaceWidthProtection` (193) | true | **LIVE** — `RoadMaskBuilder.cs:139` two-pass rasterizer (Phase-4) | KEEP-HARD |
| `EnableEndpointTerrainSlopeMatch` (218) | true | **LIVE** — skips legacy `ApplyEndpointTapering` (`:342`) so it doesn't fight affine; slope sample at `:1097` | KEEP-HARD (verify no fight at dead-end+junction splines, followup §6) |
| `EnablePropagationOverlapTaper` (166) | true | **PARTIAL** — Phase-5b mid-spline propagation (`:94,299-309`) may still run; dead for primary blend zones | KEEP-HARD (verify Phase-5b actually fires on no-blend path before removing) |

### 2d. The no-blend path itself — FOLD / hardcode

| Param (line) | Default | Action |
|---|---|---|
| `EnableAffineJunctionLeveling` (92) | true | **FOLD** (D3) — remove flag; affine implicit when blendOff |
| `AffineJunctionTargetMode` (103) | Consensus | **FOLD** (D2) — hardcode `ThroughRoad`; delete `Consensus`/`RawTerrain` branches in `BuildEndpointTargetLookup`; remove the TEMP hardcode comment |

### 2e. No-blend tunables — NEW UI (D1 + user "new for UI")

| Param (line) | Default | Live? | Action |
|---|---|---|---|
| `BankingRunoffSurfaceWidthMultiplier` (114) | 3 | LIVE — §4 banking runoff zone | NEW-UI + preset |
| `ConnectorGradeRampLengthMeters` (127) | 6 | LIVE — §connector end-weld | NEW-UI + preset |
| `EnableTiltedRoundaboutPlane` (434) | false | LIVE — roundabout ring tilt opt-in | NEW-UI + preset |
| `RoundaboutMaxPlaneTilt` (442) | 0.06 | LIVE — used when tilt on | NEW-UI + preset (D1) |

### 2f. Already exposed & still valid — KEEP UI

`EnableJunctionHarmonization` (20), `JunctionDetectionRadiusMeters` (308), `EnableRoundaboutDetection` (371),
`EnableRoundaboutRoadTrimming` (385), `RoundaboutConnectionRadiusMeters` (396),
`RoundaboutOverlapToleranceMeters` (408), `ForceUniformRoundaboutElevation` (422). Keep their controls.
(Debug toggles `ExportJunctionDebugImage` (509) / `ExportRoundaboutDebugImage` (522) are hardcoded `true` in the
initializer — leave as-is, or optionally expose as advanced toggles. Minor.)

### 2g. DECIDE — live-but-legacy or dead-but-exposed (see §3)

| Param (line) | Default | Finding | Why it needs a call |
|---|---|---|---|
| `EnableJunctionIdwFiltering` (475) | true | **DECLARED-BUT-DEAD** — neutralized by `UnifiedRoadSmoother.cs:403` reset; Phase-4 never reads `JunctionIdwWeightModifier` | Has UI (`razor:980`) + preset. Remove the whole feature, or repair the wiring? |
| `MinTerminatingIdwWeight` (490) | 0.1 | DEAD (same reset) | "" |
| `IdwFilterTaperDistanceMeters` (498) | null | DEAD (same reset) | "" |
| `EnableMultiWayDominantRoadDetection` (348) | true | **DEAD on affine path** — only runs in blend-on branch; `ThroughRoadJunctionElevation.Compute` supersedes it for Y/X/Complex | User believed important. Remove (commit to no-blend) or keep (if blend may return)? |
| `DominantRoadWidthRatio` (359) | 1.5 | DEAD on affine path (feeds the above) | "" |
| `JunctionBlendDistanceMeters` (328) | 50 | live only via (a) IDW taper [dead per 403] and (b) legacy roundabout connector blend (`RoundaboutElevationHarmonizer.cs:509`) | Becomes fully obsolete once IDW removed + roundabout goes no-blend. Keep as interim roundabout knob, or remove now? Has UI + preset. |
| `BlendFunctionType` (334) | CubicHermiteC1 | **NOT fully obsolete** — used in Phase-4 terrain blend (`SinglePassBlender.cs:62,93`, `DistanceFieldTerrainBlender.cs:90`) **and** roundabout connector curve (`RoundaboutElevationHarmonizer.cs:534`) | It controls the *terrain blend* filter, not junction elevation. Keep (maybe rename/relocate out of the junction class) — confirm intent. Has UI + preset. |
| `RoundaboutBlendDistanceMeters` (455) + `EffectiveRoundaboutBlendDistanceMeters` (461) | 50 | LIVE for legacy roundabout connector blend | Obsolete once roundabout no-blend lands. Remove now or after that work? Has UI + preset. |

---

## 3. Open decisions for the user — RESOLVED (user, 2026-05-31)

1. **IDW filtering trio (`EnableJunctionIdwFiltering` / `MinTerminatingIdwWeight` / `IdwFilterTaperDistanceMeters`).**
   The feature is dead (reset at `UnifiedRoadSmoother.cs:403`; Phase-4 ignores the modifier).
   **DECISION: REMOVE entirely** — params + UI controls + preset keys + `ComputeJunctionIdwWeightModifiers`
   + the reset line + the `JunctionIdwWeightModifier` field (T4).
2. **Multi-way dominant-road detection (`EnableMultiWayDominantRoadDetection` / `DominantRoadWidthRatio`).**
   Dead on the affine path (superseded by `ThroughRoadJunctionElevation.Compute`).
   **DECISION: REMOVE with the blend code** (T3). Commits to no-blend.
3. **`BlendFunctionType`.** It is *not* dead — it selects the **terrain-blend** filter (Phase-4) and the legacy
   roundabout curve, not junction elevation.
   **DECISION: KEEP + relabel** as a terrain-blend setting (rename/relocate so it no longer reads as a
   junction-blend knob). Keep its UI + preset.
4. **`JunctionBlendDistanceMeters` + `RoundaboutBlendDistanceMeters`.**
   **DECISION (user, corrected): the roundabout no-blend work is ALREADY committed on this branch** (see commits
   `2a1b4ea` flat ring default, `6c33fe0` pivot ring plane, `8487387`/`3026188` connector grade weld). So the
   premise that these stay as "interim roundabout knobs until roundabout no-blend lands" is stale.
   **RECHECK the live roundabout elevation path and REMOVE the obsolete parts** — if `RoundaboutElevationHarmonizer`'s
   legacy connector blend is no longer the live mechanism, drop `JunctionBlendDistanceMeters`,
   `RoundaboutBlendDistanceMeters`, `EffectiveRoundaboutBlendDistanceMeters`, and `GetEffectiveBlendDistance` /
   `GetEffectiveRoundaboutBlendDistance` along with it. Verify before deleting.

---

## 4. Cleanup task groups (implementation order; TDD; do NOT bundle)

> Each group is its own commit with tests green. Build env note: DLL-lock errors (MSB3027/3021) are from the
> running app, not compile errors — check `error CS`.

- **T1 — Remove §2a obsolete params.** **RE-SCOPED (2026-05-31):** only the 4 *standalone* rejected features —
  `EnableHermiteGradeSkip` + `GradeSkipThresholdPercent` (grade-skip block in `UnifiedRoadSmoother.BuildEndpointAnchorLookup`
  + `JunctionElevationPinner.ShouldSkipHermiteRamp`), `EnableMaxGradeClamp` (threaded into
  `ApplyEndpointAnchoring`→`OptimizedElevationSmoother`), and `EnablePhaseBDiagnostics` (the `PhaseBDiagnostics`
  CSV emitter). The other two §2a params — `EnableBlendZoneEndC1` (B.3) and `EnableBlendDistanceStretchToMatchSlope`
  (Phase-C) — are **parameters of `BlendSplineProfileParabolic`** and the blend dispatch; gutting them out of a
  method T3 deletes wholesale is wasted/risky surgery, so they **move to T3** (they die with the parabolic method,
  `CubicJunctionProfile`, and `BuildMidSplineCrossingDistances`). No UI/preset impact. Tests: delete
  `PhaseBBlendZoneEndC1Tests`/`PhaseCStretchLBlendTests`/`CubicJunctionProfileTests` in T3, grade-skip/clamp/diag
  tests in T1.
- **T2 — Fold the affine flag + collapse the target mode (D2, D3).** Remove `EnableAffineJunctionLeveling`;
  make affine implicit on the blend-off dispatch. Hardcode `ThroughRoad`; delete the `Consensus`/`RawTerrain`
  branches in `BuildEndpointTargetLookup` and the TEMP hardcode at `razor.cs:1148-1149`. Keep the enum type
  only if still referenced; otherwise remove it. Tests: `ThroughRoadJunctionElevationTests`,
  `RetargetTerminatingToSettledThroughTests` must stay green.
- **T3 — Retire the blend machinery (§2b) + B.3/Phase-C + dominant-road [decisions confirmed].**
  **PRECISE INVENTORY (traced 2026-05-31, post-T2):**
  - **Spine params to delete (10):** `EnableParabolicJunctionBlend`, `EnableHermiteJunctionBlend`,
    `EnableParabolicBankBlend`, `EnableShortConnectorBlend`, `EnableAashtoBlendDistanceCap`,
    `EnableBlendZoneEndC1` (B.3), `EnableBlendDistanceStretchToMatchSlope` (Phase-C),
    `EnableMultiWayDominantRoadDetection`, `DominantRoadWidthRatio`, `DesignSpeedKmh`. (After this the dispatch is
    unconditional: blend-off is the only path. `IsAffineThroughActive` already simplified in T2 — but note the
    blender's `ApplyUnifiedProfiles` still reads `EnableParabolicJunctionBlend`/`EnableHermiteJunctionBlend` to
    dispatch; with both gone, delete the Step-2/Step-3 dispatch blocks entirely.)
  - **Production classes to DELETE outright:** `CubicJunctionProfile.cs` (B.3), `ParabolicJunctionProfile.cs`
    (only used by the parabolic blend method), `AashtoKValueTable.cs` (K-cap), `BlendDistanceStretcher.cs`
    (Phase-C; only used at blender `:1234,:1277` inside `BlendSplineProfileParabolic`).
  - **Methods to delete inside `UnifiedJunctionProfileBlender.cs`:** `BlendSplineProfile`,
    `BlendSplineProfileParabolic`, `BlendShortConnectorCompositional`, `DetectDominantRoad`,
    `ComputeMultiTJunctionConstraints`, `BuildMidSplineCrossingDistances` (+ `_midSplineCrossingDistancesBySpline`
    field + its build at `:102-108` + clear at `:322`). Remove the dominant-road deferral block (`:126-146`),
    the pass-2 multi-T recompute (`:193-207`), and both dispatch blocks (`:161-175`, `:229-243`). Trim the
    `SplineClaimedZones.Build` trigger at `:94-100` to **only** `buildForA5` (drop `buildForB3OrB2`).
  - **MUST KEEP (LIVE — interleaved with the dead code):** `SplineClaimedZones` (used by the A.5 propagation
    taper, `EnablePropagationOverlapTaper`), `OverlapTaper` (used by `SplineClaimedZones.cs:107,117` — LIVE, NOT
    just the deleted short-connector), the A.5 Step-5b propagation, Step-4 edge derivation, Step-5 mid-spline
    crossings, Step-6 endpoint tapering, Step-7 IDW (that's T4), and `EnableSurfacePriorityOverride` /
    `EnableSurfaceWidthProtection` / `EnableEndpointTerrainSlopeMatch` / `EnablePhase19JunctionPinning`.
  - **`DesignSpeedKmh` extra sites:** UI control + preset-apply (`razor.cs:1045-1046,1159`, control near `:823-825`)
    and the preset DTO (`TerrainPresetResult.cs:365`).
  - **Test files to DELETE:** `AashtoKValueTableTests`, `BlendSplineProfileParabolicTests`,
    `CubicJunctionProfileTests`, `ParabolicJunctionProfileTests`, `PhaseBBlendZoneEndC1Tests`,
    `PhaseBKValueCapTests`, `PhaseBShortConnectorTests`, `PhaseCStretchLBlendTests`, `PhaseDBankBlendTests`,
    `BlendDistanceStretcherTests`.
  - **Test files to KEEP:** `SplineClaimedZonesTests`, `SplineClaimedZonesNestedGuardTests`,
    `PropagationOverlapTaperTests`, `OverlapTaperTests`, `SurfacePriorityOverrideTests`,
    `ThroughRoadJunctionElevationTests`, `RetargetTerminatingToSettledThroughTests`,
    `BankingMatchToThroughSurfaceTests`, `ConnectorGradeRampTests`, `TiltedRoundaboutGateTests`,
    `AffineJunctionLevelerTests`, `PhaseBEndpointTerrainSlopeTests` (tests B.4 = live `EnableEndpointTerrainSlopeMatch`),
    `ContestedPixelResolverTests`, `HeightmapSlopeSamplerTests`, `JunctionElevationPinnerTests`,
    `JunctionPinningValidationExporterTests`, `TopologyJunctionDetectionTests`.
  - **Suggested sub-commits (safe to split — still one *task group*):** (a) dominant-road removal; (b) K-cap +
    `DesignSpeedKmh` (incl. its UI/preset/DTO); (c) the parabolic/hermite/short-connector blend methods + B.3 +
    Phase-C + dispatch. Re-verify the no-blend renders after.
  - **DONE (2026-05-31, commits `c223215` a, `a6c85c4` b, `98fbe76` c, `533d91f` §3.4):** All of the above plus
    several consequences the inventory hadn't fully traced:
    - Removing `EnableParabolicJunctionBlend`/`EnableHermiteJunctionBlend` forced the affine path to become
      **unconditional** in `UnifiedRoadSmoother` (D3): deleted `IsAffineThroughActive`; the §3/§4/ramp block and
      the Phase-2 reconciliation pass now run affine leveling for every spline; `ShouldUseTiltedRoundaboutPlane`
      keys only off `EnableTiltedRoundaboutPlane`.
    - `FinalSnapTJunctionEndpoints` (blender) + the Phase-3 endpoint-anchoring warm-start
      (`BuildEndpointAnchorLookup` + `ApplyEndpointAnchoring` call) were dead on blend-off (always skipped) →
      deleted. `OptimizedElevationSmoother.ApplyEndpointAnchoring` + the `EndpointAnchor` type are now unused but
      left in place (out of scope).
    - `EnablePhaseBDiagnostics` was a dangling flag (T1 removed its emitter but left the field) → removed here.
    - **§3.4 = FULL removal** (user-confirmed): the legacy roundabout connector blend was verified dead in
      production (sole caller passed `skipConnectingRoadBlending: true`). Deleted
      `RoundaboutElevationHarmonizer.BlendConnectingRoads` (+ its only-callers `CalculateDistancesFromEndpoint`,
      `ApplyBlendFunction`) and the `skipConnectingRoadBlending` param; removed `RoundaboutBlendDistanceMeters` /
      `EffectiveRoundaboutBlendDistanceMeters` / `GetEffectiveRoundaboutBlendDistance` + their UI/preset/DTO;
      `ComputeRoundaboutConstraints` now uses `GetEffectiveBlendDistance`. **`JunctionBlendDistanceMeters` +
      `GetEffectiveBlendDistance` KEPT** — still live (propagation Step 5b, mid-spline crossings, MaintainBanking
      extent). This overrides §5's stale "keep RoundaboutBlendDistanceMeters for now" note.
    - Test files deleted: AashtoKValueTable, PhaseBKValueCap, BlendSplineProfileParabolic, CubicJunctionProfile,
      ParabolicJunctionProfile, PhaseBShortConnector, PhaseBBlendZoneEndC1, PhaseCStretchLBlend, PhaseDBankBlend,
      BlendDistanceStretcher. `TiltedRoundaboutGateTests` rewritten to the flag-only contract.
- **T4 — IDW decision (§3.1). DONE (2026-05-31, commit `86814fd`; 322 tests green).** Removed the whole
  feature per the §3.1 REMOVE decision: the 3 params (`EnableJunctionIdwFiltering` /
  `MinTerminatingIdwWeight` / `IdwFilterTaperDistanceMeters`) + their two `Validate()` checks;
  `ComputeJunctionIdwWeightModifiers` + its Step-7 call + the `IdwModifiersSet` result property + the
  ring-CS clone copy; the `UnifiedCrossSection.JunctionIdwWeightModifier` field + doc; the
  `UnifiedRoadSmoother` per-iteration reset to 1.0f; the UI MudPaper section + all code-behind/preset
  sites (props, preset-apply, build-params, export keys, import-apply, the `TerrainPresetResult` DTO,
  `TerrainPresetExporter`, both `TerrainPresetImporter` blocks). **Kept** `CalculateDistancesFromEndpoint`
  (still used by blender Steps 1 and 5). No IDW test file existed, so the suite count held at 322.
  Behaviorally inert on the live path (the modifier was already neutralized by the reset). **Next:** hand
  back to the user for a `franco_same_prio` + `_generated_terrain` render before T5 — do not start T5/T6/T7
  without that checkpoint.
- **T5 — UI overhaul. DONE (2026-05-31, commit `3a05e7b`; 322 tests green).** Added the 4 NEW-UI
  params (`BankingRunoffSurfaceWidthMultiplier` slider, `ConnectorGradeRampLengthMeters` numeric,
  `EnableTiltedRoundaboutPlane` switch, `RoundaboutMaxPlaneTilt` slider shown only when tilt on) to
  `TerrainMaterialItemExtended` and wired them through `BuildRoadSmoothingParameters`. Regrouped the
  Junctions tab into **Junction Detection** / **Side-Road Transitions** / **Roundabouts** sections.
  Relabeled the terrain `BlendFunctionType` control → "Terrain Blend Function" (kept, §3.3).
  Relabeled `JunctionBlendDistanceMeters` control → "Propagation Distance" (still live for constraint
  propagation / mid-spline crossings / banking runoff per §3.4 — not a blend-curve length anymore).
  Fixed the now-stale "not for UI" doc comments on the 4 spine params. **NOT YET wired into preset
  save/load** — `ApplyPreset`, the JSON export/import, `RoadSmoothingPresets` initializers, and the
  `TerrainPresetResult` DTO are all T6. A new material gets correct defaults; selecting a preset will
  not yet update the 4 new params. **Next = T6.**
- **T6 — Preset DTO overhaul + round-trip fixes. DONE (2026-05-31, commit `10a6cfe`; 322 green).**
  Wired the 4 new params + the apply-only roundabout params through all four serialize/deserialize
  surfaces: (A) per-material JSON export/import (`TerrainMaterialSettings.razor.cs`), (B) `ApplyPreset`
  (spine→material; tilt gradient→degrees via `atan`), (C) the full terrain preset DTO
  (`JunctionHarmonizationSettings` in `TerrainPresetResult.cs` + `TerrainPresetExporter.razor` +
  both `TerrainPresetImporter.razor` blocks). Tilt persists in **degrees** everywhere UI-facing; the
  deg↔gradient conversion is confined to the build boundary (`tan`) and `ApplyPreset` (`atan`).
  Aligned the spine `RoundaboutMaxPlaneTilt` default `0.06`→`0.1051` (tan 6°) so fresh-material and
  apply-preset agree at 6°. `RoadSmoothingPresets.cs` initializers intentionally keep relying on the
  now-consistent class defaults for the roundabout/no-blend params (no per-preset override needed).
- **T7 — Gate the `[NO-BLEND]` diagnostics behind a flag (NOT delete). DONE (2026-05-31; 322 green).**
  User call (2026-05-31): the no-blend work is still being validated (§2 absolute-depth, connector ramp,
  roundabout tilt), so the DIAG/OWN/PROFILE/RAB/RAMP dumps are worth keeping rather than ripping out.
  Added `private const bool EmitNoBlendDiagnostics = false;` to `UnifiedRoadSmoother` and wrapped the five
  sites: the DIAG/OWN+PROFILE block (~`:485`), the RAB block (~`:677`), the per-connector RAMP log (~`:1671`),
  and the `_preCorrectionElevations` populate in the Phase-2 reconciliation (~`:1246`, the PROFILE dump's only
  consumer). Flip the const + rebuild to re-enable; off, the JIT dead-strips the blocks (no runtime cost). The
  cheap `[NO-BLEND] §3/§4/ramp` one-line **result** summaries are left unconditional (they're operation logs,
  not diagnostics). Comments retitled TEMP→flag-gated. **Behaviorally inert** — diagnostics-off changes no
  terrain output and deletes no live code, so it lands safely regardless of the T3–T6 render gate.
  **Out of scope (left as-is):** the orphaned `OptimizedElevationSmoother.ApplyEndpointAnchoring` + `EndpointAnchor`
  type and `ApplyEdgeConstraints` slop are *dead-code deletions*, not diagnostics — a separate sweep if wanted.

---

## 5. Blazor UI overhaul (`TerrainMaterialSettings.razor` + `.razor.cs`)

**Current state:** 12 of 42 params have controls; the affine mode is a TEMP hardcode; IDW + several roundabout
params are exposed but dead/legacy.

**Add (NEW-UI):**
- `BankingRunoffSurfaceWidthMultiplier` (slider, ~1–6, def 3) — "Banking runoff width (× surface width)"
- `ConnectorGradeRampLengthMeters` (numeric, 0–25 m, def 6; 0 = off) — "Connector grade ramp length"
- `EnableTiltedRoundaboutPlane` (switch, def off) — "Tilt roundabout ring to terrain"
- `RoundaboutMaxPlaneTilt` (slider/numeric, 0–0.06, def 0.06) — "Max roundabout tilt (rise/run)" — show only
  when tilt enabled (D1)

**Remove from UI:**
- `DesignSpeedKmh` control (`razor:952`) — with T3
- IDW trio controls (`razor:980,990,1005`) — with T4 (if remove)
- `BlendFunctionType` control — **keep** but relabel as a terrain-blend setting (§3.3), or move to the terrain
  section
- `JunctionBlendDistanceMeters` / `RoundaboutBlendDistanceMeters` — keep for now, label "legacy roundabout"
  (§3.4)

**Group the surviving controls** into clear sections: *Junction detection* (`JunctionDetectionRadiusMeters`),
*No-blend tuning* (the 2 new multipliers), *Roundabouts* (detection/trim/radius/overlap/uniform + the 2 new
tilt controls + legacy blend distance). Drop the experimental phase flags entirely (they stay code-only
defaults).

---

## 6. Preset DTO overhaul (`junctionHarmonization` section)

**Persisted today (8):** `EnableJunctionHarmonization`, `JunctionDetectionRadiusMeters`,
`JunctionBlendDistanceMeters`, `BlendFunctionType`, `EnableJunctionIdwFiltering`, `MinTerminatingIdwWeight`,
`IdwFilterTaperDistanceMeters` (+ apply-only roundabout params).

**Round-trip gaps found (fix or intentionally drop):**
- Roundabout params (`EnableRoundaboutDetection`, `…RoadTrimming`, `…ConnectionRadiusMeters`,
  `…OverlapToleranceMeters`, `ForceUniformRoundaboutElevation`, `RoundaboutBlendDistanceMeters`) are **applied
  from** presets but **not exported** → preset save/load silently loses them. Add to export+import.
- `DesignSpeedKmh` apply-only, not exported (moot if removed in T3).

**Add to preset:** the 4 NEW-UI params (`BankingRunoffSurfaceWidthMultiplier`, `ConnectorGradeRampLengthMeters`,
`EnableTiltedRoundaboutPlane`, `RoundaboutMaxPlaneTilt`).
**Remove from preset:** IDW trio (T4), `DesignSpeedKmh` (T3).
**Also update** the preset initializers in `RoadSmoothingPresets.cs` (they currently only set 4 params, so the
rest revert to class defaults on preset apply).

---

## 7. Risk / test impact

- ~430 terrain tests today. T3 (blend retirement) deletes the most test code — expect to remove
  `PhaseBKValueCapTests`, `PhaseDBankBlendTests`, blend-specific parts of others; the no-blend tests
  (`ThroughRoadJunctionElevationTests`, `RetargetTerminatingToSettledThroughTests`,
  `BankingMatchToThroughSurfaceTests`, `ConnectorGradeRampTests`, `TiltedRoundaboutGateTests`) must stay green.
- After each group: user runs a `franco_same_prio` + `_generated_terrain` render and confirms no regression
  (the agent cannot run the WinForms+Blazor app).
- Do **not** start §2 (absolute-depth trench/berm) here — it is a parked architectural design call
  (`2026-05-31-section2-absolute-depth-design.md`), independent of this cleanup.

## 8. Validation handles

`franco_same_prio` (J#78 node 282534762, J#126 node 282534707, twist node 282534733).
`_generated_terrain` (J#148 node 264064974). Grep `[NO-BLEND DIAG/OWN/PROFILE/RAMP]` until T7 removes them.

---

## HANDOFF PROMPT (paste to resume if context is lost) — updated 2026-05-31, T1–T6 DONE

> Resuming the **no-blend-zones code cleanup + UI overhaul** on branch
> `experimental/noblendzones_code_cleanup` (NOT the old `switch_off_blend_zones`). Read this file first
> (`ai_docs/code_cleanup_no_blend_zones/2026-05-31-cleanup-and-ui-overhaul-plan.md`), then the no-blend
> source-of-truth `ai_docs/no_blend_zones/2026-05-30-no-blend-zones-followup.md`.
>
> **State: T1–T6 are committed and the suite is 322 green** (was 402; blend test files deleted in T3). The
> live road-elevation path is now unconditional **affine `ThroughRoad`** (blend machinery, IDW, dominant-road,
> AASHTO K-cap, DesignSpeedKmh, the affine flag/target-mode all removed). The terrain-material Blazor UI + the
> preset round-trip are overhauled. Commits: T1/T2 early; T3 a/b/c `c223215`/`a6c85c4`/`98fbe76` + §3.4
> `533d91f`; T4 `86814fd`; T5 `3a05e7b` (UI), `d879cf5` (polish), `2736b63` (degrees tilt); T6 `10a6cfe`.
>
> **GATE — do this before any code:** the user must run a `franco_same_prio` + `_generated_terrain` render and
> confirm no regression from T3–T6 (UI controls + presets behave, no elevation change). The agent CANNOT run the
> WinForms+Blazor app. **Do not start T7 until the user signs off on that render.**
>
> **The only task left is T7 (see §4 / §7) — it stays LAST:** remove the TEMP `[NO-BLEND]` diagnostics in
> `UnifiedRoadSmoother.cs` (the DIAG/OWN/PROFILE/RAMP `TerrainCreationLogger` dumps, `_preCorrectionElevations`,
> `_iterationSnapshots`, the T-SNAP skip log) plus the orphaned `ApplyEdgeConstraints` slop (followup §4/§7).
> These are kept until now because they're still useful for validating the connector ramp + roundabout work, so
> only delete them once the render above is signed off. Re-grep line numbers before editing — they drift.
> Also (optional, low value): `OptimizedElevationSmoother.ApplyEndpointAnchoring` + the `EndpointAnchor` type
> were left orphaned by T3 (out of scope then) — can be swept in T7 if desired.
>
> **What T5/T6 actually shipped (so you don't redo it):**
> - 4 NEW-UI params on `TerrainMaterialItemExtended` + wired through `BuildRoadSmoothingParameters`:
>   `BankingRunoffSurfaceWidthMultiplier` (slider 1–6, def 3), `ConnectorGradeRampLengthMeters` (numeric 0–25,
>   def 6, 0=off), `EnableTiltedRoundaboutPlane` (switch, def off), `RoundaboutMaxPlaneTiltDegrees` (slider
>   0–15°, def 6°; shown only when tilt on).
> - **Tilt unit:** the UI works in **degrees**; the spine param `RoundaboutMaxPlaneTilt` is a **gradient
>   (rise/run)**. Conversion is confined to two spots: `tan` at the build boundary
>   (`BuildRoadSmoothingParameters`) and `atan` in `ApplyPreset`. Spine default aligned `0.06`→`0.1051` (=tan 6°)
>   so fresh-material and apply-preset both read 6°. Don't reintroduce a hidden conversion anywhere else.
> - Junctions tab regrouped: **Junction Detection** (`JunctionDetectionRadiusMeters` + `JunctionBlendDistanceMeters`,
>   relabeled "Propagation Distance" — still live for propagation/mid-spline/banking-runoff per §3.4) /
>   **Side-Road Transitions** (the 2 no-blend tunables) / **Roundabouts** (detection/trim/radius/overlap/uniform
>   + the 2 tilt controls). Terrain `BlendFunctionType` control relabeled "Terrain Blend Function" (kept — it's
>   the Phase-4 terrain-blend filter, not a junction knob).
> - Preset round-trip wired through **all four** serialize surfaces: (A) per-material JSON export/import in
>   `TerrainMaterialSettings.razor.cs`; (B) `ApplyPreset`; (C) the full terrain DTO —
>   `JunctionHarmonizationSettings` in `TerrainPresetResult.cs`, `TerrainPresetExporter.razor`, and both
>   `TerrainPresetImporter.razor` blocks. `RoadSmoothingPresets.cs` initializers intentionally rely on the
>   now-consistent class defaults for the roundabout/no-blend params.
>
> **Build/test (DLL-lock MSB3027/3021 = the running app, not compile errors — check `error CS`/`error RZ`):**
> `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj -p:EnableWindowsTargeting=true -clp:ErrorsOnly`
> then `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --nologo
> -clp:ErrorsOnly` (expect 322 green).
>
> **House rules:** TDD where applicable; one commit per task group, don't bundle. The `git commit -m @'...'@`
> PowerShell here-string does NOT work in the bash tool — write the message to a file and use
> `git commit -F <file>`. End commits with the `Co-Authored-By: Claude Opus 4.8 (1M context)` trailer. No grade
> clamps; affine is a linear tilt (can't kink a road). The user runs the app and shares renders/logs. Don't open
> §2 (absolute-depth trench/berm) — it's a parked design call (`2026-05-31-section2-absolute-depth-design.md`).
> Tooling: ChunkHound is installed + indexed (voyage-code-3; MCP server `ChunkHound`; `.chunkhound.json` is
> git-ignored, holds the key). Re-index after big changes with `chunkhound index` (one-shot; holds a DuckDB
> write lock; don't pipe its output to `head` — that SIGPIPEs it mid-embed).
