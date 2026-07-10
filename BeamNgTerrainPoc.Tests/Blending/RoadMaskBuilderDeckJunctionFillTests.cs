using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Blending;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Blending;

/// <summary>
///     Doc 13 §3.3 — the junction gap fill paints a disk at <c>HarmonizedElevation</c> for every
///     non-excluded junction. At a deck-deck junction (a ramp span landing mid-span on a trunk deck,
///     Manhattan j106) EVERY contributor section is an EXCLUDED deck: there are no stamped road pixels
///     around it, so the disk is a pure ground-to-deck pillar at deck Z under a mid-air joint. With
///     <c>EnableBridgeToBridgeAbutmentSuppression</c> on any contributor spline the fill must skip such
///     junctions; flag off keeps today's fill (byte-identical).
/// </summary>
public class RoadMaskBuilderDeckJunctionFillTests
{
    private static ParameterizedRoadSpline DeckSpline(int id, Vector2 a, Vector2 b, bool suppressionFlag)
    {
        var spline = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline([a, b], SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 10f, TerrainAffectedRangeMeters = 4f,
                CrossSectionIntervalMeters = 0.5f, RoadEdgeProtectionBufferMeters = 2f,
            },
            MaterialName = "asphalt", SplineId = id, Priority = 10,
        };
        if (suppressionFlag)
            spline.Parameters.BridgeRules = new BridgeRuleSystemOptions
            {
                EnableBridgeToBridgeAbutmentSuppression = true,
            };
        return spline;
    }

    private static UnifiedCrossSection DeckSection(int splineId, int localIndex, int index, Vector2 center) => new()
    {
        Index = index, LocalIndex = localIndex, OwnerSplineId = splineId,
        CenterPoint = center,
        TangentDirection = new Vector2(1f, 0f), NormalDirection = new Vector2(0f, 1f),
        EffectiveRoadWidth = 10f, SurfaceWidth = 10f, Priority = 10,
        IsExcluded = true,
        StructureSpanId = 7,
        TargetElevation = 116f, LeftEdgeElevation = 116f, RightEdgeElevation = 116f,
        DistanceAlongSpline = localIndex,
    };

    /// <summary>Two excluded deck splines meeting at (64,64), junction pinned at deck z=116. All
    /// contributor sections are excluded deck — nothing is stamped anywhere near the junction.</summary>
    private static UnifiedRoadNetwork BuildDeckDeckJunction(bool suppressionFlag)
    {
        var network = new UnifiedRoadNetwork();
        var trunk = DeckSpline(1, new Vector2(34, 64), new Vector2(94, 64), suppressionFlag);
        var ramp = DeckSpline(2, new Vector2(64, 34), new Vector2(64, 63), suppressionFlag);
        network.AddSpline(trunk);
        network.AddSpline(ramp);

        UnifiedCrossSection? trunkAtJunction = null;
        for (var i = 0; i <= 60; i++)
        {
            var cs = DeckSection(1, i, 1_000 + i, new Vector2(34f + i, 64f));
            if (i == 30) trunkAtJunction = cs;
            network.AddCrossSection(cs);
        }

        UnifiedCrossSection? rampEnd = null;
        for (var i = 0; i <= 29; i++)
        {
            var cs = DeckSection(2, i, 2_000 + i, new Vector2(64f, 34f + i));
            rampEnd = cs;
            network.AddCrossSection(cs);
        }

        var junction = new NetworkJunction
        {
            JunctionId = 1,
            Type = JunctionType.TJunction,
            Position = new Vector2(64f, 64f),
            HarmonizedElevation = 116f,
        };
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = trunkAtJunction!, Spline = trunk, IsSplineStart = false, IsSplineEnd = false
        });
        junction.Contributors.Add(new JunctionContributor
        {
            CrossSection = rampEnd!, Spline = ramp, IsSplineStart = false, IsSplineEnd = true
        });
        network.Junctions.Add(junction);

        return network;
    }

    [Fact]
    public void DeckDeckJunction_SuppressionOn_NoDiskFilledAtDeckHeight()
    {
        var network = BuildDeckDeckJunction(suppressionFlag: true);

        var result = new RoadMaskBuilder()
            .BuildCombinedMaskWithElevation(network, width: 160, height: 128, metersPerPixel: 1.0f);

        // No pixel anywhere gets stamped at the 116 m deck height — the junction disk would have
        // been a ground-to-deck terrain pillar under the mid-air deck joint.
        Assert.Equal(0, result.Mask[64, 64]);
        for (var y = 0; y < 128; y++)
        for (var x = 0; x < 160; x++)
        {
            var z = result.ElevationMap[y, x];
            if (!float.IsNaN(z))
                Assert.True(z < 100f, $"pixel ({x},{y}) stamped at deck height {z:F1}");
        }
    }

    [Fact]
    public void DeckDeckJunction_SuppressionOff_LegacyDiskFill()
    {
        var network = BuildDeckDeckJunction(suppressionFlag: false);

        var result = new RoadMaskBuilder()
            .BuildCombinedMaskWithElevation(network, width: 160, height: 128, metersPerPixel: 1.0f);

        // Legacy (byte-identical) behaviour: the junction disk IS filled at the harmonized deck Z.
        Assert.Equal(255, result.Mask[64, 64]);
        Assert.Equal(116f, result.ElevationMap[64, 64], 1);
    }
}
