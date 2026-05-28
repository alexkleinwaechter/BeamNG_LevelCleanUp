# Junction Elevation Pinning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Phase 1.9 step to `UnifiedRoadSmoother` that pins junction `HarmonizedElevation` before road smoothing, so terminating roads ramp into a fixed Z while continuous roads slope across it untouched — eliminating Phase-3 iterative correction at the junction and the ditch/kink artefacts it produced.

**Architecture:** A new `JunctionElevationPinner` class is called between Phase 1.8 (`DetectJunctions`) and Phase 2 (`CalculateNetworkElevations`). It sets `junction.HarmonizedElevation` for `Endpoint`, `TJunction`, `YJunction`, `CrossRoads`, `Complex`; leaves `MidSplineCrossing`, `Roundabout`, `Continuation` as `NaN` (their existing handlers run). Three downstream consumer touchpoints (`BuildEndpointAnchorLookup`, `NetworkJunctionHarmonizer.ComputeJunctionElevations`, `UnifiedJunctionProfileBlender`'s 6 writes to `HarmonizedElevation`) are made NaN-aware so a Phase 1.9 pin survives into Phase 3. Behaviour is bit-identical when the feature flag is off — no junction is pinned, every NaN guard falls through.

**Tech Stack:** .NET 9 / C# / xUnit 2.9.2. No new dependencies. Tests under `BeamNgTerrainPoc.Tests/` mirror existing `Junction/` and `Elevation/` folder layout. Build via `dotnet build BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`; run with `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`.

**Spec:** [2026-05-14-junction-elevation-pinning-design.md](2026-05-14-junction-elevation-pinning-design.md)

---

## File Structure

**New files**
- `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs` — pure function over `(UnifiedRoadNetwork, float[,], float, JunctionHarmonizationParameters)` that writes `HarmonizedElevation`. ~150 lines.
- `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs` — W1 harness: per-junction residual CSV, three-band heatmap PNG, `w`-test summary, ±d quadratic-growth check, aggregate stats. ~250 lines.
- `BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs` — unit tests for the pinner. ~200 lines.
- `BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs` — unit tests for the W1 math (three-band classifier, w-test, quadratic growth check). ~150 lines.

**Modified files**
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — add 3 new flags + 1 float parameter.
- `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` — Phase 1.9 call site; C1a (call argument); C1b (gate the `JunctionType.Endpoint` early-out); validation-exporter call site.
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs` — C2 (early-`continue` when `HarmonizedElevation` is non-NaN).
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs` — C3 (NaN-guard the 6 writes to `HarmonizedElevation`); W2 grade-skip; W3 max-grade clamp.

**Files NOT touched** (verified during plan write-up — these are correctly assumed unchanged)
- `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` — `ApplyEndpointAnchoring` already accepts arbitrary anchor Z via the existing `EndpointAnchor` record.
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs` — `HarmonizedElevation` field already exists (line 118).
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/JunctionEndpointConstraint.cs` — `Elevation` and `Slope` fields already exist; slope continues to be populated by the existing `CalculateSlopeAtIndex` call in the blender (line 368) reading the (Phase-1.9-aware) primary CS profile. No model change needed.

---

## Phase A — Step 0: W1 Validation Harness

The harness must exist before any baseline capture so Steps 0–3 measure with the same instrument.

### Task A1: `JunctionPinningValidationExporter` skeleton + integration

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (add call site at end of pipeline, near L439)

- [ ] **Step 1: Create the skeleton class with a single entry point.**

```csharp
// BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     W1 validation harness for Phase 1.9 junction pinning.
///     Emits per-junction residual CSV, three-band heatmap PNG, w-test summary,
///     ±d quadratic-growth check rows, and aggregate stats — all to MT_TerrainGeneration/.
///     Thresholds and statistical model from Oude Elberink &amp; Vosselman 2007.
/// </summary>
public static class JunctionPinningValidationExporter
{
    public record AggregateStats(
        int JunctionCount,
        float PinResidualMean,
        float PinResidualSigma,
        float PinResidualMaxAbs,
        int WTestOutliersGt3,
        long RedBandPixelCount);

    public static AggregateStats Export(
        UnifiedRoadNetwork network,
        float[,] modifiedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var redBandCount = ExportThreeBandHeatmap(
            modifiedHeightMap, originalHeightMap,
            Path.Combine(outputDirectory, "delta_three_band.png"));

        var residualStats = ExportJunctionResidualsCsv(
            network, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "junction_residuals.csv"));

        var wStats = ExportWTestSummary(
            network, modifiedHeightMap, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "w_test_summary.csv"));

        ExportQuadraticGrowthCsv(
            network, modifiedHeightMap, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "quadratic_growth.csv"));

        return new AggregateStats(
            JunctionCount: residualStats.Count,
            PinResidualMean: residualStats.Mean,
            PinResidualSigma: residualStats.Sigma,
            PinResidualMaxAbs: residualStats.MaxAbs,
            WTestOutliersGt3: wStats.OutliersGt3,
            RedBandPixelCount: redBandCount);
    }

    private static long ExportThreeBandHeatmap(
        float[,] modified, float[,] original, string path) => 0; // Task A2

    private record ResidualStats(int Count, float Mean, float Sigma, float MaxAbs);
    private static ResidualStats ExportJunctionResidualsCsv(
        UnifiedRoadNetwork network, float[,] original, float metersPerPixel, string path)
        => new(0, 0f, 0f, 0f); // Task A3

    private record WTestStats(int OutliersGt3);
    private static WTestStats ExportWTestSummary(
        UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
        => new(0); // Task A4

    private static void ExportQuadraticGrowthCsv(
        UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
    { } // Task A5
}
```

- [ ] **Step 2: Add integration call in `UnifiedRoadSmoother`.**

The existing `ExportJunctionDebugImageIfRequested` method (UnifiedRoadSmoother.cs:1037-1082, verified) shows the exact accessor chain to copy: `materialWithJunctionDebug.RoadParameters?.JunctionHarmonizationParameters?.ExportJunctionDebugImage` and `materialWithJunctionDebug.RoadParameters!.DebugOutputDirectory`. Mirror it.

Locate the call at L439:

```csharp
ExportJunctionDebugImageIfRequested(network, lastHarmonizationResult, heightMap, metersPerPixel, roadMaterials);
```

The W1 exporter needs the *original* heightmap too (for the modified−original delta). The original is not directly available at L439 — `ExportDebugImagesIfRequested` at L1089 receives both `smoothedHeightMap` and `originalHeightMap`. Place the new call *there* instead, alongside the per-material debug images. Locate `ExportDebugImagesIfRequested` (L1089) and add the call as the first action after `mainDebugDir` is computed:

```csharp
// W1 — Phase 1.9 validation harness (gated on ExportJunctionDebugImage like the legacy debug image).
ExportJunctionPinningValidationIfRequested(network, smoothedHeightMap, originalHeightMap, metersPerPixel, roadMaterials);
```

Then add this method near the existing `ExportJunctionDebugImageIfRequested` definition (~L1037), copying the accessor chain verbatim:

```csharp
private void ExportJunctionPinningValidationIfRequested(
    UnifiedRoadNetwork network,
    float[,] smoothedHeightMap,
    float[,] originalHeightMap,
    float metersPerPixel,
    List<MaterialDefinition> roadMaterials)
{
    var materialWithJunctionDebug = roadMaterials.FirstOrDefault(m =>
        m.RoadParameters?.JunctionHarmonizationParameters?.ExportJunctionDebugImage == true);

    if (materialWithJunctionDebug == null) return;

    try
    {
        var materialDebugDir = materialWithJunctionDebug.RoadParameters!.DebugOutputDirectory ?? ".";
        var mainDebugDir = Path.GetDirectoryName(materialDebugDir);
        if (string.IsNullOrEmpty(mainDebugDir)) mainDebugDir = materialDebugDir;

        var stats = JunctionPinningValidationExporter.Export(
            network, smoothedHeightMap, originalHeightMap, metersPerPixel, mainDebugDir);

        TerrainCreationLogger.Current?.Detail(
            $"W1 validation: n={stats.JunctionCount}, pinResMean={stats.PinResidualMean:F3}m, " +
            $"pinResSigma={stats.PinResidualSigma:F3}m, pinResMaxAbs={stats.PinResidualMaxAbs:F3}m, " +
            $"wTestOutliers={stats.WTestOutliersGt3}, redBandPixels={stats.RedBandPixelCount}");
    }
    catch (Exception ex)
    {
        TerrainLogger.Warning($"Failed to export W1 validation harness: {ex.Message}");
    }
}
```

- [ ] **Step 3: Build to verify the integration compiles.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds. Each `Export*` private returns a default — no behaviour change.

- [ ] **Step 4: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs \
        BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat: scaffold JunctionPinningValidationExporter (W1 harness, Task A1)"
```

---

### Task A2: Three-band heatmap PNG

Bands: green `|Δ| < 0.2 m`, yellow `< 0.5 m`, red `≥ 0.5 m`. Oude Elberink &amp; Vosselman 2007 Fig 9 thresholds. Return red-pixel count for aggregate stats.

**Files:**
- Create: `BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs` (replace `ExportThreeBandHeatmap` stub)

- [ ] **Step 1: Write the failing test for the band classifier.**

```csharp
// BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Junction;

public class JunctionPinningValidationExporterTests
{
    [Theory]
    [InlineData(0.0f, JunctionPinningValidationExporter.DeltaBand.Green)]
    [InlineData(0.19f, JunctionPinningValidationExporter.DeltaBand.Green)]
    [InlineData(0.20f, JunctionPinningValidationExporter.DeltaBand.Yellow)]
    [InlineData(0.49f, JunctionPinningValidationExporter.DeltaBand.Yellow)]
    [InlineData(0.50f, JunctionPinningValidationExporter.DeltaBand.Red)]
    [InlineData(1.50f, JunctionPinningValidationExporter.DeltaBand.Red)]
    [InlineData(-0.30f, JunctionPinningValidationExporter.DeltaBand.Yellow)] // negatives use |Δ|
    [InlineData(-0.60f, JunctionPinningValidationExporter.DeltaBand.Red)]
    public void ClassifyBand_UsesAbsoluteThresholds_0p2_0p5(float delta, JunctionPinningValidationExporter.DeltaBand expected)
    {
        Assert.Equal(expected, JunctionPinningValidationExporter.ClassifyBand(delta));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ClassifyBand"`
Expected: FAIL — `DeltaBand` and `ClassifyBand` do not yet exist.

- [ ] **Step 3: Implement `ClassifyBand` and `DeltaBand` (minimum to pass).**

In `JunctionPinningValidationExporter.cs`, replace the stub `ExportThreeBandHeatmap` with the band classifier first:

```csharp
public enum DeltaBand { Green, Yellow, Red }

public static DeltaBand ClassifyBand(float delta)
{
    var abs = MathF.Abs(delta);
    if (abs < 0.20f) return DeltaBand.Green;
    if (abs < 0.50f) return DeltaBand.Yellow;
    return DeltaBand.Red;
}
```

- [ ] **Step 4: Run the test to verify it passes.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ClassifyBand"`
Expected: PASS (8/8).

- [ ] **Step 5: Implement `ExportThreeBandHeatmap` (the PNG writer).**

The project uses `SixLabors.ImageSharp` (verified via `NetworkJunctionHarmonizer.ExportJunctionDebugImage` at L791-889: `using var image = new Image<Rgba32>(...)`, `image.SaveAsPng(outputPath)`). Mirror that style — do NOT add `System.Drawing`.

Add the using directives at the top of `JunctionPinningValidationExporter.cs`:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
```

Then replace the stub `private static long ExportThreeBandHeatmap(...)`:

```csharp
private static long ExportThreeBandHeatmap(float[,] modified, float[,] original, string path)
{
    var h = modified.GetLength(0);
    var w = modified.GetLength(1);
    using var image = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 255));
    long redCount = 0;

    var green = new Rgba32(40, 180, 60, 255);
    var yellow = new Rgba32(230, 200, 30, 255);
    var red = new Rgba32(220, 40, 40, 255);
    var black = new Rgba32(0, 0, 0, 255);

    image.ProcessPixelRows(accessor =>
    {
        for (var y = 0; y < h; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < w; x++)
            {
                var m = modified[y, x];
                var o = original[y, x];
                if (float.IsNaN(m) || float.IsNaN(o))
                {
                    row[x] = black;
                    continue;
                }
                var band = ClassifyBand(m - o);
                row[x] = band switch
                {
                    DeltaBand.Green => green,
                    DeltaBand.Yellow => yellow,
                    DeltaBand.Red => red,
                    _ => black
                };
                if (band == DeltaBand.Red) redCount++;
            }
        }
    });

    image.SaveAsPng(path);
    return redCount;
}
```

- [ ] **Step 6: Build + run all tests to confirm nothing else breaks.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All tests pass (8 new in this task + existing suite green).

- [ ] **Step 7: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs
git commit -m "feat: three-band heatmap classifier and PNG writer (Task A2)"
```

