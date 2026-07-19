using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Step 1 of bridge-deck generation
///     (ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md).
///
///     Proves that the opt-in <c>includeExcluded</c> path on <see cref="CrossSectionConverter"/> yields a
///     non-empty world-coordinate cross-section list for an excluded bridge spline, while the default
///     (excluded-dropping) path used by road/DecalRoad callers still returns nothing for it.
/// </summary>
public class CrossSectionConverterBridgeTests
{
    private const int TerrainSizePixels = 300;
    private const float MetersPerPixel = 1f;
    private const float TerrainBaseHeight = 0f;

    /// <summary>
    ///     Builds road → bridge → road over a valley, marks the bridge cross-sections excluded exactly as
    ///     <c>UnifiedRoadSmoother</c> does, and runs the same chain-based elevation solve as the spike — so
    ///     the bridge cross-sections carry a solved (non-NaN) TargetElevation but remain IsExcluded.
    /// </summary>
    private static (Terrain.Models.RoadGeometry.UnifiedRoadNetwork network, int bridgeSplineId) BuildSolvedBridgeNetwork()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true); // excludeBridges defaults to true in the helper
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        // Valley under the bridge (x = 120..180 low), high plateaus at the approaches.
        var hm = new float[TerrainSizePixels, TerrainSizePixels];
        for (var y = 0; y < TerrainSizePixels; y++)
        for (var x = 0; x < TerrainSizePixels; x++)
        {
            if (x < 80 || x > 220) hm[y, x] = 100f;
            else if (x >= 120 && x <= 180) hm[y, x] = 60f;
            else if (x < 120) hm[y, x] = 100f - (x - 80f) / 40f * 40f;
            else hm[y, x] = 60f + (x - 180f) / 40f * 40f;
        }

        // Mirror UnifiedRoadSmoother.cs:1156-1178 — mark bridge cross-sections excluded BEFORE the solve.
        foreach (var spline in network.Splines)
        {
            var p = spline.Parameters;
            var generateStructure = (spline.IsBridge && p.ExcludeBridgesFromTerrain) ||
                                    (spline.IsTunnel && p.ExcludeTunnelsFromTerrain);
            if (!generateStructure) continue;
            foreach (var cs in network.CrossSections.Where(c => c.OwnerSplineId == spline.SplineId))
                cs.IsExcluded = true;
        }

        RoadNetworkTestHelpers.RunChainSmoothing(network, hm);
        return (network, bridge.SplineId);
    }

    [Fact]
    public void ConvertSplineToWorldCoordinates_IncludesExcludedBridgeSpline()
    {
        var (network, bridgeSplineId) = BuildSolvedBridgeNetwork();

        // Precondition: every cross-section of the bridge spline is excluded.
        var bridgeCs = network.GetCrossSectionsForSpline(bridgeSplineId).ToList();
        Assert.NotEmpty(bridgeCs);
        Assert.All(bridgeCs, cs => Assert.True(cs.IsExcluded, "bridge CS should be excluded"));

        // The opt-in path keeps the excluded sections → non-empty deck cross-section list.
        var deck = CrossSectionConverter.ConvertSplineToWorldCoordinates(
            network, bridgeSplineId, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        Assert.NotEmpty(deck);
        Assert.True(deck.Count >= 2, "need at least two cross-sections to form a deck ribbon");
        Assert.All(deck, cs => Assert.True(cs.WidthMeters > 0f, "deck width must come through from the spline"));
        Assert.All(deck, cs => Assert.False(float.IsNaN(cs.CenterElevation), "deck elevation must be solved"));
    }

    [Fact]
    public void ConvertPathToWorldCoordinates_DropsExcludedBridgeSpline_ByDefault()
    {
        var (network, bridgeSplineId) = BuildSolvedBridgeNetwork();
        var bridgeCs = network.GetCrossSectionsForSpline(bridgeSplineId);

        // Default (road/DecalRoad) behavior is unchanged: excluded sections are still dropped.
        var dropped = CrossSectionConverter.ConvertPathToWorldCoordinates(
            bridgeCs, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        Assert.Empty(dropped);
    }
}
