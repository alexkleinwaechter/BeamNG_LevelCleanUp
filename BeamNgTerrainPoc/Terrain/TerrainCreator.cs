using System.Diagnostics;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Processing;
using BeamNgTerrainPoc.Terrain.Services;
using BeamNgTerrainPoc.Terrain.Validation;
using Grille.BeamNG;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain;

/// <summary>
///     Main API for creating BeamNG terrain (.ter) files.
/// </summary>
public class TerrainCreator
{
    private readonly UnifiedRoadSmoother _unifiedRoadSmoother;

    public TerrainCreator()
    {
        _unifiedRoadSmoother = new UnifiedRoadSmoother();
    }

    /// <summary>
    ///     Creates a BeamNG terrain file from the provided parameters (async version).
    /// </summary>
    /// <param name="outputPath">Path where the .ter file will be saved</param>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <returns>True if terrain was created successfully, false otherwise</returns>
    public async Task<bool> CreateTerrainFileAsync(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        // Determine log directory - always use MT_TerrainGeneration/logs subfolder
        var outputDir = Path.GetDirectoryName(outputPath);
        string? debugBaseDir = null;

        // Check if any material has a DebugOutputDirectory set
        // Use the parent of the material's debug dir (which is usually MT_TerrainGeneration/MaterialName)
        var debugDir = parameters.Materials
            .Select(m => m.RoadParameters?.DebugOutputDirectory)
            .FirstOrDefault(d => !string.IsNullOrEmpty(d));
        if (!string.IsNullOrEmpty(debugDir))
        {
            // Go up one level from material-specific folder to the main debug folder (MT_TerrainGeneration)
            var parentDir = Path.GetDirectoryName(debugDir);
            if (!string.IsNullOrEmpty(parentDir))
                debugBaseDir = parentDir;
            else
                debugBaseDir = debugDir;
        }
        else
        {
            // Fallback: create MT_TerrainGeneration folder next to output
            debugBaseDir = Path.Combine(outputDir!, "MT_TerrainGeneration");
        }

        // Always put logs in a 'logs' subfolder
        var logDirectory = Path.Combine(debugBaseDir!, "logs");

        // Ensure the log directory exists
        Directory.CreateDirectory(logDirectory);

        // Initialize terrain creation logger - always active for terrain generation
        using var perfLog = new TerrainCreationLogger(logDirectory, $"TerrainGen_{parameters.Size}");
        var totalSw = Stopwatch.StartNew();

        perfLog.Timing($"Output path: {outputPath}");
        perfLog.Timing($"Terrain size: {parameters.Size}x{parameters.Size}");
        perfLog.Timing($"Max height: {parameters.MaxHeight}");
        perfLog.Timing($"Meters per pixel: {parameters.MetersPerPixel}");
        perfLog.Timing($"Materials count: {parameters.Materials.Count}");
        perfLog.Timing($"Road materials: {parameters.Materials.Count(m => m.RoadParameters != null)}");
        perfLog.LogMemoryUsage("Start");

        // 1. Validate inputs
        perfLog.Info("Validating parameters...");
        var validation = TerrainValidator.Validate(parameters);

        if (!validation.IsValid)
        {
            perfLog.Error("Validation failed:");
            foreach (var error in validation.Errors) perfLog.Error($"  {error}");
            return false;
        }

        // Show warnings
        foreach (var warning in validation.Warnings) perfLog.Warning($"  {warning}");

        Image<L16>? heightmapImage = null;
        var shouldDisposeHeightmap = false;

        try
        {
            // 2. Load or use heightmap (priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory)
            perfLog.LogSection("Heightmap Loading");
            var sw = Stopwatch.StartNew();

            if (parameters.HeightmapImage != null)
            {
                heightmapImage = parameters.HeightmapImage;
                shouldDisposeHeightmap = false; // Caller owns this
                perfLog.Timing("Using provided HeightmapImage");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.HeightmapPath))
            {
                perfLog.Info($"Loading heightmap from PNG: {parameters.HeightmapPath}");
                heightmapImage = Image.Load<L16>(parameters.HeightmapPath);
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                perfLog.Timing($"Loaded PNG heightmap: {sw.ElapsedMilliseconds}ms");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.GeoTiffPath))
            {
                // Load from single GeoTIFF file
                perfLog.Info($"Loading heightmap from GeoTIFF: {parameters.GeoTiffPath}");
                var geoTiffResult = await LoadFromGeoTiffAsync(parameters.GeoTiffPath, parameters, perfLog);
                heightmapImage = geoTiffResult;
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                perfLog.Timing($"Loaded GeoTIFF heightmap: {sw.Elapsed.TotalSeconds:F2}s");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.GeoTiffDirectory))
            {
                // Combine and load from multiple GeoTIFF tiles
                perfLog.Info($"Loading heightmap from GeoTIFF directory: {parameters.GeoTiffDirectory}");
                var geoTiffResult = await LoadFromGeoTiffDirectoryAsync(parameters.GeoTiffDirectory, parameters, perfLog);
                heightmapImage = geoTiffResult;
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                perfLog.Timing($"Loaded GeoTIFF directory heightmap: {sw.Elapsed.TotalSeconds:F2}s");
            }
            else
            {
                perfLog.Error(
                    "No heightmap provided (HeightmapImage, HeightmapPath, GeoTiffPath, or GeoTiffDirectory required)");
                return false;
            }

            // 3. Process heightmap
            perfLog.LogSection("Heightmap Processing");
            sw.Restart();
            perfLog.Info("Processing heightmap...");
            var heights = HeightmapProcessor.ProcessHeightmap(
                heightmapImage,
                parameters.MaxHeight);
            perfLog.Timing($"HeightmapProcessor.ProcessHeightmap: {sw.ElapsedMilliseconds}ms");

            // 3a. Apply road smoothing if road materials exist
            SmoothingResult? smoothingResult = null;
            float[,]? heightMap2DForSpawn = null;
            
            if (parameters.Materials.Any(m => m.RoadParameters != null))
            {
                perfLog.LogSection("Road Smoothing");
                sw.Restart();
                perfLog.Info("Applying road smoothing...");

                smoothingResult = ApplyRoadSmoothing(
                    heights,
                    parameters.Materials,
                    parameters.MetersPerPixel,
                    parameters.Size,
                    parameters.EnableCrossMaterialHarmonization,
                    parameters.GlobalJunctionDetectionRadiusMeters,
                    parameters.GlobalJunctionBlendDistanceMeters);

                if (smoothingResult != null)
                {
                    heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
                    heightMap2DForSpawn = smoothingResult.ModifiedHeightMap;
                    perfLog.Timing($"Road smoothing completed: {sw.Elapsed.TotalSeconds:F2}s");
                    perfLog.Info("Road smoothing completed successfully!");
                }
            }

            // 3b. Extract spawn point from road splines (requires heightmap)
            // This is done INSIDE TerrainCreator because we have access to the smoothed heightmap
            perfLog.LogSection("Spawn Point Extraction");
            sw.Restart();
            
            // Use smoothed heightmap if available, otherwise convert the original
            if (heightMap2DForSpawn == null)
            {
                heightMap2DForSpawn = ConvertTo2DArray(heights, parameters.Size);
            }
            
            parameters.ExtractedSpawnPoint = SpawnPointData.ExtractFromRoads(
                parameters.Materials,
                heightMap2DForSpawn,
                parameters.Size,
                parameters.MetersPerPixel,
                parameters.TerrainBaseHeight);
            
            if (parameters.ExtractedSpawnPoint != null)
            {
                var sp = parameters.ExtractedSpawnPoint;
                perfLog.Info($"Extracted spawn point: ({sp.X:F1}, {sp.Y:F1}, {sp.Z:F1}) - {(sp.IsOnRoad ? $"on road '{sp.SourceMaterialName}'" : "terrain center")}");
                perfLog.Timing($"Spawn point extraction: {sw.ElapsedMilliseconds}ms");
            }

            // 4. Process material layers
            perfLog.LogSection("Material Layer Processing");
            sw.Restart();
            perfLog.Info("Processing material layers...");
            var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
                parameters.Materials,
                parameters.Size);
            perfLog.Timing($"MaterialLayerProcessor.ProcessMaterialLayers: {sw.ElapsedMilliseconds}ms");

            // 5. Create Grille.BeamNG.Lib Terrain object
            perfLog.Info("Building terrain data structure...");
            var materialNames = parameters.Materials
                .Select(m => m.MaterialName)
                .ToList();

            var terrain = new Grille.BeamNG.Terrain(
                parameters.Size,
                materialNames);

            // 6. Fill terrain data with spike prevention
            perfLog.LogSection("Terrain Data Assembly");
            sw.Restart();
            perfLog.Info("Filling terrain data...");
            
            // PRE-SAVE SPIKE PREVENTION: Scan and fix height values before writing
            var spikeFixCount = 0;
            var nanCount = 0;
            var negativeCount = 0;
            var overMaxCount = 0;
            var nearMaxCount = 0;
            
            // Calculate a reasonable "normal" height from the data
            var validHeights = heights.Where(h => !float.IsNaN(h) && !float.IsInfinity(h) && h >= 0 && h < parameters.MaxHeight * 0.95f).ToList();
            var medianHeight = validHeights.Count > 0 
                ? validHeights.OrderBy(h => h).ElementAt(validHeights.Count / 2) 
                : 0.23f; // BeamNG default road elevation
            var nearMaxThreshold = parameters.MaxHeight * 0.99f;
            
            perfLog.Info($"  Pre-save analysis: median valid height = {medianHeight:F2}m, maxHeight = {parameters.MaxHeight:F2}m");
            
            for (var i = 0; i < terrain.Data.Length; i++)
            {
                var height = heights[i];
                var originalHeight = height;
                var needsFix = false;
                
                // Check for problematic values
                if (float.IsNaN(height) || float.IsInfinity(height))
                {
                    nanCount++;
                    needsFix = true;
                }
                else if (height < 0)
                {
                    negativeCount++;
                    needsFix = true;
                }
                else if (height >= parameters.MaxHeight)
                {
                    overMaxCount++;
                    needsFix = true;
                }
                else if (height >= nearMaxThreshold)
                {
                    // Check if this is an isolated near-max value (potential spike)
                    var x = i % parameters.Size;
                    var y = i / parameters.Size;
                    var neighborAvg = GetNeighborAverageHeight(heights, x, y, parameters.Size, 3);
                    
                    // If we're near max but neighbors are much lower, this is a spike
                    if (neighborAvg < parameters.MaxHeight * 0.5f)
                    {
                        nearMaxCount++;
                        needsFix = true;
                    }
                }
                
                if (needsFix)
                {
                    // Try to get a sensible replacement value
                    var x = i % parameters.Size;
                    var y = i / parameters.Size;
                    var neighborAvg = GetNeighborAverageHeight(heights, x, y, parameters.Size, 3);
                    
                    // Use neighbor average if valid, otherwise use median, otherwise use default
                    if (!float.IsNaN(neighborAvg) && neighborAvg >= 0 && neighborAvg < parameters.MaxHeight * 0.95f)
                    {
                        height = neighborAvg;
                    }
                    else if (medianHeight > 0)
                    {
                        height = medianHeight;
                    }
                    else
                    {
                        height = 0.23f; // BeamNG default
                    }
                    
                    heights[i] = height; // Update the array too
                    spikeFixCount++;
                }
                
                terrain.Data[i] = new TerrainData
                {
                    Height = height,
                    Material = materialIndices[i],
                    IsHole = false
                };
            }
            
            perfLog.Timing($"Fill terrain data array: {sw.ElapsedMilliseconds}ms");
            
            // Report pre-save fixes
            if (spikeFixCount > 0)
            {
                perfLog.Warning($"  PRE-SAVE SPIKE PREVENTION: Fixed {spikeFixCount} problematic height values:");
                if (nanCount > 0) perfLog.Warning($"    - NaN/Infinity values: {nanCount}");
                if (negativeCount > 0) perfLog.Warning($"    - Negative values: {negativeCount}");
                if (overMaxCount > 0) perfLog.Warning($"    - Over-max values (>= {parameters.MaxHeight}m): {overMaxCount}");
                if (nearMaxCount > 0) perfLog.Warning($"    - Isolated near-max spikes: {nearMaxCount}");
            }
            else
            {
                perfLog.Info("  ? No problematic height values found");
            }

            // 7. Save using Grille.BeamNG.Lib
            perfLog.LogSection("File Writing");
            sw.Restart();
            perfLog.Info($"Writing terrain file to {outputPath}...");

            // Ensure output directory exists
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

            // Save synchronously (the Save method is synchronous)
            await Task.Run(() => terrain.Save(outputPath, parameters.MaxHeight));
            perfLog.Timing($"terrain.Save: {sw.Elapsed.TotalSeconds:F2}s");

            // 7b. Validate terrain for spikes (informational only - spikes should already be fixed)
            perfLog.LogSection("Spike Validation");
            sw.Restart();
            perfLog.Info("Validating terrain for elevation spikes...");
            var spikeValidation = TerrainSpikeValidator.ValidateTerrainFile(outputPath, parameters.MaxHeight);
            perfLog.Timing($"Spike validation: {sw.ElapsedMilliseconds}ms");
            
            if (!spikeValidation.IsValid)
            {
                perfLog.Warning($"SPIKE VALIDATION WARNING: {spikeValidation.SpikeCount} potential spikes detected after pre-save fix.");
                perfLog.Warning("This may indicate an issue with the source data or smoothing parameters.");
            }
            else
            {
                perfLog.Info("? Terrain spike validation passed");
            }

            // 8. Write terrain.json metadata file
            sw.Restart();
            perfLog.Info("Writing terrain.json metadata file...");
            await WriteTerrainJsonAsync(outputPath, parameters, perfLog);
            perfLog.Timing($"WriteTerrainJsonAsync: {sw.ElapsedMilliseconds}ms");

            // 7a. Save modified heightmap if road smoothing was applied
            if (smoothingResult != null)
            {
                sw.Restart();
                perfLog.Info("Saving modified heightmap...");
                SaveModifiedHeightmap(
                    smoothingResult.ModifiedHeightMap,
                    outputPath,
                    parameters.MaxHeight,
                    parameters.Size,
                    perfLog);
                perfLog.Timing($"SaveModifiedHeightmap: {sw.ElapsedMilliseconds}ms");
            }

            totalSw.Stop();
            perfLog.LogMemoryUsage("End");
            perfLog.Timing($"=== TERRAIN CREATION TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s ===");

            perfLog.Info("Terrain file created successfully!");

            // Display statistics
            var fileInfo = new FileInfo(outputPath);
            perfLog.Info($"File size: {fileInfo.Length:N0} bytes");
            perfLog.Info($"Terrain size: {parameters.Size}x{parameters.Size}");
            perfLog.Info($"Max height: {parameters.MaxHeight}");
            perfLog.Info($"Materials: {materialNames.Count}");

            // Display GeoTIFF info if available
            if (parameters.GeoBoundingBox != null)
            {
                perfLog.Info("=== GeoTIFF Import Info ===");
                perfLog.Info($"Bounding box: {parameters.GeoBoundingBox}");
                perfLog.Info($"Overpass API bbox: {parameters.GeoBoundingBox.ToOverpassBBox()}");
                if (parameters.GeoTiffMinElevation.HasValue && parameters.GeoTiffMaxElevation.HasValue)
                    perfLog.Info(
                        $"Elevation range: {parameters.GeoTiffMinElevation:F1}m - {parameters.GeoTiffMaxElevation:F1}m");
                perfLog.Info("===========================");
            }

            // Display road smoothing statistics if available
            if (smoothingResult != null) DisplaySmoothingStatistics(smoothingResult.Statistics, perfLog);

            return true;
        }
        catch (Exception ex)
        {
            perfLog.Error($"Failed to create terrain file: {ex.Message}");
            perfLog.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
        finally
        {
            // Dispose heightmap if we loaded it
            if (shouldDisposeHeightmap && heightmapImage != null) heightmapImage.Dispose();
        }
    }

    /// <summary>
    ///     Creates a BeamNG terrain file from the provided parameters (synchronous version).
    /// </summary>
    /// <param name="outputPath">Path where the .ter file will be saved</param>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <returns>True if terrain was created successfully, false otherwise</returns>
    public bool CreateTerrainFile(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        return CreateTerrainFileAsync(outputPath, parameters).GetAwaiter().GetResult();
    }

    private SmoothingResult? ApplyRoadSmoothing(
        float[] heightMap1D,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization,
        float globalJunctionDetectionRadius,
        float globalJunctionBlendDistance)
    {
        // Convert 1D heightmap to 2D (already flipped by HeightmapProcessor)
        var heightMap2D = ConvertTo2DArray(heightMap1D, size);

        // Use the unified road smoother for network-centric processing
        var unifiedResult = _unifiedRoadSmoother.SmoothAllRoads(
            heightMap2D,
            materials,
            metersPerPixel,
            size,
            enableCrossMaterialHarmonization,
            globalJunctionDetectionRadius,
            globalJunctionBlendDistance);

        if (unifiedResult == null)
            return null;

        // Convert to SmoothingResult for compatibility with existing terrain creation flow
        // Create a minimal road mask for the geometry wrapper
        var minimalRoadMask = new byte[size, size];
        var defaultParams = materials
            .FirstOrDefault(m => m.RoadParameters != null)?.RoadParameters
            ?? new RoadSmoothingParameters();

        return unifiedResult.ToSmoothingResult(minimalRoadMask, defaultParams);
    }

    /// <summary>
    ///     Loads heightmap from a single GeoTIFF file and populates parameters with geo-metadata.
    ///     Applies cropping if specified in parameters.
    /// </summary>
    private async Task<Image<L16>> LoadFromGeoTiffAsync(string geoTiffPath, TerrainCreationParameters parameters, TerrainCreationLogger log)
    {
        var reader = new GeoTiffReader();

        // Run on background thread since GDAL operations can be slow
        // Pass crop parameters if cropping is enabled
        GeoTiffImportResult result;
        if (parameters.CropGeoTiff && parameters.CropWidth > 0 && parameters.CropHeight > 0)
        {
            log.Info(
                $"Cropping GeoTIFF: offset ({parameters.CropOffsetX}, {parameters.CropOffsetY}), size {parameters.CropWidth}x{parameters.CropHeight}");
            result = await Task.Run(() => reader.ReadGeoTiff(
                geoTiffPath,
                parameters.Size,
                parameters.CropOffsetX,
                parameters.CropOffsetY,
                parameters.CropWidth,
                parameters.CropHeight));
        }
        else
        {
            result = await Task.Run(() => reader.ReadGeoTiff(geoTiffPath, parameters.Size));
        }

        // Populate parameters with geo-metadata
        // CRITICAL: Use Wgs84BoundingBox for Overpass API queries (lat/lon degrees, not projected meters)
        // Fall back to native BoundingBox only if transformation failed AND it looks valid for WGS84
        parameters.GeoBoundingBox = result.Wgs84BoundingBox ??
                                    (result.BoundingBox.IsValidWgs84 ? result.BoundingBox : null);
        parameters.GeoTiffMinElevation = result.MinElevation;
        parameters.GeoTiffMaxElevation = result.MaxElevation;

        // If MaxHeight wasn't explicitly set (<=0), use the elevation range from GeoTIFF
        if (parameters.MaxHeight <= 0)
        {
            parameters.MaxHeight = (float)result.ElevationRange;
            log.Info($"Using GeoTIFF elevation range as MaxHeight: {parameters.MaxHeight:F1}m");

            // Also auto-set the base height to the minimum elevation
            if (parameters.AutoSetBaseHeightFromGeoTiff)
            {
                parameters.TerrainBaseHeight = (float)result.MinElevation;
                log.Info(
                    $"Using GeoTIFF minimum elevation as TerrainBaseHeight: {parameters.TerrainBaseHeight:F1}m");
            }
        }

        // Log the bounding box that will be used for Overpass API
        if (parameters.GeoBoundingBox != null)
        {
            log.Info($"GeoTIFF bounding box for Overpass API: {parameters.GeoBoundingBox.ToOverpassBBox()}");
        }
        else
        {
            log.Warning("Could not determine WGS84 bounding box - OSM road features will not be available.");
            log.Warning(
                $"Native bounding box was: {result.BoundingBox} (likely in projected coordinates, not lat/lon)");
        }

        return result.HeightmapImage;
    }

    /// <summary>
    ///     Combines multiple GeoTIFF tiles from a directory and loads as heightmap.
    ///     Supports cropping the combined result.
    /// </summary>
    private async Task<Image<L16>> LoadFromGeoTiffDirectoryAsync(string geoTiffDirectory,
        TerrainCreationParameters parameters, TerrainCreationLogger log)
    {
        var combiner = new GeoTiffCombiner();

        // Combine and import, passing crop parameters if specified
        GeoTiffImportResult result;
        
        if (parameters.CropGeoTiff && parameters.CropWidth > 0 && parameters.CropHeight > 0)
        {
            log.Info(
                $"Cropping combined GeoTIFF: offset ({parameters.CropOffsetX}, {parameters.CropOffsetY}), " +
                $"size {parameters.CropWidth}x{parameters.CropHeight}");
            
            result = await combiner.CombineAndImportAsync(
                geoTiffDirectory, 
                parameters.Size,
                parameters.CropOffsetX,
                parameters.CropOffsetY,
                parameters.CropWidth,
                parameters.CropHeight);
        }
        else
        {
            result = await combiner.CombineAndImportAsync(geoTiffDirectory, parameters.Size);
        }

        // Populate parameters with geo-metadata
        // CRITICAL: Use Wgs84BoundingBox for Overpass API queries (lat/lon degrees, not projected meters)
        // Fall back to native BoundingBox only if transformation failed AND it looks valid for WGS84
        parameters.GeoBoundingBox = result.Wgs84BoundingBox ??
                                    (result.BoundingBox.IsValidWgs84 ? result.BoundingBox : null);
        parameters.GeoTiffMinElevation = result.MinElevation;
        parameters.GeoTiffMaxElevation = result.MaxElevation;

        // If MaxHeight wasn't explicitly set (<=0), use the elevation range from GeoTIFF
        if (parameters.MaxHeight <= 0)
        {
            parameters.MaxHeight = (float)result.ElevationRange;
            log.Info($"Using combined GeoTIFF elevation range as MaxHeight: {parameters.MaxHeight:F1}m");

            // Also auto-set the base height to the minimum elevation
            if (parameters.AutoSetBaseHeightFromGeoTiff)
            {
                parameters.TerrainBaseHeight = (float)result.MinElevation;
                log.Info(
                    $"Using combined GeoTIFF minimum elevation as TerrainBaseHeight: {parameters.TerrainBaseHeight:F1}m");
            }
        }

        // Log the bounding box that will be used for Overpass API
        if (parameters.GeoBoundingBox != null)
        {
            log.Info(
                $"Combined GeoTIFF bounding box for Overpass API: {parameters.GeoBoundingBox.ToOverpassBBox()}");
        }
        else
        {
            log.Warning("Could not determine WGS84 bounding box - OSM road features will not be available.");
            log.Warning(
                $"Native bounding box was: {result.BoundingBox} (likely in projected coordinates, not lat/lon)");
        }

        return result.HeightmapImage;
    }

    private float[,] ConvertTo2DArray(float[] array1D, int size)
    {
        var array2D = new float[size, size];

        // array1D is already flipped by HeightmapProcessor (bottom-up)
        // Just unpack it into 2D with same orientation
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            array2D[y, x] = array1D[y * size + x];

        return array2D;
    }

    private float[] ConvertTo1DArray(float[,] array2D)
    {
        var size = array2D.GetLength(0);
        var array1D = new float[size * size];

        // Pack 2D into 1D maintaining the orientation
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            array1D[y * size + x] = array2D[y, x];

        return array1D;
    }

    private void SaveModifiedHeightmap(
        float[,] modifiedHeights,
        string outputPath,
        float maxHeight,
        int size,
        TerrainCreationLogger log)
    {
        try
        {
            // Create output path for heightmap (same directory as .ter file)
            var outputDir = Path.GetDirectoryName(outputPath);
            var terrainName = Path.GetFileNameWithoutExtension(outputPath);
            var heightmapOutputPath = Path.Combine(outputDir!, $"{terrainName}_smoothed_heightmap.png");

            // Convert float heights back to 16-bit heightmap
            using var heightmapImage = new Image<L16>(size, size);

            // modifiedHeights is in BeamNG orientation (bottom-up)
            // ImageSharp expects top-down, so flip Y when writing
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Flip Y to convert from bottom-up (BeamNG) to top-down (ImageSharp)
                var flippedY = size - 1 - y;

                // Convert height (0.0 to maxHeight) to 16-bit value (0 to 65535)
                var normalizedHeight = modifiedHeights[y, x] / maxHeight;
                var pixelValue = (ushort)Math.Clamp(normalizedHeight * 65535f, 0, 65535);

                heightmapImage[x, flippedY] = new L16(pixelValue);
            }

            heightmapImage.SaveAsPng(heightmapOutputPath);
            log.Info($"Saved modified heightmap to: {heightmapOutputPath}");
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to save modified heightmap: {ex.Message}");
            // Don't throw - this is optional output
        }
    }

    private void DisplaySmoothingStatistics(SmoothingStatistics stats, TerrainCreationLogger log)
    {
        log.Info("=== Road Smoothing Statistics ===");
        log.Info($"Pixels modified: {stats.PixelsModified:N0}");
        log.Info($"Max road slope: {stats.MaxRoadSlope:F2}°");
        log.Info($"Max discontinuity: {stats.MaxDiscontinuity:F3}m");
        log.Info($"Cut volume: {stats.TotalCutVolume:F2} m³");
        log.Info($"Fill volume: {stats.TotalFillVolume:F2} m³");
        log.Info($"Constraints met: {stats.MeetsAllConstraints}");

        if (stats.ConstraintViolations.Any())
        {
            log.Warning("Constraint violations:");
            foreach (var violation in stats.ConstraintViolations) log.Warning($"  - {violation}");
        }

        log.Info("================================");
    }

    /// <summary>
    ///     Writes the terrain.json metadata file alongside the .ter file.
    /// </summary>
    private async Task WriteTerrainJsonAsync(string terFilePath, TerrainCreationParameters parameters, TerrainCreationLogger log)
    {
        try
        {
            // Extract level name from the output path
            // Expected structure: .../levels/levelname/theTerrain.ter
            var levelName = ExtractLevelName(terFilePath);

            // Create the metadata
            var metadata = TerrainJsonMetadata.FromParameters(parameters, levelName);

            // Build the output path for terrain.json
            var outputDir = Path.GetDirectoryName(terFilePath)!;
            var terrainJsonPath = Path.Combine(outputDir, $"{parameters.TerrainName}.terrain.json");

            // Serialize with pretty printing
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var jsonContent = JsonSerializer.Serialize(metadata, jsonOptions);
            await File.WriteAllTextAsync(terrainJsonPath, jsonContent);

            log.Info($"Wrote terrain metadata to: {terrainJsonPath}");
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to write terrain.json: {ex.Message}");
            // Don't throw - this is optional output, the .ter file is the critical one
        }
    }

    /// <summary>
    ///     Extracts the level name from a file path.
    ///     Looks for "levels" folder and takes the folder name after it.
    ///     Falls back to parent folder name if "levels" pattern not found.
    /// </summary>
    private string ExtractLevelName(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                return "unknown";

            // Split path and look for "levels" folder
            var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            for (var i = 0; i < parts.Length - 1; i++)
                if (parts[i].Equals("levels", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];

            // Fallback: use the immediate parent folder name
            return new DirectoryInfo(directory).Name;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    ///     Calculates the average height of neighboring pixels.
    ///     Used for spike detection and correction during terrain data assembly.
    /// </summary>
    /// <param name="heights">The height array (1D, row-major)</param>
    /// <param name="x">X coordinate</param>
    /// <param name="y">Y coordinate</param>
    /// <param name="size">Terrain size</param>
    /// <param name="radius">Search radius in pixels</param>
    /// <returns>Average height of valid neighbors, or NaN if no valid neighbors</returns>
    private static float GetNeighborAverageHeight(float[] heights, int x, int y, int size, int radius)
    {
        var sum = 0f;
        var count = 0;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue; // Skip center pixel

                var nx = x + dx;
                var ny = y + dy;

                if (nx >= 0 && nx < size && ny >= 0 && ny < size)
                {
                    var index = ny * size + nx;
                    var h = heights[index];
                    
                    // Only include valid heights (not NaN, not negative, not at extreme max)
                    if (!float.IsNaN(h) && !float.IsInfinity(h) && h >= 0)
                    {
                        sum += h;
                        count++;
                    }
                }
            }
        }

        return count > 0 ? sum / count : float.NaN;
    }
}