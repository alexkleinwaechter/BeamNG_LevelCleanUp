using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Processing;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tunnel plan Phase 4: portal hole cutting. Synthetic mountain over a straight span — deep
///     mid-span cells keep intact terrain (drive under the mountain), portal-wall cells and
///     tube-clipping cells become holes (byte 255), lateral margin respected, foreign painted
///     surfaces protected, flag off ⇒ grid untouched.
/// </summary>
public class TunnelPortalHoleProviderTests
{
    private const int Size = 512;
    private const int SplineId = 1;
    private const float FloorZ = 20f;

    // Span [100,300] along X at y=150. Rules: interior 5, wall 0.6, clearance 1, margin 1,
    // portal hole length 8 ⇒ roofOuter = 25.6, corridor half-width = 4+1+0.6+1 = 6.6.
    private static UnifiedRoadNetwork BuildNetwork(bool holesOn = true)
    {
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 31337L },
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            SplineId, new Vector2(0, 150), new Vector2(400, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        var rules = holesOn
            ? TunnelRuleSystemOptions.CreateWithAllRulesEnabled()
            : new TunnelRuleSystemOptions();
        spline.Parameters.TunnelRules = rules;

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);

        var stations = new List<BridgeStation>();
        for (var d = 100f; d <= 300f; d += 5f)
        {
            stations.Add(new BridgeStation
            {
                Center = new Vector2(d, 150f),
                Normal = new Vector2(0f, -1f),
                Tangent = new Vector2(1f, 0f),
                Width = 8f,
                CenterZ = FloorZ, LeftEdgeZ = FloorZ, RightEdgeZ = FloorZ,
                DistanceAlongSpline = d,
            });
        }

