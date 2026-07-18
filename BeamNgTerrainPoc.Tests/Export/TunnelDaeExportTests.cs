using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tunnel plan Phase 3b: DAE export wiring. Flag off ⇒ no files; flag on ⇒ one
///     <c>tunnel_{SpanId}.dae</c> per captured span (temp dirs, deleted in finally —
///     BridgePierExportTests pattern).
/// </summary>
public class TunnelDaeExportTests
{
    private const int SplineId = 1;

    private static UnifiedRoadNetwork BuildNetwork(bool meshOn)
    {
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 4242L },
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            SplineId, new Vector2(0, 100), new Vector2(400, 100), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.TunnelRules = new TunnelRuleSystemOptions { EnableTunnelMesh = meshOn };

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

        network.TunnelSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = SplineId, SpanId = seg.SpanId, OsmWayIds = { 4242L }, Stations = stations,
        });

        return network;
    }

    [Fact]
    public void FlagOn_WritesOneDaePerSpan()
    {
        var network = BuildNetwork(meshOn: true);
        var dir = Path.Combine(Path.GetTempPath(), "tunnelexport_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TunnelDaeExporter().Export(network, dir, 512, 1f, 0f);

            Assert.True(result.Success);
            var item = Assert.Single(result.Tunnels);
            Assert.Equal(network.TunnelSpans[0].SpanId, item.SpanId);
            Assert.True(File.Exists(item.OutputPath));
            Assert.StartsWith("tunnel_", item.DaeFileName);
            Assert.True(item.Vertices > 0);
            Assert.True(item.Triangles > 0);
            Assert.Equal(200f, item.LengthMeters, 0.1f);

            // The DAE contains the collision node (drivable floor).
            var dae = File.ReadAllText(item.OutputPath);
            Assert.Contains("Colmesh-1", dae);
            Assert.Contains(TunnelDaeExporter.DefaultMaterialName, dae);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FlagOff_NoFiles_NoItems()
    {
        var network = BuildNetwork(meshOn: false);
        var dir = Path.Combine(Path.GetTempPath(), "tunnelexport_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TunnelDaeExporter().Export(network, dir, 512, 1f, 0f);

            Assert.True(result.Success);
            Assert.Empty(result.Tunnels);
            Assert.False(Directory.Exists(dir)); // no output directory created at all
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DegenerateSpan_SkippedWithWarning()
    {
        var network = BuildNetwork(meshOn: true);
        network.TunnelSpans[0].Stations.RemoveRange(1, network.TunnelSpans[0].Stations.Count - 1);

        var dir = Path.Combine(Path.GetTempPath(), "tunnelexport_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new TunnelDaeExporter().Export(network, dir, 512, 1f, 0f);

            Assert.True(result.Success);
            Assert.Empty(result.Tunnels);
            Assert.Equal(1, result.TunnelsSkipped);
            Assert.NotEmpty(result.Warnings);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
