# Terrain Spike Investigation — Continued

**Date**: 2026-03-08
**Branch**: `research/2026_03_08_hermite_c1` (based on d9e0254)
**Status**: ROOT CAUSE FOUND — MidSplineCrossing→TJunction conversion causes all spikes
**Related**: `dead_end_spike_investigation_2026-03-06.md`, `terrain_wall_bug_investigation_2026-03-06.md`

---

## Current State

Commit d9e0254 is the last good baseline — roads are smooth and junctions work correctly **except for 3 terrain spikes** on a 4x4km map with many OSM roads. Everything else is clean.

### Spike Types (from screenshots)

1. **Two nearby dead-end endpoints** — both rise instead of tapering to terrain.
2. **T-junction: high-priority asphalt + low-priority dirt road** — spike at the junction itself.
3. **Junction of same-priority dirt roads** — spike at the junction itself.

---

## Resolution: MidSplineCrossing→TJunction Conversion Is the Root Cause

### Diagnostic Tests Performed

1. **Disabled `FinalSnapTJunctionEndpoints` entirely** (commented out call in `UnifiedRoadSmoother.cs:464`)
   - Junction spikes (types 2 & 3) **disappeared**
   - Dead-end spikes (type 1) **smaller but still present**
   - Junction quality **degraded** — elevation mismatches at junctions with elevation changes (bumps visible). FinalSnap IS needed.

2. **Re-enabled `FinalSnapTJunctionEndpoints` with skip guard** (skip when endpointCS > totalExtent*2 from junction)
   - 25 splines were skipped — far too many
   - Some junctions improved, others got worse because they lost their drift correction

3. **Re-enabled `FinalSnapTJunctionEndpoints` with isStart flip** (flip when other endpoint is closer to junction)
   - Did not fully resolve spikes, introduced new artifacts

4. **Disabled MidSplineCrossing→TJunction conversion via parameter**
   - **ALL spikes disappeared**
   - **Junctions almost completely clean**
   - This is the definitive root cause identification

### Why MidSplineCrossing→TJunction Conversion Causes Spikes

The conversion creates junctions in the **middle** of splines, but the junction system assumes junctions are at spline **endpoints**. This causes:

1. **Wrong `IsSplineStart` flag**: The boolean can only say "start" or "end" — it cannot represent "middle of spline". `CrossroadToTJunctionConverter` picks one end, often the wrong one.

2. **`FinalSnapTJunctionEndpoints` corrupts remote cross-sections**: With wrong `isStart`, distances are measured from the dead-end tip (700m from junction), and cross-sections near the dead end get snapped to extrapolated primary surface elevations.

3. **`ComputeTJunctionConstraints` stores constraints under wrong key**: The constraint for the converted junction gets keyed to the wrong endpoint, potentially applying corrections at the wrong end of the spline.

4. **Multiple converted junctions per spline**: A long spline crossing multiple roads gets multiple MidSplineCrossing→TJunction conversions (e.g., Spline 23 had 4 converted junctions). Each one has potentially wrong `IsSplineStart`, compounding the corruption.

### Without the Conversion

- MidSplineCrossings remain as their original junction type
- No wrong `IsSplineStart` assignments
- `FinalSnapTJunctionEndpoints` only processes real T-junctions (where splines genuinely terminate at another road)
- `BlendSplineProfile` handles the mid-spline crossings correctly since it uses symmetric distance measurement

---

## Next Steps — User Testing

The user is testing with MidSplineCrossing→TJunction conversion disabled to verify:
- All spikes are truly gone across the full map
- Junction quality at mid-spline crossings is acceptable without the T-junction treatment
- No regressions in other areas

### If Testing Succeeds: Consider Permanent Removal

The conversion was added to give mid-spline crossings the benefit of T-junction surface matching (terminating road snaps to primary surface). But the cost is:
- Wrong `IsSplineStart` causing spikes
- 25+ splines affected on a single 4x4km map
- Multiple failed fix attempts

`BlendSplineProfile`'s two-pass system may already handle mid-spline crossings well enough without converting them to T-junctions. The conversion may be unnecessary.

### If Some Junctions Need T-Junction Treatment

The proper fix would be to **split the spline at the crossing point** into two separate splines, each with a genuine endpoint at the junction. This is architecturally correct (each spline terminates at the junction) but requires significant refactoring of `CrossroadToTJunctionConverter`.

---

## Approaches Tried (All Failed)

### Attempt 1: Clamp Surface Extrapolation (6 code locations)
**Hypothesis**: Unbounded `longitudinalOffset * slope` in `GetPrimarySurfaceElevation()`.
**Result**: Spikes persisted, new artifacts introduced. Constraint deltas were all <15m — extrapolation wasn't the issue.
**Conclusion**: Wrong approach. The constraint values are fine; the problem is WHERE they're applied.

### Attempt 2: Flip IsSplineStart in FinalSnapTJunctionEndpoints
**Hypothesis**: Wrong `IsSplineStart` flag causes wrong distance measurement direction.
**Result**: New bugs appeared. Flipping direction changes all downstream offset calculations.
**Conclusion**: Fixing direction in isolation isn't enough — the entire converted junction's data is inconsistent.

### Attempt 3: Skip FinalSnapTJunctionEndpoints for remote endpointCS
**Hypothesis**: Skip snapping when endpointCS is far from junction.
**Result**: 25 splines skipped, junction quality degraded for those splines.
**Conclusion**: Too many junctions affected — skipping is not viable.

### Attempt 4: Disable FinalSnapTJunctionEndpoints entirely
**Result**: Junction spikes gone but junction quality degraded (elevation bumps).
**Conclusion**: FinalSnap IS needed for quality, just shouldn't process converted junctions.

### Attempt 5: Disable MidSplineCrossing→TJunction conversion (SUCCESS)
**Result**: All spikes gone, junctions almost completely clean.
**Conclusion**: The conversion itself is the root cause. Without it, the pipeline works correctly.

---

## Key Files

| File | Role |
|------|------|
| `CrossroadToTJunctionConverter.cs` | MidSplineCrossing→TJunction conversion — **ROOT CAUSE** |
| `UnifiedJunctionProfileBlender.cs:1132` | `FinalSnapTJunctionEndpoints` — corrupted by wrong IsSplineStart from conversion |
| `UnifiedJunctionProfileBlender.cs:555` | `BlendSplineProfile` — handles crossings correctly without conversion |
| `NetworkJunctionDetector.cs` | Junction detection, calls the converter |
| `UnifiedRoadSmoother.cs:464` | Calls `FinalSnapTJunctionEndpoints` |

---

## Previous Investigation References

- `ai_docs/dead_end_spike_investigation_2026-03-06.md` — Dead-end spike analysis (Spline 20 case study)
- `ai_docs/terrain_wall_bug_investigation_2026-03-06.md` — Terrain wall fix that unmasked the spikes
- `ai_docs/hermite_c1_tjunction_pipeline_2026-03-06.md` — Pipeline architecture reference
