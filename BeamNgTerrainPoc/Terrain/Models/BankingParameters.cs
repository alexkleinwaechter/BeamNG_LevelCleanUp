using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters controlling road banking (superelevation) behavior.
/// Banking tilts the road surface on curves for improved vehicle handling.
/// </summary>
public class BankingParameters
{
    // ========================================
    // BANKING (SUPERELEVATION) PARAMETERS
    // ========================================

    /// <summary>
    /// Enable automatic banking (superelevation) on curves.
    /// When enabled, the road surface tilts based on curve curvature.
    /// Default: false
    /// </summary>
    public bool EnableAutoBanking { get; set; } = false;

    /// <summary>
    /// Maximum bank angle in degrees.
    /// Real-world highways typically use 4-8°, race tracks up to 15°.
    /// Default: 8.0 (moderate banking suitable for highways)
    /// </summary>
    public float MaxBankAngleDegrees { get; set; } = 8.0f;

    /// <summary>
    /// Banking strength multiplier (0-1).
    /// 0 = no banking, 1 = full banking based on curvature.
    /// Use lower values for urban roads, higher for highways/race tracks.
    /// Default: 0.5
    /// </summary>
    public float BankStrength { get; set; } = 0.5f;

    /// <summary>
    /// Controls how banking transitions at curve boundaries.
    /// Higher values = sharper falloff (banking drops faster from curve apex).
    /// Range: 0.3-2.0
    /// Default: 0.6 (smooth transitions)
    /// </summary>
    public float AutoBankFalloff { get; set; } = 0.6f;

    /// <summary>
    /// Curvature scale factor for bank angle calculation.
    /// Formula: bankAngle = min(curvature * CurvatureToBankScale, 1) * maxBankAngle
    /// Higher values = more aggressive banking on gentle curves.
    /// Default: 500.0 (empirically tuned for driving simulation)
    /// </summary>
    public float CurvatureToBankScale { get; set; } = 500.0f;

    /// <summary>
    /// Minimum curve radius (meters) below which maximum banking is applied.
    /// Curves tighter than this get full MaxBankAngleDegrees.
    /// Default: 50.0 (tight curves like hairpins)
    /// </summary>
    public float MinCurveRadiusForMaxBank { get; set; } = 50.0f;

    /// <summary>
    /// Transition length (meters) for banking changes.
    /// Banking fades in/out over this distance at curve entries/exits.
    /// Default: 30.0 (smooth transitions)
    /// </summary>
    public float BankTransitionLengthMeters { get; set; } = 30.0f;

    /// <summary>
    /// Validates banking parameters and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (MaxBankAngleDegrees < 0 || MaxBankAngleDegrees > 45)
            errors.Add("MaxBankAngleDegrees must be between 0 and 45");
        if (BankStrength < 0 || BankStrength > 1)
            errors.Add("BankStrength must be between 0 and 1");
        if (AutoBankFalloff < 0.1f || AutoBankFalloff > 3.0f)
            errors.Add("AutoBankFalloff must be between 0.1 and 3.0");
        if (CurvatureToBankScale < 1 || CurvatureToBankScale > 2000)
            errors.Add("CurvatureToBankScale must be between 1 and 2000");
        if (BankTransitionLengthMeters < 1 || BankTransitionLengthMeters > 200)
            errors.Add("BankTransitionLengthMeters must be between 1 and 200");
        if (MinCurveRadiusForMaxBank < 5 || MinCurveRadiusForMaxBank > 500)
            errors.Add("MinCurveRadiusForMaxBank must be between 5 and 500");

        return errors;
    }

    /// <summary>
    /// Creates default banking parameters suitable for highways.
    /// </summary>
    public static BankingParameters Highway => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 8.0f,
        BankStrength = 0.7f,
        AutoBankFalloff = 0.5f,
        BankTransitionLengthMeters = 40.0f
    };

    /// <summary>
    /// Creates default banking parameters suitable for race tracks.
    /// </summary>
    public static BankingParameters RaceTrack => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 15.0f,
        BankStrength = 1.0f,
        AutoBankFalloff = 0.4f,
        BankTransitionLengthMeters = 25.0f
    };

    /// <summary>
    /// Creates default banking parameters for gentle rural roads.
    /// </summary>
    public static BankingParameters RuralRoad => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 5.0f,
        BankStrength = 0.4f,
        AutoBankFalloff = 0.8f,
        BankTransitionLengthMeters = 20.0f
    };

    /// <summary>
    /// Creates a deep copy of these parameters.
    /// </summary>
    public BankingParameters Clone() => new()
    {
        EnableAutoBanking = EnableAutoBanking,
        MaxBankAngleDegrees = MaxBankAngleDegrees,
        BankStrength = BankStrength,
        AutoBankFalloff = AutoBankFalloff,
        CurvatureToBankScale = CurvatureToBankScale,
        MinCurveRadiusForMaxBank = MinCurveRadiusForMaxBank,
        BankTransitionLengthMeters = BankTransitionLengthMeters
    };
}
