using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
/// Calculates bank angles for road cross-sections based on curvature.
/// Implements BeamNG-style banking with falloff transitions.
/// 
/// Banking (superelevation) tilts the road surface on curves to counteract
/// centrifugal forces, improving vehicle handling and realism.
/// </summary>
public class BankingCalculator
{
    private readonly CurvatureCalculator _curvatureCalculator = new();

    /// <summary>
    /// Calculates bank angles for all cross-sections of a spline.
    /// </summary>
    /// <param name="crossSections">Ordered cross-sections for a single spline</param>
    /// <param name="parameters">Banking parameters controlling the calculation</param>
    public void CalculateBanking(
        IList<UnifiedCrossSection> crossSections,
        BankingParameters parameters)
    {
        if (!parameters.EnableAutoBanking || crossSections.Count < 2)
        {
            // No banking - set all to zero/default
            foreach (var cs in crossSections)
            {
                cs.BankAngleRadians = 0;
                cs.BankedNormal3D = new Vector3(0, 0, 1);
            }
            return;
        }

        // Step 1: Calculate curvature at each point
        _curvatureCalculator.CalculateCurvature(crossSections);

        // Step 2: Calculate raw bank angles from curvature
        var maxBankRad = DegreesToRadians(parameters.MaxBankAngleDegrees);
        var rawBankAngles = new float[crossSections.Count];

        for (int i = 0; i < crossSections.Count; i++)
        {
            rawBankAngles[i] = CalculateBankAngleFromCurvature(
                crossSections[i].Curvature,
                parameters.CurvatureToBankScale,
                parameters.BankStrength,
                maxBankRad);
        }

        // Step 3: Apply falloff blending for smooth transitions
        ApplyFalloffBlending(crossSections, rawBankAngles, parameters);

        // Step 4: Calculate 3D banked normals
        CalculateBankedNormals(crossSections);
    }

    /// <summary>
    /// Calculates basic banking (curvature and bank angles) without junction awareness.
    /// This is Phase 1 of the two-phase banking process, used BEFORE junction harmonization.
    /// </summary>
    /// <param name="crossSections">Ordered cross-sections for a single spline</param>
    /// <param name="parameters">Banking parameters controlling the calculation</param>
    public void CalculateBankingBasic(
        IList<UnifiedCrossSection> crossSections,
        BankingParameters parameters)
    {
        if (!parameters.EnableAutoBanking || crossSections.Count < 2)
        {
            foreach (var cs in crossSections)
            {
                cs.BankAngleRadians = 0;
                cs.BankedNormal3D = new Vector3(0, 0, 1);
            }
            return;
        }

        // Calculate raw bank angles from curvature (curvature should already be calculated)
        var maxBankRad = DegreesToRadians(parameters.MaxBankAngleDegrees);
        var rawBankAngles = new float[crossSections.Count];

        for (int i = 0; i < crossSections.Count; i++)
        {
            rawBankAngles[i] = CalculateBankAngleFromCurvature(
                crossSections[i].Curvature,
                parameters.CurvatureToBankScale,
                parameters.BankStrength,
                maxBankRad);
        }

        // Apply falloff blending for smooth transitions
        ApplyFalloffBlending(crossSections, rawBankAngles, parameters);

        // Calculate 3D banked normals
        CalculateBankedNormals(crossSections);
    }

