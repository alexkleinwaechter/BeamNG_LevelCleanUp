using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Calculates elevation profiles for bridges and tunnels.
/// Bridges and tunnels have independent elevation profiles that start at entry elevation,
/// follow a smooth curve, and end at exit elevation - independent of terrain below.
/// </summary>
public class StructureElevationCalculator
{
    // ========================================
    // BRIDGE PROFILE THRESHOLDS
    // ========================================

    /// <summary>
    /// Bridges shorter than this use linear profile (no sag/arch).
    /// </summary>
    private const float ShortBridgeThresholdMeters = 50f;

    /// <summary>
    /// Bridges longer than this may use arch profile.
    /// </summary>
    private const float LongBridgeThresholdMeters = 200f;

    /// <summary>
    /// Maximum sag offset for medium bridges (percentage of length, capped).
    /// Sag provides drainage on flat bridges.
    /// </summary>
    private const float BridgeSagPercent = 0.005f; // 0.5% of length

    /// <summary>
    /// Maximum sag in meters (cap for very long bridges).
    /// </summary>
    private const float MaxBridgeSagMeters = 2.0f;

    /// <summary>
    /// Arch rise for long bridges (percentage of length, capped).
    /// </summary>
    private const float BridgeArchPercent = 0.01f; // 1% of length

    /// <summary>
    /// Maximum arch rise in meters (cap for very long bridges).
    /// </summary>
    private const float MaxBridgeArchRiseMeters = 10.0f;

    // ========================================
    // TUNNEL PROFILE PARAMETERS
    // ========================================

    /// <summary>
    /// Default tunnel interior height (floor to ceiling) in meters.
    /// </summary>
    private const float DefaultTunnelHeightMeters = 5.0f;

    /// <summary>
    /// Default minimum clearance below terrain surface to tunnel ceiling.
    /// </summary>
    private const float DefaultTunnelClearanceMeters = 5.0f;

    /// <summary>
    /// Default maximum grade percentage allowed in tunnels.
    /// </summary>
    private const float DefaultMaxTunnelGradePercent = 6.0f;

    /// <summary>
    /// Tunnels shorter than this threshold can use linear profile if clearance allows.
    /// </summary>
    private const float ShortTunnelThresholdMeters = 100f;

    /// <summary>
    /// Number of samples to take along structure path for terrain elevation.
    /// </summary>
    private const int TerrainSampleCount = 20;

    // ========================================
    // CONFIGURABLE PARAMETERS
    // ========================================

    /// <summary>
    /// Minimum clearance for tunnels below terrain surface (meters).
    /// </summary>
    public float TunnelMinClearanceMeters { get; set; } = DefaultTunnelClearanceMeters;

    /// <summary>
    /// Interior height of tunnels (floor to ceiling) in meters.
    /// </summary>
    public float TunnelInteriorHeightMeters { get; set; } = DefaultTunnelHeightMeters;

    /// <summary>
    /// Maximum grade percentage allowed for tunnel approaches.
    /// </summary>
    public float TunnelMaxGradePercent { get; set; } = DefaultMaxTunnelGradePercent;

    /// <summary>
    /// Minimum clearance for bridges above obstacles (meters).
    /// </summary>
    public float BridgeMinClearanceMeters { get; set; } = 5.0f;

    // ========================================
    // BRIDGE ELEVATION CALCULATION
    // ========================================

    /// <summary>
    /// Calculates the elevation profile for a bridge.
    /// </summary>
    /// <param name="structure">The bridge structure data.</param>
    /// <param name="entryElevation">Road elevation at bridge entry.</param>
    /// <param name="exitElevation">Road elevation at bridge exit.</param>
    /// <returns>Elevation profile for the bridge.</returns>
    public StructureElevationProfile CalculateBridgeProfile(
        OsmBridgeTunnel structure,
        float entryElevation,
        float exitElevation)
    {
        var profile = new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = structure.LengthMeters,
            MinimumClearanceMeters = BridgeMinClearanceMeters,
            IsBridge = true,
            IsTunnel = false
        };

        // Determine curve type based on bridge length and structure type
        profile.CurveType = DetermineBridgeCurveType(structure);

        // Calculate lowest and highest points based on curve type
        CalculateBridgeElevationExtremes(profile);

        // Calculate max grade
        profile.MaxGradePercent = CalculateMaxGrade(profile);

