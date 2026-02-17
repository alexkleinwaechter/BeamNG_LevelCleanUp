using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Exports individual buildings to DAE (Collada) files for use as TSStatic objects in BeamNG.
///
/// Each building produces one DAE file with geometry in local coordinates:
/// - Origin at the building centroid on the ground plane (floor level)
/// - Separate mesh nodes per material (walls, roof, floor)
/// - Materials reference textures relative to the DAE file location
///
/// Unlike RoadNetworkDaeExporter (world coordinates), buildings use local coordinates
/// so users can move/rotate them in BeamNG's World Editor.
/// </summary>
public class BuildingDaeExporter
{
    private readonly BuildingMaterialLibrary _materialLibrary;
    private readonly BuildingMeshGenerator _meshGenerator;

    public BuildingDaeExporter(BuildingMaterialLibrary materialLibrary)
    {
        _materialLibrary = materialLibrary;
        _meshGenerator = new BuildingMeshGenerator();
    }

    /// <summary>
    /// Exports a single building to a DAE file.
    /// </summary>
    /// <param name="building">The building data (footprint already in local coordinates).</param>
    /// <param name="outputPath">Absolute path for the output DAE file.</param>
    /// <returns>Export result with statistics.</returns>
    public BuildingDaeExportResult ExportBuilding(BuildingData building, string outputPath)
    {
        var result = new BuildingDaeExportResult { OsmId = building.OsmId };

        // Generate meshes grouped by material key
        var meshesByMaterial = _meshGenerator.GenerateMeshes(building);

        if (meshesByMaterial.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = $"No geometry generated for building {building.OsmId}";
            return result;
        }

        // Resolve material keys to actual material definitions and set mesh material names
        var exportMeshes = new List<BeamNG.Procedural3D.Core.Mesh>();
        var exportMaterials = new List<BeamNG.Procedural3D.Core.Material>();

        foreach (var (materialKey, mesh) in meshesByMaterial)
        {
            var matDef = _materialLibrary.GetMaterial(materialKey);

            // Update mesh material name to the actual material name (not the key)
            mesh.MaterialName = matDef.MaterialName;

            exportMeshes.Add(mesh);
            result.TotalVertices += mesh.VertexCount;
            result.TotalTriangles += mesh.TriangleCount;

            // Create export material with texture paths relative to DAE location
            var exportMat = matDef.ToExportMaterial("textures/");
            exportMaterials.Add(exportMat);
        }

        // Export via ColladaExporter
        // Building geometry is in BeamNG's Z-up coordinate system (local space).
        // ConvertToZUp transforms to Y-up (Collada standard).
        var exportOptions = new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false
        };

        var exporter = new ColladaExporter(exportOptions);
        exporter.RegisterMaterials(exportMaterials);
        exporter.Export(exportMeshes, outputPath);

        result.Success = true;
        result.OutputPath = outputPath;
        result.MeshCount = exportMeshes.Count;

