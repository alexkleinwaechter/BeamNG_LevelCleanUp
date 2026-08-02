using System.Diagnostics;
using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.Biome;
using BeamNgTerrainPoc.Terrain.Biome;
using BeamNgTerrainPoc.Terrain.ColorExtraction;
using Grille.BeamNG.IO.Binary;

namespace BeamNG_LevelCleanUp.LogicBiome;

/// <summary>
/// One terrain material row for the Generate Biome material list.
/// </summary>
public class BiomeMaterialInfo
{
    public string InternalName { get; init; } = string.Empty;
    public string BaseColorHex { get; init; } = "#808080";
    public long PixelCount { get; init; }
    public double CoveragePercent { get; init; }
}

/// <summary>
/// One discovered OSM mask PNG, usable as a biome layer region or a negative-list entry.
/// </summary>
public class BiomeOsmLayerInfo
{
    /// <summary>File stem — the stable SourceKey stored in settings.</summary>
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    /// <summary>True for {material}_osm_layer.png selection masks (vs per-category masks).</summary>
    public bool IsMaterialSelection { get; init; }
}

/// <summary>
/// Everything loaded for one level; owned by the page after LoadLevel.
/// Memory note: only the raw .ter arrays are held (heights + material bytes) —
/// per-material masks are never materialized.
/// </summary>
public class BiomeLevelContext
{
    public required string LevelPath { get; init; }
    public required string LevelName { get; init; }
    public required string TerrainFilePath { get; init; }
    public required TerrainV9Binary Terrain { get; init; }
    public required int TerrainSize { get; init; }
    public required float MetersPerPixel { get; init; }
    /// <summary>World X of terrain pixel (0,0) — TerrainBlock.position[0]; the terrain is NOT necessarily centered.</summary>
    public required float TerrainOriginX { get; init; }
    /// <summary>World Y of terrain pixel (0,0) — TerrainBlock.position[1].</summary>
    public required float TerrainOriginY { get; init; }
    public required float MaxHeight { get; init; }
    public required float TerrainBaseHeight { get; init; }
    /// <summary>.ter material name (== material internalName) → material byte index, case-insensitive.</summary>
    public required Dictionary<string, byte> MaterialIndexByName { get; init; }
    public required List<BiomeMaterialInfo> Materials { get; init; }
    /// <summary>OSM mask PNGs discovered under MT_TerrainGeneration (empty when none exist).</summary>
    public required List<BiomeOsmLayerInfo> OsmLayers { get; init; }
    public required BiomeBrushCatalog Catalog { get; init; }
    public required BiomeSettings Settings { get; init; }
    public BiomeManifest Manifest { get; set; } = new();

    /// <summary>Biome-owned forest files with no manifest entry (interrupted generations); cleaned by global delete.</summary>
    public int OrphanedForestFileCount { get; set; }

    public BiomeTerrainContext CreateTerrainContext() => new()
    {
        Size = TerrainSize,
        MetersPerPixel = MetersPerPixel,
        HeightData = Terrain.HeightData,
        MaxHeight = MaxHeight,
        TerrainBaseHeight = TerrainBaseHeight,
    };
}

public class BiomeLoadResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public BiomeLevelContext? Context { get; init; }

    public static BiomeLoadResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

public class BiomeGenerationResult
{
    public int LayersGenerated { get; set; }
    public int ItemsPlaced { get; set; }
    public int ItemsRemovedBeforeRegenerate { get; set; }
    /// <summary>Layers refused (over cap, missing material/mask, invalid zones).</summary>
    public int LayersSkipped { get; set; }
    /// <summary>One human-readable reason per skipped layer — shown directly in the snackbar.</summary>
    public List<string> SkipReasons { get; } = new();
    /// <summary>Items removed again by the automatic negative-list cleanup after generation.</summary>
    public int ItemsRemovedByCleanup { get; set; }
}

/// <summary>Outcome of one negative-list cleanup pass over the manifest-tracked items.</summary>
public class BiomeCleanupResult
{
    public int ItemsRemoved { get; set; }
    public int LayersAffected { get; set; }
    /// <summary>Tracked records on the mask that no forest file contained anymore.</summary>
    public int OrphanRecords { get; set; }
}

/// <summary>
/// Result of <see cref="BiomeService.RunNegativeListCleanup"/>. Carries the combined
/// negative mask so the optional foreign-item follow-up (count → confirm → remove)
/// does not rebuild the expensive distance-buffered mask.
/// </summary>
public sealed class BiomeCleanupSession
{
    internal bool[] Mask { get; init; } = Array.Empty<bool>();
    public BiomeCleanupResult TrackedResult { get; init; } = new();
}

public class BiomeDeleteResult
{
    public int LayersDeleted { get; set; }
    public int ItemsRemoved { get; set; }
    public int OrphanRecords { get; set; }
    /// <summary>MT_biome_* files removed that no manifest layer claimed (crashed generations).</summary>
    public int OrphanFilesDeleted { get; set; }
}

public class BiomeService
{
    /// <summary>Warn (not block) above this many items in one generation run.</summary>
    public const long ItemCountWarningThreshold = 500_000;

    /// <summary>Minimum interval between UI progress messages — each one triggers a full page re-render.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    public BiomeLoadResult LoadLevel(string folder)
    {
        try
        {
            var levelPath = ValidateLevelFolder(folder);
            if (levelPath == null)
                return BiomeLoadResult.Fail(
                    "Selected folder does not appear to be a valid BeamNG level. Please select a folder containing info.json.");

            var levelName = new BeamFileReader(levelPath, null).GetLevelName();

            var terrainBlock = ReadTerrainBlockParams(levelPath);
            if (!terrainBlock.MaxHeight.HasValue)
                return BiomeLoadResult.Fail(
                    "No TerrainBlock with a maxHeight property was found in main/**/items.level.json — cannot compute tree elevations.");
            if (!terrainBlock.SquareSize.HasValue)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "TerrainBlock has no squareSize — assuming 1 meter per pixel.");
            }

