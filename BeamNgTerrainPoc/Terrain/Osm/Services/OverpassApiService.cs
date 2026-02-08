using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Parsing;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Service for querying OpenStreetMap data via the Overpass API.
/// Uses round-robin retry across multiple endpoints for resilience.
/// </summary>
public class OverpassApiService : IOverpassApiService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OsmGeoJsonParser _parser;
    private readonly bool _ownsHttpClient;
    
    /// <summary>
    /// Available Overpass API endpoints for round-robin failover.
    /// Order matters: primary endpoints first, then fallbacks.
    /// </summary>
    public static readonly string[] AvailableEndpoints =
    [
        "https://overpass.private.coffee/api/interpreter",
        "https://overpass-api.de/api/interpreter",
        "https://overpass.osm.jp/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter"
    ];
    
    /// <summary>
    /// Default Overpass API endpoint (first in the list).
    /// </summary>
    public static string DefaultEndpoint => AvailableEndpoints[0];
    
    /// <summary>
    /// Default timeout for queries in seconds (increased for larger areas).
    /// </summary>
    public const int DefaultTimeoutSeconds = 180;
    
    /// <summary>
    /// Maximum number of complete rounds through all endpoints.
    /// Total attempts = MaxRounds * AvailableEndpoints.Length
    /// </summary>
    public const int MaxRounds = 3;
    
    /// <summary>
    /// Delay in milliseconds between rounds (not between individual endpoint attempts).
    /// Applied with exponential backoff: Round 1 = 0ms, Round 2 = 2000ms, Round 3 = 4000ms
    /// </summary>
    public const int BaseRoundDelayMs = 2000;
    
    /// <summary>
    /// The primary endpoint URL being used.
    /// </summary>
    public string Endpoint { get; }
    
    /// <summary>
    /// Creates a new OverpassApiService with the default endpoint.
    /// </summary>
    public OverpassApiService() : this(DefaultEndpoint)
    {
    }
    
    /// <summary>
    /// Creates a new OverpassApiService with a specific endpoint.
    /// </summary>
    /// <param name="endpoint">The Overpass API endpoint URL.</param>
    public OverpassApiService(string endpoint)
    {
        Endpoint = endpoint;
        _parser = new OsmGeoJsonParser();
        
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds + 60) // Extra buffer for network
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BeamNG_LevelCleanUp", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        
        _ownsHttpClient = true;
    }
    
    /// <summary>
    /// Creates a new OverpassApiService with a provided HttpClient (for DI scenarios).
    /// </summary>
    /// <param name="httpClient">Pre-configured HttpClient.</param>
    public OverpassApiService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _parser = new OsmGeoJsonParser();
        Endpoint = DefaultEndpoint;
        _ownsHttpClient = false;
    }
    
    /// <inheritdoc />
    public async Task<OsmQueryResult> QueryAllFeaturesAsync(
        GeoBoundingBox bbox, 
        CancellationToken cancellationToken = default)
    {
        var query = BuildAllFeaturesQuery(bbox);
        return await ExecuteQueryAsync(query, bbox, cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<OsmQueryResult> QueryByTagsAsync(
        GeoBoundingBox bbox, 
        Dictionary<string, string?> tagFilters, 
        CancellationToken cancellationToken = default)
    {
        var query = BuildTagFilterQuery(bbox, tagFilters);
        return await ExecuteQueryAsync(query, bbox, cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<string> ExecuteRawQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        return await ExecuteRawQueryWithRoundRobinAsync(query, cancellationToken);
    }
    
    /// <summary>
    /// Executes a query with round-robin retry across all available endpoints.
    /// Cycles through all endpoints on each failure, with no delay between servers
    /// within a round, but adds delay between complete rounds.
    /// </summary>
    private async Task<string> ExecuteRawQueryWithRoundRobinAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var totalEndpoints = AvailableEndpoints.Length;
        var totalAttempts = MaxRounds * totalEndpoints;
        
        TerrainLogger.Info($"Executing Overpass query ({query.Length} chars) with round-robin across {totalEndpoints} endpoints...");
        
        Exception? lastException = null;
        string? lastErrorMessage = null;
        
        for (int round = 0; round < MaxRounds; round++)
        {
            // Add delay between rounds (not before the first round)
            if (round > 0)
            {
                var roundDelayMs = BaseRoundDelayMs * round; // 0ms, 2000ms, 4000ms
                TerrainLogger.Info($"Starting round {round + 1}/{MaxRounds} after {roundDelayMs}ms delay...");
                await Task.Delay(roundDelayMs, cancellationToken);
            }
            
            for (int endpointIndex = 0; endpointIndex < totalEndpoints; endpointIndex++)
            {
                var endpoint = AvailableEndpoints[endpointIndex];
                var attemptNumber = round * totalEndpoints + endpointIndex + 1;
                var endpointName = GetEndpointShortName(endpoint);
                
                try
                {
                    TerrainLogger.Info($"Attempt {attemptNumber}/{totalAttempts}: Trying {endpointName}...");
                    
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("data", query)
                    });
                    
                    var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsStringAsync(cancellationToken);
                        
                        // Validate that the response is actually JSON, not an HTML error page
                        // Overpass API can return 200 OK with HTML when overloaded
                        var trimmed = result.TrimStart();
                        if (trimmed.StartsWith('<'))
                        {
                            lastErrorMessage = $"Overpass API ({endpointName}) returned HTML instead of JSON (server error page)";
                            lastException = new HttpRequestException(lastErrorMessage);
                            TerrainLogger.Warning($"{lastErrorMessage} (attempt {attemptNumber}/{totalAttempts})");
                            continue; // Try next endpoint
                        }
                        
                        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
                        {
                            lastErrorMessage = $"Overpass API ({endpointName}) returned invalid response (not JSON)";
                            lastException = new HttpRequestException(lastErrorMessage);
                            TerrainLogger.Warning($"{lastErrorMessage} (attempt {attemptNumber}/{totalAttempts})");
                            continue; // Try next endpoint
                        }
                        
                        TerrainLogger.Info($"Success on {endpointName} (attempt {attemptNumber}/{totalAttempts})");
                        return result;
                    }
                    
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var isRetryable = IsRetryableError(response.StatusCode, errorBody);
                    
                    lastErrorMessage = $"Overpass API ({endpointName}) returned {response.StatusCode}";
                    if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                        errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErrorMessage += " - Server is busy";
                    }
                    
                    TerrainLogger.Warning($"{lastErrorMessage} (attempt {attemptNumber}/{totalAttempts}, retryable: {isRetryable})");
                    
                    // For non-retryable errors, still try the next endpoint in round-robin
                    // Only skip to next endpoint, don't throw yet
                    lastException = new HttpRequestException(lastErrorMessage);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HTTP client timeout (not user cancellation)
                    lastErrorMessage = $"Request to {endpointName} timed out";
                    lastException = new HttpRequestException(lastErrorMessage);
                    TerrainLogger.Warning($"{lastErrorMessage} (attempt {attemptNumber}/{totalAttempts})");
                }
                catch (HttpRequestException ex)
                {
                    lastErrorMessage = $"Request to {endpointName} failed: {ex.Message}";
                    lastException = ex;
                    TerrainLogger.Warning($"{lastErrorMessage} (attempt {attemptNumber}/{totalAttempts})");
                }
                
                // No delay between endpoints within the same round - immediately try the next one
            }
        }
        
        // All rounds exhausted
        var finalMessage = $"Overpass API request failed after {totalAttempts} attempts across {totalEndpoints} endpoints ({MaxRounds} rounds). Last error: {lastErrorMessage}";
        TerrainLogger.Error(finalMessage);
        
        throw lastException ?? new HttpRequestException(finalMessage);
    }
    
    /// <summary>
    /// Gets a short, readable name for an endpoint URL for logging purposes.
    /// </summary>
    private static string GetEndpointShortName(string endpoint)
    {
        try
        {
            var uri = new Uri(endpoint);
            return uri.Host;
        }
        catch
        {
            return endpoint;
        }
    }
    
    /// <summary>
    /// Determines if an error is retryable based on status code and error body.
    /// </summary>
    private static bool IsRetryableError(System.Net.HttpStatusCode statusCode, string errorBody)
    {
        // Gateway errors are typically transient
        if (statusCode == System.Net.HttpStatusCode.GatewayTimeout ||
            statusCode == System.Net.HttpStatusCode.BadGateway ||
            statusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
            (int)statusCode == 429) // Too Many Requests
        {
            return true;
        }
        
        // Check error body for known transient errors
        if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        return false;
    }
    
    private async Task<OsmQueryResult> ExecuteQueryAsync(
        string query, 
        GeoBoundingBox bbox,
        CancellationToken cancellationToken)
    {
        var json = await ExecuteRawQueryAsync(query, cancellationToken);
        TerrainLogger.Info($"Received {json.Length:N0} bytes from Overpass API");
        
        var result = _parser.Parse(json, bbox);
        return result;
    }
    
    /// <summary>
    /// Builds a query to fetch relevant features in a bounding box.
    /// Uses tag filters to exclude irrelevant features (power, telecom, pipeline, etc.)
    /// which typically reduces response size by 30-60%.
    /// </summary>
    private string BuildAllFeaturesQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);

        // Only fetch feature categories relevant for terrain generation.
        // This dramatically reduces response size vs the previous unfiltered query.
        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            (
              // Roads and paths
              way["highway"]{bboxStr};
              // Land use areas
              way["landuse"]{bboxStr};
              // Natural features (water bodies, forests, etc.)
              way["natural"]{bboxStr};
              // Waterways (rivers, streams, canals)
              way["waterway"]{bboxStr};
              // Railways
              way["railway"]{bboxStr};
              // Buildings (for procedural generation)
              way["building"]{bboxStr};
              way["building:part"]{bboxStr};
              // Leisure areas (parks, gardens, sports)
              way["leisure"]{bboxStr};
              // Amenity areas (parking, schools, hospitals)
              way["amenity"]{bboxStr};
              // Bridges and tunnels (standalone tagged)
              way["bridge"]{bboxStr};
              way["tunnel"]{bboxStr};
              way["man_made"="bridge"]{bboxStr};
              // Aeroway (runways, taxiways)
              way["aeroway"]{bboxStr};
              // Barriers (walls, fences - useful for terrain)
              way["barrier"]{bboxStr};

              // Relations for multipolygons and boundaries
              relation["type"="multipolygon"]["landuse"]{bboxStr};
              relation["type"="multipolygon"]["natural"]{bboxStr};
              relation["type"="multipolygon"]["building"]{bboxStr};
              relation["type"="multipolygon"]["leisure"]{bboxStr};
              relation["type"="multipolygon"]["amenity"]{bboxStr};
              relation["type"="multipolygon"]["waterway"]{bboxStr};
              relation["type"="boundary"]["boundary"="administrative"]{bboxStr};
              relation["type"="route"]["route"="road"]{bboxStr};

              // Commented out: categories excluded from terrain generation
              // Uncomment if needed for future features:
              // way["power"]{bboxStr};
              // way["telecom"]{bboxStr};
              // way["pipeline"]{bboxStr};
              // way["geological"]{bboxStr};
              // way["historic"]{bboxStr};
              // way["tourism"]{bboxStr};
              // way["shop"]{bboxStr};
              // way["office"]{bboxStr};
              // way["craft"]{bboxStr};
              // way["healthcare"]{bboxStr};
              // way["advertising"]{bboxStr};
              // relation["type"="multipolygon"]["tourism"]{bboxStr};
            );
            out geom;
            """;
    }
    
    /// <summary>
    /// Builds a query with specific tag filters.
    /// </summary>
    private string BuildTagFilterQuery(GeoBoundingBox bbox, Dictionary<string, string?> tagFilters)
    {
        var bboxStr = FormatBBox(bbox);
        var sb = new StringBuilder();
        
        sb.AppendLine($"[out:json][timeout:{DefaultTimeoutSeconds}];");
        sb.AppendLine("(");
        
        foreach (var (key, value) in tagFilters)
        {
            var tagFilter = value != null ? $"[\"{key}\"=\"{value}\"]" : $"[\"{key}\"]";
            
            // Query ways and relations with this tag (nodes don't have geometry for terrain materials)
            sb.AppendLine($"  way{tagFilter}{bboxStr};");
            sb.AppendLine($"  relation{tagFilter}{bboxStr};");
        }
        
        sb.AppendLine(");");
        sb.AppendLine("out geom;");
        
        return sb.ToString();
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
    
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
