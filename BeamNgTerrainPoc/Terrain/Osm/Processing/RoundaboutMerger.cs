using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Converts detected OsmRoundabout objects into road splines suitable for
/// terrain generation. Creates a single closed-loop spline for the roundabout
/// ring and provides tracking information for junction detection.
/// </summary>
public class RoundaboutMerger
{
    private readonly OsmGeometryProcessor _geometryProcessor;

    /// <summary>
    /// Creates a new RoundaboutMerger instance.
    /// </summary>
    /// <param name="geometryProcessor">The geometry processor for coordinate transformation.</param>
    public RoundaboutMerger(OsmGeometryProcessor geometryProcessor)
    {
        _geometryProcessor = geometryProcessor;
    }

    /// <summary>
    /// Result of processing roundabouts for a material.
    /// </summary>
    public class RoundaboutProcessingResult
    {
        /// <summary>
        /// Road splines representing roundabout rings.
        /// Each roundabout produces one closed-loop spline.
        /// </summary>
        public List<RoadSpline> RoundaboutSplines { get; set; } = [];

        /// <summary>
        /// OSM feature IDs that have been processed as roundabouts and should
        /// be excluded from normal road processing.
        /// </summary>
        public HashSet<long> ProcessedFeatureIds { get; set; } = [];

        /// <summary>
        /// Information about each roundabout for junction detection.
        /// Maps roundabout ID to its processing info.
        /// </summary>
        public List<ProcessedRoundaboutInfo> RoundaboutInfos { get; set; } = [];
        
        /// <summary>
        /// Statistics about the merge operation.
        /// </summary>
        public MergeStatistics Statistics { get; set; } = new();
    }

    /// <summary>
    /// Information about a processed roundabout for junction detection.
    /// </summary>
    public class ProcessedRoundaboutInfo
    {
        /// <summary>
        /// The original OSM roundabout ID.
        /// </summary>
        public long OriginalId { get; set; }

        /// <summary>
        /// Index of this roundabout's spline in the RoundaboutSplines list.
        /// </summary>
        public int SplineIndex { get; set; }

        /// <summary>
        /// The created RoadSpline for this roundabout.
        /// </summary>
        public RoadSpline? Spline { get; set; }

        /// <summary>
        /// Center of the roundabout in meter coordinates.
        /// </summary>
        public Vector2 CenterMeters { get; set; }

        /// <summary>
        /// Radius of the roundabout in meters.
        /// </summary>
        public float RadiusMeters { get; set; }

        /// <summary>
        /// Connection information for roads that connect to this roundabout.
        /// </summary>
        public List<ProcessedConnection> Connections { get; set; } = [];

        /// <summary>
        /// Whether this roundabout was successfully converted to a spline.
        /// </summary>
        public bool IsValid => Spline != null;
        
        /// <summary>
        /// The original OsmRoundabout object.
        /// </summary>
        public OsmRoundabout? OriginalRoundabout { get; set; }
    }

    /// <summary>
    /// Information about a connection to a roundabout in meter coordinates.
    /// </summary>
    public class ProcessedConnection
    {
        /// <summary>
        /// The OSM way ID of the connecting road.
        /// </summary>
        public long ConnectingWayId { get; set; }

        /// <summary>
        /// Connection point in meter coordinates.
        /// </summary>
        public Vector2 ConnectionPointMeters { get; set; }

        /// <summary>
        /// Angle around the roundabout center (radians, 0 = East, ?/2 = North).
        /// </summary>
        public float AngleRadians { get; set; }

        /// <summary>
        /// Distance along the roundabout ring spline to this connection point.
        /// </summary>
        public float DistanceAlongSpline { get; set; }

        /// <summary>
        /// Direction of traffic at this connection.
        /// </summary>
        public RoundaboutConnectionDirection Direction { get; set; }
    }

    /// <summary>
    /// Statistics about the merge operation.
    /// </summary>
    public class MergeStatistics
    {
        /// <summary>
        /// Number of roundabouts successfully converted to splines.
        /// </summary>
        public int RoundaboutsProcessed { get; set; }

        /// <summary>
        /// Number of roundabouts that failed to convert.
        /// </summary>
        public int RoundaboutsFailed { get; set; }

        /// <summary>
        /// Total number of OSM ways processed as roundabouts.
        /// </summary>
        public int TotalWaysProcessed { get; set; }

        /// <summary>
        /// Total number of connections detected.
        /// </summary>
        public int TotalConnectionsProcessed { get; set; }
    }

