using System.Drawing;
using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Parses OSM building features into <see cref="BuildingData"/> objects.
///
/// Responsibilities:
/// - Extracts building polygon features from <see cref="OsmQueryResult"/>
/// - Parses OSM tags (height, levels, material, roof:*, building:colour, etc.)
/// - Converts WGS84 polygon coordinates to local meter-space footprints (origin at centroid)
/// - Computes world position (centroid) and ground elevation via terrain heightmap sampling
///
/// Coordinate pipeline:
///   WGS84 (lon/lat) → terrain pixels (via OsmGeometryProcessor) → meters (× metersPerPixel)
///   → local footprint (subtract centroid) + WorldPosition (centroid in BeamNG world space)
/// </summary>
public class OsmBuildingParser
{
    private readonly OsmGeometryProcessor _geometryProcessor;

    public OsmBuildingParser(OsmGeometryProcessor geometryProcessor)
    {
        _geometryProcessor = geometryProcessor;
    }

    /// <summary>
    /// Parses all building polygon features from an OSM query result.
    /// </summary>
    /// <param name="queryResult">The OSM query result containing building features.</param>
    /// <param name="bbox">Geographic bounding box for coordinate transformation.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per terrain pixel).</param>
    /// <param name="heightSampler">Optional callback to sample terrain height at a world position (X, Y in meters).
    /// Returns ground elevation in meters. If null, GroundElevation defaults to 0.</param>
    /// <returns>List of parsed building data objects.</returns>
    public List<BuildingData> ParseBuildings(
        OsmQueryResult queryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler = null)
    {
        var buildingFeatures = queryResult.GetFeaturesByCategory("building")
            .Where(f => f.GeometryType == OsmGeometryType.Polygon)
            .ToList();

        return ParseBuildingFeatures(buildingFeatures, bbox, terrainSize, metersPerPixel, heightSampler);
    }

    /// <summary>
    /// Parses a pre-filtered list of building polygon features.
    /// Use this overload when the user has selected specific building features via the UI.
    /// </summary>
    /// <param name="buildingFeatures">Pre-filtered list of building polygon features.</param>
    /// <param name="bbox">Geographic bounding box for coordinate transformation.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per terrain pixel).</param>
    /// <param name="heightSampler">Optional callback to sample terrain height at a world position (X, Y in meters).
    /// Returns ground elevation in meters. If null, GroundElevation defaults to 0.</param>
    /// <returns>List of parsed building data objects.</returns>
    public List<BuildingData> ParseBuildingFeatures(
        List<OsmFeature> buildingFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler = null)
    {
        Console.WriteLine($"OsmBuildingParser: Found {buildingFeatures.Count} building polygon features");

        var buildings = new List<BuildingData>();
        int skippedTooSmall = 0;
        int skippedDegenerate = 0;

        foreach (var feature in buildingFeatures)
        {
            var building = ParseSingleBuilding(feature, bbox, terrainSize, metersPerPixel, heightSampler);
            if (building == null)
            {
                skippedDegenerate++;
                continue;
            }

            // Skip very small buildings (< 4 m² footprint area)
            float area = ComputePolygonArea(building.FootprintOuter);
            if (area < 4.0f)
            {
                skippedTooSmall++;
                continue;
            }

            buildings.Add(building);
        }

        Console.WriteLine($"OsmBuildingParser: Parsed {buildings.Count} buildings " +
            $"(skipped {skippedDegenerate} degenerate, {skippedTooSmall} too small)");

        return buildings;
    }

