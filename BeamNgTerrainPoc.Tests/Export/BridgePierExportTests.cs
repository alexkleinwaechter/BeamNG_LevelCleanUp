using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Doc 19 exporter wiring: with <c>EnableBridgePiers</c> ON and a heightmap, the deck DAE gains a
///     separate additive pier mesh (cap + columns per planned pier) — the DECK submesh itself never
///     changes. Flag off (default) ⇒ output identical to before the feature; flag on without a
///     heightmap ⇒ warning + no piers (they must stand on the final carved ground).
/// </summary>
public class BridgePierExportTests
{
    private const int SplineId = 1;

    // §4 count formulas (see BridgePierMeshBuilderTests): single-column pier = 6-face cap + 1 column.
    private const int PierVerts = 24 + 32 + 2 * 9;
    private const int PierTris = 12 + 16 + 2 * 8;

    /// <summary>One straight 200 m bridge span along X (y=100, stations every 5 m, deck Z 24, 8 m wide).</summary>
    private static UnifiedRoadNetwork BuildNetwork(bool piersOn)
    {
        var rules = new BridgeRuleSystemOptions { EnableBridgePiers = piersOn };

        var seg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 777L },
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            SplineId, new Vector2(0, 100), new Vector2(400, 100), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.BridgeRules = rules;

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);

        var stations = new List<BridgeStation>();
        for (var d = 100f; d <= 300f; d += 5f)
        {
            stations.Add(new BridgeStation
            {
                Center = new Vector2(d, 100f),
                Normal = new Vector2(0f, -1f),
                Tangent = new Vector2(1f, 0f),
                Width = 8f,
                CenterZ = 24f, LeftEdgeZ = 24f, RightEdgeZ = 24f,
                DistanceAlongSpline = d,
            });
        }

        network.BridgeSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = SplineId, SpanId = seg.SpanId, OsmWayIds = { 777L }, Stations = stations,
        });

        return network;
    }

    private static BridgeDeckExportResult Export(UnifiedRoadNetwork network, float[,]? heightMap)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pierexport_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new BridgeDeckDaeExporter().Export(
                network, dir, terrainSizePixels: 512, metersPerPixel: 1f, terrainBaseHeight: 0f,
                heightMap: heightMap);
            Assert.True(result.Success);
            return result;
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FlagOn_AddsAdditivePierMesh_DeckUnchanged()
    {
        var flat = new float[512, 512]; // ground 0, deck 24 ⇒ all piers stand

        var off = Export(BuildNetwork(piersOn: false), flat);
        var on = Export(BuildNetwork(piersOn: true), flat);

        var deckOff = Assert.Single(off.Decks);
        var deckOn = Assert.Single(on.Decks);

        Assert.Equal(0, deckOff.Piers);
        // 200 m span, spacing 30, usable 188 ⇒ 6 piers; 8 m deck < twin threshold ⇒ single columns.
        Assert.Equal(6, deckOn.Piers);

        // Piers are ADDITIVE: the delta is exactly the pier mesh; the deck submesh never changes.
        Assert.Equal(deckOff.Vertices + 6 * PierVerts, deckOn.Vertices);
        Assert.Equal(deckOff.Triangles + 6 * PierTris, deckOn.Triangles);
    }

    [Fact]
    public void FlagOff_OutputIdenticalAcrossRuns()
    {
        var flat = new float[512, 512];
        var a = Export(BuildNetwork(piersOn: false), flat);
        var b = Export(BuildNetwork(piersOn: false), flat);

        Assert.Equal(a.Decks[0].Vertices, b.Decks[0].Vertices);
        Assert.Equal(a.Decks[0].Triangles, b.Decks[0].Triangles);
        Assert.Equal(0, a.TotalPiers);
    }

    [Fact]
    public void FlagOn_WithoutHeightmap_WarnsAndBuildsNoPiers()
    {
        var off = Export(BuildNetwork(piersOn: false), null);
        var on = Export(BuildNetwork(piersOn: true), null);

        Assert.Equal(0, on.TotalPiers);
        Assert.Equal(off.Decks[0].Vertices, on.Decks[0].Vertices); // no meshes without ground data
        Assert.Contains(on.Warnings, w => w.Contains("heightmap"));
    }

    [Fact]
    public void FlagOn_LowDeck_NoStubPiers()
    {
        // Ground at 22.4: soffit 24 − 1.2 = 22.8 ⇒ only 0.4 m of air — embankment zone, no piers.
        var high = new float[512, 512];
        for (var y = 0; y < 512; y++)
        for (var x = 0; x < 512; x++)
            high[y, x] = 22.4f;

        var result = Export(BuildNetwork(piersOn: true), high);
        Assert.Equal(0, result.TotalPiers);
    }
}
