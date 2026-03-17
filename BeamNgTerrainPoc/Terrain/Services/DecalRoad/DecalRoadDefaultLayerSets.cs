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
///   Edge blend 1:     1.0m,   position: ±1.1,  material: m_road_asphalt_edge,       renderPriority: 9
///   Edge blend 2:     2.0m,   position: ±1.25, material: m_road_edge_dirt,           renderPriority: 9
///   Edge blend 3:     3.0m,   position: ±1.35, material: m_road_asphalt_edge_grass,  renderPriority: 9
///   Light tread marks:           texLen: 5.0,   material: m_tread_marks_clean         (per-lane)
///   Heavy tread marks:           texLen: 5.0,   material: road_rubber_double          (per-lane)
///   Wear 1 (Damage Asphalt 1):   texLen: 10.0,  material: m_asphalt_damaged_01        (per-lane)
///   Wear 2 (Damage Asphalt 2):   texLen: 25.0,  material: m_asphalt_damaged_02        (per-lane)
///   Fix 1 (Repair 1):            texLen: 25.0,  material: repair1                     (per-lane)
///   Fix 2 (Repair 2):            texLen: 25.0,  material: repair2                     (per-lane)
///   Patches:                      texLen: 45.0,  material: road_patches1               (per-lane)
///   Cracks:                       texLen: 15.0,  material: m_asphalt_cracks_02          (per-lane)
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

    /// <summary>
    /// Returns disabled tread mark / wear / damage layers matching BeamNG's auto-generated layers.
    /// These are available for users to enable in the layer set editor.
    /// All use TreadMarks layer type for per-lane center positioning.
    /// </summary>
    private static List<DecalRoadLayerDefinition> GetDisabledWearLayers() =>
    [
        new() { Name = "HeavyTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "road_rubber_double", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Wear1", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "m_asphalt_damaged_01", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Wear2", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "m_asphalt_damaged_02", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 25.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Fix1", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "repair1", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 25.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Fix2", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "repair2", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 25.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Patches", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "road_patches1", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 45.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
        new() { Name = "Cracks", LayerType = DecalRoadLayerType.TreadMarks,
                 Material = "m_asphalt_cracks_02", IsLaneWidth = true, IsEnabled = false,
                 TextureLength = 15.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                 RenderPriority = 10, InterruptAtJunctions = false },
    ];

    /// <summary>
    /// Returns a disabled Edge Blend 3 layer (m_road_asphalt_edge_grass, 3.0m, pos ±1.35).
    /// </summary>
    private static DecalRoadLayerDefinition GetDisabledEdgeBlend3() => new()
    {
        Name = "EdgeBlend3", LayerType = DecalRoadLayerType.EdgeBlend,
        Material = "m_road_asphalt_edge_grass", Width = 3.0f, Position = 1.35f,
        TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
        IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true,
        IsEnabled = false
    };

    private static DecalRoadLayerSet CreateHighwaySet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f },
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
        };
        layers.AddRange(GetDisabledWearLayers());
        layers.AddRange([
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            GetDisabledEdgeBlend3(),
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = lanes / 2 },
        ]);
        return new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers };
    }

    private static DecalRoadLayerSet CreateStandardRoadSet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
        };
        layers.AddRange(GetDisabledWearLayers());
        layers.AddRange([
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            GetDisabledEdgeBlend3(),
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]);
        return new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers };
    }

    private static DecalRoadLayerSet CreateMinimalRoadSet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
        };
        layers.AddRange(GetDisabledWearLayers());
        layers.AddRange([
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            GetDisabledEdgeBlend3(),
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]);
        return new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers };
    }

    private static DecalRoadLayerSet CreateResidentialSet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true, IsEnabled = false },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true, IsEnabled = false },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
        };
        layers.AddRange(GetDisabledWearLayers());
        layers.AddRange([
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            GetDisabledEdgeBlend3(),
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1, InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = 1, LanesRight = 1 },
        ]);
        return new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers };
    }

    private static DecalRoadLayerSet CreateServiceSet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true, IsEnabled = false },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true, IsEnabled = false },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     RenderPriority = 10, InterruptAtJunctions = false },
        };
        layers.AddRange(GetDisabledWearLayers());
        layers.AddRange([
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, RenderPriority = 9, InterruptAtJunctions = true },
            GetDisabledEdgeBlend3(),
        ]);
        return new DecalRoadLayerSet { Name = name, DefaultLaneCount = lanes, Layers = layers };
    }

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