    /// <summary>
    /// Parses a single OSM building feature into a <see cref="BuildingData"/>.
    /// Returns null if the feature has degenerate geometry (< 3 unique vertices).
    /// </summary>
    private BuildingData? ParseSingleBuilding(
        OsmFeature feature,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler)
    {
        // Transform outer ring: WGS84 → terrain pixels → meters
        var outerMeterCoords = TransformRingToMeters(feature.Coordinates, bbox, terrainSize, metersPerPixel);
        if (outerMeterCoords.Count < 3)
            return null;

        // Compute centroid in world meter-space
        var centroid = ComputeCentroid(outerMeterCoords);

        // Convert to local coordinates (origin at centroid)
        var localOuter = outerMeterCoords.Select(p => new Vector2(p.X - centroid.X, p.Y - centroid.Y)).ToList();

        // Ensure CCW winding for outer ring
        if (!IsCounterClockwise(localOuter))
            localOuter.Reverse();

        // Close the ring if not already closed
        localOuter = EnsureClosed(localOuter);

        // Process inner rings (holes/courtyards)
        List<List<Vector2>>? localHoles = null;
        if (feature.InnerRings != null && feature.InnerRings.Count > 0)
        {
            localHoles = new List<List<Vector2>>();
            foreach (var innerRing in feature.InnerRings)
            {
                var innerMeterCoords = TransformRingToMeters(innerRing, bbox, terrainSize, metersPerPixel);
                if (innerMeterCoords.Count < 3)
                    continue;

                var localInner = innerMeterCoords
                    .Select(p => new Vector2(p.X - centroid.X, p.Y - centroid.Y))
                    .ToList();

                // Ensure CW winding for inner rings (holes)
                if (IsCounterClockwise(localInner))
                    localInner.Reverse();

                localInner = EnsureClosed(localInner);
                localHoles.Add(localInner);
            }

            if (localHoles.Count == 0)
                localHoles = null;
        }

        // Sample ground elevation at the lowest footprint vertex.
        // Using the minimum ensures the building floor is never buried in the terrain
        // on sloped ground (it may float slightly on the high side, which looks better).
        float groundElevation = 0f;
        if (heightSampler != null)
        {
            groundElevation = outerMeterCoords.Min(p => heightSampler(p.X, p.Y));
        }

        // Parse OSM tags
        var tags = feature.Tags;
        string buildingType = GetTagValue(tags, "building") ?? "yes";

        // Get defaults for this building type
        string? roofShapeTag = GetTagValue(tags, "roof:shape");
        var defaults = BuildingDefaults.GetDefaultsFor(buildingType, roofShapeTag);

        // Parse height (explicit tags override defaults)
        float height = defaults.Levels * defaults.HeightPerLevel;
        int levels = defaults.Levels;
        float heightPerLevel = defaults.HeightPerLevel;

        if (TryParseMeters(GetTagValue(tags, "height"), out float parsedHeight))
        {
            height = parsedHeight;
        }
        else if (TryParseMeters(GetTagValue(tags, "building:height"), out float parsedBuildingHeight))
        {
            height = parsedBuildingHeight;
        }

        if (int.TryParse(GetTagValue(tags, "building:levels"), out int parsedLevels) && parsedLevels > 0)
        {
            levels = parsedLevels;
            // Recalculate height if no explicit height tag
            if (GetTagValue(tags, "height") == null && GetTagValue(tags, "building:height") == null)
            {
                height = levels * heightPerLevel;
            }
        }

        // Parse min_height (for floating building parts)
        float minHeight = 0f;
        if (TryParseMeters(GetTagValue(tags, "min_height"), out float parsedMinHeight))
        {
            minHeight = parsedMinHeight;
        }
        else if (TryParseMeters(GetTagValue(tags, "building:min_height"), out float parsedBuildingMinHeight))
        {
            minHeight = parsedBuildingMinHeight;
        }

        // Parse roof height
        float roofHeight = 0f;
        if (TryParseMeters(GetTagValue(tags, "roof:height"), out float parsedRoofHeight))
        {
            roofHeight = parsedRoofHeight;
        }

        // Parse materials (explicit tags override defaults)
        string wallMaterial = BuildingMaterialLibrary.MapOsmWallMaterial(
            GetTagValue(tags, "building:material") ?? GetTagValue(tags, "building:facade:material"));
        if (wallMaterial == "BUILDING_DEFAULT" && defaults.WallMaterial != "BUILDING_DEFAULT")
        {
            wallMaterial = defaults.WallMaterial;
        }

        string roofMaterial = BuildingMaterialLibrary.MapOsmRoofMaterial(GetTagValue(tags, "roof:material"));
        if (roofMaterial == "ROOF_DEFAULT")
        {
            roofMaterial = defaults.RoofMaterial;
        }

        // Parse colours
        Color? wallColor = ParseOsmColor(GetTagValue(tags, "building:colour"));
        Color? roofColor = ParseOsmColor(GetTagValue(tags, "roof:colour"));

        return new BuildingData
        {
            OsmId = feature.Id,
            BuildingType = buildingType,
            FootprintOuter = localOuter,
            FootprintHoles = localHoles,
            Height = height,
            MinHeight = minHeight,
            Levels = levels,
            HeightPerLevel = heightPerLevel,
            RoofShape = defaults.RoofShape,
            RoofHeight = roofHeight,
            WallMaterial = wallMaterial,
            RoofMaterial = roofMaterial,
            WallColor = wallColor,
            RoofColor = roofColor,
            HasWindows = defaults.HasWindows,
            HasWalls = defaults.HasWalls,
            GroundElevation = groundElevation,
            WorldPosition = new Vector3(centroid.X, centroid.Y, groundElevation)
        };
    }

    /// <summary>
    /// Transforms a ring of geographic coordinates to meter coordinates in terrain space.
    /// Removes duplicate consecutive points.
    /// </summary>
    private List<Vector2> TransformRingToMeters(
        List<GeoCoordinate> geoCoords,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        // Transform to terrain-space pixels (bottom-left origin, Y increases northward)
        var pixelCoords = _geometryProcessor.TransformToTerrainCoordinates(geoCoords, bbox, terrainSize);

        // Convert pixels to meters
        var meterCoords = pixelCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();

        // Remove duplicate consecutive points (tolerance 0.01m)
        return RemoveDuplicateConsecutive(meterCoords, 0.01f);
    }

