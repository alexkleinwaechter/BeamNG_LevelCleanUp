using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Tunnel plan Phase 2c (ai_docs/2026-07-18_tunnel_generation/01): the portal apron stamper.
///     Terrain across the apron (first/last PortalApronMeters of the span) is stamped to the SOLVED
///     road surface — both raise AND cut (the portal approach cut) — so terrain and floor mesh meet at
///     the same Z at the portal mouth. Owner-raster guarded; mid-span mountain untouched; flag-off ⇒
///     byte-identical.
/// </summary>
public class TunnelPortalApronTests
{
    // Corridor (50,150)→(450,150) = 400 m, tunnel span [100,200], road Z 10, mountain +30 mid-span.
    private static UnifiedRoadNetwork BuildCorridor(bool apronsOn = true)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Tunnel, Layer = -1, OsmWayIds = { 77001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.TunnelRules = apronsOn
            ? TunnelRuleSystemOptions.CreateWithAllRulesEnabled()
            : new TunnelRuleSystemOptions();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = 10f; // solved tunnel floor = flat 10 (post TunnelProfileSolver)
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.StructureSpanType = span.Type;
                // Interior excluded, aprons (3 m) not — mirrors MarkStructureExclusions' shrink.
                var d = cs.DistanceAlongSpline;
                cs.IsExcluded = d >= span.StartDistance + 3f && d <= span.EndDistance - 3f;
            }
        }

        return network;
    }

    [Fact]
    public void Stamp_ApronCells_SetToRoadZ_BothPortals()
    {
        var network = BuildCorridor();
        // Mountain flank: terrain rises from 10 at the portals; make the apron cells sit high (12) so
        // the stamper must CUT them down to road Z — the portal approach cut a raise-only tongue can't do.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);

        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.True(cells > 0);
        Assert.Equal(10f, hm[150, 151], 0.05f); // station 101 — start apron cut down to road Z
        Assert.Equal(10f, hm[150, 249], 0.05f); // station 199 — end apron
        Assert.Equal(12f, hm[150, 200], 0.001f); // station 150 — mid-span mountain untouched
        Assert.Equal(12f, hm[150, 160], 0.001f); // station 110 — beyond the apron
    }

    [Fact]
    public void Stamp_RaisesLowGroundToo()
    {
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 9f); // ground below road

        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.True(cells > 0);
        Assert.Equal(10f, hm[150, 151], 0.05f); // filled up to road Z
    }

    /// <summary>
    ///     Banking follow-up (doc 03): with EnableTunnelBanking on, the apron terrain tilts across
    ///     the width with the road's bank (target = z + offset·sin(bank), the bridge tongue formula)
    ///     — the stamped surface is the same banked plane the floor mesh starts with at the portal.
    /// </summary>
    [Fact]
    public void Stamp_BankedStation_TerrainTiltsAcrossWidth()
    {
        var network = BuildCorridor();
        foreach (var cs in network.CrossSections)
            cs.BankAngleRadians = 0.1f; // chain bank, corridor-wide (approach + span)
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);

        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.True(cells > 0);
        // Station 101 (start apron), offsets ±3 within the 4 m half-width ⇒ full stamp to the
        // banked plane: center 10, sides 10 ± 3·sin(0.1) ≈ ±0.30 — left ≠ right.
        Assert.Equal(10f, hm[150, 151], 0.05f);
        var tilt = hm[147, 151] - hm[153, 151];
        Assert.Equal(6f * MathF.Sin(0.1f), MathF.Abs(tilt), 0.05f);
    }

    [Fact]
    public void Stamp_BankingFlagOff_FlatAcrossWidth_EvenWithChainBank()
    {
        // v1 baseline: banking off ⇒ the apron stays flat even when the sections carry a bank
        // (the approach neighbour may be banked in flat-tunnel mode — must not leak into the stamp).
        var network = BuildCorridor();
        foreach (var cs in network.CrossSections)
        {
            cs.BankAngleRadians = 0.1f;
            var spline = network.GetSplineById(cs.OwnerSplineId);
            spline!.Parameters.TunnelRules!.EnableTunnelBanking = false;
        }

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);
        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.True(cells > 0);
        Assert.Equal(10f, hm[147, 151], 0.05f);
        Assert.Equal(10f, hm[153, 151], 0.05f);
    }

    [Fact]
    public void Stamp_FlagOff_ByteIdentical()
    {
        var network = BuildCorridor(apronsOn: false);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);

        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(0, cells);
        Assert.Equal(12f, hm[150, 151], 0.0001f);
    }

    [Fact]
    public void Stamp_RespectsRoadSurfaceOwnerGuard()
    {
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);

        // A foreign spline (id 2) owns every cell — nothing may be stamped.
        var owner = new int[512, 512];
        for (var y = 0; y < 512; y++)
        for (var x = 0; x < 512; x++)
            owner[y, x] = 2;

        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false, roadSurfaceOwner: owner);

        Assert.Equal(0, cells);
        Assert.Equal(12f, hm[150, 151], 0.0001f);
    }

    [Fact]
    public void Stamp_BridgeSpans_NotTouched()
    {
        // A bridge-typed span must never be apron-stamped (type gate).
        var network = BuildCorridor();
        foreach (var cs in network.CrossSections)
            if (cs.StructureSpanId >= 0)
                cs.StructureSpanType = StructureType.Bridge;

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 12f);
        var cells = TunnelPortalApronStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(0, cells);
    }
}
