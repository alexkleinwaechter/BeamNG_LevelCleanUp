using System.Globalization;
using System.Text;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Service for querying junction data from OpenStreetMap via the Overpass API.
/// Detects both explicitly tagged junctions (motorway exits, traffic signals, etc.)
/// and geometric intersections (nodes shared by multiple highway ways).
/// 
/// This service uses caching to avoid redundant Overpass API calls. Cached results
/// are stored on disk and reused when the same (or containing) bounding box is queried.
/// 
/// This approach is more accurate than pure geometric detection because:
/// 1. OSM junction tags are curated by mappers who understand road network topology
/// 2. Provides junction type information (motorway exits, traffic signals, etc.)
/// 3. Handles complex interchange geometry that geometric analysis might miss
/// </summary>
public class OsmJunctionQueryService : IOsmJunctionQueryService
{
    private readonly IOverpassApiService _overpassService;
    private readonly OsmJunctionCache _cache;

    /// <summary>
    /// Default query timeout in seconds.
    /// Junction queries can be complex due to way_cnt filtering.
    /// </summary>
    private const int DefaultTimeoutSeconds = 90;

    /// <summary>
    /// Highway types to consider for geometric intersection detection.
    /// Excludes minor paths like footways to avoid noise.
    /// </summary>
    private static readonly string[] HighwayTypesForIntersection =
    [
        "motorway", "motorway_link",
        "trunk", "trunk_link",
        "primary", "primary_link",
        "secondary", "secondary_link",
        "tertiary", "tertiary_link",
        "unclassified",
        "residential",
        "service",
        "living_street",
        "track"
    ];

    /// <summary>
    /// Creates a new OsmJunctionQueryService with the default Overpass API service and cache.
    /// </summary>
    public OsmJunctionQueryService()
    {
        _overpassService = new OverpassApiService();
        _cache = new OsmJunctionCache();
    }

    /// <summary>
    /// Creates a new OsmJunctionQueryService with a specific Overpass API service.
    /// </summary>
    /// <param name="overpassService">The Overpass API service to use for queries.</param>
    public OsmJunctionQueryService(IOverpassApiService overpassService)
    {
        _overpassService = overpassService ?? throw new ArgumentNullException(nameof(overpassService));
        _cache = new OsmJunctionCache();
    }

