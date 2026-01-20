using System.Diagnostics;
using System.Text.Json;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Export;
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
        var isGeoTiffSource = false; // Track source type for spike prevention strategy

        try
        {
            // 2. Load or use heightmap (priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory)
            perfLog.LogSection("Heightmap Loading");
            var sw = Stopwatch.StartNew();

            if (parameters.HeightmapImage != null)
            {
                heightmapImage = parameters.HeightmapImage;
                shouldDisposeHeightmap = false; // Caller owns this
                isGeoTiffSource = false; // Provided image, assume pre-processed
                perfLog.Timing("Using provided HeightmapImage");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.HeightmapPath))
            {
                perfLog.Info($"Loading heightmap from PNG: {parameters.HeightmapPath}");
                heightmapImage = Image.Load<L16>(parameters.HeightmapPath);
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                isGeoTiffSource = false; // PNG has valid peaks at maxHeight
                perfLog.Timing($"Loaded PNG heightmap: {sw.ElapsedMilliseconds}ms");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.GeoTiffPath))
            {
                // Load from single GeoTIFF file
                perfLog.Info($"Loading heightmap from GeoTIFF: {parameters.GeoTiffPath}");
                var geoTiffResult = await LoadFromGeoTiffAsync(parameters.GeoTiffPath, parameters, perfLog);
                heightmapImage = geoTiffResult;
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                isGeoTiffSource = true; // GeoTIFF may have data artifacts
                perfLog.Timing($"Loaded GeoTIFF heightmap: {sw.Elapsed.TotalSeconds:F2}s");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.GeoTiffDirectory))
            {
                // Combine and load from multiple GeoTIFF tiles
                perfLog.Info($"Loading heightmap from GeoTIFF directory: {parameters.GeoTiffDirectory}");
                var geoTiffResult = await LoadFromGeoTiffDirectoryAsync(parameters.GeoTiffDirectory, parameters, perfLog);
                heightmapImage = geoTiffResult;
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
                isGeoTiffSource = true; // GeoTIFF may have data artifacts
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
            UnifiedSmoothingResult? unifiedResult = null;
            float[,]? heightMap2DForSpawn = null;
            
            if (parameters.Materials.Any(m => m.RoadParameters != null))
            {
                perfLog.LogSection("Road Smoothing");
                sw.Restart();
                perfLog.Info("Applying road smoothing...");

                (smoothingResult, unifiedResult) = ApplyRoadSmoothing(
                    heights,
                    parameters.Materials,
                    parameters.MetersPerPixel,
                    parameters.Size,
                    parameters.EnableCrossMaterialHarmonization,
                    parameters.GlobalJunctionDetectionRadiusMeters,
                    parameters.GlobalJunctionBlendDistanceMeters,
                    parameters.FlipMaterialProcessingOrder,
                    debugBaseDir,
                    perfLog);

                if (smoothingResult != null)
                {
                    heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
                    heightMap2DForSpawn = smoothingResult.ModifiedHeightMap;
                    perfLog.Timing($"Road smoothing completed: {sw.Elapsed.TotalSeconds:F2}s");
                    perfLog.Info("Road smoothing completed successfully!");
                }

                // 3a-2. Export road mesh to DAE if requested
                if (parameters.ExportRoadMeshDae && unifiedResult?.Network != null)
                {
                    await ExportRoadMeshDaeAsync(unifiedResult.Network, outputPath, parameters, debugBaseDir, perfLog);
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
            
            // Try to extract spawn point from the unified road network first (works for PNG roads)
            // This captures splines created during smoothing, not just pre-built OSM splines.
            // Fall back to the material-based extraction for backward compatibility with OSM roads.
            if (unifiedResult?.Network != null && unifiedResult.Network.Splines.Count > 0)
            {
                // Use the new network-based extraction (works for both PNG and OSM)
                parameters.ExtractedSpawnPoint = SpawnPointData.ExtractFromNetwork(
                    unifiedResult.Network,
                    heightMap2DForSpawn,
                    parameters.Size,
                    parameters.MetersPerPixel,
                    parameters.TerrainBaseHeight);
            }
            else
            {
                // Fallback to material-based extraction (for OSM roads with PreBuiltSplines)
                parameters.ExtractedSpawnPoint = SpawnPointData.ExtractFromRoads(
                    parameters.Materials,
                    heightMap2DForSpawn,
                    parameters.Size,
                    parameters.MetersPerPixel,
                    parameters.TerrainBaseHeight);
            }
            
            if (parameters.ExtractedSpawnPoint != null)
            {
                var sp = parameters.ExtractedSpawnPoint;
                perfLog.Info($"Extracted spawn point: ({sp.X:F1}, {sp.Y:F1}, {sp.Z:F1}) - {(sp.IsOnRoad ? $"on road '{sp.SourceMaterialName}'" : "terrain center")}");
                perfLog.Timing($"Spawn point extraction: {sw.ElapsedMilliseconds}ms");
            }

            // 4. Process material layers
            // IMPORTANT: If road smoothing was applied, the MaterialPainter has generated
            // correct layer maps using RoadSurfaceWidthMeters. We need to save these and
            // update the MaterialDefinition.LayerImagePath so MaterialLayerProcessor uses them.
            perfLog.LogSection("Material Layer Processing");
            sw.Restart();
            perfLog.Info("Processing material layers...");
            
            // If unified result has MaterialLayers, save them and update material definitions
            if (unifiedResult?.MaterialLayers != null && unifiedResult.MaterialLayers.Count > 0)
            {
                perfLog.Info($"Updating {unifiedResult.MaterialLayers.Count} road material layer maps from spline-based painting...");
                await UpdateRoadMaterialLayersAsync(parameters.Materials, unifiedResult.MaterialLayers, debugBaseDir!, perfLog);
            }
            
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
            // Strategy differs by source:
            // - GeoTIFF: Full spike prevention (may have data artifacts, values exceeding maxHeight)
            // - PNG: Only fix NaN/negative (peaks at maxHeight are valid, not spikes)
            var spikeFixCount = 0;
            var nanCount = 0;
            var negativeCount = 0;
            var overMaxCount = 0;
            var nearMaxCount = 0;
            
            // Calculate a reasonable "normal" height from the data (for GeoTIFF spike replacement)
            float medianHeight = 0.23f; // BeamNG default
            var nearMaxThreshold = parameters.MaxHeight * 0.99f;
            
            if (isGeoTiffSource)
            {
                var validHeights = heights.Where(h => !float.IsNaN(h) && !float.IsInfinity(h) && h >= 0 && h < parameters.MaxHeight * 0.95f).ToList();
                medianHeight = validHeights.Count > 0 
                    ? validHeights.OrderBy(h => h).ElementAt(validHeights.Count / 2) 
                    : 0.23f;
                perfLog.Info($"  Pre-save analysis (GeoTIFF): median height = {medianHeight:F2}m, maxHeight = {parameters.MaxHeight:F2}m");
            }
            else
            {
                perfLog.Info($"  Pre-save analysis (PNG): maxHeight = {parameters.MaxHeight:F2}m (peaks at maxHeight are valid)");
            }
            
            for (var i = 0; i < terrain.Data.Length; i++)
            {
                var height = heights[i];
                var needsFix = false;
                
                // Check for problematic values - some checks only apply to GeoTIFF
                if (float.IsNaN(height) || float.IsInfinity(height))
                {
                    // Always fix NaN/Infinity - these are invalid for any source
                    nanCount++;
                    needsFix = true;
                }
                else if (height < 0)
                {
                    // Always fix negative heights - terrain can't go below 0
                    negativeCount++;
                    needsFix = true;
                }
                else if (isGeoTiffSource && height >= parameters.MaxHeight)
                {
                    // GeoTIFF ONLY: Values at/above maxHeight are data artifacts
                    // For PNG: values at maxHeight are valid peaks (16-bit 65535 maps to maxHeight)
                    overMaxCount++;
                    needsFix = true;
                }
                else if (isGeoTiffSource && height >= nearMaxThreshold)
                {
                    // GeoTIFF ONLY: Check for isolated near-max spikes
                    // For PNG: near-max values are valid terrain features
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
                if (overMaxCount > 0) perfLog.Warning($"    - Over-max values (>= {parameters.MaxHeight}m): {overMaxCount} [GeoTIFF only]");
                if (nearMaxCount > 0) perfLog.Warning($"    - Isolated near-max spikes: {nearMaxCount} [GeoTIFF only]");
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

    private (SmoothingResult?, UnifiedSmoothingResult?) ApplyRoadSmoothing(
        float[] heightMap1D,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization,
        float globalJunctionDetectionRadius,
        float globalJunctionBlendDistance,
        bool flipMaterialProcessingOrder,
        string? debugBaseDir,
        TerrainCreationLogger log)
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
            globalJunctionBlendDistance,
            flipMaterialProcessingOrder);

        if (unifiedResult == null)
            return (null, null);

        // Convert to SmoothingResult for compatibility with existing terrain creation flow
        // Create a minimal road mask for the geometry wrapper
        var minimalRoadMask = new byte[size, size];
        var defaultParams = materials
            .FirstOrDefault(m => m.RoadParameters != null)?.RoadParameters
            ?? new RoadSmoothingParameters();

        var smoothingResult = unifiedResult.ToSmoothingResult(minimalRoadMask, defaultParams);
        return (smoothingResult, unifiedResult);
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

    /// <summary>
    ///     Updates road material layer image paths with correctly painted layers from MaterialPainter.
    ///     This ensures that RoadSurfaceWidthMeters is respected for PNG roads (not just OSM roads).
    ///     The MaterialPainter generates layer maps by sampling splines at RoadSurfaceWidthMeters,
    ///     which may differ from the original PNG width.
    /// </summary>
    private async Task UpdateRoadMaterialLayersAsync(
        List<MaterialDefinition> materials,
        Dictionary<string, byte[,]> paintedLayers,
        string debugBaseDir,
        TerrainCreationLogger log)
    {
        foreach (var (materialName, layerData) in paintedLayers)
        {
            // Find the matching material definition
            var material = materials.FirstOrDefault(m => m.MaterialName == materialName);
            if (material == null)
            {
                log.Warning($"  Could not find material '{materialName}' to update layer map");
                continue;
            }

            // Only update if this is a road material
            if (material.RoadParameters == null)
                continue;

            try
            {
                // Save the painted layer to a PNG file
                // MaterialPainter works in terrain-space (Y=0 at bottom, BeamNG convention)
                // PNG images need image-space (Y=0 at top), so we flip Y when saving
                var height = layerData.GetLength(0);
                var width = layerData.GetLength(1);

                var safeName = SanitizeFileName(materialName);
                var layerPath = Path.Combine(debugBaseDir, $"{safeName}_painted_layer.png");

                using var image = new Image<L8>(width, height);
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    // Flip Y to convert from terrain-space (bottom-up) to image-space (top-down)
                    var flippedY = height - 1 - y;
                    image[x, flippedY] = new L8(layerData[y, x]);
                }

                await image.SaveAsPngAsync(layerPath);

                // Update the material definition to use the painted layer
                var oldPath = material.LayerImagePath;
                material.LayerImagePath = layerPath;

                var surfaceWidth = material.RoadParameters.EffectiveRoadSurfaceWidthMeters;
                var corridorWidth = material.RoadParameters.RoadWidthMeters;
                log.Info($"  {materialName}: Updated layer map (surface={surfaceWidth:F1}m, corridor={corridorWidth:F1}m)");
                
                if (!string.IsNullOrEmpty(oldPath))
                    log.Detail($"    Old: {Path.GetFileName(oldPath)} -> New: {Path.GetFileName(layerPath)}");
            }
            catch (Exception ex)
            {
                log.Warning($"  Failed to save painted layer for '{materialName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Exports the road network as a 3D mesh in DAE (Collada) format.
    ///     The mesh uses BeamNG world coordinates, so when placed at position (0,0,0)
    ///     in the BeamNG world editor, the road aligns perfectly with the terrain.
    /// </summary>
    private async Task ExportRoadMeshDaeAsync(
        UnifiedRoadNetwork network,
        string terrainOutputPath,
        TerrainCreationParameters parameters,
        string? debugBaseDir,
        TerrainCreationLogger log)
    {
        var sw = Stopwatch.StartNew();
        log.LogSection("Road Mesh DAE Export");

        try
        {
            // Determine output path
            var outputPath = parameters.RoadMeshDaeOutputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                var outputDir = Path.GetDirectoryName(terrainOutputPath) ?? ".";
                outputPath = Path.Combine(outputDir, $"{parameters.TerrainName}_roads.dae");
            }


            log.Info($"Exporting road mesh to: {outputPath}");
            log.Info($"  Network contains {network.Splines.Count} splines with {network.CrossSections.Count} cross-sections");
            log.Info($"  Terrain size: {parameters.Size}x{parameters.Size} pixels, {parameters.MetersPerPixel}m/pixel");
            log.Info($"  World size: {parameters.Size * parameters.MetersPerPixel}m x {parameters.Size * parameters.MetersPerPixel}m");
            log.Info($"  Terrain base height: {parameters.TerrainBaseHeight}m");

            // Configure mesh options from parameters
            var meshOptions = new RoadMeshOptions
            {
                MaterialName = "road_asphalt",
                TextureRepeatMetersU = parameters.RoadMeshTextureRepeatMeters,
                TextureRepeatMetersV = 1.0f, // V goes 0-1 across road width
                IncludeShoulders = parameters.RoadMeshIncludeShoulders,
                ShoulderWidthMeters = parameters.RoadMeshShoulderWidthMeters,
                SmoothNormals = true
            };

            var exporter = new RoadNetworkDaeExporter();
            RoadDaeExportResult result;

            if (parameters.ExportRoadMeshPerMaterial)
            {
                // Export separate DAE files per material
                var outputDir = Path.GetDirectoryName(outputPath) ?? ".";
                result = await Task.Run(() => exporter.ExportByMaterial(
                    network,
                    outputDir,
                    parameters.Size,
                    parameters.MetersPerPixel,
                    parameters.TerrainBaseHeight,
                    parameters.TerrainName,
                    meshOptions));
            }
            else
            {
                // Export single combined DAE file
                result = await Task.Run(() => exporter.Export(
                    network, 
                    outputPath, 
                    parameters.Size,
                    parameters.MetersPerPixel,
                    parameters.TerrainBaseHeight,
                    meshOptions));
            }

            sw.Stop();

            if (result.Success)
            {
                log.Info($"Road mesh export completed successfully:");
                log.Info($"  Meshes: {result.MeshCount}");
                log.Info($"  Splines processed: {result.SplinesMeshed}");
                log.Info($"  Vertices: {result.TotalVertices:N0}");
                log.Info($"  Triangles: {result.TotalTriangles:N0}");
                log.Info($"  Output: {result.OutputPath}");
                log.Info($"  Place at BeamNG position (0, 0, 0) for perfect terrain alignment");
                log.Timing($"Road mesh export: {sw.ElapsedMilliseconds}ms");
            }
            else
            {
                log.Warning($"Road mesh export completed with warnings: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Failed to export road mesh DAE: {ex.Message}");
            log.Detail($"Stack trace: {ex.StackTrace}");
            // Don't throw - this is optional output, terrain generation should continue
        }
    }

    /// <summary>
    ///     Sanitizes a material name for use as a file name.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim().Trim('.');
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
        log.Info($"Max road slope: {stats.MaxRoadSlope:F2}�");
        log.Info($"Max discontinuity: {stats.MaxDiscontinuity:F3}m");
        log.Info($"Cut volume: {stats.TotalCutVolume:F2} m�");
        log.Info($"Fill volume: {stats.TotalFillVolume:F2} m�");
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