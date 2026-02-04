using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
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
    /// <param name="json">The JSON content from Overpass API (with "out geom" output).</param>
    /// <param name="bbox">The bounding box that was queried.</param>
    /// <returns>Parsed query result with features.</returns>
    public OsmQueryResult Parse(string json, GeoBoundingBox bbox)
    {
        var result = new OsmQueryResult
        {
            BoundingBox = bbox,
            QueryTime = DateTime.UtcNow
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("elements", out var elements))
            {
                TerrainLogger.Warning("Overpass JSON response has no 'elements' property");
                return result;
            }

            int wayCount = 0;
            int relationCount = 0;

            foreach (var element in elements.EnumerateArray())
            {
                var feature = ParseElement(element);
                if (feature != null)
                {
                    result.Features.Add(feature);

                    if (feature.FeatureType == OsmFeatureType.Way)
                        wayCount++;
                    else if (feature.FeatureType == OsmFeatureType.Relation)
                        relationCount++;
                }
            }

            result.WayCount = wayCount;
            result.RelationCount = relationCount;

            TerrainLogger.Info($"Parsed Overpass JSON: {wayCount} ways, {relationCount} relations, {result.Features.Count} total features");
        }
        catch (JsonException ex)
        {
            TerrainLogger.Error($"Failed to parse Overpass JSON: {ex.Message}");
            throw;
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

        if (!element.TryGetProperty("id", out var idEl))
            return null;

        var id = idEl.GetInt64();

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
                if (point.TryGetProperty("lat", out var latEl) &&
                    point.TryGetProperty("lon", out var lonEl))
                {
                    var lat = latEl.GetDouble();
                    var lon = lonEl.GetDouble();
                    coordinates.Add(new GeoCoordinate(lon, lat));
                }
            }
        }
        else if (type == "relation" && element.TryGetProperty("members", out var membersEl))
        {
            // Relation: parse member geometries
            // For now, we extract coordinates from the first outer way member
            // Multi-polygon support will be added later
            var (outerCoords, innerRings, parts) = ParseRelationMembers(membersEl);
            coordinates = outerCoords;

            // Store additional geometry for multi-polygon support (future use)
            if (coordinates.Count >= 2)
            {
                var feature = new OsmFeature
                {
                    Id = id,
                    FeatureType = OsmFeatureType.Relation,
                    GeometryType = DetermineGeometryType(tags, coordinates, isRelation: true),
                    Coordinates = coordinates,
                    Tags = tags,
                    InnerRings = innerRings.Count > 0 ? innerRings : null,
                    Parts = parts.Count > 0 ? parts : null
                };
                return feature;
            }
        }

        if (coordinates.Count < 2)
            return null;

        // Determine geometry type from tags and closure
        var geometryType = DetermineGeometryType(tags, coordinates, isRelation: false);

        return new OsmFeature
        {
            Id = id,
            FeatureType = type == "way" ? OsmFeatureType.Way : OsmFeatureType.Relation,
            GeometryType = geometryType,
            Coordinates = coordinates,
            Tags = tags
        };
    }

    /// <summary>
    /// Parses relation members to extract geometry.
    /// Returns outer ring coordinates, inner rings (holes), and additional parts (disjoint polygons).
    /// 
    /// Handles the common case where multipolygon rings are split across multiple ways
    /// by assembling them into complete rings based on shared endpoints.
    /// </summary>
    private (List<GeoCoordinate> outer, List<List<GeoCoordinate>> inner, List<List<GeoCoordinate>> parts)
        ParseRelationMembers(JsonElement membersEl)
    {
        // Collect all way segments by role
        var outerSegments = new List<List<GeoCoordinate>>();
        var innerSegments = new List<List<GeoCoordinate>>();

        foreach (var member in membersEl.EnumerateArray())
        {
            if (!member.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "way")
                continue;

            if (!member.TryGetProperty("geometry", out var geomEl))
                continue;

            var role = member.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "";
            var coords = new List<GeoCoordinate>();

            foreach (var point in geomEl.EnumerateArray())
            {
                if (point.TryGetProperty("lat", out var latEl) &&
                    point.TryGetProperty("lon", out var lonEl))
                {
                    coords.Add(new GeoCoordinate(lonEl.GetDouble(), latEl.GetDouble()));
                }
            }

            if (coords.Count < 2)
                continue;

            if (role == "outer" || string.IsNullOrEmpty(role))
            {
                outerSegments.Add(coords);
            }
            else if (role == "inner")
            {
                innerSegments.Add(coords);
            }
        }

        // Assemble segments into complete rings
        var outerRings = AssembleRingsFromSegments(outerSegments);
        var innerRings = AssembleRingsFromSegments(innerSegments);

        // Log aggregate ring assembly results if any assembly occurred
        if (outerSegments.Count > 1 || innerSegments.Count > 1)
        {
            TerrainLogger.Detail($"Ring assembly: outer {outerSegments.Count} segments -> {outerRings.Count} rings, inner {innerSegments.Count} segments -> {innerRings.Count} rings");
        }

        // Return the first (largest) outer ring as the main polygon,
        // with additional outer rings as "parts" (for multi-polygon support)
        List<GeoCoordinate> mainOuter;
        var parts = new List<List<GeoCoordinate>>();

        if (outerRings.Count == 0)
        {
            mainOuter = new List<GeoCoordinate>();
        }
        else if (outerRings.Count == 1)
        {
            mainOuter = outerRings[0];
        }
        else
        {
            // Multiple outer rings - use the largest as main, others as parts
            // Sort by number of points (approximation of area)
            outerRings.Sort((a, b) => b.Count.CompareTo(a.Count));
            mainOuter = outerRings[0];
            parts.AddRange(outerRings.Skip(1));
        }

        return (mainOuter, innerRings, parts);
    }

    /// <summary>
    /// Assembles way segments into complete rings by joining segments that share endpoints.
    /// This is necessary because OSM multipolygon relations often have their rings
    /// split across multiple ways at intersections or administrative boundaries.
    /// </summary>
    /// <param name="segments">List of way segments to assemble.</param>
    /// <returns>List of assembled rings (closed or open polylines).</returns>
    private List<List<GeoCoordinate>> AssembleRingsFromSegments(List<List<GeoCoordinate>> segments)
    {
        if (segments.Count == 0)
            return new List<List<GeoCoordinate>>();

        if (segments.Count == 1)
            return new List<List<GeoCoordinate>> { segments[0] };

        // Tolerance for coordinate matching (approximately 0.1 meters at equator)
        const double tolerance = 0.000001;

        // Make copies to avoid modifying originals
        var remaining = segments.Select(s => new List<GeoCoordinate>(s)).ToList();
        var result = new List<List<GeoCoordinate>>();

        while (remaining.Count > 0)
        {
            // Start a new ring with the first remaining segment
            var ring = new List<GeoCoordinate>(remaining[0]);
            remaining.RemoveAt(0);

            // Keep trying to extend the ring until no more matches
            bool didExtend;
            int iterations = 0;
            do
            {
                didExtend = false;
                iterations++;

                var ringStart = ring[0];
                var ringEnd = ring[^1];

                for (int i = 0; i < remaining.Count; i++)
                {
                    var segment = remaining[i];
                    var segStart = segment[0];
                    var segEnd = segment[^1];

                    // Check if segment connects to ring end
                    if (CoordinatesMatch(ringEnd, segStart, tolerance))
                    {
                        // Append segment (skip first point which is duplicate)
                        ring.AddRange(segment.Skip(1));
                        remaining.RemoveAt(i);
                        didExtend = true;
                        break;
                    }
                    else if (CoordinatesMatch(ringEnd, segEnd, tolerance))
                    {
                        // Append reversed segment (skip last point which is duplicate)
                        for (int j = segment.Count - 2; j >= 0; j--)
                            ring.Add(segment[j]);
                        remaining.RemoveAt(i);
                        didExtend = true;
                        break;
                    }
                    // Check if segment connects to ring start
                    else if (CoordinatesMatch(ringStart, segEnd, tolerance))
                    {
                        // Prepend segment (skip last point which is duplicate)
                        var newRing = new List<GeoCoordinate>(segment.Take(segment.Count - 1));
                        newRing.AddRange(ring);
                        ring = newRing;
                        remaining.RemoveAt(i);
                        didExtend = true;
                        break;
                    }
                    else if (CoordinatesMatch(ringStart, segStart, tolerance))
                    {
                        // Prepend reversed segment (skip first point which is duplicate)
                        var newRing = new List<GeoCoordinate>();
                        for (int j = segment.Count - 1; j >= 1; j--)
                            newRing.Add(segment[j]);
                        newRing.AddRange(ring);
                        ring = newRing;
                        remaining.RemoveAt(i);
                        didExtend = true;
                        break;
                    }
                }
            } while (didExtend && remaining.Count > 0 && iterations < 1000);

            if (ring.Count >= 3)
            {
                result.Add(ring);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if two coordinates are effectively the same point.
    /// </summary>
    private static bool CoordinatesMatch(GeoCoordinate a, GeoCoordinate b, double tolerance)
    {
        return Math.Abs(a.Longitude - b.Longitude) < tolerance &&
               Math.Abs(a.Latitude - b.Latitude) < tolerance;
    }

    /// <summary>
    /// Determines the geometry type based on OSM tags and coordinate closure.
    /// </summary>
    /// <param name="tags">The OSM tags for the feature.</param>
    /// <param name="coordinates">The coordinates of the feature.</param>
    /// <param name="isRelation">True if this is a relation element (affects geometry type detection).</param>
    /// <returns>The determined geometry type.</returns>
    private OsmGeometryType DetermineGeometryType(
        Dictionary<string, string> tags,
        List<GeoCoordinate> coordinates,
        bool isRelation = false)
    {
        // Check for multipolygon or boundary relations - these are always polygons
        if (isRelation && tags.TryGetValue("type", out var relationType))
        {
            if (relationType == "multipolygon" || relationType == "boundary")
            {
                return OsmGeometryType.Polygon;
            }
        }

        // Check if closed (first point equals last point)
        var isClosed = coordinates.Count > 2 &&
            Math.Abs(coordinates[0].Longitude - coordinates[^1].Longitude) < 0.0000001 &&
            Math.Abs(coordinates[0].Latitude - coordinates[^1].Latitude) < 0.0000001;

        // Check for explicit area=yes/no tag
        if (tags.TryGetValue("area", out var areaValue))
        {
            if (areaValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
                return OsmGeometryType.Polygon;
            if (areaValue.Equals("no", StringComparison.OrdinalIgnoreCase))
                return OsmGeometryType.LineString;
        }

        // Area-indicating tags - these are polygons even if not perfectly closed
        // (relation rings might have small gaps due to floating point)
        if (tags.ContainsKey("building") ||
            tags.ContainsKey("landuse") ||
            tags.ContainsKey("leisure") ||
            tags.ContainsKey("amenity") ||
            tags.ContainsKey("boundary"))
        {
            // For relations with area-indicating tags, treat as polygon
            // even if not perfectly closed (assembly tolerance may cause gaps)
            if (isRelation || isClosed)
                return OsmGeometryType.Polygon;
            return OsmGeometryType.LineString;
        }

        // Natural features - some are areas, some are lines
        if (tags.TryGetValue("natural", out var naturalValue))
        {
            // coastline is always a line
            if (naturalValue == "coastline")
                return OsmGeometryType.LineString;

            // Most other natural features are areas when closed or when part of a relation
            if (isRelation || isClosed)
                return OsmGeometryType.Polygon;
            return OsmGeometryType.LineString;
        }

        // Waterway tag - rivers/streams are lines, but water bodies can be polygons
        if (tags.TryGetValue("waterway", out var waterwayValue))
        {
            // Riverbanks are polygons
            if (waterwayValue == "riverbank")
                return OsmGeometryType.Polygon;
            return OsmGeometryType.LineString;
        }

        // Line-indicating tags (even if closed)
        if (tags.ContainsKey("highway") ||
            tags.ContainsKey("railway"))
        {
            return OsmGeometryType.LineString;
        }

        // Default: closed = polygon, open = line
        return isClosed ? OsmGeometryType.Polygon : OsmGeometryType.LineString;
    }
}
