# Overpass API Performance Investigation & Optimization Plan

**Date:** 2026-02-07
**Branch:** feature/improve_overpass_perf
**Status:** Investigation complete, implementation plan ready

---

## 1. Problem Statement

The Overpass API query and cache system has significant performance issues:
1. **API queries are slow** for large bounding boxes (entire terrain areas)
2. **Cache deserialization is slow** (confirmed by TODO comment in `TerrainGenerationOrchestrator.cs:489`)
3. User perception: both fetching and loading cached data feel sluggish

---

## 2. Investigation Findings

### 2.1 Critical Bug: Memory Cache Is Never Used

**Severity: HIGH** - This alone may account for most of the perceived "slow cache" issue.

Every call site creates a **new** `OsmQueryCache()` instance:
```csharp
// TerrainGenerationOrchestrator.cs:488
var cache = new OsmQueryCache();
osmQueryResult = await cache.GetAsync(effectiveBoundingBox);

// TerrainGenerationOrchestrator.cs:885 — same pattern
// TerrainAnalysisOrchestrator.cs:346 — same pattern
```

`OsmQueryCache` has an internal `Dictionary<string, OsmQueryResult> _memoryCache` that is always **empty** on construction. The "two-tier caching" (memory + disk) is effectively single-tier (disk only). Every cache hit requires:
1. Disk file scan
2. Full JSON file read
3. Full JSON deserialization

**Fix:** Make OsmQueryCache a singleton or inject a shared instance.

### 2.2 Containing Cache Lookup Deserializes All Files

When no exact cache match exists, `GetFromContainingCacheAsync()` scans ALL `osm_v*.json` files on disk and deserializes each one to check if its bbox contains the requested bbox. For a cache folder with N files, this is **O(N) file reads + deserializations** on every miss.

File: `OsmQueryCache.cs:159-215`

### 2.3 Large JSON Files Are Slow to Deserialize

The Overpass query `way(bbox); relation(bbox); out geom;` fetches **every way and relation** in the bounding box — not just roads. For a typical 4km x 4km urban area, this can return:
- 5,000-20,000 features
- 10-50 MB of JSON
- Hundreds of thousands of coordinate pairs

`System.Text.Json` deserialization of such files is inherently slow (hundreds of ms to seconds).

### 2.4 Single Monolithic API Query

The current approach sends ONE query for the entire bounding box. For large areas:
- Overpass server must process everything at once (single-threaded on server side)
- Server may timeout (180s limit) for dense urban areas
- No opportunity for parallelism
- If it fails, the entire query must be retried

### 2.5 Query Is Too Broad

`BuildAllFeaturesQuery()` fetches ALL ways and ALL relations:
```
way(bbox); relation(bbox);
```
This includes buildings, boundaries, power lines, pipelines, etc. — features that are never used for terrain generation. Only highway, landuse, natural, waterway, railway, and a few other categories are relevant.

### 2.6 Round-Robin Is Sequential

The retry logic tries endpoints one-by-one. If the first endpoint is slow (but eventually responds), we wait the full timeout before trying faster alternatives.

---

## 3. Optimization Strategies Evaluated

### 3.1 Parallel Chunked Queries

**Concept:** Split the bounding box into smaller chunks (e.g., 500m x 500m), query each in parallel, merge results, deduplicate by OSM ID.

**Pros:**
- Faster total wall-clock time (parallel I/O)
- Each chunk query is small and fast on the Overpass server
- Individual chunk failures can be retried independently
- Better server behavior (avoids timeouts for large areas)

**Cons:**
- Duplicate features on chunk boundaries (ways/relations span multiple chunks)
- More HTTP requests total (rate limiting risk with public Overpass servers)
- Overpass public servers may block rapid parallel requests
- Merge/dedup adds CPU time

**Assessment:** **Viable but needs careful rate limiting.** Public Overpass servers have usage policies. Send max 2-3 parallel requests, not 64. Chunk size should be adaptive — smaller chunks for dense urban areas, larger for rural. Dedup by OSM element ID is straightforward.

**Recommended chunk size:** 1000m x 1000m (not 500m — too many chunks for large terrains, and features at boundaries would be heavily duplicated). For a 4km x 4km area = 16 chunks, with 2-3 parallel workers = 6-8 rounds.

### 3.2 JSON vs SQLite for Cache