            // The TerrainBlock's terrainFile is authoritative (levels can carry stale extra
            // .ter files, e.g. ellern_map: terrain4.ter next to nothing named theTerrain.ter).
            var terFilePath = ResolveTerrainFile(levelPath, terrainBlock.TerrainFile)
                              ?? FindTerrainTerFile(levelPath);
            if (string.IsNullOrWhiteSpace(terFilePath))
                return BiomeLoadResult.Fail("No .ter terrain file was found in the selected level.");

            var terrain = LayerMaskReader.ReadTerrainBinary(terFilePath);
            var terrainSize = checked((int)terrain.Size);

            var metersPerPixel = terrainBlock.SquareSize ?? 1f;

            // BeamNG anchors the terrain at TerrainBlock.position (world position of pixel
            // 0,0 — the south-west corner), NOT at the world origin. Assuming a centered
            // terrain shifted every tree on maps whose position is not exactly -size/2*mpp
            // (ellern_map: squareSize 1.2 but position [-2048,-2048] -> 409.6 m offset,
            // trees rendered outside the terrain).
            float originX, originY;
            if (terrainBlock.PositionX.HasValue && terrainBlock.PositionY.HasValue)
            {
                originX = terrainBlock.PositionX.Value;
                originY = terrainBlock.PositionY.Value;
            }
            else
            {
                originX = originY = -(terrainSize / 2f) * metersPerPixel;
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "TerrainBlock has no position — assuming a centered terrain.");
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"TerrainBlock: {Path.GetFileName(terFilePath)}, {terrainSize} px, {metersPerPixel} m/px, " +
                $"origin ({originX}, {originY}), base height {terrainBlock.PositionZ ?? 0f}, maxHeight {terrainBlock.MaxHeight.Value}.");

            var materialIndexByName = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < terrain.MaterialNames.Length && i < 255; i++)
            {
                materialIndexByName[terrain.MaterialNames[i]] = (byte)i;
            }

            var materials = BuildMaterialList(levelPath, terrain);
            var osmLayers = DiscoverOsmLayers(levelPath);
            var catalog = BiomeBrushCatalog.Load(levelPath);
            var settings = BiomeSettings.Load(levelPath) ?? new BiomeSettings();
            settings.EnsureDefaults();
            var manifest = BiomeManifestStore.Load(levelPath);

            var orphanedForestFiles = WarnAboutOrphanedBiomeFiles(levelPath, manifest);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Loaded {levelName}: {materials.Count} terrain materials, {osmLayers.Count} OSM mask layers, " +
                $"{catalog.Brushes.Count} forest brushes, {catalog.ItemData.Count} forest item types, " +
                $"{manifest.Layers.Sum(l => l.ItemCount)} previously generated items.");

            return new BiomeLoadResult
            {
                Success = true,
                Context = new BiomeLevelContext
                {
                    LevelPath = levelPath,
                    LevelName = levelName,
                    TerrainFilePath = terFilePath,
                    Terrain = terrain,
                    TerrainSize = terrainSize,
                    MetersPerPixel = metersPerPixel,
                    MaxHeight = terrainBlock.MaxHeight.Value,
                    TerrainBaseHeight = terrainBlock.PositionZ ?? 0f,
                    TerrainOriginX = originX,
                    TerrainOriginY = originY,
                    MaterialIndexByName = materialIndexByName,
                    Materials = materials,
                    OsmLayers = osmLayers,
                    Catalog = catalog,
                    Settings = settings,
                    Manifest = manifest,
                    OrphanedForestFileCount = orphanedForestFiles,
                }
            };
        }
        catch (Exception ex)
        {
            return BiomeLoadResult.Fail(ex.InnerException != null
                ? $"{ex.Message} {ex.InnerException.Message}"
                : ex.Message);
        }
    }

    /// <summary>
    /// "Terrain changed since last generation" banner input; null when not stale.
    /// </summary>
    public static string? ComputeStaleReason(BiomeLevelContext context)
    {
        if (context.Manifest.Layers.Count == 0 || string.IsNullOrEmpty(context.Manifest.TerFileTimestampUtc))
            return null;

        var current = File.GetLastWriteTimeUtc(context.TerrainFilePath).ToString("o");
        return current == context.Manifest.TerFileTimestampUtc
            ? null
            : "The terrain (.ter) was modified after the last biome generation — placed items may float or sink. Regenerate the biome layers.";
    }

    /// <summary>
    /// Per-zone pixel counts for a layer (drives the estimated-count preview).
    /// Runs the distance transform — call from a background task, not per keystroke.
    /// </summary>
    public static long[] ComputeZonePixelCounts(BiomeLevelContext context, BiomeLayerSettings layer)
    {
        var bands = layer.Zones
            .Select(z => new BiomeZoneBandDefinition(z.DepthMeters, z.IsInterior))
            .ToList();

        if (layer.Kind == BiomeLayerKind.TerrainMaterial)
        {
            if (!context.MaterialIndexByName.TryGetValue(layer.SourceKey, out var materialIndex))
                return new long[layer.Zones.Count];
            return BiomeZoneBander.ComputeZoneCounts(
                context.Terrain.MaterialData, materialIndex, context.TerrainSize, context.MetersPerPixel, bands);
        }

        var mask = TryLoadOsmMask(context, layer.SourceKey, out var maskFailReason);
        if (mask == null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Layer '{layer.SourceKey}': {maskFailReason}");
            return new long[layer.Zones.Count];
        }
        return BiomeZoneBander.ComputeZoneCounts(mask, context.TerrainSize, context.MetersPerPixel, bands);
    }

    public BiomeGenerationResult GenerateLayers(
        BiomeLevelContext context,
        IReadOnlyList<BiomeLayerSettings> layers,
        CancellationToken cancellationToken = default)
    {
        var result = new BiomeGenerationResult();
        var terrainContext = context.CreateTerrainContext();
        var anyWritten = false;

        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (layer.Zones.Count == 0)
            {
                SkipLayer(result, PubSubMessageType.Info,
                    $"'{layer.SourceKey}': no zones configured.");
                continue;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Layer '{layer.SourceKey}': computing zone bands...");

            var zonePixels = ComputeLayerZonePixels(context, layer, out var zoneSkipReason);
            if (zonePixels == null)
            {
                SkipLayer(result, PubSubMessageType.Warning,
                    zoneSkipReason ?? $"'{layer.SourceKey}': zone bands could not be computed.");
                continue;
            }

            // Pre-flight cap BEFORE deleting the previous generation or touching any file:
            // a runaway configuration must fail loudly, not write a multi-gigabyte forest.
            var zoneSpecs = layer.Zones.Select(z => BuildItemSpecs(context, z)).ToList();
            var estimatedTotal = 0L;
            for (var zoneIndex = 0; zoneIndex < layer.Zones.Count; zoneIndex++)
            {
                foreach (var spec in zoneSpecs[zoneIndex])
                {
                    estimatedTotal += BiomeDensityModel.EstimateCount(
                        zonePixels[zoneIndex].Length, context.MetersPerPixel, spec.DensityPercent,
                        spec.RadiusMeters, spec.ScaleMin, spec.ScaleMax);
                }
            }
            if (estimatedTotal > context.Settings.MaxItemsPerLayer)
            {
                SkipLayer(result, PubSubMessageType.Error,
                    $"'{layer.SourceKey}': estimated {estimatedTotal:N0} items exceeds the limit of " +
                    $"{context.Settings.MaxItemsPerLayer:N0}. Lower the brush density or shrink the zones " +
                    "(MaxItemsPerLayer in MT_Biome/settings.json raises the limit).");
                continue;
            }

            // Replace semantics: remove what this layer generated before.
            var previous = context.Manifest.Layers.FirstOrDefault(l => l.LayerId == layer.LayerId);
            if (previous != null)
            {
                var deleteResult = DeleteManifestLayers(context, new[] { previous });
                result.ItemsRemovedBeforeRegenerate += deleteResult.ItemsRemoved;
            }

            var seedBase = layer.SeedOverride ?? context.Settings.GlobalSeed;

            using var writer = new BiomeLayerForestWriter(
                context.LevelPath, layer.SourceKey, layer.LayerId, context.TerrainOriginX, context.TerrainOriginY);

            for (var zoneIndex = 0; zoneIndex < layer.Zones.Count; zoneIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zone = layer.Zones[zoneIndex];
                var pixels = zonePixels[zoneIndex];
                if (pixels.Length == 0 || zone.Items.Count == 0)
                    continue;

                var itemSpecs = zoneSpecs[zoneIndex];
                if (itemSpecs.Count == 0)
                    continue;

                var seed = BiomeSeed.Derive(seedBase, layer.LayerId, zoneIndex);
                var progressTimer = Stopwatch.StartNew();
                var zoneNumber = zoneIndex + 1;
                var options = new BiomeSamplerOptions
                {
                    SpacingFactor = context.Settings.SpacingFactor,
                    ZoneSlopeMinDeg = zone.SlopeMinDeg,
                    ZoneSlopeMaxDeg = zone.SlopeMaxDeg,
                    Progress = (accepted, target) =>
                    {
                        // Time-throttled: every message costs an O(n) dedupe scan and a
                        // full Blazor re-render on the page — unthrottled progress was a
                        // dominant share of generation wall time.
                        if (progressTimer.Elapsed < ProgressInterval)
                            return;
                        progressTimer.Restart();
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Layer '{layer.SourceKey}' zone {zoneNumber}: {accepted:N0}/{target:N0} items placed");
                    },
                };

                BiomePlacementSampler.SampleZoneStreaming(
                    terrainContext, pixels, itemSpecs, seed, writer.Write, options, cancellationToken);
            }

            var fileSha = writer.Complete();
            anyWritten = true;

            if (writer.Count > ItemCountWarningThreshold)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Layer '{layer.SourceKey}' produced {writer.Count:N0} items — this may impact game performance.");
            }

            context.Manifest.Layers.RemoveAll(l => l.LayerId == layer.LayerId);
            context.Manifest.Layers.Add(new BiomeManifestLayer
            {
                LayerId = layer.LayerId,
                Kind = layer.Kind.ToString(),
                SourceKey = layer.SourceKey,
                ForestFile = writer.ForestFileRelativePath,
                FileSha256 = fileSha,
                GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                SeedUsed = BiomeSeed.Derive(seedBase, layer.LayerId, 0),
                ItemCount = writer.Count,
            });

            result.LayersGenerated++;
            result.ItemsPlaced += writer.Count;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Layer '{layer.SourceKey}': placed {writer.Count:N0} forest items.");
        }

        if (anyWritten)
        {
            BiomeForestWriter.EnsureForestSceneObject(context.LevelPath);
        }

        // Mandatory post-step: the negative list sweeps freshly generated items off
        // roads/parkings/etc. Only manifest-tracked items — foreign-item cleanup is an
        // explicit, confirmed user action and never runs automatically.
        if (anyWritten && context.Settings.NegativeList.HasEntries)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Running negative-list cleanup on the generated items...");
            var mask = BuildNegativeMask(context, out var cleanupFailReason);
            if (mask != null)
            {
                result.ItemsRemovedByCleanup =
                    RemoveTrackedItemsOnMask(context, mask, cancellationToken).ItemsRemoved;
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Automatic negative-list cleanup did not run: {cleanupFailReason}");
            }
        }

        context.Manifest.TerFileTimestampUtc = File.GetLastWriteTimeUtc(context.TerrainFilePath).ToString("o");
        BiomeManifestStore.Save(context.LevelPath, context.Manifest);
        context.Settings.Save(context.LevelPath);

        return result;
    }

    /// <summary>
    /// Negative-list cleanup ("Cleanup Now"): removes manifest-tracked items standing on
    /// the combined negative mask and saves the manifest. Returns null when the list is
    /// empty or produced no usable mask (reason already sent to the log). The session
    /// carries the mask for the optional foreign-item follow-up.
    /// </summary>
    public (BiomeCleanupSession? Session, string? FailReason) RunNegativeListCleanup(
        BiomeLevelContext context, CancellationToken cancellationToken = default)
    {
        if (!context.Settings.NegativeList.HasEntries)
        {
            return (null, "The negative list is empty — nothing to clean up.");
        }

        var mask = BuildNegativeMask(context, out var maskFailReason);
        if (mask == null)
            return (null, maskFailReason);

        var result = RemoveTrackedItemsOnMask(context, mask, cancellationToken);
        BiomeManifestStore.Save(context.LevelPath, context.Manifest);
        context.Settings.Save(context.LevelPath);
        return (new BiomeCleanupSession { Mask = mask, TrackedResult = result }, null);
    }

    /// <summary>
    /// Counts forest items NOT tracked by the manifest that stand on the session's negative
    /// mask — the number shown in the foreign-item confirmation dialog. Tracked items on the
    /// mask were already removed by <see cref="RunNegativeListCleanup"/>, so every remaining
    /// hit in any forest file is foreign (or an orphaned record, which counts as foreign too).
    /// </summary>
    public int CountForeignItemsOnMask(BiomeLevelContext context, BiomeCleanupSession session)
    {
        var predicate = CreateMaskPredicate(context, session);
        var count = 0;
        var forestDir = Path.Join(context.LevelPath, "forest");
        if (!Directory.Exists(forestDir))
            return 0;

        foreach (var file in Directory.GetFiles(forestDir, "*.forest4.json", SearchOption.TopDirectoryOnly))
        {
            count += BiomeForestLineFilter.CountLinesWhere(File.ReadLines(file), predicate);
        }
        return count;
    }

    /// <summary>
    /// Removes every remaining forest item on the session's negative mask from every
    /// forest file — the confirmed foreign-item cleanup. Untouched files keep their
    /// bytes (and therefore their fast-path hashes) intact.
    /// </summary>
    public int RemoveForeignItemsOnMask(BiomeLevelContext context, BiomeCleanupSession session)
    {
        var predicate = CreateMaskPredicate(context, session);
        var removed = 0;
        var forestDir = Path.Join(context.LevelPath, "forest");
        if (!Directory.Exists(forestDir))
            return 0;

        foreach (var file in Directory.GetFiles(forestDir, "*.forest4.json", SearchOption.TopDirectoryOnly))
        {
            removed += RemoveForestFileLinesWhere(file, predicate);
        }

        if (removed > 0)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Cleanup: removed {removed:N0} foreign forest item(s) standing on the negative list.");
        }
        return removed;
    }

    private static BiomeItemPredicate CreateMaskPredicate(BiomeLevelContext context, BiomeCleanupSession session)
    {
        var size = context.TerrainSize;
        var mpp = context.MetersPerPixel;
        var originX = context.TerrainOriginX;
        var originY = context.TerrainOriginY;
        var mask = session.Mask;
        return (_, x, y, _, _) => BiomeCleanupMask.ContainsWorldPosition(mask, size, mpp, originX, originY, x, y);
    }

    /// <summary>
    /// Combined negative mask (materials OR OSM layers, buffer-expanded), or null with a
    /// reason in <paramref name="failReason"/>. Per-entry load problems are logged as
    /// warnings; the overall failure reason is returned so the UI can show it directly.
    /// </summary>
    private static bool[]? BuildNegativeMask(BiomeLevelContext context, out string? failReason)
    {
        failReason = null;
        var negative = context.Settings.NegativeList;
        var size = context.TerrainSize;
        var mask = new bool[size * size];
        var sources = 0;
        var entryProblems = new List<string>();

        foreach (var materialName in negative.MaterialInternalNames)
        {
            if (context.MaterialIndexByName.TryGetValue(materialName, out var index))
            {
                BiomeCleanupMask.OrMaterial(mask, context.Terrain.MaterialData, index);
                sources++;
            }
            else
            {
                var problem = $"terrain material '{materialName}' not found in the .ter file";
                entryProblems.Add(problem);
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Negative list: {problem} — ignored.");
            }
        }

        foreach (var key in negative.OsmLayerKeys)
        {
            var osmMask = TryLoadOsmMask(context, key, out var maskFailReason);
            if (osmMask == null)
            {
                entryProblems.Add(maskFailReason!.TrimEnd('.'));
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Negative list: {maskFailReason}");
                continue;
            }
            BiomeCleanupMask.OrMask(mask, osmMask);
            sources++;
        }

        if (sources == 0)
        {
            failReason = "None of the selected negative-list layers could be loaded: " +
                         string.Join("; ", entryProblems) + ".";
            return null;
        }

        mask = BiomeCleanupMask.ExpandByMeters(mask, size, context.MetersPerPixel, negative.BufferMeters);

        if (BiomeCleanupMask.CountSet(mask) == 0)
        {
            failReason = "The selected negative-list layers cover no terrain pixels — nothing to clean up.";
            return null;
        }

        return mask;
    }

    /// <summary>
    /// Removes manifest-tracked items standing on the mask, then rewrites the affected
    /// layers' sidecars, counts and file hashes so later fast-path deletes stay valid.
    /// Does not save the manifest — callers do.
    /// </summary>
    private static BiomeCleanupResult RemoveTrackedItemsOnMask(
        BiomeLevelContext context, bool[] mask, CancellationToken cancellationToken)
    {
        var result = new BiomeCleanupResult();
        var size = context.TerrainSize;
        var mpp = context.MetersPerPixel;
        var originX = context.TerrainOriginX;
        var originY = context.TerrainOriginY;
        var forestDir = Path.Join(context.LevelPath, "forest");

        foreach (var layer in context.Manifest.Layers.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = BiomeManifestStore.LoadLayerItems(context.LevelPath, layer);
            var keep = new List<BiomeManifestItem>(items.Count);
            var remove = new List<BiomeManifestItem>();
            foreach (var item in items)
            {
                if (item.Pos.Length >= 2 &&
                    BiomeCleanupMask.ContainsWorldPosition(mask, size, mpp, originX, originY, item.Pos[0], item.Pos[1]))
                    remove.Add(item);
                else
                    keep.Add(item);
            }

            if (remove.Count == 0)
                continue;

            var matched = 0;
            if (Directory.Exists(forestDir))
            {
                foreach (var file in Directory.GetFiles(forestDir, "*.forest4.json", SearchOption.TopDirectoryOnly))
                {
                    matched += FilterForestFileStreaming(file, remove);
                }
            }

            var orphans = remove.Count - matched;
            if (orphans > 0)
            {
                result.OrphanRecords += orphans;
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cleanup: {orphans} tracked item(s) of layer '{layer.SourceKey}' were not found in any " +
                    "forest file (already deleted in the editor?). Their records were dropped.");
            }

            if (keep.Count == 0)
            {
                BiomeManifestStore.DeleteLayerItemsSidecar(context.LevelPath, layer.LayerId);
                context.Manifest.Layers.Remove(layer);
            }
            else
            {
                BiomeManifestStore.SaveLayerItems(context.LevelPath, layer.LayerId, keep);
                layer.Items = new List<BiomeManifestItem>(); // legacy inline records migrate to the sidecar
                layer.ItemCount = keep.Count;
                var ownedFile = Path.Join(context.LevelPath,
                    layer.ForestFile.Replace('/', Path.DirectorySeparatorChar));
                layer.FileSha256 = File.Exists(ownedFile)
                    ? BiomeManifestStore.ComputeFileSha256(ownedFile)
                    : string.Empty;
            }

            result.LayersAffected++;
            result.ItemsRemoved += matched;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Cleanup: removed {matched:N0} generated item(s) of layer '{layer.SourceKey}' standing on the negative list.");
        }

        return result;
    }

    /// <summary>
    /// Zone pixel index lists for one layer (terrain material or OSM mask region), or
    /// null with a "'{layer}': ..." reason in <paramref name="skipReason"/> — the caller
    /// logs it AND surfaces it in the UI (snackbar), so nothing is logged here.
    /// </summary>
    private static List<int[]>? ComputeLayerZonePixels(
        BiomeLevelContext context, BiomeLayerSettings layer, out string? skipReason)
    {
        skipReason = null;
        var bands = layer.Zones
            .Select(z => new BiomeZoneBandDefinition(z.DepthMeters, z.IsInterior))
            .ToList();

        try
        {
            if (layer.Kind == BiomeLayerKind.TerrainMaterial)
            {
                if (!context.MaterialIndexByName.TryGetValue(layer.SourceKey, out var materialIndex))
                {
                    skipReason = $"'{layer.SourceKey}': terrain material not found in the .ter file.";
                    return null;
                }
                return BiomeZoneBander.ComputeZonePixels(
                    context.Terrain.MaterialData, materialIndex, context.TerrainSize, context.MetersPerPixel, bands);
            }

            var mask = TryLoadOsmMask(context, layer.SourceKey, out var maskFailReason);
            if (mask == null)
            {
                skipReason = $"'{layer.SourceKey}': {maskFailReason}";
                return null;
            }
            if (BiomeOsmMaskLoader.CountInRegion(mask) == 0)
            {
                skipReason = $"'{layer.SourceKey}': the OSM mask has no white pixels — nothing to place.";
                return null;
            }
            return BiomeZoneBander.ComputeZonePixels(mask, context.TerrainSize, context.MetersPerPixel, bands);
        }
        catch (ArgumentException ex)
        {
            skipReason = $"'{layer.SourceKey}': invalid zone configuration ({ex.Message}).";
            return null;
        }
    }

    /// <summary>
    /// Loads one OSM mask PNG into terrain space (Y-flipped, holes subtracted), or null
    /// with a plain-language reason in <paramref name="failReason"/> — callers decide
    /// where to surface it (log, snackbar, or both).
    /// </summary>
    private static bool[]? TryLoadOsmMask(BiomeLevelContext context, string key, out string? failReason)
    {
        failReason = null;
        var info = context.OsmLayers.FirstOrDefault(o =>
            o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (info == null || !File.Exists(info.FilePath))
        {
            failReason = $"OSM mask '{key}.png' was not found under MT_TerrainGeneration. " +
                         "Re-select the level folder to refresh the available OSM layers.";
            return null;
        }

        bool[] mask;
        try
        {
            mask = BiomeOsmMaskLoader.Load(info.FilePath, context.TerrainSize);
        }
        catch (Exception ex)
        {
            failReason = $"could not load OSM mask '{Path.GetFileName(info.FilePath)}' ({ex.Message}).";
            return null;
        }

        var holePixels = BiomeOsmMaskLoader.SubtractHoles(mask, context.Terrain.MaterialData);
        if (holePixels > 0)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"OSM mask '{key}': {holePixels:N0} terrain-hole pixel(s) removed.");
        }
        return mask;
    }

    private const string MaterialOsmMaskSuffix = "_osm_layer";

    /// <summary>
    /// Discovers OSM mask PNGs: per-category masks under MT_TerrainGeneration\osm_layer\
    /// (primary source) plus {material}_osm_layer.png selection masks next to them.
    /// </summary>
    private static List<BiomeOsmLayerInfo> DiscoverOsmLayers(string levelPath)
    {
        var list = new List<BiomeOsmLayerInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var categoryDir = Path.Join(levelPath, "MT_TerrainGeneration", "osm_layer");
        if (Directory.Exists(categoryDir))
        {
            foreach (var file in Directory.GetFiles(categoryDir, "*.png", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(key))
                {
                    list.Add(new BiomeOsmLayerInfo
                    {
                        Key = key,
                        DisplayName = PrettifyOsmLayerName(key),
                        FilePath = file,
                        IsMaterialSelection = false,
                    });
                }
            }
        }

        var generationDir = Path.Join(levelPath, "MT_TerrainGeneration");
        if (Directory.Exists(generationDir))
        {
            foreach (var file in Directory
                         .GetFiles(generationDir, "*" + MaterialOsmMaskSuffix + ".png", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(key))
                {
                    list.Add(new BiomeOsmLayerInfo
                    {
                        Key = key,
                        DisplayName = PrettifyOsmLayerName(key),
                        FilePath = file,
                        IsMaterialSelection = true,
                    });
                }
            }
        }

        return list;
    }

    /// <summary>"landuse_forest_polygon" → "Landuse: forest (polygon)"; "Grass2_osm_layer" → "Material OSM: Grass2".</summary>
    public static string PrettifyOsmLayerName(string stem)
    {
        if (stem.EndsWith(MaterialOsmMaskSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return "Material OSM: " + stem[..^MaterialOsmMaskSuffix.Length];
        }

        var tokens = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return stem;

        var suffix = string.Empty;
        if (tokens[^1].Equals("polygon", StringComparison.OrdinalIgnoreCase))
        {
            suffix = " (polygon)";
            tokens = tokens[..^1];
        }
        else if (tokens[^1].Equals("linestring", StringComparison.OrdinalIgnoreCase))
        {
            suffix = " (line)";
            tokens = tokens[..^1];
        }

        if (tokens.Length == 0)
            return stem;

        var category = char.ToUpperInvariant(tokens[0][0]) + tokens[0][1..];
        var rest = string.Join(' ', tokens.Skip(1));
        return rest.Length > 0 ? $"{category}: {rest}{suffix}" : category + suffix;
    }

    private static void SkipLayer(BiomeGenerationResult result, PubSubMessageType messageType, string reason)
    {
        result.LayersSkipped++;
        result.SkipReasons.Add(reason);
        PubSubChannel.SendMessage(messageType, $"Layer {reason} — skipped.");
    }

    /// <summary>Deletes generated items for the given layer ids, or ALL generated items when null.</summary>
    public BiomeDeleteResult DeleteGenerated(BiomeLevelContext context, IReadOnlyCollection<string>? layerIds = null)
    {
        var targets = layerIds == null
            ? context.Manifest.Layers.ToList()
            : context.Manifest.Layers.Where(l => layerIds.Contains(l.LayerId)).ToList();

        var result = DeleteManifestLayers(context, targets);

        if (layerIds == null)
        {
            // Global delete additionally sweeps biome-owned files no manifest layer claims —
            // leftovers of generations that crashed before the manifest was written.
            result.OrphanFilesDeleted = SweepOrphanedBiomeFiles(context);
        }

        BiomeManifestStore.Save(context.LevelPath, context.Manifest);
        return result;
    }

    private static int WarnAboutOrphanedBiomeFiles(string levelPath, BiomeManifest manifest)
    {
        var forestDir = Path.Join(levelPath, "forest");
        if (!Directory.Exists(forestDir))
            return 0;

        var claimed = new HashSet<string>(
            manifest.Layers.Select(l => l.ForestFile.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        long orphanBytes = 0;
        var orphanCount = 0;
        foreach (var file in Directory.GetFiles(forestDir,
                     BiomeForestWriter.OwnedFilePrefix + "*.forest4.json", SearchOption.TopDirectoryOnly))
        {
            if (claimed.Contains("forest/" + Path.GetFileName(file)))
                continue;
            orphanCount++;
            orphanBytes += new FileInfo(file).Length;
        }

        if (orphanCount > 0)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"{orphanCount} orphaned biome forest file(s) found ({orphanBytes / (1024.0 * 1024.0):0.#} MB) — " +
                "left over from an interrupted generation. \"Delete All Generated\" removes them.");
        }

        return orphanCount;
    }

    /// <summary>
    /// Deletes MT_biome_* forest files and item sidecars not referenced by any manifest layer.
    /// The MT_biome_ prefix is this feature's namespace — such files are always ours.
    /// </summary>
    private static int SweepOrphanedBiomeFiles(BiomeLevelContext context)
    {
        var deleted = 0;
        var claimed = new HashSet<string>(
            context.Manifest.Layers.Select(l => l.ForestFile.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        var forestDir = Path.Join(context.LevelPath, "forest");
        if (Directory.Exists(forestDir))
        {
            foreach (var file in Directory.GetFiles(forestDir,
                         BiomeForestWriter.OwnedFilePrefix + "*.forest4.json", SearchOption.TopDirectoryOnly))
            {
                var relative = "forest/" + Path.GetFileName(file);
                if (claimed.Contains(relative))
                    continue;
                File.Delete(file);
                deleted++;
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Removed orphaned biome forest file {Path.GetFileName(file)} (no manifest entry — crashed generation?).");
            }
        }

        var claimedLayerIds = new HashSet<string>(
            context.Manifest.Layers.Select(l => l.LayerId), StringComparer.OrdinalIgnoreCase);
        var itemsDir = Path.Join(BiomeSettings.GetFolderPath(context.LevelPath), "items");
        if (Directory.Exists(itemsDir))
        {
            foreach (var file in Directory.GetFiles(itemsDir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (claimedLayerIds.Contains(Path.GetFileNameWithoutExtension(file)))
                    continue;
                File.Delete(file);
            }
        }

        return deleted;
    }

    private BiomeDeleteResult DeleteManifestLayers(BiomeLevelContext context, IReadOnlyList<BiomeManifestLayer> targets)
    {
        var result = new BiomeDeleteResult();

        foreach (var layer in targets)
        {
            var matched = 0;
            var absolutePath = Path.Join(context.LevelPath,
                layer.ForestFile.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(absolutePath) &&
                !string.IsNullOrEmpty(layer.FileSha256) &&
                BiomeManifestStore.ComputeFileSha256(absolutePath) == layer.FileSha256)
            {
                // Fast path: file untouched since we wrote it — only our items are inside.
                File.Delete(absolutePath);
                matched = layer.ItemCount;
            }
            else
            {
                // Fallback: the file was modified/merged externally (or is gone). Filter
                // every forest file line-wise; foreign lines survive verbatim. Records are
                // loaded from the per-layer sidecar only here — never at page load.
                var items = BiomeManifestStore.LoadLayerItems(context.LevelPath, layer);
                var forestDir = Path.Join(context.LevelPath, "forest");
                if (Directory.Exists(forestDir) && items.Count > 0)
                {
                    foreach (var file in Directory.GetFiles(forestDir, "*.forest4.json", SearchOption.TopDirectoryOnly))
                    {
                        matched += FilterForestFileStreaming(file, items);
                    }
                }

                var orphans = layer.ItemCount - matched;
                if (orphans > 0)
                {
                    result.OrphanRecords += orphans;
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Layer '{layer.SourceKey}': {orphans} generated item(s) were not found in any forest file " +
                        "(already deleted in the editor?). Their records were dropped.");
                }
            }

            BiomeManifestStore.DeleteLayerItemsSidecar(context.LevelPath, layer.LayerId);
            context.Manifest.Layers.Remove(layer);
            result.LayersDeleted++;
            result.ItemsRemoved += matched;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Layer '{layer.SourceKey}': removed {matched:N0} generated forest item(s).");
        }

        return result;
    }

    private static int FilterForestFileStreaming(string filePath, IReadOnlyCollection<BiomeManifestItem> items)
    {
        return RewriteForestFile(filePath,
            (lines, sink) => BiomeForestLineFilter.FilterLinesStreaming(lines, items, sink));
    }

    private static int RemoveForestFileLinesWhere(string filePath, BiomeItemPredicate shouldRemove)
    {
        return RewriteForestFile(filePath,
            (lines, sink) => BiomeForestLineFilter.FilterLinesWhereStreaming(lines, shouldRemove, sink));
    }

    /// <summary>
    /// Filters one forest file without buffering it: kept lines stream into a temp file
    /// which replaces the original only when something was actually removed; a file left
    /// with no non-empty lines is deleted entirely. Returns the number of removed lines.
    /// </summary>
    private static int RewriteForestFile(string filePath, Func<IEnumerable<string>, Action<string>, int> filter)
    {
        var tempPath = filePath + ".mtbiome.tmp";
        int removed;
        var keptNonEmpty = 0;
        try
        {
            using (var tempWriter = new StreamWriter(File.Create(tempPath)))
            {
                removed = filter(File.ReadLines(filePath), line =>
                {
                    tempWriter.WriteLine(line);
                    if (!string.IsNullOrWhiteSpace(line))
                        keptNonEmpty++;
                });
            }

            if (removed == 0)
            {
                File.Delete(tempPath);
                return 0;
            }

            if (keptNonEmpty == 0)
            {
                // Nothing of substance left — remove the file entirely.
                File.Delete(filePath);
                File.Delete(tempPath);
                return removed;
            }

            File.Move(tempPath, filePath, overwrite: true);
            return removed;
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static List<BiomeItemSpec> BuildItemSpecs(BiomeLevelContext context, BiomeZoneSettings zone)
    {
        var specs = new List<BiomeItemSpec>();
        // Per brush: the brush density is the total; item MixWeights split it as a
        // normalized species mix. Checking more items redistributes, never multiplies.
        foreach (var group in zone.Items
                     .Where(i => i.MixWeight > 0)
                     .GroupBy(i => i.BrushName, StringComparer.OrdinalIgnoreCase))
        {
            var brushDensity = zone.BrushDensityDefaults.TryGetValue(group.Key, out var d)
                ? d
                : BiomeSettings.DefaultBrushDensityPercent;
            if (brushDensity <= 0)
                continue;

            var sumWeights = group.Sum(i => i.MixWeight);
            if (sumWeights <= 0)
                continue;

            foreach (var selection in group)
            {
                if (!context.Catalog.ItemData.TryGetValue(selection.ItemDataName, out var itemData))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Forest item type '{selection.ItemDataName}' no longer exists in managedItemData.json — skipped.");
                    continue;
                }

                var element = string.IsNullOrEmpty(selection.BrushName)
                    ? null
                    : context.Catalog.FindElement(selection.BrushName, selection.ItemDataName);

                specs.Add(new BiomeItemSpec
                {
                    TypeName = itemData.Name,
                    DensityPercent = brushDensity * selection.MixWeight / sumWeights,
                    RadiusMeters = itemData.Radius ?? BiomeDensityModel.DefaultRadiusMeters,
                    ScaleMin = element?.ScaleMin ?? 0.8,
                    ScaleMax = Math.Max(element?.ScaleMax ?? 1.2, element?.ScaleMin ?? 0.8),
                    SinkMin = element?.SinkMin ?? 0.0,
                    SinkMax = Math.Max(element?.SinkMax ?? 0.1, element?.SinkMin ?? 0.0),
                    RotationRangeDeg = element?.RotationRange ?? 360,
                    SlopeMinDeg = element?.SlopeMin,
                    SlopeMaxDeg = element?.SlopeMax,
                    ElevationMin = element?.ElevationMin,
                    ElevationMax = element?.ElevationMax,
                });
            }
        }
        return specs;
    }

    private static List<BiomeMaterialInfo> BuildMaterialList(string levelPath, TerrainV9Binary terrain)
    {
        // Colors come from the terrain materials JSON (best effort — list works without it).
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var materialsJsonPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(levelPath);
            if (!string.IsNullOrWhiteSpace(materialsJsonPath) && File.Exists(materialsJsonPath))
            {
                var materialJsons = new List<MaterialJson>();
                var copyAssets = new List<CopyAsset>();
                var scanner = new TerrainCopyScanner(materialsJsonPath, levelPath, levelPath, materialJsons, copyAssets);
                scanner.ScanTerrainMaterials();
                TerrainCopyScanner.ExtractTerrainMaterialColors(levelPath, copyAssets);
                foreach (var asset in copyAssets.Where(a => a.CopyAssetType == CopyAssetType.Terrain))
                {
                    if (!string.IsNullOrEmpty(asset.TerrainMaterialInternalName))
                        colors[asset.TerrainMaterialInternalName] = asset.BaseColorHex;
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not extract terrain material colors: {ex.Message}");
        }

        // Single histogram pass over the material bytes — no per-material masks, no LINQ.
        var histogram = new long[256];
        foreach (var b in terrain.MaterialData)
        {
            histogram[b]++;
        }

        var totalPixels = (double)terrain.Size * terrain.Size;
        var list = new List<BiomeMaterialInfo>();
        for (var i = 0; i < terrain.MaterialNames.Length && i < 255; i++)
        {
            var name = terrain.MaterialNames[i];
            var pixelCount = histogram[i];
            list.Add(new BiomeMaterialInfo
            {
                InternalName = name,
                BaseColorHex = colors.TryGetValue(name, out var hex) ? hex : "#808080",
                PixelCount = pixelCount,
                CoveragePercent = totalPixels > 0 ? pixelCount / totalPixels * 100.0 : 0,
            });
        }
        return list;
    }

    private sealed record TerrainBlockParams(
        float? SquareSize,
        float? MaxHeight,
        float? PositionX,
        float? PositionY,
        float? PositionZ,
        string? TerrainFile);

    private static TerrainBlockParams ReadTerrainBlockParams(string levelPath)
    {
        var empty = new TerrainBlockParams(null, null, null, null, null, null);
        try
        {
            var mainPath = Path.Join(levelPath, "main");
            if (!Directory.Exists(mainPath))
                return empty;

            foreach (var itemsFile in Directory.GetFiles(mainPath, "items.level.json", SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadLines(itemsFile))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line, BeamJsonOptions.GetJsonDocumentOptions());
                        if (!doc.RootElement.TryGetProperty("class", out var cls) ||
                            cls.GetString() != "TerrainBlock")
                            continue;

                        float? squareSize = null;
                        float? maxHeight = null;
                        float? positionX = null;
                        float? positionY = null;
                        float? positionZ = null;
                        string? terrainFile = null;
                        if (doc.RootElement.TryGetProperty("squareSize", out var sq) &&
                            sq.ValueKind == JsonValueKind.Number)
                            squareSize = (float)sq.GetDouble();
                        if (doc.RootElement.TryGetProperty("maxHeight", out var mh) &&
                            mh.ValueKind == JsonValueKind.Number)
                            maxHeight = (float)mh.GetDouble();
                        if (doc.RootElement.TryGetProperty("position", out var pos) &&
                            pos.ValueKind == JsonValueKind.Array &&
                            pos.GetArrayLength() >= 3)
                        {
                            positionX = (float)pos[0].GetDouble();
                            positionY = (float)pos[1].GetDouble();
                            positionZ = (float)pos[2].GetDouble();
                        }
                        if (doc.RootElement.TryGetProperty("terrainFile", out var tf) &&
                            tf.ValueKind == JsonValueKind.String)
                            terrainFile = tf.GetString();

                        return new TerrainBlockParams(squareSize, maxHeight, positionX, positionY, positionZ, terrainFile);
                    }
                    catch (JsonException)
                    {
                        // skip malformed lines
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not read TerrainBlock parameters: {ex.Message}");
        }

        return empty;
    }

    /// <summary>
    /// Resolves the TerrainBlock's terrainFile property (e.g. "/levels/x/terrain4.ter")
    /// to an existing file in the level folder, or null.
    /// </summary>
    private static string? ResolveTerrainFile(string levelPath, string? terrainFileProperty)
    {
        if (string.IsNullOrWhiteSpace(terrainFileProperty))
            return null;

        var fileName = Path.GetFileName(terrainFileProperty.Replace('\\', '/').TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var direct = Path.Join(levelPath, fileName);
        if (File.Exists(direct))
            return direct;

        try
        {
            return Directory.GetFiles(levelPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ValidateLevelFolder(string folder)
    {
        var levelPath = ZipFileHandler.GetNamePath(folder);
        if (!string.IsNullOrWhiteSpace(levelPath))
            return levelPath;

        return File.Exists(Path.Join(folder, "info.json")) ? folder : null;
    }

    private static string? FindTerrainTerFile(string levelPath)
    {
        var topLevelFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.TopDirectoryOnly);
        var theTerrain = topLevelFiles.FirstOrDefault(x =>
            Path.GetFileName(x).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(theTerrain))
            return theTerrain;
        if (topLevelFiles.Length > 0)
            return topLevelFiles[0];

        var allFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.AllDirectories);
        return allFiles.FirstOrDefault(x =>
                   Path.GetFileName(x).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase))
               ?? allFiles.FirstOrDefault();
    }
}
