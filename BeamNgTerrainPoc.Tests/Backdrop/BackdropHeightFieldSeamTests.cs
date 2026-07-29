using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropHeightFieldSeamTests
{
    private const int Size = 16;          // terrain 16 px
    private const float U = 2.0f;         // 2 m/px → span 32 m, half = 16
    private const float BaseHeight = 50f;
    private const double CropMin = 400.0;
    private const double Band = 8.0;

    /// <summary>Terrain rect at source (100,100,16,16); backdrop raster covers (84,84,48,48).</summary>
    private static BackdropHeightField Build(
        Func<int, int, float> terrainHeight,      // (x, y[south-up]) → pre-base-height meters
        Func<double, double, double> demElevation) // (srcX, srcY) → absolute DEM meters
    {
        var terrain = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            terrain[y, x] = terrainHeight(x, y);

        var window = new PixelRect(84, 84, 48, 48);
        var far = new float[48 * 48];
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 48; x++)
            far[y * 48 + x] = (float)demElevation(window.X + x + 0.5, window.Y + y + 0.5);

        var mapper = new BackdropCoordinateMapper(new PixelRect(100, 100, Size, Size), Size, U);
        return new BackdropHeightField(
            new BackdropRaster(far, 48, 48, window),
            bandRasters: [],
            terrain, mapper, Size, U, BaseHeight, CropMin, Band);
    }

    [Fact]
    public void SeamVertices_TakeExactTerrainEdgeHeights()
    {
        // Terrain = tilted plane h(x,y) = 3 + 0.5x; DEM constant 420 (deliberately mismatched).
        var field = Build((x, y) => 3f + 0.5f * x, (_, _) => 420.0);

        // East seam at worldX = +16: terrain edge column x = Size−1 → h = 3 + 0.5*15 = 10.5.
        for (var iy = 0; iy <= Size; iy++)
        {
            var worldY = iy * U - 16.0;
            var z = field.SampleWorldZ(16.0, worldY);
            Assert.Equal(10.5 + BaseHeight, z, 9);   // EXACT snap, §7.1
        }
        // West seam at worldX = −16: column x = 0 → h = 3.
        Assert.Equal(3.0 + BaseHeight, field.SampleWorldZ(-16.0, 0.0), 9);
    }

    [Fact]
    public void BeyondBand_IsPureDemWithDatumFormula()
    {
        var field = Build((_, _) => 0f, (_, _) => 470.0);
        // d = 10 > band 8 east of the seam.
        var z = field.SampleWorldZ(16.0 + 10.0, 0.0);
        Assert.Equal(470.0 - CropMin + BaseHeight, z, 9);   // §7.3: dem − cropMin + base
    }

    [Fact]
    public void BandBlend_FadesDeltaMonotonically()
    {
        // Terrain edge z = 12 + base; DEM z = 470 − 400 + 50 = 120 → delta = (12+50) − 120 = −58.
        var field = Build((_, _) => 12f, (_, _) => 470.0);
        var demZ = 470.0 - CropMin + BaseHeight;
        double? previousError = null;
        for (var i = 0; i <= 8; i++)
        {
            var d = Band * i / 8.0;
            var z = field.SampleWorldZ(16.0 + d, 0.0);
            var error = Math.Abs(z - demZ);           // remaining influence of the terrain delta
            if (previousError.HasValue)
                Assert.True(error <= previousError.Value + 1e-9,
                    $"|z−dem| must not increase across the band (d={d}: {error} > {previousError})");
            previousError = error;
        }
        // Ends: at d=0 exact terrain edge; at d=band exact dem.
        Assert.Equal(12.0 + BaseHeight, field.SampleWorldZ(16.0, 0.0), 9);
        Assert.Equal(demZ, field.SampleWorldZ(16.0 + Band, 0.0), 9);

        // Pins the SIGN of the delta blend (not just its magnitude): at d=Band/4, t=0.25,
        // smoothstep(t)=0.15625, w=0.84375 → z = demZ + delta·w = 120 + (−58)·0.84375 = 71.0625.
        // A sign-flipped blend (demZ − delta·w) would give 168.9375 instead; a linear (non-smoothstep)
        // fade would give 76.5 instead — both are far outside this tolerance.
        Assert.Equal(71.0625, field.SampleWorldZ(16.0 + Band / 4.0, 0.0), 9);

        // Continuity just past the seam: the full terrain delta must still apply (w ≈ 1).
        Assert.Equal(62.0, field.SampleWorldZ(16.0 + 1e-6, 0.0), 3);
    }

    [Fact]
    public void MatchingDemAndTerrain_ProducesNoBandDistortion()
    {
        // DEM plane in absolute meters whose normalized form equals the terrain heights:
        // dem(src) = CropMin + 0.5 * (srcX − 100) * (terrain has h = 0.5 * x, u == 1 src px per terrain px … careful:
        // terrain px x ↔ srcX = 100 + x, so dem − cropMin at terrain sample = 0.5x = h. Datum formula ⇒ zero delta.
        var field = Build((x, _) => 0.5f * x,
            (srcX, _) => CropMin + 0.5 * (srcX - 100.0) * 1.0);
        // Inside the band the blend must be a no-op within bilinear tolerance.
        // NOTE the terrain edge column sits at srcX=115 (sample x=15) while the seam is at srcX=116 —
        // the DEM keeps rising on that last half-cell, terrain edge is clamped flat, so compare with
        // the DEM value, allowing the documented last-half-cell tolerance of 0.5*u*slope.
        var z = field.SampleWorldZ(16.0 + Band / 2.0, 0.0);
        var demZ = field.SampleDemElevation(16.0 + Band / 2.0, 0.0) - CropMin + BaseHeight;
        Assert.True(Math.Abs(z - demZ) <= 0.5 * U * 0.5 + 1e-6,
            $"delta blend should be ≈ no-op when DEM matches terrain (|{z} − {demZ}|)");
    }

    [Fact]
    public void BandRaster_PreferredOverFarRaster()
    {
        var terrain = new float[Size, Size];
        var mapper = new BackdropCoordinateMapper(new PixelRect(100, 100, Size, Size), Size, U);
        var farWindow = new PixelRect(84, 84, 48, 48);
        var far = Enumerable.Repeat(500f, 48 * 48).ToArray();
        // Band strip east of the terrain: src (116, 96, 8, 24) with different value.
        var stripWindow = new PixelRect(116, 96, 8, 24);
        var strip = Enumerable.Repeat(600f, 8 * 24).ToArray();

        var field = new BackdropHeightField(
            new BackdropRaster(far, 48, 48, farWindow),
            [new BackdropRaster(strip, 8, 24, stripWindow)],
            terrain, mapper, Size, U, BaseHeight, CropMin, Band);

        Assert.Equal(600.0, field.SampleDemElevation(16.0 + 4.0, 0.0), 6);   // inside strip
        Assert.Equal(500.0, field.SampleDemElevation(-16.0 - 4.0, 0.0), 6);  // west side → far raster
    }

    [Fact]
    public void TerrainEdge_UsesSouthUpRowIndexing()
    {
        // Terrain height increases with row index y (0 = south, Size−1 = north); DEM value is
        // irrelevant here because seam points snap exactly (d ≤ 0) and never sample the DEM.
        var field = Build((_, y) => y, (_, _) => CropMin);

        // South edge (worldY = −half) must read row 0 → height 0. A flipped row index
        // (py = (half − qy)/u) would instead read row Size−1 → 15.
        Assert.Equal(0.0 + BaseHeight, field.SampleWorldZ(0.0, -16.0), 9);
        // North edge (worldY = +half) must read row Size−1 → height 15 (clamped, not row 0).
        Assert.Equal(15.0 + BaseHeight, field.SampleWorldZ(0.0, 16.0), 9);
    }

    [Fact]
    public void SignedDistance_EuclideanOutside_NegativeInside()
    {
        var field = Build((_, _) => 0f, (_, _) => CropMin);
        Assert.Equal(5.0, field.SignedDistanceToTerrainRect(21.0, 0.0), 9);
        Assert.Equal(Math.Sqrt(50), field.SignedDistanceToTerrainRect(21.0, 21.0), 9); // corner: √(5²+5²)
        Assert.True(field.SignedDistanceToTerrainRect(0.0, 0.0) < 0);
        Assert.Equal(0.0, field.SignedDistanceToTerrainRect(16.0, 8.0), 9);
    }
}
