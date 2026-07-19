# Handoff — Bridge Road Continuity Implementation

**Date:** 2026-06-06  
**Branch:** `feature/bridges`  
**Primary doc:** `ai_docs/2026-06-03_bridge_generation/2026-06-06_bridge_road_continuity_followup.md`

## 1. Goal

Implement the **first, easier path** for top-down bridge seam continuity:

- add seam diagnostics,
- add **tangent-only** export-time bridge endpoint pose reconciliation,
- do not refactor the broader pipeline,
- do not implement virtual merged corridors yet.

This is specifically intended to fix the visible top-down kink where the road does not meet the bridge deck at the same angle.

## 2. Current production anchor

The current bridge export anchor is:

- `BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs`

Relevant existing logic already present there:

- `ShouldGenerateDeck(...)`
- `ReconcileBridgeEndpointElevations(...)`
- `FindConnectedRoadElevation(...)`
- `ApplyEndpointCorrection(...)`

Current behavior:

- bridge endpoint Z is reconciled to connected non-bridge road endpoints,
- tangent / normal / width are not reconciled yet,
- deck mesh is then built from the resulting bridge cross-sections.

This makes `BridgeDeckDaeExporter` the right first implementation point for seam continuity.

## 2.5 Review findings & corrections (2026-06-06)

> **Read this before implementing §3.** A code review against the actual mesh-build path
> found that the original Phase C ("tangent-only reconciliation") rests on a false premise.
> The corrections below supersede §3 Phase C, §6 tests, and §7 ordering where they conflict.

### Finding 1 — the deck mesh ignores `TangentDirection`

The deck geometry is produced by `RoadMeshBuilder.BuildRoadSurface`
(`BeamNG.Procedural3D/RoadMesh/RoadMeshBuilder.cs:166-204`). Every deck vertex is placed via:

- `GetLeftEdgePosition()` = `CenterPoint - NormalDirection * halfWidth` (`RoadCrossSection.cs:66`)
- `GetRightEdgePosition()` = `CenterPoint + NormalDirection * halfWidth` (`RoadCrossSection.cs:79`)

`TangentDirection` is used in **one** place — `GetTangentVector3` for end caps
(`RoadMeshBuilder.cs:380`) — and end caps are off by default (`GenerateEndCaps = false`,
`RoadMeshOptions.cs:80`), which the deck exporter never overrides. **Therefore
`TangentDirection` has zero effect on the bridge deck mesh.**

Implications:

- "Correct `TangentDirection` only" does nothing visible. The label *tangent-only* is wrong for
  this mesh.
- The only lever with a visible effect is the **recomputed `NormalDirection`**, which rotates
  each rib about its (fixed) `CenterPoint`. That can fix a **lateral rib skew** (deck edge lines
  not aligning with road edge lines) but...
- ...it **cannot change the deck's plan-view centerline heading.** In top-down view the ribbon's
  heading is the direction from `CenterPoint[i]` to `CenterPoint[i+1]`. With `CenterPoint` held
  fixed (§4 guardrail #1 / §9 non-goal #1), a centerline-heading kink is unreachable.

This is an internal contradiction in the original plan: §1 of the followup frames the artifact as
a *heading / entry-angle* problem (a centerline-direction issue), but the proposed fix can only
address *rib orientation*. They are different artifacts.

### Finding 2 — the proposed tests pass without fixing anything

The §6 tests assert "angle after correction ≤ 1.0 deg" measured on the stored `TangentDirection`.
That value converges while the mesh vertices stay unchanged → false green, directly undermining
DoD #5 ("looks materially better in screenshots"). Tests must assert on **geometry**
(`GetLeftEdgePosition`/`GetRightEdgePosition`) or on `NormalDirection`, never on the stored tangent.

### Finding 3 — contributor lookup is underspecified

`FindConnectedRoadElevation` currently **averages** all connected non-bridge elevations
(`BridgeDeckDaeExporter.cs:231-240`). A pose cannot be averaged. "Best connected contributor"
(§3 Phase B) is undefined; for a 3-way junction the choice changes the result. Pick a concrete
rule (recommended: the non-bridge contributor with the smallest XY gap to the bridge endpoint;
tie-break by highest road priority).

### Corrected approach

**Phase A is now a hard decision gate, not just step 1.** Implement diagnostics, regenerate the
problem map, and read the per-seam numbers *before* writing any correction code. Decide from the
dominant term:

- **XY gap or centerline-heading delta dominates** → the artifact is *positional*. Orientation-only
  correction will not fix it. The real fix is bounded `CenterPoint` movement at the seam (welding
  the bridge end point/first interior point onto the approach incoming direction). This is the §9
  non-goal / §10 escalation — so know it up front rather than burning a pass.
- **Normal delta dominates with small XY gap** → orientation correction is correct. Reframe the
  pass as **normal-only** (not tangent-only):
  1. Find the single connected non-bridge contributor (Finding 3 rule).
  2. Target normal = the contributor's `NormalDirection`, sign-aligned via dot product (flip if
     `Dot(bridgeNormal, roadNormal) < 0`).
  3. `smoothstep`-blend the bridge sections' `NormalDirection` from target (at the seam) to
     original (at blend-distance) over `min(20m, 20% bridge length)`; renormalize.
  4. Keep `CenterPoint`, width, and Z reconciliation unchanged.
  5. Optionally also set `TangentDirection = perp(NormalDirection)` for metadata consistency, but
     understand it has no mesh effect.

