using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     The post-blend bridge terrain passes (approach-raise fill, lower-road dips, abutment overlap tongue,
///     deck excavation) sculpt the heightmap to the bridge deck profile. Without a guard they overwrite a
///     NEIGHBOURING road's protected surface at an abutment. <see cref="RoadSurfaceOwnerRaster"/> records,
///     per heightmap cell, which spline's PAINTED surface (SurfaceWidth) owns it — so each pass can skip
///     cells owned by a DIFFERENT spline than the one it is shaping.
/// </summary>
public class RoadSurfaceOwnerRasterTests
{
    [Fact]
    public void Build_StampsPaintedSurface_WithOwnerSplineId_TerrainStaysUnowned()
    {
        // Horizontal spline 7 at y=150, painted SurfaceWidth 8 (half 4). Cells within ~±4.5 of the
        // centerline are owned by spline 7; terrain well off the surface stays NoOwner.
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(7, new(50, 150), new(250, 150), roadWidth: 8f);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);

        var owner = RoadSurfaceOwnerRaster.Build(network, 512, 512, 1f, marginMeters: 0.5f);

        Assert.Equal(7, owner[150, 150]);                          // centerline (station 100)
        Assert.Equal(7, owner[153, 150]);                          // offset 3 — inside painted half-width
        Assert.Equal(RoadSurfaceOwnerRaster.NoOwner, owner[160, 150]); // offset 10 — bare terrain
    }

    [Fact]
    public void Build_ExcludedSections_NotStamped()
    {
        // Deck (excluded) sections are not terrain road surface — they must not appear as owned cells, so
        // the bridge's own deck excavator/tongue stay free to shape the deck footprint.
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(50, 150), new(250, 150), roadWidth: 8f);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        foreach (var cs in network.GetCrossSectionsForSpline(3))
            cs.IsExcluded = true;

        var owner = RoadSurfaceOwnerRaster.Build(network, 512, 512, 1f, marginMeters: 0.5f);

        Assert.Equal(RoadSurfaceOwnerRaster.NoOwner, owner[150, 150]);
    }

    [Fact]
    public void Build_OverlappingSurfaces_HigherPriorityOwns_RegardlessOfOrder()
    {
        // Winningen render 2026-07-02 #2: a sparse span's non-excluded tongue zone lies OVER the crossing
        // road, and first-writer-wins let the lower-priority structure steal the through-road's lane cells
        // (the abutment tongue then stamped a deck-level patch across the underpass road). The overlap
        // must belong to the HIGHER-priority surface no matter which spline is rasterized first.
        var minor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 50), new(150, 250), roadWidth: 8f, priority: 3000);
        var major = RoadNetworkTestHelpers.CreateParameterizedSpline(
            9, new(50, 150), new(250, 150), roadWidth: 8f, priority: 8001);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, minor); // minor stamped FIRST
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, major);

        var owner = RoadSurfaceOwnerRaster.Build(network, 512, 512, 1f, marginMeters: 0.5f);

        Assert.Equal(9, owner[150, 150]); // the crossing cell belongs to the higher-priority road
        Assert.Equal(2, owner[100, 150]); // the minor road keeps its own exclusive cells
        Assert.Equal(9, owner[150, 100]);
    }

    [Fact]
    public void CanWrite_NullRaster_AlwaysTrue_LegacyByteIdentical()
    {
        Assert.True(RoadSurfaceOwnerRaster.CanWrite(null, 10, 10, 1));
    }

    [Fact]
    public void CanWrite_SelfAndTerrain_True_OtherSpline_False()
    {
        var owner = new int[4, 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            owner[y, x] = RoadSurfaceOwnerRaster.NoOwner;
        owner[1, 1] = 5;  // owned by spline 5
        owner[2, 2] = 9;  // owned by spline 9

        Assert.True(RoadSurfaceOwnerRaster.CanWrite(owner, 0, 0, 5));  // bare terrain
        Assert.True(RoadSurfaceOwnerRaster.CanWrite(owner, 1, 1, 5));  // owned by self
        Assert.False(RoadSurfaceOwnerRaster.CanWrite(owner, 2, 2, 5)); // owned by another spline
    }
}
