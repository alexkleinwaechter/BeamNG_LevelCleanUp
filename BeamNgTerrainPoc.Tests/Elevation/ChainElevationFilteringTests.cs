using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Elevation;

public class ChainElevationFilteringTests
{
    [Fact]
    public void ConcatenateChain_DeduplicatesColocatedEndpoints()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(190, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.True(chains.Count >= 1);

        var csBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var concatenated = OptimizedElevationSmoother.ConcatenateChainCrossSections(
            chains[0], csBySpline, 1.0f);

        var totalRaw = csBySpline.Values.Sum(cs => cs.Count);
        // Dedup should remove at least 1 co-located endpoint
        Assert.True(concatenated.Count < totalRaw,
            $"Dedup should reduce count: {concatenated.Count} < {totalRaw}");
        Assert.True(concatenated.Count >= totalRaw - 1,
            $"Should only dedup ~1 CS: {concatenated.Count} >= {totalRaw - 1}");
    }

    [Fact]
    public void TwoSplineChain_SmoothAcrossBoundary()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(150, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 50), new(290, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var hm = RoadNetworkTestHelpers.CreateStepHeightmap(300, 150, 100f, 105f);

        var (_, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        var lastOfS1 = csBySpline[1].Last().TargetElevation;
        var firstOfS2 = csBySpline[2].First().TargetElevation;
        var gap = MathF.Abs(lastOfS1 - firstOfS2);

        Assert.True(gap < 0.5f,
            $"Elevation gap at boundary should be small: {gap:F3}m (last={lastOfS1:F2}, first={firstOfS2:F2})");
    }

    [Fact]
    public void TwoSplineChain_VsPerSpline_SmallerDiscontinuityAtJoint()
    {
        var hm = RoadNetworkTestHelpers.CreateStepHeightmap(300, 150, 100f, 110f);
        var smoother = new OptimizedElevationSmoother();
        var parameters = new RoadSmoothingParameters { CrossSectionIntervalMeters = 0.5f };

        // --- Per-spline filtering ---
        var networkPS = RoadNetworkTestHelpers.BuildNetworkWithJunctions(
            RoadNetworkTestHelpers.CreateParameterizedSpline(10, new(10, 50), new(150, 50), "primary", 50),
            RoadNetworkTestHelpers.CreateParameterizedSpline(20, new(150, 50), new(290, 50), "primary", 50));
        var csBySplinePS = networkPS.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        smoother.CalculateTargetElevations(csBySplinePS[10], parameters, hm, 1f);
        smoother.CalculateTargetElevations(csBySplinePS[20], parameters, hm, 1f);

        var psGap = MathF.Abs(csBySplinePS[10].Last().TargetElevation - csBySplinePS[20].First().TargetElevation);

        // --- Chain-based filtering ---
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(150, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 50), new(290, 50), "primary", 50);
        var networkCB = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);

        var (_, csBySplineCB) = RoadNetworkTestHelpers.RunChainSmoothing(networkCB, hm, parameters: parameters);

        var cbGap = MathF.Abs(csBySplineCB[1].Last().TargetElevation - csBySplineCB[2].First().TargetElevation);

