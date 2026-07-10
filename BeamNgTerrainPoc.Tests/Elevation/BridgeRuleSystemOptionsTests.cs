using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Bridge Rule System central config (V2 plan 0.2): the §3.2/§3.3/§3.5 static tables and the
/// class-step quantization (review P0-4 — Δp from OSM class steps, links inherit the parent).
/// </summary>
public class BridgeRuleSystemOptionsTests
{
    // ---- flags -----------------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_AllFlagsOff_AnyEnabledFalse()
    {
        var o = new BridgeRuleSystemOptions();
        Assert.False(o.AnyEnabled);
    }

    [Fact]
    public void AnyEnabled_TrueWhenOneFlagSet()
    {
        Assert.True(new BridgeRuleSystemOptions { EnableRampFeasibility = true }.AnyEnabled);
        Assert.True(new BridgeRuleSystemOptions { EnableBridgeStationReprojection = true }.AnyEnabled);
    }

    [Fact]
    public void NaturalProfileAnchor_CountsTowardAnyEnabled()
    {
        Assert.True(new BridgeRuleSystemOptions { EnableNaturalProfileAnchor = true }.AnyEnabled);
        Assert.False(new BridgeRuleSystemOptions().AnyEnabled);
    }

    [Fact]
    public void ContiguousSpanConsolidation_CountsTowardAnyEnabled()
    {
        Assert.True(new BridgeRuleSystemOptions { EnableContiguousSpanConsolidation = true }.AnyEnabled);
    }

    // ---- §3.2 structural depth (clearance budget only) -------------------------------------------------------

    [Theory]
    [InlineData(5f, 0.45f)]   // span/20 = 0.25 → lower clamp (mesh minimum)
    [InlineData(30f, 1.5f)]   // span/20
    [InlineData(200f, 2.0f)]  // span/20 = 10 → upper clamp (rendered deck max — no phantom girder)
    public void StructuralDepth_SpanOver20_ClampedToRenderedDeck(float span, float expected)
    {
        var o = new BridgeRuleSystemOptions();
        Assert.Equal(expected, o.StructuralDepthMeters(span), 3);
    }

    // ---- §3.1 per-kind clearances (A2) -----------------------------------------------------------------------

    [Fact]
    public void ClearanceFor_MatchesSpecTable()
    {
        var o = new BridgeRuleSystemOptions();
        Assert.Equal(4.70f, o.ClearanceFor(BridgeObstacleKind.Road), 3);
        Assert.Equal(6.00f, o.ClearanceFor(BridgeObstacleKind.Rail, electrified: true), 3);
        Assert.Equal(5.00f, o.ClearanceFor(BridgeObstacleKind.Rail, electrified: false), 3);
        Assert.Equal(5.25f, o.ClearanceFor(BridgeObstacleKind.Water, navigable: true), 3);
        Assert.Equal(2.00f, o.ClearanceFor(BridgeObstacleKind.Water, navigable: false), 3);
        Assert.Equal(0f, o.ClearanceFor(BridgeObstacleKind.Terrain), 3);
    }

    // ---- class-step quantization (P0-4) ----------------------------------------------------------------------

    [Theory]
    [InlineData("motorway", 4)]
    [InlineData("trunk", 4)]
    [InlineData("primary", 3)]
    [InlineData("secondary", 2)]
    [InlineData("tertiary", 2)]
    [InlineData("residential", 1)]
    [InlineData("unclassified", 1)]
    [InlineData("living_street", 1)]
    [InlineData("service", 0)]
    [InlineData("track", 0)]
    [InlineData("footway", 0)]
    public void ClassStep_MatchesSpecBands(string cls, int expected)
    {
        Assert.Equal(expected, BridgeRuleSystemOptions.ClassStepFor(cls));
    }

    [Theory]
    [InlineData("motorway_link", 4)]
    [InlineData("trunk_link", 4)]
    [InlineData("primary_link", 3)]
    [InlineData("secondary_link", 2)]
    [InlineData("tertiary_link", 2)]
    public void ClassStep_LinksInheritParentStep(string cls, int expected)
    {
        Assert.Equal(expected, BridgeRuleSystemOptions.ClassStepFor(cls));
    }

    [Fact]
    public void ClassStep_MotorwayOverOwnLink_DeltaZero()
    {
        // Review P0-4: a motorway over its own exit ramp must NOT read as "much more important",
        // else the §3.5 table dips the ramp at the gore.
        var dp = BridgeRuleSystemOptions.ClassStepFor("motorway") -
                 BridgeRuleSystemOptions.ClassStepFor("motorway_link");
        Assert.Equal(0, dp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ClassStep_UnknownOrMissing_DefaultsToUnclassifiedBand(string? cls)
    {
        Assert.Equal(1, BridgeRuleSystemOptions.ClassStepFor(cls));
    }

    [Fact]
    public void ClassStep_CaseAndWhitespaceInsensitive()
    {
        Assert.Equal(4, BridgeRuleSystemOptions.ClassStepFor(" Motorway "));
        Assert.Equal(3, BridgeRuleSystemOptions.ClassStepFor("PRIMARY_LINK"));
    }

    // ---- §3.3 ramp slope tables ------------------------------------------------------------------------------

    [Theory]
    [InlineData(4, 4f, 5f)]   // motorway, trunk
    [InlineData(3, 5f, 6f)]   // primary
    [InlineData(2, 6f, 8f)]   // secondary, tertiary
    [InlineData(1, 8f, 10f)]  // residential, unclassified
    [InlineData(0, 10f, 14f)] // service, track
    public void SlopeTables_MatchSpec(int step, float normal, float absolute)
    {
        Assert.Equal(normal, BridgeRuleSystemOptions.NormalMaxSlopePercent(step));
        Assert.Equal(absolute, BridgeRuleSystemOptions.AbsoluteMaxSlopePercent(step));
    }

    // ---- §3.5 priority shares --------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0.20f)]
    [InlineData(2, 0.20f)]  // upper much more important → raise little, lower road absorbs 80 %
    [InlineData(1, 0.35f)]
    [InlineData(0, 0.50f)]
    [InlineData(-1, 0.65f)]
    [InlineData(-2, 0.80f)] // lower much more important → bridge absorbs 80 % by raising
    [InlineData(-3, 0.80f)]
    public void RaiseShare_MatchesSpecTable(int deltaP, float expected)
    {
        Assert.Equal(expected, BridgeRuleSystemOptions.RaiseShareFor(deltaP), 3);
    }

    // ---- nesting ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetBridgeRules_AutoCreatesDefaults_OnBothParameterClasses()
    {
        var smoothing = new RoadSmoothingParameters();
        Assert.Null(smoothing.BridgeRules);
        Assert.False(smoothing.GetBridgeRules().AnyEnabled);
        Assert.NotNull(smoothing.BridgeRules);

        var creation = new TerrainCreationParameters();
        Assert.Null(creation.BridgeRules);
        Assert.False(creation.GetBridgeRules().AnyEnabled);
        Assert.NotNull(creation.BridgeRules);
    }
}
