using System.Xml.Linq;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Step 2 of bridge-deck generation
///     (ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md).
///
///     Verifies <see cref="BridgeDeckDaeExporter"/> turns a solved bridge spline into a single deck
///     <c>.dae</c> file with a ribbon mesh, and that unchained/empty bridges are skipped with a warning
///     instead of producing a degenerate deck (acceptance §6.5).
/// </summary>
public class BridgeDeckDaeExporterTests : IDisposable
{
    private static readonly XNamespace ColladaNs = "http://www.collada.org/2005/11/COLLADASchema";

    private const int TerrainSizePixels = 300;
    private const float MetersPerPixel = 1f;
    private const float TerrainBaseHeight = 0f;

    private readonly string _outputDir = Path.Combine(
        Path.GetTempPath(), "beamng_bridge_deck_tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    ///     Builds road → bridge → road over a valley and runs the chain solve, then marks the bridge
    ///     cross-sections excluded exactly as <c>UnifiedRoadSmoother</c> does — so the bridge spline has a
    ///     solved (non-NaN) TargetElevation while remaining IsExcluded.
    /// </summary>
    private static (UnifiedRoadNetwork network, int bridgeSplineId) BuildSolvedBridgeNetwork()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        var hm = new float[TerrainSizePixels, TerrainSizePixels];
        for (var y = 0; y < TerrainSizePixels; y++)
        for (var x = 0; x < TerrainSizePixels; x++)
        {
            if (x < 80 || x > 220) hm[y, x] = 100f;
            else if (x >= 120 && x <= 180) hm[y, x] = 60f;
            else if (x < 120) hm[y, x] = 100f - (x - 80f) / 40f * 40f;
            else hm[y, x] = 60f + (x - 180f) / 40f * 40f;
        }

        foreach (var spline in network.Splines)
        {
            if (!BridgeDeckDaeExporter.ShouldGenerateDeck(spline)) continue;
            foreach (var cs in network.CrossSections.Where(c => c.OwnerSplineId == spline.SplineId))
                cs.IsExcluded = true;
        }

        RoadNetworkTestHelpers.RunChainSmoothing(network, hm);
        return (network, bridge.SplineId);
    }