**Corrected tests** (replace §6 assertions on stored tangent):

- After correction, the deck's seam-end edge points (`GetLeftEdgePosition`/`GetRightEdgePosition`
  of the endpoint cross-section) are collinear — within tolerance — with the connected road's
  endpoint edge points. This is the assertion that actually tracks the visible artifact.
- `NoConnectedRoad_DoesNothing` and `VeryShortBridge_ClampsBlendDistance` stay, but assert on
  edge positions / `NormalDirection`, not the tangent scalar.
- `Export_ReconcilesZAndPose_WithoutRegression`: assert `CenterPoint` values are byte-unchanged.

## 3. What to implement

### Phase A — diagnostics

Add a bridge seam diagnostics layer in or near `BridgeDeckDaeExporter.cs`.

Suggested internal models:

```csharp
internal sealed class BridgeSeamDiagnostic
{
    public int BridgeSplineId { get; set; }
    public bool IsStart { get; set; }
    public int? ConnectedRoadSplineId { get; set; }
    public float AngleBeforeDegrees { get; set; }
    public float AngleAfterDegrees { get; set; }
    public float XyGapMeters { get; set; }
    public float WidthDeltaMeters { get; set; }
    public float ZGapBeforeMeters { get; set; }
    public float ZGapAfterMeters { get; set; }
    public string Action { get; set; } = "diagnose";
}
```

Also extend the export result with summary counters:

- `PoseCorrectionsApplied`
- `MaxHeadingCorrectionDegrees`
- `SeamsOverHeadingThreshold`

### Phase B — connected contributor lookup

Refactor current endpoint lookup so it can return the connected contributor object, not only elevation.

Current method to replace or extend:

- `FindConnectedRoadElevation(...)`

Suggested new helper shape:

```csharp
private static JunctionContributor? FindConnectedRoadContributor(
    UnifiedRoadNetwork network,
    int bridgeSplineId,
    bool isStart)
```

Rules:

- same junction-based lookup style as current Z reconciliation,
- skip excluded junctions,
- find bridge contributor for `(bridgeSplineId, isStart)`,
- return best connected non-bridge contributor,
- ignore generated bridge contributors from other spans for this first version.

### Phase C — tangent-only reconciliation

> ⚠️ **Superseded by §2.5.** `TangentDirection` does not drive the deck mesh — use the
> **normal-only** correction described in §2.5, gated on the Phase A diagnostics. The text below
> is kept for history.

Add a new pass after Z reconciliation and before mesh conversion.

Suggested method name:

```csharp
internal static BridgeEndpointPoseReconciliationResult ReconcileBridgeEndpointPose(
    UnifiedRoadNetwork network,
    IReadOnlyCollection<ParameterizedRoadSpline>? bridgeSplines = null)
```

First implementation scope:

- correct `TangentDirection` only,
- recompute `NormalDirection` from corrected tangent,
- keep `CenterPoint` unchanged,
- keep Z reconciliation as-is,
- leave width unchanged.

