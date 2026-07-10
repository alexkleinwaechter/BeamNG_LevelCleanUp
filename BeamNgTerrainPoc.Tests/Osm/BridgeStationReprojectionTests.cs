using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     V2 plan 0.3a — bridge station re-projection. The legacy span anchor sums arc-length over the
///     PRE-Chaikin path while the spline is built from POST-Chaikin points, so corners upstream of the
///     bridge shift the span ("station drift", docs 11/13). With
///     <c>BridgeRuleSystemOptions.EnableBridgeStationReprojection</c> the span is anchored by projecting
///     the bridge way's ORIGINAL endpoint coordinates onto the final merged spline instead.
/// </summary>
public class BridgeStationReprojectionTests
{
    private static OsmFeature Road(long id, List<GeoCoordinate> coords, List<long> nodeIds,
        bool isBridge = false, int layer = 0)
    {
        var tags = new Dictionary<string, string> { ["highway"] = "primary" };
        if (isBridge) { tags["bridge"] = "yes"; tags["layer"] = layer.ToString(); }
        return new OsmFeature
        {
            Id = id,
            FeatureType = OsmFeatureType.Way,
            GeometryType = OsmGeometryType.LineString,
            Coordinates = coords,
            NodeIds = nodeIds,
            Tags = tags
        };
    }

    /// <summary>
    ///     road1 zigzags (two 90° corners) before the bridge, so Chaikin corner-cutting SHORTENS the
    ///     corridor upstream of the span and the pre-Chaikin arc-length sums overshoot. Bridge + road2
    ///     continue straight east (zero deflection at the shared nodes, so merging is unaffected).
    /// </summary>
    private static (GeoBoundingBox bbox, List<OsmFeature> features) ZigzagScenario()
    {
        var bbox = new GeoBoundingBox(
            new GeoCoordinate(2.999, 42.401),
            new GeoCoordinate(3.006, 42.405));

        var road1 = Road(2001,
        [
            new(3.000, 42.402), new(3.001, 42.402),      // east ~82 m
            new(3.001, 42.4025),                          // north ~56 m (corner 1)
            new(3.002, 42.4025),                          // east ~82 m (corner 2)
        ], [200, 201, 202, 203]);
        var bridge = Road(2002,
            [new(3.002, 42.4025), new(3.0025, 42.4025), new(3.003, 42.4025)], [203, 204, 205],
            isBridge: true, layer: 1);
        var road2 = Road(2003,
            [new(3.003, 42.4025), new(3.0035, 42.4025), new(3.004, 42.4025)], [205, 206, 207]);

        return (bbox, [road1, bridge, road2]);
    }

    private static StructureSegment MergedBridgeSpan(List<RoadSpline> splines, out RoadSpline corridor)
    {
        corridor = Assert.Single(splines, s =>
            s.StructureSegments != null && s.StructureSegments.Any(seg => seg.IsBridge));
        return Assert.Single(corridor.StructureSegments!, s => s.IsBridge);
    }

