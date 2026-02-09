using BeamNG.Procedural3D.Exporters;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Exports road networks to DAE (Collada) format for use in BeamNG.drive.
/// The exported mesh uses BeamNG world coordinates, so when placed at position (0,0,0)
/// in the BeamNG world editor, the road mesh aligns perfectly with the terrain.
/// </summary>
public class RoadNetworkDaeExporter
{
    /// <summary>
    /// Exports all roads from a unified road network to a single DAE file.
    /// Each spline becomes a separate mesh within the file.
    /// Coordinates are transformed to BeamNG world space (origin at terrain center).
    /// Terrain base height is added to all elevation values.
    /// </summary>
    /// <param name="network">The unified road network containing all road splines.</param>
    /// <param name="outputPath">The path where the DAE file will be saved.</param>
    /// <param name="terrainSizePixels">Terrain size in pixels (required for coordinate transformation).</param>
    /// <param name="metersPerPixel">Scale factor in meters per pixel (required for coordinate transformation).</param>
    /// <param name="terrainBaseHeight">Base height offset for the terrain (added to all Z coordinates).</param>
    /// <param name="options">Optional mesh generation options. If null, defaults are used.</param>
    /// <returns>Export statistics including number of meshes and vertices generated.</returns>
    public RoadDaeExportResult Export(
        UnifiedRoadNetwork network, 
        string outputPath,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight,
        RoadMeshOptions? options = null)
    {
        options ??= new RoadMeshOptions();
        var result = new RoadDaeExportResult();
        var meshes = new List<BeamNG.Procedural3D.Core.Mesh>();

        // Convert all cross-sections grouped by spline, transforming to world coordinates
        var convertedPaths = CrossSectionConverter.ConvertNetworkToWorldCoordinates(
            network, terrainSizePixels, metersPerPixel, terrainBaseHeight);

        foreach (var (splineId, crossSections) in convertedPaths)
        {
            if (crossSections.Count < 2)
                continue;

            // Get spline info for naming
            var spline = network.GetSplineById(splineId);
            var materialName = network.SplineMaterialMap.GetValueOrDefault(splineId, "road");
            var meshName = $"Road_{materialName}_{splineId}";

            // Create mesh options for this spline
            var splineOptions = new RoadMeshOptions
            {
                MeshName = meshName,
                MaterialName = options.MaterialName,
                TextureRepeatMetersU = options.TextureRepeatMetersU,
                TextureRepeatMetersV = options.TextureRepeatMetersV,
                IncludeShoulders = options.IncludeShoulders,
                ShoulderWidthMeters = options.ShoulderWidthMeters,
                ShoulderDropMeters = options.ShoulderDropMeters,
                ShoulderMaterialName = options.ShoulderMaterialName,
                IncludeCurbs = options.IncludeCurbs,
                CurbHeightMeters = options.CurbHeightMeters,
                SmoothNormals = options.SmoothNormals
            };

            // Build mesh from cross-sections
            var builder = new RoadMeshBuilder()
                .WithOptions(splineOptions)
                .AddCrossSections(crossSections);

            var mesh = builder.Build();

            if (mesh.Vertices.Count > 0 && mesh.Triangles.Count > 0)
            {
                meshes.Add(mesh);
                result.SplinesMeshed++;
                result.TotalVertices += mesh.Vertices.Count;
                result.TotalTriangles += mesh.Triangles.Count;
            }
        }

        if (meshes.Count == 0)
        {
            result.Success = false;
            result.ErrorMessage = "No valid road meshes could be generated from the network.";
            return result;
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Export all meshes to single DAE file
        // Road mesh is in BeamNG's Z-up coordinate system.
        // The ColladaExporter converts to Y-up (Collada standard) with proper
        // handedness preservation: (X, Y, Z) -> (X, Z, -Y)
        var exportOptions = new ColladaExportOptions
        {
            ConvertToZUp = true,     // Convert Z-up to Y-up for Collada
            FlipWindingOrder = false // Handedness preserved by coordinate transform
        };
        var exporter = new ColladaExporter(exportOptions);
        exporter.Export(meshes, outputPath);

        result.Success = true;
        result.OutputPath = outputPath;
        result.MeshCount = meshes.Count;

        return result;
    }

    /// <summary>
    /// Exports roads from a unified road network to separate DAE files per material.
    /// This creates one DAE file per unique material in the network.
    /// Coordinates are transformed to BeamNG world space (origin at terrain center).
    /// Terrain base height is added to all elevation values.
    /// </summary>
    /// <param name="network">The unified road network containing all road splines.</param>
    /// <param name="outputDirectory">The directory where DAE files will be saved.</param>
    /// <param name="terrainSizePixels">Terrain size in pixels (required for coordinate transformation).</param>
    /// <param name="metersPerPixel">Scale factor in meters per pixel (required for coordinate transformation).</param>
    /// <param name="terrainBaseHeight">Base height offset for the terrain (added to all Z coordinates).</param>
    /// <param name="fileNamePrefix">Prefix for the generated files (e.g., "road" -> "road_asphalt.dae").</param>
    /// <param name="options">Optional mesh generation options. If null, defaults are used.</param>
    /// <returns>Export statistics for all generated files.</returns>
    public RoadDaeExportResult ExportByMaterial(
        UnifiedRoadNetwork network,
        string outputDirectory,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight,
        string fileNamePrefix = "road",
        RoadMeshOptions? options = null)
    {
        options ??= new RoadMeshOptions();
        var result = new RoadDaeExportResult();
        Directory.CreateDirectory(outputDirectory);

        // Group splines by material
        var splinesByMaterial = network.SplineMaterialMap
            .GroupBy(kvp => kvp.Value)
            .ToDictionary(g => g.Key, g => g.Select(kvp => kvp.Key).ToList());

        foreach (var (materialName, splineIds) in splinesByMaterial)
        {
            var materialMeshes = new List<BeamNG.Procedural3D.Core.Mesh>();

            foreach (var splineId in splineIds)
            {
                // Get cross-sections for this spline
                var crossSections = network.CrossSections
                    .Where(cs => cs.OwnerSplineId == splineId)
                    .OrderBy(cs => cs.LocalIndex)
                    .ToList();

                if (crossSections.Count < 2)
                    continue;

                // Convert to world coordinates with terrain base height offset
                var convertedPath = CrossSectionConverter.ConvertPathToWorldCoordinates(
                    crossSections, terrainSizePixels, metersPerPixel, terrainBaseHeight);
                if (convertedPath.Count < 2)
                    continue;

                var meshName = $"Road_{splineId}";
                var splineOptions = new RoadMeshOptions
                {
                    MeshName = meshName,
                    MaterialName = materialName,
                    TextureRepeatMetersU = options.TextureRepeatMetersU,
                    TextureRepeatMetersV = options.TextureRepeatMetersV,
                    IncludeShoulders = options.IncludeShoulders,
                    ShoulderWidthMeters = options.ShoulderWidthMeters,
                    ShoulderDropMeters = options.ShoulderDropMeters,
                    ShoulderMaterialName = options.ShoulderMaterialName,
                    IncludeCurbs = options.IncludeCurbs,
                    CurbHeightMeters = options.CurbHeightMeters,
                    SmoothNormals = options.SmoothNormals
                };

                var builder = new RoadMeshBuilder()
                    .WithOptions(splineOptions)
                    .AddCrossSections(convertedPath);

                var mesh = builder.Build();

                if (mesh.Vertices.Count > 0 && mesh.Triangles.Count > 0)
                {
                    materialMeshes.Add(mesh);
                    result.SplinesMeshed++;
                    result.TotalVertices += mesh.Vertices.Count;
                    result.TotalTriangles += mesh.Triangles.Count;
                }
            }

            if (materialMeshes.Count > 0)
            {
                var outputPath = Path.Combine(outputDirectory, $"{fileNamePrefix}_{materialName}.dae");
                // Road mesh is in BeamNG's Z-up coordinate system.
                // The ColladaExporter converts to Y-up (Collada standard) with proper
                // handedness preservation: (X, Y, Z) -> (X, Z, -Y)
                var exportOptions = new ColladaExportOptions
                {
                    ConvertToZUp = true,     // Convert Z-up to Y-up for Collada
                    FlipWindingOrder = false // Handedness preserved by coordinate transform
                };
                var exporter = new ColladaExporter(exportOptions);
                exporter.Export(materialMeshes, outputPath);
                result.MeshCount += materialMeshes.Count;
            }
        }

        result.Success = result.MeshCount > 0;
        result.OutputPath = outputDirectory;

        if (!result.Success)
        {
            result.ErrorMessage = "No valid road meshes could be generated from the network.";
        }

        return result;
    }
}

/// <summary>
/// Result information from a road DAE export operation.
/// </summary>
public class RoadDaeExportResult
{
    /// <summary>
    /// Whether the export completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Path to the exported file or directory.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Number of mesh objects generated.
    /// </summary>
    public int MeshCount { get; set; }

    /// <summary>
    /// Number of splines that were successfully converted to meshes.
    /// </summary>
    public int SplinesMeshed { get; set; }

    /// <summary>
    /// Total number of vertices across all meshes.
    /// </summary>
    public int TotalVertices { get; set; }

    /// <summary>
    /// Total number of triangles across all meshes.
    /// </summary>
    public int TotalTriangles { get; set; }

    /// <summary>
    /// Error message if the export failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public override string ToString()
    {
        if (!Success)
            return $"Export failed: {ErrorMessage}";

        return $"Exported {MeshCount} meshes ({SplinesMeshed} splines) with {TotalVertices:N0} vertices and {TotalTriangles:N0} triangles to {OutputPath}";
    }
}
