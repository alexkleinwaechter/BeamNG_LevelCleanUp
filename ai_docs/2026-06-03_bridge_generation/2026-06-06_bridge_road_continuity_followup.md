# Follow-up: Bridge-to-Road Top-Down Continuity

**Date:** 2026-06-06  
**Related docs:**
- `ai_docs/2026-06-03_bridge_generation/00-findings-and-decisions.md`
- `ai_docs/2026-06-03_bridge_generation/01-spec-simple-bridge-deck.md`
- `ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md`

## 1. Why this follow-up exists

The bridge deck currently matches approach roads better in Z at endpoints (recent endpoint elevation reconciliation), but visual continuity is still off in top-down view:

- Approach roads do not always hit the bridge deck at the same heading/entry angle.
- In practice this looks like a lateral "kink" at one or both bridge ends.
- The user screenshot (red marker rectangles) shows this clearly.

This is a **plan-view continuity** issue, not only an elevation issue.

## 2. Current status of the bridge implementation plan

As of this follow-up:

- Steps 1-5: implemented
- Step 7 (bridge endpoint harmonization guard): implemented
- Step 6: still marked partial in the plan doc (manual quality pass remains)
- Step 8 (bridge DecalRoads with `OverObjects=true`): still pending

Conclusion: the existing implementation plan is not fully complete yet.

## 2b. Chosen implementation direction

We will do the **easier, lower-risk approach first**:

- keep the current bridge deck pipeline,
- add seam diagnostics,
- add export-time bridge endpoint pose reconciliation,
- start with **tangent-only** correction,
- leave the larger "virtual merged corridor" architecture as a later option.

Reasoning:

- it isolates the change to bridge deck export,
- it is easy to validate against the current screenshots,
- it avoids refactoring terrain stamping, road painting, junction logic, and DecalRoad generation all at once,
- it gives a fast answer to the question: is the visible seam primarily a tangent mismatch?

If tangent-only seam welding fixes the issue, keep the architecture simple. If it does not, extend this same
approach to bounded width/normal/centerline correction before considering the larger corridor refactor.

## 3. Problem framing

At bridge seams we need continuity in 3 dimensions:

1. **Height continuity** (Z): endpoint elevations must match.
2. **Heading continuity** (plan tangent): road and bridge should meet with consistent angle.
3. **Width/normal continuity** (lateral frame): deck and approach surface orientation should agree.

The current bridge endpoint reconciliation primarily targets (1). It compares each generated bridge endpoint
against the connected non-bridge road endpoint and adjusts the bridge deck elevation profile so the deck end
lands on the road surface. That is necessary, but the screenshot shows a different remaining artifact: from
top-down, the approach road and bridge deck do not share the same incoming direction.

This means the road can be vertically flush but still visually wrong because the **plan-view tangent** differs.
The seam then looks like a kink, angled cut, or lateral offset at the deck edge. This is especially visible when
the bridge deck is a clean rectangular/white surface and the terrain road continues underneath/near it.

For this follow-up, treat bridge seams as a **pose matching** problem:

- position: endpoint XY should be close enough to be considered the same seam,
- elevation: endpoint Z should match,
- tangent: forward direction into/out of the seam should match,
- normal: lateral frame should be compatible,
- width: road and deck should not suddenly change width unless the source data really says so.

The first implementation should focus on tangent/heading. Width and normal can be added after diagnostics prove
they are part of the visible problem.

## 4. Likely causes

1. Bridge and approach splines are separate entities, so endpoint tangents can differ at a shared node.
2. Bridge deck export currently consumes bridge spline cross-sections as-is (good for source-of-truth, but no explicit tangent reconciliation).
3. Junction harmonization pipeline focuses on elevation/banking behaviors; it does not explicitly enforce plan-view tangent welding at structure boundaries.
4. Very short transition segments or continuation boundaries can magnify heading mismatch.

More detailed possibilities:

1. **OSM way split geometry**
   - Bridge ways often begin/end at different OSM way boundaries than the visual approach curve.
   - The bridge way can have a short first/last segment whose tangent differs from the larger approach curve.

2. **Cross-section tangent source**
   - `UnifiedCrossSection.TangentDirection` is sampled from the owning spline.
   - For separate splines, a shared endpoint does not guarantee identical tangent vectors.

3. **Deck mesh faithfully exposes the mismatch**
   - The generated bridge deck uses bridge cross-sections directly.
   - This is correct for D2, but it makes tangent discontinuities visible because the deck edge is crisp.

4. **Road terrain stamping hides some road-side issues**
   - Normal roads are blended into terrain and visually softened.
   - A bridge `.dae` is hard geometry, so the same angular mismatch is more obvious.

5. **Endpoint Z correction does not affect XY/tangent**
   - `ReconcileBridgeEndpointElevations` fixes centerline elevation only.
   - It intentionally does not move points or rotate tangents, so top-down continuity is untouched.

## 5. Proposed implementation approach

### Phase A: Diagnostics first (required)

Add seam diagnostics at each generated bridge endpoint:

