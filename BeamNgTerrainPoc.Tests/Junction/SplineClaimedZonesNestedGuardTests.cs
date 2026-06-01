using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class SplineClaimedZonesNestedGuardTests
{
    private static SplineClaimedZone BuildZone(
        float roadLength,
        (int junctionId, float blendDist)? startClaim,
        (int junctionId, float blendDist)? endClaim)
    {
        var dist = new Dictionary<int, float>();
        for (var i = 0; i < (int)roadLength + 1; i++)
            dist[i] = i;

        return new SplineClaimedZone
        {
            SplineId = 1,
            RoadLength = roadLength,
            StartClaim = startClaim.HasValue
                ? new SplineEndClaim { JunctionId = startClaim.Value.junctionId, BlendDistanceMeters = startClaim.Value.blendDist }
                : null,
            EndClaim = endClaim.HasValue
                ? new SplineEndClaim { JunctionId = endClaim.Value.junctionId, BlendDistanceMeters = endClaim.Value.blendDist }
                : null,
            DistFromStartByCsIndex = dist
        };
    }

    [Fact]
    public void HasOtherClaimNear_DistInsideOwnStartClaim_OwnAnchorIsStart_ReturnsFalse()
    {
        // Own anchor is the start. Sample point at d=15 (inside start blend zone, L=30).
        // No OTHER claim → returns false.
        var zone = BuildZone(100f, startClaim: (7, 30f), endClaim: null);
        var result = SplineClaimedZones.HasOtherClaimNear(zone, distFromStart: 15f, ownAnchorIsStart: true, marginMeters: 0f);
        Assert.False(result);
    }

    [Fact]
    public void HasOtherClaimNear_DistInsideOtherEndClaim_ReturnsTrue()
    {
        // Own anchor is the start. Sample point at d=80 (within 100-L=70..100 of end claim L=30).
        // End claim is from a DIFFERENT junction → returns true.
        var zone = BuildZone(100f, startClaim: (7, 30f), endClaim: (8, 30f));
        var result = SplineClaimedZones.HasOtherClaimNear(zone, distFromStart: 80f, ownAnchorIsStart: true, marginMeters: 0f);
        Assert.True(result);
    }

    [Fact]
    public void HasOtherClaimNear_DistOutsideAllClaims_ReturnsFalse()
    {
        var zone = BuildZone(100f, startClaim: (7, 20f), endClaim: (8, 20f));
        // d=50 is outside [0,20] and outside [80,100].
        var result = SplineClaimedZones.HasOtherClaimNear(zone, distFromStart: 50f, ownAnchorIsStart: true, marginMeters: 0f);
        Assert.False(result);
    }

    [Fact]
    public void HasOtherClaimNear_OwnAnchorIsEnd_OtherClaimIsStart_DistInStartZone_ReturnsTrue()
    {
        // Own = end claim (junction 8). Sample point at d=10 (inside start claim L=30 from junction 7).
        var zone = BuildZone(100f, startClaim: (7, 30f), endClaim: (8, 30f));
        var result = SplineClaimedZones.HasOtherClaimNear(zone, distFromStart: 10f, ownAnchorIsStart: false, marginMeters: 0f);
        Assert.True(result);
    }

    [Fact]
    public void HasOtherClaimNear_MarginExpandsZone()
    {
        // End claim covers [70,100]. Sample at d=68 — 2m outside. With margin=5, it should be inside.
        var zone = BuildZone(100f, startClaim: null, endClaim: (8, 30f));
        var resultNoMargin = SplineClaimedZones.HasOtherClaimNear(zone, 68f, ownAnchorIsStart: true, marginMeters: 0f);
        var resultWithMargin = SplineClaimedZones.HasOtherClaimNear(zone, 68f, ownAnchorIsStart: true, marginMeters: 5f);
        Assert.False(resultNoMargin);
        Assert.True(resultWithMargin);
    }
}
