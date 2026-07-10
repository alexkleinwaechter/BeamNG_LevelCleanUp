using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tests for <see cref="BridgeDeckExcavator" /> — shaving terrain that pokes above a bridge deck down to
///     just under the deck, so the deck is the clean driving surface. Terrain at/below the deck is untouched.
/// </summary>
public class BridgeDeckExcavatorTests
{
    private const int Size = 300;
    private const float MetersPerPixel = 1f;

    // road1 (10..100) → bridge (100..200) → road2 (200..290) along y=50, bridge deck flat at the given z.
    private static UnifiedRoadNetwork BuildNetworkWithFlatDeck(float deckZ)
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary");

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
        {
            cs.IsExcluded = true;
            cs.TargetElevation = deckZ; // simulate a solved flat deck
        }

        return network;
    }

    [Fact]
    public void Excavate_TerrainAboveDeck_IsShavedToJustUnderDeck()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);

        // Terrain everywhere higher than the deck → it pokes up through/around the deck.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 110f);

        var result = BridgeDeckExcavator.Excavate(network, hm, MetersPerPixel, log: false);

        Assert.Equal(1, result.BridgesExcavated);
        Assert.True(result.CellsLowered > 0);

        // Under the deck, terrain is shaved to deckZ − undercut at every station (incl. the abutments).
        var expected = deckZ - BridgeDeckExcavator.DefaultUndercutMeters;
        Assert.Equal(expected, hm[50, 150], precision: 3); // mid-span
        Assert.Equal(expected, hm[50, 100], precision: 3); // abutment — no taper, no notch

        // Off to the side, outside the deck footprint, terrain is untouched.
        Assert.Equal(110f, hm[10, 150], precision: 3);
    }

    [Fact]
    public void Excavate_FollowsDeckSlope_NoFlatPad()
    {
        // A sloped deck: terrain a constant height above it everywhere. The shave must track the slope
        // (each station cut to its own deckZ − undercut), not flatten to a single pad height.
        var network = BuildNetworkWithFlatDeck(0f);
        var bridge = network.GetCrossSectionsForSpline(2).OrderBy(c => c.LocalIndex).ToList();
        foreach (var cs in bridge)
            cs.TargetElevation = 100f + cs.DistanceAlongSpline * 0.1f; // 10% grade along the deck

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 200f); // well above the whole deck

        BridgeDeckExcavator.Excavate(network, hm, MetersPerPixel, log: false);

        // Two stations along the deck must end up at different shaved heights, matching the local deck Z.
        var near = bridge[0];
        var far = bridge[^1];
        var undercut = BridgeDeckExcavator.DefaultUndercutMeters;
        Assert.Equal(near.TargetElevation - undercut,
            hm[(int)near.CenterPoint.Y, (int)near.CenterPoint.X], precision: 2);
        Assert.Equal(far.TargetElevation - undercut,
            hm[(int)far.CenterPoint.Y, (int)far.CenterPoint.X], precision: 2);
        Assert.True(MathF.Abs((far.TargetElevation - undercut) - (near.TargetElevation - undercut)) > 5f,
            "shaved heights should differ along the slope (not a flat pad)");
    }

    [Fact]
    public void Excavate_BankedDeck_TracksLateralTilt_NoPokeThroughOnLowSide()
    {
        // A curved bridge is banked: the deck tilts laterally. RoadCrossSection puts the +Normal edge at
        // center + halfWidth·sin(bank) and the −Normal edge at center − halfWidth·sin(bank). A flat
        // centerline ceiling would over-cut the high side and leave terrain poking through the low side —
        // this asserts the cut follows the tilt.
        const float deckZ = 100f;
        const float bank = 0.20f; // ~11.5° lateral tilt
        var network = BuildNetworkWithFlatDeck(deckZ);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.BankAngleRadians = bank;

        var sections = network.GetCrossSectionsForSpline(2).OrderBy(c => c.LocalIndex).ToList();
        var mid = sections[sections.Count / 2];
        var n = mid.NormalDirection;
        var hw = mid.EffectiveRoadWidth / 2f;
        var delta = hw * MathF.Sin(bank); // deck rises by delta on the +Normal edge, drops by delta on −Normal

        // Terrain flat at the centerline deck height: the +Normal (high) half of the deck now sits ABOVE the
        // terrain (must be left alone — real daylight under it), the −Normal (low) half sits BELOW it
        // (terrain pokes through → must be shaved down to the tilted deck).
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ);

        BridgeDeckExcavator.Excavate(network, hm, MetersPerPixel, log: false);

        int Px(float w) => Math.Clamp((int)(w / MetersPerPixel), 0, Size - 1);
        var highX = Px(mid.CenterPoint.X + n.X * hw * 0.8f);
        var highY = Px(mid.CenterPoint.Y + n.Y * hw * 0.8f);
        var lowX = Px(mid.CenterPoint.X - n.X * hw * 0.8f);
        var lowY = Px(mid.CenterPoint.Y - n.Y * hw * 0.8f);

        var undercut = BridgeDeckExcavator.DefaultUndercutMeters;

        // High side: the deck is above the terrain → terrain untouched (would be wrongly cut by a flat ceiling).
        Assert.Equal(deckZ, hm[highY, highX], precision: 2);

        // Low side: shaved strictly below the flat centerline ceiling, tracking the tilted deck surface.
        Assert.True(hm[lowY, lowX] < deckZ - undercut - 0.1f,
            $"low side {hm[lowY, lowX]} should be shaved below the flat ceiling {deckZ - undercut}");

        // The two sides differ by most of the lateral tilt — a flat cut would make them equal.
        Assert.True(hm[highY, highX] - hm[lowY, lowX] > 0.5f * delta,
            "banked deck low side must be cut deeper than the high side");
    }

    [Fact]
    public void Excavate_TerrainBelowDeck_IsLeftUntouched()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);

        // A real gap already exists under the deck — must not be filled.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 60f);

        var result = BridgeDeckExcavator.Excavate(network, hm, MetersPerPixel, log: false);

        Assert.Equal(0, result.CellsLowered);
        Assert.Equal(60f, hm[50, 150], precision: 3);
    }

    [Fact]
    public void Excavate_NoBridges_IsNoop()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(290, 50), "primary");
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 110f);

        var result = BridgeDeckExcavator.Excavate(network, hm, MetersPerPixel, log: false);

        Assert.Equal(0, result.CellsLowered);
        Assert.Equal(110f, hm[50, 150], precision: 3);
    }
}
