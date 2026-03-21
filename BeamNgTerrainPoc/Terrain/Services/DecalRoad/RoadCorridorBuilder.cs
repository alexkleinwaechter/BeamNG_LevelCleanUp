using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Builds RoadCorridor objects from the unified road network.
/// Each corridor contains sampled cross-sections and a corridor half-width
/// computed from the road's resolved DecalRoad layer set.
/// </summary>
public static class RoadCorridorBuilder
{
    /// <summary>
    /// Builds corridors for all eligible splines in the network.
    /// Must be called before DecalRoad generation (Pass 1 of two-pass architecture).
    /// </summary>
    public static Dictionary<int, RoadCorridor> BuildCorridors(
        UnifiedRoadNetwork network,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults,
        float nodeSpacingMeters)
    {
        var corridors = new Dictionary<int, RoadCorridor>();

        foreach (var spline in network.Splines)
        {
            if (spline.IsBridge || spline.IsTunnel)
                continue;

            DecalRoadLayerSet? layerSet;
            if (spline.IsRoundabout)
            {
                layerSet = DecalRoadLayerSetResolver.Resolve(
                    "roundabout", spline.MaterialName, settings, appDataDefaults);
                layerSet ??= DecalRoadLayerSetResolver.Resolve(
                    spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            }
            else
            {
                layerSet = DecalRoadLayerSetResolver.Resolve(
                    spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            }
            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
            var laneCount = GetLaneCount(spline, layerSet);

            var corridorHalfWidth = CalculateCorridorHalfWidth(
                layerSet.Layers, roadWidth, laneCount,
                settings.JunctionExclusionMarginMeters);

            var sampledSections = DecalRoadGenerator.SubSampleCrossSections(
                crossSections, nodeSpacingMeters);

            var sections = sampledSections.Select(cs => new CorridorSection(
                cs.CenterPoint, cs.NormalDirection, cs.DistanceAlongSpline)).ToList();

            corridors[spline.SplineId] = new RoadCorridor
            {
                SplineId = spline.SplineId,
                RoadWidth = roadWidth,
                CorridorHalfWidth = corridorHalfWidth,
                Sections = sections,
                IsClosedLoop = spline.IsRoundabout
            };
        }

        return corridors;
    }

    /// <summary>
    /// Calculates the corridor half-width as the maximum outer extent of any enabled layer.
    /// Formula per layer: |expandedPosition| * 0.5 * roadWidth + nodeWidth / 2
    /// The margin is added on top for configurable tolerance.
    /// </summary>
    public static float CalculateCorridorHalfWidth(
        IReadOnlyList<DecalRoadLayerDefinition> layers,
        float roadWidth,
        int laneCount,
        float marginMeters)
    {
        float maxExtent = 0f;

        foreach (var layer in layers)
        {
            if (!layer.IsEnabled) continue;

            // Determine the outermost |expandedPosition| for this layer
            float maxAbsPosition;
            if (layer.LayerType == DecalRoadLayerType.TreadMarks)
            {
                var centers = DecalRoadGenerator.CalculateLaneCenterPositions(laneCount);
                maxAbsPosition = centers.Length > 0
                    ? centers.Max(c => MathF.Abs(c))
                    : 0f;
            }
            else if (layer.IsPerLane)
            {
                var boundaries = DecalRoadGenerator.CalculateLaneBoundaryPositions(laneCount);
                maxAbsPosition = boundaries.Length > 0
                    ? boundaries.Max(b => MathF.Abs(b))
                    : 0f;
            }
            else // Mirrored or single placement
            {
                maxAbsPosition = MathF.Abs(layer.Position);
            }

            // Resolve node width (same logic as DecalRoadGenerator)
            float nodeWidth;
            if (layer.IsTrackWidth)
                nodeWidth = roadWidth;
            else if (layer.IsLaneWidth)
                nodeWidth = roadWidth / MathF.Max(1, laneCount);
            else
                nodeWidth = layer.Width;

            var extent = maxAbsPosition * 0.5f * roadWidth + nodeWidth / 2f;
            maxExtent = MathF.Max(maxExtent, extent);
        }

        return maxExtent + marginMeters;
    }

    private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
    {
        // Use max lane count across all segments for corridor width
        if (spline.LaneSegments != null && spline.LaneSegments.Count > 0)
            return spline.LaneSegments.Max(s => s.LaneInfo.TotalLanes);
        return layerSet.DefaultLaneCount;
    }
}
