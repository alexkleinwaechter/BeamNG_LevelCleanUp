using System.Text.Json;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicBasecolorManager;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNgTerrainPoc.Terrain.Backdrop;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     App-layer orchestration for backdrop generation (spec §11): wires the core
///     <see cref="BackdropGenerator"/> pipeline (Task 10) and <see cref="BackdropTextureBaker"/>
///     (Task 13) into the terrain generation run (gated stage, default-off), plus a standalone
///     regen button and a destructive remove action. Mirrors the established DecalRoad standalone
///     regen pattern (<c>GenerateTerrain.razor.cs</c> <c>RegenerateDecalRoads</c>).
/// </summary>
public class BackdropOrchestrator
{
    /// <summary>
    ///     In-run stage AND in-session standalone regen. Never throws — every failure is reported via
    ///     <see cref="PubSubChannel"/> as a Warning and the method returns false; the caller (terrain
    ///     generation run) always continues regardless (spec §11).
    /// </summary>
    public async Task<bool> GenerateAsync(TerrainGenerationState state, float[,] outputHeightMap)
    {
        try
        {
            return await Task.Run(async () =>
            {
                // 1. Resolve the source GeoTIFF covering the FULL mosaic — backdrop offsets are
                // mosaic-pixel coordinates, so a terrain-crop-only file is not valid input here.
                var sourcePath = await ResolveSourceGeoTiffPathAsync(state).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        "Backdrop generation skipped: no source GeoTIFF covering the full mosaic could be " +
                        "resolved (terrain generation will continue).");
                    return false;
                }

                // 2. Build parameters — SAME builder the cost-estimate path (Task 18) uses, so the two
                // can never drift.
                var parameters = BuildParameters(state, outputHeightMap, sourcePath);

                // 3. Run the core pipeline.
                var result = new BackdropGenerator().Generate(parameters,
                    Path.Combine(state.GetDebugPath(), "backdrop"));

                foreach (var warning in result.Warnings)
                    PubSubChannel.SendMessage(PubSubMessageType.Warning, $"Backdrop: {warning}");

                if (!result.Success)
                {
                    // The terrain run itself already succeeded — a backdrop failure is reported but
                    // never turns the whole run into a failure (spec §11, same contract as
                    // ExportAllOsmLayersAsync).
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Backdrop generation failed (terrain generation will continue): {result.ErrorMessage}");
                    return false;
                }

                // 4. Persist MtBackdropSettings (mesh geometry) so the texture baker and a later
                // standalone rebake/rebake-on-basecolor-change can find the chunk list.
                var settings = MtSettings.Load(state.WorkingDirectory) ?? new MtSettings();
                settings.BackdropSettings = BuildMtBackdropSettings(state, result);
                settings.Save(state.WorkingDirectory);

                // 5. Bake one satellite texture per chunk.
                var bakeStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var baked = await new BackdropTextureBaker()
                    .BakeAllChunksAsync(state.WorkingDirectory, settings)
                    .ConfigureAwait(false);
                var textureSeconds = bakeStopwatch.Elapsed.TotalSeconds;
                settings.Save(state.WorkingDirectory);

                // 6. Summary + phase timing (perf plan §0.1 — this line turns every user report
                // into a usable measurement, so it goes through PubSub, not just the console).
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Backdrop generated: {result.ExportedChunks.Count} chunk(s), {result.TotalVertices} " +
                    $"vertices, {result.TotalTriangles} triangles, {baked} texture(s) baked.");
                PubSubChannel.SendMessage(PubSubMessageType.Info, string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"[BACKDROP] timing: rasters={result.RasterLoadSeconds:F1}s mesh={result.MeshSeconds:F1}s ({result.ExportedChunks.Count} chunks, worst={result.WorstChunkSeconds:F1}s {result.WorstChunkName ?? "-"}) debug={result.DebugArtifactSeconds:F1}s textures={textureSeconds:F1}s"));

                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Backdrop generation failed (terrain generation will continue): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Standalone "Regenerate Backdrop" button: reuses the cached heightmap in-session, or
    ///     reconstructs it from the written <c>.ter</c> file across sessions (spec §11) — the same
    ///     fallback <c>RegenerateDecalRoads</c> uses for the road network.
    /// </summary>
    public async Task<bool> RegenerateStandaloneAsync(TerrainGenerationState state)
    {
        var heightMap = state.CachedHeightMap;
        if (heightMap == null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Loading heightmap from .ter file...");
            var terPath = state.GetOutputPath();
            heightMap = await Task.Run(() => DecalRoadNetworkSnapshotLoader.LoadHeightmap(terPath, state.MaxHeight))
                .ConfigureAwait(false);
            if (heightMap == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Terrain file not found at {terPath}. Generate terrain first.");
                return false;
            }

            state.CachedHeightMap = heightMap;
        }

        // Wipe discipline (spec §14.8): BackdropSceneWriter.CleanPreviousOutputs already runs inside
        // Generate() and clears art/shapes/MT_backdrop + the scene folder. The standalone path
        // additionally clears ONLY the backdrop debug subfolder — never the whole MT_TerrainGeneration
        // debug folder, which would also wipe unrelated osm_layer/other artifacts from the last full run.
        var backdropDebugPath = Path.Combine(state.GetDebugPath(), "backdrop");
        if (Directory.Exists(backdropDebugPath))
            Directory.Delete(backdropDebugPath, recursive: true);

        return await GenerateAsync(state, heightMap).ConfigureAwait(false);
    }

