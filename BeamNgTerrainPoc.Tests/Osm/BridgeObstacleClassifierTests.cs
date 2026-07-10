using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     V2 plan 0.4 — obstacle typing for the bridge rule system: per-feature classification
///     (Rail / Water / Road, with electrified + navigable refinement and the underground guard),
///     the spatial bucket, and the span-footprint crossing query.
/// </summary>
public class BridgeObstacleClassifierTests
{
    private static OsmFeature Feature(Dictionary<string, string> tags,
        OsmGeometryType geometry = OsmGeometryType.LineString)
    {
        return new OsmFeature
        {
            Id = 42,
            FeatureType = OsmFeatureType.Way,
            GeometryType = geometry,
            Coordinates = [new GeoCoordinate(3.0, 42.0), new GeoCoordinate(3.001, 42.0)],
            Tags = tags,
        };
    }

    // ---- classification --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("rail")]
    [InlineData("light_rail")]
    [InlineData("tram")]
    [InlineData("narrow_gauge")]
    public void Railway_ActiveTrack_IsRail(string value)
    {
        var kind = BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["railway"] = value }));
        Assert.Equal(BridgeObstacleKind.Rail, kind);
    }

    [Theory]
    [InlineData("abandoned")]
    [InlineData("razed")]
    [InlineData("platform")]
    [InlineData("station")]
    public void Railway_NonTrack_IsNotAnObstacle(string value)
    {
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["railway"] = value })));
    }

    [Theory]
    [InlineData("river")]
    [InlineData("canal")]
    [InlineData("stream")]
    public void Waterway_IsWater(string value)
    {
        var kind = BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["waterway"] = value }));
        Assert.Equal(BridgeObstacleKind.Water, kind);
    }

    [Fact]
    public void NaturalWater_Polygon_IsWater()
    {
        var kind = BridgeObstacleClassifier.ClassifyFeature(
            Feature(new() { ["natural"] = "water" }, OsmGeometryType.Polygon));
        Assert.Equal(BridgeObstacleKind.Water, kind);
    }

    [Fact]
    public void Highway_IsRoad()
    {
        Assert.Equal(BridgeObstacleKind.Road,
            BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["highway"] = "residential" })));
    }

    [Fact]
    public void UndergroundFeatures_NeverConstrainTheDeck()
    {
        // A buried subway / culverted stream must not force rail/water clearance above ground.
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(
            Feature(new() { ["railway"] = "subway", ["tunnel"] = "yes" })));
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(
            Feature(new() { ["waterway"] = "stream", ["tunnel"] = "culvert" })));

        var below = Feature(new() { ["railway"] = "rail", ["layer"] = "-1" });
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(below));
    }

    [Fact]
    public void LanduseAndBuildings_AreNotObstacles()
    {
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["landuse"] = "forest" })));
        Assert.Null(BridgeObstacleClassifier.ClassifyFeature(Feature(new() { ["building"] = "yes" })));
    }

    // ---- refinement ------------------------------------------------------------------------------------------

    [Fact]
    public void Electrified_ConservativeDefault_UnlessExplicitNo()
    {
        Assert.True(BridgeObstacleClassifier.IsElectrified(new Dictionary<string, string>()));
        Assert.True(BridgeObstacleClassifier.IsElectrified(
            new Dictionary<string, string> { ["electrified"] = "contact_line" }));
        Assert.False(BridgeObstacleClassifier.IsElectrified(
            new Dictionary<string, string> { ["electrified"] = "no" }));
    }

    [Theory]
    [InlineData("boat", "yes", true)]
    [InlineData("CEMT", "IV", true)]
    [InlineData("waterway", "canal", true)]
    [InlineData("width", "25", true)]   // ≥ 20 m threshold
    [InlineData("width", "12", false)]
    [InlineData("waterway", "stream", false)]
    public void Navigability_PerSpecSignals(string key, string value, bool expected)
    {
        var tags = new Dictionary<string, string> { [key] = value };
        Assert.Equal(expected, BridgeObstacleClassifier.IsNavigable(tags, navigableWidthMeters: 20f));
    }

    [Theory]
    [InlineData("12.5", 12.5f)]
    [InlineData("12,5", 12.5f)]
    [InlineData("8 m", 8f)]
    public void WidthParsing_ToleratesCommonFormats(string raw, float expected)
    {
        var parsed = BridgeObstacleClassifier.ParseWidthMeters(new Dictionary<string, string> { ["width"] = raw });
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value, 2);
    }

    // ---- spatial bucket + crossings --------------------------------------------------------------------------

    private static BridgeObstacleFeature Polyline(long id, BridgeObstacleKind kind, params Vector2[] pts)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in pts)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return new BridgeObstacleFeature
            { OsmId = id, Kind = kind, Points = pts, Min = min, Max = max };
    }

    [Fact]
    public void QueryAabb_ReturnsOnlyNearbyFeatures()
    {
        var near = Polyline(1, BridgeObstacleKind.Rail, new(90, 100), new(110, 100));
        var far = Polyline(2, BridgeObstacleKind.Water, new(5000, 5000), new(5100, 5000));
        var set = new BridgeObstacleSet([near, far]);

        var hits = set.QueryAabb(new Vector2(80, 80), new Vector2(120, 120)).ToList();

        Assert.Single(hits);
        Assert.Equal(1, hits[0].OsmId);
    }

    [Fact]
    public void FindCrossings_RailUnderFootprint_ReportsMidpointOfInsideRun()
    {
        // Footprint = axis-aligned rectangle x∈[95,105], y∈[80,120]; rail runs west→east through it at y=100.
        var rail = Polyline(1, BridgeObstacleKind.Rail, new(0, 100), new(200, 100));
        var set = new BridgeObstacleSet([rail]);

        bool Contains(Vector2 p) => p.X is >= 95f and <= 105f && p.Y is >= 80f and <= 120f;
        var crossings = BridgeObstacleClassifier.FindCrossings(
            set, new Vector2(95, 80), new Vector2(105, 120), Contains, sampleStepMeters: 1f);

        var c = Assert.Single(crossings);
        Assert.Equal(1, c.Feature.OsmId);
        Assert.InRange(c.CrossingPoint.X, 98f, 102f); // midpoint of the inside run ≈ 100
        Assert.Equal(100f, c.CrossingPoint.Y, 1f);
    }

    [Fact]
    public void FindCrossings_DeckFullyOverLakeInterior_CaughtByCenterContainment()
    {
        // Big lake polygon; footprint rectangle entirely inside it — no edge sample ever enters.
        var lake = new BridgeObstacleFeature
        {
            OsmId = 7,
            Kind = BridgeObstacleKind.Water,
            Navigable = true,
            IsPolygon = true,
            Points = new List<Vector2> { new(0, 0), new(400, 0), new(400, 400), new(0, 400) },
            Min = new Vector2(0, 0),
            Max = new Vector2(400, 400),
        };
        var set = new BridgeObstacleSet([lake]);

        bool Contains(Vector2 p) => p.X is >= 180f and <= 220f && p.Y is >= 150f and <= 250f;
        var crossings = BridgeObstacleClassifier.FindCrossings(
            set, new Vector2(180, 150), new Vector2(220, 250), Contains);

        var c = Assert.Single(crossings);
        Assert.Equal(7, c.Feature.OsmId);
        Assert.True(c.Feature.Navigable);
    }

    [Fact]
    public void FindCrossings_SpanOwnWayIds_AreIgnored_NoSelfObstacle()
    {
        // A bridge way is itself highway=* and lands in the obstacle set as a Road feature; its span
        // footprint always contains its own centerline. Without the ignore guard the bridge would report
        // ITSELF as an obstacle and the planner would raise the deck to clear its own deck.
        var ownWay = Polyline(1002, BridgeObstacleKind.Road, new(95, 100), new(105, 100));
        var realObstacle = Polyline(2002, BridgeObstacleKind.Rail, new(100, 0), new(100, 200));
        var set = new BridgeObstacleSet([ownWay, realObstacle]);

        bool Contains(Vector2 p) => p.X is >= 95f and <= 105f && p.Y is >= 80f and <= 120f;
        var crossings = BridgeObstacleClassifier.FindCrossings(
            set, new Vector2(95, 80), new Vector2(105, 120), Contains, sampleStepMeters: 1f,
            ignoreOsmWayIds: new HashSet<long> { 1002 });

        var c = Assert.Single(crossings); // only the rail survives; the span's own way is skipped
        Assert.Equal(2002, c.Feature.OsmId);
    }

    [Fact]
    public void FindCrossings_NothingUnderTheSpan_IsTerrain()
    {
        var rail = Polyline(1, BridgeObstacleKind.Rail, new(0, 500), new(200, 500));
        var set = new BridgeObstacleSet([rail]);

        bool Contains(Vector2 p) => p.X is >= 95f and <= 105f && p.Y is >= 80f and <= 120f;
        var crossings = BridgeObstacleClassifier.FindCrossings(
            set, new Vector2(95, 80), new Vector2(105, 120), Contains);

        Assert.Empty(crossings); // absence of crossings ⇒ valley/terrain bridge (spec R1)
    }
}
