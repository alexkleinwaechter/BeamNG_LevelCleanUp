using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
///     Generates AI waypoint paths for the bridge/tunnel stretches of a spline, replacing the AI
///     DecalRoad there (AI decals projected onto a deck don't feed the navgraph reliably when other
///     roads cross the structure). Output is consumed by <see cref="AiWaypointSceneWriter" />
///     (BeamNGWaypoint objects) and <see cref="AiMapJsonWriter" /> (level-root map.json segments).
///     <para>Connectivity contract (ge/map.lua): the navgraph merges any two nodes closer than the
///     larger node's radius. Structure runs are extended by exactly ONE section into their ground
///     neighbours, so the endpoint waypoints sit on the same cross-section as the adjacent ground
///     AI DecalRoad's end node — zero distance, guaranteed merge. Conversely, nodes of the SAME
///     path closer than their radius would be fused into one, so interior waypoints keep a minimum
///     spacing of twice the radius (the same rule the game uses when simplifying DecalRoad
///     subdivisions).</para>
/// </summary>
public static class AiWaypointPathGenerator
{
    /// <summary>Waypoint scene-object name prefix; must not collide with DecalRoad names.</summary>
    public const string WaypointNamePrefix = "MT_wp_";

    /// <summary>Max chord deviation from the sampled centerline when decimating waypoints.</summary>
    private const float MinRdpToleranceMeters = 0.2f;
    private const float MaxRdpToleranceMeters = 0.5f;

    public static List<GeneratedAiWaypointSegment> GenerateForSpline(
        ParameterizedRoadSpline spline,
        DecalRoadLayerDefinition aiLayer,
        IReadOnlyList<UnifiedCrossSection> sampledSections,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        bool isGeneratedBridge)
    {
        var results = new List<GeneratedAiWaypointSegment>();
        if (sampledSections.Count < 2)
            return results;

        // Extension 1 (not the decal default 2): pins each endpoint exactly on the neighbouring
        // ground run's boundary section = the ground AI decal's end node (see class doc).
        var runs = DecalRoadGenerator.PartitionSectionsByStructure(
            spline, sampledSections, isGeneratedBridge, structureRunExtension: 1);

        var defaultWidth = spline.WidthProfile?.GetWidthsAtDistance(0f).masterSpline
                           ?? spline.Parameters.EffectiveMasterSplineWidthMeters;

        var runIndex = -1;
        foreach (var run in runs)
        {
            if (run.Context == DecalRoadGenerator.StructureRunContext.Road)
                continue;
            runIndex++;

            // Build the world-space centerline with per-node radius (half the local road width).
            var points = new List<Vector3>(run.End - run.Start + 1);
            var radii = new List<float>(run.End - run.Start + 1);
            for (var s = run.Start; s <= run.End; s++)
            {
                var cs = sampledSections[s];
                var width = spline.WidthProfile?.GetWidthsAtDistance(cs.DistanceAlongSpline).masterSpline
                            ?? defaultWidth;

                // Same elevation rule as DecalRoad node generation, so endpoint waypoints coincide
                // exactly with the adjacent ground AI decal's end node.
                var elevation = float.IsFinite(cs.TargetElevation) && cs.TargetElevation > -1000f
                    ? cs.TargetElevation
                    : GetHeightMapElevation(heightMap, cs.CenterPoint.X, cs.CenterPoint.Y, metersPerPixel);

                var world = BeamNgCoordinateTransformer.TerrainToWorld(
                    cs.CenterPoint.X, cs.CenterPoint.Y, elevation + terrainBaseHeight,
                    terrainSizePixels, metersPerPixel);

                if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
                    continue;

                points.Add(world);
                radii.Add(MathF.Max(1.5f, width * 0.5f));
            }

            if (points.Count < 2)
                continue;

            var keptIndices = DecimatePath(points, radii);
            if (keptIndices.Count < 2)
                continue;

            var kind = run.Context == DecalRoadGenerator.StructureRunContext.Tunnel ? "tunnel" : "bridge";
            var segmentName = $"MT_{kind}_{spline.SplineId:D3}_{runIndex:D2}";

            var waypoints = new List<AiWaypoint>(keptIndices.Count);
            for (var k = 0; k < keptIndices.Count; k++)
            {
                var idx = keptIndices[k];
                waypoints.Add(new AiWaypoint(
                    $"{WaypointNamePrefix}{spline.SplineId:D3}_{runIndex:D2}_{k:D2}",
                    points[idx], radii[idx]));
            }

            results.Add(CreateSegment(spline, aiLayer, sampledSections, run, segmentName, waypoints));
        }

        return results;
    }

