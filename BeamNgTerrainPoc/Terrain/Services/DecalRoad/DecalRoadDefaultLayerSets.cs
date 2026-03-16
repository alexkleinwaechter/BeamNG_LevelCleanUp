using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Hardcoded fallback default DecalRoadLayerSet definitions per OSM road type.
/// Used when AppData defaults file is missing or corrupted.
///
/// Values aligned with BeamNG's Road Spline editor (layerMgr.lua):
///   Edge line width:  0.25m,  position: ±1.0
///   Center line:      0.2m,   texLen: 5.0
///   Lane marking:     0.2m,   texLen: 5.0
///   Edge blend 1:     1.0m,   position: ±1.1,  material: m_road_asphalt_edge,     renderPriority: 9
///   Edge blend 2:     2.0m,   position: ±1.25, material: m_road_edge_dirt,         renderPriority: 9
///   Edge blend 3:     3.0m,   position: ±1.35, material: m_road_asphalt_edge_grass, renderPriority: 9
///   Light tread marks: 5.0m,  texLen: 5.0,     material: m_tread_marks_clean       (per-lane)
///   Fade in/out:      1.0 / 1.0
/// </summary>
public static class DecalRoadDefaultLayerSets
{
    public static Dictionary<string, DecalRoadLayerSet> GetDefaults()
    {
        return new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = CreateHighwaySet("Motorway", 4),
            ["trunk"] = CreateHighwaySet("Trunk", 4),
            ["primary"] = CreateStandardRoadSet("Primary", 2),
            ["secondary"] = CreateStandardRoadSet("Secondary", 2),
            ["tertiary"] = CreateMinimalRoadSet("Tertiary", 2),
            ["unclassified"] = CreateMinimalRoadSet("Unclassified", 2),
            ["residential"] = CreateResidentialSet("Residential", 2),
            ["service"] = CreateServiceSet("Service", 1),
            ["track"] = CreateTrackSet("Track", 1),
        };
    }

    private static DecalRoadLayerSet CreateHighwaySet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = lanes / 2 },
        ]
    };

    private static DecalRoadLayerSet CreateStandardRoadSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "CenterLine", LayerType = DecalRoadLayerType.CenterLine,
                     Material = "m_line_white_discontinue", Width = 0.2f, Position = 0.0f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateMinimalRoadSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateResidentialSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]
    };

    private static DecalRoadLayerSet CreateServiceSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
        ]
    };

    private static DecalRoadLayerSet CreateTrackSet(string name, int lanes) => new()
    {
        Name = name, DefaultLaneCount = lanes, Layers =
        [
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 1.0f, Position = 1.1f,
                     TextureLength = 12.0f, FadeIn = 1.0f, FadeOut = 1.0f, 
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "DirtVariation04", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "m_dirt_variation_04", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 10},                     
        ]
    };
}
