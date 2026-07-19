using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Tunnel chord shaping in the chain smoother (tunneljena follow-up): the over-the-mountain
///     terrain samples under a TUNNEL span must never enter the filter input —
///     <see cref="OptimizedElevationSmoother.ApplyTunnelChordToRaw" /> replaces them with the
///     boundary-anchored chord, so the approaches are never dragged up the flank (the polluted
///     portal grades) and the corridor solves through the mountain, not over it. Flag-gated on
///     <c>TunnelRules.EnableTunnelProfile</c> ⇒ baseline byte-identical.
/// </summary>
public class TunnelChordShapingTests
{
    [Fact]
    public void ApplyTunnelChordToRaw_ReplacesSpanRun_WithBoundaryChord()
    {
        var cs = new List<UnifiedCrossSection>();
        for (var i = 0; i < 30; i++)
        {
            var inSpan = i is >= 10 and < 20;
            cs.Add(new UnifiedCrossSection
            {
                StructureSpanId = inSpan ? 42 : -1,
                StructureSpanType = inSpan ? StructureType.Tunnel : StructureType.None
            });
        }

        var raw = new float[30];
        for (var i = 0; i < 30; i++)
            raw[i] = i is >= 10 and < 20 ? 150f : 100f + i * 0.1f; // mountain inside the span

        var any = OptimizedElevationSmoother.ApplyTunnelChordToRaw(cs, raw);

        Assert.True(any);
        var zL = raw[9];
        var zR = raw[20];
        for (var i = 10; i < 20; i++)
        {
            var t = (float)(i - 10) / 9;
            Assert.Equal(zL + (zR - zL) * t, raw[i], 0.001f);
        }

        // Road sections untouched.
        Assert.Equal(100f, raw[0], 0.001f);
        Assert.Equal(100f + 29 * 0.1f, raw[29], 0.001f);
    }

    [Fact]
    public void ApplyTunnelChordToRaw_BridgeSpans_Untouched()
    {
        var cs = Enumerable.Range(0, 10).Select(i => new UnifiedCrossSection
        {
            StructureSpanId = 7,
            StructureSpanType = StructureType.Bridge
        }).ToList();
        var raw = Enumerable.Range(0, 10).Select(i => 150f).ToArray();

        var any = OptimizedElevationSmoother.ApplyTunnelChordToRaw(cs, raw);

        Assert.False(any);
        Assert.All(raw, v => Assert.Equal(150f, v));
    }

    [Fact]
    public void ChainSmoothing_TunnelFlagOn_ApproachesAndSpanStayAtPortalLevel()
    {
        var (network, cs) = BuildMountainCorridor();
        var heightMap = BuildMountainHeightmap();

        var parameters = new RoadSmoothingParameters
        {
            CrossSectionIntervalMeters = 0.5f,
            TunnelRules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled()
        };
        RoadNetworkTestHelpers.RunChainSmoothing(network, heightMap, 1f, parameters);

        // The span no longer climbs the +50 m mountain: mid-span solves near the 100 m portal line.
        var mid = cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 200f)).First();
        Assert.True(mid.TargetElevation < 106f,
            $"mid-span z {mid.TargetElevation:F1} — the mountain must not enter the filter");

        // The approach just outside the portal is unpolluted: local grade sane (was ~20% unshaped).
        var a = cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 90f)).First();
        var b = cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 99f)).First();
        var grade = MathF.Abs((b.TargetElevation - a.TargetElevation) /
                              (b.DistanceAlongSpline - a.DistanceAlongSpline));
        Assert.True(grade < 0.05f, $"approach grade {grade * 100f:F1}% — pollution not removed");
    }

    [Fact]
    public void ChainSmoothing_TunnelFlagOff_ByteIdenticalMountainClimb()
    {
        var (network, cs) = BuildMountainCorridor();
        var heightMap = BuildMountainHeightmap();

        RoadNetworkTestHelpers.RunChainSmoothing(network, heightMap, 1f,
            new RoadSmoothingParameters { CrossSectionIntervalMeters = 0.5f }); // no TunnelRules

        // Legacy behavior: the filter sees the mountain and the profile climbs it.
        var mid = cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 200f)).First();
        Assert.True(mid.TargetElevation > 110f,
            $"mid-span z {mid.TargetElevation:F1} — flag off must keep the legacy climb");
    }

    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> cs) BuildMountainCorridor()
    {
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 5L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(50, 150), new Vector2(450, 150),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);

        var network = new UnifiedRoadNetwork();
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines,
            network.CrossSections.GroupBy(c => c.OwnerSplineId)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.LocalIndex).ToList()));
        return (network, cs);
    }

    /// <summary>Terrain 100 m with a +50 m mountain across the span's world-x range [150,350].</summary>
    private static float[,] BuildMountainHeightmap()
    {
        const int size = 512;
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var z = 100f;
            if (x is >= 150 and <= 350)
                z += 50f * MathF.Sin(MathF.PI * (x - 150) / 200f);
            hm[y, x] = z;
        }

        return hm;
    }
}
