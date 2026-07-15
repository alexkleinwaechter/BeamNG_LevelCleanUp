using System.Numerics;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

/// <summary>
///     AI waypoint paths over bridges/tunnels: the AI DecalRoad is suppressed on structure runs and
///     replaced by BeamNGWaypoint objects + map.json segments (see AiWaypointPathGenerator).
///     Critical contract: the endpoint waypoints must coincide EXACTLY with the adjacent ground AI
///     DecalRoad's end nodes so the game's navgraph merge (ge/map.lua mergeOverlappingNodes) fuses
///     them into a junction.
/// </summary>
public class AiWaypointGenerationTests
{
    private static DecalRoadSettings CreateSettingsWithAiLayer()
    {
        return new DecalRoadSettings
        {
            Enabled = true,
            NodeSpacingMeters = 2.0f,
            OsmLayerSets = new Dictionary<string, DecalRoadLayerSet>
            {
                ["unclassified"] = new()
                {
                    Name = "unclassified",
                    IsEnabled = true,
                    DefaultLaneCount = 2,
                    Layers =
                    [
                        new DecalRoadLayerDefinition
                        {
                            Name = "Surface",
                            IsEnabled = true,
                            IsTrackWidth = true,
                            Material = "road_asphalt",
                            Position = 0f
                        },
                        new DecalRoadLayerDefinition
                        {
                            Name = "AIRoad",
                            LayerType = DecalRoadLayerType.AIRoad,
                            IsEnabled = true,
                            IsTrackWidth = true,
                            Material = "road_invisible",
                            Position = 0f,
                            Drivability = 1.0f,
                            LanesLeft = 1,
                            LanesRight = 1
                        }
                    ]
                }
            }
        };
    }

    private static readonly IReadOnlyDictionary<string, DecalRoadLayerSet> EmptyDefaults =
        new Dictionary<string, DecalRoadLayerSet>();

    private static StructureSegment CreateStructureSegment(
        StructureType type, float startDistance, float endDistance)
    {
        return new StructureSegment
        {
            Type = type,
            StartDistance = startDistance,
            EndDistance = endDistance,
            OsmWayIds = [1234]
        };
    }