#### JSON (Current)
| Aspect | Performance |
|--------|------------|
| Write | Fast (single `File.WriteAllText`) |
| Read exact | Fast (single file read + deserialize) |
| Read containing | SLOW (scan all files, deserialize each) |
| Spatial queries | Not supported |
| File size | Large (uncompressed text) |
| Deserialization | SLOW for large datasets (10-50MB) |

#### SQLite with R-Tree Index
| Aspect | Performance |
|--------|------------|
| Write | Moderate (insert rows, batch possible) |
| Read exact | Fast (indexed bbox lookup) |
| Read containing | FAST (R-Tree spatial query) |
| Spatial queries | Native R-Tree support |
| File size | Smaller (binary, normalized) |
| Deserialization | Fast (read only needed rows) |

#### SQLite + GeoJSON Blob Hybrid
| Aspect | Performance |
|--------|------------|
| Write | Fast (store pre-serialized blob) |
| Read exact | Fast (indexed lookup, single blob read) |
| Read containing | Fast (R-Tree on bbox, single blob read) |
| Spatial queries | R-Tree on bboxes only |
| File size | Moderate |
| Deserialization | Same as JSON per-entry |

**Assessment:** Full SQLite normalization (one row per feature, one table per coordinate) adds schema complexity and insertion overhead. **Hybrid approach recommended:** Store each query result as a JSON blob in SQLite, with an R-Tree index on the bounding box. This gives us fast spatial lookups without changing the data model.

### 3.3 Binary Serialization (MessagePack / Protobuf)

**Concept:** Replace JSON serialization with a binary format.

**Pros:**
- 2-5x faster serialization/deserialization
- 30-60% smaller file sizes
- Zero-copy deserialization options (MemoryPack)

**Cons:**
- Adds NuGet dependency
- Cache files not human-readable (harder to debug)
- Schema evolution more rigid than JSON

**Assessment:** **Good complement to other optimizations, but not the primary fix.** The biggest gains come from fixing the singleton bug and avoiding unnecessary deserialization (SQLite R-Tree), not from faster deserialization of the same large files.

### 3.4 Compressed JSON (GZip)

**Concept:** GZip-compress JSON cache files.

**Pros:**
- 5-10x smaller files (OSM JSON compresses extremely well)
- Faster disk I/O (less bytes to read)
- Trivial to implement (`GZipStream`)

**Cons:**
- Adds CPU for decompression (but CPU is faster than disk I/O)
- Not human-readable on disk

**Assessment:** **Quick win.** Can reduce a 30MB cache file to 3MB, making disk reads 10x faster. Decompression is fast on modern CPUs.

### 3.5 Query Optimization (Fetch Only Relevant Features)

**Concept:** Instead of `way(bbox); relation(bbox);`, query only relevant tag categories.

```
[out:json][timeout:180];
(
  way["highway"](bbox);
  way["landuse"](bbox);
  way["natural"](bbox);
  way["waterway"](bbox);
  way["railway"](bbox);
  way["building"](bbox);
  relation["type"="multipolygon"]["landuse"](bbox);
  relation["type"="multipolygon"]["natural"](bbox);
  relation["type"="boundary"]["boundary"="administrative"](bbox);
);
out geom;
```

**Pros:**
- Dramatically reduces response size (skip power lines, pipelines, etc.)
- Faster Overpass server processing
- Less data to cache and deserialize

**Cons:**
- Might miss some feature types users want in the future
- Must maintain the tag filter list

**Assessment:** **High-impact, low-effort.** Even a conservative filter removing obviously irrelevant features (power, telecom, pipeline) would significantly reduce data volume. Could offer "full" vs "optimized" query mode.

---

## 4. Recommended Implementation Plan

### Phase 1: Quick Wins (Low Risk, High Impact)

**Estimated effort: Small**

#### 1a. Fix Singleton Cache Bug
- Make `OsmQueryCache` a shared instance (static or injected)
- Ensures memory cache actually works across calls
- **Impact:** Eliminates redundant disk reads for the same session

#### 1b. Optimize Overpass Query
- Add tag filters to `BuildAllFeaturesQuery()` to exclude irrelevant features
- Only fetch highway, landuse, natural, waterway, railway, building, leisure, amenity
- All other tags should be listed commented for later usage
- All tags regarding buildings should be included for later procedural building generation
- Tags for bridges and tunnels are needed
- **Impact:** 30-60% reduction in response size and processing time

