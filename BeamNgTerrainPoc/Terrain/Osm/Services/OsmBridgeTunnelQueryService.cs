using System.Globalization;
using System.Text;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Service for querying bridge and tunnel data from OpenStreetMap via the Overpass API.
/// 
/// Bridge/Tunnel queries target OSM ways with these tags:
/// - bridge=yes/viaduct/cantilever/movable/etc. - way passes over obstacle
/// - tunnel=yes/building_passage/culvert - way passes through terrain
/// - covered=yes - alternative for covered passages
/// 
/// The query uses "out geom" to get full geometry (all way nodes with coordinates).
/// This avoids needing to make additional queries to resolve node positions.
/// 
/// Results are cached to disk to avoid repeated API calls for the same region.
/// </summary>
public class OsmBridgeTunnelQueryService : IOsmBridgeTunnelQueryService
{
    private readonly IOverpassApiService _overpassService;
    private readonly OsmBridgeTunnelCache _cache;

    /// <summary>
    /// Default query timeout in seconds.
    /// Bridge/tunnel queries with geometry can be larger than simple node queries.
    /// </summary>
    private const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// Highway types to include in bridge/tunnel queries.
    /// Only include road types that are relevant for terrain processing.
    /// </summary>
    private static readonly string[] HighwayTypesForStructures =
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
    /// Creates a new OsmBridgeTunnelQueryService with the default Overpass API service and cache.
    /// </summary>
    public OsmBridgeTunnelQueryService()
    {
        _overpassService = new OverpassApiService();
        _cache = new OsmBridgeTunnelCache();
    }

    /// <summary>
    /// Creates a new OsmBridgeTunnelQueryService with a specific Overpass API service.
    /// </summary>
    /// <param name="overpassService">The Overpass API service to use for queries.</param>
    public OsmBridgeTunnelQueryService(IOverpassApiService overpassService)
    {
        _overpassService = overpassService ?? throw new ArgumentNullException(nameof(overpassService));
        _cache = new OsmBridgeTunnelCache();
    }

    /// <summary>
    /// Creates a new OsmBridgeTunnelQueryService with specific services and cache.
    /// </summary>
    /// <param name="overpassService">The Overpass API service to use for queries.</param>
    /// <param name="cache">The cache to use for storing results.</param>
    public OsmBridgeTunnelQueryService(IOverpassApiService overpassService, OsmBridgeTunnelCache cache)
    {
        _overpassService = overpassService ?? throw new ArgumentNullException(nameof(overpassService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public async Task<OsmBridgeTunnelQueryResult> QueryBridgesAndTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM bridges and tunnels for bbox: {bbox}");

        // Try to get from cache first
        var cachedResult = await _cache.GetAsync(bbox);
        if (cachedResult != null)
        {
            TerrainLogger.Info($"Using cached bridge/tunnel data: {cachedResult.Structures.Count} structures " +
                              $"({cachedResult.BridgeCount} bridges, {cachedResult.TunnelCount} tunnels, " +
                              $"{cachedResult.CulvertCount} culverts)");
            return cachedResult;
        }

        // Build combined query for bridges and tunnels
        var query = BuildCombinedQuery(bbox);

        var result = new OsmBridgeTunnelQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        try
        {
            var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
            TerrainLogger.Info($"Received {json.Length:N0} bytes from Overpass API for bridge/tunnel query");

            var structures = ParseBridgeTunnelResponse(json);
            result.Structures = structures;

            TerrainLogger.Info($"Parsed {result.Structures.Count} structures " +
                              $"({result.BridgeCount} bridges, {result.TunnelCount} tunnels, " +
                              $"{result.CulvertCount} culverts)");

            // Cache the result
            await _cache.SetAsync(bbox, result);
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"Bridge/tunnel query failed: {ex.Message}");
            throw;
        }

        return result;
    }

    /// <inheritdoc />
    public OsmBridgeTunnelCache Cache => _cache;

    /// <inheritdoc />
    public void InvalidateCache(GeoBoundingBox bbox) => _cache.Invalidate(bbox);

    /// <inheritdoc />
    public void ClearCache() => _cache.ClearAll();

    /// <inheritdoc />
    public async Task<OsmBridgeTunnelQueryResult> QueryBridgesAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM bridges only for bbox: {bbox}");

        var query = BuildBridgeOnlyQuery(bbox);

        var result = new OsmBridgeTunnelQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
        var structures = ParseBridgeTunnelResponse(json);

        // Filter to bridges only (in case of any parsing issues)
        result.Structures = structures.Where(s => s.IsBridge).ToList();

        TerrainLogger.Info($"Parsed {result.BridgeCount} bridges");

        return result;
    }

