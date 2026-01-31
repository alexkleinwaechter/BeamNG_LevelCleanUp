# Overpass API Integration - GeoJSON Migration Plan

## Overview

This document describes the migration from OSM XML format to GeoJSON format for the Overpass API integration. It also addresses the coordinate system issue discovered during testing.

## Problem Discovered

During testing, the Overpass API returned a `BadRequest` error:
```
Error: line 3: static error: For the attribute "s" of the element "bbox-query" 
the only allowed values are floats between -90.0 and 90.0.
```

**Root Cause**: The GeoTIFF bounding box coordinates are in a **projected coordinate system** (likely UTM or similar), not WGS84 (EPSG:4326). Overpass API requires WGS84 coordinates (latitude -90 to 90, longitude -180 to 180).

## Current Implementation Status

### Already Implemented (Phase 1-3 from original plan)

#### BeamNgTerrainPoc - OSM Service Layer
| File | Status | Description |
|------|--------|-------------|
| `Terrain/Osm/Models/OsmElements.cs` | ? Done | OSM node, way, relation models |
| `Terrain/Osm/Models/OsmFeature.cs` | ? Done | Processed feature with geometry |
| `Terrain/Osm/Models/OsmFeatureSelection.cs` | ? Done | UI selection model |
| `Terrain/Osm/Models/OsmQueryResult.cs` | ? Done | Query result container |
| `Terrain/Osm/Services/IOverpassApiService.cs` | ? Done | Service interface |
| `Terrain/Osm/Services/OverpassApiService.cs` | ?? Needs Update | XML-based, needs GeoJSON |
| `Terrain/Osm/Services/OsmQueryCache.cs` | ? Done | Query caching |
| `Terrain/Osm/Parsing/OsmXmlParser.cs` | ? Remove | Replace with GeoJSON parser |
| `Terrain/Osm/Processing/OsmGeometryProcessor.cs` | ? Done | Geometry processing |
| `Terrain/Models/LayerSource.cs` | ? Done | Layer source abstraction |
| `Terrain/Models/MaterialDefinition.cs` | ? Done | Updated with LayerSource |
| `Terrain/Models/RoadSmoothingParameters.cs` | ? Done | Added PreBuiltSplines |

#### BeamNG_LevelCleanUp - UI Layer
| File | Status | Description |
|------|--------|-------------|
| `BlazorUI/Components/OsmFeatureSelectorDialog.razor` | ? Done | Feature selection dialog |
| `BlazorUI/Components/OsmFeaturePreview.razor` | ? Done | SVG feature preview |
| `BlazorUI/Components/TerrainMaterialSettings.razor` | ? Done | OSM source UI added |
| `BlazorUI/Components/TerrainMaterialSettings.razor.cs` | ? Done | OSM handling added |
| `BlazorUI/Pages/GenerateTerrain.razor` | ? Done | Passes bbox to materials |
| `BlazorUI/Pages/GenerateTerrain.razor.cs` | ? Done | GeoTIFF metadata reading, OSM processing |

---

## Migration Tasks

### Task 1: Coordinate System Validation & Transformation

**Problem**: GeoTIFF files may use projected coordinate systems (UTM, State Plane, etc.) instead of WGS84.

**Files to modify**:
- `BeamNgTerrainPoc/Terrain/GeoTiff/GeoTiffReader.cs`
- `BeamNgTerrainPoc/Terrain/GeoTiff/GeoBoundingBox.cs`

**Changes needed**:

1. **Add coordinate system detection** in `GeoTiffReader`:
```csharp
public bool IsWgs84(Dataset dataset)
{
    var projection = dataset.GetProjection();
    // Check if EPSG:4326 or WGS84
    return projection.Contains("WGS 84") || 
           projection.Contains("EPSG:4326") ||
           projection.Contains("GEOGCS");
}
```

2. **Add coordinate transformation** using GDAL:
```csharp
public GeoBoundingBox TransformToWgs84(GeoBoundingBox projectedBbox, string sourceProjection)
{
    // Use GDAL's CoordinateTransformation to convert
    // from source CRS to EPSG:4326
}
```

3. **Add validation** in `GeoBoundingBox`:
```csharp
public bool IsValidWgs84 => 
    MinLatitude >= -90 && MaxLatitude <= 90 &&
    MinLongitude >= -180 && MaxLongitude <= 180;
```

---

### Task 2: Switch Overpass API to GeoJSON Output

**Why GeoJSON is better**:
1. ? Native JSON parsing (no XML complexity)
2. ? Coordinates already resolved in geometry arrays
3. ? Explicit geometry types (Point, LineString, Polygon, MultiPolygon)
4. ? Smaller payload (~30% smaller than XML for same data)
5. ? GeoJSON is a standard format with broad tooling support
6. ? Properties are in a clean `properties` object

**Files to modify**:
- `BeamNgTerrainPoc/Terrain/Osm/Services/OverpassApiService.cs`

**Files to add**:
- `BeamNgTerrainPoc/Terrain/Osm/Parsing/OsmGeoJsonParser.cs`

**Files to remove**:
- `BeamNgTerrainPoc/Terrain/Osm/Parsing/OsmXmlParser.cs`

