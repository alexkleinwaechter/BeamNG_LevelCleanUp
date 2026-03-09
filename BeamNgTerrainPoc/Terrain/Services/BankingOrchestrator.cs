using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
/// Orchestrates all banking calculations in the correct order.
/// 
/// This is the main entry point for road banking in the terrain generation pipeline.
/// It coordinates:
/// 1. Priority-aware junction banking behavior calculation
/// 2. Curvature calculation
/// 3. Bank angle calculation (with junction awareness)
/// 4. Edge elevation calculation (with adaptive blending for lower-priority roads)
/// 5. Junction elevation adaptation (smooth ramp to banked surfaces)
/// 
/// IMPORTANT: Banking should be applied AFTER:
/// - Junction detection (network.Junctions populated)
/// - Elevation harmonization (TargetElevation set on all cross-sections)
/// 
/// This orchestrator keeps banking logic out of the large UnifiedRoadSmoother class.
/// </summary>
public class BankingOrchestrator
{
    private readonly PriorityAwareJunctionBankingCalculator _junctionBankingCalc;
    private readonly CurvatureCalculator _curvatureCalc;
    private readonly BankingCalculator _bankingCalc;
    private readonly BankedElevationCalculator _edgeCalc;

    public BankingOrchestrator()
    {
        _junctionBankingCalc = new PriorityAwareJunctionBankingCalculator();
        _curvatureCalc = new CurvatureCalculator();
        _bankingCalc = new BankingCalculator();
        _edgeCalc = new BankedElevationCalculator();
    }

    /// <summary>
    /// Pre-calculates banking (curvature, bank angles, edge elevations) BEFORE junction harmonization.
    /// 
    /// This is Phase 1 of the two-phase banking process. It must run BEFORE junction harmonization
    /// so that the harmonizer can account for banked road surfaces when calculating connection
    /// point elevations. Without this, secondary roads connecting to banked primary roads would
    /// get the wrong elevation (centerline instead of edge).
    /// 
    /// This phase calculates:
    /// 1. Curvature at each cross-section
    /// 2. Bank angles based on curvature and material parameters
    /// 3. Left/right edge elevations based on bank angle
    /// 
    /// Note: Junction banking behavior (AdaptToHigherPriority, etc.) is NOT applied here
    /// because junctions haven't been detected yet. That happens in FinalizeBankingAfterHarmonization.
    /// </summary>
    /// <param name="network">The road network with calculated target elevations.</param>
    /// <returns>True if any banking was pre-calculated.</returns>
    public bool ApplyBankingPreCalculation(
        UnifiedRoadNetwork network)
    {
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("BankingOrchestrator.PreCalculation");

        // Run configuration diagnostic first (before curvature calculation)
        DiagnoseBankingConfiguration(network);

        var enabledSplines = network.Splines
            .Where(s => !ShouldExcludeFromBanking(s))
            .ToList();

        TerrainLogger.Info($"BankingOrchestrator.PreCalculation: Pre-calculating banking for {enabledSplines.Count} spline(s)...");

        // Build cross-sections by spline lookup
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        int splinesProcessed = 0;

        foreach (var spline in enabledSplines)
        {
            var bankingParams = GetBankingParameters(spline) ?? new BankingParameters();

            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                continue;

            // Calculate curvature
            _curvatureCalc.CalculateCurvature(crossSections);

            // Smooth curvature to reduce noise from OSM node spacing
            if (bankingParams.EnableAutoBanking && bankingParams.CurvatureSmoothingWindow >= 3)
            {
                _curvatureCalc.SmoothCurvature(crossSections, bankingParams.CurvatureSmoothingWindow);
            }

            // Calculate bank angles (without junction awareness - that comes later)
            _bankingCalc.CalculateBankingBasic(crossSections, bankingParams);

            // Additional Gaussian smoothing pass for bank angles
            if (bankingParams.EnableAutoBanking && bankingParams.BankAngleSmoothingWindow >= 3)
            {
                _bankingCalc.SmoothBankAngles(crossSections, bankingParams.BankAngleSmoothingWindow);
            }

            // Calculate edge elevations
            var halfWidth = spline.Parameters.RoadWidthMeters / 2.0f;
            foreach (var cs in crossSections)
            {
                _edgeCalc.CalculateEdgeElevationsForCS(cs, halfWidth);
            }

            splinesProcessed++;
        }

        // Run curvature diagnostic AFTER curvature has been calculated
        DiagnoseCurvatureResults(network, enabledSplines, crossSectionsBySpline);

        TerrainLogger.Info($"BankingOrchestrator.PreCalculation: Pre-calculated banking for {splinesProcessed} splines");
        perfLog?.Timing($"Pre-calculated banking for {splinesProcessed} splines");

        return true;
    }

