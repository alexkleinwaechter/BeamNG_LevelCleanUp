using System.Diagnostics;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using BeamNgTerrainPoc.Terrain.Osm.Services;
using BeamNgTerrainPoc.Terrain.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
/// Orchestrates terrain analysis similar to TerrainGenerationOrchestrator
/// but stops after junction detection without modifying terrain.
/// This allows users to preview and modify the road network before committing
/// to full terrain generation.
/// </summary>
public class TerrainAnalysisOrchestrator
{
    /// <summary>
    /// Result of terrain analysis.
    /// </summary>
    public class AnalysisOrchestratorResult
    {
        /// <summary>
        /// Whether the analysis completed successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The underlying analyzer result with network data.
        /// </summary>
        public TerrainAnalyzer.AnalysisResult? AnalyzerResult { get; init; }

        /// <summary>
        /// Error message if analysis failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Elapsed time for analysis.
        /// </summary>
        public TimeSpan ElapsedTime { get; init; }
    }

    /// <summary>
    /// Performs analysis phase only (spline extraction + junction detection).
    /// This method mirrors the material processing in TerrainGenerationOrchestrator
    /// but stops before terrain blending.
    /// </summary>
    /// <param name="state">The terrain generation state with all settings.</param>
    /// <param name="analysisState">Optional existing analysis state to update.</param>
    /// <returns>Analysis result containing the road network and junctions.</returns>
    public async Task<AnalysisOrchestratorResult> AnalyzeAsync(
        TerrainGenerationState state,
        TerrainAnalysisState? analysisState = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Starting terrain analysis (preview mode)...");

            // Validate state
            if (!state.CanGenerate())
            {
                return new AnalysisOrchestratorResult
                {
                    Success = false,
                    ErrorMessage = "Cannot analyze: invalid generation settings."
                };
            }

            // Build material definitions (same as generation)
            var orderedMaterials = state.TerrainMaterials.OrderBy(m => m.Order).ToList();
            var materialDefinitions = new List<MaterialDefinition>();

            // Determine effective bounding box (cropped or full)
            var effectiveBoundingBox = GetEffectiveBoundingBox(state);

            // Create coordinate transformer
            var coordinateTransformer = CreateCoordinateTransformer(state, effectiveBoundingBox);

            // Cache for OSM query results
            OsmQueryResult? osmQueryResult = null;

            var debugPath = state.GetDebugPath();
            Directory.CreateDirectory(debugPath);

            // Process each material to build definitions
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

            // Check if we have any road materials to analyze
            var roadMaterials = materialDefinitions.Where(m => m.RoadParameters != null).ToList();
            if (roadMaterials.Count == 0)
            {
                return new AnalysisOrchestratorResult
                {
                    Success = false,
                    ErrorMessage = "No road materials found. Analysis requires at least one material with road smoothing enabled."
                };
            }

            // Load heightmap for elevation calculations
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Loading heightmap for analysis...");
            var heightMap = await LoadHeightMapAsync(state);
            if (heightMap == null)
            {
                return new AnalysisOrchestratorResult
                {
                    Success = false,
                    ErrorMessage = "Failed to load heightmap for analysis."
                };
            }

            // Run the analyzer
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Analyzing {roadMaterials.Count} road material(s)...");

            var analyzer = new TerrainAnalyzer();
            var analyzerResult = analyzer.Analyze(
                materialDefinitions,
                heightMap,
                state.MetersPerPixel,
                state.TerrainSize,
                state.GlobalJunctionDetectionRadiusMeters,
                generateDebugImage: true);

            if (!analyzerResult.Success)
            {
                return new AnalysisOrchestratorResult
                {
                    Success = false,
                    ErrorMessage = analyzerResult.ErrorMessage ?? "Analysis failed for unknown reason."
                };
            }

            // Update analysis state if provided
            analysisState?.SetAnalysisResult(analyzerResult);

            stopwatch.Stop();

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Analysis complete in {stopwatch.ElapsedMilliseconds}ms: " +
                $"{analyzerResult.SplineCount} splines, {analyzerResult.JunctionCount} junctions");

            // Junction breakdown details are already logged to file by TerrainAnalyzer

