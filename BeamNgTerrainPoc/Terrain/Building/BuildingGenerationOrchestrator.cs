using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using Grille.BeamNG;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Orchestrates the full building generation pipeline:
///   1. Parse OSM building features into BuildingData (with terrain height sampling)
///   2. Export DAE mesh files for each building
///   3. Deploy material textures (bundled or placeholder)
///   4. Write BeamNG scene files (items.level.json + materials.json)
///
/// Designed to be called after terrain generation completes, so the .ter heightmap
/// is available for ground elevation sampling.
/// </summary>
public class BuildingGenerationOrchestrator
{
    /// <summary>
    /// Runs the full building generation pipeline.
    /// </summary>
    /// <param name="osmQueryResult">OSM query result containing building features.</param>
    /// <param name="bbox">Geographic bounding box for coordinate transformation.</param>
    /// <param name="geometryProcessor">OsmGeometryProcessor (with coordinate transformer set if available).</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per terrain pixel).</param>
    /// <param name="maxHeight">Max height used when writing the .ter file (needed to read it back).</param>
    /// <param name="terrainBaseHeight">Base height offset (e.g., GeoTIFF min elevation) added to all Z coordinates.</param>
    /// <param name="terrainFilePath">Absolute path to the generated .ter file.</param>
    /// <param name="workingDirectory">Absolute path to the level directory.</param>
    /// <param name="levelName">Level name (used for BeamNG-relative paths).</param>
    /// <param name="selectedFeatureIds">Optional set of feature IDs to include. When provided,
    /// only buildings matching these IDs are generated. When null, all buildings are included.</param>
    /// <param name="clusterCellSizeMeters">Grid cell size for building clustering (meters).
    /// When > 0, nearby buildings are merged into combined DAE files to reduce draw calls.
    /// When 0, each building gets its own DAE file (original behavior).</param>
    /// <returns>Result summary with counts and any errors.</returns>
    public BuildingGenerationResult GenerateBuildings(
        OsmQueryResult osmQueryResult,
        GeoBoundingBox bbox,
        OsmGeometryProcessor geometryProcessor,
        int terrainSize,
        float metersPerPixel,
        float maxHeight,
        float terrainBaseHeight,
        string terrainFilePath,
        string workingDirectory,
        string levelName,
        HashSet<long>? selectedFeatureIds = null,
        float clusterCellSizeMeters = 0f)
    {
        var result = new BuildingGenerationResult();

        try
        {
            // 1. Create height sampler from generated terrain
            Func<float, float, float>? heightSampler = null;
            if (File.Exists(terrainFilePath) && maxHeight > 0)
            {
                heightSampler = CreateHeightSampler(terrainFilePath, maxHeight, metersPerPixel);
                Console.WriteLine("BuildingGenerationOrchestrator: Height sampler created from terrain file");
            }
            else
            {
                Console.WriteLine("BuildingGenerationOrchestrator: No terrain file available, buildings will use elevation 0");
            }

            // 2. Parse buildings from OSM data (filtered by user selection if provided)
            var parser = new OsmBuildingParser(geometryProcessor);
            var buildings = (selectedFeatureIds != null && selectedFeatureIds.Count > 0)
                ? ParseFilteredBuildings(parser, osmQueryResult, selectedFeatureIds, bbox, terrainSize, metersPerPixel, heightSampler)
                : parser.ParseBuildings(osmQueryResult, bbox, terrainSize, metersPerPixel, heightSampler);

            if (buildings.Count == 0)
            {
                result.BuildingsParsed = 0;
                Console.WriteLine("BuildingGenerationOrchestrator: No buildings found in OSM data");
                return result;
            }

            result.BuildingsParsed = buildings.Count;

            // 3. Convert building positions from terrain-space (corner origin) to BeamNG world-space (center origin)
            // Add terrainBaseHeight to Z (same as road mesh pipeline in CrossSectionConverter)
            float halfSizeMeters = terrainSize * metersPerPixel / 2f;
            foreach (var building in buildings)
            {
                building.WorldPosition = new Vector3(
                    building.WorldPosition.X - halfSizeMeters,
                    building.WorldPosition.Y - halfSizeMeters,
                    building.WorldPosition.Z + terrainBaseHeight);
            }

            // 3.5. Optional clustering (after elevation is set, before DAE export)
            List<BuildingCluster>? clusters = null;
            bool useClustering = clusterCellSizeMeters > 0;
            if (useClustering)
            {
                var clusterer = new BuildingClusterer();
                clusters = clusterer.ClusterBuildings(buildings, clusterCellSizeMeters);
                result.ClustersCreated = clusters.Count;
            }

            // 4. Export DAE files
            var materialLibrary = new BuildingMaterialLibrary();
            var daeExporter = new BuildingDaeExporter(materialLibrary);
            var shapesDir = Path.Combine(workingDirectory, "art", "shapes", "MT_buildings");

            BuildingDaeExportResult exportResult;
            if (useClustering && clusters != null)
            {
                exportResult = daeExporter.ExportAllClustered(clusters, shapesDir, (i, total) =>
                {
                    if (i % 50 == 0 || i == total)
                        Console.WriteLine($"BuildingGenerationOrchestrator: Exported {i}/{total} clusters");
                });
            }
            else
            {
                exportResult = daeExporter.ExportAll(buildings, shapesDir, (i, total) =>
                {
                    if (i % 50 == 0 || i == total)
                        Console.WriteLine($"BuildingGenerationOrchestrator: Exported {i}/{total} buildings");
                });
            }

            result.BuildingsExported = exportResult.BuildingsExported;
            result.BuildingsFailed = exportResult.BuildingsFailed;
            result.TotalVertices = exportResult.TotalVertices;
            result.TotalTriangles = exportResult.TotalTriangles;

            if (!exportResult.Success)
            {
                result.ErrorMessage = exportResult.ErrorMessage;
                return result;
            }

            // 5. Deploy textures
            var usedMaterials = materialLibrary.GetUsedMaterials(buildings);
            var textureDir = Path.Combine(shapesDir, "textures");
            result.TexturesDeployed = materialLibrary.DeployTextures(usedMaterials, textureDir);

            // 6. Write scene files
            var sceneWriter = new BuildingSceneWriter();

            // Ensure the "Buildings" SimGroup is registered in the parent items.level.json (add if not exists)
            var parentItemsPath = Path.Combine(workingDirectory, "main", "MissionGroup", "items.level.json");
            sceneWriter.EnsureSimGroupInParent(parentItemsPath, "MissionGroup");

            // Scene items (NDJSON) go into the MissionGroup/Buildings/ subfolder
            var sceneDir = Path.Combine(workingDirectory, "main", "MissionGroup", "MT_buildings");
            var itemsPath = Path.Combine(sceneDir, "items.level.json");
            var shapePath = $"/levels/{levelName}/art/shapes/MT_buildings/";

            if (useClustering && clusters != null)
            {
                result.SceneItemsWritten = sceneWriter.WriteClusteredSceneItems(clusters, itemsPath, shapePath);
            }
            else
            {
                result.SceneItemsWritten = sceneWriter.WriteSceneItems(buildings, itemsPath, shapePath);
            }

            // Material definitions
            var materialsPath = Path.Combine(shapesDir, "main.materials.json");
            var texturePath = $"/levels/{levelName}/art/shapes/MT_buildings/textures/";
            result.MaterialsWritten = sceneWriter.WriteMaterials(usedMaterials, materialsPath, texturePath);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"BuildingGenerationOrchestrator: Error - {ex.Message}");
        }

