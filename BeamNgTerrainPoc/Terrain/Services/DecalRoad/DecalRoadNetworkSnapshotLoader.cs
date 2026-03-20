using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Grille.BeamNG;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Loads a DecalRoadNetworkSnapshot from disk and reconstructs a UnifiedRoadNetwork
/// suitable for DecalRoadGenerator.Generate(). Also provides heightmap loading from .ter files.
/// </summary>
public static class DecalRoadNetworkSnapshotLoader
{
    public static UnifiedRoadNetwork? LoadNetwork(string levelPath)
    {
        var snapshotPath = DecalRoadNetworkSnapshot.GetSnapshotPath(levelPath);
        if (!File.Exists(snapshotPath))
            return null;

        DecalRoadNetworkSnapshot snapshot;
        using (var stream = File.OpenRead(snapshotPath))
        using (var reader = new BinaryReader(stream))
        {
            snapshot = DecalRoadNetworkSnapshot.ReadFrom(reader);
        }

        return ReconstructNetwork(snapshot);
    }

    /// <summary>
    /// Loads the heightmap from the .ter file.
    /// Returns a float[y, x] row-major array matching what DecalRoadGenerator expects.
    /// </summary>
    public static float[,]? LoadHeightmap(string terFilePath, float maxHeight)
    {
        if (!File.Exists(terFilePath))
            return null;

        using var stream = File.OpenRead(terFilePath);
        var terrain = new Grille.BeamNG.Terrain(stream, maxHeight);

        var size = terrain.Size;
        var heightMap = new float[size, size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                heightMap[y, x] = terrain.Data[x, y].Height;
            }
        }

        return heightMap;
    }

    /// <summary>
    /// Reconstructs a UnifiedRoadNetwork from a snapshot.
    /// Splines get stub RoadSpline objects (2-point linear) since the generator
    /// only uses pre-computed cross-section data.
    /// </summary>
    internal static UnifiedRoadNetwork ReconstructNetwork(DecalRoadNetworkSnapshot snapshot)
    {
        var network = new UnifiedRoadNetwork();

        var splineById = new Dictionary<int, ParameterizedRoadSpline>();
        foreach (var ss in snapshot.Splines)
        {
            var controlPoints = new List<Vector2> { ss.StartPoint, ss.EndPoint };
            var stubSpline = new RoadSpline(controlPoints, SplineInterpolationType.LinearControlPoints);

            List<LaneSegment>? laneSegments = null;
            if (ss.LaneSegments != null)
            {
                laneSegments = ss.LaneSegments.Select(ls => new LaneSegment
                {
                    StartPointIndex = ls.StartPointIndex,
                    StartDistance = ls.StartDistance,
                    LaneInfo = new OsmLaneInfo
                    {
                        TotalLanes = ls.TotalLanes,
                        LanesForward = ls.LanesForward,
                        LanesBackward = ls.LanesBackward,
                        LanesBothWays = ls.LanesBothWays,
                        IsOneWay = ls.IsOneWay
                    }
                }).ToList();
            }

            var spline = new ParameterizedRoadSpline
            {
                Spline = stubSpline,
                Parameters = new RoadSmoothingParameters
                {
                    RoadWidthMeters = ss.RoadWidthMeters,
                    RoadSurfaceWidthMeters = ss.RoadSurfaceWidthMeters,
                    MasterSplineWidthMeters = ss.MasterSplineWidthMeters,
                    TerrainAffectedRangeMeters = ss.TerrainAffectedRangeMeters
                },
                MaterialName = ss.MaterialName,
                SplineId = ss.SplineId,
                OsmRoadType = ss.OsmRoadType == string.Empty ? null : ss.OsmRoadType,
                LaneSegments = laneSegments,
                IsBridge = ss.IsBridge,
                IsTunnel = ss.IsTunnel
            };
            spline.Priority = ss.Priority;

            network.AddSpline(spline);
            splineById[ss.SplineId] = spline;
        }

        foreach (var css in snapshot.CrossSections)
        {
            network.AddCrossSection(new UnifiedCrossSection
            {
                CenterPoint = css.CenterPoint,
                NormalDirection = css.NormalDirection,
                TargetElevation = css.TargetElevation,
                OwnerSplineId = css.OwnerSplineId,
                LocalIndex = css.LocalIndex,
                DistanceAlongSpline = css.DistanceAlongSpline,
                EffectiveRoadWidth = css.EffectiveRoadWidth,
                Curvature = css.Curvature,
                IsExcluded = css.IsExcluded,
                IsSplineStart = css.IsSplineStart,
                IsSplineEnd = css.IsSplineEnd
            });
        }

        var csLookup = new Dictionary<(int, int), UnifiedCrossSection>();
        foreach (var cs in network.CrossSections)
        {
            csLookup[(cs.OwnerSplineId, cs.LocalIndex)] = cs;
        }

        foreach (var js in snapshot.Junctions)
        {
            var junction = new NetworkJunction
            {
                Position = js.Position,
                Type = (JunctionType)js.Type,
                IsExcluded = js.IsExcluded
            };

            foreach (var cs in js.Contributors)
            {
                if (!splineById.TryGetValue(cs.SplineId, out var spline))
                    continue;
                if (!csLookup.TryGetValue((cs.CrossSectionOwnerSplineId, cs.CrossSectionLocalIndex), out var crossSection))
                    continue;

                junction.Contributors.Add(new JunctionContributor
                {
                    Spline = spline,
                    CrossSection = crossSection,
                    IsSplineStart = cs.IsSplineStart,
                    IsSplineEnd = cs.IsSplineEnd
                });
            }

            network.Junctions.Add(junction);
        }

        return network;
    }
}