    [Fact]
    public void Reprojection_AnchorsSpanAtOriginalAbutments_BetterThanLegacy()
    {
        var (bbox, features) = ZigzagScenario();
        var processor = new OsmGeometryProcessor();

        var legacySplines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 1024, metersPerPixel: 1f,
            excludeBridges: true, mergeStructuresIntoCorridor: true,
            reprojectStructureStations: false);
        var reprojSplines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 1024, metersPerPixel: 1f,
            excludeBridges: true, mergeStructuresIntoCorridor: true,
            reprojectStructureStations: true);

        var legacySpan = MergedBridgeSpan(legacySplines, out var legacyCorridor);
        var reprojSpan = MergedBridgeSpan(reprojSplines, out var reprojCorridor);

        // Original abutment coordinates were captured at seeding and survive the merge.
        Assert.NotNull(reprojSpan.OriginalStartPoint);
        Assert.NotNull(reprojSpan.OriginalEndPoint);

        // The reprojected stations land the span AT the original abutment coordinates.
        var startErr = Vector2.Distance(
            reprojCorridor.GetPointAtDistance(reprojSpan.StartDistance), reprojSpan.OriginalStartPoint!.Value);
        var endErr = Vector2.Distance(
            reprojCorridor.GetPointAtDistance(reprojSpan.EndDistance), reprojSpan.OriginalEndPoint!.Value);
        Assert.True(startErr < 3f, $"reprojected start {startErr:F1}m off the original abutment");
        Assert.True(endErr < 3f, $"reprojected end {endErr:F1}m off the original abutment");

        // The legacy pre-Chaikin sums overshoot past the zigzag — strictly worse at the start abutment.
        var legacyStartErr = Vector2.Distance(
            legacyCorridor.GetPointAtDistance(legacySpan.StartDistance), reprojSpan.OriginalStartPoint.Value);
        Assert.True(legacyStartErr > startErr + 1f,
            $"expected legacy drift ({legacyStartErr:F1}m) to exceed reprojected error ({startErr:F1}m) — " +
            "if this fails the scenario no longer provokes Chaikin shortening");

        // Span length ≈ the chord between the original abutments (the bridge way is straight here);
        // scale-independent so it doesn't assume the WGS84→local transform's meters-per-unit.
        var chord = Vector2.Distance(reprojSpan.OriginalStartPoint.Value, reprojSpan.OriginalEndPoint.Value);
        Assert.InRange(reprojSpan.EndDistance - reprojSpan.StartDistance, 0.9f * chord, 1.3f * chord);
    }

    [Fact]
    public void FlagOff_KeepsLegacyStations_ButStillCapturesOriginalEndpoints()
    {
        var (bbox, features) = ZigzagScenario();
        var processor = new OsmGeometryProcessor();

        var splines = processor.ConvertLinesToSplines(
            features, bbox, terrainSize: 1024, metersPerPixel: 1f,
            excludeBridges: true, mergeStructuresIntoCorridor: true,
            reprojectStructureStations: false);

        var span = MergedBridgeSpan(splines, out _);

        // Additive capture: the coordinates ride along even with the flag off (so a later phase could
        // reproject), but the stations are the legacy pre-Chaikin sums (byte-identical path).
        Assert.NotNull(span.OriginalStartPoint);
        Assert.NotNull(span.OriginalEndPoint);
    }

    // ---- StructureSegmentOps invariants ----------------------------------------------------------------------

    [Fact]
    public void ReverseSegments_SwapsOriginalEndpoints()
    {
        var seg = new StructureSegment
        {
            StartPointIndex = 2,
            EndPointIndex = 5,
            Type = StructureType.Bridge,
            OriginalStartPoint = new Vector2(10, 0),
            OriginalEndPoint = new Vector2(50, 0),
        };

        var reversed = Assert.Single(StructureSegmentOps.ReverseSegments([seg], totalPointCount: 10));

        Assert.Equal(4, reversed.StartPointIndex);  // 10-1-5
        Assert.Equal(7, reversed.EndPointIndex);    // 10-1-2
        Assert.Equal(new Vector2(50, 0), reversed.OriginalStartPoint);
        Assert.Equal(new Vector2(10, 0), reversed.OriginalEndPoint);
    }

    [Fact]
    public void Consolidate_TakesOutermostOriginalEndpoints()
    {
        var a = new StructureSegment
        {
            StartPointIndex = 0, EndPointIndex = 4, Type = StructureType.Bridge,
            OriginalStartPoint = new Vector2(0, 0), OriginalEndPoint = new Vector2(40, 0),
            OsmWayIds = [1],
        };
        var b = new StructureSegment
        {
            StartPointIndex = 5, EndPointIndex = 9, Type = StructureType.Bridge,
            OriginalStartPoint = new Vector2(40, 0), OriginalEndPoint = new Vector2(90, 0),
            OsmWayIds = [2],
        };

        var joined = Assert.Single(StructureSegmentOps.Consolidate([a, b]));

        Assert.Equal(new Vector2(0, 0), joined.OriginalStartPoint);
        Assert.Equal(new Vector2(90, 0), joined.OriginalEndPoint);
        Assert.Equal(9, joined.EndPointIndex);
    }

    // ---- RoadSpline.GetClosestDistanceTo ---------------------------------------------------------------------

    [Fact]
    public void GetClosestDistanceTo_PointOnStraightSpline_ReturnsItsArcDistance()
    {
        var spline = RoadSpline.CreateLinear([new(0, 0), new(100, 0), new(200, 0)]);

        Assert.Equal(70f, spline.GetClosestDistanceTo(new Vector2(70, 0)), 0.1f);
        Assert.Equal(130f, spline.GetClosestDistanceTo(new Vector2(130, 5)), 0.1f); // 5 m lateral offset
        Assert.Equal(0f, spline.GetClosestDistanceTo(new Vector2(-20, 3)), 0.1f);   // clamps to start
    }

    [Fact]
    public void GetClosestDistanceTo_UShape_SeedDisambiguates()
    {
        // U-shape: east 100, north 10, west 100 — a point between the legs is ~equidistant to both.
        var spline = RoadSpline.CreateLinear(
            [new(0, 0), new(100, 0), new(100, 10), new(0, 10)]);
        var between = new Vector2(50, 5);

        var nearStart = spline.GetClosestDistanceTo(between, seedDistance: 40f);
        var nearEnd = spline.GetClosestDistanceTo(between, seedDistance: 170f);

        Assert.InRange(nearStart, 30f, 70f);    // resolved onto the first leg
        Assert.InRange(nearEnd, 140f, 180f);    // resolved onto the return leg
    }
}
