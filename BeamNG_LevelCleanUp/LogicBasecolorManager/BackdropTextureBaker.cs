using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

/// <summary>
/// Bakes one satellite texture per backdrop chunk into art/shapes/MT_backdrop/textures/, reusing the
/// shared <see cref="MapTileOverlayService"/> tile pipeline (spec §10).
/// </summary>
/// <remarks>
/// North-up convention: chunk PNGs are baked with row 0 = north. The per-chunk request reuses the
/// same warp path as the terrain overlay (web-map tile mosaic composited north-tile-first, then
/// warped through a translated geotransform), which already produces north-up output. This class
/// never flips the image — any V-orientation reconciliation happens exclusively on the DAE side
/// (<c>BackdropSceneWriter</c>'s <c>FlipUVVertical = false</c>, in-game confirmed 2026-07-28 —
/// see that class doc), so introducing a flip here would double it up.
/// </remarks>
public class BackdropTextureBaker
{
    private static readonly Rgba32 FlatFallbackColor = new(128, 128, 128, 255);
    private readonly MapTileOverlayService _overlayService = new();

    /// <summary>
    ///     Warps + adjusts one satellite texture per chunk into art/shapes/MT_backdrop/textures/.
    ///     Returns the number of successfully baked chunks. One bad chunk never fails the run
    ///     (retry once, then flat-gray + warning — spec §10).
    /// </summary>
    public async Task<int> BakeAllChunksAsync(string levelPath, MtSettings settings)
    {
        var backdrop = settings.BackdropSettings;
        if (backdrop is not { Enabled: true } || backdrop.Chunks.Count == 0)
            return 0;

        var overlay = settings.BasecolorModeSettings.OverlaySettings;
        var provider = string.IsNullOrWhiteSpace(overlay.SelectedTileProvider)
            ? "Google Satelite Only" : overlay.SelectedTileProvider;
        var texturesDir = Path.Join(levelPath, "art", "shapes", "MT_backdrop", "textures");
        Directory.CreateDirectory(texturesDir);
        var cacheRoot = Path.Join(levelPath, "MT_Tiles", "cache");           // SHARED tile cache (spec §10)
        var adjustmentFingerprint = FormattableString.Invariant(
            $"adj:{overlay.Brightness:F4}|{overlay.Contrast:F4}|{overlay.Saturation:F4}");

        var baked = 0;
        foreach (var chunk in backdrop.Chunks)
        {
            // GetFileName strips any directory component - MT_settings.json is hand-editable, and an
            // unsanitized "..\" in TextureFile would otherwise escape texturesDir on the join below.
            var outputPath = Path.Join(texturesDir, Path.GetFileName(chunk.TextureFile));
            // Per-chunk translated geotransform — same affine math as GetEffectiveSourceGeoTransform:
            double[]? gt = null;
            if (backdrop.SourceGeoTransform is { Length: 6 } baseGt && !string.IsNullOrWhiteSpace(backdrop.ProjectionWkt))
            {
                gt = baseGt.ToArray();
                gt[0] = baseGt[0] + chunk.SourceRectX * baseGt[1] + chunk.SourceRectY * baseGt[2];
                gt[3] = baseGt[3] + chunk.SourceRectX * baseGt[4] + chunk.SourceRectY * baseGt[5];
            }
            var request = new OverlayRequest(
                new GeoBoundingBox(chunk.MinLongitude, chunk.MinLatitude, chunk.MaxLongitude, chunk.MaxLatitude),
                backdrop.TerrainMetersPerPixel,
                gt,
                (int)Math.Round(chunk.SourceRectWidth), (int)Math.Round(chunk.SourceRectHeight),
                string.IsNullOrWhiteSpace(backdrop.ProjectionWkt) ? null : backdrop.ProjectionWkt,
                chunk.TextureSize, outputPath, cacheRoot, provider,
                string.IsNullOrWhiteSpace(overlay.TileImageryDate) ? null : overlay.TileImageryDate,
                adjustmentFingerprint);

            var success = await TryBakeChunkAsync(request, overlay, chunk);   // retry once inside
            if (success) baked++;
        }
        settings.BackdropSettings!.LastTextureBakeUtc = DateTime.UtcNow;
        settings.BackdropSettings.LastBakeProvider = provider;
        settings.BackdropSettings.LastBakeImageryDate = overlay.TileImageryDate ?? string.Empty;
        return baked;
    }

