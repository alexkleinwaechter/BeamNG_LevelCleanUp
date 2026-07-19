using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Minimal scenario factory for NaturalProfileAnchorTests (doc 09 C1/C2).
/// </summary>
internal static class NaturalProfileAnchorScenario
{
    /// <summary>
    ///     Builds a <see cref="UnifiedRoadNetwork"/> with exactly one junction that has two contributors,
    ///     and an <see cref="UnifiedRoadNetwork.EarlyElevationEstimate"/> mapping each contributor's
    ///     cross-section index to the given A0 values.
    /// </summary>
    public static (UnifiedRoadNetwork network, NetworkJunction junction)
        TwoContributorJunction(float a0First, float a0Second)
    {
        // Two straight splines crossing at (250, 150).
        var splineA = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new Vector2(50, 150), new Vector2(450, 150));
        var splineB = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new Vector2(250, 50), new Vector2(250, 350));

        var network = new UnifiedRoadNetwork();
        var csA = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, splineA);
        var csB = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, splineB);

        // Pick the cross-sections closest to the crossing point as contributors.
        var contributorA = csA.OrderBy(c => Vector2.Distance(c.CenterPoint, new Vector2(250, 150))).First();
        var contributorB = csB.OrderBy(c => Vector2.Distance(c.CenterPoint, new Vector2(250, 150))).First();

        var junction = new NetworkJunction
        {
            JunctionId = 1,
            Type = JunctionType.MidSplineCrossing,
            Position = new Vector2(250, 150),
        };
        junction.Contributors.Add(new JunctionContributor { CrossSection = contributorA, Spline = splineA });
        junction.Contributors.Add(new JunctionContributor { CrossSection = contributorB, Spline = splineB });
        network.Junctions.Add(junction);

        network.EarlyElevationEstimate = new Dictionary<int, float>
        {
            [contributorA.Index] = a0First,
            [contributorB.Index] = a0Second,
        };

        return (network, junction);
    }
}