#### 1c. GZip Cache Compression
- Compress JSON before writing to disk (`GZipStream`)
- Decompress on read
- Bump `CacheVersion` to 3 to auto-invalidate old uncompressed caches
- **Impact:** 5-10x smaller cache files, faster disk I/O

#### 1d. Cache Index File
- On startup, scan all cache files and build an in-memory bbox index
- Store just `{filename, bbox, timestamp}` — no full deserialization
- Use the index for "containing cache" lookups instead of reading all files
- **Impact:** Eliminates O(N) full deserializations for cache misses

### Phase 2: Parallel Chunked Queries (Medium Risk, High Impact)

**Estimated effort: Medium**

#### 2a. Bounding Box Chunking
- Add `GeoBoundingBox.SplitIntoChunks(double chunkSizeMeters)` method
- Calculate approximate meters-per-degree at the bbox center latitude
- Split into grid of roughly equal-sized chunks
- Default chunk size: 1000m x 1000m (configurable)

#### 2b. Parallel Query Executor
- New class `ChunkedOverpassQueryService` wrapping `OverpassApiService`
- Use `SemaphoreSlim` to limit concurrency (max 2-3 parallel requests)
- Each chunk checks cache first, queries Overpass only if not cached
- Individual chunk retries on failure (don't retry the whole set)
- Progress reporting per chunk via `IProgress<T>` or `PubSubChannel`

#### 2c. Result Merging & Deduplication
- Merge all chunk results into single `OsmQueryResult`
- Deduplicate features by OSM element ID (`OsmFeature.Id`)
- For features split across chunk boundaries, keep the complete geometry (Overpass `out geom` returns full way geometry even if only part is in bbox)
- Cache the merged result under the original full bbox key

#### 2d. Adaptive Chunk Sizing
- Estimate data density from first chunk response
- If response is small (< 100 features), increase chunk size for remaining queries
- If response is large (> 5000 features), decrease chunk size
- This avoids over-chunking rural areas and under-chunking urban areas

### Phase 3: SQLite Cache (Medium Risk, Medium Impact)

**Estimated effort: Medium-Large**

#### 3a. SQLite R-Tree Index Cache
- Replace file-per-query JSON cache with single SQLite database
- Schema:
  ```sql
  CREATE TABLE osm_cache (
      id INTEGER PRIMARY KEY,
      cache_key TEXT UNIQUE,
      min_lat REAL, min_lon REAL,
      max_lat REAL, max_lon REAL,
      query_time TEXT,
      data BLOB,  -- GZip-compressed JSON
      version INTEGER
  );

  CREATE VIRTUAL TABLE osm_cache_rtree USING rtree(
      id, min_lat, max_lat, min_lon, max_lon
  );
  ```
- "Containing bbox" lookup becomes: `SELECT * FROM osm_cache_rtree WHERE min_lat <= ? AND max_lat >= ? AND min_lon <= ? AND max_lon >= ?`
- **Impact:** O(log N) spatial lookups instead of O(N) file scans

#### 3b. Migration Path
- no migration needed!

#### 3c. Chunk-Level Caching
- Store each chunk result separately in SQLite
- On cache hit for full bbox: query R-Tree for all chunks that intersect, merge
- On partial cache hit: only query Overpass for missing chunks
- **Impact:** Subsequent queries for overlapping or nearby areas reuse existing chunks

---

## 5. Implementation Priority Matrix

| Optimization | Impact | Effort | Risk | Priority |
|-------------|--------|--------|------|----------|
| 1a. Fix singleton cache | HIGH | LOW | LOW | **P0 — Do first** |
| 1b. Optimize query tags | HIGH | LOW | LOW | **P0 — Do first** |
| 1c. GZip compression | MEDIUM | LOW | LOW | **P1** |
| 1d. Cache bbox index | HIGH | LOW | LOW | **P1** |
| 2a-d. Parallel chunks | HIGH | MEDIUM | MEDIUM | **P2** |
| 3a-c. SQLite cache | MEDIUM | MEDIUM-HIGH | MEDIUM | **P3 — Evaluate after Phase 1-2** |

### Decision: JSON vs SQLite

**Recommendation: Start with JSON + GZip + bbox index (Phase 1), then evaluate.**

Phase 1 improvements will likely resolve most perceived slowness:
- The singleton fix alone eliminates repeated disk deserialization within a session
- GZip reduces file sizes 5-10x
- Bbox index eliminates scanning all files for "containing" lookups

If performance is still insufficient after Phase 1 + 2, Phase 3 (SQLite) provides further gains. SQLite is most valuable when there are many cached regions (dozens of files) and frequent "containing bbox" lookups — which may not be the common case after chunked caching is implemented.

---

## 6. Overpass API Rate Limiting Considerations

Public Overpass servers have usage policies:
- **overpass-api.de:** Max 2 concurrent requests, 10,000 requests/day
- **overpass.private.coffee:** Similar limits
- **General:** Be respectful of shared infrastructure

For parallel chunked queries:
- Use `SemaphoreSlim(2)` to limit to 2 concurrent requests
- Add 200-500ms delay between starting new requests to the same endpoint
- Distribute parallel requests across different endpoints
- Cache aggressively to minimize repeat queries

---

## 7. Data Deduplication Strategy for Chunked Queries

When an OSM way spans multiple chunks, Overpass returns the **complete** way geometry in each chunk's response (because of `out geom`). This means:

1. The same way appears in multiple chunk responses with identical `id`
2. **Dedup by `(type, id)` tuple** — keep first occurrence, skip duplicates
3. No need to stitch/merge geometries — each occurrence already has full geometry
4. Relations may appear in multiple chunks — same dedup rule applies

```csharp
// Pseudocode for merging
var seen = new HashSet<(string type, long id)>();
var merged = new List<OsmFeature>();
foreach (var chunk in chunkResults)
{
    foreach (var feature in chunk.Features)
    {
        if (seen.Add((feature.FeatureType.ToString(), feature.Id)))
            merged.Add(feature);
    }
}
```

---

## 8. Ring Assembly Performance Note

The `AssembleRingsFromSegments()` method in `OsmGeoJsonParser.cs` uses O(n^2) endpoint matching. For typical OSM data this is fast (most relations have < 50 segments), but for very complex multipolygons (country boundaries with 1000+ segments), it could become a bottleneck.

**Low priority optimization:** Build a dictionary of `(lat, lon) -> segment index` for O(1) endpoint lookups. Only implement if profiling shows this as a real bottleneck.

---

## 9. Metrics to Track

After implementing optimizations, measure:

1. **Cache hit ratio** — memory hits vs disk hits vs misses
2. **Query time** — wall-clock time from request to parsed result
3. **Cache read time** — time to read + deserialize from disk
4. **Cache write time** — time to serialize + write
5. **Response size** — bytes received from Overpass (before/after tag filtering)
6. **Feature count** — number of features returned (before/after tag filtering)

Add timing logs:
- but be careful that logging is done to the perf logfile and not to the ui! TerrainLogger.Info logs to the ui!!
```csharp
var sw = Stopwatch.StartNew();
// ... operation ...
TerrainLogger.Info($"Cache read: {sw.ElapsedMilliseconds}ms, {features} features");
```

---

## 10. Files to Modify

### Phase 1
| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Osm/Services/OsmQueryCache.cs` | GZip compression, bbox index, cache version bump |
| `BeamNgTerrainPoc/Terrain/Osm/Services/OverpassApiService.cs` | Tag-filtered query builder |
| `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` | Use shared OsmQueryCache instance |
| `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainAnalysisOrchestrator.cs` | Use shared OsmQueryCache instance |

### Phase 2
| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/GeoTiff/GeoBoundingBox.cs` | `SplitIntoChunks()` method |
| `BeamNgTerrainPoc/Terrain/Osm/Services/ChunkedOverpassQueryService.cs` | **New file** — parallel chunk orchestrator |
| `BeamNgTerrainPoc/Terrain/Osm/Services/IOverpassApiService.cs` | Interface update if needed |
| `BeamNgTerrainPoc/Terrain/Osm/Models/OsmQueryResult.cs` | Merge/dedup methods |

### Phase 3
| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Osm/Services/OsmSqliteCache.cs` | **New file** — SQLite cache implementation |
| `BeamNgTerrainPoc/Terrain/Osm/Services/OsmCacheManager.cs` | Switch to SQLite backend |
| `BeamNgTerrainPoc/BeamNgTerrainPoc.csproj` | Add `Microsoft.Data.Sqlite` NuGet |
