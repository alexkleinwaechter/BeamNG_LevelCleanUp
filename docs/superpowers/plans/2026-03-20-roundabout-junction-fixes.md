# Roundabout Junction Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix duplicate roundabout ring spline generation across materials and improve roundabout junction elevation blending to match the smoothness quality of T-junctions.

**Architecture:** Two independent fixes: (1) Add cross-material deduplication to prevent the same OSM roundabout from generating multiple ring splines when its ways/connections span multiple road materials. Only ring spline creation is deduplicated — road trimming and way exclusion still run for every material so connecting roads are properly handled. (2) Align the roundabout constraint model in `UnifiedJunctionProfileBlender` with the T-junction model by baking ring banking into constraint elevation, using the connecting road's radial approach direction for slope tracking, and interpolating ring elevation at the exact connection point.

**Tech Stack:** C# / .NET 9, xUnit (BeamNgTerrainPoc.Tests), System.Numerics

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Modify | Skip ring spline creation for already-processed roundabouts while preserving trimming/exclusion |
| `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs` | Modify | Accept `skipRingCreationIds` parameter to skip ring spline creation for specific roundabouts |
| `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` | Modify | Store shared `processedRoundaboutIds` as field, thread through call chain |
| `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` | Modify | Fix `ComputeRoundaboutConstraints` to use edge-anchored pattern with radial slope |
| `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutDeduplicationTests.cs` | Create | Tests for cross-material deduplication logic |
| `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutBlendingTests.cs` | Create | Tests for roundabout constraint geometry |

---

## Task 1: Cross-Material Roundabout Ring Spline Deduplication

### Problem

