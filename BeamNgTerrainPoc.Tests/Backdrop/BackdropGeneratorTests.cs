using BeamNgTerrainPoc.Terrain.Backdrop;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backdrop_gen_" + Guid.NewGuid().ToString("N"));
    private string LevelPath => Path.Combine(_dir, "levels", "test_level");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private BackdropGenerationParameters CreateParameters()
    {
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(LevelPath);
        var tiffPath = Path.Combine(_dir, "dem.tif");
        using (var driver = Gdal.GetDriverByName("GTiff"))
        using (var ds = driver.Create(tiffPath, 128, 128, 1, DataType.GDT_Float32, null))
        {
            ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
            var data = new float[128 * 128];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
                data[y * 128 + x] = 400f + 3f * MathF.Sin(x / 5f) * MathF.Cos(y / 7f);
            ds.GetRasterBand(1).WriteRaster(0, 0, 128, 128, data, 128, 128, 0, 0);
            ds.FlushCache();
        }
        return new BackdropGenerationParameters
        {
            TerrainHeightMap = new float[32, 32],
            TerrainSizePixels = 32, TerrainMetersPerPixel = 2.0f,
            TerrainBaseHeight = 0f, TerrainCropMinElevation = 400.0,
            SourceGeoTiffPath = tiffPath,
            SourceRasterWidth = 128, SourceRasterHeight = 128,
            SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
            ProjectionWkt = null,
            SourceWgs84Bounds = new BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox(7.0, 50.0, 7.2, 50.2),
            TerrainRect = new PixelRect(48, 48, 32, 32),
            BackdropRect = new PixelRect(16, 16, 96, 96),
            LevelPath = LevelPath, LevelName = "test_level",
            EdgeBandMeters = 8, ChunkTargetMeters = 40
        };
    }

    [Fact]
    public void Generate_EndToEnd_WritesAllArtifacts()
    {
        var result = new BackdropGenerator().Generate(CreateParameters());
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.ExportedChunks);
        Assert.True(Directory.EnumerateFiles(
            Path.Combine(LevelPath, "art", "shapes", "MT_backdrop"), "*.dae").Any());
        Assert.True(File.Exists(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop", "main.materials.json")));
        Assert.True(File.Exists(Path.Combine(LevelPath, "main", "MissionGroup", "MT_backdrop", "items.level.json")));
        Assert.True(File.Exists(Path.Combine(LevelPath, "main", "MissionGroup", "items.level.json")));
        Assert.True(result.TotalTriangles > 0);
    }

    [Fact]
    public void Generate_IsDeterministic_ByteIdenticalDaes()
    {
        var generator = new BackdropGenerator();
        var p = CreateParameters();
        Assert.True(generator.Generate(p).Success);
        var dae = Directory.EnumerateFiles(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop"), "*.dae").First();
        var first = File.ReadAllText(dae);
        Assert.True(generator.Generate(p).Success);   // clean-and-rewrite, then regenerate
        var second = File.ReadAllText(dae);
        // persistentIds live in JSON files, not DAEs — DAEs must be byte-identical (the bridge
        // pipeline already relies on ColladaExporter determinism for its byte-identical baselines).
        // CONFIRMED finding: ColladaExporter.BuildAssetElement() stamps <created>/<modified> with
        // DateTime.UtcNow.ToString("O") on every export (BeamNG.Procedural3D/Exporters/ColladaExporter.cs:275-276) —
        // this IS the exporter's only nondeterminism, verified by running this test and observing the
        // raw-byte diff land exactly inside those two timestamp strings (nothing else differed).
        // Per the task brief's own instruction for this exact scenario: strip those two lines and keep
        // comparing the rest of the geometry/material byte-for-byte — do not weaken the comparison further.
        var strip = new System.Text.RegularExpressions.Regex(@"<created>.*?</created>|<modified>.*?</modified>");
        Assert.Equal(strip.Replace(first, ""), strip.Replace(second, ""));
    }

    [Fact]
    public void Generate_InvalidParameters_FailsWithMessage_WritesNothing()
    {
        var p = CreateParameters() with { BackdropRect = new PixelRect(60, 60, 10, 10) };
        var result = new BackdropGenerator().Generate(p);
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop")));
    }

    [Fact]
    public void Generate_WritesDebugArtifacts_WhenPathGiven()
    {
        var debug = Path.Combine(_dir, "MT_TerrainGeneration", "backdrop");
        var result = new BackdropGenerator().Generate(CreateParameters(), debug);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(debug, "far_raster.png")));
        Assert.True(File.Exists(Path.Combine(debug, "chunk_stats.txt")));
    }

    [Fact]
    public void Estimate_ReturnsPlausibleNumbers_WithoutWriting()
    {
        var estimate = new BackdropGenerator().Estimate(CreateParameters());
        Assert.True(estimate.EstimatedTriangles > 0);
        Assert.True(estimate.TextureMemoryBytes > 0);
        Assert.True(estimate.ChunkCount > 0);
        Assert.False(Directory.Exists(Path.Combine(LevelPath, "art")));
    }

    [Fact]
    public void LoadBandStrips_CoverEuclideanCornerLobes_NotJustEdgeMidpoints()
    {
        // FINDING 1 regression pin: the band is Euclidean, so it reaches EdgeBandMeters diagonally past
        // every terrain CORNER, not just straight out from the edges. A plus-shaped cross of 4 strips
        // (each only 2 px sideways, the pre-fix shape) covers the edge midpoints fine but leaves the
        // diagonal corner lobes uncovered — a query there would fall through to the coarse far raster.
        var p = CreateParameters();
        var strips = BackdropGenerator.LoadBandStrips(p, out _, out _);

        var t = p.TerrainRect;
        var bandPxX = (int)Math.Ceiling(p.EdgeBandMeters / p.MetersPerSourcePixelX);
        var bandPxY = (int)Math.Ceiling(p.EdgeBandMeters / p.MetersPerSourcePixelY);

        bool CoveredBySomeStrip(double x, double y) => strips.Any(s => s.ContainsSourcePoint(x, y));

        // Edge midpoints — already covered before the fix, sanity check they still are.
        Assert.True(CoveredBySomeStrip(t.X - 1, (t.Y + t.Bottom) / 2.0));           // west edge
        Assert.True(CoveredBySomeStrip(t.Right + 1, (t.Y + t.Bottom) / 2.0));       // east edge
        Assert.True(CoveredBySomeStrip((t.X + t.Right) / 2.0, t.Y - 1));            // north edge
        Assert.True(CoveredBySomeStrip((t.X + t.Right) / 2.0, t.Bottom + 1));       // south edge

        // Diagonal corner-lobe points: within band reach of the corner, but past the OLD plus-shape's
        // 2 px sideways reach (bandPxX/Y here is 4 at these test defaults, so 3 px sideways is well past
        // the old 2 px limit and still within the band).
        var diag = Math.Max(3, Math.Min(bandPxX, bandPxY) - 1);
        Assert.True(CoveredBySomeStrip(t.X - diag, t.Y - diag), "NW corner lobe uncovered");
        Assert.True(CoveredBySomeStrip(t.Right + diag, t.Y - diag), "NE corner lobe uncovered");
        Assert.True(CoveredBySomeStrip(t.X - diag, t.Bottom + diag), "SW corner lobe uncovered");
        Assert.True(CoveredBySomeStrip(t.Right + diag, t.Bottom + diag), "SE corner lobe uncovered");
    }
}