---

### Task A3: Per-junction residual CSV

For each `NetworkJunction` not `IsExcluded`: `junction_id, type, position_x, position_y, pinned_z, terrain_z, max_contributor_z, min_contributor_z, mean_contributor_z, residual_pinned_minus_terrain, residual_max_minus_min, n_contributors`.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs`
- Modify: `BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs`

- [ ] **Step 1: Write failing test for residual aggregation math.**

```csharp
[Fact]
public void ComputeResidualStats_OnFixedDeltaList_ReportsMeanSigmaMaxAbs()
{
    // Clean dataset: mean = 0, σ² = (4+1+0+1+4)/5 = 2 → σ = √2 ≈ 1.4142, maxAbs = 2.
    var residuals = new List<float> { -2f, -1f, 0f, 1f, 2f };
    var stats = JunctionPinningValidationExporter.ComputeResidualStats(residuals);

    Assert.Equal(5, stats.Count);
    Assert.Equal(0f, stats.Mean, 3);
    Assert.Equal(MathF.Sqrt(2f), stats.Sigma, 3);
    Assert.Equal(2f, stats.MaxAbs);
}

[Fact]
public void ComputeResidualStats_EmptyInput_ReturnsZeros()
{
    var stats = JunctionPinningValidationExporter.ComputeResidualStats(new List<float>());
    Assert.Equal(0, stats.Count);
    Assert.Equal(0f, stats.Mean);
    Assert.Equal(0f, stats.Sigma);
    Assert.Equal(0f, stats.MaxAbs);
}
```

- [ ] **Step 2: Run test to verify fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ComputeResidualStats"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement `ComputeResidualStats` and make the `ResidualStats` record public for tests.**

In `JunctionPinningValidationExporter.cs`:

```csharp
public record ResidualStats(int Count, float Mean, float Sigma, float MaxAbs);

public static ResidualStats ComputeResidualStats(IReadOnlyList<float> residuals)
{
    if (residuals.Count == 0) return new ResidualStats(0, 0f, 0f, 0f);
    var mean = residuals.Average();
    var sumSq = residuals.Sum(r => (r - mean) * (r - mean));
    var sigma = MathF.Sqrt(sumSq / residuals.Count); // population σ
    var maxAbs = residuals.Max(MathF.Abs);
    return new ResidualStats(residuals.Count, mean, sigma, maxAbs);
}
```

(Replace the previous private `ResidualStats` record stub with the public version. Delete the old `private record ResidualStats` line.)

- [ ] **Step 4: Run test to verify pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ComputeResidualStats"`
Expected: PASS (2/2).

- [ ] **Step 5: Implement `ExportJunctionResidualsCsv` using the math.**

Replace the stub `private static ResidualStats ExportJunctionResidualsCsv(...)`:

```csharp
private static ResidualStats ExportJunctionResidualsCsv(
    UnifiedRoadNetwork network, float[,] original, float metersPerPixel, string path)
{
    var mapHeight = original.GetLength(0);
    var mapWidth = original.GetLength(1);
    var residuals = new List<float>();

    using var writer = new StreamWriter(path);
    writer.WriteLine("junction_id,type,position_x,position_y,pinned_z,terrain_z," +
                     "max_contributor_z,min_contributor_z,mean_contributor_z," +
                     "residual_pinned_minus_terrain,residual_max_minus_min,n_contributors");

    foreach (var j in network.Junctions.Where(j => !j.IsExcluded))
    {
        var px = Math.Clamp((int)(j.Position.X / metersPerPixel), 0, mapWidth - 1);
        var py = Math.Clamp((int)(j.Position.Y / metersPerPixel), 0, mapHeight - 1);
        var terrainZ = original[py, px];
        var pinned = j.HarmonizedElevation;

        var contribElevs = j.Contributors
            .Select(c => c.CrossSection.TargetElevation)
            .Where(z => !float.IsNaN(z))
            .ToList();
        var maxZ = contribElevs.Count > 0 ? contribElevs.Max() : float.NaN;
        var minZ = contribElevs.Count > 0 ? contribElevs.Min() : float.NaN;
        var meanZ = contribElevs.Count > 0 ? contribElevs.Average() : float.NaN;

        var resPinTerr = float.IsNaN(pinned) || float.IsNaN(terrainZ) ? float.NaN : pinned - terrainZ;
        var resMaxMin = contribElevs.Count > 0 ? maxZ - minZ : float.NaN;

        writer.WriteLine(
            $"{j.JunctionId},{j.Type},{j.Position.X:F2},{j.Position.Y:F2}," +
            $"{pinned:F3},{terrainZ:F3},{maxZ:F3},{minZ:F3},{meanZ:F3}," +
            $"{resPinTerr:F3},{resMaxMin:F3},{j.Contributors.Count}");

        if (!float.IsNaN(resPinTerr)) residuals.Add(resPinTerr);
    }

    return ComputeResidualStats(residuals);
}
```

- [ ] **Step 6: Build + run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 7: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs
git commit -m "feat: per-junction residual CSV with mean/sigma/maxAbs (Task A3)"
```

---

### Task A4: `w`-test summary

`w = |Δtangent_angle_deg| / σ_predicted`. At each junction with non-NaN `HarmonizedElevation`, for each terminating contributor, sample modified-heightmap elevation along the contributor's centerline at the junction node and at `BlendDistanceMeters * 1.05` past the ramp's far end. Compute the tangent angle delta (using the elevation gradient) and divide by `σ_predicted` (default `1.0°`, lifted to `2.0°` for `motorway`/`trunk` per AASHTO higher gentleness).

Outputs a CSV: `junction_id, spline_id, is_start, tangent_at_node_deg, tangent_past_ramp_deg, delta_deg, sigma_predicted_deg, w`. Returns the count of `|w| > 3`.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs`
- Modify: `BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs`

- [ ] **Step 1: Write failing test for the `w`-test math.**

```csharp
[Theory]
[InlineData(0.0f, 1.0f, 0.0f)]    // no delta → w = 0
[InlineData(3.0f, 1.0f, 3.0f)]    // 3°/1° → w = 3 (boundary)
[InlineData(-3.0f, 1.0f, 3.0f)]   // sign ignored
[InlineData(4.0f, 2.0f, 2.0f)]    // motorway σ
[InlineData(1.5f, 1.0f, 1.5f)]
public void ComputeWStatistic_AbsoluteDeltaOverSigma(float deltaDeg, float sigma, float expected)
{
    var w = JunctionPinningValidationExporter.ComputeWStatistic(deltaDeg, sigma);
    Assert.Equal(expected, w, 3);
}

[Fact]
public void GetSigmaPredictedDeg_ByRoadClass()
{
    Assert.Equal(2.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg("motorway"));
    Assert.Equal(2.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg("trunk"));
    Assert.Equal(2.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg("motorway_link"));
    Assert.Equal(1.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg("primary"));
    Assert.Equal(1.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg("residential"));
    Assert.Equal(1.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg(null));
    Assert.Equal(1.0f, JunctionPinningValidationExporter.GetSigmaPredictedDeg(""));
}
```

