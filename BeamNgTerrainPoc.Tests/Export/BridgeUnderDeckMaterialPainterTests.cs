using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tests for <see cref="BridgeUnderDeckMaterialPainter" /> (doc 07) — repainting the terrain material
///     under bridge deck footprints so material-keyed billboard vegetation cannot grow through the deck.
///     Only "tight" cells (within the clearance below the local deck surface) are repainted; deep ravines
///     keep their natural material.
/// </summary>
public class BridgeUnderDeckMaterialPainterTests
{
    private const int Size = 300;
    private const float MetersPerPixel = 1f;
    private const float Clearance = 1.0f;

    private static readonly IReadOnlyList<string> Materials = ["grass", "dirt", "asphalt_road"];
    private const byte DirtIndex = 1;

    // Same fixture as the excavator tests: road1 (10..100) → bridge (100..200) → road2 (200..290)
    // along y=50, bridge deck flat at the given z.
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
            cs.TargetElevation = deckZ;
        }

        return network;
    }

    // ========================================
    // ResolveDefaultMaterialName (contains-query default: dirt → asphalt, shortest match)
    // ========================================

    [Fact]
    public void ResolveDefault_PrefersDirtOverAsphalt()
    {
        var resolved = BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName(
            ["asphalt", "grass", "dirt"]);

        Assert.Equal("dirt", resolved);
    }

    [Fact]
    public void ResolveDefault_ShortestMatchingNameWins()
    {
        var resolved = BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName(
            ["dirt_loose_rocky", "dirt", "dirt_loose"]);

        Assert.Equal("dirt", resolved);
    }

    [Fact]
    public void ResolveDefault_ContainsMatch_IsCaseInsensitive()
    {
        var resolved = BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName(
            ["grass", "Loose_DIRT_track"]);

        Assert.Equal("Loose_DIRT_track", resolved);
    }

    [Fact]
    public void ResolveDefault_FallsBackToAsphalt_WhenNoDirt()
    {
        var resolved = BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName(
            ["grass", "Asphalt_old", "rock"]);

        Assert.Equal("Asphalt_old", resolved);
    }

    [Fact]
    public void ResolveDefault_ReturnsNull_WhenNothingMatches()
    {
        var resolved = BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName(
            ["grass", "rock", "snow"]);

        Assert.Null(resolved);
    }

    // ========================================
    // Paint
    // ========================================

    [Fact]
    public void Paint_TightCellsUnderDeck_AreRepainted_SideCellsUntouched()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);

        // Terrain just under the deck everywhere — the doc-06 "tight" situation (overlap tongues /
        // excavated cells sit centimetres below the deck top).
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - 0.05f);
        var indices = new byte[Size * Size]; // all grass (0)

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "dirt", Materials, Clearance, log: false);

        Assert.Equal(DirtIndex, result.ResolvedMaterialIndex);
        Assert.Equal(1, result.SpansPainted);
        Assert.True(result.CellsPainted > 0);

        // Mid-span and abutment cells on the centerline are repainted.
        Assert.Equal(DirtIndex, indices[50 * Size + 150]);
        Assert.Equal(DirtIndex, indices[50 * Size + 100]);

        // Off to the side, outside the deck footprint, the material is untouched.
        Assert.Equal(0, indices[10 * Size + 150]);

        // Under the plain roads before/after the bridge, the material is untouched.
        Assert.Equal(0, indices[50 * Size + 50]);
        Assert.Equal(0, indices[50 * Size + 250]);
    }

    [Fact]
    public void Paint_DeepTerrainUnderTallSpan_KeepsNaturalMaterial()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);

        // A real ravine far below the deck — must keep its natural material.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 60f);
        var indices = new byte[Size * Size];

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "dirt", Materials, Clearance, log: false);

        Assert.Equal(0, result.CellsPainted);
        Assert.Equal(0, indices[50 * Size + 150]);
    }

    [Fact]
    public void Paint_MixedDepths_OnlyTightCellsChange()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);

        // First half of the span tight under the deck, second half a deep gap.
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - 0.5f);
        for (var y = 0; y < Size; y++)
        for (var x = 150; x < Size; x++)
            hm[y, x] = 60f;

        var indices = new byte[Size * Size];

        BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "dirt", Materials, Clearance, log: false);

        Assert.Equal(DirtIndex, indices[50 * Size + 120]); // tight half → repainted
        Assert.Equal(0, indices[50 * Size + 180]);         // deep half → natural material kept
    }

    [Fact]
    public void Paint_BankedDeck_FollowsLateralTilt()
    {
        // Banked deck: the surface rises by halfWidth·sin(bank) on the +Normal edge and drops on the
        // −Normal edge. With terrain flat at deckZ − clearance − a hair, only the LOW (−Normal) side is
        // within the clearance of the tilted surface — the high side must stay unpainted.
        const float deckZ = 100f;
        const float bank = 0.35f; // ~20° → edge delta well above the test margin
        var network = BuildNetworkWithFlatDeck(deckZ);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.BankAngleRadians = bank;

        var sections = network.GetCrossSectionsForSpline(2).OrderBy(c => c.LocalIndex).ToList();
        var mid = sections[sections.Count / 2];
        var n = mid.NormalDirection;
        var hw = mid.EffectiveRoadWidth / 2f;

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - Clearance - 0.2f);
        var indices = new byte[Size * Size];

        BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "dirt", Materials, Clearance, log: false);

        int Px(float w) => Math.Clamp((int)(w / MetersPerPixel), 0, Size - 1);
        var highX = Px(mid.CenterPoint.X + n.X * hw * 0.8f);
        var highY = Px(mid.CenterPoint.Y + n.Y * hw * 0.8f);
        var lowX = Px(mid.CenterPoint.X - n.X * hw * 0.8f);
        var lowY = Px(mid.CenterPoint.Y - n.Y * hw * 0.8f);

        // Low side: tilted deck surface dropped towards the terrain → tight → repainted.
        Assert.Equal(DirtIndex, indices[lowY * Size + lowX]);

        // High side: tilted deck surface rose away from the terrain → beyond the clearance → untouched.
        Assert.Equal(0, indices[highY * Size + highX]);
    }

    [Fact]
    public void Paint_MaterialNameMatching_IsCaseInsensitive()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - 0.05f);
        var indices = new byte[Size * Size];

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "DIRT", Materials, Clearance, log: false);

        Assert.Equal(DirtIndex, result.ResolvedMaterialIndex);
        Assert.True(result.CellsPainted > 0);
    }

    [Fact]
    public void Paint_EmptyMaterialName_IsNoop()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - 0.05f);
        var indices = new byte[Size * Size];

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, null, Materials, Clearance, log: false);

        Assert.Equal(-1, result.ResolvedMaterialIndex);
        Assert.Equal(0, result.CellsPainted);
        Assert.All(indices, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Paint_UnknownMaterialName_IsNoop()
    {
        const float deckZ = 100f;
        var network = BuildNetworkWithFlatDeck(deckZ);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, deckZ - 0.05f);
        var indices = new byte[Size * Size];

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "mud", Materials, Clearance, log: false);

        Assert.Equal(-1, result.ResolvedMaterialIndex);
        Assert.Equal(0, result.CellsPainted);
    }

    [Fact]
    public void Paint_NoBridges_IsNoop()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(290, 50), "primary");
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(Size, 100f);
        var indices = new byte[Size * Size];

        var result = BridgeUnderDeckMaterialPainter.Paint(
            network, hm, indices, MetersPerPixel, "dirt", Materials, Clearance, log: false);

        Assert.Equal(0, result.CellsPainted);
        Assert.All(indices, b => Assert.Equal(0, b));
    }
}