    /// <summary>
    /// Processes roundabouts from OSM data and creates appropriate splines.
    /// </summary>
    /// <param name="roundabouts">Detected roundabouts to process.</param>
    /// <param name="bbox">Geographic bounding box.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor from pixels to meters.</param>
    /// <param name="interpolationType">Spline interpolation type (always uses SmoothInterpolated for roundabouts).</param>
    /// <returns>Result containing splines and tracking information.</returns>
    public RoundaboutProcessingResult ProcessRoundabouts(
        List<OsmRoundabout> roundabouts,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated)
    {
        var result = new RoundaboutProcessingResult();

        if (roundabouts.Count == 0)
        {
            TerrainLogger.Info("RoundaboutMerger: No roundabouts to process");
            return result;
        }

        TerrainLogger.Info($"RoundaboutMerger: Processing {roundabouts.Count} roundabout(s)");

        foreach (var roundabout in roundabouts)
        {
            // Mark all roundabout ways as processed
            foreach (var wayId in roundabout.WayIds)
            {
                result.ProcessedFeatureIds.Add(wayId);
                result.Statistics.TotalWaysProcessed++;
            }

            // Convert roundabout ring to spline
            var processedInfo = CreateProcessedRoundaboutInfo(
                roundabout, bbox, terrainSize, metersPerPixel, interpolationType);

            if (processedInfo.IsValid)
            {
                processedInfo.SplineIndex = result.RoundaboutSplines.Count;
                result.RoundaboutSplines.Add(processedInfo.Spline!);
                result.RoundaboutInfos.Add(processedInfo);
                result.Statistics.RoundaboutsProcessed++;
                result.Statistics.TotalConnectionsProcessed += processedInfo.Connections.Count;

                TerrainLogger.Detail($"  Created ring spline for roundabout {roundabout.Id} " +
                    $"(radius={processedInfo.RadiusMeters:F1}m, {processedInfo.Connections.Count} connections)");
            }
            else
            {
                result.Statistics.RoundaboutsFailed++;
                TerrainLogger.Warning($"  Failed to create ring spline for roundabout {roundabout.Id}");
            }
        }

        TerrainLogger.Info($"RoundaboutMerger: Created {result.RoundaboutSplines.Count} roundabout ring spline(s)");
        TerrainLogger.Info($"  Excluded {result.ProcessedFeatureIds.Count} way(s) from normal processing");
        TerrainLogger.Info($"  Processed {result.Statistics.TotalConnectionsProcessed} connection(s)");

        return result;
    }

    /// <summary>
    /// Creates ProcessedRoundaboutInfo including the spline and connection info.
    /// </summary>
    private ProcessedRoundaboutInfo CreateProcessedRoundaboutInfo(
        OsmRoundabout roundabout,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType)
    {
        var info = new ProcessedRoundaboutInfo
        {
            OriginalId = roundabout.Id,
            OriginalRoundabout = roundabout
        };

        // Transform center to meter coordinates
        var centerPixel = _geometryProcessor.TransformToTerrainCoordinate(roundabout.Center, bbox, terrainSize);
        info.CenterMeters = new Vector2(centerPixel.X * metersPerPixel, centerPixel.Y * metersPerPixel);
        info.RadiusMeters = (float)roundabout.RadiusMeters;

        // Create the ring spline
        var ringSpline = CreateRoundaboutRingSpline(
            roundabout, bbox, terrainSize, metersPerPixel, interpolationType);

        if (ringSpline == null)
        {
            return info;
        }

        info.Spline = ringSpline;

        // Process connections
        foreach (var connection in roundabout.Connections)
        {
            var processedConnection = CreateProcessedConnection(
                connection, ringSpline, bbox, terrainSize, metersPerPixel);
            
            if (processedConnection != null)
            {
                info.Connections.Add(processedConnection);
            }
        }

        return info;
    }