        return result;
    }

    /// <summary>
    /// Exports all buildings to individual DAE files in the specified directory.
    /// Files are named building_{osmId}.dae.
    /// Uses Parallel.ForEach for concurrent export (mesh generation + Collada writing are thread-safe).
    /// </summary>
    /// <param name="buildings">The buildings to export.</param>
    /// <param name="outputDirectory">Directory where DAE files will be saved.</param>
    /// <param name="progress">Optional progress callback (buildingIndex, totalCount).</param>
    /// <returns>Aggregate export result.</returns>
    public BuildingDaeExportResult ExportAll(
        IReadOnlyList<BuildingData> buildings,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        int totalVertices = 0;
        int totalTriangles = 0;
        int totalMeshCount = 0;
        int succeeded = 0;
        int failed = 0;
        int completed = 0;

        Parallel.ForEach(buildings, building =>
        {
            var fileName = $"building_{building.OsmId}.dae";
            var outputPath = Path.Combine(outputDirectory, fileName);

            var buildingResult = ExportBuilding(building, outputPath);

            if (buildingResult.Success)
            {
                Interlocked.Increment(ref succeeded);
                Interlocked.Add(ref totalVertices, buildingResult.TotalVertices);
                Interlocked.Add(ref totalTriangles, buildingResult.TotalTriangles);
                Interlocked.Add(ref totalMeshCount, buildingResult.MeshCount);
            }
            else
            {
                Interlocked.Increment(ref failed);
            }

            var current = Interlocked.Increment(ref completed);
            progress?.Invoke(current, buildings.Count);
        });

        return new BuildingDaeExportResult
        {
            Success = succeeded > 0,
            OutputPath = outputDirectory,
            BuildingsExported = succeeded,
            BuildingsFailed = failed,
            TotalVertices = totalVertices,
            TotalTriangles = totalTriangles,
            MeshCount = totalMeshCount,
            ErrorMessage = succeeded > 0 ? null : $"No buildings could be exported ({failed} failed)."
        };
    }

    /// <summary>
    /// Exports a cluster of buildings as a single combined DAE file.
    /// Each building's geometry is offset from the cluster anchor position so the
    /// TSStatic can be placed at the anchor and all buildings render correctly.
    /// </summary>
    public BuildingDaeExportResult ExportCluster(BuildingCluster cluster, string outputPath)
    {
        var result = new BuildingDaeExportResult
        {
            OsmId = 0, // Not a single building
            BuildingsExported = 0
        };

        // Merged meshes by material key across all buildings in the cluster
        var mergedMeshes = new Dictionary<string, Mesh>();

        foreach (var building in cluster.Buildings)
        {
            var meshesByMaterial = _meshGenerator.GenerateMeshes(building);
            if (meshesByMaterial.Count == 0) continue;

            // Offset = building position relative to cluster anchor
            var offset = building.WorldPosition - cluster.AnchorPosition;

            foreach (var (materialKey, mesh) in meshesByMaterial)
            {
                var matDef = _materialLibrary.GetMaterial(materialKey);
                mesh.MaterialName = matDef.MaterialName;

                // Merge directly into target mesh with offset — no intermediate copy
                MergeWithOffset(mergedMeshes, materialKey, mesh, offset);

                result.TotalVertices += mesh.VertexCount;
                result.TotalTriangles += mesh.TriangleCount;
            }

            result.BuildingsExported++;
        }

        if (mergedMeshes.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = $"No geometry generated for cluster {cluster.SceneName}";
            return result;
        }

        // Resolve material definitions for export
        var exportMeshes = new List<Mesh>();
        var exportMaterials = new List<BeamNG.Procedural3D.Core.Material>();
        var seenMaterials = new HashSet<string>();

        foreach (var (materialKey, mesh) in mergedMeshes)
        {
            exportMeshes.Add(mesh);

            if (seenMaterials.Add(materialKey))
            {
                var matDef = _materialLibrary.GetMaterial(materialKey);
                exportMaterials.Add(matDef.ToExportMaterial("textures/"));
            }
        }

        var exportOptions = new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false
        };

        var exporter = new ColladaExporter(exportOptions);
        exporter.RegisterMaterials(exportMaterials);
        exporter.Export(exportMeshes, outputPath);

        result.Success = true;
        result.OutputPath = outputPath;
        result.MeshCount = exportMeshes.Count;

        return result;
    }

    /// <summary>
    /// Exports all clusters to individual DAE files.
    /// Files are named cluster_{cellX}_{cellY}.dae.
    /// Uses Parallel.ForEach for concurrent export (each cluster is independent).
    /// </summary>
    public BuildingDaeExportResult ExportAllClustered(
        IReadOnlyList<BuildingCluster> clusters,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        int totalVertices = 0;
        int totalTriangles = 0;
        int totalMeshCount = 0;
        int totalBuildingsExported = 0;
        int totalBuildingsFailed = 0;
        int succeeded = 0;
        int failed = 0;
        int completed = 0;

        Parallel.ForEach(clusters, cluster =>
        {
            var outputPath = Path.Combine(outputDirectory, cluster.FileName);

            var clusterResult = ExportCluster(cluster, outputPath);

            if (clusterResult.Success)
            {
                Interlocked.Increment(ref succeeded);
                Interlocked.Add(ref totalVertices, clusterResult.TotalVertices);
                Interlocked.Add(ref totalTriangles, clusterResult.TotalTriangles);
                Interlocked.Add(ref totalMeshCount, clusterResult.MeshCount);
                Interlocked.Add(ref totalBuildingsExported, clusterResult.BuildingsExported);
            }
            else
            {
                Interlocked.Increment(ref failed);
                Interlocked.Add(ref totalBuildingsFailed, cluster.Buildings.Count);
            }

            var current = Interlocked.Increment(ref completed);
            progress?.Invoke(current, clusters.Count);
        });

        return new BuildingDaeExportResult
        {
            Success = succeeded > 0,
            OutputPath = outputDirectory,
            ClustersExported = succeeded,
            TotalVertices = totalVertices,
            TotalTriangles = totalTriangles,
            MeshCount = totalMeshCount,
            BuildingsExported = totalBuildingsExported,
            BuildingsFailed = totalBuildingsFailed,
            ErrorMessage = succeeded > 0 ? null : $"No clusters could be exported ({failed} failed)."
        };
    }

    /// <summary>
    /// Merges a source mesh into the dictionary target for the given material key,
    /// applying a position offset to all vertices in a single pass.
    /// Avoids allocating an intermediate offset mesh copy.
    /// </summary>
    private static void MergeWithOffset(
        Dictionary<string, Mesh> meshes, string materialKey, Mesh source, Vector3 offset)
    {
        if (!meshes.TryGetValue(materialKey, out var target))
        {
            // First mesh for this material — create target and copy with offset
            target = new Mesh { Name = source.Name, MaterialName = source.MaterialName };
            meshes[materialKey] = target;
        }

        int baseIndex = target.Vertices.Count;

        // Add vertices with offset applied — single pass, no intermediate mesh
        foreach (var v in source.Vertices)
        {
            target.Vertices.Add(new Vertex(v.Position + offset, v.Normal, v.UV));
        }

        // Add triangles with rebased indices
        foreach (var tri in source.Triangles)
        {
            target.Triangles.Add(new Triangle(
                tri.V0 + baseIndex,
                tri.V1 + baseIndex,
                tri.V2 + baseIndex));
        }
    }
}

/// <summary>
/// Result information from a building DAE export operation.
/// </summary>
public class BuildingDaeExportResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
    public long OsmId { get; set; }
    public int MeshCount { get; set; }
    public int TotalVertices { get; set; }
    public int TotalTriangles { get; set; }
    public int BuildingsExported { get; set; }
    public int BuildingsFailed { get; set; }
    public int ClustersExported { get; set; }

    public override string ToString()
    {
        if (!Success)
            return $"Export failed: {ErrorMessage}";

        if (BuildingsExported > 0)
            return $"Exported {BuildingsExported} buildings ({MeshCount} meshes, {TotalVertices:N0} verts, {TotalTriangles:N0} tris) to {OutputPath}";

        return $"Exported building {OsmId} ({MeshCount} meshes, {TotalVertices} verts, {TotalTriangles} tris) to {OutputPath}";
    }
}
