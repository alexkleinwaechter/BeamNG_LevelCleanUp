using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadNetworkSnapshotTests
{
    [Fact]
    public void RoundTrip_EmptySnapshot_Succeeds()
    {
        var snapshot = new DecalRoadNetworkSnapshot();

        var deserialized = RoundTrip(snapshot);

        Assert.Empty(deserialized.Splines);
        Assert.Empty(deserialized.CrossSections);
        Assert.Empty(deserialized.Junctions);
    }

    [Fact]
    public void RoundTrip_SplineData_PreservesAllFields()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 42,
            OsmRoadType = "primary",
            MaterialName = "Asphalt",
            IsBridge = true,
            IsTunnel = false,
            Priority = 8000,
            RoadWidthMeters = 7.5f,
            RoadSurfaceWidthMeters = 6.0f,
            MasterSplineWidthMeters = 5.5f,
            TerrainAffectedRangeMeters = 12.0f,
            StartPoint = new Vector2(100, 200),
            EndPoint = new Vector2(300, 400),
            TotalLengthMeters = 283.0f
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.Splines);
        var s = result.Splines[0];
        Assert.Equal(42, s.SplineId);
        Assert.Equal("primary", s.OsmRoadType);
        Assert.Equal("Asphalt", s.MaterialName);
        Assert.True(s.IsBridge);
        Assert.False(s.IsTunnel);
        Assert.Equal(8000, s.Priority);
        Assert.Equal(7.5f, s.RoadWidthMeters);
        Assert.Equal(6.0f, s.RoadSurfaceWidthMeters);
        Assert.Equal(5.5f, s.MasterSplineWidthMeters);
        Assert.Equal(12.0f, s.TerrainAffectedRangeMeters);
        Assert.Equal(100f, s.StartPoint.X);
        Assert.Equal(200f, s.StartPoint.Y);
        Assert.Equal(300f, s.EndPoint.X);
        Assert.Equal(400f, s.EndPoint.Y);
        Assert.Equal(283.0f, s.TotalLengthMeters);
        Assert.Null(s.LaneSegments);
    }

    [Fact]
    public void RoundTrip_SplineWithLaneSegments_PreservesLaneData()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 1,
            MaterialName = "Asphalt",
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(100, 0),
            TotalLengthMeters = 100,
            LaneSegments =
            [
                new LaneSegmentSnapshot
                {
                    StartPointIndex = 0, StartDistance = 0,
                    TotalLanes = 4, LanesForward = 2, LanesBackward = 2,
                    LanesBothWays = 0, IsOneWay = false
                },
                new LaneSegmentSnapshot
                {
                    StartPointIndex = 5, StartDistance = 50,
                    TotalLanes = 3, LanesForward = 2, LanesBackward = 1,
                    LanesBothWays = 0, IsOneWay = false
                }
            ]
        });

        var result = RoundTrip(snapshot);

        Assert.NotNull(result.Splines[0].LaneSegments);
        Assert.Equal(2, result.Splines[0].LaneSegments!.Count);
        Assert.Equal(4, result.Splines[0].LaneSegments[0].TotalLanes);
        Assert.Equal(3, result.Splines[0].LaneSegments[1].TotalLanes);
        Assert.Equal(50f, result.Splines[0].LaneSegments[1].StartDistance);
    }

    [Fact]
    public void RoundTrip_NullOsmType_SerializesAsEmpty()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 1,
            OsmRoadType = string.Empty,
            MaterialName = "DirtRoad",
            StartPoint = Vector2.Zero,
            EndPoint = Vector2.One,
            TotalLengthMeters = 1
        });

        var result = RoundTrip(snapshot);
        Assert.Equal(string.Empty, result.Splines[0].OsmRoadType);
    }

    [Fact]
    public void RoundTrip_CrossSections_PreservesAllFields()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.CrossSections.Add(new CrossSectionSnapshot
        {
            CenterPoint = new Vector2(50.5f, 100.3f),
            NormalDirection = new Vector2(0.0f, 1.0f),
            TargetElevation = 125.7f,
            OwnerSplineId = 7,
            LocalIndex = 42,
            DistanceAlongSpline = 84.2f,
            EffectiveRoadWidth = 8.0f,
            Curvature = 0.015f,
            IsExcluded = true,
            IsSplineStart = false,
            IsSplineEnd = true
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.CrossSections);
        var cs = result.CrossSections[0];
        Assert.Equal(50.5f, cs.CenterPoint.X);
        Assert.Equal(100.3f, cs.CenterPoint.Y);
        Assert.Equal(0.0f, cs.NormalDirection.X);
        Assert.Equal(1.0f, cs.NormalDirection.Y);
        Assert.Equal(125.7f, cs.TargetElevation);
        Assert.Equal(7, cs.OwnerSplineId);
        Assert.Equal(42, cs.LocalIndex);
        Assert.Equal(84.2f, cs.DistanceAlongSpline);
        Assert.Equal(8.0f, cs.EffectiveRoadWidth);
        Assert.Equal(0.015f, cs.Curvature);
        Assert.True(cs.IsExcluded);
        Assert.False(cs.IsSplineStart);
        Assert.True(cs.IsSplineEnd);
    }

    [Fact]
    public void RoundTrip_NaNTargetElevation_Preserved()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.CrossSections.Add(new CrossSectionSnapshot
        {
            CenterPoint = Vector2.Zero,
            NormalDirection = Vector2.UnitX,
            TargetElevation = float.NaN,
            OwnerSplineId = 1,
            LocalIndex = 0,
        });

        var result = RoundTrip(snapshot);
        Assert.True(float.IsNaN(result.CrossSections[0].TargetElevation));
    }

    [Fact]
    public void RoundTrip_Junctions_PreservesContributorReferences()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Junctions.Add(new JunctionSnapshot
        {
            Position = new Vector2(500, 600),
            Type = (int)JunctionType.TJunction,
            IsExcluded = false,
            Contributors =
            [
                new JunctionContributorSnapshot
                {
                    SplineId = 1,
                    CrossSectionOwnerSplineId = 1,
                    CrossSectionLocalIndex = 10,
                    IsSplineStart = false,
                    IsSplineEnd = true
                },
                new JunctionContributorSnapshot
                {
                    SplineId = 2,
                    CrossSectionOwnerSplineId = 2,
                    CrossSectionLocalIndex = 5,
                    IsSplineStart = false,
                    IsSplineEnd = false
                }
            ]
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.Junctions);
        var j = result.Junctions[0];
        Assert.Equal(500f, j.Position.X);
        Assert.Equal((int)JunctionType.TJunction, j.Type);
        Assert.Equal(2, j.Contributors.Count);
        Assert.Equal(1, j.Contributors[0].SplineId);
        Assert.True(j.Contributors[0].IsSplineEnd);
        Assert.Equal(2, j.Contributors[1].SplineId);
        Assert.False(j.Contributors[1].IsSplineEnd);
    }

    [Fact]
    public void ReconstructNetwork_MergedCorridor_WidthFollowsLaneCounts_EvenWithPerSegmentWidthDisabled()
    {
        // Regression: a trunk layerset with EnablePerSegmentWidth=false collapsed a laterally
        // merged corridor (two carriageways, 4-6 lanes) to ONE carriageway's constant width
        // (DefaultLaneCount 2 × 3.5 = 7 m) — 4 lanes of markings painted into 7 m of asphalt.
        var network = new UnifiedRoadNetwork();

        var merged = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(new List<Vector2> { new(0, 0), new(200, 0) },
                SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters { RoadWidthMeters = 8f },
            MaterialName = "Asphalt",
            SplineId = 1,
            OsmRoadType = "trunk",
            IsLaterallyMerged = true,
            LaneSegments =
            [
                new LaneSegment
                {
                    StartPointIndex = 0, StartDistance = 0f,
                    LaneInfo = new OsmLaneInfo { TotalLanes = 4, LanesForward = 2, LanesBackward = 2 },
                },
                new LaneSegment
                {
                    StartPointIndex = 5, StartDistance = 100f,
                    LaneInfo = new OsmLaneInfo { TotalLanes = 6, LanesForward = 3, LanesBackward = 3 },
                },
            ],
        };
        network.AddSpline(merged);

        var single = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(new List<Vector2> { new(0, 50), new(200, 50) },
                SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters { RoadWidthMeters = 8f },
            MaterialName = "Asphalt",
            SplineId = 2,
            OsmRoadType = "trunk",
            LaneSegments =
            [
                new LaneSegment
                {
                    StartPointIndex = 0, StartDistance = 0f,
                    LaneInfo = new OsmLaneInfo { TotalLanes = 2, LanesForward = 2, IsOneWay = true },
                },
            ],
        };
        network.AddSpline(single);

        var trunkSet = new DecalRoadLayerSet
        {
            Name = "Trunk",
            DefaultLaneCount = 2,
            DefaultLaneWidth = 3.5f,
            EnablePerSegmentWidth = false,
            SmoothingCorridorMargin = 2f,
            MasterSplineMargin = 0f,
        };
        var appDataDefaults = new Dictionary<string, DecalRoadLayerSet> { ["trunk"] = trunkSet };

        var deserialized = RoundTrip(DecalRoadNetworkSnapshotBuilder.Build(network));
        var reconstructed = DecalRoadNetworkSnapshotLoader.ReconstructNetwork(
            deserialized, new DecalRoadSettings(), appDataDefaults);

        // Merged corridor: lane-derived per-segment widths despite EnablePerSegmentWidth=false.
        var mergedSpline = reconstructed.Splines.First(s => s.SplineId == 1);
        Assert.True(mergedSpline.IsLaterallyMerged);
        var mergedProfile = mergedSpline.WidthProfile!;
        Assert.Equal(4 * 3.5f, mergedProfile.GetWidthsAtDistance(0f).surface);
        Assert.Equal(6 * 3.5f, mergedProfile.GetWidthsAtDistance(200f).surface);

        // Unmerged trunk carriageway keeps the layerset's constant width.
        var singleProfile = reconstructed.Splines.First(s => s.SplineId == 2).WidthProfile!;
        Assert.Equal(2 * 3.5f, singleProfile.GetWidthsAtDistance(0f).surface);
        Assert.Equal(2 * 3.5f, singleProfile.GetWidthsAtDistance(200f).surface);
    }

    [Fact]
    public void RoundTrip_FullNetwork_BuildAndReconstruct()
    {
        var network = new UnifiedRoadNetwork();

        var spline1Points = new List<Vector2> { new(0, 0), new(100, 0), new(200, 0) };
        var spline1 = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(spline1Points, SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 8.0f,
                MasterSplineWidthMeters = 6.0f
            },
            MaterialName = "Asphalt",
            SplineId = 1,
            OsmRoadType = "primary"
        };
        spline1.Priority = 80;
        network.AddSpline(spline1);

        for (int i = 0; i < 5; i++)
        {
            network.AddCrossSection(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i * 50, 0),
                NormalDirection = new Vector2(0, 1),
                TargetElevation = 100 + i,
                OwnerSplineId = 1,
                LocalIndex = i,
                DistanceAlongSpline = i * 50,
                EffectiveRoadWidth = 8.0f,
                Curvature = 0.01f * i
            });
        }

        var snapshot = DecalRoadNetworkSnapshotBuilder.Build(network);
        var deserialized = RoundTrip(snapshot);
        var reconstructed = DecalRoadNetworkSnapshotLoader.ReconstructNetwork(deserialized);

        Assert.Single(reconstructed.Splines);
        Assert.Equal(5, reconstructed.CrossSections.Count);
        Assert.Equal("primary", reconstructed.Splines[0].OsmRoadType);
        Assert.Equal(6.0f, reconstructed.Splines[0].Parameters.EffectiveMasterSplineWidthMeters);

        var csForSpline = reconstructed.GetCrossSectionsForSpline(1).ToList();
        Assert.Equal(5, csForSpline.Count);
        Assert.Equal(102f, csForSpline[2].TargetElevation);
    }

    [Fact]
    public void InvalidVersion_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(999);
        }

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<InvalidDataException>(() => DecalRoadNetworkSnapshot.ReadFrom(r));
    }

    private static DecalRoadNetworkSnapshot RoundTrip(DecalRoadNetworkSnapshot snapshot)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            snapshot.WriteTo(w);
        }

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        return DecalRoadNetworkSnapshot.ReadFrom(r);
    }
}
