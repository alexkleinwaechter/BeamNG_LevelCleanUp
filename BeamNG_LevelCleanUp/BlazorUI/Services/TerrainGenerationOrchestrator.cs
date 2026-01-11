using System.Diagnostics;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using BeamNgTerrainPoc.Terrain.Osm.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Orchestrates the terrain generation process including:
///     - Building material definitions from OSM or PNG sources
///     - Creating coordinate transformers for proper geo-alignment
///     - Executing terrain creation
///     - Running post-generation tasks (TerrainBlock update, spawn points, etc.)
/// </summary>
public class TerrainGenerationOrchestrator
{
    /// <summary>
    ///     Executes the full terrain generation pipeline.
    /// </summary>
    public async Task<GenerationResult> ExecuteAsync(TerrainGenerationState state)
    {
        return await ExecuteInternalAsync(state, null);
    }

    /// <summary>
    ///     Executes terrain generation using a pre-analyzed road network.
    ///     This allows users to preview and modify junction exclusions before generation.
    /// </summary>
    /// <param name="state">The terrain generation state with all settings.</param>
    /// <param name="analysisState">The pre-analyzed network state with exclusions applied.</param>
    /// <returns>Generation result.</returns>
    public async Task<GenerationResult> ExecuteWithPreAnalyzedNetworkAsync(
        TerrainGenerationState state,
        TerrainAnalysisState? analysisState)
    {
        return await ExecuteInternalAsync(state, analysisState);
    }