    /// <summary>
    /// Applies junction-aware adjustments to bank angles.
    /// This is Phase 2 of the two-phase banking process, used AFTER junction harmonization.
    /// Modifies bank angles based on JunctionBankingBehavior (SuppressBanking, AdaptToHigherPriority, etc.)
    /// 
    /// CRITICAL for AdaptToHigherPriority:
    /// - Secondary roads should NOT simply inherit the primary road's bank angle
    /// - Instead, they should calculate a RAMP angle that transitions from their natural
    ///   elevation to the banked surface of the primary road
    /// - This creates a smooth transition without "speed bumps"
    /// </summary>
    /// <param name="crossSections">Ordered cross-sections for a single spline</param>
    /// <param name="parameters">Banking parameters controlling the calculation</param>
    /// <param name="getAdaptiveBankAngle">
    /// Function to calculate the adaptive bank angle for a cross-section.
    /// This should calculate the ramp angle needed to smoothly meet the higher-priority road.
    /// </param>
    public void ApplyJunctionAwareBankingAdjustments(
        IList<UnifiedCrossSection> crossSections,
        BankingParameters parameters,
        Func<UnifiedCrossSection, float>? getAdaptiveBankAngle = null)
    {
        if (!parameters.EnableAutoBanking || crossSections.Count < 2)
        {
            return;
        }

        var maxBankRad = DegreesToRadians(parameters.MaxBankAngleDegrees);

        for (int i = 0; i < crossSections.Count; i++)
        {
            var cs = crossSections[i];

            switch (cs.JunctionBankingBehavior)
            {
                case JunctionBankingBehavior.MaintainBanking:
                case JunctionBankingBehavior.Normal:
                    // Keep the existing bank angle (calculated in basic phase)
                    break;

                case JunctionBankingBehavior.SuppressBanking:
                    // Blend from calculated banking to zero at junction
                    cs.BankAngleRadians *= cs.JunctionBankingFactor;
                    break;

                case JunctionBankingBehavior.AdaptToHigherPriority:
                    // For adapting roads:
                    // - At junction (factor=0): Use adaptive angle (ramp to primary road)
                    // - Far from junction (factor=1): Use our own curvature-based angle
                    var ourBankAngle = cs.BankAngleRadians;
                    
                    // Get the adaptive bank angle (this should be a ramp angle, not the primary's angle)
                    // If not provided, suppress banking at junction (safe default)
                    var adaptiveAngle = getAdaptiveBankAngle?.Invoke(cs) ?? 0f;

                    // Interpolate: at junction = adaptive angle, far from junction = our banking
                    cs.BankAngleRadians = Lerp(adaptiveAngle, ourBankAngle, cs.JunctionBankingFactor);
                    break;
            }
        }

        // Recalculate 3D banked normals with updated angles
        CalculateBankedNormals(crossSections);
    }

    /// <summary>
    /// Calculates bank angles for cross-sections with junction awareness.
    /// IMPORTANT: Junction banking behavior must be calculated before calling this!
    /// </summary>
    /// <param name="crossSections">Ordered cross-sections for a single spline</param>
    /// <param name="parameters">Banking parameters controlling the calculation</param>
    /// <param name="getHigherPriorityBankAngle">
    /// Function to get the bank angle of a higher-priority road at a junction.
    /// Called when JunctionBankingBehavior is AdaptToHigherPriority.
    /// </param>
    public void CalculateBankingWithJunctionAwareness(
        IList<UnifiedCrossSection> crossSections,
        BankingParameters parameters,
        Func<UnifiedCrossSection, float>? getHigherPriorityBankAngle = null)
    {
        if (!parameters.EnableAutoBanking || crossSections.Count < 2)
        {
            foreach (var cs in crossSections)
            {
                cs.BankAngleRadians = 0;
                cs.BankedNormal3D = new Vector3(0, 0, 1);
            }
            return;
        }

        // Step 1: Calculate curvature at each point
        _curvatureCalculator.CalculateCurvature(crossSections);

        // Step 2: Calculate bank angles based on junction behavior
        var maxBankRad = DegreesToRadians(parameters.MaxBankAngleDegrees);

        for (int i = 0; i < crossSections.Count; i++)
        {
            var cs = crossSections[i];
            float bankAngle;

            switch (cs.JunctionBankingBehavior)
            {
                case JunctionBankingBehavior.MaintainBanking:
                case JunctionBankingBehavior.Normal:
                    // Full banking based on curvature
                    bankAngle = CalculateBankAngleFromCurvature(
                        cs.Curvature,
                        parameters.CurvatureToBankScale,
                        parameters.BankStrength,
                        maxBankRad);
                    break;

                case JunctionBankingBehavior.SuppressBanking:
                    // Blend from calculated banking to zero at junction
                    var normalBankAngle = CalculateBankAngleFromCurvature(
                        cs.Curvature,
                        parameters.CurvatureToBankScale,
                        parameters.BankStrength,
                        maxBankRad);
                    bankAngle = normalBankAngle * cs.JunctionBankingFactor;
                    break;

                case JunctionBankingBehavior.AdaptToHigherPriority:
                    // Blend from our banking to the higher-priority road's banking
                    var ourBankAngle = CalculateBankAngleFromCurvature(
                        cs.Curvature,
                        parameters.CurvatureToBankScale,
                        parameters.BankStrength,
                        maxBankRad);

                    var targetAngle = getHigherPriorityBankAngle?.Invoke(cs) ?? 0f;

                    // Interpolate: far from junction = our banking, at junction = target banking
                    bankAngle = Lerp(targetAngle, ourBankAngle, cs.JunctionBankingFactor);
                    break;

                default:
                    bankAngle = 0;
                    break;
            }

            cs.BankAngleRadians = bankAngle;
        }

        // Step 3: Apply falloff blending for smooth transitions (curve entry/exit)
        // Only apply to sections with Normal or MaintainBanking behavior
        var rawAngles = crossSections.Select(cs => cs.BankAngleRadians).ToArray();
        ApplyFalloffBlendingWithJunctionAwareness(crossSections, rawAngles, parameters);

        // Step 4: Calculate 3D banked normals
        CalculateBankedNormals(crossSections);
    }

