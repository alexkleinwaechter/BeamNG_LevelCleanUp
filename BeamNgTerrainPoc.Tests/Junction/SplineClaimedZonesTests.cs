using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class SplineClaimedZonesTests
{
    private static List<UnifiedCrossSection> BuildLinearSpline(int id, int n, float spacing)
    {
        var sections = new List<UnifiedCrossSection>();
        for (var i = 0; i < n; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                Index = id * 1000 + i,
                LocalIndex = i,
                OwnerSplineId = id,
                CenterPoint = new Vector2(i * spacing, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = 100f,
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            });
        }
        return sections;
    }

    [Fact]
    public void Build_SplineWithStartAndEndConstraints_BothClaimsPopulated()
    {
        var sections = BuildLinearSpline(id: 64, n: 100, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (64, true), new JunctionEndpointConstraint
                {
                    Elevation = 184.4f, Slope = -0.084f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 100f,
                    Junction = new NetworkJunction { JunctionId = 125 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            },
            {
                (64, false), new JunctionEndpointConstraint
                {
                    Elevation = 158.98f, Slope = 0.0011f, BankAngleRadians = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 100f,
                    Junction = new NetworkJunction { JunctionId = 126 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.True(zones.TryGetValue(64, out var zone));
        Assert.Equal(99f, zone!.RoadLength, 2);
        Assert.NotNull(zone.StartClaim);
        Assert.Equal(125, zone.StartClaim!.JunctionId);
        Assert.Equal(100f, zone.StartClaim.BlendDistanceMeters, 2);
        Assert.NotNull(zone.EndClaim);
        Assert.Equal(126, zone.EndClaim!.JunctionId);
        Assert.Equal(100f, zone.EndClaim.BlendDistanceMeters, 2);
    }

    [Fact]
    public void Build_StartOnly_EndClaimNull()
    {
        var sections = BuildLinearSpline(id: 7, n: 50, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 7, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (7, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 1 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.True(zones.TryGetValue(7, out var zone));
        Assert.NotNull(zone!.StartClaim);
        Assert.Null(zone.EndClaim);
    }

    [Fact]
    public void Build_NoConstraintsForSpline_SplineMissingFromResult()
    {
        var sections = BuildLinearSpline(id: 99, n: 10, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 99, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>();

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.False(zones.ContainsKey(99));
    }

    [Fact]
    public void Build_DistFromStartByCsIndex_MatchesCumulativeCenterPointDistances()
    {
        var sections = BuildLinearSpline(id: 5, n: 4, spacing: 2.5f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 5, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (5, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 10f,
                    Junction = new NetworkJunction { JunctionId = 1 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var zone = zones[5];

        Assert.Equal(0f, zone.DistFromStartByCsIndex[5 * 1000 + 0], 3);
        Assert.Equal(2.5f, zone.DistFromStartByCsIndex[5 * 1000 + 1], 3);
        Assert.Equal(5.0f, zone.DistFromStartByCsIndex[5 * 1000 + 2], 3);
        Assert.Equal(7.5f, zone.DistFromStartByCsIndex[5 * 1000 + 3], 3);
        Assert.Equal(7.5f, zone.RoadLength, 3);
    }

    [Fact]
    public void Build_PropagatedConstraintsAreIncluded()
    {
        var sections = BuildLinearSpline(id: 12, n: 20, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 12, sections } };

        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (12, false), new JunctionEndpointConstraint
                {
                    Elevation = 50f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 12f,
                    Junction = new NetworkJunction { JunctionId = 42 },
                    PrimaryTangentDirection = new Vector2(1f, 0f),
                    IsPropagated = true,
                    PropagatedThroughSplineId = 11
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);

        Assert.NotNull(zones[12].EndClaim);
        Assert.Equal(42, zones[12].EndClaim!.JunctionId);
    }

    [Fact]
    public void GetTaperFor_CsAtStartAnchor_DifferentJunction_ReturnsZero()
    {
        var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (1, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 7 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var csIndex = sections[0].Index;

        var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
        Assert.Equal(0f, taper, 4);
    }

    [Fact]
    public void GetTaperFor_CsAtStartAnchor_SameJunction_ReturnsOne()
    {
        var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (1, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 7 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var csIndex = sections[0].Index;

        var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 7);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void GetTaperFor_CsOutsideAnyZone_ReturnsOne()
    {
        var sections = BuildLinearSpline(id: 1, n: 100, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (1, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 20f,
                    Junction = new NetworkJunction { JunctionId = 7 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            },
            {
                (1, false), new JunctionEndpointConstraint
                {
                    Elevation = 90f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 20f,
                    Junction = new NetworkJunction { JunctionId = 8 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var csIndex = sections[50].Index;

        var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
        Assert.Equal(1f, taper, 4);
    }

    [Fact]
    public void GetTaperFor_CsInBothZones_TakesMinimum()
    {
        var sections = BuildLinearSpline(id: 1, n: 10, spacing: 1f);
        var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 1, sections } };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (1, true), new JunctionEndpointConstraint
                {
                    Elevation = 100f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = true,
                    BlendDistanceMeters = 8f,
                    Junction = new NetworkJunction { JunctionId = 1 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            },
            {
                (1, false), new JunctionEndpointConstraint
                {
                    Elevation = 90f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 8f,
                    Junction = new NetworkJunction { JunctionId = 2 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };

        var zones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        var csIndex = sections[2].Index;
        var taper = SplineClaimedZones.GetTaperFor(zones[1], csIndex, sourceJunctionId: 99);
        Assert.Equal(0.15625f, taper, 4);
    }
}
