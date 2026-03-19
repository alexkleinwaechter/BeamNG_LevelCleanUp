namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerDefinition
{
    public string Name { get; set; } = string.Empty;
    public DecalRoadLayerType LayerType { get; set; } = DecalRoadLayerType.Custom;
    public bool IsEnabled { get; set; } = true;
    public string Material { get; set; } = string.Empty;
    public float Width { get; set; } = 0.2f;
    public float TextureLength { get; set; } = 10.0f;
    public int RenderPriority { get; set; } = 10;
    public float Position { get; set; } // -1.0 = left edge, 0.0 = center, +1.0 = right edge
    public bool IsTrackWidth { get; set; }
    public bool IsLaneWidth { get; set; }
    public bool IsMirrored { get; set; }
    public bool IsPerLane { get; set; }
    public float FadeIn { get; set; } = 1.0f;
    public float FadeOut { get; set; } = 1.0f;
    public float[] DistanceFade { get; set; } = [1000f, 1500f];
    public bool InterruptAtJunctions { get; set; } = true;

    // AI Road properties (only relevant for LayerType == AIRoad)
    public float Drivability { get; set; } = -1.0f; // -1.0 = non-drivable, 1.0 = AI drivable
    public int LanesLeft { get; set; } = 1;
    public int LanesRight { get; set; } = 1;
    public bool OneWay { get; set; }
    public bool FlipDirection { get; set; }
    public bool GatedRoad { get; set; }

    // Rendering
    public bool OverObjects { get; set; } // When true, DecalRoad renders on top of mesh objects

    // ========================================
    // SPLINE BEHAVIOUR
    // ========================================

    /// <summary>
    /// Uses BeamNG's improved spline interpolation for smoother curves.
    /// When false, uses legacy Catmull-Rom interpolation.
    /// </summary>
    public bool ImprovedSpline { get; set; } = true;

    /// <summary>
    /// Spline smoothness factor (0.0 = sharp corners, 1.0 = maximum smoothing).
    /// Controls how much the spline rounds off between control points.
    /// </summary>
    public float Smoothness { get; set; } = 0.5f;

    /// <summary>
    /// Spline tessellation detail (0.1 = coarse, 1.0 = high detail).
    /// Controls segment subdivision for rendering. Lower = better performance, higher = smoother visuals.
    /// </summary>
    public float Detail { get; set; } = 1.0f;

    // ========================================
    // CURVE-ONLY CONSTRAINT
    // ========================================

    /// <summary>
    /// When true, this layer is only generated in road sections where curvature
    /// exceeds CurveMinCurvature. Straight sections are skipped.
    /// </summary>
    public bool CurveOnly { get; set; }

    /// <summary>
    /// Minimum curvature threshold (1/radius in 1/meters) for curve detection.
    /// Default 0.01 = curves tighter than 100m radius.
    /// Uses absolute value of UnifiedCrossSection.Curvature.
    /// </summary>
    public float CurveMinCurvature { get; set; } = 0.01f;

    /// <summary>
    /// Distance in meters to extend the generated zone before and after the detected curve.
    /// Creates a lead-in/lead-out zone. FadeIn/FadeOut control visual fade independently.
    /// </summary>
    public float CurveTransitionLength { get; set; } = 15.0f;

    // ========================================
    // RANDOMIZER CONSTRAINT
    // ========================================

    /// <summary>
    /// When true, this layer is generated as random patches with gaps instead of continuously.
    /// </summary>
    public bool Randomize { get; set; }

    /// <summary>
    /// Minimum length of each generated patch in meters.
    /// </summary>
    public float RandomMinPatchLength { get; set; } = 10.0f;

    /// <summary>
    /// Maximum length of each generated patch in meters.
    /// </summary>
    public float RandomMaxPatchLength { get; set; } = 50.0f;

    /// <summary>
    /// Minimum gap between patches in meters.
    /// </summary>
    public float RandomMinGapLength { get; set; } = 20.0f;

    /// <summary>
    /// Maximum gap between patches in meters.
    /// </summary>
    public float RandomMaxGapLength { get; set; } = 100.0f;
}