        network.TunnelSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = SplineId, SpanId = seg.SpanId, OsmWayIds = { 31337L }, Stations = stations,
        });

        return network;
    }

    /// <summary>
    ///     Mountain: terrain 20 (floor level) outside, rising over the span to 60 at mid-span.
    ///     Between x=100 and x=140 terrain climbs 20→60 (passes through the tube 20→25.6);
    ///     deep section x∈[140,260] terrain 60 ≫ roof; symmetric on the exit side.
    /// </summary>
    private static float[,] BuildMountain()
    {
        var hm = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            float z;
            if (x < 100 || x > 300) z = FloorZ;
            else if (x < 140) z = FloorZ + (x - 100) * 1f;   // climb 20 → 60
            else if (x <= 260) z = 60f;                       // deep cover
            else z = FloorZ + (300 - x) * 1f;                 // descend 60 → 20
            hm[y, x] = z;
        }

        return hm;
    }

    private static byte[] FreshGrid()
    {
        var grid = new byte[Size * Size];
        Array.Fill(grid, (byte)2);
        return grid;
    }

    [Fact]
    public void ClipAndPortalCells_Holed_DeepCoverKept()
    {
        var network = BuildNetwork();
        var grid = FreshGrid();

        var result = TunnelPortalHoleProvider.CutPortalHoles(
            network, BuildMountain(), grid, Size, 1f, log: false);

        Assert.True(result.CellsStamped > 0);

        bool IsHole(int x, int y) => grid[y * Size + x] == TerrainHoleCutter.HoleMaterialIndex;

        // Portal mouth (station 102, within the 8 m portal zone; terrain 22 > floor 20): holed.
        Assert.True(IsHole(102, 150), "portal-wall cell must be holed");
        // Clip zone (station 120, terrain 40? no — climb reaches 40 at x=120 which is above roof 25.6).
        // At x=104, terrain 24 is inside the tube (20..25.6): holed by the clip rule.
        Assert.True(IsHole(104, 150), "tube-clipping cell must be holed");
        // Deep cover mid-span (x=200, terrain 60 ≫ roof 25.6): intact mountain.
        Assert.False(IsHole(200, 150), "deep-cover cell must keep intact terrain");
        // Outside the corridor laterally (offset 10 > half-width 6.6 + 1 dilation): untouched.
        Assert.False(IsHole(102, 160), "cell beyond the lateral margin must stay");
        // Before the span start (x=95): untouched (terrain is at floor level there anyway).
        Assert.False(IsHole(95, 150), "cell before the portal must stay");
    }

    [Fact]
    public void LateralMargin_Respected_WithDilation()
    {
        var network = BuildNetwork();
        var grid = FreshGrid();

        TunnelPortalHoleProvider.CutPortalHoles(network, BuildMountain(), grid, Size, 1f, log: false);

        // Corridor half-width 6.6 ⇒ cells to |dy| ≈ 6 stamped, +1 dilation ⇒ ≤ ~8. dy=9 must survive.
        bool IsHole(int x, int y) => grid[y * Size + x] == TerrainHoleCutter.HoleMaterialIndex;
        Assert.True(IsHole(102, 155));
        Assert.False(IsHole(102, 159));
    }

    [Fact]
    public void FlagOff_GridUntouched()
    {
        var network = BuildNetwork(holesOn: false);
        var grid = FreshGrid();
        var before = (byte[])grid.Clone();

        var result = TunnelPortalHoleProvider.CutPortalHoles(
            network, BuildMountain(), grid, Size, 1f, log: false);

        Assert.Equal(0, result.CellsStamped);
        Assert.Equal(before, grid);
    }

    [Fact]
    public void ForeignRoadSurface_NeverHoled()
    {
        var network = BuildNetwork();
        var grid = FreshGrid();

        // A foreign road (spline 99) owns a strip crossing the portal zone.
        var owner = new int[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            owner[y, x] = RoadSurfaceOwnerRaster.NoOwner;
        for (var y = 140; y <= 160; y++)
        for (var x = 100; x <= 110; x++)
            owner[y, x] = 99;

        TunnelPortalHoleProvider.CutPortalHoles(
            network, BuildMountain(), grid, Size, 1f, log: false, roadSurfaceOwner: owner);

        bool IsHole(int x, int y) => grid[y * Size + x] == TerrainHoleCutter.HoleMaterialIndex;
        Assert.False(IsHole(102, 150), "foreign-owned cell must never be holed");
        Assert.False(IsHole(105, 152), "foreign-owned cell must never be holed");
    }

    /// <summary>
    ///     tunneljena render 2026-07-18: the clip zone must track the shell SILHOUETTE — cells
    ///     laterally beside the shell (old corridor-wide window) or above the wall shoulders opened
    ///     void the mesh could never mask. Interior half = 4+1 = 5; wall top = 3, arch apex outer =
    ///     25.6, shoulder outer window at |dy|=5 is 23.6.
    /// </summary>
    [Fact]
    public void ClipZone_TracksShellSilhouette_NoVoidBesideOrAboveShell()
    {
        var network = BuildNetwork();
        var grid = FreshGrid();
        // Flat terrain 24.5: inside the arch window at the center (< 25.6), OUTSIDE it at the
        // shoulders (> 23.6 at |dy| = 5).
        var hm = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            hm[y, x] = 24.5f;

        TunnelPortalHoleProvider.CutPortalHoles(network, hm, grid, Size, 1f, log: false);

        bool IsHole(int x, int y) => grid[y * Size + x] == TerrainHoleCutter.HoleMaterialIndex;

        // Mid-span (x=200, far from both portals):
        Assert.True(IsHole(200, 150), "terrain through the arch center must be holed");
        Assert.False(IsHole(200, 155), "shoulder cell above the wall window must keep terrain");
        Assert.False(IsHole(200, 157), "cell laterally beside the shell must keep terrain");
        // Clip cells are NOT dilated — the hole edge hugs the silhouette exactly.
        Assert.False(IsHole(200, 156));
    }

    /// <summary>
    ///     Banking follow-up (doc 03): the clip window follows the SHEARED floor line
    ///     (floorAtOffset = floorZ + offset·slope, slope from the stations' banked edge Zs). With
    ///     edge Zs 20 ∓ 0.4 (slope 0.1, normal (0,−1) ⇒ y &gt; 150 is the low left side) and flat
    ///     terrain exactly at the center floor Z, the same |offset| is holed on the low side
    ///     (terrain inside the tilted window) and solid on the high side (terrain below it).
    /// </summary>
    [Fact]
    public void BankedSpan_HoleFollowsShearedFloor_LowSideHoled_HighSideSolid()
    {
        var network = BuildNetwork();
        var span = Assert.Single(network.TunnelSpans);
        network.TunnelSpans.Clear();
        network.TunnelSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = span.SplineId, SpanId = span.SpanId, OsmWayIds = { 31337L },
            Stations = span.Stations.Select(s => new BridgeStation
            {
                Center = s.Center, Normal = s.Normal, Tangent = s.Tangent, Width = s.Width,
                CenterZ = s.CenterZ,
                LeftEdgeZ = s.CenterZ - 0.4f,  // left = −normal = +Y side, banked LOW
                RightEdgeZ = s.CenterZ + 0.4f, // right = −Y side, banked HIGH
                DistanceAlongSpline = s.DistanceAlongSpline,
            }).ToList(),
        });

        var grid = FreshGrid();
        var hm = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            hm[y, x] = FloorZ; // exactly the center floor line

        TunnelPortalHoleProvider.CutPortalHoles(network, hm, grid, Size, 1f, log: false);

        bool IsHole(int x, int y) => grid[y * Size + x] == TerrainHoleCutter.HoleMaterialIndex;

        // Mid-span (x=200, outside both portal zones), |offset| = 4 within the interior half (5):
        // low side floorAt = 19.6 ⇒ terrain 20 inside the window ⇒ holed; high side floorAt = 20.4
        // ⇒ terrain 20 below the window ⇒ intact.
        Assert.True(IsHole(200, 154), "low-side cell must be holed (terrain above the sheared floor)");
        Assert.False(IsHole(200, 146), "high-side cell must keep terrain (below the sheared floor)");
    }

    [Fact]
    public void NoTunnelSpans_NoOp()
    {
        var network = new UnifiedRoadNetwork();
        var grid = FreshGrid();

        var result = TunnelPortalHoleProvider.CutPortalHoles(
            network, BuildMountain(), grid, Size, 1f, log: false);

        Assert.Equal(0, result.CellsStamped);
    }
}
