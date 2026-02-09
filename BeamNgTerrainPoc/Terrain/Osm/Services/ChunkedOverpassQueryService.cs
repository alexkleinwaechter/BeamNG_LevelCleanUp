using System.Diagnostics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Wraps <see cref="OverpassApiService"/> to split large bounding boxes into smaller chunks,
/// query them in parallel (with rate limiting), and merge/deduplicate the results.
/// Each chunk is individually cached via <see cref="OsmQueryCache"/>.
///
/// Terrain sizes are always powers of two (512, 1024, 2048, 4096, 8192, 16384m).
/// Areas up to 4096m are queried as a single request. Larger areas are split into
/// ~4096m chunks (e.g., 8192m → 2x2, 16384m → 4x4).
/// </summary>
public class ChunkedOverpassQueryService : IDisposable
{
    /// <summary>
    /// Maximum number of concurrent Overpass API requests.
    /// Public servers typically allow 2 concurrent requests.
    /// </summary>
    private const int MaxConcurrency = 2;

    /// <summary>
    /// Delay between starting successive requests to avoid rate limiting.
    /// </summary>
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Chunk size in meters. Matches the largest terrain size that works well
    /// as a single Overpass request. Larger terrains get split into chunks of this size.
    /// </summary>
    private const double ChunkSizeMeters = 4096.0;

    /// <summary>
    /// Minimum bounding box dimension (in meters) to trigger chunking.
    /// Areas up to 4096m (one terrain tile) go as a single request.
    /// </summary>
    private const double MinChunkingThresholdMeters = 4096.0;

    private readonly OverpassApiService _apiService;
    private readonly OsmQueryCache _cache;
    private readonly bool _ownsApiService;

    /// <summary>
    /// Creates a new chunked query service using the shared cache and a new API service.
    /// </summary>
    public ChunkedOverpassQueryService()
        : this(new OverpassApiService(), OsmQueryCache.Shared, ownsApiService: true)
    {
    }

    /// <summary>
    /// Creates a new chunked query service with explicit dependencies.
    /// </summary>
    public ChunkedOverpassQueryService(OverpassApiService apiService, OsmQueryCache cache, bool ownsApiService = false)
    {
        _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _ownsApiService = ownsApiService;
    }

    /// <summary>
    /// Queries all features in a bounding box, using chunked parallel queries for large areas.
    /// Areas up to 4096m are queried directly. Larger areas are split into ~4096m chunks.
    /// Each chunk is cached individually for maximum reuse.
    /// </summary>
    /// <param name="bbox">The bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Merged, deduplicated query result.</returns>
    public async Task<OsmQueryResult> QueryAllFeaturesChunkedAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default)
    {
        // Check full-bbox cache first (from a previous run that was cached as merged)
        var cached = await _cache.GetAsync(bbox);
        if (cached != null)
        {
            TerrainLogger.Info($"Full bbox cache hit, {cached.Features.Count} features");
            return cached;
        }

        // Only chunk if bbox exceeds 4096m on either axis
        var shouldChunk = bbox.ApproximateWidthMeters > MinChunkingThresholdMeters ||
                          bbox.ApproximateHeightMeters > MinChunkingThresholdMeters;

        if (!shouldChunk)
        {
            // Fits in a single 4096m tile — single query, no chunking overhead
            TerrainLogger.Info(
                $"Bbox {bbox.ApproximateWidthMeters:F0}m x {bbox.ApproximateHeightMeters:F0}m fits in single request");
            return await QuerySingleChunkAsync(bbox, cancellationToken);
        }

        // Split into ~4096m chunks
        var chunks = bbox.SplitIntoChunks(ChunkSizeMeters);
        TerrainLogger.Info(
            $"Split {bbox.ApproximateWidthMeters:F0}m x {bbox.ApproximateHeightMeters:F0}m bbox " +
            $"into {chunks.Count} chunks (~{ChunkSizeMeters:F0}m each)");

        // Query chunks in parallel with rate limiting
        var chunkResults = await QueryChunksInParallelAsync(chunks, cancellationToken);

        // Merge and deduplicate
        var merged = OsmQueryResult.MergeChunks(chunkResults, bbox);
        TerrainLogger.Info(
            $"Merged {chunkResults.Count} chunks: {merged.Features.Count} unique features " +
            $"(deduped from {chunkResults.Sum(c => c.Features.Count)} total)");

        // Cache the merged result under the full bbox key
        await _cache.SetAsync(bbox, merged);

        return merged;
    }

    /// <summary>
    /// Queries chunks in parallel with concurrency limiting and rate spacing.
    /// </summary>
    private async Task<List<OsmQueryResult>> QueryChunksInParallelAsync(
        List<GeoBoundingBox> chunks,
        CancellationToken cancellationToken)
    {
        var results = new OsmQueryResult[chunks.Count];
        var semaphore = new SemaphoreSlim(MaxConcurrency);
        var completedCount = 0;
        var totalChunks = chunks.Count;

        var sw = Stopwatch.StartNew();

        var tasks = chunks.Select(async (chunkBbox, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Add spacing between requests to be nice to public servers
                if (index > 0)
                    await Task.Delay(RequestSpacing, cancellationToken);

                results[index] = await QuerySingleChunkAsync(chunkBbox, cancellationToken);

                var completed = Interlocked.Increment(ref completedCount);
                TerrainLogger.Info(
                    $"Chunk {completed}/{totalChunks} complete: {results[index].Features.Count} features");
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        sw.Stop();
        TerrainLogger.Info($"All {totalChunks} chunks completed in {sw.ElapsedMilliseconds}ms");

        return results.ToList();
    }

    /// <summary>
    /// Queries a single chunk: checks cache first, then falls back to API.
    /// </summary>
    private async Task<OsmQueryResult> QuerySingleChunkAsync(
        GeoBoundingBox chunkBbox,
        CancellationToken cancellationToken)
    {
        // Check chunk-level cache
        var cached = await _cache.GetAsync(chunkBbox);
        if (cached != null)
        {
            TerrainLogger.Detail($"Chunk cache hit: {cached.Features.Count} features");
            return cached;
        }

        // Query Overpass API
        var result = await _apiService.QueryAllFeaturesAsync(chunkBbox, cancellationToken);

        // Cache the chunk result
        await _cache.SetAsync(chunkBbox, result);

        return result;
    }

    public void Dispose()
    {
        if (_ownsApiService)
            _apiService.Dispose();
    }
}
