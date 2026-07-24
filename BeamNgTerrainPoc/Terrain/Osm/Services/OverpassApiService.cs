using System.Collections.Concurrent;
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
///     Service for querying OpenStreetMap data via the Overpass API.
///     Uses staggered hedged requests: the first endpoint is queried immediately, and one
///     more endpoint joins the race per stagger interval (or immediately when an earlier
///     one fails). The first valid response wins and the rest are cancelled. Compared to
///     firing all mirrors at once, this keeps the per-IP load low — public Overpass
///     instances charge the declared query cost against per-IP slots and answer bursts
///     of duplicate queries with 429 rate limiting.
/// </summary>
public class OverpassApiService : IOverpassApiService, IDisposable
{
    /// <summary>
    ///     Server-side query budget in seconds, embedded in the query as [timeout:N].
    ///     Deliberately small: the Overpass rate limiter charges the *declared* timeout
    ///     against our per-IP quota when admitting a query, and any query we abandon
    ///     (hedging losers, user cancellation) keeps running server-side for up to this
    ///     long while still occupying one of our rate-limit slots.
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    ///     Declared server-side memory budget ([maxsize:N], 128 MB). Like the timeout it
    ///     is part of the query cost the rate limiter charges per IP; the server default
    ///     of 512 MB makes our queries look four times more expensive than they are.
    /// </summary>
    public const long MaxSizeBytes = 134_217_728;

    /// <summary>
    ///     Client-side patience per endpoint attempt (headers + full body transfer).
    ///     MUST be comfortably larger than <see cref="DefaultTimeoutSeconds"/> plus queue
    ///     time: the fatal anti-pattern is abandoning a query the server is still
    ///     executing and re-submitting it — the abandoned copy keeps occupying a per-IP
    ///     rate-limit slot, so every retry stacks another zombie until the endpoint
    ///     answers 429 to everything. By the time this timeout fires, the server has long
    ///     finished or killed the query, so only a stalled transfer can be cut off here.
    /// </summary>
    public const int EndpointAttemptTimeoutSeconds = 120;

    /// <summary>
    ///     Stagger delay between hedged endpoint starts. The first endpoint usually
    ///     answers well within this window, in which case no other mirror is contacted
    ///     at all. A failed endpoint brings in the next one immediately.
    /// </summary>
    public const int HedgeDelayMs = 4000;

    /// <summary>Maximum full sweeps over the endpoint list before giving up.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Backoff between sweeps: 5 s after the first, 10 s after the second.</summary>
    public const int AttemptBackoffMs = 5000;

    /// <summary>
    ///     Cooldown for an endpoint that answered 429 without a Retry-After header.
    /// </summary>
    public const int DefaultRateLimitCooldownSeconds = 60;

    /// <summary>
    ///     Available Overpass API endpoints. lambert and gall are the two physical
    ///     instances behind the overpass-api.de DNS alias (each announces its own name
    ///     in /api/status; verified 2026-07-23). Address them directly, never via the
    ///     alias: the alias round-robins and can silently double-hit one instance,
    ///     which its per-IP slot limiter (2 slots) punishes. The legacy
    ///     lz4.overpass-api.de name resolves to the same machine as lambert — never
    ///     list it alongside.
    /// </summary>
    public static readonly string[] AvailableEndpoints =
    [
        "https://lambert.openstreetmap.de/api/interpreter",
        "https://gall.openstreetmap.de/api/interpreter",
        "https://overpass.private.coffee/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter"
    ];

    /// <summary>
    ///     Maximum time to wait for a rate-limit cooldown when every endpoint is benched.
    /// </summary>
    private static readonly TimeSpan MaxBenchWait = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     Endpoints cooling down after a 429, mapped to when they may be used again.
    ///     Static because rate limits are per IP and apply process-wide, not per instance.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> BenchedEndpoints = new();

    /// <summary>
    ///     Rotates the endpoint start order per request so concurrent queries (e.g.
    ///     parallel bbox chunks) begin on different mirrors instead of stacking on one.
    /// </summary>
    private static int _endpointRotation = -1;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly OsmGeoJsonParser _parser;

    /// <summary>
    ///     Creates a new OverpassApiService with the default endpoint.
    /// </summary>
    public OverpassApiService() : this(DefaultEndpoint)
    {
    }

