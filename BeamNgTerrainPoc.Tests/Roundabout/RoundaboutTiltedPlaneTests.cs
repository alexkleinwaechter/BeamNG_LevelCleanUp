using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Roundabout;

public class RoundaboutTiltedPlaneTests
{
    private static UnifiedCrossSection RingCs(int idx, Vector2 center) => new()
    {
        OwnerSplineId = 1,
        LocalIndex = idx,
        Index = 100 + idx,
        CenterPoint = center,
        TangentDirection = new Vector2(1f, 0f),
        NormalDirection = new Vector2(0f, 1f),
        TargetElevation = 0f
    };

    // Four ring cross-sections around a ~10 m circle centered at (50,50).
    private static List<UnifiedCrossSection> Ring() => new()
    {
        RingCs(0, new Vector2(60f, 50f)),
        RingCs(1, new Vector2(50f, 60f)),
        RingCs(2, new Vector2(40f, 50f)),
        RingCs(3, new Vector2(50f, 40f)),
    };

    [Fact]
    public void TiltedTerrain_RingFollowsPlane()
    {
        var ring = Ring();
        // Terrain tilts +0.02 along x: z = 0.02*(x-50) + 100.
        float Terrain(Vector2 p) => 0.02f * (p.X - 50f) + 100f;

        var preTilt = RoundaboutElevationHarmonizer.ApplyTiltedRingPlane(ring, Terrain, 0.06f);

        Assert.Equal(0.02f, preTilt, 3);
        // East cs (x=60) sits 0.2 m above the west cs (x=40); not uniform.
        var east = ring.First(c => c.CenterPoint.X == 60f).TargetElevation;
        var west = ring.First(c => c.CenterPoint.X == 40f).TargetElevation;
        Assert.Equal(0.4f, east - west, 2);
    }

    [Fact]
    public void FlatTerrain_RingUniform()
    {
        var ring = Ring();
        float Terrain(Vector2 _) => 100f;

        RoundaboutElevationHarmonizer.ApplyTiltedRingPlane(ring, Terrain, 0.06f);

        foreach (var cs in ring) Assert.Equal(100f, cs.TargetElevation, 3);
    }

    [Fact]
    public void SkewedTerrain_RingMinimizesWorstCaseEmbankment_BalancedCutFill()
    {
        // One ring cross-section sits over a high spur (104), the other three over flat ground (100).
        // The clamped (6%) tilt cannot follow the spur, leaving residuals; the ring must center on the
        // residual MIDRANGE so the deepest cut equals the highest fill (smallest possible embankment).
        // Hand-computed: tilt clamps to a=0.06,b=0 (pivot mean 101); residual shift = +0.7 → max fill and
        // max cut both 1.7 m. (Mean pivot would leave +2.4/−1.0; terrain-midrange would be worse still.)
        var ring = Ring();
        float Terrain(Vector2 p) => p.X == 60f ? 104f : 100f;

        RoundaboutElevationHarmonizer.ApplyTiltedRingPlane(ring, Terrain, 0.06f);

        var dev = ring.Select(cs => Terrain(cs.CenterPoint) - cs.TargetElevation).ToList();
        Assert.Equal(1.7f, dev.Max(), 2);   // highest fill
        Assert.Equal(-1.7f, dev.Min(), 2);  // deepest cut — equal magnitude → worst-case minimized
    }
}