        return result;
    }

    private static List<BeamNG.Procedural3D.Building.BuildingData> ParseFilteredBuildings(
        OsmBuildingParser parser,
        OsmQueryResult osmQueryResult,
        HashSet<long> selectedFeatureIds,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler)
    {
        var filteredFeatures = osmQueryResult.GetFeaturesByCategory("building")
            .Where(f => f.GeometryType == OsmGeometryType.Polygon &&
                        selectedFeatureIds.Contains(f.Id))
            .ToList();
        Console.WriteLine($"BuildingGenerationOrchestrator: Filtered to {filteredFeatures.Count} " +
            $"of {selectedFeatureIds.Count} user-selected building features");
        return parser.ParseBuildingFeatures(filteredFeatures, bbox, terrainSize, metersPerPixel, heightSampler);
    }

    /// <summary>
    /// Creates a height sampler function from a generated .ter file.
    /// The sampler accepts (X, Y) in terrain-space meters (corner origin)
    /// and returns the ground elevation at that point.
    /// </summary>
    private static Func<float, float, float> CreateHeightSampler(
        string terrainFilePath, float maxHeight, float metersPerPixel)
    {
        var terrain = new Grille.BeamNG.Terrain(terrainFilePath, maxHeight);
        int size = terrain.Size;

        // Extract heights to a 2D array for fast bilinear sampling
        // Array indexing: [y, x] (row-major, matching SampleHeightmapBilinear convention)
        var heightMap = new float[size, size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                heightMap[y, x] = terrain.Data[x, y].Height;
            }
        }

        return (worldX, worldY) =>
        {
            // Convert from meters to pixel coordinates
            float pixelX = worldX / metersPerPixel;
            float pixelY = worldY / metersPerPixel;

            // Bilinear interpolation for smooth elevation
            return SampleHeightmapBilinear(heightMap, pixelX, pixelY);
        };
    }

    /// <summary>
    /// Bilinear interpolation sampling of a 2D heightmap.
    /// </summary>
    private static float SampleHeightmapBilinear(float[,] heightMap, float x, float y)
    {
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);

        x = Math.Clamp(x, 0, width - 1.001f);
        y = Math.Clamp(y, 0, height - 1.001f);

        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);

        float fx = x - x0;
        float fy = y - y0;

        float h00 = heightMap[y0, x0];
        float h10 = heightMap[y0, x1];
        float h01 = heightMap[y1, x0];
        float h11 = heightMap[y1, x1];

        float h0 = h00 + (h10 - h00) * fx;
        float h1 = h01 + (h11 - h01) * fx;
        return h0 + (h1 - h0) * fy;
    }
}

/// <summary>
/// Result summary from building generation.
/// </summary>
public class BuildingGenerationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int BuildingsParsed { get; set; }
    public int BuildingsExported { get; set; }
    public int BuildingsFailed { get; set; }
    public int ClustersCreated { get; set; }
    public int TotalVertices { get; set; }
    public int TotalTriangles { get; set; }
    public int TexturesDeployed { get; set; }
    public int SceneItemsWritten { get; set; }
    public int MaterialsWritten { get; set; }

    public override string ToString()
    {
        if (!Success)
            return $"Building generation failed: {ErrorMessage}";

        var clusterInfo = ClustersCreated > 0 ? $" in {ClustersCreated} clusters" : "";
        return $"Generated {BuildingsExported} buildings{clusterInfo} ({TotalVertices:N0} verts, {TotalTriangles:N0} tris), " +
               $"{TexturesDeployed} textures, {MaterialsWritten} materials, {SceneItemsWritten} scene items";
    }
}