    /// <summary>
    ///     Applies the same AI property derivation the AI DecalRoad uses (OSM lane tags override the
    ///     layer defaults; roundabouts are always one-way), so the waypoint edge behaves identically
    ///     to the decal it replaces.
    /// </summary>
    private static GeneratedAiWaypointSegment CreateSegment(
        ParameterizedRoadSpline spline,
        DecalRoadLayerDefinition aiLayer,
        IReadOnlyList<UnifiedCrossSection> sampledSections,
        DecalRoadGenerator.SectionRun run,
        string segmentName,
        List<AiWaypoint> waypoints)
    {
        var lanesLeft = aiLayer.LanesLeft;
        var lanesRight = aiLayer.LanesRight;
        var oneWay = aiLayer.OneWay;
        var flipDirection = aiLayer.FlipDirection;
        var autoLanes = true;

        // Lane config at the run midpoint. A lane change inside a structure run is collapsed to a
        // single config — the run must stay one segment so its endpoints keep the merge contract.
        OsmLaneInfo? segInfo = null;
        if (spline.LaneSegments is { Count: > 0 })
        {
            var midDistance = sampledSections[(run.Start + run.End) / 2].DistanceAlongSpline;
            segInfo = DecalRoadGenerator.ResolveLaneInfo(spline.LaneSegments, midDistance);
        }

        if (segInfo is { TotalLanes: > 0 })
        {
            (lanesRight, lanesLeft, oneWay, flipDirection) =
                DecalRoadGenerator.DeriveAIRoadProperties(segInfo);
            autoLanes = false;
        }

        if (spline.IsRoundabout)
        {
            oneWay = true;
            lanesLeft = 0;
            if (segInfo is { TotalLanes: > 0 })
                lanesRight = segInfo.TotalLanes;
            autoLanes = false;
        }

        return new GeneratedAiWaypointSegment
        {
            Name = segmentName,
            Waypoints = waypoints,
            Drivability = aiLayer.Drivability,
            OneWay = oneWay,
            FlipDirection = flipDirection,
            AutoLanes = autoLanes,
            LanesLeft = lanesLeft,
            LanesRight = lanesRight,
            GatedRoad = aiLayer.GatedRoad,
            SplineId = spline.SplineId,
            IsTunnel = run.Context == DecalRoadGenerator.StructureRunContext.Tunnel
        };
    }

    /// <summary>
    ///     Decimates the centerline to the minimum waypoint set that still tracks the curve:
    ///     Ramer-Douglas-Peucker (3D chord deviation) first, then a minimum-spacing pass dropping
    ///     interior nodes closer than twice the radius to the previously kept one (nodes closer than
    ///     their radius would be fused by the game's navgraph merge). Endpoints are always kept.
    /// </summary>
    internal static List<int> DecimatePath(IReadOnlyList<Vector3> points, IReadOnlyList<float> radii)
    {
        var last = points.Count - 1;
        if (last < 1)
            return points.Count == 1 ? [0] : [];

        // Tolerance scales with road width but stays within sane absolute bounds.
        var avgRadius = 0f;
        for (var i = 0; i < radii.Count; i++) avgRadius += radii[i];
        avgRadius /= radii.Count;
        var tolerance = Math.Clamp(avgRadius * 0.15f, MinRdpToleranceMeters, MaxRdpToleranceMeters);

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[last] = true;
        RdpMark(points, 0, last, tolerance, keep);

        // Min-spacing pass: walk kept nodes in order, drop interior ones that sit inside the
        // previous kept node's merge range (2 × the larger of the two radii).
        var result = new List<int> { 0 };
        for (var i = 1; i < last; i++)
        {
            if (!keep[i]) continue;
            var prev = result[^1];
            var minSpacing = 2f * MathF.Max(radii[prev], radii[i]);
            if (Vector3.Distance(points[prev], points[i]) >= minSpacing)
                result.Add(i);
        }

        result.Add(last);
        return result;
    }

    private static void RdpMark(
        IReadOnlyList<Vector3> points, int start, int end, float tolerance, bool[] keep)
    {
        if (end - start < 2)
            return;

        var maxDist = -1f;
        var maxIdx = -1;
        for (var i = start + 1; i < end; i++)
        {
            var d = PointToChordDistance(points[i], points[start], points[end]);
            if (d > maxDist)
            {
                maxDist = d;
                maxIdx = i;
            }
        }

        if (maxDist <= tolerance)
            return;

        keep[maxIdx] = true;
        RdpMark(points, start, maxIdx, tolerance, keep);
        RdpMark(points, maxIdx, end, tolerance, keep);
    }

    private static float PointToChordDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-12f)
            return Vector3.Distance(p, a);
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSq, 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }

    private static float GetHeightMapElevation(
        float[,] heightMap, float terrainX, float terrainY, float metersPerPixel)
    {
        var pixelX = (int)(terrainX / metersPerPixel);
        var pixelY = (int)(terrainY / metersPerPixel);
        var size = heightMap.GetLength(0);
        pixelX = Math.Clamp(pixelX, 0, size - 1);
        pixelY = Math.Clamp(pixelY, 0, size - 1);
        return heightMap[pixelY, pixelX]; // [y, x] row-major
    }
}
