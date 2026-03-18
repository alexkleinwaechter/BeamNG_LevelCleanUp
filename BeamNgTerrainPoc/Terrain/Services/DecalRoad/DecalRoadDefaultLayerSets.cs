using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Hardcoded fallback default DecalRoadLayerSet definitions per OSM road type.
/// Used when AppData defaults file is missing or corrupted.
///
/// Values aligned with BeamNG's Road Spline editor (layerMgr.lua):
///   Edge line width:  0.25m,  position: ±1.0,  renderPriority: 6
///   Center line:      0.25m,  position: 0.0,    renderPriority: 6
///   Lane marking:     0.2m,   texLen: 5.0,      renderPriority: 6
///   Light tread marks:           texLen: 5.0,   material: m_tread_marks_clean,   renderPriority: 15, fadeIn/Out: 5
///   Heavy tread marks:           texLen: 5.0,   material: road_rubber_double,    renderPriority: 15, fadeIn/Out: 10, curveOnly
///   Skidmarks:                   texLen: 25.0,  material: skidmarks_01,          renderPriority: 10, fadeIn/Out: 20, curveOnly
///   Wear 1 (Damage Asphalt 1):   texLen: 10.0,  material: m_asphalt_damaged_01, renderPriority: 20, fadeIn/Out: 5
///   Wear 2 (Damage Asphalt 2):   texLen: 25.0,  material: m_asphalt_damaged_02, renderPriority: 20, fadeIn/Out: 10, curveOnly (disabled)
///   Fix 1 (Repair 1):            texLen: 25.0,  material: repair1,              renderPriority: 12 (disabled)
///   Fix 2 (Repair 2):            texLen: 25.0,  material: repair2,              renderPriority: 12 (disabled)
///   Patches:                      texLen: 45.0,  material: road_patches1,        renderPriority: 11, fadeIn/Out: 10, randomized
///   Cracks:                       texLen: 15.0,  material: m_asphalt_cracks_02,  renderPriority: 13 (disabled)
///   Edge blend 1:     1.0m,   position: ±1.1,  material: m_road_asphalt_edge,       renderPriority: 7
///   Edge blend 2:     2.0m,   position: ±1.25, material: m_road_edge_dirt,           renderPriority: 8, curveOnly
///   Edge blend 3:     3.0m,   position: ±1.35, material: m_road_asphalt_edge_grass,  renderPriority: 9 (disabled)
/// </summary>
public static class DecalRoadDefaultLayerSets
{
    public static Dictionary<string, DecalRoadLayerSet> GetDefaults()
    {
        return new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = CreateAsphaltRoadSet("Motorway", 4),
            ["trunk"] = CreateAsphaltRoadSet("Trunk", 4),
            ["primary"] = CreateAsphaltRoadSet("Primary", 2),
            ["secondary"] = CreateAsphaltRoadSet("Secondary", 2),
            ["tertiary"] = CreateAsphaltRoadSet("Tertiary", 2),
            ["unclassified"] = CreateAsphaltRoadSet("Unclassified", 2),
            ["residential"] = CreateAsphaltRoadSet("Residential", 2),
            ["service"] = CreateAsphaltRoadSet("Service", 1),
            ["track"] = CreateTrackSet("Track", 1),
        };
    }

    /// <summary>
    /// Creates the standard asphalt road layer set used by all paved road types.
    /// Layers and priorities are based on the tuned Primary road defaults.
    /// </summary>
    private static DecalRoadLayerSet CreateAsphaltRoadSet(string name, int lanes)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new() { Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                     Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                     TextureLength = 10.0f, RenderPriority = 6,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                     Material = "m_line_white", Width = 0.25f, Position = 0.0f,
                     TextureLength = 10.0f, RenderPriority = 6,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = true },
            new() { Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                     Material = "m_line_white_discontinue", Width = 0.2f,
                     TextureLength = 5.0f, RenderPriority = 6,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     IsPerLane = true, InterruptAtJunctions = true },
            new() { Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_tread_marks_clean", IsLaneWidth = true,
                     TextureLength = 5.0f, RenderPriority = 15,
                     FadeIn = 5.0f, FadeOut = 5.0f,
                     InterruptAtJunctions = false },
            new() { Name = "HeavyTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "road_rubber_double", IsLaneWidth = true,
                     TextureLength = 5.0f, RenderPriority = 15,
                     FadeIn = 10.0f, FadeOut = 10.0f,
                     InterruptAtJunctions = false,
                     CurveOnly = true, CurveMinCurvature = 0.05f, CurveTransitionLength = 15.0f },
            new() { Name = "Wear1", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_asphalt_damaged_01", IsLaneWidth = true,
                     TextureLength = 10.0f, RenderPriority = 20,
                     FadeIn = 5.0f, FadeOut = 5.0f,
                     InterruptAtJunctions = false },
            new() { Name = "Wear2", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_asphalt_damaged_02", IsLaneWidth = true, IsEnabled = false,
                     TextureLength = 25.0f, RenderPriority = 20,
                     FadeIn = 10.0f, FadeOut = 10.0f,
                     InterruptAtJunctions = false,
                     CurveOnly = true, CurveMinCurvature = 0.05f, CurveTransitionLength = 20.0f },
            new() { Name = "Skidmarks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "skidmarks_01", Width = 0, IsLaneWidth = true,
                     TextureLength = 25.0f, RenderPriority = 10,
                     FadeIn = 20.0f, FadeOut = 20.0f,
                     InterruptAtJunctions = false,
                     CurveOnly = true, CurveMinCurvature = 0.07f, CurveTransitionLength = 15.0f },
            new() { Name = "Fix1", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "repair1", IsLaneWidth = true, IsEnabled = false,
                     TextureLength = 25.0f, RenderPriority = 12,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = false },
            new() { Name = "Fix2", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "repair2", IsLaneWidth = true, IsEnabled = false,
                     TextureLength = 25.0f, RenderPriority = 12,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = false },
            new() { Name = "Patches", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "road_patches1", IsLaneWidth = true,
                     TextureLength = 45.0f, RenderPriority = 11,
                     FadeIn = 10.0f, FadeOut = 10.0f,
                     InterruptAtJunctions = false,
                     Randomize = true, RandomMinPatchLength = 50.0f, RandomMaxPatchLength = 250.0f,
                     RandomMinGapLength = 200.0f, RandomMaxGapLength = 500.0f },
            new() { Name = "Cracks", LayerType = DecalRoadLayerType.TreadMarks,
                     Material = "m_asphalt_cracks_02", IsLaneWidth = true, IsEnabled = false,
                     TextureLength = 15.0f, RenderPriority = 13,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = false },
            new() { Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                     TextureLength = 10.0f, RenderPriority = 7,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true },
            new() { Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.25f,
                     TextureLength = 10.0f, RenderPriority = 8,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true,
                     CurveOnly = true, CurveMinCurvature = 0.01f, CurveTransitionLength = 15.0f },
            new() { Name = "EdgeBlend3", LayerType = DecalRoadLayerType.EdgeBlend,
                     Material = "m_road_asphalt_edge_grass", Width = 3.0f, Position = 1.35f,
                     TextureLength = 10.0f, RenderPriority = 9,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     IsMirrored = true, InterruptAtJunctions = true,
                     IsEnabled = false },
            new() { Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                     Material = "road_invisible", Width = 0, Position = 0.0f,
                     IsTrackWidth = true, RenderPriority = 1,
                     FadeIn = 1.0f, FadeOut = 1.0f,
                     InterruptAtJunctions = false,
                     Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = (lanes + 1) / 2 },
        };
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
                     TextureLength = 96.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                     IsTrackWidth = true, RenderPriority = 10},
        ]
    };
}