        TerrainLogger.Detail($"Bridge profile calculated: {profile}");
        return profile;
    }

    /// <summary>
    /// Determines the appropriate curve type for a bridge based on its characteristics.
    /// </summary>
    private StructureElevationCurveType DetermineBridgeCurveType(OsmBridgeTunnel structure)
    {
        // Check for special bridge types from OSM tags
        var bridgeStructure = structure.BridgeStructure?.ToLowerInvariant();
        if (bridgeStructure != null)
        {
            // Cable-stayed and suspension bridges typically have arch profiles
            if (bridgeStructure.Contains("suspension") ||
                bridgeStructure.Contains("cable") ||
                bridgeStructure.Contains("arch"))
            {
                return structure.LengthMeters > LongBridgeThresholdMeters
                    ? StructureElevationCurveType.Arch
                    : StructureElevationCurveType.Parabolic;
            }
        }

        // Length-based curve type selection
        if (structure.LengthMeters < ShortBridgeThresholdMeters)
        {
            // Short bridges: linear (flat or constant grade)
            return StructureElevationCurveType.Linear;
        }
        else if (structure.LengthMeters < LongBridgeThresholdMeters)
        {
            // Medium bridges: slight sag curve for drainage
            return StructureElevationCurveType.Parabolic;
        }
        else
        {
            // Long bridges: may have slight arch for structural reasons
            // But default to parabolic (sag) unless it's a special structure
            return StructureElevationCurveType.Parabolic;
        }
    }

    /// <summary>
    /// Calculates the lowest and highest elevation points for a bridge profile.
    /// </summary>
    private void CalculateBridgeElevationExtremes(StructureElevationProfile profile)
    {
        float baseMin = Math.Min(profile.EntryElevation, profile.ExitElevation);
        float baseMax = Math.Max(profile.EntryElevation, profile.ExitElevation);
        float midpoint = profile.AverageEndpointElevation;

        switch (profile.CurveType)
        {
            case StructureElevationCurveType.Linear:
                profile.CalculatedLowestPointElevation = baseMin;
                profile.CalculatedHighestPointElevation = baseMax;
                break;

            case StructureElevationCurveType.Parabolic:
                // Sag curve: lowest point is at center, below midpoint
                float sagOffset = CalculateSagOffset(profile.LengthMeters);
                profile.CalculatedLowestPointElevation = midpoint - sagOffset;
                profile.CalculatedHighestPointElevation = baseMax;
                break;

            case StructureElevationCurveType.Arch:
                // Arch curve: highest point is at center, above midpoint
                float archOffset = CalculateArchOffset(profile.LengthMeters);
                profile.CalculatedLowestPointElevation = baseMin;
                profile.CalculatedHighestPointElevation = midpoint + archOffset;
                break;

            default:
                profile.CalculatedLowestPointElevation = baseMin;
                profile.CalculatedHighestPointElevation = baseMax;
                break;
        }
    }

    /// <summary>
    /// Calculates the sag offset for parabolic bridge curves.
    /// </summary>
    private static float CalculateSagOffset(float lengthMeters)
    {
        // Sag = 0.5% of length, capped at 2m
        return Math.Min(lengthMeters * BridgeSagPercent, MaxBridgeSagMeters);
    }

    /// <summary>
    /// Calculates the arch offset for arch bridge curves.
    /// </summary>
    private static float CalculateArchOffset(float lengthMeters)
    {
        if (lengthMeters < LongBridgeThresholdMeters)
            return 0f;

        // Arch rise = 1% of length, capped at 10m
        return Math.Min(lengthMeters * BridgeArchPercent, MaxBridgeArchRiseMeters);
    }

    // ========================================
    // TUNNEL ELEVATION CALCULATION
    // ========================================

    /// <summary>
    /// Calculates the elevation profile for a tunnel.
    /// </summary>
    /// <param name="structure">The tunnel structure data.</param>
    /// <param name="entryElevation">Road elevation at tunnel entry.</param>
    /// <param name="exitElevation">Road elevation at tunnel exit.</param>
    /// <param name="terrainElevationsAlongPath">Terrain surface elevations sampled along the tunnel path.</param>
    /// <returns>Elevation profile for the tunnel.</returns>
    public StructureElevationProfile CalculateTunnelProfile(
        OsmBridgeTunnel structure,
        float entryElevation,
        float exitElevation,
        float[]? terrainElevationsAlongPath = null)
    {
        var profile = new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = structure.LengthMeters,
            MinimumClearanceMeters = TunnelMinClearanceMeters,
            TerrainElevationsAlongPath = terrainElevationsAlongPath,
            IsBridge = false,
            IsTunnel = true
        };

        // Determine if we need to go deeper than linear interpolation
        DetermineTunnelCurveType(profile);

        // Calculate elevation extremes
        CalculateTunnelElevationExtremes(profile);

        // Calculate and validate grade
        profile.MaxGradePercent = CalculateMaxGrade(profile);
        ValidateTunnelGrade(profile);

        TerrainLogger.Detail($"Tunnel profile calculated: {profile}");
        return profile;
    }

    /// <summary>
    /// Determines the appropriate curve type for a tunnel based on terrain clearance requirements.
    /// </summary>
    private void DetermineTunnelCurveType(StructureElevationProfile profile)
    {
        if (profile.TerrainElevationsAlongPath == null || profile.TerrainElevationsAlongPath.Length == 0)
        {
            // No terrain data - assume linear is okay
            profile.CurveType = StructureElevationCurveType.Linear;
            profile.RequiredDepthAdjustment = false;
            return;
        }

        // Find maximum terrain elevation along the tunnel path
        float maxTerrainElevation = profile.TerrainElevationsAlongPath.Max();

        // Calculate required floor elevation to maintain clearance
        // Clearance is from terrain surface to tunnel ceiling
        float requiredCeilingElevation = maxTerrainElevation - TunnelMinClearanceMeters;
        float requiredFloorElevation = requiredCeilingElevation - TunnelInteriorHeightMeters;

        // Check midpoint of linear interpolation
        float midpointLinear = profile.AverageEndpointElevation;

        if (midpointLinear <= requiredFloorElevation)
        {
            // Linear interpolation provides adequate clearance
            profile.CurveType = StructureElevationCurveType.Linear;
            profile.RequiredDepthAdjustment = false;
        }
        else
        {
            // Need to go deeper - use S-curve
            profile.CurveType = StructureElevationCurveType.SCurve;
            profile.RequiredDepthAdjustment = true;
            profile.CalculatedLowestPointElevation = requiredFloorElevation;

            TerrainLogger.Detail($"Tunnel requires depth adjustment: " +
                               $"linear midpoint={midpointLinear:F1}m, " +
                               $"required floor={requiredFloorElevation:F1}m");
        }
    }

    /// <summary>
    /// Calculates the lowest and highest elevation points for a tunnel profile.
    /// </summary>
    private void CalculateTunnelElevationExtremes(StructureElevationProfile profile)
    {
        float baseMin = Math.Min(profile.EntryElevation, profile.ExitElevation);
        float baseMax = Math.Max(profile.EntryElevation, profile.ExitElevation);

        switch (profile.CurveType)
        {
            case StructureElevationCurveType.Linear:
                profile.CalculatedLowestPointElevation = baseMin;
                profile.CalculatedHighestPointElevation = baseMax;
                break;

            case StructureElevationCurveType.SCurve:
                // Lowest point was already calculated in DetermineTunnelCurveType
                // if not set, calculate based on terrain
                if (profile.CalculatedLowestPointElevation == 0 &&
                    profile.TerrainElevationsAlongPath != null)
                {
                    float maxTerrain = profile.TerrainElevationsAlongPath.Max();
                    profile.CalculatedLowestPointElevation =
                        maxTerrain - TunnelMinClearanceMeters - TunnelInteriorHeightMeters;
                }
                profile.CalculatedHighestPointElevation = baseMax;
                break;

            default:
                profile.CalculatedLowestPointElevation = baseMin;
                profile.CalculatedHighestPointElevation = baseMax;
                break;
        }
    }

    /// <summary>
    /// Validates that the tunnel grade doesn't exceed maximum allowed.
    /// </summary>
    private void ValidateTunnelGrade(StructureElevationProfile profile)
    {
        if (profile.MaxGradePercent > TunnelMaxGradePercent)
        {
            TerrainLogger.Warning($"Tunnel grade {profile.MaxGradePercent:F1}% exceeds maximum " +
                                 $"{TunnelMaxGradePercent}%. Consider adjusting tunnel parameters.");
        }
    }

    // ========================================
    // ELEVATION INTERPOLATION
    // ========================================

    /// <summary>
    /// Calculates the elevation at a given distance along a structure.
    /// </summary>
    /// <param name="distanceAlongStructure">Distance in meters from structure start.</param>
    /// <param name="profile">The structure's elevation profile.</param>
    /// <returns>Elevation in meters at the given distance.</returns>
    public float CalculateElevationAtDistance(float distanceAlongStructure, StructureElevationProfile profile)
    {
        if (profile.LengthMeters <= 0)
            return profile.EntryElevation;

        // Normalize distance to 0-1 range
        float t = Math.Clamp(distanceAlongStructure / profile.LengthMeters, 0f, 1f);

        return profile.CurveType switch
        {
            StructureElevationCurveType.Linear => CalculateLinearElevation(t, profile),
            StructureElevationCurveType.Parabolic => CalculateParabolicElevation(t, profile),
            StructureElevationCurveType.SCurve => CalculateSCurveElevation(t, profile),
            StructureElevationCurveType.Arch => CalculateArchElevation(t, profile),
            _ => CalculateLinearElevation(t, profile)
        };
    }

    /// <summary>
    /// Linear elevation interpolation (constant grade).
    /// </summary>
    private static float CalculateLinearElevation(float t, StructureElevationProfile profile)
    {
        return Lerp(profile.EntryElevation, profile.ExitElevation, t);
    }

    /// <summary>
    /// Parabolic (sag) elevation for medium bridges.
    /// Lowest point at center for drainage.
    /// </summary>
    private static float CalculateParabolicElevation(float t, StructureElevationProfile profile)
    {
        float baseElevation = Lerp(profile.EntryElevation, profile.ExitElevation, t);
        float sagOffset = CalculateSagOffset(profile.LengthMeters);

        // Parabola: 4 * maxSag * t * (1 - t) peaks at t=0.5
        float sagAtT = 4f * sagOffset * t * (1f - t);

        return baseElevation - sagAtT;
    }

    /// <summary>
    /// Arch elevation for long bridges.
    /// Highest point at center.
    /// </summary>
    private static float CalculateArchElevation(float t, StructureElevationProfile profile)
    {
        float baseElevation = Lerp(profile.EntryElevation, profile.ExitElevation, t);
        float archOffset = CalculateArchOffset(profile.LengthMeters);

        // Parabola: 4 * maxRise * t * (1 - t) peaks at t=0.5
        float riseAtT = 4f * archOffset * t * (1f - t);

        return baseElevation + riseAtT;
    }

    /// <summary>
    /// S-curve elevation for tunnels that need to dip below terrain.
    /// Divides into: descent (25%), level (50%), ascent (25%)
    /// </summary>
    private static float CalculateSCurveElevation(float t, StructureElevationProfile profile)
    {
        float lowestPoint = profile.CalculatedLowestPointElevation;

        if (t <= 0.25f)
        {
            // Descent phase: smooth transition from entry to lowest point
            float localT = t / 0.25f; // 0 to 1 within this phase
            float smoothT = SmoothStep(localT); // Ease in/out
            return Lerp(profile.EntryElevation, lowestPoint, smoothT);
        }
        else if (t <= 0.75f)
        {
            // Level phase: constant elevation at lowest point
            return lowestPoint;
        }
        else
        {
            // Ascent phase: smooth transition from lowest point to exit
            float localT = (t - 0.75f) / 0.25f; // 0 to 1 within this phase
            float smoothT = SmoothStep(localT);
            return Lerp(lowestPoint, profile.ExitElevation, smoothT);
        }
    }

    // ========================================
    // TERRAIN SAMPLING
    // ========================================

    /// <summary>
    /// Samples terrain elevations along a structure's path.
    /// </summary>
    /// <param name="structure">The bridge/tunnel structure.</param>
    /// <param name="heightMap">Terrain heightmap (2D array of elevations).</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="sampleCount">Number of samples to take.</param>
    /// <returns>Array of terrain elevations along the structure path.</returns>
    public float[] SampleTerrainAlongStructure(
        OsmBridgeTunnel structure,
        float[,] heightMap,
        float metersPerPixel,
        int sampleCount = TerrainSampleCount)
    {
        var elevations = new float[sampleCount];

        if (structure.PositionsMeters.Count < 2)
        {
            // Not enough points - return zeros
            return elevations;
        }

        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount > 1 ? i / (float)(sampleCount - 1) : 0;

            // Interpolate position along structure
            Vector2 worldPos = InterpolateAlongPolyline(structure.PositionsMeters, t);

            // Convert to heightmap coordinates
            int pixelX = (int)(worldPos.X / metersPerPixel);
            int pixelY = (int)(worldPos.Y / metersPerPixel);

            // Sample terrain (with bounds checking)
            elevations[i] = SampleHeightmapSafe(heightMap, pixelX, pixelY);
        }

        return elevations;
    }

    /// <summary>
    /// Samples terrain elevation at structure entry point.
    /// </summary>
    public float SampleEntryElevation(
        OsmBridgeTunnel structure,
        float[,] heightMap,
        float metersPerPixel)
    {
        if (structure.PositionsMeters.Count == 0)
            return 0;

        var startPos = structure.PositionsMeters[0];
        int pixelX = (int)(startPos.X / metersPerPixel);
        int pixelY = (int)(startPos.Y / metersPerPixel);

        return SampleHeightmapSafe(heightMap, pixelX, pixelY);
    }

    /// <summary>
    /// Samples terrain elevation at structure exit point.
    /// </summary>
    public float SampleExitElevation(
        OsmBridgeTunnel structure,
        float[,] heightMap,
        float metersPerPixel)
    {
        if (structure.PositionsMeters.Count == 0)
            return 0;

        var endPos = structure.PositionsMeters[^1];
        int pixelX = (int)(endPos.X / metersPerPixel);
        int pixelY = (int)(endPos.Y / metersPerPixel);

        return SampleHeightmapSafe(heightMap, pixelX, pixelY);
    }

    // ========================================
    // HELPER METHODS
    // ========================================

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    /// <summary>
    /// Smooth step function (ease in/out).
    /// </summary>
    private static float SmoothStep(float t)
    {
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Interpolates a position along a polyline.
    /// </summary>
    private static Vector2 InterpolateAlongPolyline(List<Vector2> points, float t)
    {
        if (points.Count == 0)
            return Vector2.Zero;
        if (points.Count == 1)
            return points[0];

        // Calculate total length
        float totalLength = 0;
        for (int i = 1; i < points.Count; i++)
        {
            totalLength += Vector2.Distance(points[i - 1], points[i]);
        }

        // Find position at t
        float targetDistance = t * totalLength;
        float currentDistance = 0;

        for (int i = 1; i < points.Count; i++)
        {
            float segmentLength = Vector2.Distance(points[i - 1], points[i]);
            if (currentDistance + segmentLength >= targetDistance)
            {
                // Found the segment
                float segmentT = (targetDistance - currentDistance) / segmentLength;
                return Vector2.Lerp(points[i - 1], points[i], segmentT);
            }
            currentDistance += segmentLength;
        }

        // Return last point if we've gone past the end
        return points[^1];
    }

    /// <summary>
    /// Safely samples the heightmap with bounds checking.
    /// </summary>
    private static float SampleHeightmapSafe(float[,] heightMap, int x, int y)
    {
        int width = heightMap.GetLength(0);
        int height = heightMap.GetLength(1);

        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);

        return heightMap[x, y];
    }

    /// <summary>
    /// Calculates the maximum grade for a structure profile.
    /// </summary>
    private static float CalculateMaxGrade(StructureElevationProfile profile)
    {
        if (profile.LengthMeters <= 0)
            return 0;

        // For linear profiles, grade is constant
        if (profile.CurveType == StructureElevationCurveType.Linear)
        {
            return profile.AverageGradePercent;
        }

        // For curved profiles, calculate max grade at steepest point
        // S-curve: steepest in descent/ascent phases (25% of length each)
        if (profile.CurveType == StructureElevationCurveType.SCurve)
        {
            float descentLength = profile.LengthMeters * 0.25f;
            float descentDrop = Math.Abs(profile.EntryElevation - profile.CalculatedLowestPointElevation);
            return descentDrop / descentLength * 100f;
        }

        // For parabolic/arch, the grade varies continuously
        // Max grade is at the endpoints where the curve meets the linear interpolation
        // Approximate by checking a few points
        float maxGrade = 0;
        for (float t = 0.1f; t <= 0.9f; t += 0.1f)
        {
            float dt = 0.01f;
            float e1 = GetElevationAtT(t - dt, profile);
            float e2 = GetElevationAtT(t + dt, profile);
            float grade = Math.Abs(e2 - e1) / (2 * dt * profile.LengthMeters) * 100f;
            maxGrade = Math.Max(maxGrade, grade);
        }

        return maxGrade;
    }

    /// <summary>
    /// Gets elevation at normalized parameter t (internal helper for grade calculation).
    /// </summary>
    private static float GetElevationAtT(float t, StructureElevationProfile profile)
    {
        t = Math.Clamp(t, 0f, 1f);
        return profile.CurveType switch
        {
            StructureElevationCurveType.Linear => CalculateLinearElevation(t, profile),
            StructureElevationCurveType.Parabolic => CalculateParabolicElevation(t, profile),
            StructureElevationCurveType.SCurve => CalculateSCurveElevation(t, profile),
            StructureElevationCurveType.Arch => CalculateArchElevation(t, profile),
            _ => CalculateLinearElevation(t, profile)
        };
    }
}
