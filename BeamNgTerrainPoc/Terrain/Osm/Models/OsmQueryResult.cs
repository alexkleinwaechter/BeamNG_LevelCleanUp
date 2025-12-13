using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents the result of an Overpass API query.
/// </summary>
public class OsmQueryResult
{
    /// <summary>
    /// The bounding box that was queried.
    /// </summary>
    public GeoBoundingBox BoundingBox { get; set; } = null!;
    
    /// <summary>
    /// List of processed features with resolved geometries.
    /// </summary>
    public List<OsmFeature> Features { get; set; } = new();
    
    /// <summary>
    /// When the query was executed.
    /// </summary>
    public DateTime QueryTime { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether this result was loaded from cache.
    /// </summary>
    public bool IsFromCache { get; set; }
    
    /// <summary>
    /// Raw node count from the OSM response.
    /// </summary>
    public int NodeCount { get; set; }
    
    /// <summary>
    /// Raw way count from the OSM response.
    /// </summary>
    public int WayCount { get; set; }
    
    /// <summary>
    /// Raw relation count from the OSM response.
    /// </summary>
    public int RelationCount { get; set; }
    
    /// <summary>
    /// Gets features filtered by geometry type.
    /// </summary>
    public IEnumerable<OsmFeature> GetFeaturesByGeometry(OsmGeometryType geometryType)
    {
        return Features.Where(f => f.GeometryType == geometryType);
    }
    
    /// <summary>
    /// Gets features filtered by category.
    /// </summary>
    public IEnumerable<OsmFeature> GetFeaturesByCategory(string category)
    {
        return Features.Where(f => f.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Gets all line features (typically roads, rivers, etc.).
    /// </summary>
    public IEnumerable<OsmFeature> Lines => GetFeaturesByGeometry(OsmGeometryType.LineString);
    
    /// <summary>
    /// Gets all polygon features (typically landuse, buildings, etc.).
    /// </summary>
    public IEnumerable<OsmFeature> Polygons => GetFeaturesByGeometry(OsmGeometryType.Polygon);
    
    /// <summary>
    /// Gets all point features.
    /// </summary>
    public IEnumerable<OsmFeature> Points => GetFeaturesByGeometry(OsmGeometryType.Point);
    
    /// <summary>
    /// Gets distinct categories present in the result.
    /// </summary>
    public IEnumerable<string> Categories => Features.Select(f => f.Category).Distinct().OrderBy(c => c);
    
    public override string ToString()
    {
        return $"OsmQueryResult: {Features.Count} features ({Lines.Count()} lines, {Polygons.Count()} polygons, {Points.Count()} points)";
    }
}