    /// <summary>
    /// Creates a new OsmJunctionQueryService with specific services and cache.
    /// </summary>
    /// <param name="overpassService">The Overpass API service to use for queries.</param>
    /// <param name="cache">The cache to use for storing results.</param>
    public OsmJunctionQueryService(IOverpassApiService overpassService, OsmJunctionCache cache)
    {
        _overpassService = overpassService ?? throw new ArgumentNullException(nameof(overpassService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public async Task<OsmJunctionQueryResult> QueryJunctionsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM junctions for bbox: {bbox}");

        // Try to get from cache first
        var cachedResult = await _cache.GetAsync(bbox);
        if (cachedResult != null)
        {
            TerrainLogger.Info($"Using cached junction data: {cachedResult.Junctions.Count} junctions " +
                              $"({cachedResult.ExplicitJunctionCount} explicit, {cachedResult.GeometricJunctionCount} geometric)");
            return cachedResult;
        }

        // Build combined query for both explicit tags and geometric intersections
        var query = BuildCombinedJunctionQuery(bbox);

        var result = new OsmJunctionQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        try
        {
            var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
            TerrainLogger.Info($"Received {json.Length:N0} bytes from Overpass API for junction query");

            var junctions = ParseJunctionResponse(json);
            result.Junctions = junctions;
            result.TotalNodesQueried = junctions.Count;

            TerrainLogger.Info($"Parsed {result.Junctions.Count} junctions " +
                              $"({result.ExplicitJunctionCount} explicit, {result.GeometricJunctionCount} geometric)");

            // Cache the result
            await _cache.SetAsync(bbox, result);
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"Junction query failed: {ex.Message}");
            throw;
        }

        return result;
    }

    /// <summary>
    /// Gets the underlying cache for advanced operations (e.g., clearing, statistics).
    /// </summary>
    public OsmJunctionCache Cache => _cache;

    /// <summary>
    /// Invalidates the cache for a specific bounding box.
    /// </summary>
    /// <param name="bbox">The bounding box to invalidate.</param>
    public void InvalidateCache(GeoBoundingBox bbox) => _cache.Invalidate(bbox);

    /// <summary>
    /// Clears all cached junction data.
    /// </summary>
    public void ClearCache() => _cache.ClearAll();

    /// <inheritdoc />
    public async Task<OsmJunctionQueryResult> QueryJunctionsByTypeAsync(
        GeoBoundingBox bbox,
        OsmJunctionType[] types,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM junctions by type for bbox: {bbox}, types: {string.Join(", ", types)}");

        // Separate explicit types from geometric types
        var explicitTypes = types.Where(IsExplicitJunctionType).ToArray();
        var geometricTypes = types.Where(t => !IsExplicitJunctionType(t)).ToArray();

        var result = new OsmJunctionQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var allJunctions = new List<OsmJunction>();

        // Query explicit junctions if requested
        if (explicitTypes.Length > 0)
        {
            var explicitQuery = BuildExplicitJunctionQuery(bbox, explicitTypes);
            var explicitJson = await _overpassService.ExecuteRawQueryAsync(explicitQuery, cancellationToken);
            var explicitJunctions = ParseJunctionResponse(explicitJson);
            allJunctions.AddRange(explicitJunctions.Where(j => types.Contains(j.Type)));
        }

        // Query geometric junctions if requested
        if (geometricTypes.Length > 0)
        {
            // Determine minimum way count based on requested types
            int minWayCount = 3; // Default for T-junctions
            if (!geometricTypes.Contains(OsmJunctionType.TJunction))
            {
                minWayCount = geometricTypes.Contains(OsmJunctionType.CrossRoads) ? 4 : 5;
            }

            var geometricQuery = BuildGeometricIntersectionQuery(bbox, minWayCount);
            var geometricJson = await _overpassService.ExecuteRawQueryAsync(geometricQuery, cancellationToken);
            var geometricJunctions = ParseJunctionResponse(geometricJson);
            allJunctions.AddRange(geometricJunctions.Where(j => types.Contains(j.Type)));
        }

        result.Junctions = allJunctions;
        result.TotalNodesQueried = allJunctions.Count;

        TerrainLogger.Info($"Parsed {result.Junctions.Count} junctions by type filter");

        return result;
    }

    /// <inheritdoc />
    public async Task<OsmJunctionQueryResult> QueryExplicitJunctionsOnlyAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying explicit OSM junctions only for bbox: {bbox}");

        var query = BuildExplicitJunctionQuery(bbox, null);

        var result = new OsmJunctionQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
        var junctions = ParseJunctionResponse(json);

        // Filter to only explicit types
        result.Junctions = junctions.Where(j => j.IsExplicitlyTagged).ToList();
        result.TotalNodesQueried = junctions.Count;

        TerrainLogger.Info($"Parsed {result.Junctions.Count} explicit junctions");

        return result;
    }

    /// <inheritdoc />
    public async Task<OsmJunctionQueryResult> QueryGeometricIntersectionsAsync(
        GeoBoundingBox bbox,
        int minimumWayCount = 3,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying geometric intersections for bbox: {bbox}, min ways: {minimumWayCount}");

        var query = BuildGeometricIntersectionQuery(bbox, minimumWayCount);

        var result = new OsmJunctionQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
        var junctions = ParseJunctionResponse(json);

        // Filter to only geometric types
        result.Junctions = junctions.Where(j => !j.IsExplicitlyTagged).ToList();
        result.TotalNodesQueried = junctions.Count;

        TerrainLogger.Info($"Parsed {result.Junctions.Count} geometric intersection junctions");

        return result;
    }