    /// <summary>
    ///     Whether the standalone regenerate button can be enabled: a heightmap must be reachable
    ///     (in-session cache or a written .ter file), GeoTIFF metadata must be loaded, and a backdrop
    ///     ring must actually be selected.
    /// </summary>
    public static bool CanRegenerate(TerrainGenerationState state)
    {
        var hasHeightSource = state.CachedHeightMap != null ||
                              (state.HasWorkingDirectory && File.Exists(state.GetOutputPath()));
        return hasHeightSource && state.GeoTiffOriginalWidth > 0 && state.Backdrop.HasSelection;
    }

    /// <summary>
    ///     Deletes art/shapes/MT_backdrop, the scene folder, the SimGroup entry and the settings block
    ///     (spec §11 Remove button). A terrain run with backdrop disabled must never call this — the
    ///     default-off behavior is strictly non-destructive.
    /// </summary>
    public static void RemoveBackdrop(string levelPath)
    {
        var shapesDir = Path.Combine(levelPath, "art", "shapes", "MT_backdrop");
        var sceneDir = Path.Combine(levelPath, "main", "MissionGroup", "MT_backdrop");

        foreach (var dir in new[] { shapesDir, sceneDir })
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);

        RemoveSimGroupEntry(Path.Combine(levelPath, "main", "MissionGroup", "items.level.json"));

