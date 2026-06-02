using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Services;

namespace BeamNgTerrainPoc.Tests.Osm;

public class OsmQueryCacheTests : IDisposable
{
    private readonly string _cacheDirectory;

    public OsmQueryCacheTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "BeamNgTerrainPocTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheDirectory);
    }

    [Fact]
    public async Task GetAsync_ReturnsCachedResult_WhenRequestedBboxIsCoveredByMultipleCachedChunks()
    {
        var cache = new OsmQueryCache(_cacheDirectory, TimeSpan.FromDays(7));
        var chunks = new[]
        {
            CreateResult(1, new GeoBoundingBox(0.0, 0.0, 2.0, 2.0), 1.75, 1.75),
            CreateResult(2, new GeoBoundingBox(2.0, 0.0, 4.0, 2.0), 2.25, 1.75),
            CreateResult(3, new GeoBoundingBox(0.0, 2.0, 2.0, 4.0), 1.75, 2.25),
            CreateResult(4, new GeoBoundingBox(2.0, 2.0, 4.0, 4.0), 2.25, 2.25)
        };

        foreach (var chunk in chunks)
            await cache.SetAsync(chunk.BoundingBox, chunk);

        cache.ClearMemoryCache();
        cache = new OsmQueryCache(_cacheDirectory, TimeSpan.FromDays(7));

        var requestedBbox = new GeoBoundingBox(1.5, 1.5, 2.5, 2.5);
        var result = await cache.GetAsync(requestedBbox);

        Assert.NotNull(result);
        Assert.True(result.IsFromCache);
        Assert.Equal(4, result.Features.Count);
        Assert.Single(result.RouteRelations);
        Assert.Equal([1, 2, 3, 4], result.Features.Select(feature => feature.Id).Order().ToArray());
        Assert.Equal(requestedBbox.MinLongitude, result.BoundingBox.MinLongitude);
        Assert.Equal(requestedBbox.MinLatitude, result.BoundingBox.MinLatitude);
        Assert.Equal(requestedBbox.MaxLongitude, result.BoundingBox.MaxLongitude);
        Assert.Equal(requestedBbox.MaxLatitude, result.BoundingBox.MaxLatitude);
    }

    private static OsmQueryResult CreateResult(long id, GeoBoundingBox bbox, double longitude, double latitude)
    {
        return new OsmQueryResult
        {
            BoundingBox = bbox,
            Features =
            [
                new OsmFeature
                {
                    Id = id,
                    FeatureType = OsmFeatureType.Way,
                    GeometryType = OsmGeometryType.LineString,
                    Coordinates =
                    [
                        new GeoCoordinate(longitude - 0.01, latitude),
                        new GeoCoordinate(longitude + 0.01, latitude)
                    ],
                    Tags = new Dictionary<string, string>
                    {
                        ["highway"] = "residential"
                    }
                }
            ],
            RouteRelations =
            [
                new RouteRelation
                {
                    RelationId = 10,
                    Tags = new Dictionary<string, string> { ["route"] = "road" },
                    Members = [new RouteRelationMember { WayId = id }]
                }
            ],
            QueryTime = DateTime.UtcNow,
            IsFromCache = false,
            NodeCount = 2,
            WayCount = 1,
            RelationCount = 0
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }
}
