using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 06 — gap-free terrain ↔ deck transition. The stamped terrain road ENDS at the exclusion
///     boundary and a butt-joint at 1 m raster always leaves a lip/hole, so
///     <see cref="BridgeAbutmentOverlapStamper"/> stamps the first/last AbutmentOverlapMeters of each
///     sparse-mode span into the heightmap at deckZ − AbutmentOverlapDropMeters (raise-only), and
///     <see cref="BridgeDeckExcavator"/> exempts that zone (ceiling deckZ − drop instead of − undercut)
///     so the tongue survives.
/// </summary>
public class BridgeAbutmentOverlapTests
{
    // Corridor (50,150)→(450,150) = 400 m, span [100,200] at deck Z 14.7. World x = 50 + station.
    private static UnifiedRoadNetwork BuildCorridor(bool sparse = true, float deckZ = 14.7f)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = sparse,
            // Fixture: deck 14.7 over terrain 10 (4.7 m). The mechanism tests here predate the
            // seam-only lift cap — raise it so they keep exercising holes/width/owner-guard/excavation;
            // the cap itself is covered by Stamp_LiftBeyondCap_IsSkipped_NeverWallsUpLowGround.
            AbutmentOverlapMaxLiftMeters = 10f,
        };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            cs.TargetElevation = 10f;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
                cs.TargetElevation = deckZ;
            }
        }

        return network;
    }

    [Fact]
    public void Stamp_FillsTongueAtBothSpanEnds_NotMidSpan()
    {
        // Natural terrain 10, deck 14.7 → tongue cells at the span ends rise to deckZ − drop (0.01) =
        // 14.69; mid-span stays natural (daylight under the deck is the excavator's business, not ours).
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var cells = BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.True(cells > 0);
        Assert.Equal(14.69f, hm[150, 151], 0.01f); // station 101 — start tongue
        Assert.Equal(14.69f, hm[150, 249], 0.01f); // station 199 — end tongue
        Assert.Equal(10f, hm[150, 200], 0.001f);   // station 150 — untouched mid-span
        Assert.Equal(10f, hm[150, 160], 0.001f);   // station 110 — beyond the 3 m overlap
    }

    [Fact]
    public void Stamp_LiftBeyondCap_IsSkipped_NeverWallsUpLowGround()
    {
        // Winningen render 2026-07-02 #2: ground ≫ AbutmentOverlapMaxLift below the deck end is not a
        // raster seam — it is genuinely low ground under an elevated deck end (e.g. a doc-28 underpass
        // well near the abutment). The tongue must skip it instead of stamping a deck-level wall/patch.
        var network = BuildCorridor(); // deck 14.7 over terrain 10 → 4.69 m lift
        var spline = network.GetSplineById(1)!;
        spline.Parameters.BridgeRules!.AbutmentOverlapMaxLiftMeters = 2f; // the production default
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(10f, hm[150, 151], 0.001f); // start tongue zone — untouched
        Assert.Equal(10f, hm[150, 249], 0.001f); // end tongue zone — untouched
    }

    [Fact]
    public void MarkStructureExclusions_SparseMode_ShrinksExclusionByOverlap()
    {
        // Doc 06 v2 (render #14 "transition quite rough"): the first/last AbutmentOverlapMeters of a
        // sparse-mode span stay ORDINARY stamped road (smoothed + painted by the normal pipeline) — only
        // the inner part is terrain-excluded. The span tag stays range-wide (deck mesh unchanged).
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150),
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true };
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        var bySpline = new Dictionary<int, List<UnifiedCrossSection>>
        {
            [1] = network.GetCrossSectionsForSpline(1).OrderBy(c => c.LocalIndex).ToList(),
        };

        UnifiedRoadSmoother.MarkStructureExclusions([corridor], bySpline);

        UnifiedCrossSection At(float station) => bySpline[1]
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

        Assert.True(At(101f).StructureSpanId >= 0);   // tagged span-wide (deck/mesh/excavator coverage)
        Assert.False(At(101f).IsExcluded);            // …but the overlap zone stays stampable road
        Assert.False(At(199f).IsExcluded);
        Assert.True(At(105f).IsExcluded);             // inner span: excluded as before
        Assert.True(At(150f).IsExcluded);
        Assert.True(At(195f).IsExcluded);
    }

    [Fact]
    public void MarkStructureExclusions_SparseOff_FullExclusionUnchanged()
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150),
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        var bySpline = new Dictionary<int, List<UnifiedCrossSection>>
        {
            [1] = network.GetCrossSectionsForSpline(1).OrderBy(c => c.LocalIndex).ToList(),
        };

        UnifiedRoadSmoother.MarkStructureExclusions([corridor], bySpline);

        Assert.True(bySpline[1].First(c => MathF.Abs(c.DistanceAlongSpline - 101f) < 0.6f).IsExcluded);
        Assert.True(bySpline[1].First(c => MathF.Abs(c.DistanceAlongSpline - 199f) < 0.6f).IsExcluded);
    }

    [Fact]
    public void Stamp_CoarseSectionInterval_NoAlignmentHoles()
    {
        // The render-#13 bug ("works at one end, not the other"): per-section stamping at a 2 m section
        // interval painted every OTHER 1 m heightmap row — whether the cap row landed painted was grid
        // alignment luck per end. The sub-stepped march must paint EVERY row of the tongue, including the
        // boundary segment between the last approach section and the first span section.
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = true,
            AbutmentOverlapMaxLiftMeters = 10f, // mechanism fixture (see BuildCorridor)
        };
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor, crossSectionSpacing: 2f);
        foreach (var cs in network.GetCrossSectionsForSpline(1))
        {
            cs.TargetElevation = 10f;
            if (cs.DistanceAlongSpline >= span.StartDistance && cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
                cs.TargetElevation = 14.7f;
            }
        }

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);
        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        // EVERY row across both tongues — odd stations included (sections only exist at even ones).
        // deckZ − drop (0.01) = 14.69.
        Assert.Equal(14.69f, hm[150, 150], 0.01f); // station 100
        Assert.Equal(14.69f, hm[150, 151], 0.01f); // station 101 — between sections (the old hole)
        Assert.Equal(14.69f, hm[150, 152], 0.01f); // station 102
        Assert.Equal(14.69f, hm[150, 249], 0.01f); // station 199 — between sections at the END tongue
        Assert.Equal(14.69f, hm[150, 250], 0.01f); // station 200

        // The boundary segment approach(98, z=10) → span(100, z=14.67) is painted (lerped), not a hole.
        Assert.True(hm[150, 149] > 10.5f, $"boundary segment must be painted, got {hm[150, 149]:F2}");
        Assert.True(hm[150, 251] > 10.5f, $"end boundary segment must be painted, got {hm[150, 251]:F2}");
    }

    [Fact]
    public void Stamp_SuppressedEnd_NoTongue_OtherEndStillStamped()
    {
        // Doc 13: a bridge-to-bridge continuation end gets NO tongue — terrain must not rise to a
        // mid-air deck corner. The opposite (ground) end keeps the doc-06 tongue.
        var network = BuildCorridor();
        var seg = network.GetSplineById(1)!.StructureSegments![0];
        seg.EndContinuesOntoDeck = true;
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(14.69f, hm[150, 151], 0.01f); // station 101 — start tongue unchanged
        Assert.Equal(10f, hm[150, 249], 0.001f);   // station 199 — suppressed end: no tongue
        Assert.Equal(10f, hm[150, 251], 0.001f);   // station 201 — approach boundary also untouched
    }

    [Fact]
    public void Stamp_IsRaiseOnly_NeverCutsHighGround()
    {
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f); // terrain above the deck

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(20f, hm[150, 151], 0.001f); // cutting is the excavator's job
    }

    [Fact]
    public void Stamp_SparseOff_NoOp()
    {
        var network = BuildCorridor(sparse: false);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var cells = BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(0, cells);
        Assert.Equal(10f, hm[150, 151], 0.001f);
    }

    [Fact]
    public void Stamp_TongueUsesCorridorWidth_NeighbourSurfaceStillProtected()
    {
        // The tongue spans the full smoothing corridor (EffectiveRoadWidth) to match the corridor-width deck
        // mesh + excavator — it raises terrain to the deck across the whole deck footprint. The per-cell
        // road-surface owner guard (NOT a narrow tongue) is what keeps it off neighbouring roads. Painted
        // 8 m inside a 20 m corridor; corridor is horizontal at y=150; station 101 → px 151; offset = |y−150|.
        var network = BuildCorridor();
        foreach (var cs in network.GetCrossSectionsForSpline(1))
        {
            cs.EffectiveRoadWidth = 20f; // corridor (tongue basis): half 10
            cs.SurfaceWidth = 8f;        // painted: half 4 — NOT what the tongue follows
        }
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        // Offset 12 is BEYOND the painted reach (4+6=10) but inside the corridor reach (10+6=16): the
        // painted-width tongue never visits it; the corridor-width tongue raises it.
        var owner = NoOwnerRaster(512);
        owner[162, 151] = 1;  // self-owned (this bridge) at offset +12 → shaped to the deck
        owner[138, 151] = 99; // neighbour-owned at offset −12 → protected

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false, roadSurfaceOwner: owner);

        Assert.True(hm[162, 151] > 12f, $"corridor-width tongue must raise the offset-12 cell, got {hm[162, 151]:F2}");
        Assert.Equal(10f, hm[138, 151], 0.01f); // neighbour-owned offset-12 cell stays natural (mask guard)
    }

    [Fact]
    public void Stamp_SkipsCellsOwnedByAnotherSpline_ButShapesSelfAndTerrain()
    {
        // At an abutment the tongue must not bury a NEIGHBOURING road's surface. With an owner raster, cells
        // owned by a different spline are left untouched; the bridge's own surface and bare terrain still rise.
        var network = BuildCorridor(); // spline 1
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var owner = NoOwnerRaster(512);
        owner[150, 151] = 99; // foreign road surface at the start tongue — must be protected
        owner[150, 152] = 1;  // the bridge's own surface — must still be shaped

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false, roadSurfaceOwner: owner);

        Assert.Equal(10f, hm[150, 151], 0.01f); // foreign cell preserved, not raised to the deck tongue
        Assert.True(hm[150, 152] > 12f, $"self-owned cell must still rise, got {hm[150, 152]:F2}");
        Assert.True(hm[150, 150] > 12f, $"bare-terrain cell must still rise, got {hm[150, 150]:F2}");
    }

    private static int[,] NoOwnerRaster(int size)
    {
        var owner = new int[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            owner[y, x] = RoadSurfaceOwnerRaster.NoOwner;
        return owner;
    }

    [Fact]
    public void Excavator_ExemptsOverlap_KeepsTongueCeiling()
    {
        // Terrain 20 pokes above the 14.7 deck everywhere. With the doc-06 exemption the overlap zone is
        // shaved only to deckZ − drop (14.67) — the tongue's level — while mid-span keeps the regular
        // undercut (14.65).
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f);

        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f,
            abutmentOverlapMeters: 3f, abutmentOverlapDropMeters: 0.03f, log: false);

        Assert.Equal(14.67f, hm[150, 151], 0.005f); // station 101 — overlap ceiling
        Assert.Equal(14.65f, hm[150, 200], 0.005f); // station 150 — regular undercut
    }

    [Fact]
    public void Excavator_SuppressedEnd_FullUndercut_NoTongueCeiling()
    {
        // Doc 13: no tongue at a bridge-to-bridge end ⇒ no exemption either — poking terrain there is
        // shaved to the regular undercut, so nothing can survive at the mid-air deck corner.
        var network = BuildCorridor();
        var seg = network.GetSplineById(1)!.StructureSegments![0];
        seg.EndContinuesOntoDeck = true;
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f);

        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f,
            abutmentOverlapMeters: 3f, abutmentOverlapDropMeters: 0.03f, log: false);

        Assert.Equal(14.67f, hm[150, 151], 0.005f); // start overlap keeps the tongue ceiling
        Assert.Equal(14.65f, hm[150, 249], 0.005f); // suppressed end: full undercut
    }

    [Fact]
    public void Excavator_DefaultParams_ByteIdenticalLegacy()
    {
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f);

        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f, log: false);

        Assert.Equal(14.65f, hm[150, 151], 0.005f); // no exemption without the overlap params
    }

    [Fact]
    public void StampThenExcavate_TongueSurvives()
    {
        // The full doc-06 pipeline order: stamp the tongue, then excavate with the exemption — the
        // tongue must come out exactly at deckZ − drop, not cut to deckZ − undercut.
        var network = BuildCorridor();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);
        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f,
            abutmentOverlapMeters: 3f, abutmentOverlapDropMeters: 0.03f, log: false);

        Assert.Equal(14.67f, hm[150, 151], 0.005f);
        Assert.Equal(14.67f, hm[150, 249], 0.005f);
    }

    [Fact]
    public void Excavate_SkipsCellsOwnedByAnotherSpline_ButCutsSelf()
    {
        // The excavator shaves terrain above the deck across the deck footprint — but it must not cut a
        // NEIGHBOURING road's surface that happens to lie under the corridor-width footprint at an abutment.
        var network = BuildCorridor(); // spline 1, deck 14.7
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f); // terrain above the deck

        var owner = NoOwnerRaster(512);
        owner[150, 200] = 99; // foreign road surface mid-span (station 150) — must not be cut
        owner[150, 201] = 1;  // the bridge's own surface (station 151) — still cut

        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f, log: false, roadSurfaceOwner: owner);

        Assert.Equal(20f, hm[150, 200], 0.01f);  // foreign cell preserved, not cut to the deck
        Assert.True(hm[150, 201] < 15f, $"self-owned cell must still be cut, got {hm[150, 201]:F2}");
    }
}
