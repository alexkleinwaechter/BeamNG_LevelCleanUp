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

## 3. Problem framing

At bridge seams we need continuity in 3 dimensions:

1. **Height continuity** (Z): endpoint elevations must match.
2. **Heading continuity** (plan tangent): road and bridge should meet with consistent angle.
3. **Width/normal continuity** (lateral frame): deck and approach surface orientation should agree.

The current fix primarily targets (1). The reported artifact indicates gaps in (2) and likely partially (3).

## 4. Likely causes

1. Bridge and approach splines are separate entities, so endpoint tangents can differ at a shared node.
2. Bridge deck export currently consumes bridge spline cross-sections as-is (good for source-of-truth, but no explicit tangent reconciliation).
3. Junction harmonization pipeline focuses on elevation/banking behaviors; it does not explicitly enforce plan-view tangent welding at structure boundaries.
4. Very short transition segments or continuation boundaries can magnify heading mismatch.

## 5. Proposed implementation approach

### Phase A: Diagnostics first (required)

Add seam diagnostics at each generated bridge endpoint:

- Bridge endpoint tangent vs connected road endpoint tangent angle delta (degrees).
- Endpoint width delta (meters).
- Endpoint normal delta (degrees).
- Endpoint centerline XY gap (meters).
- Endpoint Z gap before/after bridge endpoint reconciliation.

Log per seam and summary:

- count of seams over threshold (`> 3 deg` heading mismatch)
- max heading mismatch

This makes issues measurable and testable.

### Phase B: Bridge endpoint pose reconciliation (export-time, minimal-risk)

Before mesh generation for each bridge spline:

1. Find connected non-bridge road endpoint contributor for bridge start/end (same junction-based lookup style used for Z reconciliation).
2. Build endpoint target pose:
   - target tangent (from connected road endpoint tangent)
   - target width (from connected road endpoint effective width; optional clamp)
   - target normal (orthogonal to tangent, preserving side convention)
3. Blend bridge cross-sections near start/end toward these endpoint target poses over a short distance (for example 8-20 m or 10-20% of bridge length).
4. Keep centerline shape stable in the bridge body; only weld seam zones.

Notes:

- This is analogous to the current endpoint Z reconciliation, but for orientation/width frame.
- Use smooth blending (`smoothstep`) to avoid creating a new visible shoulder.

### Phase C: Guardrails

- Only apply pose reconciliation when bridge is generated (`IsBridge && ExcludeBridgesFromTerrain`).
- Skip isolated bridge endpoints (no connected road contributor).
- Skip corrections below tiny epsilon thresholds.
- Hard cap correction magnitudes to avoid extreme warps.

### Phase D: Tests

Add tests in `BeamNgTerrainPoc.Tests/Export/BridgeDeckDaeExporterTests.cs`:

1. Simulated tangent mismatch at bridge start -> exported seam tangent aligns within threshold.
2. Simulated tangent mismatch at bridge end -> same.
3. Very short bridge -> blend zone clamps safely.
4. No connected road contributor -> no pose correction applied.

Acceptance threshold example:

- post-reconciliation seam heading mismatch <= 1.0 deg

## 6. Optional pipeline-level alternative (larger scope)

Instead of export-time seam welding, enforce tangent continuity earlier in network building/spline conversion for bridge boundaries.

Pros:
- Single source-of-truth geometry.

Cons:
- Higher regression risk (affects all downstream consumers).
- More difficult to isolate to bridge deck feature.

Recommendation: start with export-time seam welding first, then revisit pipeline-level solution if needed.

## 7. Suggested execution order

1. Implement Phase A diagnostics.
2. Implement Phase B pose reconciliation (start with tangent only).
3. Validate on problematic map(s) with screenshots.
4. Extend to width/normal blending if tangent-only is insufficient.
5. Update `02-implementation-plan.md` with status note and link to this follow-up.

## 8. Definition of done for this follow-up

A bridge seam is considered continuous when, in top-down view:

- road and bridge meet with no visible kink,
- heading mismatch at seam is within configured threshold,
- no new artifacts are introduced in elevation or width,
- tests cover both connected and isolated endpoint behavior.
