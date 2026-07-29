using System.Diagnostics;
using System.Globalization;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>Outcome of <see cref="BackdropGenerator.Generate"/> — never thrown, always returned (spec §11).</summary>
public sealed class BackdropGenerationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; } = [];
    public BackdropChunkPlan? ChunkPlan { get; set; }
    public List<BackdropChunkExportItem> ExportedChunks { get; } = [];
    public int ChunksSkipped { get; set; }         // fully-nodata chunks (spec §11 table)
    public int TotalVertices { get; set; }
    public int TotalTriangles { get; set; }
    public double NodataPercentage { get; set; }

    // Phase timing (perf plan §0.1) — wall-clock seconds, zero when a phase didn't run. Debug time
    // includes the per-chunk quadtree-level-map bookkeeping, not just the artifact writes.
    public double RasterLoadSeconds { get; set; }
    public double MeshSeconds { get; set; }
    public double WorstChunkSeconds { get; set; }
    public string? WorstChunkName { get; set; }
    public double DebugArtifactSeconds { get; set; }
}

/// <summary>Outcome of <see cref="BackdropGenerator.Estimate"/> — a cheap cost probe for the UI (spec §5).</summary>
public sealed class BackdropEstimateResult
{
    public long EstimatedTriangles { get; set; }
    public long TextureMemoryBytes { get; set; }   // Σ chunkTexSize² × 4 (uncompressed upper bound, spec §5)
    public int ChunkCount { get; set; }
}

/// <summary>
///     Core end-to-end backdrop pipeline (spec §4): validate → rasters → plan → mesh → DAE/scene export,
///     plus a cheap <see cref="Estimate"/> cost probe for the UI. This is the only entry point the app
///     layer (<c>BackdropOrchestrator</c>, Task 14) needs to call.
/// </summary>
public sealed class BackdropGenerator
{
    /// <summary>Cap on the coarse far raster used by <see cref="Estimate"/> — always this cheap, regardless
    /// of <see cref="BackdropGenerationParameters.MaxFarRasterDimension"/> (spec §5).</summary>
    private const int EstimateFarRasterDimension = 512;