#### 2.1 Update OverpassApiService

Change the query output format from XML to GeoJSON:

```csharp
// OLD (XML)
private string BuildAllFeaturesQuery(GeoBoundingBox bbox)
{
    return $"""
        [out:xml][timeout:{DefaultTimeoutSeconds}];
        (
          nwr{bboxStr};
          <;
          >;
        );
        out meta;
        """;
}

// NEW (GeoJSON)
private string BuildAllFeaturesQuery(GeoBoundingBox bbox)
{
    var bboxStr = FormatBBox(bbox);
    
    // Use [out:json] with geom for resolved coordinates
    return $"""
        [out:json][timeout:{DefaultTimeoutSeconds}];
        (
          way{bboxStr};
          relation{bboxStr};
        );
        out geom;
        """;
}
```

**Key changes**:
- `[out:xml]` ? `[out:json]`
- `out meta` ? `out geom` (includes resolved geometry coordinates)
- Query only ways and relations (we don't need standalone nodes for terrain materials)

#### 2.2 Create GeoJSON Parser

**New file**: `BeamNgTerrainPoc/Terrain/Osm/Parsing/OsmGeoJsonParser.cs`

```csharp
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Parsing;

/// <summary>
/// Parses Overpass API JSON responses with geometry output.
/// </summary>
public class OsmGeoJsonParser
{
    /// <summary>
    /// Parses the Overpass JSON response.
    /// </summary>
    public OsmQueryResult Parse(string json, GeoBoundingBox bbox)
    {
        var result = new OsmQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("elements", out var elements))
            return result;
        
        foreach (var element in elements.EnumerateArray())
        {
            var feature = ParseElement(element);
            if (feature != null)
            {
                result.Features.Add(feature);
            }
        }
        
        return result;
    }
    
    private OsmFeature? ParseElement(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeEl))
            return null;
        
        var type = typeEl.GetString();
        if (type != "way" && type != "relation")
            return null;
        
        var id = element.GetProperty("id").GetInt64();
        
        // Parse tags
        var tags = new Dictionary<string, string>();
        if (element.TryGetProperty("tags", out var tagsEl))
        {
            foreach (var tag in tagsEl.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.GetString() ?? "";
            }
        }
        
        // Parse geometry (from "out geom" output)
        var coordinates = new List<GeoCoordinate>();
        
        if (element.TryGetProperty("geometry", out var geomEl))
        {
            // Way geometry: array of {lat, lon} objects
            foreach (var point in geomEl.EnumerateArray())
            {
                var lat = point.GetProperty("lat").GetDouble();
                var lon = point.GetProperty("lon").GetDouble();
                coordinates.Add(new GeoCoordinate(lon, lat));
            }
        }
        
        if (coordinates.Count < 2)
            return null;
        
        // Determine geometry type from tags and closure
        var geometryType = DetermineGeometryType(tags, coordinates);
        
        return new OsmFeature
        {
            Id = id,
            FeatureType = type == "way" ? OsmFeatureType.Way : OsmFeatureType.Relation,
            GeometryType = geometryType,
            Coordinates = coordinates,
            Tags = tags
        };
    }
    
    private OsmGeometryType DetermineGeometryType(
        Dictionary<string, string> tags, 
        List<GeoCoordinate> coordinates)
    {
        // Check if closed (first point equals last point)
        var isClosed = coordinates.Count > 2 &&
            Math.Abs(coordinates[0].Longitude - coordinates[^1].Longitude) < 0.0000001 &&
            Math.Abs(coordinates[0].Latitude - coordinates[^1].Latitude) < 0.0000001;
        
        // Area-indicating tags
        if (tags.ContainsKey("building") || 
            tags.ContainsKey("landuse") || 
            tags.ContainsKey("natural") ||
            tags.ContainsKey("leisure") ||
            tags.ContainsKey("amenity"))
        {
            return isClosed ? OsmGeometryType.Polygon : OsmGeometryType.LineString;
        }
        
        // Line-indicating tags (even if closed)
        if (tags.ContainsKey("highway") || 
            tags.ContainsKey("railway") || 
            tags.ContainsKey("waterway"))
        {
            return OsmGeometryType.LineString;
        }
        
        // Explicit area tag
        if (tags.TryGetValue("area", out var area) && area == "yes")
        {
            return OsmGeometryType.Polygon;
        }
        
        return isClosed ? OsmGeometryType.Polygon : OsmGeometryType.LineString;
    }
}
```

#### 2.3 Update HTTP Client Headers

In `OverpassApiService`:

```csharp
// OLD
_httpClient.DefaultRequestHeaders.Accept.Add(
    new MediaTypeWithQualityHeaderValue("application/xml"));

// NEW
_httpClient.DefaultRequestHeaders.Accept.Add(
    new MediaTypeWithQualityHeaderValue("application/json"));
```

---

### Task 3: Update Service to Use GeoJSON Parser

**File**: `BeamNgTerrainPoc/Terrain/Osm/Services/OverpassApiService.cs`

```csharp
// Replace field
private readonly OsmXmlParser _parser;
// With
private readonly OsmGeoJsonParser _parser;

// In constructor
_parser = new OsmGeoJsonParser();
```

---

### Task 4: Handle Relation Geometries (Multi-Polygons)

Relations in OSM can represent multi-polygons (e.g., a forest with holes, or multiple disjoint areas).

**Changes to OsmFeature**:
```csharp
public class OsmFeature
{
    // ... existing properties ...
    
    /// <summary>
    /// For multi-polygons, stores additional rings/parts.
    /// The main Coordinates list is the outer ring.
    /// </summary>
    public List<List<GeoCoordinate>>? InnerRings { get; set; }
    
    /// <summary>
    /// For multi-part features (e.g., disjoint forest areas).
    /// </summary>
    public List<List<GeoCoordinate>>? Parts { get; set; }
}
```

**Update OsmGeometryProcessor.RasterizePolygonsToLayerMap**:
- Handle inner rings (holes) by clearing those pixels
- Handle multi-part polygons by rasterizing each part

---

### Task 5: Specific Query Optimization

Instead of querying ALL features in the bbox (which can be huge), query only relevant feature types:

```csharp
// Optimized query for terrain materials
private string BuildTerrainFeaturesQuery(GeoBoundingBox bbox)
{
    var bboxStr = FormatBBox(bbox);
    
    return $"""
        [out:json][timeout:{DefaultTimeoutSeconds}];
        (
          // Roads and paths
          way["highway"]{bboxStr};
          
          // Landuse (forest, grass, farmland, etc.)
          way["landuse"]{bboxStr};
          relation["landuse"]{bboxStr};
          
          // Natural features (wood, water, scrub, etc.)
          way["natural"]{bboxStr};
          relation["natural"]{bboxStr};
          
          // Water bodies
          way["water"]{bboxStr};
          relation["water"]{bboxStr};
          
          // Buildings (for urban materials)
          way["building"]{bboxStr};
        );
        out geom;
        """;
}
```

This significantly reduces response size and parsing time.

---

## Updated Execution Order

1. **Task 1**: Coordinate System Validation & Transformation
   - This MUST be done first as it's blocking the Overpass API calls
   - Add CRS detection to GeoTiffReader
   - Add transformation to WGS84 using GDAL

2. **Task 2**: Switch to GeoJSON Output
   - Create OsmGeoJsonParser
   - Update OverpassApiService queries
   - Update HTTP headers

3. **Task 3**: Update Service Integration
   - Replace XML parser with GeoJSON parser
   - Test with real GeoTIFF files

4. **Task 4**: Multi-Polygon Support
   - Update OsmFeature model
   - Update rasterization to handle rings

5. **Task 5**: Query Optimization
   - Implement focused queries
   - Add category-based query methods

---

## Testing Checklist

- [ ] GeoTIFF with WGS84 coordinates (should work directly)
- [ ] GeoTIFF with UTM coordinates (should transform to WGS84)
- [ ] GeoTIFF with unknown CRS (should warn user)
- [ ] Small bbox query (< 1km²)
- [ ] Medium bbox query (1-10 km²)
- [ ] Large bbox query (> 10 km²) - may need pagination
- [ ] Parse highway ways (LineStrings)
- [ ] Parse landuse polygons
- [ ] Parse multi-polygon relations (forest with holes)
- [ ] Rasterize polygons to layer map
- [ ] Convert roads to splines

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Large response size | Use focused queries (Task 5), implement pagination |
| Rate limiting | Use caching (already implemented), add retry with backoff |
| Complex multi-polygons | Start with simple polygons, add multi-polygon support later |
| CRS transformation errors | Validate transformed coordinates, fall back to manual bbox entry |

---

## Dependencies

No new NuGet packages needed. The existing dependencies are sufficient:
- `System.Text.Json` - For JSON parsing (built-in)
- GDAL - For CRS transformation (already used for GeoTIFF)

---

## Files Summary

### Files to Create
| Path | Purpose |
|------|---------|
| `Terrain/Osm/Parsing/OsmGeoJsonParser.cs` | Parse Overpass JSON responses |

### Files to Modify
| Path | Changes |
|------|---------|
| `Terrain/GeoTiff/GeoTiffReader.cs` | Add CRS detection and transformation |
| `Terrain/GeoTiff/GeoBoundingBox.cs` | Add WGS84 validation |
| `Terrain/Osm/Services/OverpassApiService.cs` | Switch to JSON output, use GeoJSON parser |
| `Terrain/Osm/Models/OsmFeature.cs` | Add multi-polygon support |
| `Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Handle multi-polygons in rasterization |

### Files to Remove
| Path | Reason |
|------|--------|
| `Terrain/Osm/Parsing/OsmXmlParser.cs` | Replaced by GeoJSON parser |

---

## Estimated Effort

- Task 1 (CRS): 2-3 hours
- Task 2 (GeoJSON): 2 hours
- Task 3 (Integration): 1 hour
- Task 4 (Multi-polygons): 2-3 hours
- Task 5 (Optimization): 1 hour
- Testing: 2-3 hours

**Total: ~1-1.5 days**
