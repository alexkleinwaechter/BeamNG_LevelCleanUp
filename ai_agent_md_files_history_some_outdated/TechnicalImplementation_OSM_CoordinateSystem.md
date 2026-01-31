# Technical Implementation: OSM Integration & Coordinate System

## Overview

This document provides comprehensive technical details about the OpenStreetMap (OSM) integration for terrain generation, including coordinate system handling, bounding box management, and the dual-coordinate transformation system.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Coordinate Systems](#coordinate-systems)
3. [Bounding Box Management](#bounding-box-management)
4. [OSM Data Flow](#osm-data-flow)
5. [Coordinate Transformation](#coordinate-transformation)
6. [Layer Map Generation](#layer-map-generation)
7. [Road Spline Processing](#road-spline-processing)
8. [Caching Strategy](#caching-strategy)
9. [Error Handling](#error-handling)
10. [Class Reference](#class-reference)

---

## Architecture Overview

### Component Diagram

```
???????????????????????????????????????????????????????????????????????????
?                        GenerateTerrain.razor.cs                         ?
?  - Reads GeoTIFF metadata (WGS84 bbox)                                 ?
?  - Manages _geoBoundingBox (WGS84)                                     ?
?  - Orchestrates terrain generation                                      ?
???????????????????????????????????????????????????????????????????????????
                                    ?
                                    ?
???????????????????????????????????????????????????????????????????????????
?                          GeoTiffReader                                  ?
?  - Reads GeoTIFF files via GDAL                                        ?
?  - Extracts native bounding box                                         ?
?  - Transforms to WGS84 (EPSG:4326)                                     ?
?  - Returns GeoTiffImportResult / GeoTiffInfoResult                     ?
???????????????????????????????????????????????????????????????????????????
                                    ?
                                    ?
???????????????????????????????????????????????????????????????????????????
?                        OverpassApiService                               ?
?  - Queries OSM data via Overpass API                                   ?
?  - Uses WGS84 bounding box for queries                                 ?
?  - Retry logic with exponential backoff                                ?
?  - Fallback to alternative endpoints                                   ?
???????????????????????????????????????????????????????????????????????????
                                    ?
                                    ?
???????????????????????????????????????????????????????????????????????????
?                       OsmGeometryProcessor                              ?
?  - Transforms geo coordinates to pixel coordinates                     ?
?  - Two coordinate systems: Image-space & Terrain-space                 ?
?  - Rasterizes polygons and lines to layer maps                         ?
?  - Converts lines to RoadSpline objects                                ?
???????????????????????????????????????????????????????????????????????????
                                    ?
                    ?????????????????????????????????
                    ?                               ?
????????????????????????????????   ????????????????????????????????
?    Layer Maps (PNG)          ?   ?    Road Splines              ?
?  - Image-space coordinates   ?   ?  - Terrain-space coordinates ?
?  - Y=0 at top                ?   ?  - Y=0 at bottom             ?
?  - Saved as 8-bit grayscale  ?   ?  - Used with heightmap array ?
????????????????????????????????   ????????????????????????????????
                    ?                               ?
                    ?                               ?
????????????????????????????????   ????????????????????????????????
?  MaterialLayerProcessor      ?   ?  RoadSmoothingService        ?
?  - Flips Y when reading      ?   ?  - Uses splines directly     ?
?  - Writes to terrain array   ?   ?  - Modifies heightmap        ?
????????????????????????????????   ????????????????????????????????
```

### Key Design Decisions

1. **WGS84 Bounding Box Once**: The WGS84 bounding box is extracted from GeoTIFF metadata **once** during file browsing (`ReadGeoTiffMetadata`), not during terrain generation.

2. **Dual Coordinate Transformation**: Two separate transformation methods handle the different Y-axis conventions:
   - Image-space (top-left origin) for layer maps
   - Terrain-space (bottom-left origin) for heightmap operations

3. **Separation of Concerns**: Layer map generation and road smoothing use different coordinate systems appropriate to their downstream consumers.

---

## Coordinate Systems

### Three Coordinate Systems in Use

| System | Origin | Y Direction | Use Case |
|--------|--------|-------------|----------|
| **Geographic (WGS84)** | Equator/Prime Meridian | North = +Y | OSM queries, bounding boxes |
| **Image-space** | Top-left | Down = +Y | PNG files, layer maps |
| **Terrain-space (BeamNG)** | Bottom-left | Up = +Y | Heightmap arrays, splines |

### Visual Representation

```
Geographic (WGS84)          Image-space              Terrain-space (BeamNG)
     North (+Y)                  Y=0                      Y=max
         ?                    ????????                  ????????
         ?                    ?      ?                  ?      ?
    West ? ? East        X=0  ?      ?  X=max      X=0  ?      ?  X=max
         ?                    ?      ?                  ?      ?
         ?                    ????????                  ????????
     South (-Y)               Y=max                       Y=0
```

### Coordinate System Mapping

```
Geographic ? Image-space:
  X_pixel = (lon - minLon) / width * terrainSize
  Y_pixel = (1.0 - (lat - minLat) / height) * terrainSize  // INVERTED

Geographic ? Terrain-space:
  X_pixel = (lon - minLon) / width * terrainSize
  Y_pixel = (lat - minLat) / height * terrainSize          // NOT inverted
```

---

## Bounding Box Management

### GeoTIFF Bounding Boxes

When reading a GeoTIFF file, two bounding boxes are produced:

```csharp
public class GeoTiffImportResult
{
    /// <summary>
    /// Native bounding box in the GeoTIFF's coordinate reference system.
    /// May be projected coordinates (e.g., UTM with values in meters).
    /// </summary>
    public required GeoBoundingBox BoundingBox { get; init; }
    
    /// <summary>
    /// WGS84 (EPSG:4326) bounding box for Overpass API queries.
    /// Null if transformation failed.
    /// </summary>
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }
}
```

### Bounding Box Flow

```
GeoTIFF File
     ?
     ?
???????????????????????????????????????????????????????
? GeoTiffReader.GetGeoTiffInfoExtended()              ?
?                                                     ?
? 1. Read geotransform from GDAL                     ?
? 2. Calculate native bounding box                   ?
? 3. Get projection WKT                              ?
? 4. If not WGS84:                                   ?
?    - Transform corners to WGS84 via GDAL           ?
?    - Create Wgs84BoundingBox                       ?
???????????????????????????????????????????????????????
     ?
     ?
???????????????????????????????????????????????????????
? GenerateTerrain.ReadGeoTiffMetadata()              ?
?                                                     ?
? - Stores Wgs84BoundingBox in _geoBoundingBox       ?
? - This is the ONLY place bbox is set for OSM       ?
? - TerrainCreator does NOT overwrite this           ?
???????????????????????????????????????????????????????
     ?
     ?
???????????????????????????????????????????????????????
? OverpassApiService.QueryAllFeaturesAsync()         ?
?                                                     ?
? - Uses _geoBoundingBox (WGS84) for query           ?
? - Formats as: (south, west, north, east)           ?
? - Example: (50.2,7.1,50.3,7.2)                     ?
???????????????????????????????????????????????????????
```

### Critical Bug Fix

**Problem**: After terrain generation, `_geoBoundingBox` was being overwritten with `parameters.GeoBoundingBox` (the native/projected coordinates):

```csharp
// BUG - This overwrote WGS84 with projected coordinates!
if (parameters.GeoBoundingBox != null)
{
    _geoBoundingBox = parameters.GeoBoundingBox;  // WRONG!
}
```

**Solution**: Don't overwrite `_geoBoundingBox` after terrain generation. Only capture elevation values:

```csharp
// CORRECT - Only capture elevation values, not bounding box
if (parameters.GeoTiffMinElevation.HasValue)
{
    _geoTiffMinElevation = parameters.GeoTiffMinElevation;
}
if (parameters.GeoTiffMaxElevation.HasValue)
{
    _geoTiffMaxElevation = parameters.GeoTiffMaxElevation;
}
```

---

## OSM Data Flow

### Query Pipeline

```
User selects GeoTIFF
        ?
        ?
ReadGeoTiffMetadata()
  - Extract WGS84 bbox
  - Store in _geoBoundingBox
        ?
        ?
User opens OsmFeatureSelectorDialog
        ?
        ?
OsmQueryCache.GetAsync(bbox)
  - Check if cached result exists
  - Cache key: bbox string hash
        ?
        ??? Cache Hit ??? Return cached OsmQueryResult
        ?
        ??? Cache Miss ??? OverpassApiService.QueryAllFeaturesAsync(bbox)
                               ?
                               ?
                          Build Overpass Query:
                          [out:json][timeout:180];
                          (way(south,west,north,east);
                           relation(south,west,north,east););
                          out geom;
                               ?
                               ?
                          HTTP POST to Overpass API
                          (with retry & fallback)
                               ?
                               ?
                          OsmGeoJsonParser.Parse()
                          - Convert JSON to OsmFeature objects
                          - Resolve geometry coordinates
                               ?
                               ?
                          Cache result & return
```

### Overpass Query Format

```
[out:json][timeout:180];
(
  way(50.200000,7.100000,50.300000,7.200000);
  relation(50.200000,7.100000,50.300000,7.200000);
);
out geom;
```

The bounding box format is `(south, west, north, east)` which corresponds to `(minLat, minLon, maxLat, maxLon)`.

---

## Coordinate Transformation

### OsmGeometryProcessor Methods

#### TransformToPixelCoordinate (Image-space)

Used for layer maps that will be saved as PNG files and read by `MaterialLayerProcessor`.

```csharp
/// <summary>
/// Transforms a geographic coordinate to pixel coordinates in image-space.
/// Image-space uses top-left origin (Y=0 at top), matching PNG/image files.
/// </summary>
public Vector2 TransformToPixelCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize)
{
    var normalizedX = (coord.Longitude - bbox.MinLongitude) / bbox.Width;
    var normalizedY = (coord.Latitude - bbox.MinLatitude) / bbox.Height;
    
    // Y is INVERTED for image-space
    var pixelX = (float)(normalizedX * terrainSize);
    var pixelY = (float)((1.0 - normalizedY) * terrainSize);
    
    return new Vector2(pixelX, pixelY);
}
```

#### TransformToTerrainCoordinate (Terrain-space)

Used for road splines that will be used directly with the heightmap array.

```csharp
/// <summary>
/// Transforms a geographic coordinate to pixel coordinates in BeamNG terrain-space.
/// BeamNG uses bottom-left origin (Y=0 at bottom), matching how the heightmap is stored.
/// </summary>
public Vector2 TransformToTerrainCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize)
{
    var normalizedX = (coord.Longitude - bbox.MinLongitude) / bbox.Width;
    var normalizedY = (coord.Latitude - bbox.MinLatitude) / bbox.Height;
    
    // NO Y inversion for terrain-space
    var pixelX = (float)(normalizedX * terrainSize);
    var pixelY = (float)(normalizedY * terrainSize);
    
    return new Vector2(pixelX, pixelY);
}
```

### Why Two Systems?

```
Layer Maps (PNG)                    Road Splines
       ?                                  ?
       ?                                  ?
MaterialLayerProcessor              RoadSmoothingService
       ?                                  ?
       ? Reads PNG (Y=0 at top)           ? Uses heightMap[y,x]
       ? Flips Y when writing to          ? HeightMap is already in
       ? terrain array                    ? BeamNG format (Y=0 at bottom)
       ?                                  ?
       ?                                  ?
???????????????????               ???????????????????
? flippedY =      ?               ? // Direct access ?
? size - 1 - y    ?               ? heightMap[y, x]  ?
???????????????????               ???????????????????
```

If we used image-space coordinates for splines, the roads would appear at the wrong Y position (mirrored vertically).

---

## Layer Map Generation

### Polygon Rasterization

```csharp
public byte[,] RasterizePolygonsToLayerMap(
    List<OsmFeature> polygonFeatures, 
    GeoBoundingBox bbox, 
    int terrainSize)
{
    var result = new byte[terrainSize, terrainSize];
    
    foreach (var feature in polygonFeatures.Where(f => f.GeometryType == OsmGeometryType.Polygon))
    {
        // Use IMAGE-SPACE coordinates for layer maps
        var pixelCoords = TransformToPixelCoordinates(feature.Coordinates, bbox, terrainSize);
        RasterizePolygon(result, pixelCoords);
    }
    
    return result;
}
```

### Line Rasterization

```csharp
public byte[,] RasterizeLinesToLayerMap(
    List<OsmFeature> lineFeatures,
    GeoBoundingBox bbox,
    int terrainSize,
    float lineWidthPixels)
{
    var result = new byte[terrainSize, terrainSize];
    var halfWidth = lineWidthPixels / 2f;
    
    foreach (var feature in lineFeatures.Where(f => f.GeometryType == OsmGeometryType.LineString))
    {
        // Use IMAGE-SPACE coordinates for layer maps
        var pixelCoords = TransformToPixelCoordinates(feature.Coordinates, bbox, terrainSize);
        var croppedCoords = CropLineToTerrain(pixelCoords, terrainSize);
        RasterizeLine(result, croppedCoords, halfWidth);
    }
    
    return result;
}
```

### Scanline Fill Algorithm

For polygon rasterization, a scanline algorithm is used:

```csharp
private void RasterizePolygon(byte[,] mask, List<Vector2> polygon)
{
    // 1. Find Y bounds
    var minY = Math.Max(0, (int)polygon.Min(p => p.Y));
    var maxY = Math.Min(height - 1, (int)polygon.Max(p => p.Y));
    
    // 2. For each scanline
    for (var y = minY; y <= maxY; y++)
    {
        var intersections = new List<float>();
        
        // 3. Find all edge intersections
        for (var i = 0; i < polygon.Count - 1; i++)
        {
            // ... calculate intersection X values
        }
        
        // 4. Sort intersections and fill between pairs
        intersections.Sort();
        for (var i = 0; i < intersections.Count - 1; i += 2)
        {
            for (var x = xStart; x <= xEnd; x++)
            {
                mask[y, x] = 255;
            }
        }
    }
}
```

---

## Road Spline Processing

### Spline Creation from OSM Lines

```csharp
public List<RoadSpline> ConvertLinesToSplines(
    List<OsmFeature> lineFeatures,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel)
{
    var splines = new List<RoadSpline>();
    
    foreach (var feature in lineFeatures.Where(f => f.GeometryType == OsmGeometryType.LineString))
    {
        // Use TERRAIN-SPACE coordinates for splines
        var terrainCoords = TransformToTerrainCoordinates(feature.Coordinates, bbox, terrainSize);
        var croppedCoords = CropLineToTerrain(terrainCoords, terrainSize);
        
        if (croppedCoords.Count >= 2)
        {
            var spline = new RoadSpline(croppedCoords);
            splines.Add(spline);
        }
    }
    
    return splines;
}
```

### RoadSpline Interpolation

The `RoadSpline` class uses adaptive interpolation based on point count:

| Points | Interpolation Method | Notes |
|--------|---------------------|-------|
| 5+ | **Akima Spline** | Best quality, avoids overshoot |
| 3-4 | **Natural Cubic Spline** | Good smoothness |
| 2 | **Linear Interpolation** | Simple straight line |

```csharp
public RoadSpline(List<Vector2> controlPoints)
{
    // Choose interpolation method based on number of points
    if (controlPoints.Count >= MinPointsForAkima)
    {
        _splineX = CubicSpline.InterpolateAkimaSorted(t, x);
        _splineY = CubicSpline.InterpolateAkimaSorted(t, y);
    }
    else if (controlPoints.Count >= 3)
    {
        _splineX = CubicSpline.InterpolateNaturalSorted(t, x);
        _splineY = CubicSpline.InterpolateNaturalSorted(t, y);
    }
    else
    {
        _splineX = LinearSpline.InterpolateSorted(t, x);
        _splineY = LinearSpline.InterpolateSorted(t, y);
    }
}
```

### Cross-Section Generation

Splines are sampled to create cross-sections for height modification:

```csharp
// In RoadSmoothingService.BuildGeometryFromPreBuiltSplines()
foreach (var spline in splines)
{
    var samples = spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
    
    foreach (var sample in samples)
    {
        var crossSection = new CrossSection
        {
            CenterPoint = sample.Position,      // Terrain-space coordinates
            NormalDirection = sample.Normal,
            TangentDirection = sample.Tangent,
            WidthMeters = parameters.RoadWidthMeters
        };
        geometry.CrossSections.Add(crossSection);
    }
}
```

---

## Caching Strategy

### OsmQueryCache

OSM query results are cached to avoid redundant API calls:

```csharp
public class OsmQueryCache
{
    private static readonly ConcurrentDictionary<string, OsmQueryResult> _cache = new();
    
    public Task<OsmQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        var key = GetCacheKey(bbox);
        return Task.FromResult(_cache.TryGetValue(key, out var result) ? result : null);
    }
    
    public Task SetAsync(GeoBoundingBox bbox, OsmQueryResult result)
    {
        var key = GetCacheKey(bbox);
        result.IsFromCache = false;  // Will be true on subsequent reads
        _cache[key] = result;
        return Task.CompletedTask;
    }
    
    private string GetCacheKey(GeoBoundingBox bbox)
    {
        // Use bounding box string representation as key
        return bbox.ToFileNameString();
    }
}
```

### Cache Key Format

```
bbox_7.100000_50.200000_7.200000_50.300000
```

---

## Error Handling

### Overpass API Retry Strategy

```csharp
public const int MaxRetryAttempts = 3;
public const int BaseRetryDelayMs = 2000;

for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
{
    try
    {
        var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(cancellationToken);
        
        var isRetryable = IsRetryableError(response.StatusCode, errorBody);
        
        if (!isRetryable || attempt == MaxRetryAttempts)
            throw new HttpRequestException($"Overpass API returned {response.StatusCode}");
        
        // Exponential backoff: 2s, 4s, 8s
        var delayMs = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
        await Task.Delay(delayMs, cancellationToken);
    }
    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // HTTP client timeout - retry
    }
}

// Fallback to alternative endpoint
return await ExecuteRawQueryWithRetryAsync(query, AlternativeEndpoint, cancellationToken);
```

### Retryable Errors

```csharp
private static bool IsRetryableError(HttpStatusCode statusCode, string errorBody)
{
    // Gateway errors
    if (statusCode == HttpStatusCode.GatewayTimeout ||
        statusCode == HttpStatusCode.BadGateway ||
        statusCode == HttpStatusCode.ServiceUnavailable ||
        (int)statusCode == 429)  // Too Many Requests
        return true;
    
    // Known transient errors in body
    if (errorBody.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        errorBody.Contains("too busy", StringComparison.OrdinalIgnoreCase) ||
        errorBody.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
        errorBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        return true;
    
    return false;
}
```

### Spline Creation Error Handling

```csharp
if (croppedCoords.Count >= 2)
{
    try
    {
        var spline = new RoadSpline(croppedCoords);
        splines.Add(spline);
    }
    catch (Exception ex)
    {
        // Log warning but continue processing other features
        TerrainLogger.Warning($"Failed to create spline from OSM feature {feature.Id}: {ex.Message}");
    }
}
```

---

## Class Reference

### Core Classes

#### GeoBoundingBox

```csharp
namespace BeamNgTerrainPoc.Terrain.GeoTiff;

public class GeoBoundingBox
{
    // Properties
    public GeoCoordinate LowerLeft { get; }
    public GeoCoordinate UpperRight { get; }
    public double MinLongitude { get; }
    public double MinLatitude { get; }
    public double MaxLongitude { get; }
    public double MaxLatitude { get; }
    public double Width { get; }
    public double Height { get; }
    
    // Methods
    public bool Contains(GeoCoordinate point);
    public string ToOverpassBBox();  // Returns "(south,west,north,east)"
    public string ToFileNameString();
    public bool IsValidWgs84 { get; }
    
    public static GeoBoundingBox? TransformToWgs84(GeoBoundingBox projectedBbox, string sourceProjectionWkt);
    public static bool IsWgs84Projection(string? projectionWkt);
}
```

#### GeoCoordinate

```csharp
namespace BeamNgTerrainPoc.Terrain.GeoTiff;

public class GeoCoordinate
{
    public double Longitude { get; }  // X: -180 to 180
    public double Latitude { get; }   // Y: -90 to 90
    public double? Altitude { get; }
}
```

#### OsmGeometryProcessor

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

public class OsmGeometryProcessor
{
    // Coordinate Transformation
    Vector2 TransformToPixelCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize);
    Vector2 TransformToTerrainCoordinate(GeoCoordinate coord, GeoBoundingBox bbox, int terrainSize);
    List<Vector2> TransformToPixelCoordinates(List<GeoCoordinate> geoCoords, GeoBoundingBox bbox, int terrainSize);
    List<Vector2> TransformToTerrainCoordinates(List<GeoCoordinate> geoCoords, GeoBoundingBox bbox, int terrainSize);
    
    // Clipping
    List<OsmFeature> CropToBoundingBox(List<OsmFeature> features, GeoBoundingBox bbox);
    List<Vector2> CropLineToTerrain(List<Vector2> coords, int terrainSize);
    
    // Rasterization (uses image-space)
    byte[,] RasterizePolygonsToLayerMap(List<OsmFeature> polygonFeatures, GeoBoundingBox bbox, int terrainSize);
    byte[,] RasterizeLinesToLayerMap(List<OsmFeature> lineFeatures, GeoBoundingBox bbox, int terrainSize, float lineWidthPixels);
    
    // Spline Conversion (uses terrain-space, returns METER coordinates)
    // IMPORTANT: Returned splines are in meter coordinates (not pixels)
    List<RoadSpline> ConvertLinesToSplines(List<OsmFeature> lineFeatures, GeoBoundingBox bbox, int terrainSize, float metersPerPixel);
    
    // Feature Retrieval
    List<OsmFeature> GetFeaturesByIds(OsmQueryResult queryResult, IEnumerable<long> featureIds);
    List<OsmFeature> GetFeaturesFromSelections(OsmQueryResult queryResult, IEnumerable<OsmFeatureSelection> selections);
    
    // Debug Export
    // Exports a debug image showing OSM splines for coordinate verification
    void ExportOsmSplineDebugImage(List<RoadSpline> splines, int terrainSize, float metersPerPixel, string outputPath);
}
```

#### OverpassApiService

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Services;

public class OverpassApiService : IOverpassApiService, IDisposable
{
    // Constants
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    public const string AlternativeEndpoint = "https://overpass.kumi.systems/api/interpreter";
    public const int DefaultTimeoutSeconds = 180;
    public const int MaxRetryAttempts = 3;
    public const int BaseRetryDelayMs = 2000;
    
    // Methods
    Task<OsmQueryResult> QueryAllFeaturesAsync(GeoBoundingBox bbox, CancellationToken cancellationToken = default);
    Task<OsmQueryResult> QueryByTagsAsync(GeoBoundingBox bbox, Dictionary<string, string?> tagFilters, CancellationToken cancellationToken = default);
    Task<string> ExecuteRawQueryAsync(string query, CancellationToken cancellationToken = default);
}
```

#### RoadSpline

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

public class RoadSpline
{
    // Properties
    public List<Vector2> ControlPoints { get; }
    public float TotalLength { get; }
    
    // Methods
    public RoadSpline(List<Vector2> controlPoints);  // Requires >= 2 points
    public Vector2 GetPointAtDistance(float distance);
    public Vector2 GetTangentAtDistance(float distance);
    public Vector2 GetNormalAtDistance(float distance);
    public List<SplineSample> SampleByDistance(float intervalMeters);
}

public struct SplineSample
{
    public float Distance;
    public Vector2 Position;
    public Vector2 Tangent;
    public Vector2 Normal;
}
```

---

## Configuration & Parameters

### RoadSmoothingParameters (OSM-related)

```csharp
public class RoadSmoothingParameters
{
    /// <summary>
    /// Pre-built splines from OSM data (bypasses skeleton extraction).
    /// </summary>
    public List<RoadSpline>? PreBuiltSplines { get; set; }
    
    /// <summary>
    /// Whether to use pre-built splines instead of extracting from road mask.
    /// </summary>
    public bool UsePreBuiltSplines => PreBuiltSplines != null && PreBuiltSplines.Count > 0;
}
```

### TerrainCreationParameters (Geo-related)

```csharp
public class TerrainCreationParameters
{
    /// <summary>
    /// Path to a GeoTIFF heightmap file.
    /// </summary>
    public string? GeoTiffPath { get; set; }
    
    /// <summary>
    /// Path to a directory containing multiple GeoTIFF tiles.
    /// </summary>
    public string? GeoTiffDirectory { get; set; }
    
    /// <summary>
    /// Geographic bounding box (populated during GeoTIFF import).
    /// WARNING: This is the native/projected bbox, not WGS84!
    /// </summary>
    public GeoBoundingBox? GeoBoundingBox { get; set; }
    
    /// <summary>
    /// Min/max elevation from GeoTIFF.
    /// </summary>
    public double? GeoTiffMinElevation { get; set; }
    public double? GeoTiffMaxElevation { get; set; }
}
```

---

## Troubleshooting

### Common Issues

#### 1. Roads Appear at Wrong Position

**Symptom**: Roads are mirrored vertically or offset from expected location.

**Cause**: Using image-space coordinates instead of terrain-space for splines.

**Solution**: Use `TransformToTerrainCoordinates()` for road splines, not `TransformToPixelCoordinates()`.

#### 2. Overpass API Returns BadRequest

**Symptom**: `HttpRequestException: Overpass API returned BadRequest`

**Cause**: Using projected coordinates (UTM) instead of WGS84 for the query.

**Solution**: Ensure `_geoBoundingBox` is not overwritten after terrain generation. Only the WGS84 bounding box from `ReadGeoTiffMetadata()` should be used.

**Example of invalid coordinates**:
```
way(5564999.500000,385999.500000,5569000.500000,390000.500000);  // UTM - WRONG!
```

**Example of valid coordinates**:
```
way(50.200000,7.100000,50.300000,7.200000);  // WGS84 - CORRECT!
```

#### 3. Spline Creation Fails with ArgumentException

**Symptom**: `ArgumentException: The given array is too small. It must be at least 5 long.`

**Cause**: Akima spline interpolation requires at least 5 points, but some OSM road segments have fewer.

**Solution**: `RoadSpline` constructor now falls back to cubic spline (3-4 points) or linear interpolation (2 points).

#### 4. GeoTIFF Resize Causes Offset

**Symptom**: Features are offset after resizing GeoTIFF from non-power-of-2 to power-of-2.

**Cause**: The geographic extent stays the same, but if the OSM transformation uses the original size instead of the target size, coordinates won't match.

**Solution**: Always use `terrainSize` (the target size after resize) for coordinate transformation, not the original GeoTIFF dimensions. The bounding box represents the geographic extent which doesn't change during resize.

#### 5. OSM Roads Appear at Wrong Position (Offset)

**Symptom**: OSM road splines are offset or misaligned with the terrain heightmap. The road carving in the terrain doesn't match where the OSM roads are drawn.

**Cause**: The `OsmGeometryProcessor.ConvertLinesToSplines()` was returning splines in **pixel coordinates**, but `RoadSpline.SampleByDistance()` and the road smoothing service expect coordinates in **meters**.

When spline control points are in pixel units (0 to terrainSize), the spline's internal arc length calculation produces pixel-based distances. Then when `SampleByDistance(intervalMeters)` is called with meter values (e.g., 0.5m), it samples near position 0.5 in pixel coordinates, which is basically just the start of the road. This causes roads to appear as tiny segments clustered at one corner.

**Solution**: Convert pixel coordinates to meter coordinates in `ConvertLinesToSplines()` by multiplying by `metersPerPixel`:

```csharp
// Convert from pixel coordinates to meter coordinates
var meterCoords = croppedCoords
    .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
    .ToList();

var spline = new RoadSpline(meterCoords);
```

**Debugging**: A new debug image export `ExportOsmSplineDebugImage()` can be used to visualize where OSM splines are positioned. The image shows:
- White lines connecting control points
- Red dots at spline start points
- Blue dots at spline end points
- Green dots at intermediate control points

This debug image is automatically exported to `{debugPath}/{materialName}_osm_splines_debug.png` during terrain generation when OSM road features are used.

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024-01 | Initial OSM integration with Overpass API |
| 1.1 | 2024-01 | Fixed WGS84 bounding box overwrite issue |
| 1.2 | 2024-01 | Added dual coordinate transformation (image-space vs terrain-space) |
| 1.3 | 2024-01 | Added adaptive spline interpolation for short road segments |
| 1.4 | 2024-01 | Fixed spline coordinate unit mismatch: pixel coords ? meter coords |
| 1.5 | 2024-01 | Added OSM spline debug image export for coordinate verification |

---

## References

- [Overpass API Documentation](https://wiki.openstreetmap.org/wiki/Overpass_API)
- [OSM Tags](https://wiki.openstreetmap.org/wiki/Tags)
- [EPSG:4326 (WGS84)](https://epsg.io/4326)
- [BeamNG Terrain Format](https://documentation.beamng.com/modding/terrain/)
- [GDAL Documentation](https://gdal.org/api/index.html)