        var settings = MtSettings.Load(levelPath);
        if (settings != null)
        {
            settings.BackdropSettings = null;
            // dropBackdropSettings: true - this is the explicit removal path; without it, Save()'s
            // cross-page preservation logic would just reload the block it and write it right back.
            settings.Save(levelPath, dropBackdropSettings: true);
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info, "Backdrop removed.");
    }

    /// <summary>
    ///     Shared parameter builder for both the in-run/standalone generation path and the UI's cost
    ///     estimate path (Task 18) — kept as a single method so the two can never drift apart.
    ///     <paramref name="heightMapOrNull"/> is null for the estimate path, which never reads terrain
    ///     heights near the seam (it only reads sizes/geotransform off <paramref name="state"/>) — a
    ///     zeroed <c>float[TerrainSize, TerrainSize]</c> is substituted so the parameters remain valid.
    ///     <paramref name="sourceGeoTiffPathOverride"/> lets <see cref="GenerateAsync"/> pass in the
    ///     already-resolved full-mosaic path (step 1); when omitted (the estimate path, which has no
    ///     resolved combine), a best-effort path is used instead. Unlike the height map, the estimate
    ///     path does read this file's pixel content — <c>BackdropGenerator.Estimate</c> loads the far
    ///     raster off it (downsampled to ≤512px) to probe cost — so a stale/mismatched best-effort path
    ///     here degrades estimate accuracy. It never affects the actual generation, which always
    ///     re-resolves and probes the real file (step 1 above).
    /// </summary>
    internal static BackdropGenerationParameters BuildParameters(
        TerrainGenerationState state, float[,]? heightMapOrNull, string? sourceGeoTiffPathOverride = null)
    {
        var heightMap = heightMapOrNull ?? new float[state.TerrainSize, state.TerrainSize];
        var sourcePath = sourceGeoTiffPathOverride ?? state.CachedCombinedGeoTiffPath ?? state.GeoTiffPath ?? "";

        var crop = state.CropResult;
        var terrainRect = crop is { NeedsCropping: true }
            ? new PixelRect(crop.OffsetX, crop.OffsetY, crop.CropWidth, crop.CropHeight)
            : new PixelRect(0, 0, state.GeoTiffOriginalWidth, state.GeoTiffOriginalHeight);

        var cropMin = crop?.CroppedMinElevation ?? state.GeoTiffMinElevation ?? 0.0;
        Console.WriteLine($"[BACKDROP] datum: cropMin={cropMin}, baseHeight={state.TerrainBaseHeight}");

        return new BackdropGenerationParameters
        {
            TerrainHeightMap = heightMap,
            TerrainSizePixels = state.TerrainSize,
            TerrainMetersPerPixel = state.MetersPerPixel,
            TerrainBaseHeight = state.TerrainBaseHeight,
            TerrainCropMinElevation = cropMin,
            SourceGeoTiffPath = sourcePath,
            EpsgOverride = state.GeoTiffEpsgOverride,
            SourceRasterWidth = state.GeoTiffOriginalWidth,
            SourceRasterHeight = state.GeoTiffOriginalHeight,
            SourceGeoTransform = state.GeoTiffGeoTransform ?? [],
            ProjectionWkt = state.GeoTiffProjectionWkt,
            SourceWgs84Bounds = state.GeoBoundingBox,
            TerrainRect = terrainRect,
            BackdropRect = new PixelRect(state.Backdrop.OffsetX, state.Backdrop.OffsetY,
                state.Backdrop.Width, state.Backdrop.Height),
            LevelPath = state.WorkingDirectory,
            LevelName = state.LevelName,
            EdgeBandMeters = state.Backdrop.EdgeBandMeters,
            MaxVerticalErrorNearMeters = state.Backdrop.MaxVerticalErrorNearMeters,
            MaxVerticalErrorFarMeters = state.Backdrop.MaxVerticalErrorFarMeters,
            ChunkTargetMeters = state.Backdrop.ChunkTargetMeters,
            TexelDensityNearMPerPx = state.Backdrop.TexelDensityNearMPerPx,
            MaxChunkTextureSize = state.Backdrop.MaxChunkTextureSize,
            MaxFarRasterDimension = state.Backdrop.MaxFarRasterDimension,
            SeamSkirt = state.Backdrop.SeamSkirt,
            CollisionMesh = state.Backdrop.CollisionMesh
        };
    }

    /// <summary>
    ///     Resolves a GeoTIFF that covers the FULL uncropped source mosaic (spec §7: backdrop rect
    ///     offsets are mosaic-pixel coordinates, not terrain-crop-relative). For the single-file source
    ///     this is just the file itself. For directory/XYZ sources the cached combined file is reused
    ///     when it actually covers the full mosaic; the direct-crop optimization
    ///     (<c>TerrainGenerationOrchestrator.cs:1139-1158</c>) and the GeoTIFF "Reduce" flow both can
    ///     leave <see cref="TerrainGenerationState.CachedCombinedGeoTiffPath"/> pointing at a
    ///     terrain-crop-only file instead — detected here by comparing raster dimensions against the
    ///     full mosaic size recorded at import time — in which case the tiles are combined fresh.
    /// </summary>
    private static async Task<string?> ResolveSourceGeoTiffPathAsync(TerrainGenerationState state)
    {
        if (state.HeightmapSourceType == HeightmapSourceType.GeoTiffFile)
            return state.GeoTiffPath;

        if (state.HeightmapSourceType != HeightmapSourceType.GeoTiffDirectory &&
            state.HeightmapSourceType != HeightmapSourceType.XyzFile)
            return null;

        var cached = state.CachedCombinedGeoTiffPath;
        if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
        {
            try
            {
                var info = new GeoTiffReader().GetGeoTiffInfo(cached);
                if (info.Width == state.GeoTiffOriginalWidth && info.Height == state.GeoTiffOriginalHeight)
                    return cached;
            }
            catch
            {
                // Unreadable cache entry — fall through and recombine from the source tiles.
            }
        }

        // No usable cached mosaic — combine the full source now (established pattern:
        // GenerateTerrain.razor.cs:1939-1952 / GeoTiffMetadataService.CombineXyzTilesAsync).
        string[]? files = null;
        string? overrideProjection = null;

        if (state.HeightmapSourceType == HeightmapSourceType.GeoTiffDirectory &&
            !string.IsNullOrEmpty(state.GeoTiffDirectory) && Directory.Exists(state.GeoTiffDirectory))
        {
            files = Directory.GetFiles(state.GeoTiffDirectory)
                .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".geotiff", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".asc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToArray();
            overrideProjection = state.GeoTiffEpsgOverride.HasValue
                ? GeoTiffReader.GetProjectionWktFromEpsg(state.GeoTiffEpsgOverride.Value)
                : null;
        }
        else if (state.HeightmapSourceType == HeightmapSourceType.XyzFile)
        {
            files = state.XyzFilePaths is { Length: > 0 }
                ? state.XyzFilePaths
                : !string.IsNullOrEmpty(state.XyzPath) ? new[] { state.XyzPath } : null;
            overrideProjection = GeoTiffReader.GetProjectionWktFromEpsg(state.XyzEpsgCode);
        }

        if (files is not { Length: > 0 })
            return null;

        var tempPath = Path.Combine(Path.GetTempPath(), $"backdrop_combined_{Guid.NewGuid():N}.tif");
        await new GeoTiffCombiner().CombineFilesAsync(files, tempPath, overrideProjection).ConfigureAwait(false);

        // Every BackdropRect/TerrainRect coordinate is expressed in the DECLARED source grid
        // (state.GeoTiffOriginalWidth/Height + state.GeoTiffGeoTransform), and BackdropRasterLoader
        // reads windows against that grid with no bounds guard — a freshly combined mosaic that
        // silently drifted from it (stale tile set, combiner quirk, etc.) would misplace or garble
        // the whole ring. Verify dimensions AND geotransform (within a small relative epsilon)
        // before trusting the file at all.
        if (!TryValidateMosaicGrid(tempPath, state, out var mismatchReason))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Backdrop generation skipped: the freshly combined GeoTIFF does not match the " +
                $"declared source grid ({mismatchReason}).");
            TryDeleteFile(tempPath, "rejected combined GeoTIFF");
            return null;
        }

        // Safe to publish back onto the shared cache field: the probe above guarantees this file
        // matches the EXACT grid the page's own cached crop offsets/dimensions were computed
        // against, so any later reuse of state.CachedCombinedGeoTiffPath (by this class or the page)
        // stays valid. Clean up the file being replaced first — TerrainGenerationState's own
        // CleanupCachedCombinedGeoTiff() only ever deletes the NEWEST path, so without this the
        // previous combine result would leak on disk every time this method recombines.
        if (!string.IsNullOrEmpty(cached) && !string.Equals(cached, tempPath, StringComparison.OrdinalIgnoreCase))
            TryDeleteFile(cached, "replaced combined GeoTIFF cache");

        state.CachedCombinedGeoTiffPath = tempPath;
        return tempPath;
    }

    /// <summary>
    ///     Verifies a combined GeoTIFF's raster dimensions and geotransform match the declared source
    ///     grid recorded in <paramref name="state"/> at import time (spec §7: backdrop rect
    ///     coordinates are only meaningful in that exact grid). Geotransform elements are compared
    ///     within a small relative epsilon to tolerate floating-point round-trip noise, not real drift.
    /// </summary>
    private static bool TryValidateMosaicGrid(string path, TerrainGenerationState state, out string mismatchReason)
    {
        try
        {
            var info = new GeoTiffReader().GetGeoTiffInfoExtended(path);

            if (info.Width != state.GeoTiffOriginalWidth || info.Height != state.GeoTiffOriginalHeight)
            {
                mismatchReason = $"dimensions {info.Width}x{info.Height} vs declared " +
                                  $"{state.GeoTiffOriginalWidth}x{state.GeoTiffOriginalHeight}";
                return false;
            }

            var declaredGt = state.GeoTiffGeoTransform;
            if (declaredGt is { Length: 6 })
            {
                var actualGt = info.GeoTransform;
                if (actualGt is not { Length: 6 })
                {
                    mismatchReason = "combined file has no readable geotransform";
                    return false;
                }

                for (var i = 0; i < 6; i++)
                {
                    var declared = declaredGt[i];
                    var actual = actualGt[i];
                    var tolerance = Math.Max(1e-6, Math.Max(Math.Abs(declared), Math.Abs(actual)) * 1e-6);
                    if (Math.Abs(declared - actual) > tolerance)
                    {
                        mismatchReason = $"geotransform[{i}]={actual} vs declared {declared}";
                        return false;
                    }
                }
            }

            mismatchReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            mismatchReason = $"could not read combined file ({ex.Message})";
            return false;
        }
    }

    /// <summary>
    ///     Best-effort file deletion for combine-cache housekeeping (rejected/replaced temp mosaics).
    ///     Never throws — the file may be locked by another process; a failure here is a leaked temp
    ///     file, not a functional problem for the current run, so it is warn-logged and swallowed.
    /// </summary>
    private static void TryDeleteFile(string path, string context)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BACKDROP] warning: could not delete {context} '{path}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Builds the MtBackdropSettings block from a completed generation: per-chunk rows (matched
    ///     from the chunk plan by (Cx, Cy)) plus settings-level bounds that are the union of every
    ///     chunk's own WGS84 bounds. The geotransform is the EFFECTIVE source geotransform of the whole
    ///     mosaic (uncropped <see cref="TerrainGenerationState.GeoTiffGeoTransform"/>), matching how
    ///     <see cref="BackdropTextureBaker"/> re-derives each chunk's translated geotransform.
    /// </summary>
    private static MtBackdropSettings BuildMtBackdropSettings(
        TerrainGenerationState state, BackdropGenerationResult result)
    {
        var chunksByKey = (result.ChunkPlan?.Chunks ?? (IReadOnlyList<BackdropChunkDefinition>)Array.Empty<BackdropChunkDefinition>())
            .ToDictionary(c => (c.Cx, c.Cy));

        var chunkRows = new List<MtBackdropChunk>(result.ExportedChunks.Count);
        var boundsUnion = new List<GeoBoundingBox>();

        foreach (var exported in result.ExportedChunks)
        {
            if (!chunksByKey.TryGetValue((exported.Cx, exported.Cy), out var def))
                continue; // should never happen — every exported chunk came from this same plan

            var bounds = def.Wgs84Bounds;
            if (bounds != null)
                boundsUnion.Add(bounds);

            chunkRows.Add(new MtBackdropChunk
            {
                Cx = exported.Cx,
                Cy = exported.Cy,
                MinLongitude = bounds?.MinLongitude ?? 0,
                MinLatitude = bounds?.MinLatitude ?? 0,
                MaxLongitude = bounds?.MaxLongitude ?? 0,
                MaxLatitude = bounds?.MaxLatitude ?? 0,
                SourceRectX = def.SourceRectX,
                SourceRectY = def.SourceRectY,
                SourceRectWidth = def.SourceRectWidth,
                SourceRectHeight = def.SourceRectHeight,
                TextureFile = exported.TextureFileName,
                TextureSize = def.TextureSize
            });
        }

        return new MtBackdropSettings
        {
            Enabled = true,
            MinLongitude = boundsUnion.Count > 0 ? boundsUnion.Min(b => b.MinLongitude) : 0,
            MinLatitude = boundsUnion.Count > 0 ? boundsUnion.Min(b => b.MinLatitude) : 0,
            MaxLongitude = boundsUnion.Count > 0 ? boundsUnion.Max(b => b.MaxLongitude) : 0,
            MaxLatitude = boundsUnion.Count > 0 ? boundsUnion.Max(b => b.MaxLatitude) : 0,
            SourceGeoTransform = state.GeoTiffGeoTransform ?? [],
            ProjectionWkt = state.GeoTiffProjectionWkt ?? string.Empty,
            TerrainMetersPerPixel = state.MetersPerPixel,
            EdgeBandMeters = state.Backdrop.EdgeBandMeters,
            Chunks = chunkRows,
            HasCollision = state.Backdrop.CollisionMesh,
            LastBakeUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Removes the <c>MT_backdrop</c> SimGroup line from the parent items.level.json (NDJSON — each
    ///     line is its own JSON object, no array wrapper). Malformed lines are kept untouched rather than
    ///     risk dropping unrelated scene data.
    /// </summary>
    private static void RemoveSimGroupEntry(string itemsPath)
    {
        if (!File.Exists(itemsPath))
            return;

        var lines = File.ReadAllLines(itemsPath);
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                kept.Add(line);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("class", out var cls) && cls.GetString() == "SimGroup" &&
                    root.TryGetProperty("name", out var name) && name.GetString() == "MT_backdrop")
                    continue; // drop this line — the SimGroup entry itself
            }
            catch (JsonException)
            {
                // Malformed line — keep it untouched rather than risk dropping unrelated data.
            }

            kept.Add(line);
        }

        File.WriteAllLines(itemsPath, kept);
    }
}