    /// <summary>
    ///     Internal implementation of terrain generation.
    ///     Runs the heavy computation on a background thread to keep the UI responsive.
    /// </summary>
    private async Task<GenerationResult> ExecuteInternalAsync(
        TerrainGenerationState state,
        TerrainAnalysisState? analysisState)
    {
        var debugPath = state.GetDebugPath();

        // Clear the debug folder before starting a new generation (quick operation, OK on UI thread)
        ClearDebugFolder(debugPath);
        Directory.CreateDirectory(debugPath);

        TerrainCreationParameters? terrainParameters = null;
        var success = false;

        try
        {
            // Run the heavy computation on a background thread to keep the UI responsive
            // This allows Windows to properly handle ALT-TAB and taskbar clicks during generation
            var result = await Task.Run(async () =>
            {
                var creator = new TerrainCreator();

                // Build material definitions
                var orderedMaterials = state.TerrainMaterials.OrderBy(m => m.Order).ToList();
                var materialDefinitions = new List<MaterialDefinition>();

                // Determine effective bounding box (cropped or full)
                var effectiveBoundingBox = GetEffectiveBoundingBox(state);

                // Create coordinate transformer
                var coordinateTransformer = CreateCoordinateTransformer(state, effectiveBoundingBox);

                // Cache for OSM query results
                OsmQueryResult? osmQueryResult = null;

                // Process each material
                foreach (var mat in orderedMaterials)
                {
                    var (layerImagePath, roadParams) = await ProcessMaterialAsync(
                        mat,
                        effectiveBoundingBox,
                        coordinateTransformer,
                        debugPath,
                        state,
                        osmQueryResult,
                        newOsmResult => osmQueryResult = newOsmResult);

                    materialDefinitions.Add(new MaterialDefinition(
                        mat.InternalName,
                        layerImagePath,
                        roadParams));
                }

                // Export ALL OSM layers to osm_layer subfolder (if OSM data is available)
                await ExportAllOsmLayersAsync(
                    osmQueryResult,
                    effectiveBoundingBox,
                    coordinateTransformer,
                    state,
                    debugPath);

                // Build terrain creation parameters
                var parameters = BuildTerrainParameters(state, materialDefinitions, analysisState);

                // Execute terrain creation
                var outputPath = state.GetOutputPath();

                if (analysisState?.HasAnalysis == true)
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Starting terrain generation with pre-analyzed network ({analysisState.SplineCount} splines, " +
                        $"{analysisState.ActiveJunctionCount} active junctions, {analysisState.ExcludedCount} excluded)...");
                else
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Starting terrain generation: {state.TerrainSize}x{state.TerrainSize}, {materialDefinitions.Count} materials...");

                var generationSuccess = await creator.CreateTerrainFileAsync(outputPath, parameters);

                return (Success: generationSuccess, Parameters: parameters);
            }).ConfigureAwait(false);

            success = result.Success;
            terrainParameters = result.Parameters;

            // Update state with auto-calculated values
            UpdateStateFromParameters(state, terrainParameters);

            return new GenerationResult
            {
                Success = success,
                Parameters = terrainParameters
            };
        }
        catch (Exception ex)
        {
            return new GenerationResult
            {
                Success = false,
                Parameters = terrainParameters,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Runs post-generation tasks like TerrainBlock update and spawn point creation.
    /// </summary>
    public async Task RunPostGenerationTasksAsync(
        TerrainGenerationState state,
        TerrainCreationParameters? terrainParameters)
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Running post-generation tasks...");
        var postGenStopwatch = Stopwatch.StartNew();

        await Task.Run(() =>
        {
            var taskStopwatch = Stopwatch.StartNew();

            //// Update TerrainMaterialTextureSet baseTexSize
            //var terrainMaterialsPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(state.WorkingDirectory);
            //if (!string.IsNullOrEmpty(terrainMaterialsPath))
            //{
            //    var pbrHandler = new PbrUpgradeHandler(terrainMaterialsPath, state.LevelName, state.WorkingDirectory);
            //    pbrHandler.EnsureTerrainMaterialTextureSetSize(state.TerrainSize);
            //    PubSubChannel.SendMessage(PubSubMessageType.Info,
            //        $"[Perf] EnsureTerrainMaterialTextureSetSize: {taskStopwatch.ElapsedMilliseconds}ms");

            //    // Resize base textures
            //    taskStopwatch.Restart();
            //    var resizedCount = TerrainTextureHelper.ResizeBaseTexturesToTerrainSize(
            //        state.WorkingDirectory, state.TerrainSize);
            //    PubSubChannel.SendMessage(PubSubMessageType.Info,
            //        $"[Perf] ResizeBaseTexturesToTerrainSize: {taskStopwatch.ElapsedMilliseconds}ms ({resizedCount} textures)");
            //}
            //else
            //{
            //    PubSubChannel.SendMessage(PubSubMessageType.Warning,
            //        "Could not find terrain materials.json for PBR texture set update.");
            //}

            // Update TerrainBlock
            if (state.UpdateTerrainBlock)
            {
                taskStopwatch.Restart();
                var terrainBlockUpdated = TerrainBlockUpdater.UpdateOrCreateTerrainBlock(
                    state.WorkingDirectory,
                    state.TerrainName,
                    state.TerrainSize,
                    state.MaxHeight,
                    state.TerrainBaseHeight,
                    state.MetersPerPixel);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"[Perf] UpdateOrCreateTerrainBlock: {taskStopwatch.ElapsedMilliseconds}ms");

                if (terrainBlockUpdated)
                    PubSubChannel.SendMessage(PubSubMessageType.Info, "TerrainBlock updated in items.level.json");
                else
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        "Could not update TerrainBlock - check warnings");
            }

            // Handle spawn points
            taskStopwatch.Restart();
            HandleSpawnPoints(state, terrainParameters);
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"[Perf] SpawnPoint handling: {taskStopwatch.ElapsedMilliseconds}ms");
        });