    [Fact]
    public void Export_WritesOneDeckDaePerBridge_WithBoxMesh()
    {
        var (network, bridgeSplineId) = BuildSolvedBridgeNetwork();
        var bridgeCsCount = network.GetCrossSectionsForSpline(bridgeSplineId).Count();

        var result = new BridgeDeckDaeExporter().Export(
            network, _outputDir, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        Assert.True(result.Success);
        Assert.Single(result.Decks);
        Assert.Equal(0, result.BridgesSkipped);

        var deck = result.Decks[0];
        Assert.Equal(bridgeSplineId, deck.SplineId);
        Assert.Equal($"bridge_{bridgeSplineId}.dae", deck.DaeFileName);
        Assert.True(File.Exists(deck.OutputPath), "deck DAE file should be written to disk");

        // Solid box + edge parapets + soffit-following end stamps — the exporter must produce exactly
        // the mesh BridgeDeckMeshBuilder builds from the same world-converted cross-sections with the
        // default profile (stamp face count depends on station spacing inside the 3 m stamp length, so
        // the expected counts are computed through the same path rather than a spacing-baked formula).
        Assert.True(bridgeCsCount >= 2);
        var worldCs = CrossSectionConverter.ConvertSplineToWorldCoordinates(
            network, bridgeSplineId, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);
        var expected = new BeamNG.Procedural3D.RoadMesh.BridgeDeckMeshBuilder().Build(
            worldCs, new BeamNG.Procedural3D.RoadMesh.BridgeDeckProfile(), "expected", "mat");
        Assert.Equal(expected.Vertices.Count, deck.Vertices);
        Assert.Equal(expected.Triangles.Count, deck.Triangles);
    }

    [Fact]
    public void Export_WritesBeamNgDaeHierarchy_WithSeparateColmesh()
    {
        var (network, _) = BuildSolvedBridgeNetwork();

        var result = new BridgeDeckDaeExporter().Export(
            network, _outputDir, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        var daePath = Assert.Single(result.Decks).OutputPath;
        var doc = XDocument.Load(daePath);

        var nodes = doc.Descendants(ColladaNs + "node").ToList();
        Assert.Contains(nodes, n => n.Attribute("name")?.Value == "base00");
        Assert.Contains(nodes, n => n.Attribute("name")?.Value == "start01");
        Assert.Contains(nodes, n => n.Attribute("name")?.Value == "Colmesh-1");
        Assert.Contains(nodes, n => n.Attribute("name")?.Value == "collision-1");
        Assert.Contains(nodes, n => n.Attribute("name")?.Value.StartsWith("bridge_") == true &&
                                    n.Attribute("name")?.Value.Contains("_a1") == true);

        var colmeshGeometry = doc.Descendants(ColladaNs + "geometry")
            .Single(g => g.Attribute("name")?.Value == "Colmesh-1");
        var colmeshTriangles = colmeshGeometry.Descendants(ColladaNs + "triangles").ToList();
        Assert.NotEmpty(colmeshTriangles);
        Assert.All(colmeshTriangles, triangles => Assert.Null(triangles.Attribute("material")));
    }

    [Fact]
    public void Export_DoesNotGenerateDeck_ForNonBridgeRoads()
    {
        var (network, _) = BuildSolvedBridgeNetwork();

        var result = new BridgeDeckDaeExporter().Export(
            network, _outputDir, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        // Only the single bridge spline (id 2) becomes a deck; the two plain roads do not.
        Assert.All(result.Decks, d => Assert.Equal(2, d.SplineId));
    }

    [Fact]
    public void Export_SkipsBridgeWithoutSolvedElevation_WithWarning()
    {
        var (network, bridgeSplineId) = BuildSolvedBridgeNetwork();

        // Simulate an unchained bridge: wipe the solved elevation so no usable cross-sections remain.
        foreach (var cs in network.CrossSections.Where(c => c.OwnerSplineId == bridgeSplineId))
            cs.TargetElevation = float.NaN;

        var result = new BridgeDeckDaeExporter().Export(
            network, _outputDir, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);

        Assert.True(result.Success, "a skipped bridge must not fail the whole run");
        Assert.Empty(result.Decks);
        Assert.Equal(1, result.BridgesSkipped);
        Assert.Single(result.Warnings);
        Assert.Contains($"{bridgeSplineId}", result.Warnings[0]);
    }

    [Fact]
    public void Export_BridgeEndpointContinuity_NowSolvedBy_BridgeProfileSolver()
    {
        // Endpoint elevation + grade continuity moved out of the exporter into
        // BridgeProfileSolver.RefineSpans (run network-wide before DecalRoad gen + export, so
        // deck and markings agree). The exporter no longer mutates elevations; it just consumes them.
        var (network, bridgeSplineId) = BuildSolvedBridgeNetwork();
        var road1End = network.GetCrossSectionsForSpline(1).Last();
        var road2Start = network.GetCrossSectionsForSpline(3).First();

        // Simulate late junction passes moving the approach surfaces after the deck was chain-solved.
        road1End.TargetElevation += 2.25f;
        road2Start.TargetElevation -= 1.50f;

        BridgeProfileSolver.RefineSpans(network, log: false);

        var bridgeSections = network.GetCrossSectionsForSpline(bridgeSplineId)
            .OrderBy(c => c.LocalIndex).ToList();

        // Deck endpoints now match the (moved) approach elevations — G0 continuity.
        Assert.Equal(road1End.TargetElevation, bridgeSections.First().TargetElevation, precision: 2);
        Assert.Equal(road2Start.TargetElevation, bridgeSections.Last().TargetElevation, precision: 2);
        Assert.False(float.IsNaN(bridgeSections.First().LeftEdgeElevation));
        Assert.False(float.IsNaN(bridgeSections.Last().RightEdgeElevation));

        // And the exporter still produces the deck from those consumed elevations.
        var result = new BridgeDeckDaeExporter().Export(
            network, _outputDir, TerrainSizePixels, MetersPerPixel, TerrainBaseHeight);
        Assert.True(result.Success);
        Assert.Single(result.Decks);
    }
}
