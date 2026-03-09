# Next Session Prompt: Junction Elevation Architecture Rethink

## Quick Context
Read these files first:
- `ai_docs/junction_bump_investigation_2026-02-28.md` — full investigation log of what was tried
- `ai_docs/rubberband_elevation_architecture.md` — current architecture
- `C:\Users\aklei\.claude\projects\d--Source-Heroes-beamng-mapping-pro\memory\junction_elevation_research.md` — memory file

## Current state of the code (after rollback)

Branch `research_rubberband_idea`, commit `7a6c0f8`. All broken experimental changes have been **rolled back**. The code is clean and contains only the SAFE fixes from the session:

**What's in the committed code (SAFE, KEPT):**
- Phase 3.6: Edge constraint propagation moved after Phase 3.5 banking finalization
- BankingOrchestrator: Hardcoded 30m synced with configured blend distance
- MasterSplineExporter: Uses constrained edge centers instead of TargetElevation
- Junction CS recomputed in Phase 3.6 at weight=1.0 (skipTargetElevationModification flag)

**What was rolled back (broken, NOT in code):**
- `ApplyEdgeConstraintsForMultiWayJunction` — caused spikes at non-T-junctions
- Widened Phase 3.6 filter to Y/CrossRoads/Complex — caused walls/spikes
- Phase 3.7 `SmoothConstrainedEdgeProfiles` — caused mesh folding at blend boundaries

## IMPORTANT: Check for hardcoded values!

Before making any changes, audit the code for **hardcoded values that should use UI parameters**. Known issue: `BankingOrchestrator.cs` previously had `30.0f` hardcoded in 4 places for transition distances. This was fixed to use `GetMaxEffectiveBlendDistance(network)`. But there may be OTHER hardcoded values throughout the pipeline that should read from `JunctionHarmonizationParameters`, `RoadSmoothingParameters`, or `BankingParameters` instead.

Key parameters to check against hardcoded values:
- `JunctionBlendDistanceMeters` (default 30m) — used for edge constraint blend zone
- `RoadMaxSlopeDegrees` (default 6°) — used in `CalculateAdaptiveBlendDistance`
- `RoadEdgeProtectionBufferMeters` (default 2m) — terrain blending protection zone
- `SmoothingWindowSize` (default 101) — elevation smoother window
- `BankingParameters.MaxBankAngleDegrees` — banking limits
- Any transition distances, blend factors, or thresholds in Phase 3.5/3.6 code

Search for magic numbers like `30.0f`, `0.001f`, `6.0f`, `2.0f`, etc. in:
- `BankingOrchestrator.cs`
- `NetworkJunctionHarmonizer.cs`
- `PriorityAwareJunctionBankingCalculator.cs`
- `JunctionBankingAdapter.cs`
- `JunctionSurfaceCalculator.cs`

## What happened (2026-02-28)

We spent a full day fixing junction bumps. Several real issues were fixed (see "safe" changes above). But a persistent bump near junctions remained despite eliminating every suspected cause individually. The bump appears on terminating roads near the junction point.

### What we tried that DIDN'T fix the remaining bump:
1. Removing Phase 3.5 Step 3 (AdaptElevationsToHigherPriorityBanking) — no change
2. Setting protection buffer to 0 — no change
3. Various stale constraint fixes — no change
4. Extending Phase 3.6 to non-T-junctions — caused massive new problems (reverted)
5. Post-Phase-3.6 box filter smoothing on constrained edges — caused mesh folding (reverted)

### Key discovery: The persistent bump was at junctions NOT classified as T-Junctions
Phase 3.6 (edge constraint propagation) ONLY runs for `JunctionType.TJunction`. Junctions classified as Y-Junction, CrossRoads, or Complex get NO edge constraints and NO Phase 3.6 propagation. The bump was at these junction types.

## The fundamental problem

Multiple systems modify the terminating road's elevation near junctions:
- **Phase 3.1 (Rubberband)**: CubicHermiteC1 correction on TargetElevation (entire road)
- **Phase 3.5 Step 2**: Banking angle adjustment (cosine transition)
- **Phase 3.5 Step 3**: TargetElevation quintic ramp (blend distance zone)
- **Phase 3.6**: Constrained edge projection onto primary surface (blend distance zone)

Each uses a different transition curve (Hermite, cosine, quintic, blend function). They overlap and interfere. Removing any individual system doesn't eliminate the bump — it's the interaction.

## Recommended approaches (in order of preference)

### Option A: Fix junction detection (most targeted)
The persistent bump was at junctions that weren't T-Junctions. Instead of forcing edge constraints on junction types they weren't designed for (which caused spikes), improve junction DETECTION:
1. Keep Phase 3.6 as T-Junction only
2. Extend `CrossroadToTJunctionConverter` to handle more cases — any junction where there IS a clear continuous road should be classified as T-Junction(s)
3. This way Phase 3.6 handles them correctly with existing proven logic

### Option B: Single unified system (ambitious, cleanest)
Replace all four overlapping systems with ONE system that handles both TargetElevation AND edges in a single coordinated pass:
1. Compute target surface at junction (primary road projection)
2. Compute natural surface far from junction (terrain-following + banking)
3. Blend using ONE curve
4. Set BOTH TargetElevation AND constrained edges from this single blend

### Option C: Post-processing smoothing on final mesh data
After all phases complete, smooth the FINAL edge elevation profiles:
1. Compute final left/right edge positions (what RoadMeshBuilder would produce)
2. Detect bumps (high second derivative)
3. Smooth with bilateral/Gaussian filter (preserve endpoints)
4. **Must include safety check: left/right edges must not cross each other**

### Option D: Iterative relaxation (user's original idea)
Run pipeline → detect acceleration spikes → set virtual anchors → re-blend → repeat 3-5 times.

## Key files
- `NetworkJunctionHarmonizer.cs` — Phase 3, 3.6, edge constraint propagation
- `BankingOrchestrator.cs` — Phase 3.5 orchestration
- `JunctionBankingAdapter.cs` — Phase 3.5 Step 3
- `JunctionSurfaceCalculator.cs` — surface projection math
- `BankedTerrainHelper.cs` — GetBankedElevation code path split
- `OptimizedElevationSmoother.cs` — existing smoothing filters (BoxFilterPrefixSum, ButterworthLowPassFilter)
- `UnifiedRoadSmoother.cs` — pipeline orchestration
- `MasterSplineExporter.cs` — spline export
- `CrossSectionConverter.cs` — mesh export
- `NetworkJunctionDetector.cs` — junction type classification
- `CrossroadToTJunctionConverter.cs` — converts crossroads to T-junction pairs

## Test preset
`D:\Temp\Test_Cleanup\__preset_france_italy\theTerrain_terrainPreset.json`
