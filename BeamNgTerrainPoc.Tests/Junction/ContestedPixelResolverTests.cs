using BeamNgTerrainPoc.Terrain.Algorithms.Blending;

namespace BeamNgTerrainPoc.Tests.Junction;

public class ContestedPixelResolverTests
{
    private static SplineOverlapMetadata Meta(int splineId, int priority = 0, float length = 0f) =>
        new(splineId, priority, length);

    [Fact]
    public void Resolve_FlagOff_DifferentSplines_KeepsExistingClaim()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_HigherPriorityCandidate_TakesOwnership()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_LowerPriorityCandidate_KeepsExisting()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 9),  existingElev: 100f,
            candidate: Meta(20, priority: 5), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriority_LongerCandidate_TakesOwnership()
    {
        // Tier 2 of the cascade: equal Priority but candidate is the longer spline →
        // candidate wins (proxy for "through road > terminating side road").
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 7, length: 80f),   existingElev: 100f,
            candidate: Meta(20, priority: 7, length: 250f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriority_ShorterCandidate_KeepsExisting()
    {
        // Symmetric tier-2 check: candidate is shorter → existing keeps the pixel.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 7, length: 250f), existingElev: 100f,
            candidate: Meta(20, priority: 7, length: 80f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriorityAndLength_LowerSplineIdCandidateWins()
    {
        // Tier 3 of the cascade: priority + length both tied → lower SplineId wins.
        // Deterministic tiebreaker; never depends on iteration order.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: 20, priority: 7, length: 100f), existingElev: 100f,
            candidate: Meta(splineId: 10, priority: 7, length: 100f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass1_EqualPriorityAndLength_HigherSplineIdCandidateKeepsExisting()
    {
        // Symmetric tier-3 check: candidate has higher SplineId → existing keeps the pixel.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: 10, priority: 7, length: 100f), existingElev: 100f,
            candidate: Meta(splineId: 20, priority: 7, length: 100f), candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOn_Pass2_HigherPriorityCandidate_StillKeepsExisting()
    {
        // Pass 2 (corridor stamp) is unaffected even when the flag is on.
        // A.8's invariant: surface pixels claimed in Pass 1 stay claimed; A.8.2 does NOT
        // weaken that. Same applies to Pass 2 vs Pass 2 — width-first wins.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(20, priority: 9), candidateElev: 110f,
            useSurfaceWidthOnly: false,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(100f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_SameSpline_TakesMinElevation()
    {
        // Same-spline re-stamp: banking refinement preserves the lower elevation.
        // This mirrors RoadMaskBuilder L381-385.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(10, priority: 5), candidateElev: 95f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(95f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_SameSpline_KeepsLowerExistingIfCandidateHigher()
    {
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 95f,
            candidate: Meta(10, priority: 5), candidateElev: 100f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: true);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(95f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_FlagOff_SameSpline_StillTakesMinElevation()
    {
        // Same-spline lower-elevation rule applies regardless of the priority-override flag.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(10, priority: 5),  existingElev: 100f,
            candidate: Meta(10, priority: 5), candidateElev: 92f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.False(outcome.TakeOwnership);
        Assert.Equal(92f, outcome.NewElevation, 4);
    }

    [Fact]
    public void Resolve_UnclaimedExisting_CandidateAlwaysWins()
    {
        // Defensive — caller's outer `if (mask[y,x] == 0)` handles this separately, but
        // Resolve should still return coherent values if called with existing.SplineId = -1.
        var outcome = ContestedPixelResolver.Resolve(
            existing: Meta(splineId: -1, priority: 0),  existingElev: float.NaN,
            candidate: Meta(20, priority: 0),           candidateElev: 110f,
            useSurfaceWidthOnly: true,
            enableSurfacePriorityOverride: false);

        Assert.True(outcome.TakeOwnership);
        Assert.Equal(110f, outcome.NewElevation, 4);
    }
}
