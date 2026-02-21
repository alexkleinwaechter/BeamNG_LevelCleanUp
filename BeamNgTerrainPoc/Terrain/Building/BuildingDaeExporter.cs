using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;
using Bldg = BeamNG.Procedural3D.Building.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
///     Exports individual buildings to DAE (Collada) files for use as TSStatic objects in BeamNG.
///     Each building produces one DAE file with geometry in local coordinates:
///     - Origin at the building centroid on the ground plane (floor level)
///     - Separate mesh nodes per material (walls, roof, floor)
///     - Materials reference textures relative to the DAE file location
///     Supports both:
///     - Simple buildings (single BuildingData per building) — legacy API
///     - Multi-part buildings (Building with multiple BuildingData parts) — Phase 2 API
///     Unlike RoadNetworkDaeExporter (world coordinates), buildings use local coordinates
///     so users can move/rotate them in BeamNG's World Editor.
/// </summary>
public class BuildingDaeExporter
{
    private readonly BuildingMaterialLibrary _materialLibrary;
    private readonly BuildingMeshGenerator _meshGenerator;

    /// <summary>
    ///     LOD bias multiplier applied to all dynamic LOD computations.
    ///     Default 1.0. Set before calling export methods.
    ///     Values &gt; 1 increase thresholds (detail drops sooner at distance).
    ///     Values &lt; 1 decrease thresholds (more detail retained at distance).
    /// </summary>
    public float LodBias { get; set; } = 1.0f;

    /// <summary>
    ///     Maximum LOD level to include in exported DAE files.
    ///     0 = LOD0 only (walls + roof), 1 = up to LOD1 (textured windows), 2 = up to LOD2 (full 3D windows).
    /// </summary>
    public int MaxLodLevel { get; set; } = 2;

    /// <summary>
    ///     Pixel-size cull threshold for the nulldetail node.
    ///     When the object is smaller than this many pixels on screen, it is not rendered at all.
    ///     0 = no nulldetail node (object always rendered). Default 0.
    /// </summary>
    public int NullDetailPixelSize { get; set; }

    public BuildingDaeExporter(BuildingMaterialLibrary materialLibrary)
    {
        _materialLibrary = materialLibrary;
        _meshGenerator = new BuildingMeshGenerator();
    }

    // ==========================================
    // Multi-part Building API (Phase 2)
    // ==========================================

    /// <summary>
    ///     Exports a multi-part building to a single DAE file with BeamNG LOD hierarchy.
    ///     All parts are rendered into the same file with separate meshes per material.
    ///     Includes convex hull collision mesh and proper base00/start01 node structure.
    ///     Generates 3 LOD levels: walls-only, textured windows, full 3D windows+doors.
    /// </summary>
    public BuildingDaeExportResult ExportBuilding(Bldg building, string outputPath)
    {
        var result = new BuildingDaeExportResult { OsmId = building.OsmId };
        var lodMeshes = _meshGenerator.GenerateMultiLodMeshes(building, GetTextureScale, MaxLodLevel);
        var lodDefaults = BeamNgLodDefaults.ComputeForBounds(ComputeMaxBoundsDimension(building), LodBias);
        return FinishMultiLodExport(lodMeshes, result, outputPath, lodDefaults);
    }