- Bridge endpoint tangent vs connected road endpoint tangent angle delta (degrees).
- Endpoint width delta (meters).
- Endpoint normal delta (degrees).
- Endpoint centerline XY gap (meters).
- Endpoint Z gap before/after bridge endpoint reconciliation.
- Optional: road and bridge OSM node IDs / way IDs when available.

Log per seam and summary:

- count of seams over threshold (`> 3 deg` heading mismatch)
- max heading mismatch
- max XY gap
- max width delta
- max Z gap before/after correction

This makes issues measurable and testable.

Recommended log shape:

```text
[BRIDGE-SEAM] spline=81 start node=... roadSpline=80 angle=7.4deg xyGap=0.18m widthDelta=0.00m zBefore=0.22m zAfter=0.00m action=diagnose
[BRIDGE-SEAM] summary seams=20 angleOver3deg=4 maxAngle=11.2deg maxXYGap=0.31m maxWidthDelta=0.50m
```

Important: diagnostics should run before any tangent correction, then optionally again after correction so we can
see improvement.

### Phase B: Bridge endpoint pose reconciliation (export-time, minimal-risk, chosen first path)

Before mesh generation for each bridge spline:

1. Find connected non-bridge road endpoint contributor for bridge start/end (same junction-based lookup style used for Z reconciliation).
2. Build endpoint target pose:
   - target tangent (from connected road endpoint tangent)
   - target width (from connected road endpoint effective width; optional clamp)
   - target normal (orthogonal to tangent, preserving side convention)
3. Blend bridge cross-sections near start/end toward these endpoint target poses over a short distance.
4. Keep the bridge body stable; only weld seam zones.

Notes:

- This is analogous to the current endpoint Z reconciliation, but for orientation/width frame.
- Use smooth blending (`smoothstep`) to avoid creating a new visible shoulder.

Chosen first implementation: **tangent-only correction**.

Do not move centerline XY in the first pass. Instead:

1. At bridge start, replace or blend the first N cross-section tangents toward the connected road endpoint tangent.
2. At bridge end, replace or blend the last N cross-section tangents toward the connected road endpoint tangent.
3. Recompute normals from the corrected tangents.
4. Leave center points and elevations unchanged.

This is the first implementation we will actually do. It is low-risk because the mesh strip still follows the same
centerline, but its lateral edge orientation at the seam matches the approach road better.

If tangent-only does not remove the visible kink, Phase B.2 can add **centerline seam position blending**:

- move only the first/last cross-section center point to the connected road endpoint if the XY gap is below a safe cap,
- blend the next few center points with decreasing weight,
- never move bridge body points beyond the seam weld distance.

Suggested default parameters:

| Parameter | Initial value | Purpose |
|-----------|---------------|---------|
| Heading warn threshold | 3 deg | log suspicious seams |
| Heading correction threshold | 1 deg | skip tiny changes |
| Seam blend distance | min(20m, 20% bridge length) | tangent/normal correction zone |
| Max XY correction | 2m | only for optional centerline blending |
| Max width correction | 2m | avoid extreme deck width changes |

Use `smoothstep` weight from 1 at the seam to 0 at the end of the weld zone:

```csharp
var t = distanceFromSeam / blendDistance;
var weight = 1f - SmoothStep(Math.Clamp(t, 0f, 1f));
```

For tangent blending, use normalized vector lerp:

```csharp
var blended = Vector2.Normalize(Vector2.Lerp(originalTangent, targetTangent, weight));
```

If the target tangent points opposite the bridge tangent, flip it before blending:

```csharp
if (Vector2.Dot(originalTangent, targetTangent) < 0f)
    targetTangent = -targetTangent;
```

Then recompute normal using the same convention as the rest of the terrain pipeline.

### Phase C: Guardrails

- Only apply pose reconciliation when bridge is generated (`IsBridge && ExcludeBridgesFromTerrain`).
- Skip isolated bridge endpoints (no connected road contributor).
- Skip corrections below tiny epsilon thresholds.
- Hard cap correction magnitudes to avoid extreme warps.
- Never use terrain elevation for this correction.
- Do not apply to tunnels yet.
- Preserve existing Z reconciliation behavior.
- Preserve banking/edge elevation values unless the tangent/normal correction requires re-deriving deck mesh edges.
- Log every skipped correction reason when diagnostics are enabled.

Potential skip reasons:

- no connected non-bridge endpoint,
- connected endpoint has invalid tangent,
- bridge endpoint has invalid tangent,
- heading delta below threshold,
- XY gap above safety cap,
- bridge too short for requested blend distance.

### Phase D: Tests

Add tests in `BeamNgTerrainPoc.Tests/Export/BridgeDeckDaeExporterTests.cs`:

1. Simulated tangent mismatch at bridge start -> exported seam tangent aligns within threshold.
2. Simulated tangent mismatch at bridge end -> same.
3. Very short bridge -> blend zone clamps safely.
4. No connected road contributor -> no pose correction applied.
5. Opposite-direction tangent contributor -> target tangent is flipped before blending.
6. Z reconciliation still works when tangent reconciliation also runs.

Acceptance threshold example:

- post-reconciliation seam heading mismatch <= 1.0 deg

Test implementation hint:

