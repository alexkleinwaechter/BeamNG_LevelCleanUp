using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Phase C (plan doc 14 §7, §11): the bridge-deck pin (<see cref="UnifiedCrossSection.PinnedElevation"/>) must
/// survive the FOUR elevation passes — the chain box low-pass (input overwrite + hard-hold), the re-smooth
/// iterations, the affine leveler (exempted + boundary-blended, D6) and the max-slope clamp (pinned neighbourhood
/// exempted) — so the smoother holds the deck and BUILDS the rising approach ramps instead of dragging the deck
/// to terrain. Non-pinned smoothing stays byte-identical.
/// </summary>
public class BridgeDeckPinTests
{
    // A flat-terrain corridor with a pinned "deck" plateau in the middle. Cross-sections at 1 m spacing; the
    // tighter smoothing window makes the box-filter ramp visible (default 301 would barely lift a short deck).
    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> cs, RoadSmoothingParameters prms)
        PinnedCorridor(float deckZ = 10f, float spanStart = 150f, float spanEnd = 250f, int window = 41,
            bool maxSlope = false)
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(0, 150), new(400, 150));
        var network = new UnifiedRoadNetwork();
        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        foreach (var c in cs)
            if (c.DistanceAlongSpline >= spanStart && c.DistanceAlongSpline <= spanEnd)
                c.PinnedElevation = deckZ;

        var prms = new RoadSmoothingParameters
        {
            CrossSectionIntervalMeters = 1f,
            EnableMaxSlopeConstraint = maxSlope,
            RoadMaxSlopeDegrees = 3f,
            // Box filter (not Butterworth) so the ramp is a clean monotone average, not a ringing IIR step
            // response — the deck hard-hold works under either, but monotonicity is a box-filter property.
            SplineParameters = new SplineRoadParameters { SmoothingWindowSize = window, UseButterworthFilter = false },
        };
        return (network, cs, prms);
    }

    private static List<UnifiedCrossSection> DeckSections(List<UnifiedCrossSection> cs) =>
        cs.Where(c => c.PinnedElevation.HasValue).OrderBy(c => c.DistanceAlongSpline).ToList();

    // Runs chain smoothing for `iterations` passes (iteration 0 = CalculateChainElevations, 1+ = ReSmooth).
    private static void RunIterations(
        UnifiedRoadNetwork network, float[,] hm, RoadSmoothingParameters prms, int iterations)
    {
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();
        var csBySpline = network.CrossSections
            .GroupBy(c => c.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.LocalIndex).ToList());

        var smoother = new OptimizedElevationSmoother();
        for (var it = 0; it < iterations; it++)
        foreach (var chain in chains)
        {
            var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(
                chain, csBySpline, prms.CrossSectionIntervalMeters);
            if (it == 0)
                smoother.CalculateChainElevations(chainCS, prms, hm, 1f);
            else
                smoother.ReSmoothChainFromExistingElevations(chainCS, prms);
            OptimizedElevationSmoother.PropagateToDeduped(chain);
        }
    }

    // ── Pass 1: box low-pass holds the deck and grows rising ramps ────────────────────────────────────────

    [Fact]
    public void BoxLowPass_HoldsDeck_AndApproachRampsRiseMonotonically()
    {
        var (network, cs, prms) = PinnedCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        RunIterations(network, hm, prms, iterations: 1);

        // Deck held exactly at the pin.
        Assert.All(DeckSections(cs), c => Assert.Equal(10f, c.TargetElevation, 0.01f));

        // Left approach rises monotonically toward the deck and reaches well above terrain at the abutment.
        var left = cs.Where(c => c.DistanceAlongSpline < 150f)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < left.Count; i++)
            Assert.True(left[i].TargetElevation >= left[i - 1].TargetElevation - 0.01f,
                $"ramp not monotone at {left[i].DistanceAlongSpline}m");
        Assert.True(left[^1].TargetElevation > 2f, "approach did not rise toward the deck");
        Assert.True(left[0].TargetElevation < 1f, "far approach should still sit near terrain");
    }

    // ── Pass 2: pin survives 3 iterations ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Pin_SurvivesThreeReSmoothIterations()
    {
        var (network, cs, prms) = PinnedCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        RunIterations(network, hm, prms, iterations: 3);

        Assert.All(DeckSections(cs), c => Assert.Equal(10f, c.TargetElevation, 0.01f));
    }

    // ── Pass 4: max-slope clamp does not flatten the pinned deck/ramp ─────────────────────────────────────

    [Fact]
    public void MaxSlopeClampOn_DoesNotFlattenPinnedDeck()
    {
        // A 3° clamp at 1 m spacing limits rise to ~0.05 m/section — without the pin exemption it would drag the
        // 10 m deck down to terrain. The exemption must keep it.
        var (network, cs, prms) = PinnedCorridor(maxSlope: true);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        RunIterations(network, hm, prms, iterations: 1);

        Assert.All(DeckSections(cs), c => Assert.Equal(10f, c.TargetElevation, 0.01f));
    }

    // ── Pass 3: affine leveler exempts the pinned deck, blending the correction (D6) ──────────────────────

    [Fact]
    public void AffineCore_DoesNotTiltPinnedDeck_AndStillHitsEndpointTargets()
    {
        // 401 sections at 1 m. Baseline 5 m everywhere, deck [150,250] pinned to 10. Endpoints carry an error vs
        // their junction targets (start wants +2). The affine must (a) hold the deck at 10, (b) still land the
        // free start on its target, (c) blend with no step at the abutment.
        const int n = 401;
        var elev = new float[n];
        var dist = new float[n];
        var pinned = new bool[n];
        for (var i = 0; i < n; i++)
        {
            dist[i] = i;
            var inDeck = i is >= 150 and <= 250;
            elev[i] = inDeck ? 10f : 5f;
            pinned[i] = inDeck;
        }

        UnifiedRoadSmoother.ApplyAffineLevelingCore(elev, dist, pinned, targetStart: 7f, targetEnd: 5f);

        // Deck untouched.
        for (var i = 150; i <= 250; i++)
            Assert.Equal(10f, elev[i], 0.001f);

        // Endpoints (far from the deck → full affine weight) hit their targets.
        Assert.Equal(7f, elev[0], 0.05f);
        Assert.Equal(5f, elev[n - 1], 0.05f);

        // No step at the abutment: the section just outside the deck is between its corrected baseline and 10.
        Assert.InRange(elev[149], 5f, 10f);
        Assert.InRange(elev[251], 5f, 10f);
    }

    [Fact]
    public void AffinePinWeights_ZeroOnPin_RampUpToOneAwayFromIt()
    {
        var dist = new float[101];
        var pinned = new bool[101];
        for (var i = 0; i < 101; i++)
        {
            dist[i] = i;
            pinned[i] = i is >= 40 and <= 60;
        }

        var w = UnifiedRoadSmoother.BuildAffinePinWeights(dist, pinned);

        Assert.Equal(0f, w[50], 0.001f);                    // on the pin
        Assert.Equal(0f, w[40], 0.001f);                    // pin edge
        Assert.Equal(1f, w[0], 0.001f);                     // 40 m away ⇒ full blend (AffinePinBlendMeters)
        Assert.Equal(1f, w[100], 0.001f);                   // 40 m past the far pin edge
        Assert.InRange(w[20], 0.01f, 0.99f);                // 20 m from the pin ⇒ mid-blend
        // Monotone rising away from the pin on the left side.
        for (var i = 1; i <= 40; i++)
            Assert.True(w[40 - i] >= w[40 - i + 1] - 1e-4f);
    }

    [Fact]
    public void AffineCore_NoPins_IsByteIdenticalToPlainAffine()
    {
        const int n = 200;
        var dist = new float[n];
        var elevA = new float[n];
        for (var i = 0; i < n; i++)
        {
            dist[i] = i;
            elevA[i] = 5f + 0.01f * i;
        }

        var elevB = (float[])elevA.Clone();

        AffineJunctionLeveler.Apply(elevA, dist, 3f, 9f);
        UnifiedRoadSmoother.ApplyAffineLevelingCore(elevB, dist, pinned: null, targetStart: 3f, targetEnd: 9f);

        for (var i = 0; i < n; i++)
            Assert.Equal(elevA[i], elevB[i], 0.0001f);
    }
}
