using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit.Abstractions;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Step 0 verification spike for bridge-deck generation
///     (see ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md).
///
///     Question this answers: with bridge cross-sections marked <see cref="UnifiedCrossSection.IsExcluded"/>
///     (exactly as <c>UnifiedRoadSmoother</c> does at lines 1156-1178, BEFORE the chain solve at 1209-1237),
///     do those cross-sections still carry usable deck geometry afterwards?
///
///     Specifically:
///       - Is <see cref="UnifiedCrossSection.TargetElevation"/> populated (not NaN)?
///       - Is the deck elevation continuous with the approach roads?
///       - Is <see cref="UnifiedCrossSection.EffectiveRoadWidth"/> sane?
///       - What do banking / edge elevations look like for excluded sections?
///
///     This replicates the production ORDER (mark excluded → chain-solve) using the same
///     <c>ConcatenateChainCrossSections</c> + <c>CalculateChainElevations</c> path the smoother uses.
/// </summary>
public class BridgeDeckElevationSpikeTests
{
    private readonly ITestOutputHelper _output;

    public BridgeDeckElevationSpikeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ExcludedBridgeCrossSections_StillCarryUsableDeckGeometry()
    {
        // Arrange: road → bridge → road spanning a valley (the realistic bridge case).
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);   // excludeBridges defaults to true in the helper
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        // Valley under the bridge (x = 120..180 is low), high plateaus at the approaches.
        var hm = new float[300, 300];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
        {
            if (x < 80 || x > 220) hm[y, x] = 100f;
            else if (x >= 120 && x <= 180) hm[y, x] = 60f;
            else if (x < 120) hm[y, x] = 100f - (x - 80f) / 40f * 40f;
            else hm[y, x] = 60f + (x - 180f) / 40f * 40f;
        }

        // Mirror UnifiedRoadSmoother.cs:1156-1178 — mark bridge/tunnel cross-sections excluded
        // BEFORE the elevation solve. This is the exact step under test.
        var markedExcluded = 0;
        foreach (var spline in network.Splines)
        {
            var p = spline.Parameters;
            var generateStructure = (spline.IsBridge && p.ExcludeBridgesFromTerrain) ||
                                     (spline.IsTunnel && p.ExcludeTunnelsFromTerrain);
            if (!generateStructure) continue;
            foreach (var cs in network.CrossSections.Where(c => c.OwnerSplineId == spline.SplineId))
            {
                cs.IsExcluded = true;
                markedExcluded++;
            }
        }
        Assert.True(markedExcluded > 0, "Spike precondition: at least one bridge cross-section must be marked excluded");

        // Act: run the same chain-based elevation solve the smoother runs at Phase 2.
        var (chains, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        var bridgeCs = csBySpline[2].OrderBy(c => c.LocalIndex).ToList();
        var road1Cs = csBySpline[1].OrderBy(c => c.LocalIndex).ToList();
        var road2Cs = csBySpline[3].OrderBy(c => c.LocalIndex).ToList();

        // --- Report (visible with `dotnet test -v n` / test output) ---
        _output.WriteLine($"chains={chains.Count}, bridgeCS={bridgeCs.Count}");
        _output.WriteLine($"all bridge CS IsExcluded   : {bridgeCs.All(c => c.IsExcluded)}");
        _output.WriteLine($"all bridge CS TargetElev set: {bridgeCs.All(c => !float.IsNaN(c.TargetElevation))}");
        _output.WriteLine($"bridge TargetElevation  min/max: {bridgeCs.Min(c => c.TargetElevation):F2} / {bridgeCs.Max(c => c.TargetElevation):F2}");
        _output.WriteLine($"bridge EffectiveRoadWidth min/max: {bridgeCs.Min(c => c.EffectiveRoadWidth):F2} / {bridgeCs.Max(c => c.EffectiveRoadWidth):F2}");
        _output.WriteLine($"bridge BankAngleRadians   min/max: {bridgeCs.Min(c => c.BankAngleRadians):F4} / {bridgeCs.Max(c => c.BankAngleRadians):F4}");
        _output.WriteLine($"bridge LeftEdgeElev NaN count : {bridgeCs.Count(c => float.IsNaN(c.LeftEdgeElevation))}/{bridgeCs.Count}");
        _output.WriteLine($"bridge RightEdgeElev NaN count: {bridgeCs.Count(c => float.IsNaN(c.RightEdgeElevation))}/{bridgeCs.Count}");
        var terrainMid = hm[50, 150];
        _output.WriteLine($"terrain under bridge mid (x=150): {terrainMid:F2}, deck mid: {bridgeCs[bridgeCs.Count / 2].TargetElevation:F2} (deck should float ABOVE valley floor)");

        // --- Load-bearing assertions (decision gate for the implementation plan) ---

        // 1. Marking survived into the solved network.
        Assert.All(bridgeCs, cs => Assert.True(cs.IsExcluded, "bridge CS should remain IsExcluded after solve"));

        // 2. Deck elevation IS populated for excluded bridge cross-sections (THE key question → D2).
        Assert.All(bridgeCs, cs => Assert.False(float.IsNaN(cs.TargetElevation),
            "excluded bridge CS must still have a solved TargetElevation"));

        // 3. Deck is continuous with the approach roads (< 1m, same bar as BridgeElevationChainingTests).
        var gapIn = Math.Abs(road1Cs.Last().TargetElevation - bridgeCs.First().TargetElevation);
        var gapOut = Math.Abs(bridgeCs.Last().TargetElevation - road2Cs.First().TargetElevation);
        Assert.True(gapIn < 1.0f, $"road1→bridge gap {gapIn:F3}m");
        Assert.True(gapOut < 1.0f, $"bridge→road2 gap {gapOut:F3}m");

        // 4. Deck floats above the valley floor (it did NOT follow the terrain down).
        Assert.True(bridgeCs[bridgeCs.Count / 2].TargetElevation > terrainMid + 5f,
            "deck mid should be well above the valley floor");

        // 5. Width is usable straight from the cross-section.
        Assert.All(bridgeCs, cs => Assert.True(cs.EffectiveRoadWidth > 0f, "EffectiveRoadWidth must be > 0"));

        // NOTE (not assertions — documenting expected behavior for the plan):
        // Banking phases skip excluded sections, so for a straight bridge BankAngleRadians stays 0 and
        // Left/RightEdgeElevation stay NaN. The deck builder must therefore derive edges from
        // (center ± width/2) at TargetElevation (flat pane), NOT read Left/RightEdgeElevation. This is
        // the accepted v1 behavior (flat deck) per the spec.
    }
}
