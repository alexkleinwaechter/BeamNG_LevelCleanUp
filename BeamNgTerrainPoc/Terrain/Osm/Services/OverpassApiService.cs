using System.Diagnostics;
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
/// Uses hedged (racing) requests across multiple endpoints for speed and resilience:
/// all endpoints are queried simultaneously, and the first valid response wins.
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
    /// Default timeout for Overpass server-side query execution in seconds.
    /// This is embedded in the query as [timeout:N] and controls how long the
    /// server will spend producing results. Set high (180s) because large
    /// bounding boxes with many features need significant server processing time.
    /// </summary>
    public const int DefaultTimeoutSeconds = 180;
    
    /// <summary>
    /// Maximum number of racing rounds. Each round queries all endpoints simultaneously.
    /// If no endpoint returns a valid response in a round, the next round starts after a delay.
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
            // Server-side timeout (180s) + generous buffer for network transfer of large responses.
            // The HttpClient timeout covers the entire request/response cycle including data transfer,
            // so it must be substantially larger than the server-side query timeout to avoid killing
            // active transfers of large payloads.
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds + 120)
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
        return await ExecuteRawQueryWithHedgedRequestsAsync(query, cancellationToken);
    }

    /// <summary>
    /// Executes a query using hedged requests across all available endpoints.
    /// All endpoints are queried simultaneously in each round. The first endpoint
    /// to return a valid JSON response wins, and all other in-flight requests are cancelled.
    /// If all endpoints fail in a round, retries after exponential backoff delay.
    /// </summary>
    private async Task<string> ExecuteRawQueryWithHedgedRequestsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var totalEndpoints = AvailableEndpoints.Length;

        TerrainLogger.Info($"Executing Overpass query ({query.Length} chars) with hedged requests across {totalEndpoints} endpoints...");

        List<string>? lastRoundErrors = null;

        for (int round = 0; round < MaxRounds; round++)
        {
            // Add delay between rounds (not before the first round)
            if (round > 0)
            {
                var roundDelayMs = BaseRoundDelayMs * round; // 0ms, 2000ms, 4000ms
                TerrainLogger.Info($"Starting race round {round + 1}/{MaxRounds} after {roundDelayMs}ms delay...");
                await Task.Delay(roundDelayMs, cancellationToken);
            }

            var (result, errors) = await RaceEndpointsAsync(query, round, cancellationToken);
            if (result != null)
                return result;

            lastRoundErrors = errors;
        }

        // All rounds exhausted
        var errorSummary = lastRoundErrors != null ? string.Join("; ", lastRoundErrors) : "Unknown error";
        var finalMessage = $"Overpass API request failed after {MaxRounds} racing rounds across {totalEndpoints} endpoints. Last round errors: {errorSummary}";
        TerrainLogger.Error(finalMessage);

        throw new HttpRequestException(finalMessage);
    }

    /// <summary>
    /// Races all endpoints simultaneously for a single round.
    /// Returns the first valid JSON response, or null with collected errors if all fail.
    /// </summary>
    private async Task<(string? Result, List<string> Errors)> RaceEndpointsAsync(
        string query,
        int round,
        CancellationToken callerToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var raceToken = raceCts.Token;
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();

        // Launch all endpoint requests simultaneously
        var tasks = new List<Task<(string? Result, string Endpoint, string? Error)>>(AvailableEndpoints.Length);
        foreach (var endpoint in AvailableEndpoints)
        {
            tasks.Add(QuerySingleEndpointAsync(endpoint, query, raceToken));
        }

        // Process completions one by one — first valid response wins
        var remaining = new HashSet<Task<(string? Result, string Endpoint, string? Error)>>(tasks);
        string? winningResult = null;

        try
        {
            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining);
                remaining.Remove(completed);

                var (result, endpoint, error) = await completed;

                if (result != null)
                {
                    var endpointName = GetEndpointShortName(endpoint);
                    TerrainLogger.Info(
                        $"Race winner: {endpointName} responded in {sw.ElapsedMilliseconds}ms " +
                        $"(round {round + 1}/{MaxRounds})");
                    winningResult = result;
                    break;
                }

                errors.Add(error ?? "Unknown error");
            }
        }
        finally
        {
            // Cancel remaining in-flight requests
            await raceCts.CancelAsync();

            // Await all remaining tasks to observe exceptions and ensure cleanup
            foreach (var task in remaining)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    // Expected — we cancelled these
                }
                catch (Exception ex)
                {
                    TerrainLogger.Detail($"Cancelled request cleanup: {ex.Message}");
                }
            }
        }

        if (winningResult != null)
            return (winningResult, errors);

        TerrainLogger.Warning(
            $"All {AvailableEndpoints.Length} endpoints failed in round {round + 1}/{MaxRounds}. " +
            $"Errors: {string.Join("; ", errors)}");
        return (null, errors);
    }

    /// <summary>
    /// Queries a single Overpass endpoint. Returns the valid JSON result on success,
    /// or null with an error message on failure.
    /// </summary>
    private async Task<(string? Result, string Endpoint, string? Error)> QuerySingleEndpointAsync(
        string endpoint,
        string query,
        CancellationToken cancellationToken)
    {
        var endpointName = GetEndpointShortName(endpoint);

        try
        {
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
                    var error = $"{endpointName} returned HTML instead of JSON (server error page)";
                    TerrainLogger.Warning(error);
                    return (null, endpoint, error);
                }

                if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
                {
                    var error = $"{endpointName} returned invalid response (not JSON)";
                    TerrainLogger.Warning(error);
                    return (null, endpoint, error);
                }

                return (result, endpoint, null);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var isRetryable = IsRetryableError(response.StatusCode, errorBody);

            var errorMsg = $"{endpointName} returned {response.StatusCode}";
            if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase))
            {
                errorMsg += " - Server is busy";
            }

            TerrainLogger.Warning($"{errorMsg} (retryable: {isRetryable})");
            return (null, endpoint, errorMsg);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled or race was won — propagate cancellation
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient timeout (not cancellation — that case is caught above)
            var error = $"{endpointName} timed out";
            TerrainLogger.Warning(error);
            return (null, endpoint, error);
        }
        catch (HttpRequestException ex)
        {
            var error = $"{endpointName} failed: {ex.Message}";
            TerrainLogger.Warning(error);
            return (null, endpoint, error);
        }
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

              // Building entrance/door nodes (for correct door placement on building walls)
              // Port of OSM2World: doors are placed at nodes tagged with entrance=* or door=*
              node["entrance"]{bboxStr};
              node["door"]{bboxStr};

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