            return new AnalysisOrchestratorResult
            {
                Success = true,
                AnalyzerResult = analyzerResult,
                ElapsedTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Analysis failed: {ex.Message}");

            return new AnalysisOrchestratorResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ElapsedTime = stopwatch.Elapsed
            };
        }
    }

    /// <summary>
    /// Saves the analysis debug image to a file.
    /// </summary>
    /// <param name="analysisState">The analysis state with debug image data.</param>
    /// <param name="outputPath">The output file path.</param>
    /// <returns>True if saved successfully.</returns>
    public async Task<bool> SaveDebugImageAsync(TerrainAnalysisState analysisState, string outputPath)
    {
        if (analysisState.DebugImageData == null || analysisState.DebugImageData.Length == 0)
            return false;

        try
        {
            await File.WriteAllBytesAsync(outputPath, analysisState.DebugImageData);
            // Debug image save confirmation is minor status - only log failures
            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to save debug image: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Regenerates the debug image with current exclusion state.
    /// Call this after user modifies exclusions to update the visualization.
    /// </summary>
    /// <param name="analysisState">The analysis state to update.</param>
    /// <param name="metersPerPixel">The terrain meters per pixel.</param>
    /// <returns>True if image was regenerated successfully.</returns>
    public bool RegenerateDebugImage(TerrainAnalysisState analysisState, float metersPerPixel)
    {
        if (analysisState.Network == null)
            return false;

        try
        {
            // Ensure exclusions are applied to the network
            analysisState.ApplyExclusions();

            // Regenerate the debug image
            var imageData = GenerateSimplifiedDebugImage(
                analysisState.Network,
                analysisState.DebugImageWidth,
                analysisState.DebugImageHeight,
                metersPerPixel);

            if (imageData != null)
            {
                analysisState.DebugImageData = imageData;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to regenerate debug image: {ex.Message}");
            return false;
        }
    }

    #region Private Helpers

    private static GeoBoundingBox? GetEffectiveBoundingBox(TerrainGenerationState state)
    {
        if (state.CropResult is { NeedsCropping: true, CroppedBoundingBox: not null })
            return state.CropResult.CroppedBoundingBox;

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

        if (state.CropResult is { NeedsCropping: true })
        {
            var croppedGeoTransform = new double[6];
            Array.Copy(state.GeoTiffGeoTransform, croppedGeoTransform, 6);

            croppedGeoTransform[0] = state.GeoTiffGeoTransform[0] +
                                     state.CropResult.OffsetX * state.GeoTiffGeoTransform[1];
            croppedGeoTransform[3] = state.GeoTiffGeoTransform[3] +
                                     state.CropResult.OffsetY * state.GeoTiffGeoTransform[5];

            return new GeoCoordinateTransformer(
                state.GeoTiffProjectionWkt,
                croppedGeoTransform,
                state.CropResult.CropWidth,
                state.CropResult.CropHeight,
                state.TerrainSize);
        }

        return new GeoCoordinateTransformer(
            state.GeoTiffProjectionWkt,
            state.GeoTiffGeoTransform,
            state.GeoTiffOriginalWidth,
            state.GeoTiffOriginalHeight,
            state.TerrainSize);
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
        {
            var lineFeatures = fullFeatures
                .Where(f => f.GeometryType == OsmGeometryType.LineString)
                .ToList();

            if (lineFeatures.Any())
            {
                var minPathLengthMeters = mat.MinPathLengthPixels * state.MetersPerPixel;
                var interpolationType = mat.SplineInterpolationType;

                var splines = processor.ConvertLinesToSplines(
                    lineFeatures,
                    effectiveBoundingBox,
                    state.TerrainSize,
                    state.MetersPerPixel,
                    interpolationType,
                    minPathLengthMeters);

                roadParams = mat.BuildRoadSmoothingParameters(debugPath, state.TerrainBaseHeight);
                roadParams.PreBuiltSplines = splines;

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
        }

        return (layerImagePath, roadParams, osmQueryResult);
    }

    private async Task<float[,]?> LoadHeightMapAsync(TerrainGenerationState state)
    {
        try
        {
            return state.HeightmapSourceType switch
            {
                HeightmapSourceType.Png => await LoadPngHeightMapAsync(state.HeightmapPath, state.MaxHeight),
                HeightmapSourceType.GeoTiffFile => await LoadGeoTiffHeightMapAsync(state),
                HeightmapSourceType.GeoTiffDirectory => await LoadGeoTiffDirectoryHeightMapAsync(state),
                _ => null
            };
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to load heightmap: {ex.Message}");
            return null;
        }
    }

    private static async Task<float[,]?> LoadPngHeightMapAsync(string? path, float maxHeight)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        return await Task.Run(() =>
        {
            using var image = SixLabors.ImageSharp.Image.Load<L16>(path);
            var heightMap = new float[image.Height, image.Width];

            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = image[x, y];
                    heightMap[y, x] = pixel.PackedValue / 65535f * maxHeight;
                }
            }

            return heightMap;
        });
    }

    private static async Task<float[,]?> LoadGeoTiffHeightMapAsync(TerrainGenerationState state)
    {
        if (string.IsNullOrEmpty(state.GeoTiffPath) || !File.Exists(state.GeoTiffPath))
            return null;

        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();
            GeoTiffImportResult result;

            if (state.CropResult is { NeedsCropping: true })
            {
                result = reader.ReadGeoTiff(
                    state.GeoTiffPath,
                    state.TerrainSize,
                    state.CropResult.OffsetX,
                    state.CropResult.OffsetY,
                    state.CropResult.CropWidth,
                    state.CropResult.CropHeight);
            }
            else
            {
                result = reader.ReadGeoTiff(state.GeoTiffPath, state.TerrainSize);
            }

            return ConvertImageToHeightMap(result.HeightmapImage, result.MinElevation, result.MaxElevation);
        });
    }

    private static async Task<float[,]?> LoadGeoTiffDirectoryHeightMapAsync(TerrainGenerationState state)
    {
        // Use cached combined GeoTIFF if available
        var geoTiffPath = state.CachedCombinedGeoTiffPath;

        if (string.IsNullOrEmpty(geoTiffPath) || !File.Exists(geoTiffPath))
        {
            if (string.IsNullOrEmpty(state.GeoTiffDirectory) || !Directory.Exists(state.GeoTiffDirectory))
                return null;

            // Combine tiles on the fly
            var combiner = new GeoTiffCombiner();

            var tempPath = Path.Combine(Path.GetTempPath(), $"combined_{Guid.NewGuid():N}.tif");
            await combiner.CombineGeoTiffsAsync(state.GeoTiffDirectory, tempPath);
            geoTiffPath = tempPath;
        }

        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();
            GeoTiffImportResult result;

            if (state.CropResult is { NeedsCropping: true })
            {
                result = reader.ReadGeoTiff(
                    geoTiffPath,
                    state.TerrainSize,
                    state.CropResult.OffsetX,
                    state.CropResult.OffsetY,
                    state.CropResult.CropWidth,
                    state.CropResult.CropHeight);
            }
            else
            {
                result = reader.ReadGeoTiff(geoTiffPath, state.TerrainSize);
            }

            return ConvertImageToHeightMap(result.HeightmapImage, result.MinElevation, result.MaxElevation);
        });
    }

    /// <summary>
    /// Converts a 16-bit heightmap image to a float array.
    /// </summary>
    private static float[,] ConvertImageToHeightMap(
        SixLabors.ImageSharp.Image<L16> image,
        double minElevation,
        double maxElevation)
    {
        var heightMap = new float[image.Height, image.Width];
        var elevationRange = maxElevation - minElevation;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                var normalized = pixel.PackedValue / 65535.0;
                heightMap[y, x] = (float)(minElevation + normalized * elevationRange);
            }
        }

        return heightMap;
    }

    private static async Task<string> SaveLayerMapToPngAsync(byte[,] layerMap, string debugPath, string materialName)
    {
        var height = layerMap.GetLength(0);
        var width = layerMap.GetLength(1);

        var safeName = string.Join("_", materialName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(debugPath, $"{safeName}_osm_layer.png");

        using var image = new SixLabors.ImageSharp.Image<L8>(width, height);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = new L8(layerMap[y, x]);

        await image.SaveAsPngAsync(filePath);

        return filePath;
    }

    /// <summary>
    /// Generates a simplified debug image showing junction states.
    /// This is used for updating the visualization after exclusion changes.
    /// </summary>
    private static byte[]? GenerateSimplifiedDebugImage(
        UnifiedRoadNetwork network,
        int imageWidth,
        int imageHeight,
        float metersPerPixel)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            return null;

        using var image = new SixLabors.ImageSharp.Image<Rgba32>(imageWidth, imageHeight, new Rgba32(30, 30, 30, 255));

        // Draw cross-sections as road paths
        foreach (var cs in network.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            var halfWidth = cs.EffectiveRoadWidth / 2.0f;
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

            var lx = (int)(left.X / metersPerPixel);
            var ly = imageHeight - 1 - (int)(left.Y / metersPerPixel);
            var rx = (int)(right.X / metersPerPixel);
            var ry = imageHeight - 1 - (int)(right.Y / metersPerPixel);

            DrawLine(image, lx, ly, rx, ry, new Rgba32(100, 100, 100, 255));
        }

        // Draw spline centerlines
        foreach (var spline in network.Splines)
        {
            var color = new Rgba32(255, 200, 0, 255); // Gold

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            for (int i = 0; i < crossSections.Count - 1; i++)
            {
                var p1 = crossSections[i].CenterPoint;
                var p2 = crossSections[i + 1].CenterPoint;

                var x1 = (int)(p1.X / metersPerPixel);
                var y1 = imageHeight - 1 - (int)(p1.Y / metersPerPixel);
                var x2 = (int)(p2.X / metersPerPixel);
                var y2 = imageHeight - 1 - (int)(p2.Y / metersPerPixel);

                DrawLine(image, x1, y1, x2, y2, color);
            }
        }

        // Draw junctions
        foreach (var junction in network.Junctions)
        {
            var jx = (int)(junction.Position.X / metersPerPixel);
            var jy = imageHeight - 1 - (int)(junction.Position.Y / metersPerPixel);

            Rgba32 junctionColor;
            if (junction.IsExcluded)
            {
                junctionColor = new Rgba32(128, 128, 128, 180);
            }
            else
            {
                junctionColor = junction.Type switch
                {
                    JunctionType.Endpoint => new Rgba32(255, 255, 0, 255),
                    JunctionType.TJunction => new Rgba32(0, 255, 255, 255),
                    JunctionType.YJunction => new Rgba32(0, 255, 0, 255),
                    JunctionType.CrossRoads => new Rgba32(255, 128, 0, 255),
                    JunctionType.Complex => new Rgba32(255, 0, 255, 255),
                    JunctionType.MidSplineCrossing => new Rgba32(255, 64, 128, 255),
                    _ => new Rgba32(255, 255, 255, 255)
                };
            }

            var radius = junction.Type == JunctionType.Endpoint ? 4 : 7;
            DrawFilledCircle(image, jx, jy, radius, junctionColor);

            if (junction.IsCrossMaterial && !junction.IsExcluded)
            {
                DrawCircleOutline(image, jx, jy, radius + 3, new Rgba32(255, 255, 255, 200));
            }

            if (junction.IsExcluded)
            {
                DrawX(image, jx, jy, radius + 2, new Rgba32(255, 50, 50, 255));
            }
        }

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    #region Drawing Helpers

    private static void DrawLine(SixLabors.ImageSharp.Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawFilledCircle(SixLabors.ImageSharp.Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (x * x + y * y <= radius * radius)
            {
                var px = cx + x;
                var py = cy + y;
                if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    img[px, py] = color;
            }
    }

    private static void DrawCircleOutline(SixLabors.ImageSharp.Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 2)
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    private static void DrawX(SixLabors.ImageSharp.Image<Rgba32> img, int cx, int cy, int size, Rgba32 color)
    {
        for (int i = -size; i <= size; i++)
        {
            var px1 = cx + i;
            var py1 = cy + i;
            if (px1 >= 0 && px1 < img.Width && py1 >= 0 && py1 < img.Height)
                img[px1, py1] = color;

            var px2 = cx + i;
            var py2 = cy - i;
            if (px2 >= 0 && px2 < img.Width && py2 >= 0 && py2 < img.Height)
                img[px2, py2] = color;
        }
    }

    #endregion

    #endregion
}
