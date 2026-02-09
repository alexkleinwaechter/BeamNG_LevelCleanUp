using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Exports ALL OSM feature types as individual 8-bit PNG layer maps.
/// This is called automatically during terrain generation when OSM data is available.
/// Each feature type (category + subcategory + geometry type) gets its own PNG file.
/// </summary>
public class OsmLayerExporter
{
    /// <summary>
    /// Information about a group of OSM features that will be exported as a single layer.
    /// </summary>
    private class FeatureGroupInfo
    {
        public string Category { get; init; } = string.Empty;
        public string SubCategory { get; init; } = string.Empty;
        public OsmGeometryType GeometryType { get; init; }
        public List<OsmFeature> Features { get; init; } = new();
        public string SafeFileName { get; init; } = string.Empty;
        public int FeatureCount => Features.Count;
    }

    /// <summary>
    /// Exports all OSM feature types to 8-bit PNG layer maps.
    /// Each unique combination of category + subcategory + geometry type gets its own file.
    /// Uses parallel processing for improved performance.
    /// </summary>
    /// <param name="osmResult">The OSM query result (from cache or API)</param>
    /// <param name="effectiveBoundingBox">The WGS84 bounding box (possibly cropped)</param>
    /// <param name="coordinateTransformer">Optional GDAL transformer for projected CRS (NOT thread-safe - only used for single-threaded fallback)</param>
    /// <param name="terrainSize">Size of terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor (meters per terrain pixel)</param>
    /// <param name="outputFolder">Target folder where osm_layer subfolder will be created</param>
    /// <returns>Number of layer files exported</returns>
    /// <remarks>
    /// WARNING: The coordinateTransformer parameter is kept for backward compatibility but should NOT be used 
    /// with parallel processing. Use the overload that accepts GeoCoordinateTransformerFactory instead.
    /// When coordinateTransformer is provided, this method falls back to sequential processing.
    /// </remarks>
    public async Task<int> ExportAllOsmLayersAsync(
        OsmQueryResult osmResult,
        GeoBoundingBox effectiveBoundingBox,
        GeoCoordinateTransformer? coordinateTransformer,
        int terrainSize,
        float metersPerPixel,
        string outputFolder)
    {
        // When a transformer is provided but no factory, we must process sequentially
        // because GDAL's CoordinateTransformation is not thread-safe
        return await ExportAllOsmLayersAsync(
            osmResult,
            effectiveBoundingBox,
            transformerFactory: null,
            coordinateTransformer,
            terrainSize,
            metersPerPixel,
            outputFolder);
    }

    /// <summary>
    /// Exports all OSM feature types to 8-bit PNG layer maps.
    /// Each unique combination of category + subcategory + geometry type gets its own file.
    /// Uses parallel processing for improved performance.
    /// </summary>
    /// <param name="osmResult">The OSM query result (from cache or API)</param>
    /// <param name="effectiveBoundingBox">The WGS84 bounding box (possibly cropped)</param>
    /// <param name="transformerFactory">Factory for creating thread-safe transformer instances (recommended for parallel processing)</param>
    /// <param name="terrainSize">Size of terrain in pixels</param>
    /// <param name="metersPerPixel">Scale factor (meters per terrain pixel)</param>
    /// <param name="outputFolder">Target folder where osm_layer subfolder will be created</param>
    /// <returns>Number of layer files exported</returns>
    public Task<int> ExportAllOsmLayersAsync(
        OsmQueryResult osmResult,
        GeoBoundingBox effectiveBoundingBox,
        GeoCoordinateTransformerFactory? transformerFactory,
        int terrainSize,
        float metersPerPixel,
        string outputFolder)
    {
        return ExportAllOsmLayersAsync(
            osmResult,
            effectiveBoundingBox,
            transformerFactory,
            sharedTransformer: null,
            terrainSize,
            metersPerPixel,
            outputFolder);
    }