        Assert.True(cbGap <= psGap + 0.01f,
            $"Chain gap ({cbGap:F3}m) should be <= per-spline gap ({psGap:F3}m)");
    }

    [Fact]
    public void ReversedSplineInChain_CorrectWriteBack()
    {
        // s1: left to right, s2: right to left (will be reversed in chain)
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(190, 50), new(100, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(200, 50f);

        var (_, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        // All cross-sections should have valid elevations (not NaN)
        foreach (var cs in network.CrossSections)
            Assert.False(float.IsNaN(cs.TargetElevation),
                $"CS {cs.OwnerSplineId}:{cs.LocalIndex} should have valid elevation");
    }

    [Fact]
    public void BridgeSpline_GetsTargetElevation_ButTerrainUntouched()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        // Mark bridge CS as excluded (as the smoother would do)
        foreach (var cs in network.CrossSections.Where(cs => cs.OwnerSplineId == 2))
            cs.IsExcluded = true;

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(300, 75f);

        var (_, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        // Bridge cross-sections should have valid TargetElevation AND remain IsExcluded
        var bridgeCS = csBySpline[2];
        foreach (var cs in bridgeCS)
        {
            Assert.False(float.IsNaN(cs.TargetElevation),
                $"Bridge CS {cs.LocalIndex} should have valid elevation (virtual data for ramp matching)");
            Assert.True(cs.IsExcluded,
                $"Bridge CS {cs.LocalIndex} should remain IsExcluded=true (terrain under bridge untouched)");
        }
    }

    [Fact]
    public void SlopeConstraint_OnChain_NoKinkAtJoint()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(150, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 50), new(290, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);

        // Steep slope: 100m to 200m over 290m = ~19° >> 6° constraint
        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(300, 100f, 200f);

        var parameters = new RoadSmoothingParameters
        {
            CrossSectionIntervalMeters = 0.5f,
            EnableMaxSlopeConstraint = true,
            RoadMaxSlopeDegrees = 6f
        };

        var (_, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm, parameters: parameters);

        var lastS1 = csBySpline[1].Last().TargetElevation;
        var firstS2 = csBySpline[2].First().TargetElevation;
        var gap = MathF.Abs(lastS1 - firstS2);

        Assert.True(gap < 1.0f,
            $"Slope-constrained elevation gap at joint should be small: {gap:F3}m");
    }

    [Fact]
    public void ShortSpline_BenefitsFromChainContext()
    {
        // Noisy terrain (zigzag pattern)
        var hm = new float[250, 250];
        for (var y = 0; y < 250; y++)
        for (var x = 0; x < 250; x++)
            hm[y, x] = 100f + (x % 10 < 5 ? 3f : -3f);

        var smoother = new OptimizedElevationSmoother();
        var parameters = new RoadSmoothingParameters { CrossSectionIntervalMeters = 0.5f };

        // --- Chain-based: short (30m) + long (200m) ---
        var shortSpline = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(40, 50), "primary", 50);
        var longSpline = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(40, 50), new(240, 50), "primary", 50);
        var networkCB = RoadNetworkTestHelpers.BuildNetworkWithJunctions(shortSpline, longSpline);

        var (_, csBySplineCB) = RoadNetworkTestHelpers.RunChainSmoothing(networkCB, hm, parameters: parameters);

        var shortCS_CB = csBySplineCB[1];
        var cbMean = shortCS_CB.Average(cs => cs.TargetElevation);
        var cbVariance = shortCS_CB.Average(cs => MathF.Pow(cs.TargetElevation - cbMean, 2));

        // --- Per-spline: short alone ---
        var shortOnly = RoadNetworkTestHelpers.CreateParameterizedSpline(10, new(10, 50), new(40, 50), "primary", 50);
        var networkPS = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(networkPS, shortOnly);
        var shortCS_PS = networkPS.CrossSections.OrderBy(cs => cs.LocalIndex).ToList();
        smoother.CalculateTargetElevations(shortCS_PS, parameters, hm, 1f);
        var psMean = shortCS_PS.Average(cs => cs.TargetElevation);
        var psVariance = shortCS_PS.Average(cs => MathF.Pow(cs.TargetElevation - psMean, 2));

        Assert.True(cbVariance <= psVariance + 0.1f,
            $"Chain variance ({cbVariance:F4}) should be <= per-spline variance ({psVariance:F4})");
    }

    [Fact]
    public void ChainAssignsChainIdAndIndex()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(190, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        var csBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var concatenated = OptimizedElevationSmoother.ConcatenateChainCrossSections(
            chains[0], csBySpline, 0.5f);

        Assert.NotEmpty(concatenated);
        Assert.All(concatenated, cs =>
        {
            Assert.Equal(chains[0].ChainId, cs.ChainId);
            Assert.True(cs.ChainIndex >= 0);
        });

        // ChainIndex should be monotonically increasing
        for (var i = 1; i < concatenated.Count; i++)
            Assert.True(concatenated[i].ChainIndex > concatenated[i - 1].ChainIndex,
                $"ChainIndex should be monotonic at position {i}");
    }

    [Fact]
    public void ReSmoothIteration_UsesChainConcatenation()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(150, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 50), new(290, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var hm = RoadNetworkTestHelpers.CreateStepHeightmap(300, 150, 100f, 110f);
        var parameters = new RoadSmoothingParameters { CrossSectionIntervalMeters = 0.5f };

        // Iteration 0: initial chain smoothing
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        var csBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var smoother = new OptimizedElevationSmoother();

        foreach (var chain in chains)
        {
            var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(chain, csBySpline, 0.5f);
            smoother.CalculateChainElevations(chainCS, parameters, hm, 1f);
            OptimizedElevationSmoother.PropagateToDeduped(chain);
        }

        var gapIter0 = MathF.Abs(csBySpline[1].Last().TargetElevation - csBySpline[2].First().TargetElevation);

        // Iteration 1: re-smooth from existing elevations, still using chains
        foreach (var chain in chains)
        {
            var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(chain, csBySpline, 0.5f);
            smoother.ReSmoothChainFromExistingElevations(chainCS, parameters);
            OptimizedElevationSmoother.PropagateToDeduped(chain);
        }

        var gapIter1 = MathF.Abs(csBySpline[1].Last().TargetElevation - csBySpline[2].First().TargetElevation);

        // Re-smooth should maintain or improve the boundary (not reintroduce artifacts)
        Assert.True(gapIter1 <= gapIter0 + 0.1f,
            $"Re-smooth gap ({gapIter1:F3}m) should not be much worse than iter0 gap ({gapIter0:F3}m)");
    }
}