    /// <summary>
    /// Creates a closed-loop spline from a roundabout's ring coordinates.
    /// Uses special handling to ensure smooth circular geometry.
    /// </summary>
    private RoadSpline? CreateRoundaboutRingSpline(
        OsmRoundabout roundabout,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType)
    {
        if (roundabout.RingCoordinates.Count < 4)
        {
            TerrainLogger.Warning($"Roundabout {roundabout.Id} has too few coordinates ({roundabout.RingCoordinates.Count})");
            return null;
        }

        // Transform to terrain-space coordinates
        var terrainCoords = _geometryProcessor.TransformToTerrainCoordinates(
            roundabout.RingCoordinates, bbox, terrainSize);

        // Crop to terrain bounds
        var croppedCoords = _geometryProcessor.CropLineToTerrain(terrainCoords, terrainSize);

        if (croppedCoords.Count < 4)
        {
            TerrainLogger.Detail($"Roundabout {roundabout.Id} outside terrain bounds after cropping");
            return null;
        }

        // Convert to meter coordinates
        var meterCoords = croppedCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();

        // Ensure ring is closed (first point ~= last point)
        const float closureTolerance = 0.1f; // 10cm
        if (Vector2.Distance(meterCoords[0], meterCoords[^1]) > closureTolerance)
        {
            // Add first point again to close the ring
            meterCoords.Add(meterCoords[0]);
        }

        // Remove duplicate consecutive points
        var uniqueCoords = RemoveDuplicateConsecutivePoints(meterCoords, 0.01f);

        if (uniqueCoords.Count < 4)
        {
            TerrainLogger.Warning($"Roundabout {roundabout.Id} has too few unique coordinates after cleanup");
            return null;
        }

        try
        {
            // For roundabouts, always use smooth interpolation for nice circular curves
            // regardless of what the caller requested
            var spline = new RoadSpline(uniqueCoords, SplineInterpolationType.SmoothInterpolated);
            return spline;
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to create roundabout ring spline for {roundabout.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a processed connection with meter coordinates and spline distance.
    /// </summary>
    private ProcessedConnection? CreateProcessedConnection(
        RoundaboutConnection connection,
        RoadSpline ringSpline,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        // Transform connection point to meter coordinates
        var pixelCoord = _geometryProcessor.TransformToTerrainCoordinate(
            connection.ConnectionPoint, bbox, terrainSize);
        var meterCoord = new Vector2(pixelCoord.X * metersPerPixel, pixelCoord.Y * metersPerPixel);

        // Find the closest point on the spline
        float closestDistance = FindClosestDistanceOnSpline(ringSpline, meterCoord);

        return new ProcessedConnection
        {
            ConnectingWayId = connection.ConnectingWayId,
            ConnectionPointMeters = meterCoord,
            AngleRadians = (float)(connection.AngleDegrees * Math.PI / 180.0),
            DistanceAlongSpline = closestDistance,
            Direction = connection.Direction
        };
    }

    /// <summary>
    /// Finds the distance along a spline that is closest to a given point.
    /// </summary>
    private static float FindClosestDistanceOnSpline(RoadSpline spline, Vector2 targetPoint)
    {
        // Sample the spline at regular intervals and find the closest point
        const float sampleInterval = 0.5f; // 0.5 meter intervals
        float closestDistance = 0;
        float minDistSq = float.MaxValue;

        for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
        {
            var point = spline.GetPointAtDistance(d);
            var distSq = Vector2.DistanceSquared(point, targetPoint);
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closestDistance = d;
            }
        }

        // Refine search around the found point
        float searchStart = Math.Max(0, closestDistance - sampleInterval);
        float searchEnd = Math.Min(spline.TotalLength, closestDistance + sampleInterval);
        const float refineSampleInterval = 0.05f;

        for (float d = searchStart; d <= searchEnd; d += refineSampleInterval)
        {
            var point = spline.GetPointAtDistance(d);
            var distSq = Vector2.DistanceSquared(point, targetPoint);
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closestDistance = d;
            }
        }

        return closestDistance;
    }

    /// <summary>
    /// Removes consecutive duplicate points from a path.
    /// </summary>
    private static List<Vector2> RemoveDuplicateConsecutivePoints(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;

        var result = new List<Vector2> { points[0] };
        var toleranceSquared = tolerance * tolerance;

        for (int i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(result[^1], points[i]) > toleranceSquared)
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets a processed roundabout info by its original OSM ID.
    /// </summary>
    /// <param name="result">The processing result to search.</param>
    /// <param name="osmId">The OSM roundabout ID.</param>
    /// <returns>The processed info, or null if not found.</returns>
    public static ProcessedRoundaboutInfo? GetRoundaboutById(RoundaboutProcessingResult result, long osmId)
    {
        return result.RoundaboutInfos.FirstOrDefault(r => r.OriginalId == osmId);
    }

    /// <summary>
    /// Gets all roundabouts that have a connection to a specific road way.
    /// </summary>
    /// <param name="result">The processing result to search.</param>
    /// <param name="wayId">The OSM way ID of the connecting road.</param>
    /// <returns>List of roundabouts that the road connects to.</returns>
    public static List<ProcessedRoundaboutInfo> GetRoundaboutsConnectedToWay(
        RoundaboutProcessingResult result, 
        long wayId)
    {
        return result.RoundaboutInfos
            .Where(r => r.Connections.Any(c => c.ConnectingWayId == wayId))
            .ToList();
    }
}
