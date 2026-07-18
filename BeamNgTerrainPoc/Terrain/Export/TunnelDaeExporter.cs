using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Exports tunnel tube meshes to DAE (tunnel plan Phase 3b) — the straight clone of
/// <see cref="BridgeDeckDaeExporter"/>'s span path: one <c>tunnel_{SpanId}.dae</c> per captured
/// <c>network.TunnelSpans</c> snapshot, world-coordinate mesh (Z-up, TSStatic at origin),
/// <c>base00→start01→{Colmesh-1, tunnel}</c> hierarchy with a full collision clone (floor is the
/// drivable surface; walls/ceiling collide too, which is desired). Gated per spline on
/// <c>TunnelRules.EnableTunnelMesh</c>.
/// </summary>
public class TunnelDaeExporter
{
    /// <summary>Placeholder material for tunnel tubes (darker than bridge concrete).</summary>
    public const string DefaultMaterialName = "building_concrete";

    /// <summary>
    /// Exports one tube DAE per captured tunnel span whose owning spline has the mesh rule enabled.
    /// </summary>
    public TunnelExportResult Export(
        UnifiedRoadNetwork network,
        string shapesOutputDirectory,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight,
        string materialName = DefaultMaterialName)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(shapesOutputDirectory);

        var result = new TunnelExportResult { OutputDirectory = shapesOutputDirectory };

        var spans = network.TunnelSpans
            .Where(s => network.GetSplineById(s.SplineId)?.Parameters.TunnelRules?.EnableTunnelMesh == true)
            .ToList();
        if (spans.Count == 0)
        {
            result.Success = true;
            return result;
        }

        Directory.CreateDirectory(shapesOutputDirectory);

        foreach (var span in spans)
        {
            var rules = network.GetSplineById(span.SplineId)!.Parameters.TunnelRules!;
            var crossSections = span.Stations
                .Select(st => new RoadCrossSection
                {
                    CenterPoint = BeamNgCoordinateTransformer.TerrainToWorld2D(
                        st.Center, terrainSizePixels, metersPerPixel),
                    CenterElevation = st.CenterZ + terrainBaseHeight,
                    TangentDirection = st.Tangent,
                    NormalDirection = st.Normal,
                    WidthMeters = st.Width,
                    // Bank rides in the edge Zs (the builder shears from them); the angle is derived
                    // for RoadCrossSection self-consistency. Flat spans (banking off) ⇒ asin(0) = 0.
                    BankAngleRadians = st.Width > 1e-3f
                        ? MathF.Asin(Math.Clamp((st.RightEdgeZ - st.LeftEdgeZ) / st.Width, -1f, 1f))
                        : 0f,
                    DistanceAlongRoad = st.DistanceAlongSpline,
                    LeftEdgeElevation = st.LeftEdgeZ + terrainBaseHeight,
                    RightEdgeElevation = st.RightEdgeZ + terrainBaseHeight
                })
                .ToList();

            if (crossSections.Count < 2)
            {
                result.Warnings.Add(
                    $"Tunnel span {span.SpanId} on spline {span.SplineId} produced " +
                    $"{crossSections.Count} usable station(s) — skipped (no solved floor geometry).");
                result.TunnelsSkipped++;
                continue;
            }

            var profile = new TunnelMeshProfile
            {
                InteriorHeightMeters = rules.TunnelInteriorHeightMeters,
                WallThicknessMeters = rules.TunnelWallThicknessMeters,
                SideClearanceMeters = rules.TunnelSideClearanceMeters,
                ArchSegments = Math.Max(2, rules.TunnelCeilingArchSegments),
                HeadwallFlareMeters = rules.PortalHeadwallFlareMeters,
                HeadwallDepthMeters = rules.PortalHeadwallDepthMeters,
                FloorTongueMeters = rules.PortalFloorTongueMeters
            };

            var mesh = new TunnelMeshBuilder()
                .Build(crossSections, profile, $"Tunnel_{span.SpanId}", materialName);
            if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            {
                result.Warnings.Add($"Tunnel span {span.SpanId} produced an empty tube mesh — skipped.");
                result.TunnelsSkipped++;
                continue;
            }

            var daeFileName = $"tunnel_{span.SpanId}.dae";
            var outputPath = Path.Combine(shapesOutputDirectory, daeFileName);
            WriteBeamNgTunnelDae(mesh, span.SpanId, outputPath);

            var length = span.Stations[^1].DistanceAlongSpline - span.Stations[0].DistanceAlongSpline;
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[TUNNEL-MESH] span {span.SpanId} spline {span.SplineId}: stations={span.Stations.Count} " +
                $"len={length:F1}m bore={span.Stations[0].Width + 2f * rules.TunnelSideClearanceMeters:F1}x" +
                $"{rules.TunnelInteriorHeightMeters:F1}m wall={rules.TunnelWallThicknessMeters:F2}m " +
                $"verts={mesh.Vertices.Count} tris={mesh.Triangles.Count}");

            result.Tunnels.Add(new TunnelExportItem
            {
                SpanId = span.SpanId,
                SplineId = span.SplineId,
                DaeFileName = daeFileName,
                OutputPath = outputPath,
                Vertices = mesh.Vertices.Count,
                Triangles = mesh.Triangles.Count,
                LengthMeters = length
            });
        }

        result.Success = true;
        return result;
    }

    /// <summary>Writes one tunnel DAE (visual tube + full collision clone).</summary>
    private static void WriteBeamNgTunnelDae(Mesh tubeMesh, int spanId, string outputPath)
    {
        var collision = new Mesh { Name = "Colmesh-1" };
        collision.Vertices.AddRange(tubeMesh.Vertices);
        collision.Triangles.AddRange(tubeMesh.Triangles);
        collision.MaterialName = null;

        var scene = new BeamNgDaeScene
        {
            BaseName = $"tunnel_{spanId}",
            LodLevels = [new LodLevel(1, new List<Mesh> { tubeMesh })],
            ColmeshMeshes = collision.HasGeometry ? [collision] : null
        };

        new ColladaExporter(new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false
        }).Export(scene, outputPath);
    }
}

/// <summary>Result of a single exported tunnel tube.</summary>
public class TunnelExportItem
{
    public int SpanId { get; init; }
    public int SplineId { get; init; }
    public required string DaeFileName { get; init; }
    public required string OutputPath { get; init; }
    public int Vertices { get; init; }
    public int Triangles { get; init; }
    public float LengthMeters { get; init; }
}

/// <summary>Aggregate result of a tunnel export run.</summary>
public class TunnelExportResult
{
    public bool Success { get; set; }
    public string? OutputDirectory { get; set; }
    public List<TunnelExportItem> Tunnels { get; } = [];
    public int TunnelsSkipped { get; set; }
    public List<string> Warnings { get; } = [];
    public int TotalVertices => Tunnels.Sum(t => t.Vertices);
    public int TotalTriangles => Tunnels.Sum(t => t.Triangles);
}