    /// <inheritdoc />
    public async Task<OsmBridgeTunnelQueryResult> QueryTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM tunnels only for bbox: {bbox}");

        var query = BuildTunnelOnlyQuery(bbox);

        var result = new OsmBridgeTunnelQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var json = await _overpassService.ExecuteRawQueryAsync(query, cancellationToken);
        var structures = ParseBridgeTunnelResponse(json);

        // Filter to tunnels only
        result.Structures = structures.Where(s => s.IsTunnel || s.IsCulvert).ToList();

        TerrainLogger.Info($"Parsed {result.TunnelCount} tunnels, {result.CulvertCount} culverts");

        return result;
    }

    /// <inheritdoc />
    public async Task<OsmBridgeTunnelQueryResult> QueryByTypesAsync(
        GeoBoundingBox bbox,
        StructureType[] types,
        CancellationToken cancellationToken = default)
    {
        TerrainLogger.Info($"Querying OSM structures by type for bbox: {bbox}, types: {string.Join(", ", types)}");

        // Determine which queries we need
        var needBridges = types.Contains(StructureType.Bridge);
        var needTunnels = types.Contains(StructureType.Tunnel) ||
                          types.Contains(StructureType.BuildingPassage) ||
                          types.Contains(StructureType.Culvert);

        var result = new OsmBridgeTunnelQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        var allStructures = new List<OsmBridgeTunnel>();

        if (needBridges && needTunnels)
        {
            // Query all, then filter
            var fullResult = await QueryBridgesAndTunnelsAsync(bbox, cancellationToken);
            allStructures = fullResult.Structures.Where(s => types.Contains(s.StructureType)).ToList();
        }
        else if (needBridges)
        {
            var bridgeResult = await QueryBridgesAsync(bbox, cancellationToken);
            allStructures = bridgeResult.Structures.Where(s => types.Contains(s.StructureType)).ToList();
        }
        else if (needTunnels)
        {
            var tunnelResult = await QueryTunnelsAsync(bbox, cancellationToken);
            allStructures = tunnelResult.Structures.Where(s => types.Contains(s.StructureType)).ToList();
        }

        result.Structures = allStructures;
        TerrainLogger.Info($"Parsed {result.Structures.Count} structures matching requested types");

        return result;
    }

    /// <summary>
    /// Builds a combined Overpass query for both bridges and tunnels.
    /// Uses "out geom" to get full way geometry in a single query.
    /// </summary>
    private string BuildCombinedQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);
        var highwayRegex = BuildHighwayRegex();

        // Query bridges and tunnels on highway ways with geometry
        // "out geom" returns the coordinates of all nodes in each way
        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            (
              // Bridges on highways
              way["bridge"]["highway"~"{highwayRegex}"]{bboxStr};
              
              // Tunnels on highways  
              way["tunnel"]["highway"~"{highwayRegex}"]{bboxStr};
              
              // Covered passages (alternative tagging)
              way["covered"="yes"]["highway"~"{highwayRegex}"]{bboxStr};
            );
            out geom;
            """;
    }

    /// <summary>
    /// Builds an Overpass query for bridges only.
    /// </summary>
    private string BuildBridgeOnlyQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);
        var highwayRegex = BuildHighwayRegex();

        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            way["bridge"]["highway"~"{highwayRegex}"]{bboxStr};
            out geom;
            """;
    }

    /// <summary>
    /// Builds an Overpass query for tunnels only.
    /// </summary>
    private string BuildTunnelOnlyQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);
        var highwayRegex = BuildHighwayRegex();

        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            (
              way["tunnel"]["highway"~"{highwayRegex}"]{bboxStr};
              way["covered"="yes"]["highway"~"{highwayRegex}"]{bboxStr};
            );
            out geom;
            """;
    }

    /// <summary>
    /// Parses the Overpass JSON response into OsmBridgeTunnel objects.
    /// </summary>
    private List<OsmBridgeTunnel> ParseBridgeTunnelResponse(string json)
    {
        var structures = new List<OsmBridgeTunnel>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("elements", out var elements))
            {
                TerrainLogger.Warning("Overpass JSON response has no 'elements' property");
                return structures;
            }

            foreach (var element in elements.EnumerateArray())
            {
                var structure = ParseWayElement(element);
                if (structure != null)
                {
                    structures.Add(structure);
                }
            }
        }
        catch (JsonException ex)
        {
            TerrainLogger.Error($"Failed to parse bridge/tunnel JSON: {ex.Message}");
            throw;
        }

        return structures;
    }

    /// <summary>
    /// Parses a single way element into an OsmBridgeTunnel object.
    /// </summary>
    private OsmBridgeTunnel? ParseWayElement(JsonElement element)
    {
        // Must be a way (not a node or relation)
        if (!element.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "way")
            return null;

        if (!element.TryGetProperty("id", out var idEl))
            return null;

        var wayId = idEl.GetInt64();

        // Parse tags
        var tags = new Dictionary<string, string>();
        if (element.TryGetProperty("tags", out var tagsEl))
        {
            foreach (var tag in tagsEl.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }

        // Determine structure type from tags
        var structureType = DetermineStructureType(tags);
        if (structureType == null)
        {
            // Not a valid bridge/tunnel
            return null;
        }

        // Parse geometry from "geometry" array (provided by "out geom")
        var coordinates = new List<GeoCoordinate>();
        if (element.TryGetProperty("geometry", out var geometryEl))
        {
            foreach (var point in geometryEl.EnumerateArray())
            {
                if (point.TryGetProperty("lat", out var latEl) &&
                    point.TryGetProperty("lon", out var lonEl))
                {
                    coordinates.Add(new GeoCoordinate(lonEl.GetDouble(), latEl.GetDouble()));
                }
            }
        }

        if (coordinates.Count < 2)
        {
            TerrainLogger.Warning($"Bridge/tunnel way {wayId} has insufficient geometry ({coordinates.Count} points)");
            return null;
        }

        // Parse layer (default 0)
        int layer = 0;
        if (tags.TryGetValue("layer", out var layerStr) && int.TryParse(layerStr, out var parsedLayer))
        {
            layer = parsedLayer;
        }
        else
        {
            // Set default layer based on structure type if not explicitly tagged
            if (structureType == StructureType.Tunnel || structureType == StructureType.BuildingPassage)
            {
                layer = -1; // Default underground
            }
            else if (structureType == StructureType.Bridge)
            {
                layer = 1; // Default elevated
            }
        }

        // Get highway type
        tags.TryGetValue("highway", out var highwayType);

        // Get name (from name, bridge:name, or tunnel:name tags)
        string? name = null;
        if (tags.TryGetValue("bridge:name", out var bridgeName))
            name = bridgeName;
        else if (tags.TryGetValue("tunnel:name", out var tunnelName))
            name = tunnelName;
        else if (tags.TryGetValue("name", out var generalName))
            name = generalName;

        // Get bridge structure type if applicable
        tags.TryGetValue("bridge:structure", out var bridgeStructure);

        // Calculate approximate length from coordinates
        float lengthMeters = CalculateApproximateLength(coordinates);

        // Estimate width from highway type (fallback only)
        float widthMeters = EstimateWidthFromHighway(highwayType);

        return new OsmBridgeTunnel
        {
            WayId = wayId,
            StructureType = structureType.Value,
            Coordinates = coordinates,
            Layer = layer,
            HighwayType = highwayType,
            Name = name,
            BridgeStructure = bridgeStructure,
            Tags = tags,
            LengthMeters = lengthMeters,
            WidthMeters = widthMeters
        };
    }

    /// <summary>
    /// Determines the structure type from OSM tags.
    /// </summary>
    private static StructureType? DetermineStructureType(Dictionary<string, string> tags)
    {
        // Check bridge tag
        if (tags.TryGetValue("bridge", out var bridgeValue))
        {
            // bridge=yes, bridge=viaduct, bridge=cantilever, bridge=movable, etc.
            // All indicate a bridge structure
            if (!string.IsNullOrEmpty(bridgeValue) && bridgeValue != "no")
            {
                return StructureType.Bridge;
            }
        }

        // Check tunnel tag
        if (tags.TryGetValue("tunnel", out var tunnelValue))
        {
            if (!string.IsNullOrEmpty(tunnelValue) && tunnelValue != "no")
            {
                return tunnelValue.ToLowerInvariant() switch
                {
                    "building_passage" => StructureType.BuildingPassage,
                    "culvert" => StructureType.Culvert,
                    _ => StructureType.Tunnel
                };
            }
        }

        // Check covered tag (alternative for covered passages)
        if (tags.TryGetValue("covered", out var coveredValue))
        {
            if (coveredValue == "yes")
            {
                return StructureType.BuildingPassage;
            }
        }

        return null;
    }

    /// <summary>
    /// Calculates approximate length from geographic coordinates using Haversine formula.
    /// </summary>
    private static float CalculateApproximateLength(List<GeoCoordinate> coordinates)
    {
        if (coordinates.Count < 2)
            return 0;

        float totalLength = 0;
        for (int i = 0; i < coordinates.Count - 1; i++)
        {
            totalLength += CalculateHaversineDistance(coordinates[i], coordinates[i + 1]);
        }
        return totalLength;
    }

    /// <summary>
    /// Calculates distance between two points using Haversine formula.
    /// </summary>
    private static float CalculateHaversineDistance(GeoCoordinate p1, GeoCoordinate p2)
    {
        const double EarthRadiusMeters = 6371000;

        var lat1Rad = p1.Latitude * Math.PI / 180;
        var lat2Rad = p2.Latitude * Math.PI / 180;
        var deltaLat = (p2.Latitude - p1.Latitude) * Math.PI / 180;
        var deltaLon = (p2.Longitude - p1.Longitude) * Math.PI / 180;

        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return (float)(EarthRadiusMeters * c);
    }

    /// <summary>
    /// Estimates road width based on highway type.
    /// This is only used as a fallback if no spline match is found.
    /// In normal flow, width comes from the matched spline's RoadSmoothingParameters.
    /// </summary>
    private static float EstimateWidthFromHighway(string? highwayType)
    {
        return highwayType?.ToLowerInvariant() switch
        {
            "motorway" => 12.0f,
            "motorway_link" => 6.0f,
            "trunk" => 10.0f,
            "trunk_link" => 5.0f,
            "primary" => 8.0f,
            "primary_link" => 5.0f,
            "secondary" => 7.0f,
            "secondary_link" => 4.5f,
            "tertiary" => 6.0f,
            "tertiary_link" => 4.0f,
            "unclassified" => 5.0f,
            "residential" => 5.0f,
            "service" => 4.0f,
            "living_street" => 4.0f,
            "track" => 3.0f,
            _ => 6.0f // Default fallback
        };
    }

    /// <summary>
    /// Builds a regex pattern matching the highway types to include in queries.
    /// </summary>
    private static string BuildHighwayRegex()
    {
        return "^(" + string.Join("|", HighwayTypesForStructures) + ")$";
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
