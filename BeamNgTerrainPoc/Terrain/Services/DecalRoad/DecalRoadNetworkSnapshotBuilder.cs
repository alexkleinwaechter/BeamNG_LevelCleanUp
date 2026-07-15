using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Builds a DecalRoadNetworkSnapshot from a live UnifiedRoadNetwork,
/// capturing only the fields that DecalRoadGenerator needs.
/// </summary>
public static class DecalRoadNetworkSnapshotBuilder
{
    public static DecalRoadNetworkSnapshot Build(UnifiedRoadNetwork network)
    {
        var snapshot = new DecalRoadNetworkSnapshot();

        foreach (var spline in network.Splines)
        {
            var ss = new SplineSnapshot
            {
                SplineId = spline.SplineId,
                OsmRoadType = spline.OsmRoadType ?? string.Empty,
                MaterialName = spline.MaterialName,
                IsBridge = spline.IsBridge,
                IsTunnel = spline.IsTunnel,
                IsRoundabout = spline.IsRoundabout,
                IsLaterallyMerged = spline.IsLaterallyMerged,
                Priority = spline.Priority,
                RoadWidthMeters = spline.Parameters.RoadWidthMeters,
                RoadSurfaceWidthMeters = spline.Parameters.RoadSurfaceWidthMeters,
                MasterSplineWidthMeters = spline.Parameters.MasterSplineWidthMeters,
                TerrainAffectedRangeMeters = spline.Parameters.TerrainAffectedRangeMeters,
                StartPoint = spline.StartPoint,
                EndPoint = spline.EndPoint,
                TotalLengthMeters = spline.TotalLengthMeters
            };

            if (spline.LaneSegments != null)
            {
                ss.LaneSegments = spline.LaneSegments.Select(ls => new LaneSegmentSnapshot
                {
                    StartPointIndex = ls.StartPointIndex,
                    StartDistance = ls.StartDistance,
                    TotalLanes = ls.LaneInfo.TotalLanes,
                    LanesForward = ls.LaneInfo.LanesForward,
                    LanesBackward = ls.LaneInfo.LanesBackward,
                    LanesBothWays = ls.LaneInfo.LanesBothWays,
                    IsOneWay = ls.LaneInfo.IsOneWay,
                    WidthMeters = ls.LaneInfo.WidthMeters,
                    EstWidthMeters = ls.LaneInfo.EstWidthMeters,
                }).ToList();
            }

            snapshot.Splines.Add(ss);
        }

        foreach (var cs in network.CrossSections)
        {
            snapshot.CrossSections.Add(new CrossSectionSnapshot
            {
                CenterPoint = cs.CenterPoint,
                NormalDirection = cs.NormalDirection,
                TargetElevation = cs.TargetElevation,
                OwnerSplineId = cs.OwnerSplineId,
                LocalIndex = cs.LocalIndex,
                DistanceAlongSpline = cs.DistanceAlongSpline,
                EffectiveRoadWidth = cs.EffectiveRoadWidth,
                Curvature = cs.Curvature,
                IsExcluded = cs.IsExcluded,
                IsSplineStart = cs.IsSplineStart,
                IsSplineEnd = cs.IsSplineEnd
            });
        }

        foreach (var junction in network.Junctions)
        {
            var js = new JunctionSnapshot
            {
                Position = junction.Position,
                Type = (int)junction.Type,
                IsExcluded = junction.IsExcluded
            };

            foreach (var contributor in junction.Contributors)
            {
                js.Contributors.Add(new JunctionContributorSnapshot
                {
                    SplineId = contributor.Spline.SplineId,
                    CrossSectionOwnerSplineId = contributor.CrossSection.OwnerSplineId,
                    CrossSectionLocalIndex = contributor.CrossSection.LocalIndex,
                    IsSplineStart = contributor.IsSplineStart,
                    IsSplineEnd = contributor.IsSplineEnd
                });
            }

            snapshot.Junctions.Add(js);
        }

        return snapshot;
    }

    public static void SaveToLevel(UnifiedRoadNetwork network, string levelPath)
    {
        var snapshot = Build(network);
        var path = DecalRoadNetworkSnapshot.GetSnapshotPath(levelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        snapshot.WriteTo(writer);
    }
}
