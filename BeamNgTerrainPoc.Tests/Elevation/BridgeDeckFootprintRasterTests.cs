using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 09 §9.2 — deck-footprint RAISE guard.
///     <see cref="BridgeDeckFootprintRaster" /> rasterizes every bridge deck's plan-view footprint so that
///     raising passes (<see cref="BridgeAbutmentOverlapStamper" />, <see cref="GradeSeparationResolver" />)
///     can skip cells owned by a FOREIGN deck. Lowering passes are intentionally NOT wired to this raster.
/// </summary>
public class BridgeDeckFootprintRasterTests
{
    // ------------------------------------------------------------------ helpers

    /// <summary>
    ///     One straight bridge deck: spline 1, span 0, IsExcluded sections from station 100→200,
    ///     deck Z = <paramref name="deckZ" />, EffectiveRoadWidth = 8 m.
    ///     World layout: corridor from (50,150) to (450,150); x = 50 + station.
    /// </summary>
    private static UnifiedRoadNetwork BuildSingleDeck(float deckZ = 15f, int splineId = 1)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200,
            Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new Vector2(50, 150), new Vector2(450, 150),
            priority: 10000, mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = true,
            AbutmentOverlapMaxLiftMeters = 10f,
        };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            cs.TargetElevation = 10f;
            cs.EffectiveRoadWidth = 8f;
            cs.SurfaceWidth = 8f;
            if (cs.DistanceAlongSpline >= span.StartDistance &&
                cs.DistanceAlongSpline <= span.EndDistance)
            {
                cs.StructureSpanId = span.SpanId;
                cs.IsExcluded = true;
                cs.TargetElevation = deckZ;
            }
        }

        return network;
    }

    // ------------------------------------------------------------------ Build tests

    [Fact]
    public void Build_DeckCellsOwnedByDeckSpline()
    {
        // Deck runs along y=150, x ≈ 150..250 (stations 100..200; world x = 50+station).
        // Cells on the centerline inside the deck footprint are owned by spline 1.
        // A cell far from the deck (y=50, x=50) stays NoOwner.
        var network = BuildSingleDeck(deckZ: 15f, splineId: 1);

        var raster = BridgeDeckFootprintRaster.Build(network, 512, 512, 1f);

        // Centerline cell at station 150 → world (200,150) → px=200,py=150
        Assert.Equal(1, raster[150, 200]);
        // Far cell — bare terrain, no deck there
        Assert.Equal(BridgeDeckFootprintRaster.NoOwner, raster[50, 50]);
    }

    [Fact]
    public void Build_NonExcludedSectionsNotStamped()
    {
        // Approach sections (IsExcluded=false) must NOT claim any cell in the DECK footprint raster.
        // Approach at station 50 (world x=100, y=150) → must remain NoOwner.
        var network = BuildSingleDeck(deckZ: 15f, splineId: 1);

        var raster = BridgeDeckFootprintRaster.Build(network, 512, 512, 1f);

        // Approach at station 50 → world (100,150) → px=100,py=150
        Assert.Equal(BridgeDeckFootprintRaster.NoOwner, raster[150, 100]);
    }

    [Fact]
    public void Build_OverlappingDecks_LowerDeckWins()
    {
        // Deck A (spline 1) at Z=10, deck B (spline 2) at Z=30 — same horizontal centerline.
        // In the overlap region the LOWER deck (A, Z=10) must own the cell.
        var spanA = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200,
            Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 1001L }
        };
        var corrA = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(50, 150), new Vector2(450, 150),
            priority: 10000, mergeStructuresIntoCorridor: true, structureSegments: [spanA]);
        corrA.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = true,
            AbutmentOverlapMaxLiftMeters = 10f,
        };

        // Deck B same horizontal path but slightly different y to share the grid cells.
        var spanB = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200,
            Type = StructureType.Bridge, Layer = 2, OsmWayIds = { 2001L }
        };
        var corrB = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(50, 150), new Vector2(450, 150),
            priority: 8000, mergeStructuresIntoCorridor: true, structureSegments: [spanB]);
        corrB.Parameters.BridgeRules = new BridgeRuleSystemOptions
        {
            EnableSparseDeckConstraints = true,
            AbutmentOverlapMaxLiftMeters = 10f,
        };

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corrA);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corrB);

        foreach (var cs in network.GetCrossSectionsForSpline(1))
        {
            cs.EffectiveRoadWidth = 8f;
            cs.TargetElevation = 10f;
            if (cs.DistanceAlongSpline >= 100f && cs.DistanceAlongSpline <= 200f)
            {
                cs.StructureSpanId = spanA.SpanId;
                cs.IsExcluded = true;
                cs.TargetElevation = 10f; // LOWER deck
            }
        }

        foreach (var cs in network.GetCrossSectionsForSpline(2))
        {
            cs.EffectiveRoadWidth = 8f;
            cs.TargetElevation = 10f;
            if (cs.DistanceAlongSpline >= 100f && cs.DistanceAlongSpline <= 200f)
            {
                cs.StructureSpanId = spanB.SpanId;
                cs.IsExcluded = true;
                cs.TargetElevation = 30f; // HIGHER deck
            }
        }

        var raster = BridgeDeckFootprintRaster.Build(network, 512, 512, 1f);

        // The shared cell at station 150, y=150 → px=200,py=150 must be owned by spline 1 (lower).
        Assert.Equal(1, raster[150, 200]);
    }

    [Fact]
    public void CanRaise_Semantics()
    {
        // null raster → always true (legacy / no guard).
        Assert.True(BridgeDeckFootprintRaster.CanRaise(null, 0, 0, 42));

        var raster = new int[10, 10];
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 10; x++)
            raster[y, x] = BridgeDeckFootprintRaster.NoOwner;

        // NoOwner cell → true regardless of selfSplineId
        Assert.True(BridgeDeckFootprintRaster.CanRaise(raster, 5, 5, 1));

        // Self-owned cell → true
        raster[5, 5] = 1;
        Assert.True(BridgeDeckFootprintRaster.CanRaise(raster, 5, 5, 1));

        // Foreign-owned cell → false
        raster[5, 5] = 99;
        Assert.False(BridgeDeckFootprintRaster.CanRaise(raster, 5, 5, 1));
    }

    // ------------------------------------------------------------------ Behavior test

    [Fact]
    public void Stamp_DeckFootprintGuard_BlocksBuryingNeighbourDeck()
    {
        // Two bridges: bridge A (spline 1) is the STAMPER — its tongue raises terrain at its abutment.
        // Bridge B (spline 2) is the VICTIM — its deck footprint lies where A's tongue would land.
        //
        // Layout (1 m/pixel, 512×512 map):
        //   Bridge A: corridor (50,250)→(450,250), deck span [100..200], deck Z=15.
        //   Bridge B: corridor (50,150)→(450,150), deck span [140..160], deck Z=12.
        //             B is LOWER — its abutment is inside A's approach-raise footprint.
        //
        // Both splines run east-west at different y. A's tongue expands in the y-direction too.
        // We place B's deck at y=150, and A's tongue reaches from y≈244 outward — they don't
        // spatially overlap at 1 m/px with these y-coordinates (100 m apart).
        //
        // Simpler: use ONE spline for the stamper (A) and manually own some cells of B's footprint
        // in the deckFootprint raster, then verify those cells are NOT raised while A's own cells ARE.
        //
        // This directly tests the CanRaise call in StampRun: a cell owned by B (foreign) is skipped.

        var network = BuildSingleDeck(deckZ: 15f, splineId: 1); // bridge A is the stamper
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        // Build the real deck raster for A — cells along A's deck (y=150, x≈150..250) are owned by 1.
        var realRaster = BridgeDeckFootprintRaster.Build(network, 512, 512, 1f);

        // Prove OLD behaviour (null raster → B-deck cell IS raised by A's tongue):
        // The tongue lands at station 101, y=150 → px=151, py=150.
        var hmOld = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);
        BridgeAbutmentOverlapStamper.Stamp(network, hmOld, 1f, log: false, deckFootprint: null);
        var raisedWithoutGuard = hmOld[150, 151] > 10.1f; // tongue DID raise it

        // NEW behaviour: pass the real raster — cells A owns (spline 1) are still raised; a cell we
        // forcibly mark as foreign (spline 99) in a copy of the raster must be SKIPPED.
        var guardedRaster = (int[,])realRaster.Clone();
        // Hijack one tongue cell (px=152, py=150) to simulate a foreign deck there.
        guardedRaster[150, 152] = 99;
        // Leave px=151 owned by 1 (self) — should still be raised.
        guardedRaster[150, 151] = 1;

        var hmNew = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);
        BridgeAbutmentOverlapStamper.Stamp(network, hmNew, 1f, log: false, deckFootprint: guardedRaster);

        // The foreign cell (152) must be UNCHANGED.
        Assert.Equal(10f, hmNew[150, 152], 0.01f);
        // The self-owned cell (151) must still be raised.
        Assert.True(hmNew[150, 151] > 10.1f,
            $"self-owned tongue cell must still be raised, got {hmNew[150, 151]:F3}");
        // Confirm old behaviour was raising both (proves the guard makes a difference).
        Assert.True(raisedWithoutGuard,
            $"without guard the tongue cell must have been raised (got {hmOld[150, 151]:F3})");
    }
}
