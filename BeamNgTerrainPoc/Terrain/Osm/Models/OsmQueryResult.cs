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
    
    /// <summary>
    /// Gets all features that are part of a roundabout (junction=roundabout tag).
    /// These features need special handling as they are often split at connecting roads.
    /// </summary>
    public IEnumerable<OsmFeature> RoundaboutFeatures => 
        Features.Where(f => f.IsRoundabout && f.GeometryType == OsmGeometryType.LineString);
    
    /// <summary>
    /// Gets all highway features excluding roundabout segments.
    /// Use this when processing regular roads to avoid double-processing roundabouts.
    /// </summary>
    public IEnumerable<OsmFeature> NonRoundaboutHighways => 
        Features.Where(f => f.Category == "highway" && 
                           f.GeometryType == OsmGeometryType.LineString && 
                           !f.IsRoundabout);
    
    /// <summary>
    /// Merges multiple chunk results into a single result, deduplicating features by (type, id).
    /// Overpass <c>out geom</c> returns complete way geometry in every chunk that intersects it,
    /// so we keep the first occurrence and skip duplicates.
    /// </summary>
    /// <param name="chunkResults">Chunk results to merge.</param>
    /// <param name="fullBbox">The original full bounding box that was split into chunks.</param>
    /// <returns>A merged result with deduplicated features.</returns>
    public static OsmQueryResult MergeChunks(IReadOnlyList<OsmQueryResult> chunkResults, GeoBoundingBox fullBbox)
    {
        var seen = new HashSet<(string type, long id)>();
        var mergedFeatures = new List<OsmFeature>();
        int totalNodes = 0, totalWays = 0, totalRelations = 0;

        foreach (var chunk in chunkResults)
        {
            totalNodes += chunk.NodeCount;
            totalWays += chunk.WayCount;
            totalRelations += chunk.RelationCount;

            foreach (var feature in chunk.Features)
            {
                if (seen.Add((feature.FeatureType.ToString(), feature.Id)))
                    mergedFeatures.Add(feature);
            }
        }

        return new OsmQueryResult
        {
            BoundingBox = fullBbox,
            Features = mergedFeatures,
            QueryTime = DateTime.UtcNow,
            IsFromCache = false,
            NodeCount = totalNodes,
            WayCount = totalWays,
            RelationCount = totalRelations
        };
    }

    public override string ToString()
    {
        var roundaboutCount = RoundaboutFeatures.Count();
        var roundaboutInfo = roundaboutCount > 0 ? $", {roundaboutCount} roundabout segments" : "";
        return $"OsmQueryResult: {Features.Count} features ({Lines.Count()} lines, {Polygons.Count()} polygons, {Points.Count()} points{roundaboutInfo})";
    }
}