    /// <summary>
    /// Computes the maximum effective blend distance across all splines in the network.
    /// Used to synchronize banking transition distances with edge constraint blend zones.
    /// Falls back to 30m if no junction harmonization parameters are configured.
    /// </summary>
    private static float GetMaxEffectiveBlendDistance(UnifiedRoadNetwork network)
    {
        var maxBlendDistance = 30.0f; // Fallback default

        foreach (var spline in network.Splines)
        {
            var junctionParams = spline.Parameters.JunctionHarmonizationParameters;
            if (junctionParams == null)
                continue;

            var effectiveBlend = junctionParams.GetEffectiveBlendDistance(
                spline.Parameters.RoadWidthMeters);
            maxBlendDistance = MathF.Max(maxBlendDistance, effectiveBlend);
        }

        return maxBlendDistance;
    }

    /// <summary>
    /// Checks if a spline should be excluded from banking calculations.
    /// Roundabout splines are always excluded - they're circular and banking would create a tilted ring.
    /// </summary>
    private static bool ShouldExcludeFromBanking(ParameterizedRoadSpline spline)
    {
        return spline.IsRoundabout;
    }

    /// <summary>
    /// Gets the banking parameters for a spline.
    /// </summary>
    private static BankingParameters? GetBankingParameters(ParameterizedRoadSpline spline)
    {
        return spline.Parameters.GetSplineParameters()?.Banking;
    }

    /// <summary>
    /// Calculates the adaptive bank angle for a cross-section that needs to adapt to a higher-priority road.
    /// 
    /// CRITICAL: This does NOT return the higher-priority road's bank angle!
    /// Instead, it calculates a RAMP angle that will make the secondary road smoothly transition
    /// to the higher-priority road's banked surface.
    /// 
    /// The ramp angle is calculated based on:
    /// 1. The elevation difference between this CS's target elevation and the primary road's surface
    /// 2. The road width over which this ramp must occur
    /// 
    /// For example: If a secondary road is 10m wide and needs to rise 0.5m from left to right
    /// to meet a banked primary road, the bank angle would be arcsin(0.5/5) ? 5.7�
    /// </summary>
    private static float CalculateAdaptiveBankAngle(
        UnifiedCrossSection cs,
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        if (!cs.HigherPrioritySplineId.HasValue)
            return 0f;

        var nearestCS = FindNearestHigherPriorityCrossSection(cs, network, crossSectionsBySpline);
        if (nearestCS == null)
            return 0f;

        // Find where this secondary road connects to the primary road
        // Project this CS's center onto the primary road to find the connection direction
        var toPrimary = nearestCS.CenterPoint - cs.CenterPoint;
        var distToPrimary = toPrimary.Length();
        
        if (distToPrimary < 0.001f)
        {
            // Very close - just use the primary road's bank angle
            return nearestCS.BankAngleRadians;
        }

        // Calculate the primary road's surface elevation at the point where we connect
        var connectionPoint = nearestCS.CenterPoint;
        var primarySurfaceElevation = BankedTerrainHelper.GetBankedElevation(nearestCS, cs.CenterPoint);
        
        if (float.IsNaN(primarySurfaceElevation))
            primarySurfaceElevation = nearestCS.TargetElevation;

        // Calculate the elevation difference between this CS's center and the primary road's surface
        var elevationDifference = primarySurfaceElevation - cs.TargetElevation;

        // The "connection edge" of our road is the edge that faces the primary road
        // Determine which direction (left or right) faces the primary road
        var dirToPrimary = Vector2.Normalize(toPrimary);
        var dotWithNormal = Vector2.Dot(dirToPrimary, cs.NormalDirection);
        
        // If dotWithNormal > 0, the primary road is to our RIGHT
        // If dotWithNormal < 0, the primary road is to our LEFT
        var primaryIsOnRight = dotWithNormal > 0;

        // Calculate the bank angle needed to create a ramp
        var halfWidth = cs.EffectiveRoadWidth / 2.0f;
        
        // The edge facing the primary road needs to match the primary road's surface elevation
        // The opposite edge stays at our TargetElevation (or close to it)
        
        // If primary is on the right:
        //   - Right edge should be at primarySurfaceElevation
        //   - Left edge stays at TargetElevation
        //   - elevationDelta = (rightElev - leftElev) / 2 = (primarySurface - target) / 2
        //   - But for banking: right = center + delta, left = center - delta
        //   - So: bankAngle = arcsin(elevationDelta / halfWidth)
        
        // Simplified: the bank angle makes the connecting edge rise/fall by elevationDifference
        // Over the half-width distance
        var rampAngle = MathF.Asin(Math.Clamp(elevationDifference / halfWidth, -1f, 1f));

        // If primary is on the left, we need to negate the angle
        // (banking positive = right side higher)
        if (!primaryIsOnRight)
        {
            rampAngle = -rampAngle;
        }

        // Clamp to reasonable limits (max ~20 degrees for ramps)
        var maxRampAngle = 20f * MathF.PI / 180f;
        rampAngle = Math.Clamp(rampAngle, -maxRampAngle, maxRampAngle);

        return rampAngle;
    }

