using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
///     Hardcoded fallback default DecalRoadLayerSet definitions per OSM road type.
///     Used when AppData defaults file is missing or corrupted.
///     Values aligned with BeamNG's Road Spline editor (layerMgr.lua):
///     Edge line width:  0.25m,  position: ±1.0,  renderPriority: 6
///     Center line:      0.25m,  position: 0.0,    renderPriority: 6
///     Lane marking:     0.2m,   texLen: 5.0,      renderPriority: 6
///     Light tread marks:           texLen: 5.0,   material: m_tread_marks_clean,   renderPriority: 15, fadeIn/Out: 5
///     Heavy tread marks:           texLen: 5.0,   material: road_rubber_double,    renderPriority: 15, fadeIn/Out: 10,
///     curveOnly
///     Wear 1 (Damage Asphalt 1):   texLen: 10.0,  material: m_asphalt_damaged_01, renderPriority: 20, fadeIn/Out: 5
///     Wear 2 (Damage Asphalt 2):   texLen: 25.0,  material: m_asphalt_damaged_02, renderPriority: 20, fadeIn/Out: 10,
///     curveOnly (disabled)
///     Fix 1 (Repair 1):            texLen: 25.0,  material: repair1,              renderPriority: 12 (disabled)
///     Fix 2 (Repair 2):            texLen: 25.0,  material: repair2,              renderPriority: 12 (disabled)
///     Patches:                      texLen: 45.0,  material: road_patches1,        renderPriority: 11, fadeIn/Out: 10,
///     randomized
///     Cracks:                       texLen: 15.0,  material: m_asphalt_cracks_02,  renderPriority: 13 (disabled)
///     Edge blend 1:     1.0m,   position: ±1.1,  material: m_road_asphalt_edge,       renderPriority: 7
///     Edge blend 2:     2.0m,   position: ±1.25, material: m_road_edge_dirt,           renderPriority: 8, curveOnly
///     Edge blend 3:     3.0m,   position: ±1.35, material: m_road_asphalt_edge_grass,  renderPriority: 9 (disabled)
/// </summary>
public static class DecalRoadDefaultLayerSets
{
    public static Dictionary<string, DecalRoadLayerSet> GetDefaults()
    {
        // Link roads: slip roads, ramps, turning lanes — single lane, narrower than parent
        return new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = CreateAsphaltRoadSet("Motorway", 4, 3.5f, 2.0f, 0.0f),
            ["motorway_link"] = CreateAsphaltRoadSet("Motorway Link", 1, 3.5f, 1.5f, 0.0f),
            ["trunk"] = CreateAsphaltRoadSet("Trunk", 4, 3.5f, 2.0f, 0.0f),
            ["trunk_link"] = CreateAsphaltRoadSet("Trunk Link", 1, 3.5f, 1.5f, 0.0f),
            ["primary"] = CreateAsphaltRoadSet("Primary", 2, 3.5f, 2.0f, 0.0f),
            ["primary_link"] = CreateAsphaltRoadSet("Primary Link", 1, 3.25f, 1.5f, 0.0f),
            ["secondary"] = CreateAsphaltRoadSet("Secondary", 2, 3.5f, 2.0f, 0.0f),
            ["secondary_link"] = CreateAsphaltRoadSet("Secondary Link", 1, 3.0f, 1.5f, 0.0f),
            ["tertiary"] = CreateAsphaltRoadSet("Tertiary", 2, 3.0f, 2.0f, 0.0f),
            ["tertiary_link"] = CreateAsphaltRoadSet("Tertiary Link", 1, 3.0f, 1.0f, 0.0f),
            ["unclassified"] = CreateAsphaltRoadSet("Unclassified", 2, 3.0f, 2.0f, 0.0f),
            ["residential"] = CreateAsphaltRoadSet("Residential", 2, 3.0f, 2.0f, 0.0f),
            ["service"] = CreateAsphaltRoadSet("Service", 2, 2.75f, 1.5f, 0.0f),
            ["raceway"] = CreateAsphaltRoadSet("Raceway", 1, 5f, 3f, -0.8f),
            ["road"] = CreateAsphaltRoadSet("Road", 2, 3f, 1.5f, 0.0f),
            ["track"] = CreateTrackSet("Track", 2, 2f, 1.0f, 0.0f),
            ["path"] = CreateTrackSet("Track", 2, 2f, 1.0f, 0.0f),
            ["roundabout"] = CreateRoundaboutSet("Roundabout", 1, 3.5f, 2.0f, 0.0f)
        };
    }

    /// <summary>
    ///     Creates the standard asphalt road layer set used by all paved road types.
    ///     Layers and priorities are based on the tuned Primary road defaults.
    /// </summary>
    private static DecalRoadLayerSet CreateAsphaltRoadSet(string name, int lanes, float laneWidth = 3.5f,
        float smoothingMargin = 2.0f, float splineMargin = 0.0f)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new()
            {
                Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                TextureLength = 10.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f
            },
            new()
            {
                Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                Material = "m_line_yellow_double", Width = 0.25f, Position = 0.0f,
                TextureLength = 10.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f
            },
            new()
            {
                Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                Material = "m_line_white_discontinue", Width = 0.2f,
                TextureLength = 5.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsPerLane = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                CurveConstraint = CurveConstraintMode.ReplaceInCurve,
                CurveReplacementMaterial = "m_line_white",
                CurveReplacementTextureLength = 0.0f, CurveReplacementWidth = 0.0f,
                CurveMinCurvature = 0.05f, CurveTransitionLength = 30.0f,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f
            },
            new()
            {
                Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_tread_marks_clean", IsLaneWidth = true,
                TextureLength = 5.0f, RenderPriority = 15,
                FadeIn = 5.0f, FadeOut = 5.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "HeavyTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "road_rubber_double", IsLaneWidth = true,
                TextureLength = 5.0f, RenderPriority = 15,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.05f,
                CurveTransitionLength = 20,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Wear1", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_damaged_01", IsLaneWidth = true,
                TextureLength = 10.0f, RenderPriority = 20,
                FadeIn = 5.0f, FadeOut = 5.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Wear2", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_damaged_02", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 25.0f, RenderPriority = 20,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.05f,
                CurveTransitionLength = 20.0f,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Heavy wear in tighter curves", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "road_rubber_double", Width = 0, IsLaneWidth = true,
                TextureLength = 25.0f, RenderPriority = 10,
                FadeIn = 20.0f, FadeOut = 20.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.07f,
                CurveTransitionLength = 15.0f,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Fix1", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "repair1", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 25.0f, RenderPriority = 12,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f
            },
            new()
            {
                Name = "Fix2", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "repair2", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 25.0f, RenderPriority = 12,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Patches", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "road_patches1", IsLaneWidth = true,
                TextureLength = 45.0f, RenderPriority = 11,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                Randomize = true, RandomMinPatchLength = 50.0f, RandomMaxPatchLength = 250.0f,
                RandomMinGapLength = 200.0f, RandomMaxGapLength = 500.0f,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Cracks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_cracks_02", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 15.0f, RenderPriority = 13,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                TextureLength = 10.0f, RenderPriority = 7,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.3f,
                TextureLength = 10.0f, RenderPriority = 8,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend3", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_asphalt_edge_grass", Width = 3.0f, Position = 1.5f,
                TextureLength = 10.0f, RenderPriority = 9,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                IsEnabled = false,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "BridgeTunnelSurface", LayerType = DecalRoadLayerType.RoadSurface,
                Material = "road_asphalt_2lane", IsTrackWidth = true,
                TextureLength = 5.0f, RenderPriority = 30,
                FadeIn = 2.0f, FadeOut = 2.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = false, RenderOnBridges = true, RenderOnTunnels = true
            },
            new()
            {
                Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                Material = "road_invisible", Width = 0, Position = 0.0f,
                IsTrackWidth = true, RenderPriority = 1,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = (lanes + 1) / 2,
                ImprovedSpline = false, Detail = 0.5f, Smoothness = 0.5f
            }
        };
        return new DecalRoadLayerSet
        {
            Name = name,
            DefaultLaneCount = lanes,
            DefaultLaneWidth = laneWidth,
            SmoothingCorridorMargin = smoothingMargin,
            MasterSplineMargin = splineMargin,
            Layers = layers
        };
    }

    private static DecalRoadLayerSet CreateRoundaboutSet(string name, int lanes, float laneWidth = 3.5f,
        float smoothingMargin = 2.0f, float splineMargin = 0.0f)
    {
        var layers = new List<DecalRoadLayerDefinition>
        {
            new()
            {
                Name = "EdgeLine", LayerType = DecalRoadLayerType.EdgeLine,
                Material = "m_line_white", Width = 0.25f, Position = 1.0f,
                TextureLength = 10.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f
            },
            new()
            {
                Name = "DirectionDivider", LayerType = DecalRoadLayerType.DirectionDivider,
                Material = "m_line_yellow_double", Width = 0.25f, Position = 0.0f,
                TextureLength = 10.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f,
                IsEnabled = false
            },
            new()
            {
                Name = "LaneMarking", LayerType = DecalRoadLayerType.LaneMarking,
                Material = "m_line_white_discontinue", Width = 0.2f,
                TextureLength = 5.0f, RenderPriority = 6,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsPerLane = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                CurveConstraint = CurveConstraintMode.ReplaceInCurve,
                CurveReplacementMaterial = "m_line_white",
                CurveReplacementTextureLength = 0.0f, CurveReplacementWidth = 0.0f,
                CurveMinCurvature = 0.05f, CurveTransitionLength = 30.0f,
                ImprovedSpline = true, Detail = 0.5f, Smoothness = 0.5f,
                IsEnabled = false
            },
            new()
            {
                Name = "LightTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_tread_marks_clean", IsLaneWidth = true,
                TextureLength = 5.0f, RenderPriority = 15,
                FadeIn = 5.0f, FadeOut = 5.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = false, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "HeavyTreadMarks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "road_rubber_double", IsLaneWidth = true,
                TextureLength = 5.0f, RenderPriority = 15,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                IsEnabled = true,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Wear1", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_damaged_01", IsLaneWidth = true,
                TextureLength = 10.0f, RenderPriority = 20,
                FadeIn = 5.0f, FadeOut = 5.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                IsEnabled = false,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Wear2", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_damaged_02", IsLaneWidth = true,
                TextureLength = 25.0f, RenderPriority = 20,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                CurveConstraint = CurveConstraintMode.CurveOnly, CurveMinCurvature = 0.05f,
                CurveTransitionLength = 20.0f,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Fix1", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "repair1", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 25.0f, RenderPriority = 12,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Fix2", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "repair2", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 25.0f, RenderPriority = 12,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Patches", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "road_patches1", IsLaneWidth = true,
                TextureLength = 45.0f, RenderPriority = 11,
                FadeIn = 10.0f, FadeOut = 10.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                IsEnabled = false,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "Cracks", LayerType = DecalRoadLayerType.TreadMarks,
                Material = "m_asphalt_cracks_02", IsLaneWidth = true, IsEnabled = false,
                TextureLength = 15.0f, RenderPriority = 13,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_asphalt_edge", Width = 1.0f, Position = 1.1f,
                TextureLength = 10.0f, RenderPriority = 7,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend2", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_edge_dirt", Width = 2.0f, Position = 1.3f,
                TextureLength = 10.0f, RenderPriority = 8,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "EdgeBlend3", LayerType = DecalRoadLayerType.EdgeBlend,
                Material = "m_road_asphalt_edge_grass", Width = 3.0f, Position = 1.5f,
                TextureLength = 10.0f, RenderPriority = 9,
                FadeIn = 1.0f, FadeOut = 1.0f,
                IsMirrored = true, JunctionConstraint = JunctionConstraintMode.Interrupt,
                IsEnabled = false,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
            },
            new()
            {
                Name = "BridgeTunnelSurface", LayerType = DecalRoadLayerType.RoadSurface,
                Material = "road_asphalt_2lane", IsTrackWidth = true,
                TextureLength = 5.0f, RenderPriority = 30,
                FadeIn = 2.0f, FadeOut = 2.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                RenderOnRoads = false, RenderOnBridges = true, RenderOnTunnels = true
            },
            new()
            {
                Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                Material = "road_invisible", Width = 0, Position = 0.0f,
                IsTrackWidth = true, RenderPriority = 1,
                FadeIn = 1.0f, FadeOut = 1.0f,
                JunctionConstraint = JunctionConstraintMode.None,
                Drivability = 1.0f, LanesLeft = 0, LanesRight = lanes,
                OneWay = true,
                ImprovedSpline = false, Detail = 0.5f, Smoothness = 0.5f
            }
        };


        return new DecalRoadLayerSet
        {
            Name = name,
            DefaultLaneCount = lanes,
            DefaultLaneWidth = laneWidth,
            SmoothingCorridorMargin = smoothingMargin,
            MasterSplineMargin = splineMargin,
            Layers = layers
        };
    }

    private static DecalRoadLayerSet CreateTrackSet(string name, int lanes, float laneWidth = 2.5f,
        float smoothingMargin = 1.0f, float splineMargin = 0.0f)
    {
        return new DecalRoadLayerSet
        {
            Name = name,
            DefaultLaneCount = lanes,
            DefaultLaneWidth = laneWidth,
            SmoothingCorridorMargin = smoothingMargin,
            MasterSplineMargin = splineMargin,
            Layers =
            [
                new ()
                {
                    Name = "EdgeBlend1", LayerType = DecalRoadLayerType.EdgeBlend,
                    Material = "m_road_edge_dirt", Width = 1.0f, Position = 1.1f,
                    TextureLength = 12.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                    IsMirrored = true, RenderPriority = 9, JunctionConstraint = JunctionConstraintMode.Interrupt,
                    ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                    RenderOnRoads = true, RenderOnBridges = false, RenderOnTunnels = false
                },
                new DecalRoadLayerDefinition
                {
                    Name = "DirtVariation04", LayerType = DecalRoadLayerType.TreadMarks,
                    Material = "m_dirt_variation_04", Width = 0, Position = 0.0f,
                    TextureLength = 96.0f, FadeIn = 1.0f, FadeOut = 1.0f,
                    IsTrackWidth = true, RenderPriority = 10,
                    JunctionConstraint = JunctionConstraintMode.None,
                    ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f
                },
                new()
                {
                    Name = "BridgeTunnelSurface", LayerType = DecalRoadLayerType.RoadSurface,
                    Material = "road_asphalt_2lane", IsTrackWidth = true,
                    TextureLength = 5.0f, RenderPriority = 30,
                    FadeIn = 2.0f, FadeOut = 2.0f,
                    JunctionConstraint = JunctionConstraintMode.None,
                    ImprovedSpline = true, Detail = 0.6f, Smoothness = 0.5f,
                    RenderOnRoads = false, RenderOnBridges = true, RenderOnTunnels = true
                },
                new ()
                {
                    Name = "AIRoad", LayerType = DecalRoadLayerType.AIRoad,
                    Material = "road_invisible", Width = 0, Position = 0.0f,
                    IsTrackWidth = true, RenderPriority = 1,
                    FadeIn = 1.0f, FadeOut = 1.0f,
                    JunctionConstraint = JunctionConstraintMode.None,
                    GatedRoad = true,
                    Drivability = 1.0f, LanesLeft = lanes / 2, LanesRight = (lanes + 1) / 2,
                    ImprovedSpline = false, Detail = 0.5f, Smoothness = 0.5f
                }
            ]
        };
    }
}