    /// <summary>
    /// Calculates the bank angle from curvature using the configured scaling.
    /// </summary>
    /// <param name="curvature">Curvature value (1/radius in 1/meters)</param>
    /// <param name="curvatureScale">Scale factor for curvature-to-banking conversion</param>
    /// <param name="strength">Banking strength multiplier (0-1)</param>
    /// <param name="maxBankRad">Maximum bank angle in radians</param>
    /// <returns>Signed bank angle in radians</returns>
    private static float CalculateBankAngleFromCurvature(
        float curvature,
        float curvatureScale,
        float strength,
        float maxBankRad)
    {
        // Convert curvature to normalized banking factor [0, 1]
        var bankFactor = MathF.Min(MathF.Abs(curvature) * curvatureScale, 1f);

        // Apply strength multiplier
        bankFactor *= strength;

        // Calculate angle (preserve sign for direction)
        // Positive curvature (left turn) = positive bank angle (right side higher)
        var sign = MathF.Sign(curvature);
        return sign * bankFactor * maxBankRad;
    }

    /// <summary>
    /// Applies distance-based falloff to smooth banking transitions.
    /// Based on BeamNG's autoBankFalloff algorithm.
    /// 
    /// IMPORTANT: Only considers nearby cross-sections within the transition length,
    /// not the entire spline. This prevents averaging away banking on long splines.
    /// </summary>
    private void ApplyFalloffBlending(
        IList<UnifiedCrossSection> crossSections,
        float[] rawBankAngles,
        BankingParameters parameters)
    {
        var smoothedAngles = new float[crossSections.Count];
        var falloff = parameters.AutoBankFalloff;
        var transitionLength = parameters.BankTransitionLengthMeters;

        // For each cross-section, blend bank angles from nearby nodes based on distance
        // Only look at cross-sections within 2x transition length to avoid averaging entire spline
        for (int i = 0; i < crossSections.Count; i++)
        {
            var currentDist = crossSections[i].DistanceAlongSpline;
            var blendedAngle = 0f;
            var totalWeight = 0f;
            
            // Search window: only check nearby cross-sections (within 2x transition length)
            var searchRadius = transitionLength * 2f;

            // Search backward from current position
            for (int j = i; j >= 0; j--)
            {
                var nodeDist = crossSections[j].DistanceAlongSpline;
                var distFromNode = currentDist - nodeDist;
                
                if (distFromNode > searchRadius)
                    break; // No need to check further back

                // Calculate falloff weight: max(0, 1 - |dist| * falloff / transitionLength)
                var weight = MathF.Max(0f, 1f - distFromNode * falloff / transitionLength);

                if (weight > 0.001f)
                {
                    blendedAngle += rawBankAngles[j] * weight;
                    totalWeight += weight;
                }
            }
            
            // Search forward from current position (skip i since we already added it)
            for (int j = i + 1; j < crossSections.Count; j++)
            {
                var nodeDist = crossSections[j].DistanceAlongSpline;
                var distFromNode = nodeDist - currentDist;
                
                if (distFromNode > searchRadius)
                    break; // No need to check further ahead

                var weight = MathF.Max(0f, 1f - distFromNode * falloff / transitionLength);

                if (weight > 0.001f)
                {
                    blendedAngle += rawBankAngles[j] * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight > 0.001f)
            {
                smoothedAngles[i] = blendedAngle / totalWeight;
            }
            else
            {
                smoothedAngles[i] = rawBankAngles[i];
            }
        }

        // Apply smoothed angles
        for (int i = 0; i < crossSections.Count; i++)
        {
            crossSections[i].BankAngleRadians = smoothedAngles[i];
        }
    }

    /// <summary>
    /// Applies falloff blending while respecting junction banking behavior.
    /// Only blends between cross-sections with compatible junction behaviors.
    /// 
    /// IMPORTANT: Only considers nearby cross-sections within the transition length,
    /// not the entire spline. This prevents averaging away banking on long splines.
    /// </summary>
    private void ApplyFalloffBlendingWithJunctionAwareness(
        IList<UnifiedCrossSection> crossSections,
        float[] rawBankAngles,
        BankingParameters parameters)
    {
        var smoothedAngles = new float[crossSections.Count];
        var falloff = parameters.AutoBankFalloff;
        var transitionLength = parameters.BankTransitionLengthMeters;
        var searchRadius = transitionLength * 2f;

        for (int i = 0; i < crossSections.Count; i++)
        {
            var cs = crossSections[i];
            var currentDist = cs.DistanceAlongSpline;

            // For junction-affected sections, reduce or skip falloff blending
            if (cs.JunctionBankingBehavior == JunctionBankingBehavior.SuppressBanking ||
                cs.JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority)
            {
                // Keep the already-calculated junction-aware angle
                smoothedAngles[i] = rawBankAngles[i];
                continue;
            }

            var blendedAngle = 0f;
            var totalWeight = 0f;

            // Search backward from current position
            for (int j = i; j >= 0; j--)
            {
                var otherCs = crossSections[j];
                var nodeDist = otherCs.DistanceAlongSpline;
                var distFromNode = currentDist - nodeDist;
                
                if (distFromNode > searchRadius)
                    break;

                // Skip blending with junction-affected sections
                if (otherCs.JunctionBankingBehavior == JunctionBankingBehavior.SuppressBanking ||
                    otherCs.JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority)
                {
                    continue;
                }

                var weight = MathF.Max(0f, 1f - distFromNode * falloff / transitionLength);

                if (weight > 0.001f)
                {
                    blendedAngle += rawBankAngles[j] * weight;
                    totalWeight += weight;
                }
            }
            
            // Search forward from current position (skip i since we already added it)
            for (int j = i + 1; j < crossSections.Count; j++)
            {
                var otherCs = crossSections[j];
                var nodeDist = otherCs.DistanceAlongSpline;
                var distFromNode = nodeDist - currentDist;
                
                if (distFromNode > searchRadius)
                    break;

                // Skip blending with junction-affected sections
                if (otherCs.JunctionBankingBehavior == JunctionBankingBehavior.SuppressBanking ||
                    otherCs.JunctionBankingBehavior == JunctionBankingBehavior.AdaptToHigherPriority)
                {
                    continue;
                }

                var weight = MathF.Max(0f, 1f - distFromNode * falloff / transitionLength);

                if (weight > 0.001f)
                {
                    blendedAngle += rawBankAngles[j] * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight > 0.001f)
            {
                smoothedAngles[i] = blendedAngle / totalWeight;
            }
            else
            {
                smoothedAngles[i] = rawBankAngles[i];
            }
        }

        // Apply smoothed angles
        for (int i = 0; i < crossSections.Count; i++)
        {
            crossSections[i].BankAngleRadians = smoothedAngles[i];
        }
    }

    /// <summary>
    /// Calculates the 3D banked normal by rotating the horizontal normal
    /// around the tangent axis by the bank angle.
    /// Uses Rodrigues' rotation formula.
    /// </summary>
    private void CalculateBankedNormals(IList<UnifiedCrossSection> crossSections)
    {
        foreach (var cs in crossSections)
        {
            if (MathF.Abs(cs.BankAngleRadians) < 0.0001f)
            {
                // No banking - use vertical normal
                cs.BankedNormal3D = new Vector3(0, 0, 1);
                continue;
            }

            // Start with horizontal normal (perpendicular to road in 2D plane, pointing up)
            // The horizontal normal is a vector perpendicular to the road AND horizontal
            // When we bank, we rotate around the tangent to tilt this normal
            var horizontalNormal = new Vector3(cs.NormalDirection.X, cs.NormalDirection.Y, 0);

            // Tangent axis (3D, along road direction)
            var tangentAxis = Vector3.Normalize(
                new Vector3(cs.TangentDirection.X, cs.TangentDirection.Y, 0));

            // The surface normal starts as (0, 0, 1) - pointing straight up
            // Banking rotates this around the tangent axis
            var surfaceNormal = new Vector3(0, 0, 1);

            // Rotate surface normal around tangent by bank angle
            cs.BankedNormal3D = RotateAroundAxis(surfaceNormal, tangentAxis, cs.BankAngleRadians);

            // Ensure the banked normal points generally upward (positive Z)
            if (cs.BankedNormal3D.Z < 0)
            {
                cs.BankedNormal3D = -cs.BankedNormal3D;
            }

            cs.BankedNormal3D = Vector3.Normalize(cs.BankedNormal3D);
        }
    }

    /// <summary>
    /// Rotates a vector around an axis using Rodrigues' rotation formula.
    /// v_rot = v*cos(?) + (k×v)*sin(?) + k*(k·v)*(1-cos(?))
    /// where k is the normalized rotation axis.
    /// </summary>
    /// <param name="v">The vector to rotate</param>
    /// <param name="axis">The rotation axis (must be normalized)</param>
    /// <param name="angleRadians">The rotation angle in radians</param>
    /// <returns>The rotated vector</returns>
    private static Vector3 RotateAroundAxis(Vector3 v, Vector3 axis, float angleRadians)
    {
        var cos = MathF.Cos(angleRadians);
        var sin = MathF.Sin(angleRadians);

        return v * cos
             + Vector3.Cross(axis, v) * sin
             + axis * Vector3.Dot(axis, v) * (1 - cos);
    }

    /// <summary>
    /// Converts degrees to radians.
    /// </summary>
    private static float DegreesToRadians(float degrees)
    {
        return degrees * MathF.PI / 180f;
    }

    /// <summary>
    /// Converts radians to degrees.
    /// </summary>
    public static float RadiansToDegrees(float radians)
    {
        return radians * 180f / MathF.PI;
    }

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Gets the maximum bank angle that would be applied for a given curve radius.
    /// Useful for debugging and parameter tuning.
    /// </summary>
    /// <param name="radiusMeters">Curve radius in meters</param>
    /// <param name="parameters">Banking parameters</param>
    /// <returns>Bank angle in degrees</returns>
    public static float GetBankAngleForRadius(float radiusMeters, BankingParameters parameters)
    {
        if (radiusMeters <= 0)
        {
            return parameters.MaxBankAngleDegrees;
        }

        var curvature = 1f / radiusMeters;
        var bankFactor = MathF.Min(curvature * parameters.CurvatureToBankScale, 1f);
        bankFactor *= parameters.BankStrength;

        return bankFactor * parameters.MaxBankAngleDegrees;
    }

    /// <summary>
    /// Gets the minimum curve radius that would result in maximum banking.
    /// </summary>
    /// <param name="parameters">Banking parameters</param>
    /// <returns>Radius in meters below which maximum banking is applied</returns>
    public static float GetRadiusForMaxBanking(BankingParameters parameters)
    {
        // At max banking: curvature * scale * strength = 1
        // curvature = 1 / (scale * strength)
        // radius = scale * strength
        if (parameters.BankStrength < 0.001f)
        {
            return float.MaxValue;
        }

        return 1f / (parameters.CurvatureToBankScale * parameters.BankStrength);
    }
}
