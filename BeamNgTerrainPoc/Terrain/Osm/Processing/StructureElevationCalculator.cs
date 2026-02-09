using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Calculates elevation profiles for bridge and tunnel structures.
/// Bridges and tunnels have independent elevation profiles that don't follow terrain.
/// This calculator determines appropriate vertical curves based on structure type and length.
/// </summary>
/// <remarks>
/// <para>Bridge profiles:</para>
/// <list type="bullet">
/// <item>Short bridges (&lt;50m): Linear profile between entry/exit</item>
/// <item>Medium bridges (50-200m): Slight sag curve for drainage (parabolic)</item>
/// <item>Long bridges (&gt;200m): Arch profile for cable-stayed/suspension designs</item>
/// </list>
/// <para>Tunnel profiles:</para>
/// <list type="bullet">
/// <item>Short tunnels (&lt;100m): Linear profile if clearance allows</item>
/// <item>Medium/Long tunnels: S-curve when terrain requires deeper passage</item>
/// <item>Must maintain minimum clearance below terrain surface</item>
/// <item>Descent phase (25%), level phase (50%), ascent phase (25%)</item>
/// </list>
/// </remarks>
public class StructureElevationCalculator
{
    // ========================================
    // BRIDGE LENGTH THRESHOLDS
    // ========================================

    /// <summary>
    /// Maximum length for a "short" bridge that uses linear profile.
    /// Bridges shorter than this get simple linear interpolation.
    /// </summary>
    public float ShortBridgeMaxLengthMeters { get; set; } = 50.0f;

    /// <summary>
    /// Maximum length for a "medium" bridge that uses sag curve.
    /// Bridges between ShortBridgeMaxLength and this value get parabolic sag curve.
    /// Bridges longer than this get arch profile.
    /// </summary>
    public float MediumBridgeMaxLengthMeters { get; set; } = 200.0f;

    // ========================================
    // SAG CURVE PARAMETERS (Medium bridges)
    // ========================================

    /// <summary>
    /// Maximum sag depth as a percentage of bridge length.
    /// Default 0.5% means a 100m bridge sags 0.5m at center.
    /// This provides drainage while being imperceptible to drivers.
    /// </summary>
    public float SagCurveMaxPercent { get; set; } = 0.005f;

    /// <summary>
    /// Maximum absolute sag depth in meters.
    /// Limits the sag for very long bridges to prevent excessive dip.
    /// Default 2m is typical for bridge drainage design.
    /// </summary>
    public float SagCurveMaxAbsoluteMeters { get; set; } = 2.0f;

    // ========================================
    // ARCH CURVE PARAMETERS (Long bridges)
    // ========================================

    /// <summary>
    /// Maximum arch rise as a percentage of bridge length.
    /// Default 1% means a 500m bridge rises 5m at center.
    /// </summary>
    public float ArchCurveMaxPercent { get; set; } = 0.01f;

    /// <summary>
    /// Maximum absolute arch rise in meters.
    /// Limits the rise for very long bridges.
    /// Default 10m is typical for major suspension bridges.
    /// </summary>
    public float ArchCurveMaxAbsoluteMeters { get; set; } = 10.0f;

    /// <summary>
    /// Minimum bridge length for arch profile application.
    /// Bridges shorter than this won't have arch applied even if categorized as "long".
    /// </summary>
    public float ArchCurveMinLengthMeters { get; set; } = 200.0f;

    // ========================================
    // TUNNEL PARAMETERS
    // ========================================

    /// <summary>
    /// Minimum vertical clearance for tunnels below terrain surface (meters).
    /// This is the distance from terrain surface to tunnel ceiling.
    /// Default: 5.0m (reasonable rock/soil cover).
    /// </summary>
    public float TunnelMinClearanceMeters { get; set; } = 5.0f;

    /// <summary>
    /// Assumed tunnel interior height (floor to ceiling) in meters.
    /// Used to calculate required floor elevation from clearance.
    /// Default: 5.0m (standard road tunnel height).
    /// </summary>
    public float TunnelInteriorHeightMeters { get; set; } = 5.0f;

    /// <summary>
    /// Maximum grade (slope) percentage allowed for tunnel approaches.
    /// Steeper grades may be uncomfortable or unsafe for vehicles.
    /// Default: 6.0% (typical maximum for road tunnels).
    /// </summary>
    public float TunnelMaxGradePercent { get; set; } = 6.0f;

    /// <summary>
    /// Maximum length for a "short" tunnel that uses linear profile.
    /// Tunnels shorter than this get simple linear interpolation (if clearance allows).
    /// </summary>
    public float ShortTunnelMaxLengthMeters { get; set; } = 100.0f;

    /// <summary>
    /// Fraction of tunnel length for descent phase in S-curve profile.
    /// Default: 0.25 (25% of tunnel length for initial descent).
    /// </summary>
    public float TunnelDescentPhaseFraction { get; set; } = 0.25f;

    /// <summary>
    /// Fraction of tunnel length for ascent phase in S-curve profile.
    /// Default: 0.25 (25% of tunnel length for final ascent).
    /// The level phase is: 1.0 - DescentFraction - AscentFraction (50% by default).
    /// </summary>
    public float TunnelAscentPhaseFraction { get; set; } = 0.25f;

    // ========================================
    // BRIDGE PROFILE CALCULATION
    // ========================================

    /// <summary>
    /// Creates an elevation profile for a bridge structure.
    /// Automatically selects curve type based on bridge length.
    /// </summary>
    /// <param name="bridgeSpline">The bridge spline to create a profile for.</param>
    /// <param name="entryElevation">Elevation at the bridge entry (from connecting road).</param>
    /// <param name="exitElevation">Elevation at the bridge exit (from connecting road).</param>
    /// <returns>An elevation profile configured for the bridge.</returns>
    public StructureElevationProfile CalculateBridgeProfile(
        ParameterizedRoadSpline bridgeSpline,
        float entryElevation,
        float exitElevation)
    {
        return CalculateBridgeProfile(
            bridgeSpline.TotalLengthMeters,
            entryElevation,
            exitElevation);
    }