    /// <summary>
    /// Internal implementation that handles both factory and shared transformer scenarios.
    /// </summary>
    private async Task<int> ExportAllOsmLayersAsync(
        OsmQueryResult osmResult,
        GeoBoundingBox effectiveBoundingBox,
        GeoCoordinateTransformerFactory? transformerFactory,
        GeoCoordinateTransformer? sharedTransformer,
        int terrainSize,
        float metersPerPixel,
        string outputFolder)
    {
        if (osmResult.Features.Count == 0)
        {
            TerrainLogger.Info("OsmLayerExporter: No features to export");
            return 0;
        }

        // Create the osm_layer subfolder
        var osmLayerFolder = Path.Combine(outputFolder, "osm_layer");
        Directory.CreateDirectory(osmLayerFolder);

        // Build feature groups from OSM result
        var featureGroups = BuildFeatureGroups(osmResult);
        
        if (featureGroups.Count == 0)
        {
            TerrainLogger.Info("OsmLayerExporter: No feature groups to export");
            return 0;
        }

        TerrainLogger.Info($"OsmLayerExporter: Exporting {featureGroups.Count} OSM layer types (parallel)...");

        // Pre-assign unique filenames (must be done sequentially to avoid conflicts)
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupsWithPaths = new List<(FeatureGroupInfo Group, string FilePath)>();
        
        foreach (var group in featureGroups)
        {
            var fileName = EnsureUniqueFileName(group.SafeFileName, usedFileNames);
            usedFileNames.Add(fileName);
            var filePath = Path.Combine(osmLayerFolder, $"{fileName}.png");
            groupsWithPaths.Add((group, filePath));
        }

        // Process feature groups
        // When we have a factory, we can process in parallel (each thread gets its own transformer)
        // When we only have a shared transformer, we must process sequentially (GDAL is not thread-safe)
        var exportedCount = 0;
        var failedCount = 0;
        var lockObj = new object();

        var canProcessInParallel = transformerFactory != null || sharedTransformer == null;

        if (canProcessInParallel)
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(groupsWithPaths, 
                    new ParallelOptions 
                    { 
                        // Limit parallelism to avoid excessive memory usage with large terrains
                        MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) 
                    },
                    groupWithPath =>
                    {
                        // Create a thread-local transformer if we have a factory
                        GeoCoordinateTransformer? threadLocalTransformer = null;
                        try
                        {
                            // Create a processor instance per thread for thread safety
                            var processor = new OsmGeometryProcessor();
                            
                            if (transformerFactory != null)
                            {
                                // Create a new transformer for this thread
                                threadLocalTransformer = transformerFactory.Create();
                                processor.SetCoordinateTransformer(threadLocalTransformer);
                            }
                            // If no factory and no shared transformer, processor will use linear interpolation

                            // Rasterize and save synchronously within the parallel task
                            ExportFeatureGroupSync(
                                groupWithPath.Group, 
                                processor, 
                                effectiveBoundingBox, 
                                terrainSize, 
                                metersPerPixel, 
                                groupWithPath.FilePath);

                            lock (lockObj)
                            {
                                exportedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            TerrainLogger.Warning($"OsmLayerExporter: Failed to export {groupWithPath.Group.SafeFileName}: {ex.Message}");
                            lock (lockObj)
                            {
                                failedCount++;
                            }
                        }
                        finally
                        {
                            // Dispose the thread-local transformer
                            threadLocalTransformer?.Dispose();
                        }
                    });
            });
        }
        else
        {
            // Sequential processing when using a shared transformer (not thread-safe)
            TerrainLogger.Info("OsmLayerExporter: Using sequential processing (shared transformer is not thread-safe)");
            await Task.Run(() =>
            {
                foreach (var groupWithPath in groupsWithPaths)
                {
                    try
                    {
                        var processor = new OsmGeometryProcessor();
                        processor.SetCoordinateTransformer(sharedTransformer);

                        ExportFeatureGroupSync(
                            groupWithPath.Group, 
                            processor, 
                            effectiveBoundingBox, 
                            terrainSize, 
                            metersPerPixel, 
                            groupWithPath.FilePath);

                        exportedCount++;
                    }
                    catch (Exception ex)
                    {
                        TerrainLogger.Warning($"OsmLayerExporter: Failed to export {groupWithPath.Group.SafeFileName}: {ex.Message}");
                        failedCount++;
                    }
                }
            });
        }

        TerrainLogger.Info($"OsmLayerExporter: Successfully exported {exportedCount}/{featureGroups.Count} OSM layer maps to {osmLayerFolder}" +
                          (failedCount > 0 ? $" ({failedCount} failed)" : ""));
        return exportedCount;
    }

    /// <summary>
    /// Synchronous version of ExportFeatureGroupAsync for use in parallel processing.
    /// </summary>
    private static void ExportFeatureGroupSync(
        FeatureGroupInfo group,
        OsmGeometryProcessor processor,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        string filePath)
    {
        byte[,] layerMap;

        if (group.GeometryType == OsmGeometryType.LineString)
        {
            // For lines, use a sensible width based on road/feature type
            var lineWidthMeters = GetDefaultLineWidth(group.Category, group.SubCategory);
            var lineWidthPixels = lineWidthMeters / metersPerPixel;
            
            layerMap = processor.RasterizeLinesToLayerMap(
                group.Features, bbox, terrainSize, lineWidthPixels);
        }
        else // Polygon
        {
            layerMap = processor.RasterizePolygonsToLayerMap(
                group.Features, bbox, terrainSize);
        }

        // Save as 8-bit grayscale PNG (synchronously)
        SaveLayerMapSync(layerMap, filePath);
    }

    /// <summary>
    /// Synchronous version of SaveLayerMapAsync for use in parallel processing.
    /// </summary>
    private static void SaveLayerMapSync(byte[,] layerMap, string filePath)
    {
        var height = layerMap.GetLength(0);
        var width = layerMap.GetLength(1);

        using var image = new Image<L8>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new L8(layerMap[y, x]);
            }
        }

        image.SaveAsPng(filePath);
    }

    /// <summary>
    /// Builds feature groups from OSM query result.
    /// Groups features by category + subcategory + geometry type.
    /// </summary>
    private static List<FeatureGroupInfo> BuildFeatureGroups(OsmQueryResult osmResult)
    {
        return osmResult.Features
            .Where(f => f.GeometryType == OsmGeometryType.LineString || f.GeometryType == OsmGeometryType.Polygon)
            .GroupBy(f => new { f.Category, f.SubCategory, f.GeometryType })
            .Select(g => new FeatureGroupInfo
            {
                Category = g.Key.Category,
                SubCategory = g.Key.SubCategory,
                GeometryType = g.Key.GeometryType,
                Features = g.ToList(),
                SafeFileName = SanitizeFileName($"{g.Key.Category}_{g.Key.SubCategory}_{g.Key.GeometryType}")
            })
            .Where(g => g.FeatureCount > 0)
            .OrderBy(g => g.Category)
            .ThenBy(g => g.SubCategory)
            .ThenBy(g => g.GeometryType)
            .ToList();
    }

    /// <summary>
    /// Exports a single feature group to a PNG file.
    /// </summary>
    private static async Task ExportFeatureGroupAsync(
        FeatureGroupInfo group,
        OsmGeometryProcessor processor,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        string filePath)
    {
        byte[,] layerMap;

        if (group.GeometryType == OsmGeometryType.LineString)
        {
            // For lines, use a sensible width based on road/feature type
            var lineWidthMeters = GetDefaultLineWidth(group.Category, group.SubCategory);
            var lineWidthPixels = lineWidthMeters / metersPerPixel;
            
            layerMap = processor.RasterizeLinesToLayerMap(
                group.Features, bbox, terrainSize, lineWidthPixels);
        }
        else // Polygon
        {
            layerMap = processor.RasterizePolygonsToLayerMap(
                group.Features, bbox, terrainSize);
        }

        // Save as 8-bit grayscale PNG
        await SaveLayerMapAsync(layerMap, filePath);
    }

    /// <summary>
    /// Gets the default line width in meters based on feature category and subcategory.
    /// </summary>
    private static float GetDefaultLineWidth(string category, string subCategory)
    {
        if (category == "highway")
        {
            return subCategory switch
            {
                "motorway" => 20.0f,
                "motorway_link" => 12.0f,
                "trunk" => 18.0f,
                "trunk_link" => 10.0f,
                "primary" => 16.0f,
                "primary_link" => 8.0f,
                "secondary" => 14.0f,
                "secondary_link" => 7.0f,
                "tertiary" => 12.0f,
                "tertiary_link" => 6.0f,
                "unclassified" => 10.0f,
                "residential" => 10.0f,
                "living_street" => 8.0f,
                "service" => 6.0f,
                "track" => 4.0f,
                "path" => 2.0f,
                "footway" => 2.0f,
                "pedestrian" => 4.0f,
                "cycleway" => 3.0f,
                "bridleway" => 3.0f,
                "steps" => 2.0f,
                _ => 8.0f // Default for unrecognized road types
            };
        }
        
        if (category == "railway")
        {
            return subCategory switch
            {
                "rail" => 6.0f,
                "light_rail" => 5.0f,
                "subway" => 5.0f,
                "tram" => 4.0f,
                "narrow_gauge" => 3.0f,
                "miniature" => 2.0f,
                _ => 4.0f
            };
        }

        if (category == "waterway")
        {
            return subCategory switch
            {
                "river" => 20.0f,
                "canal" => 15.0f,
                "stream" => 6.0f,
                "drain" => 4.0f,
                "ditch" => 2.0f,
                _ => 6.0f
            };
        }

        // Default for other line features
        return 6.0f;
    }

    /// <summary>
    /// Sanitizes a string to be safe for use as a filename.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new char[name.Length];

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (invalid.Contains(c) || c == ' ' || c == '-')
                result[i] = '_';
            else
                result[i] = char.ToLowerInvariant(c);
        }

        return new string(result);
    }

    /// <summary>
    /// Ensures a filename is unique by appending a counter if necessary.
    /// </summary>
    private static string EnsureUniqueFileName(string baseName, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(baseName))
            return baseName;

        var counter = 2;
        string uniqueName;
        do
        {
            uniqueName = $"{baseName}_{counter}";
            counter++;
        } while (usedNames.Contains(uniqueName));

        return uniqueName;
    }

    /// <summary>
    /// Saves a layer map as an 8-bit grayscale PNG file.
    /// </summary>
    private static async Task SaveLayerMapAsync(byte[,] layerMap, string filePath)
    {
        var height = layerMap.GetLength(0);
        var width = layerMap.GetLength(1);

        using var image = new Image<L8>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new L8(layerMap[y, x]);
            }
        }

        await image.SaveAsPngAsync(filePath);
    }
}
