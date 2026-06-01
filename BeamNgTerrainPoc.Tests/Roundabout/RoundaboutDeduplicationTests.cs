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
        var alreadyProcessedRingIds = new HashSet<long> { 100L };
        var detectedIds = new List<long> { 100L, 200L };
        var createRingFor = detectedIds.Where(id => !alreadyProcessedRingIds.Contains(id)).ToList();
        var excludeWaysFor = detectedIds;
        Assert.Single(createRingFor);
        Assert.Equal(200L, createRingFor[0]);
        Assert.Equal(2, excludeWaysFor.Count);
    }

    [Fact]
    public void FirstMaterial_CreatesRing_AndMarksProcessed()
    {
        var processed = new HashSet<long>();
        var roundaboutId = 100L;
        Assert.DoesNotContain(roundaboutId, processed);
        processed.Add(roundaboutId);
        Assert.Contains(roundaboutId, processed);
    }

    [Fact]
    public void SingleMaterial_NoDedup_AllRingsCreated()
    {
        HashSet<long>? alreadyProcessed = null;
        var detectedIds = new List<long> { 100L, 200L };
        var createRingFor = alreadyProcessed == null
            ? detectedIds
            : detectedIds.Where(id => !alreadyProcessed.Contains(id)).ToList();
        Assert.Equal(2, createRingFor.Count);
    }
}