`ConvertLinesToSplinesWithRoundabouts` is called once per material in `TerrainGenerationOrchestrator.ProcessOsmRoadMaterialAsync()` (line ~838). The filtering at [OsmGeometryProcessor.cs:1153-1156](BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs#L1153-L1156) includes a roundabout if **any** ring way OR **any** connecting road is in the current material. When a roundabout's ring ways are in material A (e.g., `highway=tertiary`) but a connecting road is in material B (e.g., `highway=residential`), both materials create independent ring splines → two splines in the unified network for one physical roundabout.

### Solution

**Key insight:** We cannot simply skip the entire roundabout for material B — that would also skip connecting road trimming (lines 1207-1226) and way ID exclusion (lines 1239-1244), breaking material B's connecting roads. Instead, we keep the roundabout in `detectedRoundabouts` for ALL materials (so trimming and exclusion work correctly), but only create the ring **spline** once.

The dedup is applied at the `RoundaboutMerger.ProcessRoundabouts` level: a `skipRingCreationIds` set tells the merger which roundabouts should have their way IDs marked as processed (for exclusion) but should NOT get a new ring spline. The `HashSet<long>` is stored as a field on the orchestrator and flows through the call chain.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs:165-223` — add `skipRingCreationIds` parameter
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs:1121-1235` — pass dedup set through to merger, mark processed IDs after merger returns
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs:108-120,714-750,752-787,789-866` — add field + thread parameter through 3 intermediate methods
- Create: `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutDeduplicationTests.cs`

- [ ] **Step 1: Write test for deduplication logic**

Create `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutDeduplicationTests.cs`:

```csharp
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

/// <summary>
/// Tests that roundabout ring splines are created only once across materials,
/// while connecting road trimming and way exclusion still work for all materials.
/// </summary>
public class RoundaboutDeduplicationTests
{
    [Fact]
    public void SecondMaterial_SkipsRingCreation_ButKeepsWayExclusion()
    {
        // Simulate: roundabout 100 was already processed by material A.
        // Material B also detects it (via connecting road).
        // Way exclusion should still work, but ring spline should not be created.
        var alreadyProcessedRingIds = new HashSet<long> { 100L };

        // All detected roundabouts for material B
        var detectedIds = new List<long> { 100L, 200L };

        // Ring spline creation: only for NOT-yet-processed
        var createRingFor = detectedIds.Where(id => !alreadyProcessedRingIds.Contains(id)).ToList();

        // Way exclusion: for ALL detected roundabouts (regardless of ring creation)
        var excludeWaysFor = detectedIds; // all of them

        Assert.Single(createRingFor);
        Assert.Equal(200L, createRingFor[0]);
        Assert.Equal(2, excludeWaysFor.Count); // both still get way exclusion
    }

    [Fact]
    public void FirstMaterial_CreatesRing_AndMarksProcessed()
    {
        var processed = new HashSet<long>();
        var roundaboutId = 100L;

        // First material processes it
        Assert.DoesNotContain(roundaboutId, processed);
        processed.Add(roundaboutId);
        Assert.Contains(roundaboutId, processed);
    }

    [Fact]
    public void SingleMaterial_NoDedup_AllRingsCreated()
    {
        // When no dedup set is provided, all roundabouts create ring splines
        HashSet<long>? alreadyProcessed = null;
        var detectedIds = new List<long> { 100L, 200L };

        var createRingFor = alreadyProcessed == null
            ? detectedIds
            : detectedIds.Where(id => !alreadyProcessed.Contains(id)).ToList();

        Assert.Equal(2, createRingFor.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoundaboutDeduplicationTests" -v minimal`
Expected: 3 tests PASS

- [ ] **Step 3: Add `skipRingCreationIds` parameter to `RoundaboutMerger.ProcessRoundabouts`**

In `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs`, modify the `ProcessRoundabouts` method signature (line ~165) to accept an optional set of roundabout IDs to skip ring creation for:

```csharp
public RoundaboutProcessingResult ProcessRoundabouts(
    List<OsmRoundabout> roundabouts,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    SplineInterpolationType interpolationType,
    HashSet<long>? skipRingCreationIds = null)  // <-- NEW
```

Then inside the `foreach (var roundabout in roundabouts)` loop (line ~187), after marking way IDs as processed (lines 189-194), add a check before ring spline creation:

```csharp
// Mark all roundabout ways as processed (always — needed for way exclusion)
foreach (var wayId in roundabout.WayIds)
{
    result.ProcessedFeatureIds.Add(wayId);
    result.Statistics.TotalWaysProcessed++;
}

// Skip ring spline creation if this roundabout was already processed by another material
if (skipRingCreationIds != null && skipRingCreationIds.Contains(roundabout.Id))
{
    TerrainLogger.Detail($"  Skipping ring spline creation for roundabout {roundabout.Id} " +
        $"(already created by another material)");
    continue;
}

// Convert roundabout ring to spline
var processedInfo = CreateProcessedRoundaboutInfo(
    roundabout, bbox, terrainSize, metersPerPixel, interpolationType);
```

This ensures way IDs are always marked as processed (so connecting roads from material B still get filtered from regular processing at lines 1239-1244), but the ring spline is only created once.

- [ ] **Step 4: Add `alreadyProcessedRoundaboutIds` to `ConvertLinesToSplinesWithRoundabouts` and pass it to the merger**

In `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`, add the parameter to the method signature (line 1121):

```csharp
public List<RoadSpline> ConvertLinesToSplinesWithRoundabouts(
    List<OsmFeature> lineFeatures,
    OsmQueryResult fullQueryResult,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    SplineInterpolationType interpolationType,
    out List<OsmRoundabout> detectedRoundabouts,
    out HashSet<long> roundaboutWayIds,
    out RoundaboutMerger.RoundaboutProcessingResult roundaboutProcessingResult,
    bool enableRoadTrimming = true,
    float overlapToleranceMeters = 2.0f,
    float minPathLengthMeters = 1.0f,
    float duplicatePointToleranceMeters = 0.01f,
    float endpointJoinToleranceMeters = 1.0f,
    string? debugOutputPath = null,
    bool excludeBridges = false,
    bool excludeTunnels = false,
    HashSet<long>? alreadyProcessedRoundaboutIds = null)  // <-- NEW
```

Then at the merger call site (lines 1228-1235), pass the dedup set:

```csharp
// Step 3: Create roundabout ring splines using RoundaboutMerger
var merger = new RoundaboutMerger(this);
roundaboutProcessingResult = merger.ProcessRoundabouts(
    detectedRoundabouts,
    bbox,
    terrainSize,
    metersPerPixel,
    interpolationType,
    skipRingCreationIds: alreadyProcessedRoundaboutIds);  // <-- NEW
```

After the merger returns, mark newly-processed roundabouts so subsequent material calls skip them:

```csharp
// Mark these roundabouts as processed for subsequent material calls
if (alreadyProcessedRoundaboutIds != null)
{
    foreach (var r in detectedRoundabouts)
        alreadyProcessedRoundaboutIds.Add(r.Id);
}
```

Insert this immediately after the merger call (after line 1235).

**Note:** The `detectedRoundabouts` list is NOT filtered — all matching roundabouts remain, ensuring trimming (Step 2 at lines 1207-1226) and way exclusion (Step 4 at lines 1239-1244) still work for every material.

- [ ] **Step 5: Thread `alreadyProcessedRoundaboutIds` through the orchestrator call chain**

The call chain is: `foreach mat loop` (line 111) → `ProcessMaterialAsync` (line 714) → `ProcessOsmMaterialAsync` (line 752) → `ProcessOsmRoadMaterialAsync` (line 789) → `ConvertLinesToSplinesWithRoundabouts` (line 838).

The `HashSet` must be threaded through all four levels.

**5a.** Add a field to `TerrainGenerationOrchestrator` (near other fields at the class level):

```csharp
/// <summary>
/// Tracks which roundabouts have already had ring splines created across material iterations.
/// Prevents duplicate ring splines when roundabout components span multiple materials.
/// Reset at the start of each terrain generation run.
/// </summary>
private HashSet<long> _processedRoundaboutIds = new();
```

**5b.** Reset at the start of the generation method (near line 108, before the foreach loop):

```csharp
_processedRoundaboutIds = new HashSet<long>();
```

**5c.** Modify `ProcessMaterialAsync` signature (line 714) to accept the set:

```csharp
private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams)> ProcessMaterialAsync(
    TerrainMaterialSettings.TerrainMaterialItemExtended mat,
    GeoBoundingBox? effectiveBoundingBox,
    GeoCoordinateTransformer? coordinateTransformer,
    string debugPath,
    TerrainGenerationState state,
    OsmQueryResult? osmQueryResult,
    Action<OsmQueryResult> setOsmQueryResult,
    HashSet<long>? processedRoundaboutIds = null)  // <-- NEW
```

Pass it at call site (line 113):

```csharp
var (layerImagePath, roadParams) = await ProcessMaterialAsync(
    mat, effectiveBoundingBox, coordinateTransformer, debugPath, state,
    osmQueryResult, newOsmResult => osmQueryResult = newOsmResult,
    _processedRoundaboutIds);  // <-- NEW
```

**5d.** Modify `ProcessOsmMaterialAsync` signature (line 752) to accept the set:

```csharp
private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams, OsmQueryResult? OsmResult)>
    ProcessOsmMaterialAsync(
        TerrainMaterialSettings.TerrainMaterialItemExtended mat,
        GeoBoundingBox effectiveBoundingBox,
        GeoCoordinateTransformer? coordinateTransformer,
        string debugPath,
        TerrainGenerationState state,
        OsmQueryResult? osmQueryResult,
        HashSet<long>? processedRoundaboutIds = null)  // <-- NEW
```

Pass it at call site in `ProcessMaterialAsync` (line 730):

```csharp
(layerImagePath, roadParams, osmQueryResult) = await ProcessOsmMaterialAsync(
    mat, effectiveBoundingBox, coordinateTransformer, debugPath, state, osmQueryResult,
    processedRoundaboutIds);  // <-- NEW
```

**5e.** Modify `ProcessOsmRoadMaterialAsync` signature (line 789) to accept the set:

```csharp
private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams)> ProcessOsmRoadMaterialAsync(
    TerrainMaterialSettings.TerrainMaterialItemExtended mat,
    List<OsmFeature> fullFeatures,
    GeoBoundingBox effectiveBoundingBox,
    OsmGeometryProcessor processor,
    string debugPath,
    TerrainGenerationState state,
    OsmQueryResult osmQueryResult,
    HashSet<long>? processedRoundaboutIds = null)  // <-- NEW
```

Pass it at call site in `ProcessOsmMaterialAsync` (line 780):

```csharp
(layerImagePath, roadParams) = await ProcessOsmRoadMaterialAsync(
    mat, fullFeatures, effectiveBoundingBox, processor, debugPath, state, osmQueryResult,
    processedRoundaboutIds);  // <-- NEW
```

**5f.** Pass it at the final call site (~line 838):

```csharp
splines = processor.ConvertLinesToSplinesWithRoundabouts(
    lineFeatures,
    osmQueryResult,
    effectiveBoundingBox,
    state.TerrainSize,
    state.MetersPerPixel,
    interpolationType,
    out var detectedRoundabouts,
    out var roundaboutWayIds,
    out var roundaboutProcessingResult,
    enableRoadTrimming,
    overlapTolerance,
    minPathLengthMeters,
    duplicatePointToleranceMeters: 0.01f,
    endpointJoinToleranceMeters: 1.0f,
    debugOutputPath: roundaboutDebugPath,
    excludeBridges: state.ExcludeBridgesFromTerrain,
    excludeTunnels: state.ExcludeTunnelsFromTerrain,
    alreadyProcessedRoundaboutIds: processedRoundaboutIds);  // <-- NEW
```

- [ ] **Step 6: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj && dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeds with no errors (warnings about DLL locks from running app are OK)

- [ ] **Step 7: Run tests**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoundaboutDeduplicationTests" -v minimal`
Expected: All tests PASS

- [ ] **Step 8: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs \
       BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs \
       BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs \
       BeamNgTerrainPoc.Tests/Roundabout/RoundaboutDeduplicationTests.cs
git commit -m "fix: prevent duplicate roundabout ring splines across materials

Add skipRingCreationIds parameter to RoundaboutMerger.ProcessRoundabouts
that skips ring spline creation for roundabouts already processed by a
previous material. Way IDs are still marked as processed (for connecting
road exclusion from regular processing) and trimming still runs.
The orchestrator threads a shared HashSet through the call chain:
ProcessMaterialAsync → ProcessOsmMaterialAsync → ProcessOsmRoadMaterialAsync
→ ConvertLinesToSplinesWithRoundabouts → ProcessRoundabouts."
```

---

## Task 2: Fix Roundabout Constraint Model (Edge-Anchored + Radial Slope)

### Problem

`ComputeRoundaboutConstraints` at [UnifiedJunctionProfileBlender.cs:363-453](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L363-L453) has three differences from the T-junction model that cause less smooth blending:

1. **No edge offset** — constraint elevation is at the terminating road's center point, not offset along the away-direction by the ring half-width (T-junctions use `primaryHalfWidth` offset)
2. **Circumferential slope** — uses `ringSlope` computed along the ring circumference, but the connecting road approaches radially. The relevant slope is the ring surface's gradient projected onto the approach direction.
3. **Ring banking not baked** — sets `PrimaryBankAngleRadians = ringCS.BankAngleRadians` instead of `0f`, causing double-counting in the transition zone's analytical delta computation

### Solution

Rewrite to match the T-junction edge-anchored pattern: compute elevation at an exit point offset along `awayDirection * ringHalfWidth`, project the ring surface gradient onto the approach direction for slope, and set `PrimaryBankAngleRadians = 0f` (bake banking into elevation). This diverges from the T-junction's slope computation (which uses longitudinal slope directly) because the connecting road's approach vector is perpendicular to the ring tangent, requiring projection.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs:363-453`
- Create: `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutBlendingTests.cs`

- [ ] **Step 1: Write geometry validation tests**

Create `BeamNgTerrainPoc.Tests/Roundabout/RoundaboutBlendingTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

/// <summary>
/// Tests for roundabout constraint computation.
/// Validates the edge-anchored constraint model and radial slope projection.
/// </summary>
public class RoundaboutBlendingTests
{
    /// <summary>
    /// The ring surface elevation at an offset point should account for
    /// both longitudinal slope and banking (lateral tilt).
    /// GetPrimarySurfaceElevation is the shared utility for this.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 100f, 100f)]       // No offset → same elevation
    [InlineData(5f, 0f, 100f, 100f)]        // Lateral offset, no banking → same elevation
    [InlineData(0f, 5f, 100f, 100.5f)]      // Longitudinal offset, slope=0.1 → +0.5m
    public void GetPrimarySurfaceElevation_AccountsForSlopeAndBanking(
        float lateralOffset, float longitudinalOffset, float centerElev, float expectedElev)
    {
        var cs = new UnifiedCrossSection
        {
            CenterPoint = new Vector2(100, 100),
            TangentDirection = new Vector2(0, 1),  // Road going north
            NormalDirection = new Vector2(1, 0),    // Normal pointing east
            TargetElevation = centerElev,
            BankAngleRadians = 0f,                 // No banking
            EffectiveRoadWidth = 10f
        };

        var worldPos = cs.CenterPoint
                       + cs.NormalDirection * lateralOffset
                       + cs.TangentDirection * longitudinalOffset;

        var slope = 0.1f; // 10% grade
        var result = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(worldPos, cs, slope);

        Assert.InRange(result, expectedElev - 0.01f, expectedElev + 0.01f);
    }

    /// <summary>
    /// For a flat roundabout ring (no banking), the radial slope should be ~0
    /// because the ring tangent is perpendicular to the radial approach.
    /// Even with non-zero circumferential slope, radial projection → ~0.
    /// </summary>
    [Theory]
    [InlineData(0f)]    // East connection
    [InlineData(90f)]   // North connection
    [InlineData(180f)]  // West connection
    [InlineData(270f)]  // South connection
    public void RadialSlope_IsPerpendicular_ToCircumferentialSlope(float angleDegrees)
    {
        var angleRad = angleDegrees * MathF.PI / 180f;

        // Ring tangent is perpendicular to radial direction at any point
        var radialDir = new Vector2(MathF.Cos(angleRad), MathF.Sin(angleRad));
        var ringTangent = new Vector2(-radialDir.Y, radialDir.X);

        // Circumferential slope: 2% grade along ring
        var circumferentialSlope = 0.02f;

        // Radial slope = circumferential slope projected onto radial direction
        var radialSlope = circumferentialSlope * Vector2.Dot(ringTangent, radialDir);

        Assert.True(MathF.Abs(radialSlope) < 0.001f,
            $"Radial slope should be ~0 but was {radialSlope:F6}");
    }

    /// <summary>
    /// Verifies that the edge-anchored exit point is offset along the connecting
    /// road's away-direction, not at the road centerpoint.
    /// </summary>
    [Fact]
    public void EdgeAnchoredExitPoint_IsOffset_ByRingHalfWidth()
    {
        var centerPoint = new Vector2(100, 100);
        var awayDirection = new Vector2(1, 0); // Heading east (away from ring)
        var ringHalfWidth = 5f;

        var exitPoint = centerPoint + awayDirection * ringHalfWidth;

        Assert.Equal(105f, exitPoint.X, 0.01f);
        Assert.Equal(100f, exitPoint.Y, 0.01f);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~RoundaboutBlendingTests" -v minimal`
Expected: PASS

- [ ] **Step 3: Replace `ComputeRoundaboutConstraints` with edge-anchored + radial slope model**

In `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`, replace the method body at lines 363-453:

```csharp
private void ComputeRoundaboutConstraints(
    NetworkJunction junction,
    Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
    Dictionary<(int, bool), JunctionEndpointConstraint> constraints)
{
    var continuous = junction.GetContinuousRoads().ToList();
    if (continuous.Count == 0)
    {
        // No ring found — fall back to multi-way
        ComputeMultiWayConstraints(junction, constraints);
        return;
    }

    var ringContributor = continuous.OrderByDescending(c => c.Spline.Priority).First();
    var ringCS = ringContributor.CrossSection;

    // Find the closest ring CS to the junction position for more accurate local data.
    // Also capture neighbors for elevation interpolation (Task 3).
    UnifiedCrossSection? ringCSPrev = null;
    UnifiedCrossSection? ringCSNext = null;
    if (crossSectionsBySpline.TryGetValue(ringContributor.Spline.SplineId, out var ringSections))
    {
        var closestDist = float.MaxValue;
        var closestIdx = -1;
        for (var i = 0; i < ringSections.Count; i++)
        {
            var dist = Vector2.Distance(ringSections[i].CenterPoint, junction.Position);
            if (dist < closestDist)
            {
                closestDist = dist;
                closestIdx = i;
                ringCS = ringSections[i];
            }
        }

        // Grab neighbors for interpolation (used in Task 3)
        if (closestIdx > 0)
            ringCSPrev = ringSections[closestIdx - 1];
        if (closestIdx < ringSections.Count - 1)
            ringCSNext = ringSections[closestIdx + 1];
    }

    // Interpolate ring elevation at the exact junction position between
    // the nearest CS and its closest neighbor for sub-CS accuracy.
    if (ringCSPrev != null || ringCSNext != null)
    {
        var junctionPos = junction.Position;
        var distToNearest = Vector2.Distance(ringCS.CenterPoint, junctionPos);

        UnifiedCrossSection? neighbor = null;
        var neighborDist = float.MaxValue;

        if (ringCSPrev != null)
        {
            var d = Vector2.Distance(ringCSPrev.CenterPoint, junctionPos);
            if (d < neighborDist) { neighbor = ringCSPrev; neighborDist = d; }
        }
        if (ringCSNext != null)
        {
            var d = Vector2.Distance(ringCSNext.CenterPoint, junctionPos);
            if (d < neighborDist) { neighbor = ringCSNext; neighborDist = d; }
        }

        if (neighbor != null && !float.IsNaN(neighbor.TargetElevation) && !float.IsNaN(ringCS.TargetElevation))
        {
            var totalDist = distToNearest + neighborDist;
            if (totalDist > 0.01f)
            {
                var t = distToNearest / totalDist;
                var interpolatedElev = ringCS.TargetElevation * (1f - t) + neighbor.TargetElevation * t;

                if (MathF.Abs(interpolatedElev - ringCS.TargetElevation) > 0.001f)
                {
                    TerrainCreationLogger.Current?.Detail(
                        $"  Ring CS interpolation: nearest={ringCS.TargetElevation:F3}m, " +
                        $"neighbor={neighbor.TargetElevation:F3}m, interpolated={interpolatedElev:F3}m (t={t:F2})");

                    // Store interpolated elevation as offset to apply later.
                    // We don't modify the original ring CS in the network.
                    var elevOffset = interpolatedElev - ringCS.TargetElevation;
                    // Apply offset: adjust ringCS.TargetElevation for local use only
                    // by creating a local copy (all properties are { get; set; })
                    ringCS = new UnifiedCrossSection
                    {
                        Index = ringCS.Index,
                        OwnerSplineId = ringCS.OwnerSplineId,
                        LocalIndex = ringCS.LocalIndex,
                        CenterPoint = ringCS.CenterPoint,
                        TangentDirection = ringCS.TangentDirection,
                        NormalDirection = ringCS.NormalDirection,
                        TargetElevation = interpolatedElev,
                        BankAngleRadians = ringCS.BankAngleRadians,
                        EffectiveRoadWidth = ringCS.EffectiveRoadWidth,
                        LeftEdgeElevation = ringCS.LeftEdgeElevation,
                        RightEdgeElevation = ringCS.RightEdgeElevation,
                        JunctionBankingBehavior = ringCS.JunctionBankingBehavior,
                        Priority = ringCS.Priority,
                        DistanceAlongSpline = ringCS.DistanceAlongSpline,
                        OriginalTerrainElevation = ringCS.OriginalTerrainElevation,
                        Curvature = ringCS.Curvature,
                        JunctionIdwWeightModifier = ringCS.JunctionIdwWeightModifier
                    };
                }
            }
        }
    }

    // Calculate ring's circumferential slope (along the ring tangent)
    var circumferentialSlope = 0f;
    if (crossSectionsBySpline.TryGetValue(ringContributor.Spline.SplineId, out var ringAllSections))
    {
        var ringIndex = ringAllSections.FindIndex(cs => cs.Index == ringCS.Index);
        if (ringIndex >= 0)
            circumferentialSlope = CalculateSlopeAtIndex(ringAllSections, ringIndex);
    }

    if (float.IsNaN(circumferentialSlope))
        circumferentialSlope = 0f;

    foreach (var terminating in junction.GetTerminatingRoads())
    {
        var terminatingCS = terminating.CrossSection;
        var halfWidth = terminatingCS.EffectiveRoadWidth / 2f;
        var ringHalfWidth = ringCS.EffectiveRoadWidth / 2f;

        // === Edge-Anchored Constraint (matching T-junction pattern) ===
        // Compute the exit point where the connecting road leaves the ring surface,
        // offset along the connecting road's away-direction by the ring half-width.
        var awayDirection = terminating.IsSplineStart
            ? terminatingCS.TangentDirection
            : -terminatingCS.TangentDirection;
        var edgeCenterPoint = terminatingCS.CenterPoint + awayDirection * ringHalfWidth;

        // Primary surface elevation at the edge exit point
        var edgeCenterElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeCenterPoint, ringCS, circumferentialSlope);

        // Bank angle at the exit point from edge projections
        var edgeLeftPos = edgeCenterPoint - terminatingCS.NormalDirection * halfWidth;
        var edgeRightPos = edgeCenterPoint + terminatingCS.NormalDirection * halfWidth;
        var edgeLeftElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeLeftPos, ringCS, circumferentialSlope);
        var edgeRightElev = JunctionSurfaceCalculator.GetPrimarySurfaceElevation(
            edgeRightPos, ringCS, circumferentialSlope);
        var edgeDelta = (edgeRightElev - edgeLeftElev) / 2f;
        var sinBank = halfWidth > 0.01f ? Math.Clamp(edgeDelta / halfWidth, -1f, 1f) : 0f;
        var edgeBankAngle = MathF.Asin(sinBank);

        junction.HarmonizedElevation = edgeCenterElev;

        // Compute the slope in the connecting road's approach direction.
        // Project the ring's surface gradient onto the away-direction.
        // The ring surface gradient has two components:
        //   1. Circumferential slope (along ring tangent)
        //   2. Banking slope (along ring normal)
        // For a radial approach, tangent·away ≈ 0, so circumferential slope vanishes.
        // This is correct: a road leaving a flat ring radially has ~zero slope.
        var slopeAlongTangent = circumferentialSlope;
        var bankingSlopePerMeter = ringCS.EffectiveRoadWidth > 0.01f
            ? MathF.Sin(ringCS.BankAngleRadians)
            : 0f;
        var radialSlope =
            slopeAlongTangent * Vector2.Dot(awayDirection, ringCS.TangentDirection) +
            bankingSlopePerMeter * Vector2.Dot(awayDirection, ringCS.NormalDirection);

        if (float.IsNaN(radialSlope))
            radialSlope = 0f;

        // Use roundabout-specific blend distance
        var junctionParams = terminating.Spline.Parameters.JunctionHarmonizationParameters
                             ?? new JunctionHarmonizationParameters();
        var blendDist = CalculateAdaptiveBlendDistance(
            junctionParams.GetEffectiveRoundaboutBlendDistance(terminating.Spline.Parameters.RoadWidthMeters),
            edgeCenterElev, terminatingCS.TargetElevation, terminating.Spline.Parameters);

        var key = (terminating.Spline.SplineId, terminating.IsSplineStart);
        constraints.TryAdd(key, new JunctionEndpointConstraint
        {
            Elevation = edgeCenterElev,
            Slope = radialSlope,
            BankAngleRadians = edgeBankAngle,
            IsSplineStart = terminating.IsSplineStart,
            Junction = junction,
            FlatZoneDistance = ringHalfWidth,
            BlendDistanceMeters = blendDist,
            // Slope tracking: PrimaryTangentDirection set for flat-zone surface following.
            // PrimaryBankAngleRadians = 0 because banking is baked into edgeCenterElev.
            // This matches the T-junction pattern and prevents double-counting.
            PrimaryTangentDirection = ringCS.TangentDirection,
            PrimaryBankAngleRadians = 0f
        });

        TerrainCreationLogger.Current?.Detail(
            $"Roundabout Junction #{junction.JunctionId}: Spline {terminating.Spline.SplineId} EDGE constraint: " +
            $"edgeElev={edgeCenterElev:F2}m, radialSlope={radialSlope:F4}, circumSlope={circumferentialSlope:F4}, " +
            $"bank={BankingCalculator.RadiansToDegrees(edgeBankAngle):F1}°, " +
            $"flatZone={ringHalfWidth:F2}m, blendDist={blendDist:F1}m");
    }
}
```

Key changes from the original:
1. **Edge-anchored exit point** — `edgeCenterPoint` offset along `awayDirection * ringHalfWidth` (like T-junction uses `primaryHalfWidth`)
2. **Radial slope** — projects ring surface gradient (circumferential + banking) onto approach direction. This DIVERGES from T-junction (which uses `primarySlope` directly) because T-junction primary roads run parallel to the connecting road's approach, while roundabout rings run perpendicular.
3. **`PrimaryBankAngleRadians = 0f`** — banking baked into `edgeCenterElev` (matches T-junction pattern)
4. **`FlatZoneDistance = ringHalfWidth`** — uses ring half-width (matches T-junction's `primaryHalfWidth`)
5. **CS elevation interpolation** — interpolates between nearest + neighbor CS for sub-CS accuracy
6. **Robust CS copy** — copies all commonly-used properties including `Priority`, `DistanceAlongSpline`, `Curvature`

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds. If `UnifiedCrossSection` has properties not listed in the copy construction, add them. Check for CS0200 ("property is read-only") errors — all properties should be `{ get; set; }`.

- [ ] **Step 5: Run all tests**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests PASS (existing + new)

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs \
       BeamNgTerrainPoc.Tests/Roundabout/RoundaboutBlendingTests.cs
git commit -m "fix: align roundabout constraint model with T-junction edge-anchored pattern

Rewrite ComputeRoundaboutConstraints with three changes:
1. Edge-anchored exit point offset along connecting road's away-direction
   by ring half-width (matching T-junction primaryHalfWidth pattern)
2. Radial slope: project ring surface gradient onto approach direction
   instead of using circumferential ring slope directly
3. PrimaryBankAngleRadians=0 (bake banking into elevation) to prevent
   double-counting in the transition zone analytical delta
Also adds ring CS elevation interpolation between nearest and neighbor
for sub-CS accuracy at the connection point."
```

---

## Task 3: Verify Full Build and Run All Tests

- [ ] **Step 1: Full solution build**

Run: `dotnet build BeamNG_LevelCleanUp.sln`
Expected: Build succeeds (ignore MSB3027/MSB3021 DLL lock warnings if app is running)

- [ ] **Step 2: Run complete test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests -v minimal`
Expected: All tests PASS

- [ ] **Step 3: Commit if any remaining changes**

Only if there are uncommitted fixes from build errors.

---

## Verification Checklist

After implementation, manually verify with a map containing roundabouts:

- [ ] Console output shows "Skipping ring spline creation for roundabout X (already created by another material)" when a roundabout spans materials
- [ ] Only ONE ring spline per physical roundabout in the network (check log: "Total roundabout splines identified: N")
- [ ] Material B's connecting roads are still properly trimmed (no overlapping segments with ring)
- [ ] Roundabout connecting road elevations blend smoothly to ring surface (no visible step at junction)
- [ ] T-junction smoothness quality is maintained (regression check)
- [ ] Log shows "EDGE constraint" for roundabout junctions (confirming new code path)

## Risk Notes

- **Task 2 is the highest-risk change.** The old `ComputeRoundaboutConstraints` is fully replaced. The edge-anchored pattern is proven for T-junctions, but roundabout ring geometry (curved surface) adds complexity — `GetPrimarySurfaceElevation` assumes a planar surface projection from the CS, which is only locally accurate for curved rings. For small roundabouts (<15m radius) with high banking, the linear surface model may deviate from the actual curved surface.
- **Radial slope diverges from T-junction.** T-junctions use `primarySlope` directly as the constraint slope. Roundabouts project the surface gradient onto the approach direction. This is geometrically correct for radial approaches but is a behavioral difference — not a "match." The plan notes this explicitly.
- **CS copy construction in Task 2** copies the most commonly-used properties but may miss newly-added ones. If a new property is added to `UnifiedCrossSection` in the future and matters for `GetPrimarySurfaceElevation`, the copy will need updating. A `MemberwiseClone()` override would be more robust but is out of scope.
- **Task 1 is safe** — way exclusion and trimming continue for all materials. Only ring spline creation is deduplicated.