    /// <summary>
    ///     Exports all multi-part buildings to individual DAE files.
    ///     Files are named building-{osmId}.dae.
    ///     Uses Parallel.ForEach for concurrent export.
    /// </summary>
    public BuildingDaeExportResult ExportAllBuildings(
        IReadOnlyList<Bldg> buildings,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        int totalVertices = 0, totalTriangles = 0, totalMeshCount = 0;
        int succeeded = 0, failed = 0, completed = 0;

        Parallel.ForEach(buildings, building =>
        {
            var outputPath = Path.Combine(outputDirectory, building.DaeFileName);
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
    ///     Exports a cluster of multi-part buildings as a single combined DAE file
    ///     with BeamNG LOD hierarchy and collision mesh.
    ///     Each building's geometry is offset from the cluster anchor position.
    ///     Generates 3 LOD levels with windows/doors.
    /// </summary>
    public BuildingDaeExportResult ExportBuildingCluster(BuildingCluster<Bldg> cluster, string outputPath)
    {
        var result = new BuildingDaeExportResult { OsmId = 0, BuildingsExported = 0 };
        var mergedLodMeshes = new Dictionary<int, Dictionary<string, Mesh>>
        {
            [0] = new(), [1] = new(), [2] = new()
        };
        var allBoundsVertices = new List<Vector2>();
        var clusterMinHeight = float.MaxValue;
        float clusterMaxHeight = 0;

        foreach (var building in cluster.Buildings)
        {
            // Remap BUILDING_DEFAULT wall material to per-color variants for clustered export.
            // Must happen before mesh generation so meshes are keyed by variant material key.
            foreach (var part in building.Parts)
            {
                if (part.WallColor.HasValue && part.WallMaterial == "BUILDING_DEFAULT")
                {
                    var variant = _materialLibrary.GetOrCreateColorVariant("BUILDING_DEFAULT", part.WallColor.Value);
                    part.WallMaterial = variant.MaterialKey;
                }
            }

            var lodMeshes = _meshGenerator.GenerateMultiLodMeshes(building, GetTextureScale, MaxLodLevel);
            if (lodMeshes.Values.All(m => m.Count == 0)) continue;

            var offset = building.WorldPosition - cluster.AnchorPosition;

            foreach (var (lod, meshByMat) in lodMeshes)
            foreach (var (materialKey, mesh) in meshByMat)
            {
                var matDef = _materialLibrary.GetMaterial(materialKey);
                mesh.MaterialName = matDef.MaterialName;
                MergeWithOffset(mergedLodMeshes[lod], materialKey, mesh, offset);
                if (lod == 0)
                {
                    result.TotalVertices += mesh.VertexCount;
                    result.TotalTriangles += mesh.TriangleCount;
                }
            }

            // Collect footprint vertices (offset to cluster space) for bounds computation
            foreach (var part in building.Parts)
            {
                foreach (var v in part.FootprintOuter)
                    allBoundsVertices.Add(new Vector2(v.X + offset.X, v.Y + offset.Y));
                clusterMinHeight = MathF.Min(clusterMinHeight, part.MinHeight);
                clusterMaxHeight = MathF.Max(clusterMaxHeight, part.Height);
            }

            result.BuildingsExported++;
        }

        var clusterMaxDimension = ComputeClusterMaxBoundsDimension(
            allBoundsVertices, clusterMinHeight, clusterMaxHeight);
        var lodDefaults = clusterMaxDimension > 0
            ? BeamNgLodDefaults.ComputeForBounds(clusterMaxDimension, LodBias)
            : BeamNgLodDefaults.Cluster;

        return FinishMultiLodClusterExport(mergedLodMeshes, result, cluster.SceneName, outputPath,
            lodDefaults);
    }

    /// <summary>
    ///     Exports all multi-part building clusters to individual DAE files.
    /// </summary>
    public BuildingDaeExportResult ExportAllBuildingClusters(
        IReadOnlyList<BuildingCluster<Bldg>> clusters,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        return ExportAllClustersInternal(clusters, outputDirectory,
            (cluster, path) => ExportBuildingCluster(cluster, path), progress);
    }

    // ==========================================
    // Legacy API (flat BuildingData)
    // ==========================================

    /// <summary>
    ///     Exports a single building (flat BuildingData) to a DAE file with BeamNG LOD hierarchy.
    ///     Generates 3 LOD levels: walls-only, textured windows, full 3D windows+doors.
    /// </summary>
    public BuildingDaeExportResult ExportBuilding(BuildingData building, string outputPath)
    {
        var result = new BuildingDaeExportResult { OsmId = building.OsmId };
        var lodMeshes = _meshGenerator.GenerateMultiLodMeshes(building, GetTextureScale, MaxLodLevel);
        var lodDefaults = BeamNgLodDefaults.ComputeForBounds(ComputeMaxBoundsDimension(building), LodBias);
        return FinishMultiLodExport(lodMeshes, result, outputPath, lodDefaults);
    }

    /// <summary>
    ///     Exports all buildings (flat BuildingData) to individual DAE files.
    /// </summary>
    public BuildingDaeExportResult ExportAll(
        IReadOnlyList<BuildingData> buildings,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        int totalVertices = 0, totalTriangles = 0, totalMeshCount = 0;
        int succeeded = 0, failed = 0, completed = 0;

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
    ///     Exports a cluster of buildings (flat BuildingData) as a single combined DAE file
    ///     with BeamNG LOD hierarchy and collision mesh.
    ///     Generates 3 LOD levels with windows/doors.
    /// </summary>
    public BuildingDaeExportResult ExportCluster(BuildingCluster<BuildingData> cluster, string outputPath)
    {
        var result = new BuildingDaeExportResult { OsmId = 0, BuildingsExported = 0 };
        var mergedLodMeshes = new Dictionary<int, Dictionary<string, Mesh>>
        {
            [0] = new(), [1] = new(), [2] = new()
        };
        var allBoundsVertices = new List<Vector2>();
        var clusterMinHeight = float.MaxValue;
        float clusterMaxHeight = 0;

        foreach (var building in cluster.Buildings)
        {
            // Remap BUILDING_DEFAULT wall material to per-color variants for clustered export.
            if (building.WallColor.HasValue && building.WallMaterial == "BUILDING_DEFAULT")
            {
                var variant = _materialLibrary.GetOrCreateColorVariant("BUILDING_DEFAULT", building.WallColor.Value);
                building.WallMaterial = variant.MaterialKey;
            }

            var lodMeshes = _meshGenerator.GenerateMultiLodMeshes(building, GetTextureScale, MaxLodLevel);
            if (lodMeshes.Values.All(m => m.Count == 0)) continue;

            var offset = building.WorldPosition - cluster.AnchorPosition;

            foreach (var (lod, meshByMat) in lodMeshes)
            foreach (var (materialKey, mesh) in meshByMat)
            {
                var matDef = _materialLibrary.GetMaterial(materialKey);
                mesh.MaterialName = matDef.MaterialName;
                MergeWithOffset(mergedLodMeshes[lod], materialKey, mesh, offset);
                if (lod == 0)
                {
                    result.TotalVertices += mesh.VertexCount;
                    result.TotalTriangles += mesh.TriangleCount;
                }
            }

            // Collect footprint vertices for bounds computation
            foreach (var v in building.FootprintOuter)
                allBoundsVertices.Add(new Vector2(v.X + offset.X, v.Y + offset.Y));
            clusterMinHeight = MathF.Min(clusterMinHeight, building.MinHeight);
            clusterMaxHeight = MathF.Max(clusterMaxHeight, building.Height);

            result.BuildingsExported++;
        }

        var clusterMaxDimension = ComputeClusterMaxBoundsDimension(
            allBoundsVertices, clusterMinHeight, clusterMaxHeight);
        var lodDefaults = clusterMaxDimension > 0
            ? BeamNgLodDefaults.ComputeForBounds(clusterMaxDimension, LodBias)
            : BeamNgLodDefaults.Cluster;

        return FinishMultiLodClusterExport(mergedLodMeshes, result, cluster.SceneName, outputPath,
            lodDefaults);
    }

    /// <summary>
    ///     Exports all clusters (flat BuildingData) to individual DAE files.
    /// </summary>
    public BuildingDaeExportResult ExportAllClustered(
        IReadOnlyList<BuildingCluster<BuildingData>> clusters,
        string outputDirectory,
        Action<int, int>? progress = null)
    {
        return ExportAllClustersInternal(clusters, outputDirectory,
            (cluster, path) => ExportCluster(cluster, path), progress);
    }

    // ==========================================
    // Shared export internals
    // ==========================================

    /// <summary>
    ///     Finishes a multi-LOD export for a single building.
    ///     Resolves materials, builds LOD levels, writes Collada with BeamNG hierarchy.
    ///     Collision mesh is generated from LOD0 geometry (merged, no materials).
    /// </summary>
    private BuildingDaeExportResult FinishMultiLodExport(
        Dictionary<int, Dictionary<string, Mesh>> lodMeshes,
        BuildingDaeExportResult result,
        string outputPath,
        BeamNgLodDefaults lodDefaults)
    {
        // Check that at least LOD0 has geometry
        if (!lodMeshes.TryGetValue(0, out var lod0) || lod0.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = $"No geometry generated for building {result.OsmId}";
            return result;
        }

        var allMaterials = new List<Material>();
        var seenMaterials = new HashSet<string>();

        // Resolve materials and assign MaterialName on all LOD meshes
        foreach (var (_, meshByMat) in lodMeshes)
        foreach (var (materialKey, mesh) in meshByMat)
        {
            var matDef = _materialLibrary.GetMaterial(materialKey);
            mesh.MaterialName = matDef.MaterialName;
            if (seenMaterials.Add(materialKey))
                allMaterials.Add(matDef.ToExportMaterial(_materialLibrary.GetDeployedFileName));
        }

        // Count vertices/triangles from LOD2 (highest detail, largest)
        var countLod = lodMeshes.ContainsKey(2) ? lodMeshes[2] : lod0;
        foreach (var (_, mesh) in countLod)
        {
            result.TotalVertices += mesh.VertexCount;
            result.TotalTriangles += mesh.TriangleCount;
        }

        // Collision mesh from LOD0 geometry (merged, no materials)
        var collisionMesh = CollisionMeshGenerator.GenerateFromLod0(lod0);

        var lodPixelSizes = new[] { lodDefaults.Lod0PixelSize, lodDefaults.Lod1PixelSize, lodDefaults.Lod2PixelSize };
        WriteColladaWithMultiLod(lodMeshes, allMaterials, collisionMesh, lodPixelSizes,
            $"building_{result.OsmId}", outputPath, NullDetailPixelSize);

        result.Success = true;
        result.OutputPath = outputPath;
        result.MeshCount = countLod.Count;
        return result;
    }

    /// <summary>
    ///     Finishes a multi-LOD cluster export.
    ///     Resolves materials, builds LOD levels, writes Collada with BeamNG hierarchy.
    ///     Collision mesh is generated from merged LOD0 geometry (all buildings combined).
    /// </summary>
    private BuildingDaeExportResult FinishMultiLodClusterExport(
        Dictionary<int, Dictionary<string, Mesh>> lodMeshes,
        BuildingDaeExportResult result,
        string sceneName,
        string outputPath,
        BeamNgLodDefaults lodDefaults)
    {
        if (!lodMeshes.TryGetValue(0, out var lod0) || lod0.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = $"No geometry generated for cluster {sceneName}";
            return result;
        }

        var allMaterials = new List<Material>();
        var seenMaterials = new HashSet<string>();

        foreach (var (_, meshByMat) in lodMeshes)
        foreach (var (materialKey, mesh) in meshByMat)
            if (seenMaterials.Add(materialKey))
            {
                var matDef = _materialLibrary.GetMaterial(materialKey);
                allMaterials.Add(matDef.ToExportMaterial(_materialLibrary.GetDeployedFileName));
            }

        // Collision mesh from merged LOD0 geometry (all cluster buildings combined)
        var collisionMesh = CollisionMeshGenerator.GenerateFromLod0(lod0);

        var lodPixelSizes = new[] { lodDefaults.Lod0PixelSize, lodDefaults.Lod1PixelSize, lodDefaults.Lod2PixelSize };
        WriteColladaWithMultiLod(lodMeshes, allMaterials, collisionMesh, lodPixelSizes,
            sceneName, outputPath, NullDetailPixelSize);

        result.Success = true;
        result.OutputPath = outputPath;
        result.MeshCount = lod0.Count;
        return result;
    }

    /// <summary>
    ///     Writes multi-LOD meshes with BeamNG LOD hierarchy (base00/start01/collision-1 structure).
    ///     Each LOD level gets its own node under start01, named with _a{pixelSize} suffix.
    /// </summary>
    private static void WriteColladaWithMultiLod(
        Dictionary<int, Dictionary<string, Mesh>> lodMeshes,
        List<Material> materials,
        Mesh? collisionMesh,
        int[] lodPixelSizes,
        string baseName,
        string outputPath,
        int nullDetailPixelSize = 0)
    {
        var exportOptions = new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false
        };

        var exporter = new ColladaExporter(exportOptions);
        exporter.RegisterMaterials(materials);

        var lodLevels = new List<LodLevel>();
        for (var lod = 0; lod < lodPixelSizes.Length && lod <= 2; lod++)
            if (lodMeshes.TryGetValue(lod, out var meshByMat) && meshByMat.Count > 0)
                lodLevels.Add(new LodLevel(lodPixelSizes[lod], meshByMat.Values.ToList()));

        // Fallback: if no LOD levels could be built, use LOD0 meshes at lowest pixel size
        if (lodLevels.Count == 0 && lodMeshes.ContainsKey(0))
            lodLevels.Add(new LodLevel(lodPixelSizes[0], lodMeshes[0].Values.ToList()));

        var scene = new BeamNgDaeScene
        {
            BaseName = baseName,
            LodLevels = lodLevels,
            ColmeshMeshes = collisionMesh is { HasGeometry: true } ? [collisionMesh] : null,
            NullDetailPixelSize = nullDetailPixelSize
        };

        exporter.Export(scene, outputPath);
    }

    /// <summary>
    ///     Generic "export all clusters" loop with parallel execution.
    /// </summary>
    private static BuildingDaeExportResult ExportAllClustersInternal<T>(
        IReadOnlyList<T> clusters,
        string outputDirectory,
        Func<T, string, BuildingDaeExportResult> exportCluster,
        Action<int, int>? progress) where T : IClusterInfo
    {
        Directory.CreateDirectory(outputDirectory);

        int totalVertices = 0, totalTriangles = 0, totalMeshCount = 0;
        int totalBuildingsExported = 0, totalBuildingsFailed = 0;
        int succeeded = 0, failed = 0, completed = 0;

        Parallel.ForEach(clusters, cluster =>
        {
            var outputPath = Path.Combine(outputDirectory, cluster.FileName);
            var clusterResult = exportCluster(cluster, outputPath);

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
                Interlocked.Add(ref totalBuildingsFailed, cluster.BuildingCount);
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

    private Vector2 GetTextureScale(string materialKey)
    {
        var matDef = _materialLibrary.GetMaterial(materialKey);
        return new Vector2(matDef.TextureScaleU, matDef.TextureScaleV);
    }

    /// <summary>
    ///     Computes the maximum AABB dimension for a single BuildingData.
    /// </summary>
    private static float ComputeMaxBoundsDimension(BuildingData building)
    {
        if (building.FootprintOuter.Count == 0) return 15.0f; // safe default

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (var v in building.FootprintOuter)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }

        var width = maxX - minX;
        var depth = maxY - minY;
        var height = building.Height - building.MinHeight;

        return MathF.Max(width, MathF.Max(depth, height));
    }

    /// <summary>
    ///     Computes the maximum AABB dimension for a multi-part Building.
    /// </summary>
    private static float ComputeMaxBoundsDimension(Bldg building)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float maxHeight = 0;

        foreach (var part in building.Parts)
        {
            foreach (var v in part.FootprintOuter)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }

            maxHeight = MathF.Max(maxHeight, part.Height);
        }

        if (minX == float.MaxValue) return 15.0f; // safe default

        var width = maxX - minX;
        var depth = maxY - minY;

        return MathF.Max(width, MathF.Max(depth, maxHeight));
    }

    /// <summary>
    ///     Computes the maximum AABB dimension from offset collision vertices and height range.
    /// </summary>
    private static float ComputeClusterMaxBoundsDimension(
        List<Vector2> collisionVertices, float minHeight, float maxHeight)
    {
        if (collisionVertices.Count == 0) return 0;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (var v in collisionVertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }

        var width = maxX - minX;
        var depth = maxY - minY;
        var height = maxHeight - (minHeight == float.MaxValue ? 0 : minHeight);

        return MathF.Max(width, MathF.Max(depth, height));
    }

    private static void MergeWithOffset(
        Dictionary<string, Mesh> meshes, string materialKey, Mesh source, Vector3 offset)
    {
        if (!meshes.TryGetValue(materialKey, out var target))
        {
            target = new Mesh { Name = source.Name, MaterialName = source.MaterialName };
            meshes[materialKey] = target;
        }

        var baseIndex = target.Vertices.Count;
        foreach (var v in source.Vertices)
            target.Vertices.Add(new Vertex(v.Position + offset, v.Normal, v.UV));

        foreach (var tri in source.Triangles)
            target.Triangles.Add(new Triangle(tri.V0 + baseIndex, tri.V1 + baseIndex, tri.V2 + baseIndex));
    }
}

/// <summary>
///     Result information from a building DAE export operation.
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
            return
                $"Exported {BuildingsExported} buildings ({MeshCount} meshes, {TotalVertices:N0} verts, {TotalTriangles:N0} tris) to {OutputPath}";

        return
            $"Exported building {OsmId} ({MeshCount} meshes, {TotalVertices} verts, {TotalTriangles} tris) to {OutputPath}";
    }
}