    /// <summary>
    /// Creates an elevation profile for a bridge structure.
    /// Automatically selects curve type based on bridge length.
    /// </summary>
    /// <param name="lengthMeters">Total length of the bridge in meters.</param>
    /// <param name="entryElevation">Elevation at the bridge entry (from connecting road).</param>
    /// <param name="exitElevation">Elevation at the bridge exit (from connecting road).</param>
    /// <returns>An elevation profile configured for the bridge.</returns>
    public StructureElevationProfile CalculateBridgeProfile(
        float lengthMeters,
        float entryElevation,
        float exitElevation)
    {
        // Determine curve type based on length
        var curveType = DetermineBridgeCurveType(lengthMeters);

        var profile = new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = lengthMeters,
            CurveType = curveType,
            IsValid = true
        };

        // Calculate lowest and highest points based on curve type
        switch (curveType)
        {
            case StructureElevationCurveType.Linear:
                profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
                profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);
                break;

            case StructureElevationCurveType.Parabolic:
                // Sag curve: lowest point is at center, below the linear interpolation
                var linearMidpoint = (entryElevation + exitElevation) / 2f;
                var sagOffset = CalculateSagCurveOffset(0.5f, lengthMeters);
                profile.CalculatedLowestPointElevation = linearMidpoint - sagOffset;
                profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);
                break;

            case StructureElevationCurveType.Arch:
                // Arch curve: highest point is at center, above the linear interpolation
                var archLinearMidpoint = (entryElevation + exitElevation) / 2f;
                var archOffset = CalculateArchCurveOffset(0.5f, lengthMeters);
                profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
                profile.CalculatedHighestPointElevation = archLinearMidpoint + archOffset;
                break;

            default:
                profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
                profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);
                break;
        }

        // Calculate max grade
        profile.MaxGradePercent = profile.AverageGradePercent;

        TerrainLogger.Info($"Bridge profile: {profile}");

        return profile;
    }

    /// <summary>
    /// Determines the appropriate curve type for a bridge based on its length.
    /// </summary>
    /// <param name="lengthMeters">Bridge length in meters.</param>
    /// <returns>The recommended curve type.</returns>
    public StructureElevationCurveType DetermineBridgeCurveType(float lengthMeters)
    {
        if (lengthMeters <= ShortBridgeMaxLengthMeters)
        {
            return StructureElevationCurveType.Linear;
        }
        
        if (lengthMeters <= MediumBridgeMaxLengthMeters)
        {
            return StructureElevationCurveType.Parabolic;
        }

        return StructureElevationCurveType.Arch;
    }

    // ========================================
    // ELEVATION CALCULATION AT DISTANCE
    // ========================================

    /// <summary>
    /// Calculates the elevation at a specific distance along a bridge.
    /// </summary>
    /// <param name="distanceAlongStructure">Distance from bridge entry in meters.</param>
    /// <param name="profile">The elevation profile for the bridge.</param>
    /// <returns>The calculated elevation at that distance.</returns>
    public float CalculateBridgeElevation(float distanceAlongStructure, StructureElevationProfile profile)
    {
        // Normalize distance to 0-1 range
        float t = profile.LengthMeters > 0
            ? Math.Clamp(distanceAlongStructure / profile.LengthMeters, 0f, 1f)
            : 0f;

        // Calculate base elevation (linear interpolation between entry and exit)
        float baseElevation = Lerp(profile.EntryElevation, profile.ExitElevation, t);

        return profile.CurveType switch
        {
            StructureElevationCurveType.Linear => baseElevation,

            StructureElevationCurveType.Parabolic =>
                // Sag curve: subtract offset (dips below linear)
                baseElevation - CalculateSagCurveOffset(t, profile.LengthMeters),

            StructureElevationCurveType.Arch =>
                // Arch curve: add offset (rises above linear)
                baseElevation + CalculateArchCurveOffset(t, profile.LengthMeters),

            _ => baseElevation
        };
    }

    /// <summary>
    /// Calculates the elevation at a normalized position (0-1) along a bridge.
    /// </summary>
    /// <param name="normalizedPosition">Position from 0 (entry) to 1 (exit).</param>
    /// <param name="profile">The elevation profile for the bridge.</param>
    /// <returns>The calculated elevation at that position.</returns>
    public float CalculateBridgeElevationNormalized(float normalizedPosition, StructureElevationProfile profile)
    {
        return CalculateBridgeElevation(normalizedPosition * profile.LengthMeters, profile);
    }

    // ========================================
    // CURVE OFFSET CALCULATIONS
    // ========================================

    /// <summary>
    /// Calculates the sag curve offset at a normalized position.
    /// The sag curve is a downward parabola with vertex at t=0.5 (center of bridge).
    /// </summary>
    /// <param name="t">Normalized position along bridge (0 to 1).</param>
    /// <param name="lengthMeters">Total length of the bridge.</param>
    /// <returns>The vertical offset (positive value representing downward sag).</returns>
    /// <remarks>
    /// The parabola formula: offset = 4 * maxSag * t * (1 - t)
    /// This creates a curve that:
    /// - Is 0 at t=0 (entry)
    /// - Reaches maximum at t=0.5 (center)
    /// - Returns to 0 at t=1 (exit)
    /// 
    /// The sag provides drainage for rainwater on the bridge deck.
    /// </remarks>
    public float CalculateSagCurveOffset(float t, float lengthMeters)
    {
        // Calculate maximum sag based on length, capped by absolute maximum
        float maxSag = Math.Min(
            lengthMeters * SagCurveMaxPercent,
            SagCurveMaxAbsoluteMeters);

        // Parabola: 4 * maxSag * t * (1 - t) peaks at t=0.5 with value maxSag
        return 4f * maxSag * t * (1f - t);
    }

    /// <summary>
    /// Calculates the arch curve offset at a normalized position.
    /// The arch curve is an upward parabola with vertex at t=0.5 (center of bridge).
    /// </summary>
    /// <param name="t">Normalized position along bridge (0 to 1).</param>
    /// <param name="lengthMeters">Total length of the bridge.</param>
    /// <returns>The vertical offset (positive value representing upward rise).</returns>
    /// <remarks>
    /// Used for long bridges (cable-stayed, suspension) where the center
    /// is typically higher than the endpoints.
    /// 
    /// The arch formula is identical to sag but the offset is added rather than subtracted.
    /// </remarks>
    public float CalculateArchCurveOffset(float t, float lengthMeters)
    {
        // No arch for bridges shorter than minimum
        if (lengthMeters < ArchCurveMinLengthMeters)
        {
            return 0f;
        }

        // Calculate maximum rise based on length, capped by absolute maximum
        float maxRise = Math.Min(
            lengthMeters * ArchCurveMaxPercent,
            ArchCurveMaxAbsoluteMeters);

        // Parabola: 4 * maxRise * t * (1 - t) peaks at t=0.5 with value maxRise
        return 4f * maxRise * t * (1f - t);
    }

    // ========================================
    // UTILITY METHODS
    // ========================================

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    /// <param name="a">Start value.</param>
    /// <param name="b">End value.</param>
    /// <param name="t">Interpolation factor (0-1).</param>
    /// <returns>Interpolated value.</returns>
    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Smooth step interpolation (ease in/out).
    /// Provides smooth transitions at the start and end.
    /// </summary>
    /// <param name="t">Input value (0-1).</param>
    /// <returns>Smoothed value (0-1).</returns>
    /// <remarks>
    /// Formula: 3t² - 2t³
    /// This creates an S-curve that:
    /// - Starts with zero slope at t=0
    /// - Ends with zero slope at t=1
    /// - Has inflection point at t=0.5
    /// 
    /// Useful for tunnel entry/exit transitions in Phase 3.
    /// </remarks>
    public static float SmoothStep(float t)
    {
        return t * t * (3f - 2f * t);
    }

    // ========================================
    // BATCH PROCESSING
    // ========================================

    /// <summary>
    /// Calculates elevation profiles for all bridge splines in a collection.
    /// </summary>
    /// <param name="splines">Collection of splines (bridges and non-bridges).</param>
    /// <param name="getEntryElevation">Function to get entry elevation for a spline.</param>
    /// <param name="getExitElevation">Function to get exit elevation for a spline.</param>
    /// <returns>Number of profiles calculated.</returns>
    public int CalculateBridgeProfiles(
        IEnumerable<ParameterizedRoadSpline> splines,
        Func<ParameterizedRoadSpline, float> getEntryElevation,
        Func<ParameterizedRoadSpline, float> getExitElevation)
    {
        int count = 0;

        foreach (var spline in splines.Where(s => s.IsBridge))
        {
            var entryElevation = getEntryElevation(spline);
            var exitElevation = getExitElevation(spline);

            spline.ElevationProfile = CalculateBridgeProfile(
                spline,
                entryElevation,
                exitElevation);

            count++;
        }

        if (count > 0)
        {
            TerrainLogger.Info($"Calculated elevation profiles for {count} bridge(s)");
        }

        return count;
    }

    /// <summary>
    /// Calculates elevation profiles for all bridge splines using a default elevation.
    /// Use this when connecting road elevations are not available.
    /// </summary>
    /// <param name="splines">Collection of splines (bridges and non-bridges).</param>
    /// <param name="defaultElevation">Default elevation to use for entry and exit.</param>
    /// <returns>Number of profiles calculated.</returns>
    public int CalculateBridgeProfilesWithDefault(
        IEnumerable<ParameterizedRoadSpline> splines,
        float defaultElevation)
    {
        return CalculateBridgeProfiles(
            splines,
            _ => defaultElevation,
            _ => defaultElevation);
    }

    // ========================================
    // ELEVATION SAMPLING FOR CROSS-SECTIONS
    // ========================================

    /// <summary>
    /// Samples elevations along a bridge at specified distances.
    /// Useful for generating cross-sections with correct target elevations.
    /// </summary>
    /// <param name="profile">The bridge elevation profile.</param>
    /// <param name="distances">Distances along the bridge in meters.</param>
    /// <returns>Array of elevations corresponding to each distance.</returns>
    public float[] SampleBridgeElevations(StructureElevationProfile profile, float[] distances)
    {
        var elevations = new float[distances.Length];

        for (int i = 0; i < distances.Length; i++)
        {
            elevations[i] = CalculateBridgeElevation(distances[i], profile);
        }

        return elevations;
    }

    /// <summary>
    /// Samples elevations along a bridge at regular intervals.
    /// </summary>
    /// <param name="profile">The bridge elevation profile.</param>
    /// <param name="sampleCount">Number of samples (including start and end).</param>
    /// <returns>Array of elevations at regular intervals.</returns>
    public float[] SampleBridgeElevationsUniform(StructureElevationProfile profile, int sampleCount)
    {
        if (sampleCount < 2)
        {
            sampleCount = 2;
        }

        var elevations = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            elevations[i] = CalculateBridgeElevationNormalized(t, profile);
        }

        return elevations;
    }

    // ========================================
    // TUNNEL PROFILE CALCULATION
    // ========================================

    /// <summary>
    /// Creates an elevation profile for a tunnel structure.
    /// Calculates whether a linear profile provides adequate clearance,
    /// or whether an S-curve is needed to go deeper under terrain.
    /// </summary>
    /// <param name="tunnelSpline">The tunnel spline to create a profile for.</param>
    /// <param name="entryElevation">Elevation at the tunnel entry (from connecting road).</param>
    /// <param name="exitElevation">Elevation at the tunnel exit (from connecting road).</param>
    /// <param name="terrainElevationsAlongPath">Terrain elevations sampled along the tunnel path.</param>
    /// <returns>An elevation profile configured for the tunnel.</returns>
    public StructureElevationProfile CalculateTunnelProfile(
        ParameterizedRoadSpline tunnelSpline,
        float entryElevation,
        float exitElevation,
        float[] terrainElevationsAlongPath)
    {
        return CalculateTunnelProfile(
            tunnelSpline.TotalLengthMeters,
            entryElevation,
            exitElevation,
            terrainElevationsAlongPath);
    }

    /// <summary>
    /// Creates an elevation profile for a tunnel structure.
    /// Calculates whether a linear profile provides adequate clearance,
    /// or whether an S-curve is needed to go deeper under terrain.
    /// </summary>
    /// <param name="lengthMeters">Total length of the tunnel in meters.</param>
    /// <param name="entryElevation">Elevation at the tunnel entry (from connecting road).</param>
    /// <param name="exitElevation">Elevation at the tunnel exit (from connecting road).</param>
    /// <param name="terrainElevationsAlongPath">Terrain elevations sampled along the tunnel path.</param>
    /// <returns>An elevation profile configured for the tunnel.</returns>
    public StructureElevationProfile CalculateTunnelProfile(
        float lengthMeters,
        float entryElevation,
        float exitElevation,
        float[] terrainElevationsAlongPath)
    {
        var profile = new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = lengthMeters,
            MinimumClearanceMeters = TunnelMinClearanceMeters,
            TerrainElevationsAlongPath = terrainElevationsAlongPath,
            IsValid = true
        };

        // If no terrain data, use linear profile as fallback
        if (terrainElevationsAlongPath == null || terrainElevationsAlongPath.Length == 0)
        {
            profile.CurveType = StructureElevationCurveType.Linear;
            profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
            profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);
            profile.ValidationMessage = "No terrain data available - using linear profile";
            TerrainLogger.Warning($"Tunnel profile: No terrain data, using linear fallback");
            return profile;
        }

        // Calculate the required tunnel floor elevation to maintain clearance
        // Tunnel ceiling must be MinimumClearanceMeters below terrain surface
        // Tunnel floor is TunnelInteriorHeightMeters below ceiling
        float requiredClearance = TunnelMinClearanceMeters + TunnelInteriorHeightMeters;

        // Check if linear interpolation between entry/exit provides enough depth at all points
        bool linearProfileSufficient = CheckLinearProfileClearance(
            entryElevation,
            exitElevation,
            terrainElevationsAlongPath,
            requiredClearance);

        if (linearProfileSufficient)
        {
            // Linear profile works - tunnel entry/exit are deep enough
            profile.CurveType = StructureElevationCurveType.Linear;
            profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
            profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);
            profile.MaxGradePercent = Math.Abs(profile.AverageGradePercent);

            TerrainLogger.Info($"Tunnel profile (Linear): {profile}");
        }
        else
        {
            // Need to go deeper - calculate S-curve profile
            profile.CurveType = StructureElevationCurveType.SCurve;

            // Calculate the required lowest point to clear all terrain samples
            float requiredLowestPoint = CalculateRequiredTunnelLowestPoint(
                terrainElevationsAlongPath,
                requiredClearance);

            profile.CalculatedLowestPointElevation = requiredLowestPoint;
            profile.CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation);

            // Validate that the required grade is achievable
            ValidateTunnelGrade(profile);

            TerrainLogger.Info($"Tunnel profile (S-Curve): {profile}");
        }

        return profile;
    }

    /// <summary>
    /// Checks if a linear profile between entry and exit provides adequate clearance
    /// at all points along the tunnel path.
    /// </summary>
    /// <param name="entryElevation">Tunnel entry elevation.</param>
    /// <param name="exitElevation">Tunnel exit elevation.</param>
    /// <param name="terrainElevations">Terrain elevations sampled along the path.</param>
    /// <param name="requiredClearance">Required vertical clearance below terrain.</param>
    /// <returns>True if linear profile is sufficient, false if S-curve is needed.</returns>
    private bool CheckLinearProfileClearance(
        float entryElevation,
        float exitElevation,
        float[] terrainElevations,
        float requiredClearance)
    {
        int sampleCount = terrainElevations.Length;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            float linearElevation = Lerp(entryElevation, exitElevation, t);
            float terrainElevation = terrainElevations[i];

            // Check if tunnel floor is far enough below terrain
            // (tunnel floor + clearance + height must be <= terrain)
            if (linearElevation + requiredClearance > terrainElevation)
            {
                // Not enough clearance at this point
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Calculates the required lowest point elevation for a tunnel
    /// to maintain adequate clearance below terrain at all points.
    /// </summary>
    /// <param name="terrainElevations">Terrain elevations along the tunnel path.</param>
    /// <param name="requiredClearance">Required vertical clearance below terrain.</param>
    /// <returns>The lowest safe tunnel floor elevation.</returns>
    private float CalculateRequiredTunnelLowestPoint(
        float[] terrainElevations,
        float requiredClearance)
    {
        // Find the minimum terrain elevation along the path
        float minTerrainElevation = terrainElevations.Min();

        // The tunnel floor must be at least requiredClearance below the lowest terrain point
        // But for S-curves, we need to consider the middle section which is below entry/exit
        // So we calculate based on the terrain along the middle 50% of the tunnel
        int sampleCount = terrainElevations.Length;
        int middleStart = (int)(sampleCount * TunnelDescentPhaseFraction);
        int middleEnd = (int)(sampleCount * (1.0f - TunnelAscentPhaseFraction));

        // Ensure we have at least one sample in the middle
        if (middleEnd <= middleStart)
        {
            middleStart = sampleCount / 4;
            middleEnd = sampleCount * 3 / 4;
        }

        // Find the maximum terrain elevation in the middle section
        // (this is what the tunnel needs to go under)
        float maxMiddleTerrainElevation = float.MinValue;
        for (int i = middleStart; i <= middleEnd && i < sampleCount; i++)
        {
            maxMiddleTerrainElevation = Math.Max(maxMiddleTerrainElevation, terrainElevations[i]);
        }

        // If no middle samples, use the overall max
        if (maxMiddleTerrainElevation == float.MinValue)
        {
            maxMiddleTerrainElevation = terrainElevations.Max();
        }

        // Required floor elevation = terrain elevation - clearance
        return maxMiddleTerrainElevation - requiredClearance;
    }

    /// <summary>
    /// Validates that the tunnel profile doesn't exceed maximum grade constraints.
    /// If the grade is too steep, marks the profile as invalid and adjusts if possible.
    /// </summary>
    /// <param name="profile">The tunnel profile to validate.</param>
    private void ValidateTunnelGrade(StructureElevationProfile profile)
    {
        // For S-curve tunnels, the steepest grades are in the descent/ascent phases
        float descentLength = profile.LengthMeters * TunnelDescentPhaseFraction;
        float ascentLength = profile.LengthMeters * TunnelAscentPhaseFraction;

        // Calculate descent grade (from entry to lowest point)
        float descentDrop = profile.EntryElevation - profile.CalculatedLowestPointElevation;
        float descentGradePercent = descentLength > 0 
            ? Math.Abs(descentDrop / descentLength * 100f) 
            : 0f;

        // Calculate ascent grade (from lowest point to exit)
        float ascentRise = profile.ExitElevation - profile.CalculatedLowestPointElevation;
        float ascentGradePercent = ascentLength > 0 
            ? Math.Abs(ascentRise / ascentLength * 100f) 
            : 0f;

        // Use the steeper of the two grades
        profile.MaxGradePercent = Math.Max(descentGradePercent, ascentGradePercent);

        // Check against maximum allowed grade
        if (profile.MaxGradePercent > TunnelMaxGradePercent)
        {
            profile.IsValid = false;
            profile.ValidationMessage = 
                $"Tunnel grade {profile.MaxGradePercent:F1}% exceeds maximum {TunnelMaxGradePercent:F1}%. " +
                $"Consider adjusting tunnel length or entry/exit elevations.";

            TerrainLogger.Warning(
                $"Tunnel profile grade warning: {profile.MaxGradePercent:F1}% exceeds max {TunnelMaxGradePercent:F1}%");
        }
    }

    // ========================================
    // TUNNEL ELEVATION AT DISTANCE
    // ========================================

    /// <summary>
    /// Calculates the elevation at a specific distance along a tunnel.
    /// </summary>
    /// <param name="distanceAlongStructure">Distance from tunnel entry in meters.</param>
    /// <param name="profile">The elevation profile for the tunnel.</param>
    /// <returns>The calculated elevation at that distance.</returns>
    public float CalculateTunnelElevation(float distanceAlongStructure, StructureElevationProfile profile)
    {
        // Normalize distance to 0-1 range
        float t = profile.LengthMeters > 0
            ? Math.Clamp(distanceAlongStructure / profile.LengthMeters, 0f, 1f)
            : 0f;

        return profile.CurveType switch
        {
            StructureElevationCurveType.Linear =>
                Lerp(profile.EntryElevation, profile.ExitElevation, t),

            StructureElevationCurveType.SCurve =>
                CalculateSCurveElevation(t, profile),

            _ => Lerp(profile.EntryElevation, profile.ExitElevation, t)
        };
    }

    /// <summary>
    /// Calculates the elevation at a normalized position (0-1) along a tunnel.
    /// </summary>
    /// <param name="normalizedPosition">Position from 0 (entry) to 1 (exit).</param>
    /// <param name="profile">The elevation profile for the tunnel.</param>
    /// <returns>The calculated elevation at that position.</returns>
    public float CalculateTunnelElevationNormalized(float normalizedPosition, StructureElevationProfile profile)
    {
        return CalculateTunnelElevation(normalizedPosition * profile.LengthMeters, profile);
    }

    /// <summary>
    /// Calculates elevation using an S-curve profile.
    /// Divides tunnel into: descent phase, level phase, ascent phase.
    /// </summary>
    /// <param name="t">Normalized position (0 to 1).</param>
    /// <param name="profile">The tunnel elevation profile.</param>
    /// <returns>The elevation at the given position.</returns>
    /// <remarks>
    /// The S-curve profile consists of three phases:
    /// <list type="bullet">
    /// <item>Descent (0 to DescentFraction): Smooth transition from entry to lowest point</item>
    /// <item>Level (DescentFraction to 1-AscentFraction): Constant elevation at lowest point</item>
    /// <item>Ascent (1-AscentFraction to 1): Smooth transition from lowest point to exit</item>
    /// </list>
    /// </remarks>
    private float CalculateSCurveElevation(float t, StructureElevationProfile profile)
    {
        float lowestPoint = profile.CalculatedLowestPointElevation;
        float levelPhaseStart = TunnelDescentPhaseFraction;
        float levelPhaseEnd = 1.0f - TunnelAscentPhaseFraction;

        if (t <= levelPhaseStart)
        {
            // Descent phase: smooth transition from entry to lowest point
            float localT = levelPhaseStart > 0 ? t / levelPhaseStart : 0f;
            float smoothT = SmoothStep(localT);
            return Lerp(profile.EntryElevation, lowestPoint, smoothT);
        }
        else if (t <= levelPhaseEnd)
        {
            // Level phase: constant elevation at lowest point
            return lowestPoint;
        }
        else
        {
            // Ascent phase: smooth transition from lowest point to exit
            float remainingFraction = 1.0f - levelPhaseEnd;
            float localT = remainingFraction > 0 ? (t - levelPhaseEnd) / remainingFraction : 1f;
            float smoothT = SmoothStep(localT);
            return Lerp(lowestPoint, profile.ExitElevation, smoothT);
        }
    }

    // ========================================
    // TUNNEL ELEVATION SAMPLING
    // ========================================

    /// <summary>
    /// Samples elevations along a tunnel at specified distances.
    /// Useful for generating cross-sections with correct target elevations.
    /// </summary>
    /// <param name="profile">The tunnel elevation profile.</param>
    /// <param name="distances">Distances along the tunnel in meters.</param>
    /// <returns>Array of elevations corresponding to each distance.</returns>
    public float[] SampleTunnelElevations(StructureElevationProfile profile, float[] distances)
    {
        var elevations = new float[distances.Length];

        for (int i = 0; i < distances.Length; i++)
        {
            elevations[i] = CalculateTunnelElevation(distances[i], profile);
        }

        return elevations;
    }

    /// <summary>
    /// Samples elevations along a tunnel at regular intervals.
    /// </summary>
    /// <param name="profile">The tunnel elevation profile.</param>
    /// <param name="sampleCount">Number of samples (including start and end).</param>
    /// <returns>Array of elevations at regular intervals.</returns>
    public float[] SampleTunnelElevationsUniform(StructureElevationProfile profile, int sampleCount)
    {
        if (sampleCount < 2)
        {
            sampleCount = 2;
        }

        var elevations = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            elevations[i] = CalculateTunnelElevationNormalized(t, profile);
        }

        return elevations;
    }

    // ========================================
    // BATCH PROCESSING FOR TUNNELS
    // ========================================

    /// <summary>
    /// Calculates elevation profiles for all tunnel splines in a collection.
    /// </summary>
    /// <param name="splines">Collection of splines (tunnels and non-tunnels).</param>
    /// <param name="getEntryElevation">Function to get entry elevation for a spline.</param>
    /// <param name="getExitElevation">Function to get exit elevation for a spline.</param>
    /// <param name="getTerrainElevations">Function to get terrain elevations along the tunnel path.</param>
    /// <returns>Number of profiles calculated.</returns>
    public int CalculateTunnelProfiles(
        IEnumerable<ParameterizedRoadSpline> splines,
        Func<ParameterizedRoadSpline, float> getEntryElevation,
        Func<ParameterizedRoadSpline, float> getExitElevation,
        Func<ParameterizedRoadSpline, float[]> getTerrainElevations)
    {
        int count = 0;

        foreach (var spline in splines.Where(s => s.IsTunnel))
        {
            var entryElevation = getEntryElevation(spline);
            var exitElevation = getExitElevation(spline);
            var terrainElevations = getTerrainElevations(spline);

            spline.ElevationProfile = CalculateTunnelProfile(
                spline,
                entryElevation,
                exitElevation,
                terrainElevations);

            count++;
        }

        if (count > 0)
        {
            TerrainLogger.Info($"Calculated elevation profiles for {count} tunnel(s)");
        }

        return count;
    }

    /// <summary>
    /// Calculates elevation profiles for all tunnel splines using a default elevation
    /// and no terrain data. Use this when terrain sampling is not available.
    /// </summary>
    /// <param name="splines">Collection of splines (tunnels and non-tunnels).</param>
    /// <param name="defaultElevation">Default elevation to use for entry and exit.</param>
    /// <returns>Number of profiles calculated.</returns>
    public int CalculateTunnelProfilesWithDefault(
        IEnumerable<ParameterizedRoadSpline> splines,
        float defaultElevation)
    {
        return CalculateTunnelProfiles(
            splines,
            _ => defaultElevation,
            _ => defaultElevation,
            _ => Array.Empty<float>());
    }

    // ========================================
    // UNIFIED STRUCTURE ELEVATION CALCULATION
    // ========================================

    /// <summary>
    /// Calculates elevation at a distance along any structure (bridge or tunnel).
    /// Automatically selects the appropriate calculation method based on profile curve type.
    /// </summary>
    /// <param name="distanceAlongStructure">Distance from structure entry in meters.</param>
    /// <param name="profile">The elevation profile for the structure.</param>
    /// <returns>The calculated elevation at that distance.</returns>
    public float CalculateStructureElevation(float distanceAlongStructure, StructureElevationProfile profile)
    {
        return profile.CurveType switch
        {
            StructureElevationCurveType.SCurve => CalculateTunnelElevation(distanceAlongStructure, profile),
            _ => CalculateBridgeElevation(distanceAlongStructure, profile)
        };
    }

    /// <summary>
    /// Samples elevations along any structure at regular intervals.
    /// Automatically selects the appropriate calculation method based on profile curve type.
    /// </summary>
    /// <param name="profile">The structure elevation profile.</param>
    /// <param name="sampleCount">Number of samples (including start and end).</param>
    /// <returns>Array of elevations at regular intervals.</returns>
    public float[] SampleStructureElevationsUniform(StructureElevationProfile profile, int sampleCount)
    {
        return profile.CurveType switch
        {
            StructureElevationCurveType.SCurve => SampleTunnelElevationsUniform(profile, sampleCount),
            _ => SampleBridgeElevationsUniform(profile, sampleCount)
        };
    }

    // ========================================
    // TERRAIN SAMPLING ALONG STRUCTURE PATH
    // ========================================

    /// <summary>
    /// Default number of terrain samples to take along a structure path.
    /// More samples provide better accuracy for tunnel clearance calculations.
    /// </summary>
    public int DefaultTerrainSampleCount { get; set; } = 20;

    /// <summary>
    /// Samples terrain elevations along a structure spline path.
    /// These samples are used for tunnel depth calculations to ensure adequate clearance.
    /// </summary>
    /// <param name="structureSpline">The structure spline to sample along.</param>
    /// <param name="heightMap">2D heightmap array [y, x] containing terrain elevations in meters.</param>
    /// <param name="metersPerPixel">Scale factor for converting world coordinates to heightmap pixels.</param>
    /// <param name="sampleCount">Number of samples to take along the structure (default: 20).</param>
    /// <returns>Array of terrain elevations at regular intervals along the structure path.</returns>
    /// <remarks>
    /// <para>The heightmap is assumed to use the following coordinate system:</para>
    /// <list type="bullet">
    /// <item>Array indexing: heightMap[y, x] where y is row and x is column</item>
    /// <item>Origin (0,0) is at the bottom-left corner of the terrain</item>
    /// <item>X increases to the right, Y increases upward</item>
    /// </list>
    /// <para>Spline coordinates are in terrain meters, converted to pixels by dividing by metersPerPixel.</para>
    /// </remarks>
    public float[] SampleTerrainAlongStructure(
        ParameterizedRoadSpline structureSpline,
        float[,] heightMap,
        float metersPerPixel,
        int sampleCount = 0)
    {
        if (sampleCount <= 0)
        {
            sampleCount = DefaultTerrainSampleCount;
        }

        // Ensure at least 2 samples (start and end)
        sampleCount = Math.Max(2, sampleCount);

        var elevations = new float[sampleCount];
        var spline = structureSpline.Spline;

        for (int i = 0; i < sampleCount; i++)
        {
            // Calculate normalized position along spline (0 to 1)
            float t = i / (float)(sampleCount - 1);

            // Get distance along spline for this sample
            float distance = t * spline.TotalLength;

            // Sample position along spline (in terrain meters)
            var position = spline.GetPointAtDistance(distance);

            // Convert to heightmap pixel coordinates
            int pixelX = (int)(position.X / metersPerPixel);
            int pixelY = (int)(position.Y / metersPerPixel);

            // Sample terrain elevation (with bounds checking)
            elevations[i] = SampleHeightmapSafe(heightMap, pixelX, pixelY);
        }

        TerrainLogger.Info(
            $"Sampled terrain along structure: {sampleCount} points, " +
            $"min={elevations.Min():F1}m, max={elevations.Max():F1}m, " +
            $"range={elevations.Max() - elevations.Min():F1}m");

        return elevations;
    }

    /// <summary>
    /// Samples terrain elevations along a structure spline path using the spline directly.
    /// Overload for when you don't have a ParameterizedRoadSpline wrapper.
    /// </summary>
    /// <param name="spline">The road spline to sample along.</param>
    /// <param name="heightMap">2D heightmap array [y, x] containing terrain elevations in meters.</param>
    /// <param name="metersPerPixel">Scale factor for converting world coordinates to heightmap pixels.</param>
    /// <param name="sampleCount">Number of samples to take along the structure (default: 20).</param>
    /// <returns>Array of terrain elevations at regular intervals along the structure path.</returns>
    public float[] SampleTerrainAlongSpline(
        RoadSpline spline,
        float[,] heightMap,
        float metersPerPixel,
        int sampleCount = 0)
    {
        if (sampleCount <= 0)
        {
            sampleCount = DefaultTerrainSampleCount;
        }

        // Ensure at least 2 samples (start and end)
        sampleCount = Math.Max(2, sampleCount);

        var elevations = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Calculate normalized position along spline (0 to 1)
            float t = i / (float)(sampleCount - 1);

            // Get distance along spline for this sample
            float distance = t * spline.TotalLength;

            // Sample position along spline (in terrain meters)
            var position = spline.GetPointAtDistance(distance);

            // Convert to heightmap pixel coordinates
            int pixelX = (int)(position.X / metersPerPixel);
            int pixelY = (int)(position.Y / metersPerPixel);

            // Sample terrain elevation (with bounds checking)
            elevations[i] = SampleHeightmapSafe(heightMap, pixelX, pixelY);
        }

        return elevations;
    }

    /// <summary>
    /// Samples terrain elevations at specific distances along a structure path.
    /// Useful when you need samples at specific cross-section locations.
    /// </summary>
    /// <param name="structureSpline">The structure spline to sample along.</param>
    /// <param name="heightMap">2D heightmap array [y, x] containing terrain elevations in meters.</param>
    /// <param name="metersPerPixel">Scale factor for converting world coordinates to heightmap pixels.</param>
    /// <param name="distances">Array of distances along the spline in meters.</param>
    /// <returns>Array of terrain elevations corresponding to each distance.</returns>
    public float[] SampleTerrainAtDistances(
        ParameterizedRoadSpline structureSpline,
        float[,] heightMap,
        float metersPerPixel,
        float[] distances)
    {
        var elevations = new float[distances.Length];
        var spline = structureSpline.Spline;

        for (int i = 0; i < distances.Length; i++)
        {
            // Clamp distance to valid range
            float distance = Math.Clamp(distances[i], 0, spline.TotalLength);

            // Sample position along spline (in terrain meters)
            var position = spline.GetPointAtDistance(distance);

            // Convert to heightmap pixel coordinates
            int pixelX = (int)(position.X / metersPerPixel);
            int pixelY = (int)(position.Y / metersPerPixel);

            // Sample terrain elevation (with bounds checking)
            elevations[i] = SampleHeightmapSafe(heightMap, pixelX, pixelY);
        }

        return elevations;
    }

    /// <summary>
    /// Safely samples the heightmap at the given pixel coordinates with bounds checking.
    /// Returns the elevation at the nearest valid pixel if coordinates are out of bounds.
    /// </summary>
    /// <param name="heightMap">2D heightmap array [y, x].</param>
    /// <param name="pixelX">X coordinate in pixels.</param>
    /// <param name="pixelY">Y coordinate in pixels.</param>
    /// <returns>Terrain elevation at the specified location in meters.</returns>
    private static float SampleHeightmapSafe(float[,] heightMap, int pixelX, int pixelY)
    {
        // Get heightmap dimensions
        // heightMap[y, x] - first dimension is Y (rows), second is X (columns)
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);

        // Clamp coordinates to valid range
        pixelX = Math.Clamp(pixelX, 0, width - 1);
        pixelY = Math.Clamp(pixelY, 0, height - 1);

        return heightMap[pixelY, pixelX];
    }

    /// <summary>
    /// Samples the heightmap with bilinear interpolation for smoother results.
    /// Use this when you need more accurate terrain elevation between pixels.
    /// </summary>
    /// <param name="heightMap">2D heightmap array [y, x].</param>
    /// <param name="x">X coordinate in pixels (can be fractional).</param>
    /// <param name="y">Y coordinate in pixels (can be fractional).</param>
    /// <returns>Interpolated terrain elevation at the specified location in meters.</returns>
    public static float SampleHeightmapBilinear(float[,] heightMap, float x, float y)
    {
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);

        // Clamp to valid range
        x = Math.Clamp(x, 0, width - 1.001f);
        y = Math.Clamp(y, 0, height - 1.001f);

        // Get integer coordinates
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        // Calculate fractional parts
        float fx = x - x0;
        float fy = y - y0;

        // Sample four corners
        float h00 = heightMap[y0, x0];
        float h10 = heightMap[y0, x1];
        float h01 = heightMap[y1, x0];
        float h11 = heightMap[y1, x1];

        // Bilinear interpolation
        float h0 = Lerp(h00, h10, fx);
        float h1 = Lerp(h01, h11, fx);
        return Lerp(h0, h1, fy);
    }

    /// <summary>
    /// Calculates the terrain elevation at the entry point of a structure.
    /// </summary>
    /// <param name="structureSpline">The structure spline.</param>
    /// <param name="heightMap">2D heightmap array [y, x].</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <returns>Terrain elevation at the structure entry point in meters.</returns>
    public float GetTerrainElevationAtEntry(
        ParameterizedRoadSpline structureSpline,
        float[,] heightMap,
        float metersPerPixel)
    {
        var position = structureSpline.StartPoint;
        int pixelX = (int)(position.X / metersPerPixel);
        int pixelY = (int)(position.Y / metersPerPixel);
        return SampleHeightmapSafe(heightMap, pixelX, pixelY);
    }

    /// <summary>
    /// Calculates the terrain elevation at the exit point of a structure.
    /// </summary>
    /// <param name="structureSpline">The structure spline.</param>
    /// <param name="heightMap">2D heightmap array [y, x].</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <returns>Terrain elevation at the structure exit point in meters.</returns>
    public float GetTerrainElevationAtExit(
        ParameterizedRoadSpline structureSpline,
        float[,] heightMap,
        float metersPerPixel)
    {
        var position = structureSpline.EndPoint;
        int pixelX = (int)(position.X / metersPerPixel);
        int pixelY = (int)(position.Y / metersPerPixel);
        return SampleHeightmapSafe(heightMap, pixelX, pixelY);
    }

    // ========================================
    // INTEGRATED STRUCTURE PROFILE CALCULATION
    // ========================================

    /// <summary>
    /// Calculates elevation profiles for all structure splines (bridges and tunnels) in a collection.
    /// Automatically samples terrain and determines appropriate profiles.
    /// </summary>
    /// <param name="splines">Collection of splines to process.</param>
    /// <param name="heightMap">2D heightmap array [y, x] containing terrain elevations.</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <param name="getConnectingElevation">
    /// Optional function to get elevation from connecting roads.
    /// If null, terrain elevation at entry/exit points will be used.
    /// </param>
    /// <returns>Number of profiles calculated (bridges + tunnels).</returns>
    public int CalculateAllStructureProfiles(
        IEnumerable<ParameterizedRoadSpline> splines,
        float[,] heightMap,
        float metersPerPixel,
        Func<ParameterizedRoadSpline, bool, float>? getConnectingElevation = null)
    {
        int bridgeCount = 0;
        int tunnelCount = 0;

        foreach (var spline in splines.Where(s => s.IsStructure))
        {
            // Get entry/exit elevations
            float entryElevation;
            float exitElevation;

            if (getConnectingElevation != null)
            {
                // Use provided function to get elevations from connecting roads
                entryElevation = getConnectingElevation(spline, true);  // isEntry = true
                exitElevation = getConnectingElevation(spline, false);  // isEntry = false
            }
            else
            {
                // Fall back to terrain elevation at entry/exit points
                entryElevation = GetTerrainElevationAtEntry(spline, heightMap, metersPerPixel);
                exitElevation = GetTerrainElevationAtExit(spline, heightMap, metersPerPixel);
            }

            if (spline.IsBridge)
            {
                // Bridges don't need terrain sampling - they go over obstacles
                spline.ElevationProfile = CalculateBridgeProfile(
                    spline,
                    entryElevation,
                    exitElevation);
                bridgeCount++;
            }
            else if (spline.IsTunnel)
            {
                // Tunnels need terrain sampling to ensure adequate clearance
                var terrainSamples = SampleTerrainAlongStructure(
                    spline,
                    heightMap,
                    metersPerPixel,
                    DefaultTerrainSampleCount);

                spline.ElevationProfile = CalculateTunnelProfile(
                    spline,
                    entryElevation,
                    exitElevation,
                    terrainSamples);
                tunnelCount++;
            }
        }

        if (bridgeCount > 0 || tunnelCount > 0)
        {
            TerrainLogger.Info(
                $"Calculated structure elevation profiles: {bridgeCount} bridge(s), {tunnelCount} tunnel(s)");
        }

        return bridgeCount + tunnelCount;
    }

    /// <summary>
    /// Calculates the elevation profile for a single structure spline with automatic terrain sampling.
    /// </summary>
    /// <param name="spline">The structure spline (must have IsBridge or IsTunnel set).</param>
    /// <param name="heightMap">2D heightmap array [y, x].</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="entryElevation">Optional override for entry elevation. If null, uses terrain elevation.</param>
    /// <param name="exitElevation">Optional override for exit elevation. If null, uses terrain elevation.</param>
    /// <returns>The calculated elevation profile, or null if spline is not a structure.</returns>
    public StructureElevationProfile? CalculateStructureProfileWithTerrainSampling(
        ParameterizedRoadSpline spline,
        float[,] heightMap,
        float metersPerPixel,
        float? entryElevation = null,
        float? exitElevation = null)
    {
        if (!spline.IsStructure)
        {
            return null;
        }

        // Get entry/exit elevations
        float entry = entryElevation ?? GetTerrainElevationAtEntry(spline, heightMap, metersPerPixel);
        float exit = exitElevation ?? GetTerrainElevationAtExit(spline, heightMap, metersPerPixel);

        if (spline.IsBridge)
        {
            return CalculateBridgeProfile(spline, entry, exit);
        }

        if (spline.IsTunnel)
        {
            var terrainSamples = SampleTerrainAlongStructure(
                spline,
                heightMap,
                metersPerPixel,
                DefaultTerrainSampleCount);

            return CalculateTunnelProfile(spline, entry, exit, terrainSamples);
        }

        return null;
    }
}