    /// <summary>
    ///     Creates a new OverpassApiService with a specific endpoint.
    /// </summary>
    /// <param name="endpoint">The Overpass API endpoint URL.</param>
    public OverpassApiService(string endpoint)
    {
        Endpoint = endpoint;
        _parser = new OsmGeoJsonParser();

        _httpClient = new HttpClient
        {
            // Per-request deadlines are enforced via cancellation tokens
            // (EndpointAttemptTimeoutSeconds); no client-level timeout on top.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BeamNG_LevelCleanUp", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _ownsHttpClient = true;
    }

    /// <summary>
    ///     Creates a new OverpassApiService with a provided HttpClient (for DI scenarios).
    ///     The client's Timeout should be at least <see cref="EndpointAttemptTimeoutSeconds"/>
    ///     (or infinite) so it does not cut off slow-but-alive transfers.
    /// </summary>
    /// <param name="httpClient">Pre-configured HttpClient.</param>
    public OverpassApiService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _parser = new OsmGeoJsonParser();
        Endpoint = DefaultEndpoint;
        _ownsHttpClient = false;
    }

    /// <summary>
    ///     Default Overpass API endpoint (first in the list).
    /// </summary>
    public static string DefaultEndpoint => AvailableEndpoints[0];

    /// <summary>
    ///     The primary endpoint URL being used.
    /// </summary>
    public string Endpoint { get; }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
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
    ///     Executes a query using staggered hedged requests. Endpoints benched by a 429
    ///     cooldown are skipped. If a whole sweep fails, retries after a backoff delay.
    /// </summary>
    private async Task<string> ExecuteRawQueryWithHedgedRequestsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        TerrainLogger.Info(
            $"Executing Overpass query ({query.Length} chars): staggered hedging over up to " +
            $"{AvailableEndpoints.Length} endpoints ({HedgeDelayMs}ms stagger, " +
            $"{EndpointAttemptTimeoutSeconds}s per-endpoint timeout)...");

