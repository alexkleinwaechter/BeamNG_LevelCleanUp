namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters for junction and endpoint elevation harmonization.
/// Controls how road elevations are blended at intersections and endpoints
/// to eliminate discontinuities.
/// </summary>
public class JunctionHarmonizationParameters
{
    // ========================================
    // MASTER ENABLE
    // ========================================
    
    /// <summary>
    /// Enable junction elevation harmonization.
    /// When enabled, road elevations at intersections and endpoints are smoothed
    /// to eliminate discontinuities.
    /// Default: true
    /// </summary>
    public bool EnableJunctionHarmonization { get; set; } = true;
    
    // ========================================
    // JUNCTION DETECTION
    // ========================================
    
    /// <summary>
    /// Maximum distance (in meters) between path endpoints to consider them part of the same junction.
    /// Larger values will merge more endpoints into single junctions.
    /// 
    /// Typical values:
    /// - 10-15m: Small intersections, tight curves
    /// - 15-25m: Standard road intersections (DEFAULT)
    /// - 25-40m: Large intersections, highway ramps
    /// 
    /// Default: 20.0
    /// </summary>
    public float JunctionDetectionRadiusMeters { get; set; } = 20.0f;
    
    // ========================================
    // JUNCTION BLENDING
    // ========================================
    
    /// <summary>
    /// Distance (in meters) over which to blend from junction elevation back to path elevation.
    /// Larger values create smoother transitions but affect more of the road.
    /// 
    /// Typical values:
    /// - 20-30m: Tight blending (urban roads)
    /// - 30-50m: Standard blending (DEFAULT)
    /// - 50-100m: Very smooth blending (highways)
    /// 
    /// Default: 40.0
    /// </summary>
    public float JunctionBlendDistanceMeters { get; set; } = 40.0f;
    
    /// <summary>
    /// Blend function type for junction transitions.
    /// Default: Cosine (smooth S-curve)
    /// </summary>
    public JunctionBlendFunctionType BlendFunctionType { get; set; } = JunctionBlendFunctionType.Cosine;
    
    // ========================================
    // ENDPOINT TAPERING
    // ========================================
    
    /// <summary>
    /// Enable tapering at isolated endpoints (roads that end without connecting to another road).
    /// When enabled, road elevation gradually transitions back toward terrain at dead ends.
    /// Default: true
    /// </summary>
    public bool EnableEndpointTaper { get; set; } = true;
    
    /// <summary>
    /// Distance (in meters) over which to taper endpoint elevation back toward terrain.
    /// Only applies to isolated endpoints (not junctions).
    /// 
    /// Typical values:
    /// - 10-20m: Short taper (abrupt ending)
    /// - 20-40m: Standard taper (DEFAULT)
    /// - 40-80m: Long taper (gradual ending)
    /// 
    /// Default: 30.0
    /// </summary>
    public float EndpointTaperDistanceMeters { get; set; } = 30.0f;
    
    /// <summary>
    /// How much to blend endpoint elevation toward terrain (0-1).
    /// 0 = no blending (road keeps its elevation at endpoint)
    /// 0.5 = blend halfway to terrain
    /// 1.0 = fully blend to terrain elevation
    /// 
    /// Default: 0.3 (subtle blend - road mostly keeps its elevation)
    /// </summary>
    public float EndpointTerrainBlendStrength { get; set; } = 0.3f;
    
    // ========================================
    // DEBUG OPTIONS
    // ========================================
    
    /// <summary>
    /// Export debug image showing detected junctions and blend zones.
    /// Default: false
    /// </summary>
    public bool ExportJunctionDebugImage { get; set; } = false;
    
    /// <summary>
    /// Validates the junction harmonization parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (JunctionDetectionRadiusMeters <= 0)
            errors.Add("JunctionDetectionRadiusMeters must be greater than 0");
        
        if (JunctionBlendDistanceMeters <= 0)
            errors.Add("JunctionBlendDistanceMeters must be greater than 0");
        
        if (EndpointTaperDistanceMeters <= 0)
            errors.Add("EndpointTaperDistanceMeters must be greater than 0");
        
        if (EndpointTerrainBlendStrength < 0 || EndpointTerrainBlendStrength > 1)
            errors.Add("EndpointTerrainBlendStrength must be between 0 and 1");
        
        return errors;
    }
}

/// <summary>
/// Type of blend function for junction transitions.
/// </summary>
public enum JunctionBlendFunctionType
{
    /// <summary>
    /// Linear interpolation - simple but may have visible transition points.
    /// </summary>
    Linear,
    
    /// <summary>
    /// Cosine interpolation - smooth S-curve, good balance of smoothness and performance.
    /// </summary>
    Cosine,
    
    /// <summary>
    /// Cubic Hermite (smoothstep) - very smooth with zero first derivative at endpoints.
    /// </summary>
    Cubic,
    
    /// <summary>
    /// Quintic (smootherstep) - extremely smooth with zero first and second derivatives.
    /// Best quality but slightly more computation.
    /// </summary>
    Quintic
}