    /// <summary>
    /// Builds a combined Overpass query for both explicit junction tags and geometric intersections.
    /// This minimizes API calls by combining everything into one query.
    /// 
    /// IMPORTANT: The way_cnt filter counts how many ways reference a node. This correctly
    /// identifies where roads share a node (actual junction), but does NOT detect:
    /// - Grade-separated crossings (overpasses/underpasses) where roads don't share nodes
    /// - Road crossings where the OSM data has separate nodes for each road
    /// 
    /// For those cases, the geometric mid-spline crossing detection in NetworkJunctionDetector
    /// is still needed as a fallback.
    /// 
    /// NOTE ON way_cnt VALUES:
    /// - way_cnt:2- finds nodes shared by 2+ ways = simple crossroads (2 roads meeting at one point)
    /// - way_cnt:3- finds nodes shared by 3+ ways = T-junctions, Y-junctions
    /// - way_cnt:4- finds nodes shared by 4+ ways = four-way intersections
    /// 
    /// We use way_cnt:2- to catch ALL road intersections, including simple crossings where
    /// exactly 2 roads meet at a shared node. This is the most common case for crossroads.
    /// </summary>
    private string BuildCombinedJunctionQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);
        var highwayRegex = BuildHighwayRegex();

        // NOTE: way_cnt (not way_link!) counts how many ways from the input set reference each node.
        // Using way_cnt:2- finds ALL road intersections including simple 2-way crossings.
        // This is critical for crossroad detection where two roads cross at a shared node.
        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            (
              // Explicit junction tags
              node["highway"="motorway_junction"]{bboxStr};
              node["highway"="traffic_signals"]{bboxStr};
              node["highway"="stop"]{bboxStr};
              node["highway"="give_way"]{bboxStr};
              node["highway"="mini_roundabout"]{bboxStr};
              node["highway"="turning_circle"]{bboxStr};
              node["highway"="crossing"]{bboxStr};
              
              // Geometric intersections (nodes shared by 2+ highway ways)
              // way_cnt:2- catches simple crossroads where 2 roads share a node
              // way_cnt:3- would miss the common case of 2 roads crossing!
              way["highway"~"{highwayRegex}"]{bboxStr}->.highways;
              node(way_cnt.highways:2-){bboxStr};
            );
            out body;
            """;
    }

    /// <summary>
    /// Builds an Overpass query for explicitly tagged junctions only.
    /// </summary>
    /// <param name="bbox">The bounding box to query.</param>
    /// <param name="types">Optional filter for specific junction types. If null, queries all explicit types.</param>
    private string BuildExplicitJunctionQuery(GeoBoundingBox bbox, OsmJunctionType[]? types)
    {
        var bboxStr = FormatBBox(bbox);
        var sb = new StringBuilder();

        sb.AppendLine($"[out:json][timeout:{DefaultTimeoutSeconds}];");
        sb.AppendLine("(");

        if (types == null || types.Length == 0)
        {
            // Query all explicit junction types
            sb.AppendLine($"  node[\"highway\"=\"motorway_junction\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"traffic_signals\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"stop\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"give_way\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"mini_roundabout\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"turning_circle\"]{bboxStr};");
            sb.AppendLine($"  node[\"highway\"=\"crossing\"]{bboxStr};");
        }
        else
        {
            // Query only specified types
            foreach (var type in types)
            {
                var tag = GetHighwayTagForJunctionType(type);
                if (!string.IsNullOrEmpty(tag))
                {
                    sb.AppendLine($"  node[\"highway\"=\"{tag}\"]{bboxStr};");
                }
            }
        }

        sb.AppendLine(");");
        sb.AppendLine("out body;");

        return sb.ToString();
    }

    /// <summary>
    /// Builds an Overpass query for geometric intersections (nodes shared by multiple highway ways).
    /// </summary>
    /// <param name="bbox">The bounding box to query.</param>
    /// <param name="minimumWayCount">Minimum number of ways that must share a node.</param>
    private string BuildGeometricIntersectionQuery(GeoBoundingBox bbox, int minimumWayCount)
    {
        var bboxStr = FormatBBox(bbox);
        var highwayRegex = BuildHighwayRegex();

        // NOTE: way_cnt (not way_link!) counts how many ways from the input set reference each node
        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            way["highway"~"{highwayRegex}"]{bboxStr}->.highways;
            node(way_cnt.highways:{minimumWayCount}-){bboxStr};
            out body;
            """;
    }

    /// <summary>
    /// Parses the Overpass JSON response into OsmJunction objects.
    /// </summary>
    private List<OsmJunction> ParseJunctionResponse(string json)
    {
        var junctions = new List<OsmJunction>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("elements", out var elements))
            {
                TerrainLogger.Warning("Overpass JSON response has no 'elements' property");
                return junctions;
            }

            foreach (var element in elements.EnumerateArray())
            {
                var junction = ParseJunctionElement(element);
                if (junction != null)
                {
                    junctions.Add(junction);
                }
            }
        }
        catch (JsonException ex)
        {
            TerrainLogger.Error($"Failed to parse junction JSON: {ex.Message}");
            throw;
        }

        return junctions;
    }

    /// <summary>
    /// Parses a single JSON element into an OsmJunction object.
    /// </summary>
    private OsmJunction? ParseJunctionElement(JsonElement element)
    {
        // Must be a node
        if (!element.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "node")
            return null;

        if (!element.TryGetProperty("id", out var idEl))
            return null;

        if (!element.TryGetProperty("lat", out var latEl) || !element.TryGetProperty("lon", out var lonEl))
            return null;

        var nodeId = idEl.GetInt64();
        var latitude = latEl.GetDouble();
        var longitude = lonEl.GetDouble();

        // Parse tags
        var tags = new Dictionary<string, string>();
        if (element.TryGetProperty("tags", out var tagsEl))
        {
            foreach (var tag in tagsEl.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        // Determine junction type from tags
        var junctionType = DetermineJunctionType(tags);

        // NOTE: Overpass way_cnt filter doesn't include the count in the response.
        // We query way_cnt:2- to find nodes shared by 2+ highway ways.
        // Without the actual count in the response, we default based on what's most common:
        // - Nodes from way_cnt:2- are most commonly simple crossroads (2 roads crossing)
        // - We classify these as CrossRoads type which the CrossroadToTJunctionConverter handles
        int connectedRoadCount = 0;
        
        // If no explicit type was found, this is a geometric intersection from way_cnt
        if (junctionType == OsmJunctionType.Unknown && !tags.ContainsKey("highway"))
        {
            // This node came from the way_cnt filter, so it has at least 2 roads meeting
            // The most common case for way_cnt:2- is a simple crossroad (2 roads crossing)
            // We classify as CrossRoads which the CrossroadToTJunctionConverter will process
            junctionType = OsmJunctionType.CrossRoads;
            connectedRoadCount = 2; // Minimum from way_cnt:2-
        }

        // Extract name and reference
        string? name = tags.TryGetValue("name", out var n) ? n : null;
        string? reference = tags.TryGetValue("ref", out var r) ? r : null;

        return new OsmJunction
        {
            OsmNodeId = nodeId,
            Location = new GeoCoordinate(longitude, latitude),
            Type = junctionType,
            Name = name,
            Reference = reference,
            ConnectedRoadCount = connectedRoadCount,
            Tags = tags
        };
    }

    /// <summary>
    /// Determines the junction type from OSM tags.
    /// </summary>
    private static OsmJunctionType DetermineJunctionType(Dictionary<string, string> tags)
    {
        if (tags.TryGetValue("highway", out var highway))
        {
            return highway.ToLowerInvariant() switch
            {
                "motorway_junction" => OsmJunctionType.MotorwayJunction,
                "traffic_signals" => OsmJunctionType.TrafficSignals,
                "stop" => OsmJunctionType.Stop,
                "give_way" => OsmJunctionType.GiveWay,
                "mini_roundabout" => OsmJunctionType.MiniRoundabout,
                "turning_circle" => OsmJunctionType.TurningCircle,
                "crossing" => OsmJunctionType.Crossing,
                _ => OsmJunctionType.Unknown
            };
        }

        return OsmJunctionType.Unknown;
    }

    /// <summary>
    /// Gets the OSM highway tag value for a junction type.
    /// </summary>
    private static string? GetHighwayTagForJunctionType(OsmJunctionType type)
    {
        return type switch
        {
            OsmJunctionType.MotorwayJunction => "motorway_junction",
            OsmJunctionType.TrafficSignals => "traffic_signals",
            OsmJunctionType.Stop => "stop",
            OsmJunctionType.GiveWay => "give_way",
            OsmJunctionType.MiniRoundabout => "mini_roundabout",
            OsmJunctionType.TurningCircle => "turning_circle",
            OsmJunctionType.Crossing => "crossing",
            _ => null
        };
    }

    /// <summary>
    /// Determines if a junction type is explicitly tagged (vs. geometric detection).
    /// </summary>
    private static bool IsExplicitJunctionType(OsmJunctionType type)
    {
        return type != OsmJunctionType.TJunction &&
               type != OsmJunctionType.CrossRoads &&
               type != OsmJunctionType.ComplexJunction &&
               type != OsmJunctionType.Unknown;
    }

    /// <summary>
    /// Builds a regex pattern matching the highway types to consider for intersection detection.
    /// </summary>
    private static string BuildHighwayRegex()
    {
        return "^(" + string.Join("|", HighwayTypesForIntersection) + ")$";
    }

    /// <summary>
    /// Formats a bounding box for Overpass queries.
    /// Overpass uses (south, west, north, east) format.
    /// </summary>
    private static string FormatBBox(GeoBoundingBox bbox)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "({0:F6},{1:F6},{2:F6},{3:F6})",
            bbox.MinLatitude, bbox.MinLongitude, bbox.MaxLatitude, bbox.MaxLongitude);
    }
}