    /// <summary>
    ///     Full pipeline: validate → rasters → plan → mesh → DAEs → scene. Never throws for data errors —
    ///     any unexpected exception is caught and reported via <see cref="BackdropGenerationResult.ErrorMessage"/>
    ///     so the app layer decides how to surface it (spec §11).
    /// </summary>
    public BackdropGenerationResult Generate(BackdropGenerationParameters parameters, string? debugOutputPath = null)
    {
        var result = new BackdropGenerationResult();
        try
        {
            var validation = parameters.Validate();
            result.Warnings.AddRange(validation.Warnings);
            if (!validation.IsValid)
            {
                result.Success = false;
                result.ErrorMessage = string.Join(" ", validation.Errors);
                return result;
            }

            GeoTiffReader.InitializeGdal();

            // 1. Rasters: far raster (capped) covering the whole backdrop rect, plus up to 4 native-res
            //    band strips around the terrain edges (spec §6).
            var rasterStopwatch = Stopwatch.StartNew();
            var farRaster = BackdropRasterLoader.LoadWindow(parameters.SourceGeoTiffPath, parameters.BackdropRect,
                parameters.MaxFarRasterDimension, out var farNodataPercentage, out var farNodataMask);
            var bandRasters = LoadBandStrips(parameters, out var stripNodataCells, out var stripTotalCells);
            result.RasterLoadSeconds = rasterStopwatch.Elapsed.TotalSeconds;

            var farTotalCells = (long)farRaster.Width * farRaster.Height;
            var farNodataCells = (long)Math.Round(farNodataPercentage / 100.0 * farTotalCells);
            var totalCells = farTotalCells + stripTotalCells;
            var totalNodataCells = farNodataCells + stripNodataCells;
            result.NodataPercentage = totalCells > 0 ? 100.0 * totalNodataCells / totalCells : 0.0;
            if (result.NodataPercentage > 0)
                result.Warnings.Add(
                    $"{result.NodataPercentage:F1} % of the backdrop area has no elevation data (filled by edge extension)");

            // 2/4. Mapper + height field (the seam/blend logic, Task 4).
            var mapper = new BackdropCoordinateMapper(parameters.TerrainRect, parameters.TerrainSizePixels,
                parameters.TerrainMetersPerPixel);
            var heightField = new BackdropHeightField(farRaster, bandRasters, parameters.TerrainHeightMap, mapper,
                parameters.TerrainSizePixels, parameters.TerrainMetersPerPixel, parameters.TerrainBaseHeight,
                parameters.TerrainCropMinElevation, parameters.EdgeBandMeters);

            // 5. Chunk plan (Task 5).
            var plan = BackdropChunkPlanner.Plan(parameters);
            result.ChunkPlan = plan;

            var mesherOptions = new BackdropMesherOptions
            {
                MaxVerticalErrorNearMeters = parameters.MaxVerticalErrorNearMeters,
                MaxVerticalErrorFarMeters = parameters.MaxVerticalErrorFarMeters,
                EdgeBandMeters = parameters.EdgeBandMeters,
                MaxMarginMeters = plan.MaxMarginMeters,
                SeamSkirt = parameters.SeamSkirt,
                SeamSkirtDepthMeters = parameters.SeamSkirtDepthMeters,
                LatticeUnitMeters = parameters.TerrainMetersPerPixel,
                HalfSizeMeters = mapper.HalfSizeMeters
            };
            var importance = new List<IBackdropImportanceSource>
            {
                new EdgeBandImportanceSource(mapper.HalfSizeMeters, parameters.EdgeBandMeters,
                    parameters.TerrainMetersPerPixel)
            };

            // 6/9. Mesh + export each chunk. A fresh mesher per chunk — BackdropQuadtreeMesher is NOT
            // reentrant (LastFallbackCount is per-instance state).
            BackdropSceneWriter.CleanPreviousOutputs(parameters.LevelPath);
            var shapesDir = Path.Combine(parameters.LevelPath, "art", "shapes", "MT_backdrop");
            var sceneWriter = new BackdropSceneWriter();

            var chunkStatLines = new List<string>();
            var chunkLeavesForDebug = debugOutputPath != null
                ? new List<(BackdropChunkDefinition Chunk, IReadOnlyList<LeafCell> Leaves)>()
                : null;

            var meshStopwatch = new Stopwatch();
            var debugStopwatch = new Stopwatch();
            foreach (var chunk in plan.Chunks)
            {
                if (IsChunkFullyNodata(farNodataMask, farRaster, chunk))
                {
                    result.ChunksSkipped++;
                    result.Warnings.Add(
                        $"Chunk {chunk.DaeFileName} skipped: its source region has no elevation data at all.");
                    continue;
                }

                meshStopwatch.Start();
                var chunkStart = meshStopwatch.Elapsed;
                var mesher = new BackdropQuadtreeMesher(heightField, mesherOptions, importance);
                var meshResult = mesher.MeshChunk(chunk, out var refinedLeaves);
                var exportItem = sceneWriter.ExportChunkDae(chunk, meshResult, shapesDir,
                    collisionEnabled: parameters.CollisionMesh);
                var chunkSeconds = (meshStopwatch.Elapsed - chunkStart).TotalSeconds;
                meshStopwatch.Stop();
                if (chunkSeconds > result.WorstChunkSeconds)
                {
                    result.WorstChunkSeconds = chunkSeconds;
                    result.WorstChunkName = chunk.DaeFileName;
                }
                result.ExportedChunks.Add(exportItem);
                result.TotalVertices += exportItem.Vertices;
                result.TotalTriangles += exportItem.Triangles;

                if (debugOutputPath != null)
                {
                    debugStopwatch.Start();
                    chunkStatLines.Add(
                        $"{chunk.DaeFileName}: leaves={meshResult.LeafCount} vertices={exportItem.Vertices} " +
                        $"triangles={exportItem.Triangles} textureSize={chunk.TextureSize} " +
                        $"distance={chunk.DistanceToTerrainMeters:F1}m");
                    chunkLeavesForDebug!.Add((chunk, refinedLeaves));
                    debugStopwatch.Stop();
                }
            }
            result.MeshSeconds = meshStopwatch.Elapsed.TotalSeconds;

            sceneWriter.WriteMaterials(result.ExportedChunks, Path.Combine(shapesDir, "main.materials.json"),
                $"/levels/{parameters.LevelName}/art/shapes/MT_backdrop/textures/");
            sceneWriter.EnsureSimGroupInParent(
                Path.Combine(parameters.LevelPath, "main", "MissionGroup", "items.level.json"));
            sceneWriter.WriteSceneItems(result.ExportedChunks,
                Path.Combine(parameters.LevelPath, "main", "MissionGroup", "MT_backdrop", "items.level.json"),
                $"/levels/{parameters.LevelName}/art/shapes/MT_backdrop/");

            if (debugOutputPath != null)
            {
                // Debug artifacts are a diagnostic side-channel, not part of the level output — a
                // failure here (disk full, permissions, an image-encoding edge case) must never turn an
                // otherwise-complete generation into Success=false. Downgrade to a warning instead.
                debugStopwatch.Start();
                try
                {
                    WriteDebugArtifacts(debugOutputPath, farRaster, bandRasters, chunkStatLines,
                        chunkLeavesForDebug!, parameters);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"debug artifacts failed: {ex.Message}");
                }
                debugStopwatch.Stop();
            }
            result.DebugArtifactSeconds = debugStopwatch.Elapsed.TotalSeconds;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[BACKDROP] timing: rasters={result.RasterLoadSeconds:F1}s mesh={result.MeshSeconds:F1}s ({result.ExportedChunks.Count} chunks, worst={result.WorstChunkSeconds:F1}s {result.WorstChunkName ?? "-"}) debug={result.DebugArtifactSeconds:F1}s"));

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    ///     Cheap cost probe for the UI (spec §5): coarse far raster (≤512) + per-chunk refinement-only
    ///     quadtree walk (no vertex emission, no writes) plus an analytic edge-band contribution. Never
    ///     throws — returns a zeroed result on any failure, mirroring <see cref="Generate"/>'s philosophy.
    /// </summary>
    public BackdropEstimateResult Estimate(BackdropGenerationParameters parameters)
    {
        try
        {
            var validation = parameters.Validate();
            if (!validation.IsValid)
                return new BackdropEstimateResult();

            GeoTiffReader.InitializeGdal();

            var farRaster = BackdropRasterLoader.LoadWindow(parameters.SourceGeoTiffPath, parameters.BackdropRect,
                EstimateFarRasterDimension, out _);

            var mapper = new BackdropCoordinateMapper(parameters.TerrainRect, parameters.TerrainSizePixels,
                parameters.TerrainMetersPerPixel);
            var heightField = new BackdropHeightField(farRaster, [], parameters.TerrainHeightMap, mapper,
                parameters.TerrainSizePixels, parameters.TerrainMetersPerPixel, parameters.TerrainBaseHeight,
                parameters.TerrainCropMinElevation, parameters.EdgeBandMeters);

            var plan = BackdropChunkPlanner.Plan(parameters);

            // No EdgeBandImportanceSource here on purpose: forcing full lattice-unit subdivision inside
            // the band would make this "cheap" probe walk O(bandArea/u²) cells — the exact cost we are
            // trying to avoid. The band's contribution is added analytically below instead (spec §5).
            var mesherOptions = new BackdropMesherOptions
            {
                MaxVerticalErrorNearMeters = parameters.MaxVerticalErrorNearMeters,
                MaxVerticalErrorFarMeters = parameters.MaxVerticalErrorFarMeters,
                EdgeBandMeters = parameters.EdgeBandMeters,
                MaxMarginMeters = plan.MaxMarginMeters,
                SeamSkirt = false,
                LatticeUnitMeters = parameters.TerrainMetersPerPixel,
                HalfSizeMeters = mapper.HalfSizeMeters,
                ErrorProbeGridSize = 2
            };
            IReadOnlyList<IBackdropImportanceSource> noImportance = [];

            long triangles = 0;
            long textureBytes = 0;
            foreach (var chunk in plan.Chunks)
            {
                var mesher = new BackdropQuadtreeMesher(heightField, mesherOptions, noImportance);
                var leaves = mesher.RefineChunk(chunk);
                triangles += leaves.Count * 2L;
                textureBytes += (long)chunk.TextureSize * chunk.TextureSize * 4;
            }

            var bandArea = ComputeBandAreaMeters(parameters, mapper);
            var u = (double)parameters.TerrainMetersPerPixel;
            triangles += (long)Math.Round(bandArea / (u * u) * 2.0);

            return new BackdropEstimateResult
            {
                EstimatedTriangles = triangles,
                TextureMemoryBytes = textureBytes,
                ChunkCount = plan.Chunks.Count
            };
        }
        catch
        {
            return new BackdropEstimateResult();
        }
    }

    /// <summary>
    ///     Loads up to 4 native-resolution elevation strips around the terrain rect's edges — one per
    ///     side with margin &gt; 0 (spec §6 band raster). Each strip is inflated <c>bandPx</c> outward
    ///     (covers <see cref="BackdropGenerationParameters.EdgeBandMeters"/>) and 2 px inward so it
    ///     overlaps the terrain rect — never abuts with a gap (Task 4 review:
    ///     <see cref="BackdropRaster.ContainsSourcePoint"/> is inclusive at Right/Bottom, so any strip that
    ///     stopped exactly AT the terrain edge would leave the seam sample point resolving to the coarse
    ///     far raster instead, breaking the exact seam snap).
    ///
    ///     Sideways, the north/south strips extend the FULL <c>bandPxX</c> beyond the terrain rect's west
    ///     and east edges (not just 2 px): the band is Euclidean, so it reaches <c>EdgeBandMeters</c>
    ///     diagonally past every terrain CORNER too — a plus-shaped cross of 4 strips (each only 2 px
    ///     sideways) leaves the diagonal corner lobes uncovered, and a query there would fall through to
    ///     the coarse far raster while the height field still thinks it's inside the band (full weight on
    ///     the seam-consistent value) — a sharp visible step. Widening north/south sideways to bandPxX
    ///     makes their rectangular window a superset of the actual Euclidean quarter-disk corner lobe, so
    ///     every band point (edge or corner) resolves to a native-resolution strip. West/east strips can
    ///     stay at a 2 px vertical overlap since north/south now cover the corners; the strips may overlap
    ///     each other there — harmless, since <see cref="BackdropHeightField.SampleDemElevation"/> takes the
    ///     first containing raster and any two overlapping strips read the same underlying native pixels.
    ///     Strip load order (west, east, north, south) is fixed regardless of which sides are present, so
    ///     which strip "wins" an overlap is deterministic.
    /// </summary>
    internal static List<BackdropRaster> LoadBandStrips(BackdropGenerationParameters p,
        out long nodataCells, out long totalCells)
    {
        var strips = new List<BackdropRaster>();
        var runningNodataCells = 0L;
        var runningTotalCells = 0L;

        var mosaic = new PixelRect(0, 0, p.SourceRasterWidth, p.SourceRasterHeight);
        var t = p.TerrainRect;
        var b = p.BackdropRect;

        double west = (t.X - b.X) * p.MetersPerSourcePixelX;
        double east = (b.Right - t.Right) * p.MetersPerSourcePixelX;
        double north = (t.Y - b.Y) * p.MetersPerSourcePixelY;
        double south = (b.Bottom - t.Bottom) * p.MetersPerSourcePixelY;

        var bandPxX = (int)Math.Ceiling(p.EdgeBandMeters / p.MetersPerSourcePixelX) + 2;
        var bandPxY = (int)Math.Ceiling(p.EdgeBandMeters / p.MetersPerSourcePixelY) + 2;

        void TryLoad(double marginMeters, PixelRect raw)
        {
            if (marginMeters <= 0) return;                 // no ring on this side — nothing to load
            var clipped = ClipRect(ClipRect(raw, mosaic), b);
            if (clipped.IsEmpty) return;                   // never construct a degenerate raster

            var raster = BackdropRasterLoader.LoadWindow(p.SourceGeoTiffPath, clipped, null, out var pct);
            strips.Add(raster);
            var cells = (long)raster.Width * raster.Height;
            runningTotalCells += cells;
            runningNodataCells += (long)Math.Round(pct / 100.0 * cells);
        }

        TryLoad(west, new PixelRect(t.X - bandPxX, t.Y - 2, bandPxX + 2, t.Height + 4));
        TryLoad(east, new PixelRect(t.Right - 2, t.Y - 2, bandPxX + 2, t.Height + 4));
        TryLoad(north, new PixelRect(t.X - bandPxX, t.Y - bandPxY, t.Width + 2 * bandPxX, bandPxY + 2));
        TryLoad(south, new PixelRect(t.X - bandPxX, t.Bottom - 2, t.Width + 2 * bandPxX, bandPxY + 2));

        nodataCells = runningNodataCells;
        totalCells = runningTotalCells;
        return strips;
    }

    private static PixelRect ClipRect(PixelRect r, PixelRect bounds)
    {
        var x0 = Math.Max(r.X, bounds.X);
        var y0 = Math.Max(r.Y, bounds.Y);
        var x1 = Math.Min(r.Right, bounds.Right);
        var y1 = Math.Min(r.Bottom, bounds.Bottom);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>
    ///     A chunk is skipped entirely (spec §6) when every far-raster cell its source rect overlaps was
    ///     nodata BEFORE edge-extension filled it in. Uses the far raster's raw mask (returned only by the
    ///     internal loader overload) since the far raster always spans the whole backdrop rect.
    /// </summary>
    private static bool IsChunkFullyNodata(bool[] farNodataMask, BackdropRaster farRaster,
        BackdropChunkDefinition chunk)
    {
        if (farNodataMask.Length == 0) return false;

        var x0 = (int)Math.Floor((chunk.SourceRectX - farRaster.SourceWindow.X) / farRaster.SourcePixelsPerCellX);
        var y0 = (int)Math.Floor((chunk.SourceRectY - farRaster.SourceWindow.Y) / farRaster.SourcePixelsPerCellY);
        var x1 = (int)Math.Ceiling((chunk.SourceRectX + chunk.SourceRectWidth - farRaster.SourceWindow.X) /
                                    farRaster.SourcePixelsPerCellX);
        var y1 = (int)Math.Ceiling((chunk.SourceRectY + chunk.SourceRectHeight - farRaster.SourceWindow.Y) /
                                    farRaster.SourcePixelsPerCellY);

        x0 = Math.Clamp(x0, 0, farRaster.Width - 1);
        y0 = Math.Clamp(y0, 0, farRaster.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, farRaster.Width);
        y1 = Math.Clamp(y1, y0 + 1, farRaster.Height);

        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
            if (!farNodataMask[y * farRaster.Width + x])
                return false;
        return true;
    }

    /// <summary>
    ///     Analytic area of the ring band (the part of the backdrop ring within <see cref="BackdropGenerationParameters.EdgeBandMeters"/>
    ///     of the terrain rect), clipped to whatever margin actually exists per side (spec §5 estimate).
    ///     Treats the band as a rectangular frame (square corners) rather than the true Euclidean
    ///     rounded-corner shape the mesher/height-field use — a deliberate over-estimate, acceptable for a
    ///     cost probe that must never under-promise GPU cost.
    /// </summary>
    private static double ComputeBandAreaMeters(BackdropGenerationParameters p, BackdropCoordinateMapper mapper)
    {
        var side = 2 * mapper.HalfSizeMeters;

        double west = (p.TerrainRect.X - p.BackdropRect.X) * p.MetersPerSourcePixelX;
        double east = (p.BackdropRect.Right - p.TerrainRect.Right) * p.MetersPerSourcePixelX;
        double north = (p.TerrainRect.Y - p.BackdropRect.Y) * p.MetersPerSourcePixelY;
        double south = (p.BackdropRect.Bottom - p.TerrainRect.Bottom) * p.MetersPerSourcePixelY;

        double BandWidth(double margin) => margin > 0 ? Math.Min(margin, p.EdgeBandMeters) : 0.0;
        var bw = BandWidth(west);
        var be = BandWidth(east);
        var bn = BandWidth(north);
        var bs = BandWidth(south);

        var outerWidth = side + bw + be;
        var outerHeight = side + bn + bs;
        return Math.Max(0.0, outerWidth * outerHeight - side * side);
    }

    private static void WriteDebugArtifacts(string debugOutputPath, BackdropRaster farRaster,
        IReadOnlyList<BackdropRaster> bandRasters, IReadOnlyList<string> chunkStatLines,
        IReadOnlyList<(BackdropChunkDefinition Chunk, IReadOnlyList<LeafCell> Leaves)> chunkLeaves,
        BackdropGenerationParameters parameters)
    {
        Directory.CreateDirectory(debugOutputPath);

        WriteNormalizedRasterPng(farRaster, Path.Combine(debugOutputPath, "far_raster.png"));

        // V1 simplification: up to 4 band strips may exist (one per side); only the first loaded one is
        // dumped as "band_raster.png" — enough to sanity-check the band data without one file per side.
        if (bandRasters.Count > 0)
            WriteNormalizedRasterPng(bandRasters[0], Path.Combine(debugOutputPath, "band_raster.png"));

        File.WriteAllLines(Path.Combine(debugOutputPath, "chunk_stats.txt"), chunkStatLines);

        WriteQuadtreeLevelsPng(farRaster, chunkLeaves, parameters, Path.Combine(debugOutputPath, "quadtree_levels.png"));
    }

    /// <summary>Min–max normalized L16 PNG of a raster's elevations, reconstructed purely from the raster's
    /// public bilinear-sampling API (grid-center samples reproduce the stored values exactly).
    /// Two passes over the SAMPLES (not a materialized double[Width×Height] buffer, which would be
    /// 537 MB for an 8192² far raster at defaults — reachable, not hypothetical): the first finds
    /// min/max, the second streams the normalized value straight into the image, one pixel at a time.</summary>
    private static void WriteNormalizedRasterPng(BackdropRaster raster, string path)
    {
        double SampleCellCenter(int x, int y)
        {
            var srcX = raster.SourceWindow.X + (x + 0.5) * raster.SourcePixelsPerCellX;
            var srcY = raster.SourceWindow.Y + (y + 0.5) * raster.SourcePixelsPerCellY;
            return raster.SampleBilinearAtSource(srcX, srcY);
        }

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = 0; y < raster.Height; y++)
        for (var x = 0; x < raster.Width; x++)
        {
            var v = SampleCellCenter(x, y);
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        if (range < 1e-6) range = 1.0;

        using var image = new Image<L16>(raster.Width, raster.Height);
        for (var y = 0; y < raster.Height; y++)
        for (var x = 0; x < raster.Width; x++)
        {
            var normalized = (SampleCellCenter(x, y) - min) / range;
            image[x, y] = new L16((ushort)Math.Clamp(normalized * 65535.0, 0, 65535));
        }

        image.Save(path);
    }

    /// <summary>Debug heat map at far-raster resolution: gray level ∝ leaf cell size (brighter = coarser),
    /// so quadtree refinement can be eyeballed against the source imagery.</summary>
    private static void WriteQuadtreeLevelsPng(BackdropRaster farRaster,
        IReadOnlyList<(BackdropChunkDefinition Chunk, IReadOnlyList<LeafCell> Leaves)> chunkLeaves,
        BackdropGenerationParameters parameters, string path)
    {
        using var image = new Image<L16>(farRaster.Width, farRaster.Height, new L16(0));
        var u = (double)parameters.TerrainMetersPerPixel;
        var mapper = new BackdropCoordinateMapper(parameters.TerrainRect, parameters.TerrainSizePixels,
            parameters.TerrainMetersPerPixel);

        var maxSizeMeters = 1.0;
        foreach (var (_, leaves) in chunkLeaves)
        foreach (var leaf in leaves)
            maxSizeMeters = Math.Max(maxSizeMeters, Math.Max(leaf.Width, leaf.Height) * u);

        foreach (var (_, leaves) in chunkLeaves)
        foreach (var leaf in leaves)
        {
            var sizeMeters = Math.Max(leaf.Width, leaf.Height) * u;
            var gray = (ushort)Math.Clamp(sizeMeters / maxSizeMeters * 65535.0, 0, 65535);

            var half = mapper.HalfSizeMeters;
            var worldMinX = leaf.X * u - half;
            var worldMinY = leaf.Y * u - half;
            var worldMaxX = (leaf.X + leaf.Width) * u - half;
            var worldMaxY = (leaf.Y + leaf.Height) * u - half;

            var c1 = mapper.WorldToSourcePixel(worldMinX, worldMinY);
            var c2 = mapper.WorldToSourcePixel(worldMaxX, worldMaxY);
            var srcX0 = Math.Min(c1.SrcX, c2.SrcX);
            var srcX1 = Math.Max(c1.SrcX, c2.SrcX);
            var srcY0 = Math.Min(c1.SrcY, c2.SrcY);
            var srcY1 = Math.Max(c1.SrcY, c2.SrcY);

            var px0 = Math.Clamp((int)Math.Floor((srcX0 - farRaster.SourceWindow.X) / farRaster.SourcePixelsPerCellX),
                0, farRaster.Width);
            var px1 = Math.Clamp((int)Math.Ceiling((srcX1 - farRaster.SourceWindow.X) / farRaster.SourcePixelsPerCellX),
                0, farRaster.Width);
            var py0 = Math.Clamp((int)Math.Floor((srcY0 - farRaster.SourceWindow.Y) / farRaster.SourcePixelsPerCellY),
                0, farRaster.Height);
            var py1 = Math.Clamp((int)Math.Ceiling((srcY1 - farRaster.SourceWindow.Y) / farRaster.SourcePixelsPerCellY),
                0, farRaster.Height);

            for (var y = py0; y < py1; y++)
            for (var x = px0; x < px1; x++)
                image[x, y] = new L16(gray);
        }

        image.Save(path);
    }
}