    /// <summary>
    /// Computes the centroid (average position) of a polygon.
    /// </summary>
    private static Vector2 ComputeCentroid(List<Vector2> polygon)
    {
        if (polygon.Count == 0)
            return Vector2.Zero;

        float sumX = 0, sumY = 0;
        // Exclude last point if ring is closed (first == last)
        int count = polygon.Count;
        if (count > 1 && Vector2.DistanceSquared(polygon[0], polygon[^1]) < 0.0001f)
            count--;

        for (int i = 0; i < count; i++)
        {
            sumX += polygon[i].X;
            sumY += polygon[i].Y;
        }

        return new Vector2(sumX / count, sumY / count);
    }

    /// <summary>
    /// Computes the signed area of a polygon (positive = CCW, negative = CW).
    /// Uses the shoelace formula.
    /// </summary>
    private static float ComputeSignedArea(List<Vector2> polygon)
    {
        float area = 0;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % n];
            area += (p2.X - p1.X) * (p2.Y + p1.Y);
        }
        return area / 2f;
    }

    /// <summary>
    /// Computes the absolute area of a polygon.
    /// </summary>
    private static float ComputePolygonArea(List<Vector2> polygon)
    {
        return MathF.Abs(ComputeSignedArea(polygon));
    }

    /// <summary>
    /// Checks if a polygon has counter-clockwise winding order.
    /// </summary>
    private static bool IsCounterClockwise(List<Vector2> polygon)
    {
        // In a standard math coordinate system (Y up), negative signed area = CCW
        // The shoelace formula as implemented uses (p2.X - p1.X) * (p2.Y + p1.Y)
        // which gives negative area for CCW polygons
        return ComputeSignedArea(polygon) < 0;
    }

    /// <summary>
    /// Ensures a polygon ring is closed (first point == last point).
    /// </summary>
    private static List<Vector2> EnsureClosed(List<Vector2> ring)
    {
        if (ring.Count < 2)
            return ring;

        if (Vector2.DistanceSquared(ring[0], ring[^1]) > 0.0001f)
        {
            ring = new List<Vector2>(ring) { ring[0] };
        }

        return ring;
    }

    /// <summary>
    /// Removes consecutive duplicate points within a tolerance.
    /// </summary>
    private static List<Vector2> RemoveDuplicateConsecutive(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;

        var result = new List<Vector2> { points[0] };
        float toleranceSq = tolerance * tolerance;

        for (int i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(result[^1], points[i]) > toleranceSq)
                result.Add(points[i]);
        }

        return result;
    }

    /// <summary>
    /// Gets a tag value, returning null if not present or empty.
    /// </summary>
    private static string? GetTagValue(Dictionary<string, string> tags, string key)
    {
        if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    /// <summary>
    /// Attempts to parse a meters value from an OSM tag.
    /// Supports formats: "10", "10.5", "10 m", "10m", "33 ft", "33ft", "33'".
    /// </summary>
    private static bool TryParseMeters(string? value, out float meters)
    {
        meters = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        // Check for feet suffix
        bool isFeet = false;
        if (value.EndsWith("ft", StringComparison.OrdinalIgnoreCase))
        {
            isFeet = true;
            value = value[..^2].Trim();
        }
        else if (value.EndsWith("'"))
        {
            isFeet = true;
            value = value[..^1].Trim();
        }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^1].Trim();
        }

        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            meters = isFeet ? parsed * 0.3048f : parsed;
            return meters > 0;
        }

        return false;
    }

    /// <summary>
    /// Parses an OSM color tag value to a <see cref="Color"/>.
    /// Supports named colors (red, brown, grey, etc.) and hex (#RRGGBB, #RGB).
    /// </summary>
    private static Color? ParseOsmColor(string? colorTag)
    {
        if (string.IsNullOrWhiteSpace(colorTag))
            return null;

        colorTag = colorTag.Trim().ToLowerInvariant();

        // Named colors commonly used in OSM
        var color = colorTag switch
        {
            "white" => Color.FromArgb(255, 255, 255),
            "black" => Color.FromArgb(30, 30, 30),
            "grey" or "gray" => Color.FromArgb(160, 160, 160),
            "red" => Color.FromArgb(180, 60, 50),
            "brown" => Color.FromArgb(140, 90, 50),
            "yellow" => Color.FromArgb(220, 200, 80),
            "green" => Color.FromArgb(80, 140, 60),
            "blue" => Color.FromArgb(60, 100, 180),
            "orange" => Color.FromArgb(220, 140, 40),
            "beige" => Color.FromArgb(220, 210, 180),
            "cream" => Color.FromArgb(240, 230, 200),
            "tan" => Color.FromArgb(200, 180, 140),
            "pink" => Color.FromArgb(220, 170, 170),
            "maroon" => Color.FromArgb(120, 40, 40),
            _ => (Color?)null
        };

        if (color.HasValue)
            return color.Value;

        // Try hex color
        if (colorTag.StartsWith('#'))
        {
            var hex = colorTag[1..];
            if (hex.Length == 3)
            {
                // #RGB → #RRGGBB
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }

            if (hex.Length == 6 &&
                int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out int r) &&
                int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g) &&
                int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
            {
                return Color.FromArgb(r, g, b);
            }
        }

        return null;
    }
}
