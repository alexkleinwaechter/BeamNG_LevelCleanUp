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
/// </summary>
public class OverpassApiService : IOverpassApiService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OsmGeoJsonParser _parser;
    private readonly bool _ownsHttpClient;
    
    /// <summary>
    /// Default Overpass API endpoint.
    /// </summary>
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    
    /// <summary>
    /// Alternative endpoint (Kumi Systems).
    /// </summary>
    public const string AlternativeEndpoint = "https://overpass.kumi.systems/api/interpreter";
    
    /// <summary>
    /// Default timeout for queries in seconds (increased for larger areas).
    /// </summary>
    public const int DefaultTimeoutSeconds = 180;
    
    /// <summary>
    /// Maximum number of retry attempts for failed requests.
    /// </summary>
    public const int MaxRetryAttempts = 3;
    
    /// <summary>
    /// Base delay for exponential backoff in milliseconds.
    /// </summary>
    public const int BaseRetryDelayMs = 2000;
    
    /// <summary>
    /// The endpoint URL being used.
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
        return await ExecuteRawQueryWithRetryAsync(query, Endpoint, cancellationToken);
    }
    
    /// <summary>
    /// Executes a query with retry logic and optional endpoint fallback.
    /// </summary>
    private async Task<string> ExecuteRawQueryWithRetryAsync(
        string query, 
        string endpoint,
        CancellationToken cancellationToken)
    {
        TerrainLogger.Info($"Executing Overpass query ({query.Length} chars)...");
        
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("data", query)
                });
                
                var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
                
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                
                // Check if it's a retryable error (timeout, too busy, gateway errors)
                var isRetryable = IsRetryableError(response.StatusCode, errorBody);
                
                if (!isRetryable || attempt == MaxRetryAttempts)
                {
                    var errorMsg = $"Overpass API returned {response.StatusCode}";
                    if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                        errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase))
                    {
                        errorMsg += " - Server is busy. Try again later or use a smaller area.";
                    }
                    TerrainLogger.Error(errorMsg);
                    throw new HttpRequestException(errorMsg);
                }
                
                // Calculate backoff delay with exponential increase
                var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                TerrainLogger.Warning($"Overpass API request failed (attempt {attempt}/{MaxRetryAttempts}). " +
                                      $"Retrying in {delayMs / 1000.0:F1}s... Reason: {response.StatusCode}");
                
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HTTP client timeout (not user cancellation)
                lastException = new HttpRequestException("Request timed out");
                
                if (attempt == MaxRetryAttempts)
                {
                    break;
                }
                
                var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                TerrainLogger.Warning($"Overpass API request timed out (attempt {attempt}/{MaxRetryAttempts}). " +
                                      $"Retrying in {delayMs / 1000.0:F1}s...");
                
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                
                if (attempt == MaxRetryAttempts)
                {
                    break;
                }
                
                var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                TerrainLogger.Warning($"Overpass API request failed (attempt {attempt}/{MaxRetryAttempts}). " +
                                      $"Retrying in {delayMs / 1000.0:F1}s... Error: {ex.Message}");
                
                await Task.Delay(delayMs, cancellationToken);
            }
        }
        
        // If we exhausted retries on default endpoint, try the alternative endpoint
        if (endpoint == DefaultEndpoint && endpoint != AlternativeEndpoint)
        {
            TerrainLogger.Info($"Trying alternative Overpass endpoint: {AlternativeEndpoint}");
            try
            {
                return await ExecuteRawQueryWithRetryAsync(query, AlternativeEndpoint, cancellationToken);
            }
            catch (Exception altEx)
            {
                TerrainLogger.Warning($"Alternative endpoint also failed: {altEx.Message}");
                // Fall through to throw the original exception
            }
        }
        
        // Log the final error before throwing
        var errorMessage = lastException?.Message ?? "Overpass API request failed after all retries";
        TerrainLogger.Error($"Overpass API request failed: {errorMessage}");
        
        throw lastException ?? new HttpRequestException(errorMessage);
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
    /// Builds a query to fetch all features in a bounding box.
    /// Uses JSON output with geometry for resolved coordinates.
    /// </summary>
    private string BuildAllFeaturesQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);
        
        // Query ways and relations in the bbox with resolved geometry
        // Using [out:json] with "out geom" includes resolved coordinates
        return $"""
            [out:json][timeout:{DefaultTimeoutSeconds}];
            (
              way{bboxStr};
              relation{bboxStr};
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