    /// <summary>
    /// Fetches (or reuses) the tile overlay for one chunk. On a fresh warp (<c>!ReusedFinalImage</c>)
    /// the brightness/contrast/saturation adjustments are baked into the final PNG so a cached hit is
    /// never re-adjusted (the <c>ExtraFingerprint</c> on the request already forces a fresh warp
    /// whenever the adjustments change). Retries the fetch once before falling back to a flat gray
    /// texture, so a single bad chunk (e.g. a transient tile-provider failure) never fails the whole
    /// bake (spec §10). Every step that touches disk is guarded end-to-end — nothing here may ever
    /// propagate out and abort the caller's foreach loop.
    /// </summary>
    private async Task<bool> TryBakeChunkAsync(OverlayRequest request, MtBasecolorOverlaySettings overlay, MtBackdropChunk chunk)
    {
        var success = false;
        for (var attempt = 1; attempt <= 2 && !success; attempt++)
        {
            try
            {
                var result = await _overlayService.EnsureOverlayImageAsync(request);
                if (!result.ReusedFinalImage)
                {
                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(request.OutputPath);
                    TerrainPbrMapBuilder.ApplyOverlayAdjustments(image, overlay.Brightness, overlay.Contrast, overlay.Saturation);
                    image.SaveAsPng(request.OutputPath); // SaveAsPng truncates/overwrites in place - no separate delete step needed.
                }

                success = true;
            }
            catch (Exception) when (attempt == 1)
            {
                // First attempt failed. This also covers the case where EnsureOverlayImageAsync itself
                // succeeded - fetched/warped a fresh PNG and wrote its ".meta.json" fingerprint sidecar -
                // but the *local* adjustment step above then threw (e.g. a disk or file-lock error):
                // without clearing that sidecar, attempt 2 would see a matching fingerprint and take the
                // cache-hit path, silently reporting success while the unadjusted (or truncated) PNG is
                // still on disk. Clearing both files forces attempt 2 into a genuine fresh warp + adjust.
                ResetChunkOutput(request.OutputPath);
            }
            catch (Exception)
            {
                // Second attempt also failed - fall through to the flat-gray fallback below.
            }
        }

        if (!success)
            WriteFlatGrayFallback(request.OutputPath, chunk.TextureSize, chunk);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Backdrop chunk {chunk.Cx}_{chunk.Cy}: {(success ? "texture baked" : "flat fallback used")}.",
            modulo: true);

        return success;
    }

    /// <summary>
    /// Best-effort removal of a chunk's final PNG and its warp-fingerprint sidecar
    /// (<c>{OutputPath}.meta.json</c>, see <see cref="MapTileOverlayService"/>) so a retry cannot
    /// mistake a half-written or stale output for a valid cache hit. Each deletion is independently
    /// guarded - a locked file here must never escape as an exception (spec §10).
    /// </summary>
    private static void ResetChunkOutput(string outputPath)
    {
        TryDelete(outputPath);
        TryDelete(outputPath + ".meta.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort only - a locked file just means the next write attempts an in-place overwrite.
        }
    }

    /// <summary>
    /// Writes a flat #808080 placeholder texture for a chunk whose bake failed twice, so the run can
    /// still complete (spec §10). Guarded end-to-end: even if the fallback write itself fails (e.g.
    /// the output path is locked), the chunk is skipped with a warning instead of aborting the bake.
    /// </summary>
    private static void WriteFlatGrayFallback(string outputPath, int size, MtBackdropChunk chunk)
    {
        try
        {
            using var flat = new Image<Rgba32>(size, size, FlatFallbackColor);
            flat.SaveAsPng(outputPath); // SaveAsPng truncates/overwrites in place - no separate delete step needed.

            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Backdrop chunk {chunk.Cx}_{chunk.Cy}: tile download failed, flat texture used");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Backdrop chunk {chunk.Cx}_{chunk.Cy}: tile download failed, and the flat fallback texture could not be written ({ex.Message}).");
        }
    }
}