- Build synthetic road -> bridge -> road network using `RoadNetworkTestHelpers`.
- Manually perturb the bridge endpoint tangent or connected road endpoint tangent.
- Call the new reconciliation method directly, similar to current bridge endpoint elevation tests.
- Assert corrected bridge first/last `TangentDirection` angle vs connected road tangent.

### Phase E: Integration and logging

Extend `BridgeDeckExportResult` with:

- `PoseCorrectionsApplied`
- `MaxHeadingCorrectionDegrees`
- `SeamsOverHeadingThreshold`

Extend the bridge export log line:

```text
Bridge deck export: 10 deck(s) written (...), 2 endpoint correction(s), max correction 0.28m, 4 pose correction(s), max heading correction 8.10deg.
```

This gives fast feedback during manual testing without opening debug images.

### Phase F: Optional debug artifact

If text logs are not enough, add a CSV file under `MT_TerrainGeneration/bridge_debug/`:

```text
bridgeSplineId,end,roadSplineId,angleBeforeDeg,angleAfterDeg,xyGapMeters,widthDeltaMeters,zGapBefore,zGapAfter,action
81,start,80,7.4,0.8,0.18,0.0,0.22,0.0,corrected
```

This can later feed a simple overlay/debug image if needed.

## 6. Optional pipeline-level alternative (larger scope, deferred)

Instead of export-time seam welding, enforce tangent continuity earlier in network building/spline conversion for bridge boundaries.

Pros:
- Single source-of-truth geometry.

Cons:
- Higher regression risk (affects all downstream consumers).
- More difficult to isolate to bridge deck feature.

Recommendation: keep this deferred. Only revisit it if diagnostics + tangent-only seam welding still leave a visible
top-down seam problem.

Pipeline-level ideas to keep in mind:

1. Merge bridge and adjacent approach geometry into one continuous master spline for sampling, while still marking the bridge span as excluded from terrain stamping.
2. Adjust OSM way-boundary tangents during `UnifiedRoadNetworkBuilder` cross-section generation.
3. Add a dedicated structure-boundary tangent harmonizer after junction detection but before mesh/decal export.

These are more coherent long-term, but they touch more downstream systems: terrain masks, DecalRoads, banking,
junction blending, and debug exports. They should wait until export-time welding proves the target behavior.

## 7. Suggested execution order

1. Implement Phase A diagnostics.
2. Implement Phase B pose reconciliation (tangent-only first).
3. Validate on problematic map(s) with screenshots.
4. Extend to width/normal blending only if tangent-only is insufficient.
5. Consider bounded centerline seam correction only if still needed.
6. Update `02-implementation-plan.md` with status note and link to this follow-up.

More detailed task list:

1. Add a `BridgeSeamPose` / `BridgeSeamDiagnostic` internal model in `BridgeDeckDaeExporter.cs` or a new helper file under `Terrain/Export/`.
2. Reuse the current junction lookup used by `FindConnectedRoadElevation`, but return the connected contributor object, not just Z.
3. Implement angle measurement helpers:
   - normalized tangent validation,
   - directed/absolute angle in degrees,
   - target tangent flip when dot product is negative.
4. Add diagnostics-only tests first.
5. Add tangent-only correction and tests.
6. Add result/log counters.
7. Run focused tests, then all tests.
8. Regenerate `franco_same_prio` and compare top-down seam screenshots.

## 8. Definition of done for this follow-up

A bridge seam is considered continuous when, in top-down view:

- road and bridge meet with no visible kink,
- heading mismatch at seam is within configured threshold,
- no new artifacts are introduced in elevation or width,
- tests cover both connected and isolated endpoint behavior.

Concrete acceptance criteria:

1. Diagnostics report every generated bridge endpoint and identify seams over the heading threshold.
2. Tangent-only correction reduces corrected seam heading mismatch to <= 1 degree in synthetic tests.
3. On the reported map, the red-marker seam areas no longer show a clear top-down angle break.
4. Existing bridge endpoint Z reconciliation still passes.
5. Full test suite remains green.

## 9. Open questions

1. Should bridge deck geometry remain strictly faithful to OSM bridge way geometry, or may it slightly rotate seam tangents for visual continuity?
   - Recommendation: allow local tangent correction; keep centerline movement disabled initially.
2. Should the road terrain surface be adjusted to the bridge tangent instead of adjusting the bridge deck?
   - Recommendation: not initially. Terrain road surfaces affect more systems and are harder to isolate.
3. Should width be matched automatically at bridge ends?
   - Recommendation: log width deltas first. Correct only if visible and bounded.
4. Should this also apply to future tunnel meshes?
   - Recommendation: eventually yes, but keep this follow-up bridge-only.

## 10. Handoff summary

The next implementation should not start by moving geometry broadly or introducing a new virtual merged-corridor
architecture. Start with seam diagnostics and tangent-only export-time pose reconciliation. That gives a low-risk way
to confirm whether the top-down visual kink is caused by tangent mismatch. If tangent correction fixes the screenshot
issue, stop there. If not, extend the same framework to bounded normal/width/centerline reconciliation before
considering the larger corridor refactor.