        List<string>? lastAttemptErrors = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var backoffMs = AttemptBackoffMs * attempt;
                TerrainLogger.Info($"Starting attempt {attempt + 1}/{MaxAttempts} after {backoffMs}ms backoff...");
                await Task.Delay(backoffMs, cancellationToken);
            }

            var endpoints = await GetEligibleEndpointsAsync(cancellationToken);
            var (result, errors) = await RaceEndpointsStaggeredAsync(endpoints, query, attempt, cancellationToken);
            if (result != null)
                return result;

            lastAttemptErrors = errors;
        }

        var errorSummary = lastAttemptErrors != null ? string.Join("; ", lastAttemptErrors) : "Unknown error";
        var finalMessage =
            $"Overpass API request failed after {MaxAttempts} attempts. Last attempt errors: {errorSummary}";
        TerrainLogger.Warning(finalMessage);

        throw new HttpRequestException(finalMessage);
    }

    /// <summary>
    ///     Returns the endpoints usable for one attempt: rotated so concurrent requests
    ///     start on different mirrors, minus endpoints benched by a 429 cooldown.
    ///     If every endpoint is benched, waits for the earliest cooldown to expire.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetEligibleEndpointsAsync(CancellationToken cancellationToken)
    {
        var offset = (int)((uint)Interlocked.Increment(ref _endpointRotation) % AvailableEndpoints.Length);
        var rotated = new string[AvailableEndpoints.Length];
        for (var i = 0; i < AvailableEndpoints.Length; i++)
            rotated[i] = AvailableEndpoints[(offset + i) % AvailableEndpoints.Length];

        var eligible = rotated.Where(e => !IsBenched(e)).ToList();
        if (eligible.Count > 0)
            return eligible;

        var earliestReturn = BenchedEndpoints.IsEmpty
            ? DateTimeOffset.UtcNow
            : BenchedEndpoints.Values.Min();
        var wait = earliestReturn - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            var cappedWait = wait > MaxBenchWait ? MaxBenchWait : wait;
            TerrainLogger.Info(
                $"All Overpass endpoints are rate-limited; waiting {cappedWait.TotalSeconds:F0}s " +
                "for the first cooldown to expire...");
            await Task.Delay(cappedWait, cancellationToken);
        }

        eligible = rotated.Where(e => !IsBenched(e)).ToList();
        return eligible.Count > 0 ? eligible : rotated;
    }

    /// <summary>
    ///     Runs one staggered hedge race. The first endpoint starts immediately; while no
    ///     response has arrived, one more endpoint joins per <see cref="HedgeDelayMs"/>,
    ///     and a failed endpoint is replaced immediately. The first valid JSON response
    ///     wins and remaining in-flight requests are cancelled.
    /// </summary>
    private async Task<(string? Result, List<string> Errors)> RaceEndpointsStaggeredAsync(
        IReadOnlyList<string> endpoints,
        string query,
        int attempt,
        CancellationToken callerToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var raceToken = raceCts.Token;
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        List<Task<(string? Result, string Endpoint, string? Error)>> active = [];
        var nextIndex = 0;
        string? winningResult = null;

        try
        {
            active.Add(QuerySingleEndpointAsync(endpoints[nextIndex++], query, raceToken));

            while (active.Count > 0)
            {
                Task completedTask;
                if (nextIndex < endpoints.Count)
                {
                    var hedgeDelay = Task.Delay(HedgeDelayMs, raceToken);
                    completedTask = await Task.WhenAny(active.Cast<Task>().Append(hedgeDelay));
                    if (completedTask == hedgeDelay)
                    {
                        // No response within the stagger window — add the next endpoint
                        active.Add(QuerySingleEndpointAsync(endpoints[nextIndex++], query, raceToken));
                        continue;
                    }
                }
                else
                {
                    completedTask = await Task.WhenAny(active);
                }

                var completed = (Task<(string? Result, string Endpoint, string? Error)>)completedTask;
                active.Remove(completed);

                (string? Result, string Endpoint, string? Error) endpointResult;
                try
                {
                    endpointResult = await completed;
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    throw;
                }

                var (result, endpoint, error) = endpointResult;

                if (result != null)
                {
                    TerrainLogger.Info(
                        $"Race winner: {GetEndpointShortName(endpoint)} responded in {sw.ElapsedMilliseconds}ms " +
                        $"(attempt {attempt + 1}/{MaxAttempts}, {nextIndex} endpoint(s) contacted)");
                    winningResult = result;
                    break;
                }

                errors.Add(error ?? "Unknown error");

                // A failed endpoint frees its hedge slot — bring in the next one immediately
                if (nextIndex < endpoints.Count)
                    active.Add(QuerySingleEndpointAsync(endpoints[nextIndex++], query, raceToken));
            }
        }
        finally
        {
            // Cancel remaining in-flight requests
            await raceCts.CancelAsync();

            // Await all remaining tasks to observe exceptions and ensure cleanup
            foreach (var task in active)
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

        if (winningResult != null)
            return (winningResult, errors);

        TerrainLogger.Warning(
            $"All {nextIndex} contacted endpoint(s) failed in attempt {attempt + 1}/{MaxAttempts}. " +
            $"Errors: {string.Join("; ", errors)}");
        return (null, errors);
    }

    /// <summary>
    ///     Queries a single Overpass endpoint. Returns the valid JSON result on success,
    ///     or null with an error message on failure. A 429 response benches the endpoint
    ///     for the duration of its Retry-After header (or a default cooldown).
    /// </summary>
    private async Task<(string? Result, string Endpoint, string? Error)> QuerySingleEndpointAsync(
        string endpoint,
        string query,
        CancellationToken raceToken)
    {
        var endpointName = GetEndpointShortName(endpoint);

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(raceToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(EndpointAttemptTimeoutSeconds));

        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("data", query)
            });

            var response = await _httpClient.PostAsync(endpoint, content, attemptCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync(attemptCts.Token);

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

                if (TryGetOverpassRemarkError(result, out var remark))
                {
                    var error = $"{endpointName} aborted the query server-side: {remark}";
                    TerrainLogger.Warning(error);
                    return (null, endpoint, error);
                }

                return (result, endpoint, null);
            }

            if ((int)response.StatusCode == 429)
            {
                BenchEndpoint(endpoint, GetRetryAfterDelay(response));
                return (null, endpoint, $"{endpointName} returned 429 TooManyRequests");
            }

            var errorBody = await response.Content.ReadAsStringAsync(attemptCts.Token);
            var errorMsg = $"{endpointName} returned {response.StatusCode}";
            if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase))
                errorMsg += " - Server is busy";

            TerrainLogger.Warning(errorMsg);
            return (null, endpoint, errorMsg);
        }
        catch (OperationCanceledException) when (raceToken.IsCancellationRequested)
        {
            // Race was won by another endpoint or the caller cancelled — propagate
            throw;
        }
        catch (OperationCanceledException)
        {
            // Per-endpoint attempt timeout. The server-side query is long dead by now
            // (its declared [timeout] is far smaller), so no zombie query remains behind.
            var error = $"{endpointName} gave no complete response within {EndpointAttemptTimeoutSeconds}s";
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
    ///     Marks an endpoint as unusable until its rate-limit cooldown expires.
    /// </summary>
    private static void BenchEndpoint(string endpoint, TimeSpan? retryAfter)
    {
        var cooldown = retryAfter ?? TimeSpan.FromSeconds(DefaultRateLimitCooldownSeconds);
        if (cooldown < TimeSpan.FromSeconds(5)) cooldown = TimeSpan.FromSeconds(5);
        if (cooldown > TimeSpan.FromMinutes(10)) cooldown = TimeSpan.FromMinutes(10);

        var until = DateTimeOffset.UtcNow + cooldown;
        BenchedEndpoints.AddOrUpdate(endpoint, until, (_, existing) => until > existing ? until : existing);
        TerrainLogger.Warning(
            $"{GetEndpointShortName(endpoint)} rate-limited us (429) — benched for {cooldown.TotalSeconds:F0}s");
    }

    private static bool IsBenched(string endpoint)
    {
        if (!BenchedEndpoints.TryGetValue(endpoint, out var until))
            return false;

        if (until <= DateTimeOffset.UtcNow)
        {
            BenchedEndpoints.TryRemove(endpoint, out _);
            return false;
        }

        return true;
    }

    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
            return null;

        if (retryAfter.Delta.HasValue)
            return retryAfter.Delta;

        if (retryAfter.Date.HasValue)
        {
            var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : null;
        }

        return null;
    }

    /// <summary>
    ///     Detects Overpass server-side failures reported inside an otherwise valid
    ///     200 JSON response. When a query hits its declared [timeout] or [maxsize],
    ///     Overpass emits a "remark" field ("runtime error: Query timed out ...") with a
    ///     possibly truncated element list. Accepting that silently would bake partial
    ///     OSM data into the terrain, so it is treated as an endpoint failure instead.
    ///     Only the head and tail of the payload are scanned (the remark lands in the
    ///     preamble or after the elements array), never the full multi-MB body.
    /// </summary>
    private static bool TryGetOverpassRemarkError(string json, out string remark)
    {
        remark = "";
        const int window = 4096;

        var index = FindRemark(json, 0, Math.Min(json.Length, window));
        if (index < 0 && json.Length > window)
            index = FindRemark(json, Math.Max(window, json.Length - window), json.Length);
        if (index < 0)
            return false;

        var snippet = json.Substring(index, Math.Min(300, json.Length - index));
        if (!snippet.Contains("runtime error", StringComparison.OrdinalIgnoreCase) &&
            !snippet.Contains("timed out", StringComparison.OrdinalIgnoreCase) &&
            !snippet.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            return false;

        remark = snippet.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return true;

        static int FindRemark(string s, int from, int to)
        {
            return s.IndexOf("\"remark\"", from, to - from, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Gets a short, readable name for an endpoint URL for logging purposes.
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
    ///     Builds a query to fetch relevant features in a bounding box.
    ///     Uses tag filters to exclude irrelevant features (power, telecom, pipeline, etc.)
    ///     which typically reduces response size by 30-60%.
    /// </summary>
    private string BuildAllFeaturesQuery(GeoBoundingBox bbox)
    {
        var bboxStr = FormatBBox(bbox);

        // Only fetch feature categories relevant for terrain generation.
        // [timeout]/[maxsize] are the declared query cost the Overpass rate limiter
        // charges per IP — keep them small and honest (see DefaultTimeoutSeconds).
        return $"""
                [out:json][timeout:{DefaultTimeoutSeconds}][maxsize:{MaxSizeBytes}];
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

                  // Relations for multipolygons (need member geometry for ring assembly)
                  relation["type"="multipolygon"]["landuse"]{bboxStr};
                  relation["type"="multipolygon"]["natural"]{bboxStr};
                  relation["type"="multipolygon"]["building"]{bboxStr};
                  relation["type"="multipolygon"]["leisure"]{bboxStr};
                  relation["type"="multipolygon"]["amenity"]{bboxStr};
                  relation["type"="multipolygon"]["waterway"]{bboxStr};

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
                  // No consumer in the pipeline; members span whole states with 'out geom':
                  // relation["type"="boundary"]["boundary"="administrative"]{bboxStr};
                );
                out geom;

                // Route relations drive road merging into long splines (Tier-0 assembly in
                // RouteRelationAssembler), but only their member ORDER is consumed (way ids
                // + roles) — the member ways themselves are fetched above. 'out body' emits
                // exactly that; 'out geom' would additionally dump the full unclipped
                // geometry of e.g. an entire interstate route crossing the bbox.
                relation["type"="route"]["route"="road"]{bboxStr};
                out body;
                """;
    }

    /// <summary>
    ///     Builds a query with specific tag filters.
    /// </summary>
    private string BuildTagFilterQuery(GeoBoundingBox bbox, Dictionary<string, string?> tagFilters)
    {
        var bboxStr = FormatBBox(bbox);
        var sb = new StringBuilder();

        sb.AppendLine($"[out:json][timeout:{DefaultTimeoutSeconds}][maxsize:{MaxSizeBytes}];");
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
    ///     Formats a bounding box for Overpass queries.
    ///     Overpass uses (south, west, north, east) format.
    /// </summary>
    private static string FormatBBox(GeoBoundingBox bbox)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "({0:F6},{1:F6},{2:F6},{3:F6})",
            bbox.MinLatitude, bbox.MinLongitude, bbox.MaxLatitude, bbox.MaxLongitude);
    }
}
