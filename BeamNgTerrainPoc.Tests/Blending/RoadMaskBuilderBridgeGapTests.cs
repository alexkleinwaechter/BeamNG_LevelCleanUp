using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

/// <summary>
///     A merged-corridor bridge span must NOT terraform the ground it spans (the rule: "a bridge spline does
///     not change the terrain"). The elevation mask builder filters excluded span cross-sections out, then
///     stitches corridor quads between list-consecutive sections — so without a gap guard it would bridge the
///     last pre-span section to the first post-span section and stamp ONE deck-height quad across the whole
///     deck. With the deck lifted (Phase C), that produced a tall smooth embankment under the bridge. The guard
///     (<c>LocalIndex</c> jump &gt; 1 ⇒ skip the quad) keeps the span unmasked so terrain stays natural under it.
/// </summary>
public class RoadMaskBuilderBridgeGapTests
{
    // A straight horizontal corridor at y=64, x∈[20,110] at 1 m spacing (LocalIndex 0..90). The middle stretch
    // (LocalIndex 30..60, x∈[50,80]) is an EXCLUDED bridge span pinned high (deck z=116); the approaches sit at
    // the low river level (z=100).
    private static UnifiedRoadNetwork BuildBridgedCorridor()
    {
        var network = new UnifiedRoadNetwork();
        var road = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(
                Enumerable.Range(0, 5).Select(i => new Vector2(20f + i * 22.5f, 64f)).ToList(),
                SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 10f, TerrainAffectedRangeMeters = 4f,
                CrossSectionIntervalMeters = 0.5f, RoadEdgeProtectionBufferMeters = 2f,
            },
            MaterialName = "asphalt", SplineId = 1, Priority = 10,
        };
        network.AddSpline(road);

        for (var i = 0; i <= 90; i++)
        {
            var inSpan = i is >= 30 and <= 60;
            var cs = new UnifiedCrossSection
            {
                Index = 1_000 + i, LocalIndex = i, OwnerSplineId = 1,
                CenterPoint = new Vector2(20f + i, 64f),
                TangentDirection = new Vector2(1f, 0f), NormalDirection = new Vector2(0f, 1f),
                EffectiveRoadWidth = 10f, SurfaceWidth = 10f, Priority = 10,
                IsExcluded = inSpan,
                StructureSpanId = inSpan ? 7 : -1,
                TargetElevation = inSpan ? 116f : 100f,
                LeftEdgeElevation = inSpan ? 116f : 100f,
                RightEdgeElevation = inSpan ? 116f : 100f,
            };
            network.AddCrossSection(cs);
        }

        return network;
    }

    [Fact]
    public void BridgeSpan_IsNotStampedIntoTheElevationMask_TerrainStaysNaturalUnderTheDeck()
    {
        var result = new RoadMaskBuilder()
            .BuildCombinedMaskWithElevation(BuildBridgedCorridor(), width: 160, height: 128, metersPerPixel: 1.0f);

        // Mid-span (x=65, y=64) is ~15 m from either abutment — far beyond any single approach quad. It must be
        // UNMASKED: no fill, elevation left NaN. Before the gap guard this was stamped at the 116 m deck height.
        Assert.Equal(0, result.Mask[64, 65]);
        Assert.True(float.IsNaN(result.ElevationMap[64, 65]),
            $"deck span was stamped into terrain: {result.ElevationMap[64, 65]:F1}");

        // The approaches ARE masked at their (low) elevation — they are real road sections that build the ramps.
        Assert.Equal(255, result.Mask[64, 30]);                 // approach near x=30
        Assert.Equal(100f, result.ElevationMap[64, 30], 1);
        Assert.Equal(255, result.Mask[64, 100]);                // approach near x=100
        Assert.Equal(100f, result.ElevationMap[64, 100], 1);

        // No pixel anywhere was stamped at the deck height (the span never contributes to the heightmap).
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 160; x++)
        {
            var z = result.ElevationMap[y, x];
            if (!float.IsNaN(z))
                Assert.True(z <= 101f, $"pixel ({x},{y}) stamped at deck-height {z:F1} — span leaked into terrain");
        }
    }
}