- [ ] **Step 2: Run test to verify fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ComputeWStatistic|FullyQualifiedName~GetSigmaPredictedDeg"`
Expected: FAIL — methods do not exist.

- [ ] **Step 3: Implement the two helpers.**

In `JunctionPinningValidationExporter.cs`:

```csharp
public static float ComputeWStatistic(float deltaDeg, float sigmaDeg)
{
    if (sigmaDeg <= 0f) return 0f;
    return MathF.Abs(deltaDeg) / sigmaDeg;
}

public static float GetSigmaPredictedDeg(string? osmRoadType)
{
    if (string.IsNullOrEmpty(osmRoadType)) return 1.0f;
    return osmRoadType switch
    {
        "motorway" or "motorway_link" or "trunk" or "trunk_link" => 2.0f,
        _ => 1.0f
    };
}
```

- [ ] **Step 4: Run test to verify pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ComputeWStatistic|FullyQualifiedName~GetSigmaPredictedDeg"`
Expected: PASS (12/12 across both tests).

- [ ] **Step 5: Implement `ExportWTestSummary` using the helpers.**

Replace the stub:

```csharp
private static WTestStats ExportWTestSummary(
    UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
{
    var mapHeight = modified.GetLength(0);
    var mapWidth = modified.GetLength(1);
    var outliers = 0;

    using var writer = new StreamWriter(path);
    writer.WriteLine("junction_id,spline_id,is_start,tangent_at_node_deg,tangent_past_ramp_deg," +
                     "delta_deg,sigma_predicted_deg,w");

    foreach (var j in network.Junctions.Where(j => !j.IsExcluded && !float.IsNaN(j.HarmonizedElevation)))
    {
        foreach (var c in j.Contributors.Where(c => c.IsEndpoint))
        {
            var sigma = GetSigmaPredictedDeg(c.Spline.OsmRoadType);
            var blendDist = c.Spline.Parameters.JunctionHarmonizationParameters
                ?.GetEffectiveBlendDistance(c.Spline.Parameters.RoadWidthMeters) ?? 30f;

            var nodeAngle = SampleTangentAngleDeg(modified, metersPerPixel, c, 0f);
            var pastAngle = SampleTangentAngleDeg(modified, metersPerPixel, c, blendDist * 1.05f);
            var delta = pastAngle - nodeAngle;
            var w = ComputeWStatistic(delta, sigma);
            if (w > 3f) outliers++;

            writer.WriteLine(
                $"{j.JunctionId},{c.Spline.SplineId},{c.IsSplineStart}," +
                $"{nodeAngle:F2},{pastAngle:F2},{delta:F2},{sigma:F2},{w:F2}");
        }
    }
    return new WTestStats(outliers);
}

private static float SampleTangentAngleDeg(
    float[,] heightMap, float metersPerPixel, JunctionContributor c, float distanceFromNodeMeters)
{
    // Sample two heightmap points 2 m apart centered on the position at distanceFromNodeMeters
    // along the contributor's spline; compute the rise/run and convert to degrees.
    // RoadSpline's public API is GetPointAtDistance(float distance) → Vector2 and TotalLength (float).
    // (Verified in BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs:90, 100, 196.)
    var spline = c.Spline.Spline;
    var totalLen = spline.TotalLength;

    float SignedDist(float d) => c.IsSplineStart ? d : totalLen - d;

    var distBefore = MathF.Max(0f, distanceFromNodeMeters - 1f);
    var distAfter = MathF.Min(totalLen, distanceFromNodeMeters + 1f);

    var posBefore = spline.GetPointAtDistance(SignedDist(distBefore));
    var posAfter = spline.GetPointAtDistance(SignedDist(distAfter));

    var mapH = heightMap.GetLength(0);
    var mapW = heightMap.GetLength(1);
    float ZAt(System.Numerics.Vector2 p)
    {
        var px = Math.Clamp((int)(p.X / metersPerPixel), 0, mapW - 1);
        var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, mapH - 1);
        return heightMap[py, px];
    }

    var dz = ZAt(posAfter) - ZAt(posBefore);
    var dx = System.Numerics.Vector2.Distance(posAfter, posBefore);
    if (dx < 0.01f) return 0f;
    return MathF.Atan2(dz, dx) * (180f / MathF.PI);
}
```

No fallback helper needed — `RoadSpline.GetPointAtDistance` is the verified public API. Do not introduce a hand-rolled `SampleSplineAt`. Do not reference `spline.Points` (does not exist; the actual list is `spline.ControlPoints` and direct iteration is not appropriate for an interpolated spline).

- [ ] **Step 6: Build + run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 7: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionPinningValidationExporterTests.cs
git commit -m "feat: w-test summary with class-keyed sigma (Task A4)"
```

---

### Task A5: ±d quadratic-growth CSV

For each junction with non-NaN `HarmonizedElevation`, for each terminating leg, sample heightmap delta (modified - original) at distances `{5, 15, 30, 60}` m along the leg. Output one row per leg: `junction_id, spline_id, is_start, delta_5m, delta_15m, delta_30m, delta_60m`. No aggregate stats — just raw rows for the engineer to inspect.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs`

- [ ] **Step 1: Implement `ExportQuadraticGrowthCsv`.**

Replace the stub:

```csharp
private static void ExportQuadraticGrowthCsv(
    UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
{
    var distances = new[] { 5f, 15f, 30f, 60f };
    var mapH = modified.GetLength(0);
    var mapW = modified.GetLength(1);

    using var writer = new StreamWriter(path);
    writer.Write("junction_id,spline_id,is_start");
    foreach (var d in distances) writer.Write($",delta_{d:0}m");
    writer.WriteLine();

    float DeltaAt(System.Numerics.Vector2 p)
    {
        var px = Math.Clamp((int)(p.X / metersPerPixel), 0, mapW - 1);
        var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, mapH - 1);
        return modified[py, px] - original[py, px];
    }

    foreach (var j in network.Junctions.Where(j => !j.IsExcluded && !float.IsNaN(j.HarmonizedElevation)))
    foreach (var c in j.Contributors.Where(c => c.IsEndpoint))
    {
        writer.Write($"{j.JunctionId},{c.Spline.SplineId},{c.IsSplineStart}");
        var totalLen = c.Spline.Spline.TotalLength;
        foreach (var d in distances)
        {
            var distFromStart = c.IsSplineStart ? d : MathF.Max(0f, totalLen - d);
            var samplePos = c.Spline.Spline.GetPointAtDistance(distFromStart);
            writer.Write($",{DeltaAt(samplePos):F3}");
        }
        writer.WriteLine();
    }
}
```

Uses `RoadSpline.GetPointAtDistance` directly (verified API at L196 of `RoadSpline.cs`).

- [ ] **Step 2: Build + run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 3: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/JunctionPinningValidationExporter.cs
git commit -m "feat: ±d quadratic-growth CSV (Task A5)"
```

---

### Task A6: Step 0 baseline capture (manual — no commit)

Now that the harness is wired and emits files even when nothing is pinned, capture the pre-Phase-1.9 baseline.

- [ ] **Step 1: Open the app, load `franco_same_prio`, run terrain generation with default settings.**
- [ ] **Step 2: Locate the `MT_TerrainGeneration` folder generated next to the level output. Copy the following into `baseline/franco_same_prio/`:**
  - `unified_junction_harmonization_debug.png`
  - `delta_three_band.png`
  - `junction_residuals.csv`
  - `w_test_summary.csv`
  - `quadratic_growth.csv`
  - The terrain-generation log (filter for `"W1 validation:"` and `"max correction"`).
- [ ] **Step 3: Repeat for one crossroads map of your choosing. Save into `baseline/<map_name>/`.**
- [ ] **Step 4: Note baseline aggregate stats in a `baseline/README.md` for later comparison.** Specifically: `pinResMean`, `pinResSigma`, `pinResMaxAbs`, `wTestOutliers`, `redBandPixels` for each map.

No code change, no commit. End of Phase A.

---

## Phase B — Step 1: Phase 1.9 + T-junctions + W2/W3

### Task B1: Add flags to `JunctionHarmonizationParameters`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

- [ ] **Step 1: Add the four properties under a new section header.**

Add immediately after the `MASTER ENABLE` section (after line 21 `public bool EnableJunctionHarmonization { get; set; } = true;`):

```csharp
    // ========================================
    // PHASE 1.9 — JUNCTION ELEVATION PINNING (new)
    // ========================================

    /// <summary>
    ///     W1 — primary pinning feature. When true, JunctionElevationPinner runs between
    ///     Phase 1.8 (junction detection) and Phase 2 (network smoothing). It writes
    ///     HarmonizedElevation for Endpoint/T/Y/X/Complex junctions so terminating
    ///     roads ramp into a fixed Z and continuous roads slope across it untouched.
    ///     Default: false (opt-in until Steps 1-3 pass on validation maps).
    /// </summary>
    public bool EnablePhase19JunctionPinning { get; set; } = false;

    /// <summary>
    ///     W2 — AASHTO §4.1.5 grade-skip rule (Wang 2011). When the natural Phase-2
    ///     grade and the pinned-junction grade differ by ≤ GradeSkipThresholdPercent,
    ///     the Hermite ramp is skipped on this leg (no kink possible, no benefit).
    ///     Default: false. Permanent toggle even after Step 4.
    /// </summary>
    public bool EnableHermiteGradeSkip { get; set; } = false;

    /// <summary>
    ///     W2 threshold in percent. Default 0.5 % per AASHTO Green Book.
    /// </summary>
    public float GradeSkipThresholdPercent { get; set; } = 0.5f;

    /// <summary>
    ///     W3 — AASHTO §4.1.5 class-dependent max-grade clamp (Wang 2011). After Hermite
    ///     ramp samples are placed, clamp any segment grade that exceeds the class-keyed
    ///     maximum (motorway 3 %, primary/secondary 5 %, residential/service 7 %, anything
    ///     else 9 %). Belt-and-braces over R7 (slope kink in steep terrain).
    ///     Default: false. Permanent toggle even after Step 4.
    /// </summary>
    public bool EnableMaxGradeClamp { get; set; } = false;