Algorithm per bridge endpoint:

1. Get bridge cross-sections for spline.
2. Find connected road contributor.
3. Measure heading delta between bridge endpoint tangent and connected road endpoint tangent.
4. If delta < threshold, skip.
5. If dot product is negative, flip target tangent.
6. Blend bridge tangents over a seam blend distance using `smoothstep` weighting.
7. Recompute normals.
8. Re-derive any dependent mesh-facing values if needed.

Suggested parameters for first pass:

- heading warn threshold: `3 deg`
- heading correction threshold: `1 deg`
- seam blend distance: `min(20m, 20% bridge length)`

Suggested helpers:

```csharp
private static float AngleDegrees(Vector2 a, Vector2 b)
private static Vector2 SafeNormalize(Vector2 v)
private static Vector2 BuildNormalFromTangent(Vector2 tangent)
private static float SmoothStep(float t)
```

## 4. Guardrails

- Do not move `CenterPoint` in the first version.
- Do not modify bridge body outside the seam blend zone.
- Skip isolated bridge endpoints.
- Skip invalid/NaN tangents.
- Skip tunnels.
- Preserve current endpoint Z reconciliation behavior.
- Keep this isolated to bridge deck export only.

## 5. Logging requirements

Add seam log lines before and after correction, or one combined line with before/after values.

Suggested format:

```text
[BRIDGE-SEAM] spline=81 start roadSpline=80 angleBefore=7.4deg angleAfter=0.8deg xyGap=0.18m widthDelta=0.00m zBefore=0.22m zAfter=0.00m action=corrected
[BRIDGE-SEAM] summary seams=20 angleOver3deg=4 poseCorrections=4 maxHeadingCorrection=8.10deg
```

Also include summary in the bridge export line from `TerrainCreator` if practical.

## 6. Tests to add first

File:

- `BeamNgTerrainPoc.Tests/Export/BridgeDeckDaeExporterTests.cs`

Add these tests:

1. `ReconcileBridgeEndpointPose_StartTangentMismatch_CorrectsHeading`
2. `ReconcileBridgeEndpointPose_EndTangentMismatch_CorrectsHeading`
3. `ReconcileBridgeEndpointPose_VeryShortBridge_ClampsBlendDistance`
4. `ReconcileBridgeEndpointPose_NoConnectedRoad_DoesNothing`
5. `ReconcileBridgeEndpointPose_OppositeDirectionContributor_FlipsTargetTangent`
6. `Export_ReconcilesZAndPose_WithoutRegression`

Synthetic test strategy:

- use `RoadNetworkTestHelpers.BuildNetworkWithJunctions(...)`,
- mark bridge cross-sections excluded as current bridge tests do,
- manually perturb bridge or road endpoint `TangentDirection`,
- call reconciliation directly,
- assert angle after correction <= `1.0 deg` for corrected seam,
- assert center points are unchanged in this first version.

## 7. Validation order

1. Add helper/refactor for connected contributor lookup.
2. Add diagnostics-only test(s).
3. Add tangent-only reconciliation.
4. Add summary counters to result.
5. Run focused tests.
6. Run full tests.
7. Regenerate the problematic level and compare seam screenshots.

## 8. Definition of done

This handoff is complete when:

1. Bridge seam diagnostics exist and identify heading mismatch.
2. Tangent-only reconciliation reduces seam angle mismatch in tests to <= `1 deg`.
3. Existing bridge endpoint Z reconciliation still passes.
4. Full test suite stays green.
5. The problematic top-down seam looks materially better in BeamNG screenshots.

## 9. Explicit non-goals for this pass

Do not do these yet:

- centerline XY seam movement,
- width matching,
- tunnel continuity,
- virtual merged corridor architecture,
- pipeline-level tangent harmonization,
- DecalRoad bridge continuity work (that belongs to pending Step 8 / separate task).

## 10. Follow-up if tangent-only is insufficient

If screenshots still show a visible kink after tangent-only correction:

1. add bounded normal/width seam blending,
2. only then consider bounded centerline seam movement,
3. only after that revisit the deferred merged-corridor architecture.