    private static void SetCrossSections(UnifiedRoadNetwork network, int splineId, float targetElevation)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            cs.TargetElevation = targetElevation;
            cs.IsExcluded = false;
        }
    }

    private static void TagSpanSections(UnifiedRoadNetwork network, int splineId, StructureSegment segment)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            if (cs.DistanceAlongSpline >= segment.StartDistance &&
                cs.DistanceAlongSpline <= segment.EndDistance)
                cs.StructureSpanId = segment.SpanId;
    }

    private static (List<GeneratedDecalRoad> roads, List<GeneratedAiWaypointSegment> waypoints)
        GenerateWithWaypoints(UnifiedRoadNetwork network)
    {
        var waypointSegments = new List<GeneratedAiWaypointSegment>();
        var roads = DecalRoadGenerator.Generate(
            network,
            RoadNetworkTestHelpers.CreateFlatHeightmap(256, elevation: 10f),
            metersPerPixel: 1f,
            terrainSizePixels: 256,
            terrainBaseHeight: 0f,
            CreateSettingsWithAiLayer(),
            EmptyDefaults,
            waypointSegments);
        return (roads, waypointSegments);
    }

    // ─── Generation: bridge span mid-spline ───────────────────────────────────────────────────

    [Fact]
    public void Generate_MergedCorridorBridgeSpan_AiDecalSuppressedAndWaypointSegmentEmitted()
    {
        var segment = CreateStructureSegment(StructureType.Bridge, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f);
        TagSpanSections(network, corridor.SplineId, segment);

        var (roads, waypoints) = GenerateWithWaypoints(network);

        // AI decal: only the two ground approach runs remain (the deck run is replaced).
        var aiRoads = roads.Where(r => r.IsAIRoad).ToList();
        Assert.Equal(2, aiRoads.Count);

        var wpSegment = Assert.Single(waypoints);
        Assert.StartsWith("MT_bridge_", wpSegment.Name);
        Assert.False(wpSegment.IsTunnel);
        Assert.Equal(1.0f, wpSegment.Drivability);
        Assert.True(wpSegment.Waypoints.Count >= 2);

        // Straight flat deck → RDP decimates to just the two endpoints.
        Assert.Equal(2, wpSegment.Waypoints.Count);

        // Endpoint pinning: first/last waypoint coincides EXACTLY with one ground AI decal's
        // end node (navgraph merge contract — distance 0 always merges).
        var first = wpSegment.Waypoints[0].Position;
        var last = wpSegment.Waypoints[^1].Position;
        Assert.Contains(aiRoads, r => NodeEquals(r.Nodes[^1], first));
        Assert.Contains(aiRoads, r => NodeEquals(r.Nodes[0], last));

        // Radius is half the road (track) width, same width the AI decal nodes carry.
        var trackWidth = aiRoads[0].Nodes[0][3];
        Assert.All(wpSegment.Waypoints, wp =>
            Assert.Equal(MathF.Max(1.5f, trackWidth * 0.5f), wp.Radius, precision: 3));

        // Waypoint names are prefixed and unique.
        Assert.All(wpSegment.Waypoints, wp =>
            Assert.StartsWith(AiWaypointPathGenerator.WaypointNamePrefix, wp.Name));
        Assert.Equal(wpSegment.Waypoints.Count, wpSegment.Waypoints.Select(w => w.Name).Distinct().Count());

        // Non-AI layers are unaffected: the Surface layer still covers the deck (OverObjects run).
        Assert.Contains(roads, r => !r.IsAIRoad && r.OverObjects);
    }

    [Fact]
    public void Generate_MergedCorridorTunnelSpan_TunnelWaypointSegmentEmitted()
    {
        var segment = CreateStructureSegment(StructureType.Tunnel, 40f, 60f);
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified",
            mergeStructuresIntoCorridor: true,
            structureSegments: [segment]);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetCrossSections(network, corridor.SplineId, targetElevation: 50f);
        TagSpanSections(network, corridor.SplineId, segment);

        var (roads, waypoints) = GenerateWithWaypoints(network);

        var wpSegment = Assert.Single(waypoints);
        Assert.StartsWith("MT_tunnel_", wpSegment.Name);
        Assert.True(wpSegment.IsTunnel);

        // AI decal suppressed over the tunnel stretch too.
        Assert.Equal(2, roads.Count(r => r.IsAIRoad));
    }

    [Fact]
    public void Generate_WholeSplineGeneratedBridge_NoAiDecalSingleWaypointSegment()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified", isBridge: true, excludeBridges: true);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        SetCrossSections(network, bridge.SplineId, targetElevation: 125f);

        var (roads, waypoints) = GenerateWithWaypoints(network);

        // The whole spline is deck: no AI decal at all, one waypoint segment end to end.
        Assert.DoesNotContain(roads, r => r.IsAIRoad);
        var wpSegment = Assert.Single(waypoints);
        Assert.StartsWith("MT_bridge_", wpSegment.Name);
        Assert.Equal(2, wpSegment.Waypoints.Count); // straight flat deck
    }

    [Fact]
    public void Generate_RegularRoadWithoutStructures_NoWaypointSegments()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(10, 150), new Vector2(100, 150),
            osmRoadType: "unclassified");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road);
        SetCrossSections(network, road.SplineId, targetElevation: 50f);

        var (roads, waypoints) = GenerateWithWaypoints(network);

        Assert.Empty(waypoints);
        // AI decal untouched on plain roads (may be length-chunked into several pieces)
        Assert.Contains(roads, r => r.IsAIRoad);
    }

    private static bool NodeEquals(float[] node, Vector3 p)
    {
        return MathF.Abs(node[0] - p.X) < 1e-3f &&
               MathF.Abs(node[1] - p.Y) < 1e-3f &&
               MathF.Abs(node[2] - p.Z) < 1e-3f;
    }

    // ─── Decimation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DecimatePath_StraightLine_KeepsOnlyEndpoints()
    {
        var points = new List<Vector3>();
        var radii = new List<float>();
        for (var i = 0; i <= 50; i++)
        {
            points.Add(new Vector3(i * 2f, 0f, 10f));
            radii.Add(3f);
        }

        var kept = AiWaypointPathGenerator.DecimatePath(points, radii);

        Assert.Equal([0, 50], kept);
    }

    [Fact]
    public void DecimatePath_Arc_KeepsInteriorNodesWithMinSpacing()
    {
        // Quarter arc, radius 100 m — chord deviation forces interior nodes.
        var points = new List<Vector3>();
        var radii = new List<float>();
        const int count = 80;
        for (var i = 0; i <= count; i++)
        {
            var angle = MathF.PI / 2f * i / count;
            points.Add(new Vector3(100f * MathF.Cos(angle), 100f * MathF.Sin(angle), 10f));
            radii.Add(3f);
        }

        var kept = AiWaypointPathGenerator.DecimatePath(points, radii);

        Assert.True(kept.Count > 2, "curved path must keep interior waypoints");
        Assert.Equal(0, kept[0]);
        Assert.Equal(count, kept[^1]);

        // Interior spacing ≥ 2 × radius, otherwise the game's navgraph would fuse the nodes.
        for (var k = 1; k < kept.Count - 1; k++)
        {
            var dist = Vector3.Distance(points[kept[k - 1]], points[kept[k]]);
            Assert.True(dist >= 2f * 3f - 1e-3f, $"waypoint spacing {dist} below merge distance");
        }

        // Chord deviation of the decimated path stays within the RDP tolerance bound (0.5 m max).
        for (var k = 1; k < kept.Count; k++)
        for (var i = kept[k - 1] + 1; i < kept[k]; i++)
        {
            var a = points[kept[k - 1]];
            var b = points[kept[k]];
            var ab = b - a;
            var t = Math.Clamp(Vector3.Dot(points[i] - a, ab) / ab.LengthSquared(), 0f, 1f);
            var deviation = Vector3.Distance(points[i], a + ab * t);
            Assert.True(deviation <= 0.75f, $"chord deviation {deviation} too large");
        }
    }

    // ─── map.json writer ───────────────────────────────────────────────────────────────────────

    private static GeneratedAiWaypointSegment CreateSegment(
        string name, params string[] waypointNames)
    {
        return new GeneratedAiWaypointSegment
        {
            Name = name,
            Waypoints = waypointNames
                .Select((n, i) => new AiWaypoint(n, new Vector3(i * 10f, 0f, 0f), 3f))
                .ToList(),
            Drivability = 1.0f,
            OneWay = false,
            FlipDirection = false
        };
    }

    private static string CreateTempLevelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mt_wp_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MapJsonWriter_CreatesFileWithSegments()
    {
        var dir = CreateTempLevelDir();
        try
        {
            var written = AiMapJsonWriter.Write(
                [CreateSegment("MT_bridge_001_00", "MT_wp_001_00_00", "MT_wp_001_00_01")], dir);

            Assert.Equal(1, written);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "map.json")));
            var seg = doc.RootElement.GetProperty("segments").GetProperty("MT_bridge_001_00");
            Assert.Equal(2, seg.GetProperty("nodes").GetArrayLength());
            Assert.Equal("MT_wp_001_00_00", seg.GetProperty("nodes")[0].GetString());
            Assert.Equal(1.0f, seg.GetProperty("drivability").GetSingle());
            Assert.False(seg.GetProperty("oneWay").GetBoolean());
            Assert.False(seg.GetProperty("flipDirection").GetBoolean());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MapJsonWriter_PreservesForeignSegments_ReplacesManaged()
    {
        var dir = CreateTempLevelDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.json"), """
                {
                    "segments": {
                        "my_custom_track": { "nodes": ["a1", "a2"], "drivability": 1 },
                        "MT_bridge_099_00": { "nodes": ["stale1", "stale2"], "drivability": 1 }
                    }
                }
                """);

            AiMapJsonWriter.Write([CreateSegment("MT_bridge_001_00", "wpA", "wpB")], dir);

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "map.json")));
            var segments = doc.RootElement.GetProperty("segments");
            Assert.True(segments.TryGetProperty("my_custom_track", out _)); // hand-authored preserved
            Assert.False(segments.TryGetProperty("MT_bridge_099_00", out _)); // stale generated removed
            Assert.True(segments.TryGetProperty("MT_bridge_001_00", out _)); // new generated added
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MapJsonWriter_EmptySegments_DeletesFileWhenOnlyManagedRemain()
    {
        var dir = CreateTempLevelDir();
        try
        {
            var mapPath = Path.Combine(dir, "map.json");
            File.WriteAllText(mapPath, """
                { "segments": { "MT_bridge_099_00": { "nodes": ["s1", "s2"], "drivability": 1 } } }
                """);

            AiMapJsonWriter.Write([], dir);

            Assert.False(File.Exists(mapPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MapJsonWriter_UnparseableFile_LeftUntouched()
    {
        var dir = CreateTempLevelDir();
        try
        {
            var mapPath = Path.Combine(dir, "map.json");
            const string broken = "{ this is not json";
            File.WriteAllText(mapPath, broken);

            var written = AiMapJsonWriter.Write([CreateSegment("MT_bridge_001_00", "a", "b")], dir);

            Assert.Equal(-1, written);
            Assert.Equal(broken, File.ReadAllText(mapPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─── Scene writer ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SceneWriter_WritesWaypointNdjsonAndParentSimGroup()
    {
        var dir = CreateTempLevelDir();
        try
        {
            var segment = CreateSegment("MT_bridge_001_00", "MT_wp_001_00_00", "MT_wp_001_00_01");
            var written = new AiWaypointSceneWriter().WriteAll([segment], dir);

            Assert.Equal(2, written);

            // Parent MissionGroup registers the MT_waypoints SimGroup.
            var parentLines = File.ReadAllLines(
                Path.Combine(dir, "main", "MissionGroup", "items.level.json"));
            Assert.Contains(parentLines, line =>
                line.Contains("\"SimGroup\"") && line.Contains(AiWaypointSceneWriter.GroupName));

            // Waypoints written as one NDJSON line each.
            var itemsPath = Path.Combine(
                dir, "main", "MissionGroup", AiWaypointSceneWriter.GroupName, "items.level.json");
            var lines = File.ReadAllLines(itemsPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.Equal(2, lines.Count);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("BeamNGWaypoint", doc.RootElement.GetProperty("class").GetString());
                Assert.Equal(AiWaypointSceneWriter.GroupName,
                    doc.RootElement.GetProperty("__parent").GetString());
                Assert.Equal(3f, doc.RootElement.GetProperty("scale")[0].GetSingle());
                Assert.Equal(3, doc.RootElement.GetProperty("position").GetArrayLength());
            }

            // CleanPrevious removes the group directory again.
            AiWaypointSceneWriter.CleanPrevious(dir);
            Assert.False(File.Exists(itemsPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