```

- [ ] **Step 2: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 3: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: add Phase 1.9 + W2 + W3 flags to JunctionHarmonizationParameters (Task B1)"
```

---

### Task B2: `JunctionElevationPinner` class — Endpoint + TJunction

Pure function over `(network, heightMap, metersPerPixel, parameters)`. Walks `network.Junctions`. For each `Endpoint` or `TJunction`: bilinear heightmap sample at `junction.Position` → `junction.HarmonizedElevation`. Everything else is left as-is (`NaN`).

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs`
- Create: `BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs`

- [ ] **Step 1: Write failing test for the Endpoint case.**

```csharp
// BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

public class JunctionElevationPinnerTests
{
    private static float[,] FlatHeightMap(int size, float elevation)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            hm[y, x] = elevation;
        return hm;
    }

    [Fact]
    public void PinNetwork_FlagOff_LeavesAllHarmonizedElevationsAtNaN()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(100, 100));
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 42.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = false };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        Assert.All(network.Junctions, j => Assert.True(float.IsNaN(j.HarmonizedElevation)));
    }

    [Fact]
    public void PinNetwork_FlagOn_EndpointJunctionPinnedToTerrainSample()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(100, 100));
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 42.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var endpoints = network.Junctions.Where(j => j.Type == JunctionType.Endpoint).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, j => Assert.Equal(42.0f, j.HarmonizedElevation, 3));
    }

    [Fact]
    public void PinNetwork_FlagOn_TJunctionPinnedToTerrainSampleAtJunctionXY()
    {
        // For NetworkJunctionDetector.ClassifyJunctions to label this a TJunction (not CrossRoads),
        // at least one contributor must IsContinuous — i.e. an endpoint touches the MIDDLE of a
        // through-road's spline (not another endpoint). Setup: one long through-road and one
        // perpendicular terminator whose endpoint sits on the through-road's centerline.
        // The detector clusters by spatial proximity; the through-road's mid-spline CS at ~100 m
        // along becomes a continuous contributor at the cluster.
        var throughRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 100), new(190, 100));
        var terminator = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(100, 10), new(100, 100));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { throughRoad, terminator })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 17.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var tJunction = network.Junctions.FirstOrDefault(j => j.Type == JunctionType.TJunction);
        Assert.NotNull(tJunction);
        Assert.Equal(17.0f, tJunction!.HarmonizedElevation, 3);
    }

    [Fact]
    public void PinNetwork_FlagOn_MidSplineCrossingStaysNaN()
    {
        // Spline 1: a long road. Spline 2: crosses it midway with both endpoints elsewhere.
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(190, 100));
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 10), new(100, 190));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 99.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var midSpline = network.Junctions.Where(j => j.Type == JunctionType.MidSplineCrossing).ToList();
        Assert.All(midSpline, j => Assert.True(float.IsNaN(j.HarmonizedElevation),
            $"MidSplineCrossing junction {j.JunctionId} unexpectedly pinned to {j.HarmonizedElevation}"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~JunctionElevationPinner"`
Expected: FAIL — `JunctionElevationPinner` does not exist.

- [ ] **Step 3: Create the class.**

```csharp
// BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase 1.9 — pins junction elevations before per-road smoothing runs.
///     Sets <see cref="NetworkJunction.HarmonizedElevation"/> for Endpoint/T/Y/X/Complex
///     junctions. Leaves MidSplineCrossing, Roundabout, and Continuation as NaN —
///     their existing handlers in Phase 3 continue to compute those.
///     Pure function: only side effect is setting HarmonizedElevation on junctions.
/// </summary>
public static class JunctionElevationPinner
{
    public static void PinNetwork(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        JunctionHarmonizationParameters parameters)
    {
        if (!parameters.EnablePhase19JunctionPinning) return;
        if (network.Junctions.Count == 0) return;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var pinned = 0;

        foreach (var j in network.Junctions)
        {
            if (j.IsExcluded) continue;

            switch (j.Type)
            {
                case JunctionType.Endpoint:
                case JunctionType.TJunction:
                    j.HarmonizedElevation = SampleHeightmapBilinear(
                        heightMap, j.Position.X, j.Position.Y, metersPerPixel, mapWidth, mapHeight);
                    if (!float.IsNaN(j.HarmonizedElevation)) pinned++;
                    break;

                // Y/X/CrossRoads/Complex handled in Task C1.
                // MidSplineCrossing, Roundabout, Continuation deliberately skipped.
                default:
                    break;
            }
        }

        TerrainCreationLogger.Current?.Detail(
            $"Phase 1.9: pinned {pinned} junction elevation(s) out of {network.Junctions.Count}");
    }

    private static float SampleHeightmapBilinear(
        float[,] heightMap, float worldX, float worldY, float metersPerPixel, int mapWidth, int mapHeight)
    {
        var fx = worldX / metersPerPixel;
        var fy = worldY / metersPerPixel;
        var x0 = Math.Clamp((int)MathF.Floor(fx), 0, mapWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(fy), 0, mapHeight - 1);
        var x1 = Math.Clamp(x0 + 1, 0, mapWidth - 1);
        var y1 = Math.Clamp(y0 + 1, 0, mapHeight - 1);
        var tx = MathF.Max(0f, MathF.Min(1f, fx - x0));
        var ty = MathF.Max(0f, MathF.Min(1f, fy - y0));

        var h00 = heightMap[y0, x0];
        var h10 = heightMap[y0, x1];
        var h01 = heightMap[y1, x0];
        var h11 = heightMap[y1, x1];

        if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
            return float.NaN;

        var top = h00 + (h10 - h00) * tx;
        var bot = h01 + (h11 - h01) * tx;
        return top + (bot - top) * ty;
    }
}
```

If `TerrainCreationLogger` is not visible in this namespace, look at how `UnifiedRoadSmoother.cs` imports it (top of file) and copy that `using` directive.

- [ ] **Step 4: Run tests to verify they pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~JunctionElevationPinner"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs
git commit -m "feat: JunctionElevationPinner for Endpoint and TJunction (Task B2)"
```

---

### Task B3: Phase 1.9 call site in `UnifiedRoadSmoother`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

- [ ] **Step 1: Locate Phase 1.8 detect call.** It is at L215: `_junctionDetector.DetectJunctions(network);`. Read 10 lines after to confirm the surrounding context.

- [ ] **Step 2: Insert the Phase 1.9 call immediately after the detect.**

Add after the existing `DetectJunctions` line (current L215). The exact insertion point may shift; find the line `_junctionDetector.DetectJunctions(network);` and add the new block on the next line:

```csharp
// Phase 1.9 — pin junction elevations before per-road smoothing.
// When EnablePhase19JunctionPinning is true, junction HarmonizedElevation is fixed
// here and consumed by Phase 2's endpoint anchor lookup. Otherwise no-op.
// Use the first material's parameters; all materials share the same junction params today.
var phase19Params = roadMaterials
    .Select(m => m.RoadParameters?.JunctionHarmonizationParameters)
    .FirstOrDefault(p => p != null);
if (phase19Params != null)
{
    JunctionElevationPinner.PinNetwork(network, heightMap, metersPerPixel, phase19Params);
}
```

If `roadMaterials` / `heightMap` / `metersPerPixel` are not yet in scope at this line, read the method signature of the enclosing method to confirm they are. If not, this call goes later in the pipeline — find the earliest spot after `DetectJunctions` where all three are available.

- [ ] **Step 3: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 4: Run full test suite to verify no regression.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green. With `EnablePhase19JunctionPinning = false` (the default) nothing changes.

- [ ] **Step 5: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat: wire Phase 1.9 call site between detect and smooth (Task B3)"
```

---

### Task B4: C1a — Pass `useHarmonizedElevation = true` on iteration 1 when flag on

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

- [ ] **Step 1: Locate L760: the existing call.**

Current line (verified):

```csharp
var endpointAnchors = BuildEndpointAnchorLookup(network, heightMap, metersPerPixel, reSmoothFromExisting);
```

- [ ] **Step 2: Replace the fourth argument so the flag forces `true` on iteration 1.**

`BuildEndpointAnchorLookup` already reads per-spline `JunctionHarmonizationParameters` at L952 (`contributor.Spline.Parameters.JunctionHarmonizationParameters`). The per-spline params already carry the new `EnablePhase19JunctionPinning` flag added in Task B1. No need to plumb `roadMaterials` through `CalculateNetworkElevations`.

At L760, replace:

```csharp
var endpointAnchors = BuildEndpointAnchorLookup(network, heightMap, metersPerPixel, reSmoothFromExisting);
```

with:

```csharp
// If any spline has Phase 1.9 enabled, force useHarmonizedElevation=true on iteration 1 too.
// Each spline carries its own JunctionHarmonizationParameters, so we OR them.
var phase19Enabled = network.Splines.Any(s =>
    s.Parameters.JunctionHarmonizationParameters?.EnablePhase19JunctionPinning == true);
var useHarmonized = reSmoothFromExisting || phase19Enabled;

var endpointAnchors = BuildEndpointAnchorLookup(network, heightMap, metersPerPixel, useHarmonized);
```

No method signatures change. No global plumbing. Verify `network.Splines` is the right collection by checking [BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedRoadNetwork.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedRoadNetwork.cs) before implementing; alternative is iterating `network.CrossSections.Select(cs => cs.OwnerSpline?.Parameters.JunctionHarmonizationParameters).Where(p => p != null)`.

- [ ] **Step 3: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 4: Run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 5: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat: C1a — useHarmonizedElevation=true on iter 1 when Phase 1.9 active (Task B4)"
```

---

### Task B5: C1b — Gate the `JunctionType.Endpoint` early-out on the flag

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`

- [ ] **Step 1: Read the surrounding context at L924.**

Run: `Read BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs lines 915-950` mentally. Confirm `BuildEndpointAnchorLookup` receives the `useHarmonizedElevation` parameter.

- [ ] **Step 2: Pass `enablePhase19JunctionPinning` into `BuildEndpointAnchorLookup`.**

Change the signature:

```csharp
// BEFORE
private Dictionary<(int splineId, bool isStart), EndpointAnchor?> BuildEndpointAnchorLookup(
    UnifiedRoadNetwork network,
    float[,] heightMap,
    float metersPerPixel,
    bool useHarmonizedElevation)

// AFTER
private Dictionary<(int splineId, bool isStart), EndpointAnchor?> BuildEndpointAnchorLookup(
    UnifiedRoadNetwork network,
    float[,] heightMap,
    float metersPerPixel,
    bool useHarmonizedElevation,
    bool allowNonEndpointJunctions)
```

Update the L760 call site to pass `phase19Enabled` (the local from Task B4) for the new argument.

- [ ] **Step 3: Replace the L924 early-out.**

Change:

```csharp
// BEFORE
if (junction.Type != JunctionType.Endpoint) continue;

// AFTER
if (!allowNonEndpointJunctions && junction.Type != JunctionType.Endpoint) continue;
```

That's it — the L949 `if (!contributor.IsEndpoint) continue;` already filters to terminating contributors, and the L928 `useHarmonizedElevation && !float.IsNaN(...)` branch already routes through `junction.HarmonizedElevation` when pinned.

- [ ] **Step 4: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 5: Run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green. When `phase19Enabled` is false (default), `allowNonEndpointJunctions` is false; only Endpoint junctions get anchors — bit-identical to today.

- [ ] **Step 6: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs
git commit -m "feat: C1b — allow non-Endpoint junctions in anchor lookup when Phase 1.9 active (Task B5)"
```

---

### Task B6: C2 — Early-`continue` in `NetworkJunctionHarmonizer.ComputeJunctionElevations`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

- [ ] **Step 1: Locate the foreach loop at L215.**

Confirm structure (verified):

```csharp
foreach (var junction in junctions)
{
    // Skip excluded junctions - they won't be harmonized
    if (junction.IsExcluded)
    {
        junction.HarmonizedElevation = float.NaN;
        continue;
    }

    switch (junction.Type)
    ...
}
```

- [ ] **Step 2: Add the early-`continue` immediately after the `IsExcluded` block, before `switch`.**

Insert:

```csharp
    // C2 (Phase 1.9 plumbing): if the junction was already pinned upstream (HarmonizedElevation
    // is non-NaN at this point), preserve the pin — do not re-derive it from already-smoothed roads.
    // Behaviour-neutral when nothing was pinned: NaN guard falls through to the switch below.
    if (!float.IsNaN(junction.HarmonizedElevation))
        continue;
```

- [ ] **Step 3: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 4: Run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green. The guard preserves pinned values and is a no-op when nothing was pinned (since the switch arms unconditionally overwrite anyway).

- [ ] **Step 5: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs
git commit -m "feat: C2 — preserve pinned HarmonizedElevation in harmonizer (Task B6)"
```

---

### Task B7: C3 — NaN-guard the six `HarmonizedElevation` writes in the blender

The blender writes `HarmonizedElevation` at lines 407, 611, 784, 919, 971, 1400. Each must become "write only if NaN" so a Phase 1.9 pin persists.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

- [ ] **Step 1: For each of the six sites, wrap the assignment with a NaN check.**

Site 1 (L407, T-junction primary):

```csharp
// BEFORE
junction.HarmonizedElevation = edgeCenterElev;
// AFTER
if (float.IsNaN(junction.HarmonizedElevation))
    junction.HarmonizedElevation = edgeCenterElev;
```

Site 2 (L611, roundabout):

```csharp
if (float.IsNaN(junction.HarmonizedElevation))
    junction.HarmonizedElevation = edgeCenterElev;
```

Site 3 (L784, dominant-road multi-way):

```csharp
if (float.IsNaN(junction.HarmonizedElevation))
    junction.HarmonizedElevation = dominantCS.TargetElevation;
```

Site 4 (L919, harmonized multi-way):

```csharp
if (float.IsNaN(junction.HarmonizedElevation))
    junction.HarmonizedElevation = harmonizedElev;
```

Site 5 (L971, endpoint terrain):

```csharp
if (float.IsNaN(junction.HarmonizedElevation))
    junction.HarmonizedElevation = terrainElev;
```

Site 6 (L1400, equal-priority weighted):

The existing assignment looks like:

```csharp
junction.HarmonizedElevation = totalPriority > 0
    ? weightedSum / totalPriority
    : junction.Contributors.Average(c => c.CrossSection.TargetElevation);
```

Wrap:

```csharp
if (float.IsNaN(junction.HarmonizedElevation))
{
    junction.HarmonizedElevation = totalPriority > 0
        ? weightedSum / totalPriority
        : junction.Contributors.Average(c => c.CrossSection.TargetElevation);
}
```

Line numbers may have drifted by 1–2 from edits in earlier tasks; locate each by searching the file for `junction.HarmonizedElevation =` (six matches expected) and apply the guard at each.

- [ ] **Step 2: Build to verify compile.**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeds.

- [ ] **Step 3: Run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green. With `EnablePhase19JunctionPinning = false`, no pin is ever set, so every NaN guard falls through and the assignment runs as before.

- [ ] **Step 4: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs
git commit -m "feat: C3 — NaN-guard the 6 HarmonizedElevation writes in blender (Task B7)"
```

---

### Task B8: W2 — Hermite ramp grade-skip

When `EnableHermiteGradeSkip` is true and `|natural_grade - pinned_grade| ≤ GradeSkipThresholdPercent`, the Hermite ramp on that leg is skipped — the terminating road keeps its natural Phase-2 profile near the junction.

The Hermite ramp is applied inside `OptimizedElevationSmoother.ApplyEndpointAnchoring` (line 216) via the `EndpointAnchor` record. The cleanest way to plumb W2 is at the call site (`BuildEndpointAnchorLookup`): if the grade test passes, return `null` for that anchor (which `ApplyEndpointAnchoring` already treats as "no anchor on this end").

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`
- Modify: `BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs` (add unit test for the threshold math)

- [ ] **Step 1: Add failing test for the threshold check.**

Add to `JunctionElevationPinnerTests.cs`:

```csharp
[Theory]
[InlineData(0.0f, 0.0f, 0.5f, true)]    // identical → skip
[InlineData(0.4f, 0.0f, 0.5f, true)]    // within threshold
[InlineData(0.5f, 0.0f, 0.5f, true)]    // exactly at threshold → skip (≤)
[InlineData(0.51f, 0.0f, 0.5f, false)]  // past threshold → keep ramp
[InlineData(-0.4f, 0.0f, 0.5f, true)]   // sign-agnostic
[InlineData(0.0f, 0.6f, 0.5f, false)]
public void ShouldSkipHermiteRamp_AppliesAashtoGradeThreshold(
    float naturalGradePct, float pinnedGradePct, float thresholdPct, bool expectSkip)
{
    Assert.Equal(expectSkip,
        JunctionElevationPinner.ShouldSkipHermiteRamp(naturalGradePct, pinnedGradePct, thresholdPct));
}
```

- [ ] **Step 2: Run test to verify fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ShouldSkipHermiteRamp"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement the helper in `JunctionElevationPinner.cs`.**

```csharp
public static bool ShouldSkipHermiteRamp(float naturalGradePct, float pinnedGradePct, float thresholdPct)
{
    return MathF.Abs(naturalGradePct - pinnedGradePct) <= thresholdPct;
}
```

- [ ] **Step 4: Run test to verify pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~ShouldSkipHermiteRamp"`
Expected: PASS (6/6).

- [ ] **Step 5: Wire W2 into `BuildEndpointAnchorLookup` in `UnifiedRoadSmoother.cs`.**

Per-contributor `JunctionHarmonizationParameters` is already read at L952 (`var junctionParams = contributor.Spline.Parameters.JunctionHarmonizationParameters;`). Reuse that — no plumbing needed.

Inside the existing anchor-building inner loop (after `var junctionParams = …;` at L952, before `anchors[key] = anchor;` at L970), insert:

```csharp
if (junctionParams?.EnableHermiteGradeSkip == true)
{
    // Compute the natural grade on this leg vs the pinned grade. If they differ by
    // ≤ GradeSkipThresholdPercent, skip the Hermite ramp entirely (AASHTO §4.1.5).
    var contributorSections = network.CrossSections
        .Where(cs => cs.OwnerSplineId == contributor.Spline.SplineId)
        .OrderBy(cs => cs.LocalIndex)
        .ToList();
    if (contributorSections.Count >= 2)
    {
        var first = contributor.IsSplineStart ? contributorSections[0] : contributorSections[^1];
        var second = contributor.IsSplineStart ? contributorSections[1] : contributorSections[^2];
        var dz = first.TargetElevation - second.TargetElevation;
        var dx = MathF.Abs(first.DistanceAlongSpline - second.DistanceAlongSpline);
        var naturalGradePct = dx > 0.01f ? (dz / dx) * 100f : 0f;

        var pinnedDz = anchorElevation - second.TargetElevation;
        var pinnedGradePct = dx > 0.01f ? (pinnedDz / dx) * 100f : 0f;

        if (JunctionElevationPinner.ShouldSkipHermiteRamp(
                naturalGradePct, pinnedGradePct, junctionParams.GradeSkipThresholdPercent))
        {
            continue; // skip this contributor's anchor; no Hermite ramp.
        }
    }
}
```

Verify the property name `OwnerSplineId` on `UnifiedCrossSection` matches by reading [BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs). If it's `OwnerSpline.SplineId` instead, adjust.

- [ ] **Step 6: Build + run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 7: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs \
        BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs
git commit -m "feat: W2 — AASHTO 0.5%% grade-skip rule for Hermite ramp (Task B8)"
```

---

### Task B9: W3 — Class-dependent max-grade clamp

After Hermite ramp samples are placed, clamp segments exceeding the class max grade. Class table (AASHTO Green Book):

| OSM `highway` | Max grade |
|----|----|
| `motorway`, `motorway_link`, `trunk`, `trunk_link` | 3 % |
| `primary`, `primary_link`, `secondary`, `secondary_link` | 5 % |
| `tertiary`, `tertiary_link`, `unclassified`, `residential`, `living_street` | 7 % |
| `service`, `track`, anything else | 9 % |

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs`
- Modify: `BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` (apply the clamp after `ApplyEndpointAnchoring`)

- [ ] **Step 1: Write failing tests for the class-keyed max-grade lookup.**

```csharp
[Theory]
[InlineData("motorway", 3.0f)]
[InlineData("motorway_link", 3.0f)]
[InlineData("trunk", 3.0f)]
[InlineData("primary", 5.0f)]
[InlineData("secondary", 5.0f)]
[InlineData("tertiary", 7.0f)]
[InlineData("residential", 7.0f)]
[InlineData("service", 9.0f)]
[InlineData("track", 9.0f)]
[InlineData(null, 9.0f)]
[InlineData("", 9.0f)]
[InlineData("nonsense", 9.0f)]
public void GetMaxGradePercent_ByOsmHighwayClass(string? osmRoadType, float expected)
{
    Assert.Equal(expected, JunctionElevationPinner.GetMaxGradePercent(osmRoadType), 3);
}

[Theory]
[InlineData(2.9f, 3.0f, 2.9f)]   // under cap → unchanged
[InlineData(3.0f, 3.0f, 3.0f)]   // at cap → unchanged
[InlineData(5.0f, 3.0f, 3.0f)]   // over cap → clamp
[InlineData(-5.0f, 3.0f, -3.0f)] // negative grade preserved
public void ClampGradePercent_ToAbsoluteCap(float input, float cap, float expected)
{
    Assert.Equal(expected, JunctionElevationPinner.ClampGradePercent(input, cap), 3);
}
```

- [ ] **Step 2: Run tests to verify fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~GetMaxGradePercent|FullyQualifiedName~ClampGradePercent"`
Expected: FAIL — methods do not exist.

- [ ] **Step 3: Implement the helpers in `JunctionElevationPinner.cs`.**

```csharp
public static float GetMaxGradePercent(string? osmRoadType)
{
    if (string.IsNullOrEmpty(osmRoadType)) return 9.0f;
    return osmRoadType switch
    {
        "motorway" or "motorway_link" or "trunk" or "trunk_link" => 3.0f,
        "primary" or "primary_link" or "secondary" or "secondary_link" => 5.0f,
        "tertiary" or "tertiary_link" or "unclassified" or "residential" or "living_street" => 7.0f,
        _ => 9.0f
    };
}

public static float ClampGradePercent(float input, float cap)
{
    if (cap <= 0f) return input;
    return MathF.Sign(input) * MathF.Min(MathF.Abs(input), cap);
}
```

- [ ] **Step 4: Run tests to verify pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~GetMaxGradePercent|FullyQualifiedName~ClampGradePercent"`
Expected: PASS (16/16).

- [ ] **Step 5: Apply the clamp in `OptimizedElevationSmoother.ApplyEndpointAnchoring`.**

Read `OptimizedElevationSmoother.cs` from line 216 to understand `ApplyEndpointAnchoring`'s structure (it writes `TargetElevation` per CS). After the anchoring loop completes, add an optional clamp pass.

Add new parameters to the method signature so it can take W3 state:

```csharp
public void ApplyEndpointAnchoring(
    List<UnifiedCrossSection> crossSections,
    EndpointAnchor? startAnchor,
    EndpointAnchor? endAnchor,
    bool enableMaxGradeClamp = false,
    string? osmRoadType = null)
{
    // ... existing logic ...

    if (enableMaxGradeClamp)
    {
        var cap = JunctionElevationPinner.GetMaxGradePercent(osmRoadType);
        for (var i = 1; i < crossSections.Count; i++)
        {
            var prev = crossSections[i - 1];
            var curr = crossSections[i];
            var dx = MathF.Abs(curr.DistanceAlongSpline - prev.DistanceAlongSpline);
            if (dx < 0.01f) continue;
            var dz = curr.TargetElevation - prev.TargetElevation;
            var gradePct = (dz / dx) * 100f;
            if (MathF.Abs(gradePct) <= cap) continue;

            var clampedGrade = JunctionElevationPinner.ClampGradePercent(gradePct, cap);
            curr.TargetElevation = prev.TargetElevation + (clampedGrade / 100f) * dx;
        }
    }
}
```

Update the existing call site in `UnifiedRoadSmoother.cs` L885 to pass the new arguments:

```csharp
// BEFORE
elevationSmoother.ApplyEndpointAnchoring(crossSections, startAnchor, endAnchor);

// AFTER
elevationSmoother.ApplyEndpointAnchoring(
    crossSections, startAnchor, endAnchor,
    enableMaxGradeClamp: spline.Parameters.JunctionHarmonizationParameters?.EnableMaxGradeClamp ?? false,
    osmRoadType: spline.OsmRoadType);
```

`spline` here is the `ParameterizedRoadSpline` currently being processed (verify by reading the surrounding `foreach` at L770-790). `OsmRoadType` is a direct property on `ParameterizedRoadSpline` (verified). Per-spline params reused per Issue 5 fix in Task B4 / B8 — no global plumbing.

**Iteration interaction note:** The clamp is applied inside `ApplyEndpointAnchoring`, which runs *every* iteration of the Phase-3 refinement loop (L243 in `RunUnified`). Each iteration: anchor → smooth → harmonize → check convergence. So the clamp is one-shot per iteration; the *final* iteration's clamp result is what reaches Phase 4. On subsequent iterations, `ReSmoothFromExisting` re-derives from already-clamped `TargetElevation`, so the clamp's effect persists. If the convergence loop drops in a future cleanup step (spec §7.1), this is still correct — clamp once after the single anchoring pass. Do *not* move the clamp into post-loop code.

- [ ] **Step 6: Build + run full test suite.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All green.

- [ ] **Step 7: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs \
        BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs \
        BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs
git commit -m "feat: W3 — AASHTO class-keyed max-grade clamp on Hermite ramp (Task B9)"
```

---

### Task B10: Step 1 visual test (manual)

- [ ] **Step 1: Run terrain generation on `franco_same_prio` with all three flags off.** Diff `delta_three_band.png` and `junction_residuals.csv` against `baseline/franco_same_prio/`. Behaviour must be bit-identical or sub-mm.

- [ ] **Step 2: Run again with `EnablePhase19JunctionPinning = true`, W2/W3 off.** Save artefacts into `step1/franco_same_prio/`. Check the spec §5 Step 1 pass criteria:
  - `pin_residual_max_abs ≤ 0.20 m`
  - `w_test_outliers_gt_3 = 0`
  - `red_band_pixel_count` not greater than baseline
  - Phase 3 max-correction on iter 1 drops to ≤ 5 cm
  - Through-road heightmap samples byte-identical to baseline
  - In-game drive: no bump on terminating side; through-road unchanged.

- [ ] **Step 3: Toggle W2 on, run again.** Save into `step1/franco_same_prio_w2on/`. Expect: slight reduction in `red_band_pixel_count` on near-flat-grade T-junctions; no regression on steep T-junctions.

- [ ] **Step 4: Toggle W3 on (W2 still on), run again.** Save into `step1/franco_same_prio_w2w3on/`. Expect: no change on `franco_same_prio` (gentle terrain — W3 only bites in steep terrain).

- [ ] **Step 5: Note results in `step1/README.md`.**

No commit unless visual tests fail and require code changes. If they fail, root-cause in C1a/C1b/C2/C3/W2/W3 and commit the fix.

---

## Phase C — Step 2: Multi-way junctions

### Task C1: Selector + weighted/sequential helpers in `JunctionElevationPinner`

For Y / X / CrossRoads / Complex: if `(p1 - p2) >= 1` use sequential snap (highest-priority contributor's terrain sample); else use width × priority weighted average across all contributors.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs`
- Modify: `BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs`

- [ ] **Step 1: Write failing test for selector branch decision.**

Verified `ParameterizedRoadSpline.GetOsmPriority` (L184-230): motorway=100, motorway_link=95, trunk=90, primary=80, secondary=75, tertiary=60, residential=55, unclassified=50, service=45, track=30. Adjacent-class gaps are typically 5-10; "one tier above" maps cleanly to **≥ 15** (e.g. primary 80 vs residential 55 = 25 → tier-above, residential 55 vs unclassified 50 = 5 → same tier).

```csharp
[Theory]
[InlineData(new[] { 55, 55 }, false)]      // residential / residential → weighted
[InlineData(new[] { 55, 50 }, false)]      // residential / unclassified (5-pt) → weighted
[InlineData(new[] { 60, 55 }, false)]      // tertiary / residential (5-pt) → weighted
[InlineData(new[] { 50 }, true)]           // single contributor → sequential (degenerate but safe)
public void SelectMultiWayStrategy_NearEqualPriority_UsesWeightedAverage(int[] priorities, bool expectSequential)
{
    var useSequential = JunctionElevationPinner.UseSequentialSnap(priorities);
    Assert.Equal(expectSequential, useSequential);
}

[Theory]
[InlineData(new[] { 100, 55 }, true)]      // motorway / residential (45-pt) → sequential
[InlineData(new[] { 80, 55 }, true)]       // primary / residential (25-pt) → sequential
[InlineData(new[] { 80, 60 }, true)]       // primary / tertiary (20-pt) → sequential
[InlineData(new[] { 100, 80 }, true)]      // motorway / primary (20-pt) → sequential
public void SelectMultiWayStrategy_TierGap_UsesSequential(int[] priorities, bool expectSequential)
{
    Assert.Equal(expectSequential, JunctionElevationPinner.UseSequentialSnap(priorities));
}
```

- [ ] **Step 2: Run test to verify fail.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~UseSequentialSnap"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement `UseSequentialSnap` in `JunctionElevationPinner.cs`.**

```csharp
// One "tier" gap on the OSM priority scale used by ParameterizedRoadSpline.GetOsmPriority.
// Adjacent-class gaps are 5-10; tier-above gaps are 15+. See L184-230 of that file.
private const int PriorityTierGap = 15;

public static bool UseSequentialSnap(IReadOnlyList<int> priorities)
{
    if (priorities.Count <= 1) return true;
    var sorted = priorities.OrderByDescending(p => p).ToList();
    return (sorted[0] - sorted[1]) >= PriorityTierGap;
}
```

- [ ] **Step 4: Run test to verify pass.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~UseSequentialSnap"`
Expected: PASS (7/7 across the two `[Theory]` blocks).

- [ ] **Step 5: Wire the selector into `PinNetwork`.**

In `JunctionElevationPinner.PinNetwork`, extend the switch:

```csharp
case JunctionType.YJunction:
case JunctionType.CrossRoads:
case JunctionType.Complex:
    PinMultiWay(j, heightMap, metersPerPixel, mapWidth, mapHeight);
    if (!float.IsNaN(j.HarmonizedElevation)) pinned++;
    break;
```

And add the helper:

```csharp
// If selector produces visible artefacts at near-equal-priority junctions, switch to
// always-sequential: pick the longest-or-highest-priority contributor, pin to its terrain
// sample, and let the others adapt. See ai_docs/2026-05-14_junction_pinning/.
// JunctionContributor has no Position field; use c.CrossSection.CenterPoint
// (verified in NetworkJunction.cs:62-94 — only CrossSection, Spline, IsSplineStart,
// IsSplineEnd, IsEndpoint, IsContinuous are exposed).
private static void PinMultiWay(
    NetworkJunction j, float[,] heightMap, float metersPerPixel, int mapWidth, int mapHeight)
{
    if (j.Contributors.Count == 0) return;
    var priorities = j.Contributors.Select(c => c.Spline.Priority).ToList();

    if (UseSequentialSnap(priorities))
    {
        var winner = j.Contributors.OrderByDescending(c => c.Spline.Priority).First();
        var wp = winner.CrossSection.CenterPoint;
        j.HarmonizedElevation = SampleHeightmapBilinear(
            heightMap, wp.X, wp.Y, metersPerPixel, mapWidth, mapHeight);
        return;
    }

    // Width × priority weighted average
    var sumWeight = 0f;
    var sumWeightedZ = 0f;
    foreach (var c in j.Contributors)
    {
        var width = c.Spline.Parameters.RoadWidthMeters;
        var weight = width * c.Spline.Priority;
        if (weight <= 0f) continue;
        var cp = c.CrossSection.CenterPoint;
        var z = SampleHeightmapBilinear(
            heightMap, cp.X, cp.Y, metersPerPixel, mapWidth, mapHeight);
        if (float.IsNaN(z)) continue;
        sumWeight += weight;
        sumWeightedZ += weight * z;
    }

    j.HarmonizedElevation = sumWeight > 0f ? sumWeightedZ / sumWeight : float.NaN;
}
```

`UnifiedCrossSection.CenterPoint` is a `Vector2` in world coordinates. Verified live; do not introduce a `Position` field on `JunctionContributor`.

- [ ] **Step 6: Add integration test for multi-way pinning.**

```csharp
[Fact]
public void PinNetwork_FlagOn_XJunctionPinnedViaWeightedAverageWhenPrioritiesNearEqual()
{
    // Four splines meeting at (100,100), all residential (priority 55) — within the
    // PriorityTierGap (15), so the weighted-average branch should fire.
    var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(100, 100), priority: 55);
    var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 100), new(190, 100), priority: 55);
    var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(100, 10), new(100, 100), priority: 55);
    var s4 = RoadNetworkTestHelpers.CreateParameterizedSpline(4, new(100, 100), new(100, 190), priority: 55);

    var network = new UnifiedRoadNetwork();
    foreach (var s in new[] { s1, s2, s3, s4 })
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

    var detector = new NetworkJunctionDetector();
    var detected = detector.DetectJunctions(network);
    network.Junctions.Clear();
    network.Junctions.AddRange(detected);

    var hm = new float[200, 200];
    // Heightmap with a vertical gradient so weighted average is not trivial:
    for (var y = 0; y < 200; y++)
    for (var x = 0; x < 200; x++)
        hm[y, x] = y * 0.1f; // 0 to ~20 m

    var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };
    JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

    var x = network.Junctions.FirstOrDefault(j =>
        j.Type == JunctionType.CrossRoads || j.Type == JunctionType.Complex || j.Type == JunctionType.YJunction);
    Assert.NotNull(x);
    Assert.False(float.IsNaN(x!.HarmonizedElevation));
    // At the junction position (100, 100) the gradient gives ~10 m. All four
    // contributors' cross-sections at the junction sample near y=100 → ~10 m.
    Assert.InRange(x.HarmonizedElevation, 9.0f, 11.0f);
}

[Fact]
public void PinNetwork_FlagOn_TJunctionSequentialSnap_PrioritiesAcrossTier()
{
    // Through-road = primary (priority 80), terminator = residential (priority 55).
    // Gap = 25 ≥ PriorityTierGap (15), so sequential snap to the higher-priority leg.
    var throughRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
        1, new(10, 100), new(190, 100), osmRoadType: "primary", priority: 80);
    var terminator = RoadNetworkTestHelpers.CreateParameterizedSpline(
        2, new(100, 10), new(100, 100), osmRoadType: "residential", priority: 55);

    var network = new UnifiedRoadNetwork();
    foreach (var s in new[] { throughRoad, terminator })
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

    var detector = new NetworkJunctionDetector();
    var detected = detector.DetectJunctions(network);
    network.Junctions.Clear();
    network.Junctions.AddRange(detected);

    var hm = new float[200, 200];
    for (var y = 0; y < 200; y++)
    for (var xCol = 0; xCol < 200; xCol++)
        hm[y, xCol] = y * 0.1f;

    var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };
    JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

    // The detector should label this as TJunction (one contributor IsContinuous).
    // TJunction takes its pin from the junction's own Position (bilinear sample), not the
    // multi-way selector — so this test confirms the TJunction branch is taken in priority
    // setups too, not that the selector fires here. The selector only fires for Y/X/Complex.
    var t = network.Junctions.FirstOrDefault(j => j.Type == JunctionType.TJunction);
    Assert.NotNull(t);
    Assert.InRange(t!.HarmonizedElevation, 9.0f, 11.0f);
}
```

- [ ] **Step 7: Run all tests.**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~JunctionElevationPinner"`
Expected: All green.

- [ ] **Step 8: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationPinner.cs \
        BeamNgTerrainPoc.Tests/Junction/JunctionElevationPinnerTests.cs
git commit -m "feat: multi-way junction pinning with priority selector (Task C1)"
```

---

### Task C2: Step 2 visual test (manual)

- [ ] **Step 1: Run the crossroads map with all flags off.** Save into `step2/<map_name>_baseline/`.
- [ ] **Step 2: Run with `EnablePhase19JunctionPinning = true`.** Save into `step2/<map_name>/`.
- [ ] **Step 3: Verify spec §5 Step 2 pass criteria:**
  - `residual_max_minus_min ≤ 10 cm` at every multi-way junction (CSV)
  - `pin_residual_max_abs ≤ 0.20 m`
  - `red_band_pixel_count` not greater than baseline (R8 ditch gate)
  - All arms share `HarmonizedElevation` in debug PNG
  - In-game drive through X: no asymmetric tilt
- [ ] **Step 4: If R8 fires on > 1 % of multi-way junctions, root-cause in §4.1 selector and consider switching to always-sequential (per the code comment in `PinMultiWay`).**
- [ ] **Step 5: Note results in `step2/README.md`.**

No commit unless visual tests reveal a bug.

---

## Phase D — Step 3: Risk validation pass

Run the W1 harness on the maps used in Steps 1/2 plus any steep-terrain map you have. Use the artefacts to evaluate each risk in spec §6.

### Task D1: R4 — short splines (manual inspection)

- [ ] **Step 1: Identify connectors < 20 m between two pinned junctions on the test maps (use `junction_residuals.csv` + spline lengths).**
- [ ] **Step 2: Inspect `quadratic_growth.csv` for those connectors — `delta_5m` should not be unreasonable. If the existing short-spline-linear-interp fix (commit `5805bc0`) handles it, log "R4 clear." If not, extend the short-spline handler.**

### Task D2: R7 — steep-terrain slope kink

- [ ] **Step 1: Find a > 4 % grade T-junction on a test map.**
- [ ] **Step 2: Inspect `w_test_summary.csv` for that junction's terminator. If `|w| > 3`, R7 has fired.**
- [ ] **Step 3: First mitigation: enable `EnableMaxGradeClamp`, rerun. If `|w|` drops below 3, R7 is mitigated.**
- [ ] **Step 4: If still > 3, the cubic-Hermite-with-far-slope mitigation (R9 #2) is the next step. Document and defer to its own task.**

### Task D3: R3 — cross-material junctions

- [ ] **Step 1: On a multi-material map, inspect `junction_residuals.csv` — `residual_max_minus_min` should be small (< 10 cm) at every multi-material junction.**
- [ ] **Step 2: If observed > 10 cm, add an assertion + log in `JunctionElevationPinner.PinNetwork` that fires on the offending junction and prints contributor materials + Z values. Re-run, capture, root-cause.**

### Task D4: R8 — multi-way ditch regression

Already covered by Step 2 (Task C2). If new ditches appear in Phase D's larger maps, treat as R8 and use Paper 4's cross-boundary neighbor-exclusion fix:

- [ ] **Step 1: When computing heightmap delta around a junction, exclude samples whose source road is not in the junction's contributor list. This may live in the existing IDW or plateau code rather than in the validation harness — locate by searching for the term "polygon" or "junction mask" in the terrain blending code.**

No commit unless a risk fires and requires code. Each fix lands as its own commit referencing the risk ID.

---

## Phase E — Step 4: Flag flip (deferred, do NOT run until Steps 1-3 pass)

### Task E1: Make `EnablePhase19JunctionPinning` default-true and remove the gating

Run only after the user explicitly confirms Steps 1-3 passed on at least two validation maps.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs`

- [ ] **Step 1: Flip default to true.**

```csharp
public bool EnablePhase19JunctionPinning { get; set; } = true;
```

- [ ] **Step 2: Run full test suite + visual test on `franco_same_prio` and crossroads map.** Expected: same artefacts as Step 1/2 with flag explicitly true. No behaviour change vs the explicit-true case.

- [ ] **Step 3: Consider removing the conditional gating at the four touchpoints once we are confident.**

The NaN-guarded touchpoints (C1b, C2, C3) can stay — they are harmless when nothing is pinned. The only flag-gated point is C1a (passing `useHarmonizedElevation = true` on iteration 1). With the flag now always true, that branch is always taken — but leave the flag as an escape hatch for now. Removal is a separate, no-rush commit.

- [ ] **Step 4: Commit.**

```bash
git add BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs
git commit -m "feat: flip EnablePhase19JunctionPinning default to true (Task E1)"
```

Note: W2 (`EnableHermiteGradeSkip`) and W3 (`EnableMaxGradeClamp`) stay default-false as permanent advanced toggles — they are belt-and-braces, not the main feature.

---

## Self-Review Checklist (run after writing the plan)

### 1. Spec coverage

| Spec section | Covered by |
|---|---|
| §2 In-scope: Phase 1.9, 5 junction types, point + tangent (slope), 3 flags, W1 harness | Tasks A1-A6 (W1), B1 (flags), B2/C1 (pinning), B6-B7 (slope via NaN guard chain) |
| §2 N1+N2 (novelty notes, terminology) | Documentation only in spec — no plan task needed |
| §3.1 New class `JunctionElevationPinner` | Task B2, C1 |
| §3.2 Three flags + `GradeSkipThresholdPercent` | Task B1 |
| §3.3 Phase 1.9 call site | Task B3 |
| §3.4 C1a, C1b, C2, C3 | Tasks B4, B5, B6, B7 |
| §4 Pin computation per type | Tasks B2 (Endpoint, TJunction), C1 (Y/X/Complex), defaults (MidSpline, Roundabout, Continuation skipped) |
| §4.1 Selector with verbatim code comment | Task C1 (comment included in code block) |
| §5 Step 0 baseline | Task A6 |
| §5 Step 1 visual test | Task B10 |
| §5 Step 2 visual test | Task C2 |
| §5 Step 3 risk validation | Tasks D1-D4 |
| §5 Step 4 flag flip | Task E1 |
| §6 Risk register R3/R4/R7/R8/R7b | Phase D |
| §7.2 W4 class-aware blend distances | **Deferred per spec** — not in this plan |
| §7.3 F1-F4 | **Deferred per spec** — not in this plan |
| §8 Files-that-change | Plan touches the exact files listed (verified) |

### 2. Placeholder scan

No TBDs, no "implement later", no "similar to Task N." Every code step shows actual code. The only handwaves are in Phase D risk-validation tasks (manual inspection) — that is appropriate because Phase D is "no new code unless a risk fires."

### 3. Type consistency

- `JunctionElevationPinner.PinNetwork(UnifiedRoadNetwork, float[,], float, JunctionHarmonizationParameters)` — used consistently in Tasks B2, B3, C1 tests and the call site.
- `JunctionPinningValidationExporter.Export(UnifiedRoadNetwork, float[,], float[,], float, string) -> AggregateStats` — consistent in Task A1 (declaration) and A1 Step 2 (call site).
- Helper signatures: `ShouldSkipHermiteRamp`, `GetMaxGradePercent`, `ClampGradePercent`, `UseSequentialSnap`, `ClassifyBand`, `ComputeWStatistic`, `GetSigmaPredictedDeg`, `ComputeResidualStats` — all `public static` on `JunctionElevationPinner` or `JunctionPinningValidationExporter`. Test invocations match.
- `JunctionHarmonizationParameters` properties: `EnablePhase19JunctionPinning`, `EnableHermiteGradeSkip`, `GradeSkipThresholdPercent`, `EnableMaxGradeClamp` — same names used in B1 (declaration) and B4/B8/B9 (consumers).
- `JunctionType` enum values: `Endpoint`, `TJunction`, `YJunction`, `CrossRoads`, `Complex`, `MidSplineCrossing`, `Roundabout`, `Continuation` — verified against `NetworkJunction.cs` and `NetworkJunctionDetector.cs`.

### 4. Dead-code check

Every line number, method name, and field reference was grepped against the current repo before plan write-up AND again after the review pass below:

- `UnifiedRoadSmoother.cs`: L215 `DetectJunctions`, L368 `HarmonizeNetwork`, L439 `ExportJunctionDebugImageIfRequested`, L760 `BuildEndpointAnchorLookup` call, L885 `ApplyEndpointAnchoring`, L901 `BuildEndpointAnchorLookup` def, L924 Endpoint early-out, L949 IsEndpoint filter, L952 per-spline `junctionParams` read, L1037 `ExportJunctionDebugImageIfRequested` body, L1089 `ExportDebugImagesIfRequested` — all live.
- `NetworkJunctionHarmonizer.cs`: L207 `ComputeJunctionElevations`, L215 foreach, L220 `IsExcluded` block, L791-889 `ExportJunctionDebugImage` (ImageSharp pattern), L889 `image.SaveAsPng` — all live.
- `UnifiedJunctionProfileBlender.cs`: L407, 611, 784, 919, 971, 1400 `HarmonizedElevation` writes — all 6 present. L368, 580, 780, 888, 1743 `CalculateSlopeAtIndex` calls — all live. L1960 method definition — live.
- `OptimizedElevationSmoother.cs` L216 `ApplyEndpointAnchoring` — live. Only caller is L885 in `UnifiedRoadSmoother`.
- `NetworkJunctionDetector.cs` L513-552 `ClassifyJunctions` — TJunction requires at least one `IsContinuous` contributor (verified for the Task B2 test fix).
- `RoadSpline.cs`: L90 `ControlPoints`, L100 `TotalLength`, L196 `GetPointAtDistance` — all live. (`GetPositionAtDistance` and `Points` do NOT exist; plan uses the correct names.)
- `NetworkJunction.cs`: `JunctionContributor` exposes only `CrossSection`, `Spline`, `IsSplineStart`, `IsSplineEnd`, `IsEndpoint`, `IsContinuous` (L62-94). No `Position` field — plan uses `c.CrossSection.CenterPoint` instead.
- `ParameterizedRoadSpline.cs` L184-230 `GetOsmPriority`: motorway=100, primary=80, residential=55. Plan's `PriorityTierGap = 15` calibrated to this scale.
- `JunctionEndpointConstraint.cs`: `Elevation`, `Slope`, `BlendDistanceMeters` properties — all live.

If any line numbers drift by the time you execute the plan, locate the equivalent line by searching for the quoted identifier. Do NOT rely on the line numbers as load-bearing.

### 5. Reviewer-pass corrections applied

This plan was reviewed by a critical-review subagent. The following issues were identified and **all have been corrected inline above** before this final commit:

| # | Severity | Original issue | Fix applied |
|---|----------|----------------|-------------|
| 1 | blocker | Task B2 T-junction test built three end-sharing splines → would be detected as CrossRoads, not TJunction | Rebuilt test with one through-road + one perpendicular terminator |
| 2 | blocker | Task C1 `PinMultiWay` referenced non-existent `JunctionContributor.Position` | Replaced with `c.CrossSection.CenterPoint` |
| 3 | blocker | Task A4 referenced non-existent `RoadSpline.GetPositionAtDistance` / `Points` | Switched to verified `GetPointAtDistance` and `TotalLength`; deleted bogus `SampleSplineAt` fallback |
| 4 | minor | Task A3 test data had misleading mean=0 comment but actual mean=0.2 | Replaced with clean `{-2,-1,0,1,2}` dataset |
| 5 | major | Tasks B4/B8 hand-waved global plumbing of `JunctionHarmonizationParameters` | Use per-spline params already accessible at L952 (`contributor.Spline.Parameters.JunctionHarmonizationParameters`) — zero global plumbing |
| 6 | minor | Task B9 W3 clamp / iteration loop interaction not explained | Added iteration-interaction note in Step 5 |
| 7 | major | Task A2 used `System.Drawing.Bitmap` (project uses `SixLabors.ImageSharp`) | Rewrote PNG writer with `Image<Rgba32>` + `ProcessPixelRows` + `SaveAsPng` |
| 8 | major | Task C1 `PriorityTierGap = 50` was unverified; tests would never exercise the sequential branch | Grounded to actual OSM priority scale (motorway=100, primary=80, etc.); set `PriorityTierGap = 15`; reworked tests to use real-world priorities |
| 9 | major | Task A1 accessor chain not verified against existing `ExportJunctionDebugImageIfRequested` | Confirmed chain verbatim from L1037-1082; relocated call site to `ExportDebugImagesIfRequested` (L1089) where `originalHeightMap` is in scope |

Issue 10 (predicted failure modes) was a roll-up of 1, 2, 5 — all addressed above.

After applying these corrections, the plan should be executable as-is. The line-number drift caveat in §4 still applies.

---

## Execution Handoff

Plan complete and saved to `ai_docs/2026-05-14_junction_pinning/2026-05-14-junction-elevation-pinning-plan.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
