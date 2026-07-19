using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Phase 5 of the "merged-corridor bridge" refactor (plan doc 11): the deck exporter builds ONE deck per
///     captured <see cref="BridgeSpanSnapshot"/> (from the merged, smoothed sub-range), keyed by the span's
///     stable id (derived from its OSM way-id set). With no captured spans it falls back to the legacy
///     whole-spline path (byte-identical).
/// </summary>
public class BridgeDeckSpanExportTests
{
    private static BridgeSpanSnapshot MakeSpan(int spanId, long wayId, float startX)
    {
        var stations = new List<BridgeStation>();
        for (var i = 0; i < 5; i++)
        {
            var d = startX + i * 5f;
            stations.Add(new BridgeStation
            {
                Center = new Vector2(d, 100f),
                Normal = new Vector2(0f, 1f),
                Tangent = new Vector2(1f, 0f),
                Width = 8f,
                CenterZ = 50f,
                LeftEdgeZ = 50f,
                RightEdgeZ = 50f,
                DistanceAlongSpline = d
            });
        }

        return new BridgeSpanSnapshot { SplineId = 1, SpanId = spanId, OsmWayIds = { wayId }, Stations = stations };
    }

    [Fact]
    public void Export_BuildsOneDeckPerSpan_KeyedBySpanId()
    {
        var network = new UnifiedRoadNetwork();
        network.BridgeSpans.Add(MakeSpan(spanId: 111, wayId: 10, startX: 20f));
        network.BridgeSpans.Add(MakeSpan(spanId: 222, wayId: 20, startX: 200f));

        var dir = Path.Combine(Path.GetTempPath(), "bridgedeck_span_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new BridgeDeckDaeExporter().Export(
                network, dir, terrainSizePixels: 512, metersPerPixel: 1f, terrainBaseHeight: 0f);

            Assert.True(result.Success);
            Assert.Equal(2, result.Decks.Count);
            Assert.Contains(result.Decks, d => d is { DaeFileName: "bridge_111.dae", SplineId: 111 });
            Assert.Contains(result.Decks, d => d is { DaeFileName: "bridge_222.dae", SplineId: 222 });
            Assert.All(result.Decks, d =>
            {
                Assert.True(d.Vertices > 0);
                Assert.True(d.Triangles > 0);
                Assert.True(File.Exists(d.OutputPath));
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Export_NoCapturedSpans_FallsBackToLegacyEmptyResult()
    {
        var network = new UnifiedRoadNetwork(); // no spans, no bridge splines
        var dir = Path.Combine(Path.GetTempPath(), "bridgedeck_legacy_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new BridgeDeckDaeExporter().Export(network, dir, 512, 1f, 0f);
            Assert.True(result.Success);
            Assert.Empty(result.Decks);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