        postGenStopwatch.Stop();
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"[Perf] Total post-generation tasks: {postGenStopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    ///     Writes terrain generation logs to files.
    /// </summary>
    public void WriteGenerationLogs(TerrainGenerationState state)
    {
        if (string.IsNullOrEmpty(state.WorkingDirectory))
            return;

        try
        {
            if (state.Messages.Any())
            {
                var messagesPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen.txt");
                File.WriteAllLines(messagesPath, state.Messages);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain generation log written to: {Path.GetFileName(messagesPath)}");
            }

            if (state.Warnings.Any())
            {
                var warningsPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen_Warnings.txt");
                File.WriteAllLines(warningsPath, state.Warnings);
            }

            if (state.Errors.Any())
            {
                var errorsPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen_Errors.txt");
                File.WriteAllLines(errorsPath, state.Errors);
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not write log files: {ex.Message}");
        }
    }

    /// <summary>
    ///     Result of terrain generation execution.
    /// </summary>
    public class GenerationResult
    {
        public bool Success { get; init; }
        public TerrainCreationParameters? Parameters { get; init; }
        public string? ErrorMessage { get; init; }
    }

    #region Private Helpers

    /// <summary>
    ///     Clears all files and subdirectories in the debug folder before starting a new generation.
    ///     This ensures clean debug output without stale files from previous runs.
    /// </summary>
    /// <param name="debugPath">Path to the debug folder (MT_TerrainGeneration).</param>
    private static void ClearDebugFolder(string debugPath)
    {
        if (!Directory.Exists(debugPath))
            return;

        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Clearing previous debug files from: {Path.GetFileName(debugPath)}");

            var filesDeleted = 0;
            var foldersDeleted = 0;

            // Delete all files in the debug folder
            foreach (var file in Directory.GetFiles(debugPath))
                try
                {
                    File.Delete(file);
                    filesDeleted++;
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Could not delete file {Path.GetFileName(file)}: {ex.Message}");
                }

            // Delete all subdirectories (material-specific debug folders)
            foreach (var dir in Directory.GetDirectories(debugPath))
                try
                {
                    Directory.Delete(dir, true);
                    foldersDeleted++;
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Could not delete folder {Path.GetFileName(dir)}: {ex.Message}");
                }

            if (filesDeleted > 0 || foldersDeleted > 0)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Cleared {filesDeleted} file(s) and {foldersDeleted} folder(s) from debug directory");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not fully clear debug folder: {ex.Message}");
        }
    }

    private static GeoBoundingBox? GetEffectiveBoundingBox(TerrainGenerationState state)
    {
        if (state.CropResult is { NeedsCropping: true, CroppedBoundingBox: not null })
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Using CROPPED bounding box for OSM: {state.CropResult.CroppedBoundingBox}");
            return state.CropResult.CroppedBoundingBox;
        }

        if (state.GeoBoundingBox != null)
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Using FULL bounding box for OSM: {state.GeoBoundingBox}");

        return state.GeoBoundingBox;
    }

    private static GeoCoordinateTransformer? CreateCoordinateTransformer(
        TerrainGenerationState state,
        GeoBoundingBox? effectiveBoundingBox)
    {
        if (state.GeoTiffGeoTransform == null ||
            state.GeoTiffProjectionWkt == null ||
            effectiveBoundingBox == null)
            return null;

        GeoCoordinateTransformer transformer;

        if (state.CropResult is { NeedsCropping: true })
        {
            // Create adjusted GeoTransform for cropped region
            var croppedGeoTransform = new double[6];
            Array.Copy(state.GeoTiffGeoTransform, croppedGeoTransform, 6);

            croppedGeoTransform[0] = state.GeoTiffGeoTransform[0] +
                                     state.CropResult.OffsetX * state.GeoTiffGeoTransform[1];
            croppedGeoTransform[3] = state.GeoTiffGeoTransform[3] +
                                     state.CropResult.OffsetY * state.GeoTiffGeoTransform[5];

            transformer = new GeoCoordinateTransformer(
                state.GeoTiffProjectionWkt,
                croppedGeoTransform,
                state.CropResult.CropWidth,
                state.CropResult.CropHeight,
                state.TerrainSize);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Using CROPPED coordinate transformer: crop origin ({state.CropResult.OffsetX}, {state.CropResult.OffsetY}), " +
                $"crop size {state.CropResult.CropWidth}x{state.CropResult.CropHeight} -> terrain {state.TerrainSize}x{state.TerrainSize}");
        }
        else
        {
            transformer = new GeoCoordinateTransformer(
                state.GeoTiffProjectionWkt,
                state.GeoTiffGeoTransform,
                state.GeoTiffOriginalWidth,
                state.GeoTiffOriginalHeight,
                state.TerrainSize);
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Using GDAL coordinate transformer for OSM features (reprojection: {transformer.UsesReprojection})");

        return transformer;
    }

    private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams)> ProcessMaterialAsync(
        TerrainMaterialSettings.TerrainMaterialItemExtended mat,
        GeoBoundingBox? effectiveBoundingBox,
        GeoCoordinateTransformer? coordinateTransformer,
        string debugPath,
        TerrainGenerationState state,
        OsmQueryResult? osmQueryResult,
        Action<OsmQueryResult> setOsmQueryResult)
    {
        RoadSmoothingParameters? roadParams = null;
        string? layerImagePath = null;

        if (mat.LayerSourceType == LayerSourceType.OsmFeatures &&
            mat.SelectedOsmFeatures?.Any() == true &&
            effectiveBoundingBox != null)
        {
            (layerImagePath, roadParams, osmQueryResult) = await ProcessOsmMaterialAsync(
                mat, effectiveBoundingBox, coordinateTransformer, debugPath, state, osmQueryResult);

            if (osmQueryResult != null)
                setOsmQueryResult(osmQueryResult);
        }
        else if (mat.LayerSourceType == LayerSourceType.PngFile)
        {
            layerImagePath = mat.LayerMapPath;
            if (mat.IsRoadMaterial)
                roadParams = mat.BuildRoadSmoothingParameters(debugPath, state.TerrainBaseHeight);
        }
        else if (mat.IsRoadMaterial)
        {
            roadParams = mat.BuildRoadSmoothingParameters(debugPath, state.TerrainBaseHeight);
        }

        return (layerImagePath, roadParams);
    }

    private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams, OsmQueryResult? OsmResult)>
        ProcessOsmMaterialAsync(
            TerrainMaterialSettings.TerrainMaterialItemExtended mat,
            GeoBoundingBox effectiveBoundingBox,
            GeoCoordinateTransformer? coordinateTransformer,
            string debugPath,
            TerrainGenerationState state,
            OsmQueryResult? osmQueryResult)
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Processing OSM features for material: {mat.InternalName}");

        // Fetch OSM data if not cached
        if (osmQueryResult == null)
        {
            var cache = new OsmQueryCache();
            osmQueryResult = await cache.GetAsync(effectiveBoundingBox);

            if (osmQueryResult == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "Fetching OSM data from Overpass API...");
                var service = new OverpassApiService();
                osmQueryResult = await service.QueryAllFeaturesAsync(effectiveBoundingBox);
                await cache.SetAsync(effectiveBoundingBox, osmQueryResult);
            }
        }

        var processor = new OsmGeometryProcessor();
        if (coordinateTransformer != null)
            processor.SetCoordinateTransformer(coordinateTransformer);

        var fullFeatures = processor.GetFeaturesFromSelections(osmQueryResult, mat.SelectedOsmFeatures);

        RoadSmoothingParameters? roadParams = null;
        string? layerImagePath = null;

        if (mat.IsRoadMaterial)
            (layerImagePath, roadParams) = await ProcessOsmRoadMaterialAsync(
                mat, fullFeatures, effectiveBoundingBox, processor, debugPath, state);
        else
            layerImagePath = await ProcessOsmPolygonMaterialAsync(
                mat, fullFeatures, effectiveBoundingBox, processor, debugPath, state);

        return (layerImagePath, roadParams, osmQueryResult);
    }

    private async Task<(string? LayerImagePath, RoadSmoothingParameters? RoadParams)> ProcessOsmRoadMaterialAsync(
        TerrainMaterialSettings.TerrainMaterialItemExtended mat,
        List<OsmFeature> fullFeatures,
        GeoBoundingBox effectiveBoundingBox,
        OsmGeometryProcessor processor,
        string debugPath,
        TerrainGenerationState state)
    {
        RoadSmoothingParameters? roadParams = null;
        string? layerImagePath = null;

        var lineFeatures = fullFeatures
            .Where(f => f.GeometryType == OsmGeometryType.LineString)
            .ToList();

        if (lineFeatures.Any())
        {
            var minPathLengthMeters = mat.MinPathLengthPixels * state.MetersPerPixel;

            // Get the interpolation type from material settings
            var interpolationType = mat.SplineInterpolationType;

            var splines = processor.ConvertLinesToSplines(
                lineFeatures,
                effectiveBoundingBox,
                state.TerrainSize,
                state.MetersPerPixel,
                interpolationType,
                minPathLengthMeters);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created {splines.Count} splines from {lineFeatures.Count} OSM line features (interpolation: {interpolationType})");

            var osmDebugPath = Path.Combine(debugPath, $"{mat.InternalName}_osm_splines_debug.png");
            processor.ExportOsmSplineDebugImage(splines, state.TerrainSize, state.MetersPerPixel, osmDebugPath);

            roadParams = mat.BuildRoadSmoothingParameters(debugPath, state.TerrainBaseHeight);
            roadParams.PreBuiltSplines = splines;

            // Rasterize layer map FROM THE SPLINES (not from OSM line features)
            // This ensures the layer map matches the interpolated spline path used for elevation smoothing
            var effectiveRoadSurfaceWidth = mat.RoadSurfaceWidthMeters is > 0
                ? mat.RoadSurfaceWidthMeters.Value
                : mat.RoadWidthMeters;

            var roadMask = processor.RasterizeSplinesToLayerMap(
                splines,
                state.TerrainSize,
                state.MetersPerPixel,
                effectiveRoadSurfaceWidth);

            layerImagePath = await SaveLayerMapToPngAsync(roadMask, debugPath, mat.InternalName);
        }

        return (layerImagePath, roadParams);
    }

    private async Task<string?> ProcessOsmPolygonMaterialAsync(
        TerrainMaterialSettings.TerrainMaterialItemExtended mat,
        List<OsmFeature> fullFeatures,
        GeoBoundingBox effectiveBoundingBox,
        OsmGeometryProcessor processor,
        string debugPath,
        TerrainGenerationState state)
    {
        var polygonFeatures = fullFeatures
            .Where(f => f.GeometryType == OsmGeometryType.Polygon)
            .ToList();

        if (!polygonFeatures.Any())
            return null;

        var layerMap = processor.RasterizePolygonsToLayerMap(
            polygonFeatures,
            effectiveBoundingBox,
            state.TerrainSize);

        var layerImagePath = await SaveLayerMapToPngAsync(layerMap, debugPath, mat.InternalName);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Rasterized {polygonFeatures.Count} OSM polygons for {mat.InternalName}");

        return layerImagePath;
    }

    private static TerrainCreationParameters BuildTerrainParameters(
        TerrainGenerationState state,
        List<MaterialDefinition> materialDefinitions,
        TerrainAnalysisState? analysisState = null)
    {
        var parameters = new TerrainCreationParameters
        {
            Size = state.TerrainSize,
            MaxHeight = state.MaxHeight,
            MetersPerPixel = state.MetersPerPixel,
            TerrainName = state.TerrainName,
            TerrainBaseHeight = state.TerrainBaseHeight,
            Materials = materialDefinitions,
            EnableCrossMaterialHarmonization = state.EnableCrossMaterialHarmonization,
            GlobalJunctionDetectionRadiusMeters = state.GlobalJunctionDetectionRadiusMeters,
            GlobalJunctionBlendDistanceMeters = state.GlobalJunctionBlendDistanceMeters,
            FlipMaterialProcessingOrder = state.FlipMaterialProcessingOrder,
            AutoSetBaseHeightFromGeoTiff = state.MaxHeight <= 0
        };

        // Pass pre-analyzed network if available
        if (analysisState?.HasAnalysis == true && analysisState.Network != null)
        {
            // Ensure exclusions are applied before passing to terrain generation
            analysisState.ApplyExclusions();
            parameters.PreAnalyzedNetwork = analysisState.Network;

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Using pre-analyzed network: {analysisState.SplineCount} splines, " +
                $"{analysisState.ActiveJunctionCount} active junctions " +
                $"({analysisState.ExcludedCount} excluded)");
        }

        // Set heightmap source
        switch (state.HeightmapSourceType)
        {
            case HeightmapSourceType.Png:
                parameters.HeightmapPath = state.HeightmapPath;
                break;

            case HeightmapSourceType.GeoTiffFile:
                parameters.GeoTiffPath = state.GeoTiffPath;
                ApplyCropSettings(state, parameters);
                break;

            case HeightmapSourceType.GeoTiffDirectory:
                if (!string.IsNullOrEmpty(state.CachedCombinedGeoTiffPath) &&
                    File.Exists(state.CachedCombinedGeoTiffPath))
                {
                    parameters.GeoTiffPath = state.CachedCombinedGeoTiffPath;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        "Using cached combined GeoTIFF (skipping tile re-combination)");
                }
                else
                {
                    parameters.GeoTiffDirectory = state.GeoTiffDirectory;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        "No cached combined GeoTIFF - will combine tiles during generation");
                }

                ApplyCropSettings(state, parameters);
                break;
        }

        return parameters;
    }

    private static void ApplyCropSettings(TerrainGenerationState state, TerrainCreationParameters parameters)
    {
        if (state.CropResult is not { NeedsCropping: true })
            return;

        parameters.CropGeoTiff = true;
        parameters.CropOffsetX = state.CropResult.OffsetX;
        parameters.CropOffsetY = state.CropResult.OffsetY;
        parameters.CropWidth = state.CropResult.CropWidth;
        parameters.CropHeight = state.CropResult.CropHeight;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Applying GeoTIFF crop: offset ({state.CropResult.OffsetX}, {state.CropResult.OffsetY}), " +
            $"size {state.CropResult.CropWidth}x{state.CropResult.CropHeight}");
    }

    private static void UpdateStateFromParameters(TerrainGenerationState state, TerrainCreationParameters parameters)
    {
        if (parameters.GeoTiffMinElevation.HasValue)
            state.GeoTiffMinElevation = parameters.GeoTiffMinElevation;
        if (parameters.GeoTiffMaxElevation.HasValue)
            state.GeoTiffMaxElevation = parameters.GeoTiffMaxElevation;

        if (state.MaxHeight <= 0 && parameters.MaxHeight > 0)
        {
            state.MaxHeight = parameters.MaxHeight;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Max height auto-calculated from GeoTIFF: {state.MaxHeight:F1}m");
        }

        if (parameters.TerrainBaseHeight != state.TerrainBaseHeight && parameters.GeoTiffMinElevation.HasValue)
        {
            state.TerrainBaseHeight = parameters.TerrainBaseHeight;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Base height auto-calculated from GeoTIFF min elevation: {state.TerrainBaseHeight:F1}m");
        }
    }

    private static void HandleSpawnPoints(TerrainGenerationState state, TerrainCreationParameters? terrainParameters)
    {
        var spawnPointExists = SpawnPointUpdater.SpawnPointExists(state.WorkingDirectory);

        if (!spawnPointExists)
        {
            if (terrainParameters?.ExtractedSpawnPoint != null)
            {
                var spawnPoint = CreateSpawnPointFromExtracted(terrainParameters.ExtractedSpawnPoint);
                if (SpawnPointUpdater.CreateSpawnPoint(state.WorkingDirectory, spawnPoint))
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Created spawn point at ({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})");
            }
            else
            {
                if (SpawnPointUpdater.CreateSpawnPoint(state.WorkingDirectory))
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        "Created default spawn point 'spawn_default_MT'");
            }
        }
        else if (terrainParameters?.ExtractedSpawnPoint != null)
        {
            var spawnPoint = CreateSpawnPointFromExtracted(terrainParameters.ExtractedSpawnPoint);
            if (SpawnPointUpdater.UpdateSpawnPoint(state.WorkingDirectory, spawnPoint))
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Spawn point updated at ({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})");
        }
        else
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "Could not extract spawn point from terrain generation");
        }
    }

    private static SpawnPointSuggestion CreateSpawnPointFromExtracted(SpawnPointData extracted)
    {
        return new SpawnPointSuggestion
        {
            X = extracted.X,
            Y = extracted.Y,
            Z = extracted.Z,
            RotationMatrix = extracted.RotationMatrix,
            IsOnRoad = extracted.IsOnRoad,
            SourceMaterialName = extracted.SourceMaterialName
        };
    }

    private static async Task<string> SaveLayerMapToPngAsync(byte[,] layerMap, string debugPath, string materialName)
    {
        var height = layerMap.GetLength(0);
        var width = layerMap.GetLength(1);

        var safeName = string.Join("_", materialName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(debugPath, $"{safeName}_osm_layer.png");

        using var image = new Image<L8>(width, height);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = new L8(layerMap[y, x]);

        await image.SaveAsPngAsync(filePath);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Saved OSM layer map: {Path.GetFileName(filePath)}");

        return filePath;
    }

    /// <summary>
    ///     Exports ALL available OSM feature types as individual 8-bit PNG layer maps.
    ///     This happens automatically when terrain generation uses a GeoTIFF with valid WGS84 bounding box.
    ///     Each unique combination of category + subcategory + geometry type gets its own file.
    ///     The files are saved to {debugPath}/osm_layer/ folder.
    /// </summary>
    /// <param name="osmQueryResult">The OSM query result (may be null if no OSM data fetched).</param>
    /// <param name="effectiveBoundingBox">The WGS84 bounding box (possibly cropped).</param>
    /// <param name="coordinateTransformer">Optional GDAL transformer for projected CRS.</param>
    /// <param name="state">The terrain generation state.</param>
    /// <param name="debugPath">The debug output folder (MT_TerrainGeneration).</param>
    private static async Task ExportAllOsmLayersAsync(
        OsmQueryResult? osmQueryResult,
        GeoBoundingBox? effectiveBoundingBox,
        GeoCoordinateTransformer? coordinateTransformer,
        TerrainGenerationState state,
        string debugPath)
    {
        // Only export if:
        // 1. We have OSM data available (either from material processing or we can fetch it)
        // 2. We have a valid GeoTIFF-based heightmap (not PNG)
        // 3. We can fetch OSM data (valid WGS84 bounding box)

        var isGeoTiffSource = state.HeightmapSourceType == HeightmapSourceType.GeoTiffFile ||
                              state.HeightmapSourceType == HeightmapSourceType.GeoTiffDirectory;

        if (!isGeoTiffSource || !state.CanFetchOsmData || effectiveBoundingBox == null)
            // OSM layer export not applicable - silently skip
            return;

        try
        {
            // If no OSM data was fetched during material processing, fetch it now
            if (osmQueryResult == null)
            {
                var cache = new OsmQueryCache();
                osmQueryResult = await cache.GetAsync(effectiveBoundingBox);

                if (osmQueryResult == null)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        "Fetching OSM data for layer export...");
                    var service = new OverpassApiService();
                    osmQueryResult = await service.QueryAllFeaturesAsync(effectiveBoundingBox);
                    await cache.SetAsync(effectiveBoundingBox, osmQueryResult);
                }
            }

            if (osmQueryResult.Features.Count == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No OSM features found in bounding box - skipping layer export");
                return;
            }

            // Export all OSM layers
            var exporter = new OsmLayerExporter();
            var exportedCount = await exporter.ExportAllOsmLayersAsync(
                osmQueryResult,
                effectiveBoundingBox,
                coordinateTransformer,
                state.TerrainSize,
                state.MetersPerPixel,
                debugPath);

            if (exportedCount > 0)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Exported {exportedCount} OSM layer maps to osm_layer folder");
        }
        catch (Exception ex)
        {
            // Don't fail the terrain generation if OSM layer export fails
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"OSM layer export failed (terrain generation will continue): {ex.Message}");
        }
    }

    #endregion
}