    /// <summary>
    /// Gets the bank angle of the nearest cross-section on a higher-priority road.
    /// Used for AdaptToHigherPriority behavior.
    /// 
    /// NOTE: This is the OLD method that just returns the primary road's bank angle.
    /// For proper ramp calculation, use CalculateAdaptiveBankAngle instead.
    /// </summary>
    private static float GetHigherPriorityBankAngle(
        UnifiedCrossSection cs,
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        // Use the new adaptive calculation instead of just returning the primary's angle
        return CalculateAdaptiveBankAngle(cs, network, crossSectionsBySpline);
    }

    /// <summary>
    /// Finds the nearest cross-section on the higher-priority road.
    /// </summary>
    private static UnifiedCrossSection? FindNearestHigherPriorityCrossSection(
        UnifiedCrossSection adaptingCS,
        UnifiedRoadNetwork network,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        if (!adaptingCS.HigherPrioritySplineId.HasValue)
            return null;

        if (!crossSectionsBySpline.TryGetValue(adaptingCS.HigherPrioritySplineId.Value, out var higherPriorityCSList))
            return null;

        // Find the nearest cross-section by distance
        UnifiedCrossSection? nearest = null;
        var nearestDistSq = float.MaxValue;

        foreach (var cs in higherPriorityCSList)
        {
            var distSq = Vector2.DistanceSquared(cs.CenterPoint, adaptingCS.CenterPoint);
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = cs;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Logs banking statistics for the network.
    /// </summary>
    private static void LogBankingStatistics(UnifiedRoadNetwork network)
    {
        var bankedCount = network.CrossSections.Count(cs => MathF.Abs(cs.BankAngleRadians) > 0.001f);
        var totalCount = network.CrossSections.Count;

        if (bankedCount == 0)
        {
            TerrainLogger.Info("BankingOrchestrator: No cross-sections have significant banking.");
            return;
        }

        var maxAngleRad = network.CrossSections.Max(cs => MathF.Abs(cs.BankAngleRadians));
        var maxAngleDeg = maxAngleRad * 180f / MathF.PI;

        var avgAngleRad = network.CrossSections
            .Where(cs => MathF.Abs(cs.BankAngleRadians) > 0.001f)
            .Average(cs => MathF.Abs(cs.BankAngleRadians));
        var avgAngleDeg = avgAngleRad * 180f / MathF.PI;

        TerrainLogger.Info(
            $"BankingOrchestrator: Applied banking to {bankedCount}/{totalCount} cross-sections, " +
            $"max angle: {maxAngleDeg:F1}�, avg angle: {avgAngleDeg:F1}�");

        // Log behavior breakdown
        var behaviorCounts = network.CrossSections
            .Where(cs => MathF.Abs(cs.BankAngleRadians) > 0.001f)
            .GroupBy(cs => cs.JunctionBankingBehavior)
            .ToDictionary(g => g.Key, g => g.Count());

        if (behaviorCounts.Count > 1)
        {
            TerrainLogger.Info(
                $"  Banking by junction behavior: " +
                $"Normal/Maintain={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.Normal) + behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.MaintainBanking)}, " +
                $"Adapt={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.AdaptToHigherPriority)}, " +
                $"Suppress={behaviorCounts.GetValueOrDefault(JunctionBankingBehavior.SuppressBanking)}");
        }
    }

    /// <summary>
    /// Gets the maximum elevation difference between left and right road edges in the network.
    /// Useful for verification and debugging.
    /// </summary>
    public static float GetMaxEdgeElevationDifference(UnifiedRoadNetwork network)
    {
        return network.CrossSections
            .Where(cs => !float.IsNaN(cs.LeftEdgeElevation) && !float.IsNaN(cs.RightEdgeElevation))
            .Select(cs => MathF.Abs(cs.RightEdgeElevation - cs.LeftEdgeElevation))
            .DefaultIfEmpty(0f)
            .Max();
    }

    /// <summary>
    /// Gets banking information for a specific spline.
    /// </summary>
    public static SplineBankingInfo GetSplineBankingInfo(
        UnifiedRoadNetwork network,
        int splineId)
    {
        var crossSections = network.GetCrossSectionsForSpline(splineId).ToList();

        if (crossSections.Count == 0)
        {
            return new SplineBankingInfo
            {
                SplineId = splineId,
                CrossSectionCount = 0
            };
        }

        var bankedCS = crossSections.Where(cs => MathF.Abs(cs.BankAngleRadians) > 0.001f).ToList();

        return new SplineBankingInfo
        {
            SplineId = splineId,
            CrossSectionCount = crossSections.Count,
            BankedCrossSectionCount = bankedCS.Count,
            MaxBankAngleDegrees = bankedCS.Count > 0
                ? bankedCS.Max(cs => MathF.Abs(cs.BankAngleRadians)) * 180f / MathF.PI
                : 0f,
            MaxCurvature = crossSections.Max(cs => MathF.Abs(cs.Curvature)),
            JunctionAffectedCount = crossSections.Count(cs =>
                cs.JunctionBankingBehavior != JunctionBankingBehavior.Normal &&
                cs.JunctionBankingBehavior != JunctionBankingBehavior.MaintainBanking)
        };
    }

    /// <summary>
    /// Diagnostic method to trace why banking is or isn't being applied.
    /// Writes detailed diagnostic information to the log file only (not UI).
    /// NOTE: This runs BEFORE curvature calculation, so curvature values will be 0.
    /// </summary>
    /// <param name="network">The road network to diagnose.</param>
    public static void DiagnoseBankingConfiguration(UnifiedRoadNetwork network)
    {
        var perfLog = TerrainCreationLogger.Current;
        
        // Use Detail() for file-only logging
        void Log(string message) => perfLog?.Detail(message);
        
        Log("=== BANKING CONFIGURATION DIAGNOSTIC ===");
        Log("");

        // Check if network has any splines
        if (network.Splines.Count == 0)
        {
            Log("PROBLEM: Network has NO splines!");
            Log("   Banking cannot be applied without road splines.");
            Log("=== END CONFIGURATION DIAGNOSTIC ===");
            return;
        }

        Log($"Network has {network.Splines.Count} spline(s):");
        Log("");

        int splinesWithBankingEnabled = 0;
        int splinesWithBankingDisabled = 0;
        int splinesWithNullBanking = 0;
        int splinesWithNullSplineParams = 0;

        foreach (var spline in network.Splines)
        {
            Log($"  Spline ID={spline.SplineId}, Priority={spline.Priority}, IsRoundabout={spline.IsRoundabout}:");

            // Roundabout splines are never banked
            if (spline.IsRoundabout)
            {
                Log($"    [SKIP] IsRoundabout = TRUE (banking never applied to roundabouts)");
                continue;
            }

            // Check SplineParameters
            var splineParams = spline.Parameters.SplineParameters;
            if (splineParams == null)
            {
                Log($"    [FAIL] SplineParameters is NULL");
                splinesWithNullSplineParams++;
                continue;
            }

            // Check Banking object
            var banking = splineParams.Banking;
            if (banking == null)
            {
                Log($"    [FAIL] Banking object is NULL (banking disabled by default)");
                splinesWithNullBanking++;
                continue;
            }

            // Check EnableAutoBanking flag
            if (!banking.EnableAutoBanking)
            {
                Log($"    [INFO] EnableAutoBanking = FALSE (banking pipeline runs with angle=0)");
                splinesWithBankingDisabled++;
                continue;
            }

            // Banking is enabled with user-configured angles
            splinesWithBankingEnabled++;
            Log($"    [OK] EnableAutoBanking = TRUE");
            Log($"       MaxBankAngleDegrees = {banking.MaxBankAngleDegrees:F1} deg");
            Log($"       BankStrength = {banking.BankStrength:F2}");
            Log($"       AutoBankFalloff = {banking.AutoBankFalloff:F2}");
            Log($"       CurvatureToBankScale = {banking.CurvatureToBankScale:F1}");
            Log($"       MinCurveRadiusForMaxBank = {banking.MinCurveRadiusForMaxBank:F1}m");
            Log($"       BankTransitionLengthMeters = {banking.BankTransitionLengthMeters:F1}m");

            // Check for potential issues
            if (banking.BankStrength <= 0.01f)
            {
                Log($"    [WARN] BankStrength is very low ({banking.BankStrength:F2}), banking will be minimal!");
            }
            if (banking.MaxBankAngleDegrees <= 1.0f)
            {
                Log($"    [WARN] MaxBankAngleDegrees is very low ({banking.MaxBankAngleDegrees:F1} deg), banking will be minimal!");
            }
        }

        Log("");
        Log("=== CONFIGURATION SUMMARY ===");
        Log($"  Splines with banking ENABLED:  {splinesWithBankingEnabled}");
        Log($"  Splines with banking DISABLED: {splinesWithBankingDisabled}");
        Log($"  Splines with NULL Banking:     {splinesWithNullBanking}");
        Log($"  Splines with NULL SplineParams: {splinesWithNullSplineParams}");
        Log("");

        if (splinesWithBankingEnabled == 0)
        {
            Log($"[OK] CONCLUSION: No splines have user-enabled banking. Pipeline runs with angle=0 for junction adaptation.");
        }
        else
        {
            Log($"[OK] CONCLUSION: {splinesWithBankingEnabled} spline(s) have banking enabled.");
            Log("   Curvature will be calculated next...");
        }

        Log("");
        Log("=== END CONFIGURATION DIAGNOSTIC ===");
    }

    /// <summary>
    /// Diagnostic method to analyze curvature and banking results AFTER calculation.
    /// Writes detailed diagnostic information to the log file only (not UI).
    /// </summary>
    private static void DiagnoseCurvatureResults(
        UnifiedRoadNetwork network,
        List<ParameterizedRoadSpline> enabledSplines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var perfLog = TerrainCreationLogger.Current;
        
        // Use Detail() for file-only logging
        void Log(string message) => perfLog?.Detail(message);
        
        Log("");
        Log("=== CURVATURE & BANKING RESULTS DIAGNOSTIC ===");
        Log("");

        int totalCrossSections = 0;
        int crossSectionsWithCurvature = 0;
        int crossSectionsWithBanking = 0;
        float maxCurvature = 0;
        float maxBankAngle = 0;

        foreach (var spline in enabledSplines)
        {
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                continue;

            var splineCurvatures = crossSections.Select(cs => MathF.Abs(cs.Curvature)).ToList();
            var splineBankAngles = crossSections.Select(cs => MathF.Abs(cs.BankAngleRadians)).ToList();
            
            var splineMaxCurvature = splineCurvatures.Max();
            var splineMaxBankAngle = splineBankAngles.Max();
            var splineCurvedCount = splineCurvatures.Count(c => c > 0.0001f);
            var splineBankedCount = splineBankAngles.Count(b => b > 0.001f);

            totalCrossSections += crossSections.Count;
            crossSectionsWithCurvature += splineCurvedCount;
            crossSectionsWithBanking += splineBankedCount;
            maxCurvature = MathF.Max(maxCurvature, splineMaxCurvature);
            maxBankAngle = MathF.Max(maxBankAngle, splineMaxBankAngle);

            Log($"  Spline ID={spline.SplineId}:");
            Log($"    Cross-sections: {crossSections.Count}");
            Log($"    With curvature (>0.0001): {splineCurvedCount}");
            Log($"    With banking (>0.001 rad): {splineBankedCount}");
            Log($"    Max curvature: {splineMaxCurvature:F6} (radius ~{(splineMaxCurvature > 0.0001f ? 1f/splineMaxCurvature : float.MaxValue):F0}m)");
            Log($"    Max bank angle: {splineMaxBankAngle * 180f / MathF.PI:F2} deg");

            // Sample some cross-sections to see curvature distribution
            if (crossSections.Count > 0)
            {
                var samples = new[] { 0, crossSections.Count / 4, crossSections.Count / 2, 3 * crossSections.Count / 4, crossSections.Count - 1 };
                Log($"    Sample curvatures at indices:");
                foreach (var idx in samples.Where(i => i < crossSections.Count).Distinct())
                {
                    var cs = crossSections[idx];
                    Log($"      [{idx}]: pos=({cs.CenterPoint.X:F1},{cs.CenterPoint.Y:F1}), curv={cs.Curvature:F6}, bank={cs.BankAngleRadians * 180f / MathF.PI:F2} deg");
                }
            }
        }

        Log("");
        Log("=== CURVATURE SUMMARY ===");
        Log($"  Total cross-sections in banking-enabled splines: {totalCrossSections}");
        Log($"  Cross-sections with curvature (>0.0001): {crossSectionsWithCurvature}");
        Log($"  Cross-sections with banking (>0.001 rad): {crossSectionsWithBanking}");
        Log($"  Max curvature found: {maxCurvature:F6} (radius ~{(maxCurvature > 0.0001f ? 1f/maxCurvature : float.MaxValue):F0}m)");
        Log($"  Max bank angle found: {maxBankAngle * 180f / MathF.PI:F2} deg");
        Log("");

        if (maxCurvature < 0.0001f)
        {
            Log("[PROBLEM] All curvature values are essentially ZERO!");
            Log("");
            Log("   This means the CurvatureCalculator found no curves in the road geometry.");
            Log("   Possible causes:");
            Log("   1. Roads are truly straight (no curves at all)");
            Log("   2. Cross-section spacing is too large (missing curve details)");
            Log("   3. Control points are collinear (spline has no curvature)");
            Log("   4. Cross-sections are not properly ordered (LocalIndex issue)");
            Log("");
            Log("   Debug: Check the spline debug images to verify road geometry has curves.");
        }
        else if (crossSectionsWithBanking == 0)
        {
            Log("[PROBLEM] Curvature exists but NO banking was applied!");
            Log("");
            Log("   Possible causes:");
            Log("   1. BankStrength is too low");
            Log("   2. CurvatureToBankScale is too low");
            Log("   3. AutoBankFalloff is filtering out the banking");
        }
        else
        {
            Log($"[OK] Banking applied to {crossSectionsWithBanking} cross-sections.");
        }

        Log("");
        Log("=== END CURVATURE DIAGNOSTIC ===");
    }
}

/// <summary>
/// Banking information for a single spline.
/// </summary>
public record SplineBankingInfo
{
    public int SplineId { get; init; }
    public int CrossSectionCount { get; init; }
    public int BankedCrossSectionCount { get; init; }
    public float MaxBankAngleDegrees { get; init; }
    public float MaxCurvature { get; init; }
    public int JunctionAffectedCount { get; init; }
}
