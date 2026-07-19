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
    public static UnifiedRoadNetwork? LoadNetwork(
        string levelPath,
        DecalRoadSettings? decalRoadSettings = null,
        IReadOnlyDictionary<string, DecalRoadLayerSet>? appDataDefaults = null)
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

        return ReconstructNetwork(snapshot, decalRoadSettings, appDataDefaults);
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
    /// When <paramref name="decalRoadSettings"/> and <paramref name="appDataDefaults"/> are provided,
    /// the RoadWidthProfile is also reconstructed from the serialized lane segment width data.
    /// </summary>
    internal static UnifiedRoadNetwork ReconstructNetwork(
        DecalRoadNetworkSnapshot snapshot,
        DecalRoadSettings? decalRoadSettings = null,
        IReadOnlyDictionary<string, DecalRoadLayerSet>? appDataDefaults = null)
    {
        var network = new UnifiedRoadNetwork();

        var splineById = new Dictionary<int, ParameterizedRoadSpline>();
        foreach (var ss in snapshot.Splines)
        {
            // Ensure start != end to avoid zero-length spline exception
            // (e.g., roundabouts have identical start/end points)
            var endPoint = ss.EndPoint;
            if (Vector2.DistanceSquared(ss.StartPoint, endPoint) < 0.001f * 0.001f)
                endPoint = ss.StartPoint + new Vector2(0.01f, 0);

            var controlPoints = new List<Vector2> { ss.StartPoint, endPoint };
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
                        IsOneWay = ls.IsOneWay,
                        WidthMeters = ls.WidthMeters,
                        EstWidthMeters = ls.EstWidthMeters,
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
                // OsmTags: not persisted in the DecalRoad snapshot (D-6 is for live OSM generation); left null.
            };
            spline.IsRoundabout = ss.IsRoundabout;
            spline.IsLaterallyMerged = ss.IsLaterallyMerged;
            spline.Priority = ss.Priority;

            // Reconstruct RoadWidthProfile when layerset resolution context is available
            if (decalRoadSettings != null && appDataDefaults != null)
            {
                var layerSet = DecalRoadLayerSetResolver.Resolve(
                    spline.OsmRoadType, spline.MaterialName, decalRoadSettings, appDataDefaults);
                spline.WidthProfile = BuildWidthProfile(spline, layerSet);
            }

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
                SurfaceWidth = css.EffectiveRoadWidth, // A.8: snapshot loader fallback — old snapshots have no surface width persisted
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

    /// <summary>
    /// Builds a RoadWidthProfile from the lane segments and layerset configuration.
    /// Mirrors the logic in UnifiedRoadNetworkBuilder.BuildWidthProfile.
    /// Returns null if no layerset is provided.
    /// </summary>
    private static RoadWidthProfile? BuildWidthProfile(
        ParameterizedRoadSpline spline,
        DecalRoadLayerSet? layerSet)
    {
        if (layerSet == null) return null;

        var segments = new List<WidthSegment>();

        // Laterally merged corridors carry BOTH carriageways — width must follow the merged
        // per-station lane counts even when the layerset disables per-segment width (the
        // constant default describes ONE carriageway). Mirrors UnifiedRoadNetworkBuilder.
        if ((layerSet.EnablePerSegmentWidth || spline.IsLaterallyMerged) &&
            spline.LaneSegments is { Count: > 0 })
        {
            foreach (var ls in spline.LaneSegments)
            {
                float surfaceWidth;
                WidthSource source;

                if (layerSet.UseOsmWidthTag && ls.LaneInfo.WidthMeters.HasValue)
                {
                    surfaceWidth = ls.LaneInfo.WidthMeters.Value;
                    source = WidthSource.OsmWidthTagExact;
                }
                else if (layerSet.UseOsmWidthTag && ls.LaneInfo.EstWidthMeters.HasValue)
                {
                    surfaceWidth = ls.LaneInfo.EstWidthMeters.Value;
                    source = WidthSource.OsmWidthTagEstimated;
                }
                else if (ls.LaneInfo.TotalLanes > 0)
                {
                    surfaceWidth = ls.LaneInfo.TotalLanes * layerSet.DefaultLaneWidth;
                    source = WidthSource.LaneCalculation;
                }
                else
                {
                    surfaceWidth = layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth;
                    source = WidthSource.LayerSetDefault;
                }

                segments.Add(new WidthSegment
                {
                    StartDistance = ls.StartDistance,
                    RoadSurfaceWidth = surfaceWidth,
                    SmoothingCorridorWidth = surfaceWidth + 2 * layerSet.SmoothingCorridorMargin,
                    MasterSplineWidth = surfaceWidth + 2 * layerSet.MasterSplineMargin,
                    LaneCount = ls.LaneInfo.TotalLanes,
                    Source = source
                });
            }
        }
        else
        {
            var surfaceWidth = layerSet.DefaultLaneCount * layerSet.DefaultLaneWidth;
            segments.Add(new WidthSegment
            {
                StartDistance = 0f,
                RoadSurfaceWidth = surfaceWidth,
                SmoothingCorridorWidth = surfaceWidth + 2 * layerSet.SmoothingCorridorMargin,
                MasterSplineWidth = surfaceWidth + 2 * layerSet.MasterSplineMargin,
                LaneCount = layerSet.DefaultLaneCount,
                Source = WidthSource.LayerSetDefault
            });
        }

        return new RoadWidthProfile(segments);
    }
}
