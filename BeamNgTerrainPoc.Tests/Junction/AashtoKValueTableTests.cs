using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Junction;

public class AashtoKValueTableTests
{
    [Theory]
    [InlineData(120, 57f, 95f)]
    [InlineData(100, 45f, 50f)]
    [InlineData(80, 32f, 30f)]
    [InlineData(50, 15f, 10f)]
    [InlineData(30, 4f, 3f)]
    public void GetKFromSpeed_ExactRowSpeeds_ReturnsExactRowValues(int speedKmh, float kSag, float kCrest)
    {
        Assert.Equal(kSag, AashtoKValueTable.GetKFromSpeed(speedKmh, isSag: true), 2);
        Assert.Equal(kCrest, AashtoKValueTable.GetKFromSpeed(speedKmh, isSag: false), 2);
    }

    [Fact]
    public void GetKFromSpeed_90Kmh_LinearlyInterpolatesPrimaryAndTrunk()
    {
        Assert.Equal(38.5f, AashtoKValueTable.GetKFromSpeed(90, isSag: true), 1);
        Assert.Equal(40f, AashtoKValueTable.GetKFromSpeed(90, isSag: false), 1);
    }

    [Fact]
    public void GetKFromSpeed_BelowMinSpeed_ClampsToResidential()
    {
        Assert.Equal(4f, AashtoKValueTable.GetKFromSpeed(10, isSag: true), 2);
        Assert.Equal(3f, AashtoKValueTable.GetKFromSpeed(10, isSag: false), 2);
    }

    [Fact]
    public void GetKFromSpeed_AboveMaxSpeed_ClampsToMotorway()
    {
        Assert.Equal(57f, AashtoKValueTable.GetKFromSpeed(200, isSag: true), 2);
        Assert.Equal(95f, AashtoKValueTable.GetKFromSpeed(200, isSag: false), 2);
    }

    [Theory]
    [InlineData("motorway", 57f, 95f)]
    [InlineData("motorway_link", 57f, 95f)]
    [InlineData("trunk", 45f, 50f)]
    [InlineData("primary", 32f, 30f)]
    [InlineData("secondary", 15f, 10f)]
    [InlineData("tertiary", 15f, 10f)]
    [InlineData("residential", 4f, 3f)]
    [InlineData("service", 4f, 3f)]
    public void GetKFromOsmRoadType_KnownTypes_MatchExpectedRow(string osmType, float kSag, float kCrest)
    {
        Assert.Equal(kSag, AashtoKValueTable.GetKFromOsmRoadType(osmType, isSag: true), 2);
        Assert.Equal(kCrest, AashtoKValueTable.GetKFromOsmRoadType(osmType, isSag: false), 2);
    }

    [Fact]
    public void GetKFromOsmRoadType_NullOrEmpty_FallsBackToResidentialRow()
    {
        Assert.Equal(4f, AashtoKValueTable.GetKFromOsmRoadType(null, isSag: true), 2);
        Assert.Equal(3f, AashtoKValueTable.GetKFromOsmRoadType("", isSag: false), 2);
    }

    [Fact]
    public void GetKFromOsmRoadType_CaseInsensitive()
    {
        Assert.Equal(57f, AashtoKValueTable.GetKFromOsmRoadType("MOTORWAY", isSag: true), 2);
    }

    [Fact]
    public void ResolveDesignSpeed_OsmTypePresent_OsmWinsOverMaterial()
    {
        var speed = AashtoKValueTable.ResolveDesignSpeed("motorway", materialOverrideKmh: 30);
        Assert.Equal(120, speed);
    }

    [Fact]
    public void ResolveDesignSpeed_OsmNull_MaterialOverrideUsed()
    {
        var speed = AashtoKValueTable.ResolveDesignSpeed(null, materialOverrideKmh: 70);
        Assert.Equal(70, speed);
    }

    [Fact]
    public void ResolveDesignSpeed_OsmEmpty_MaterialOverrideUsed()
    {
        var speed = AashtoKValueTable.ResolveDesignSpeed("", materialOverrideKmh: 70);
        Assert.Equal(70, speed);
    }

    [Fact]
    public void ResolveDesignSpeed_BothNull_ReturnsResidentialDefault()
    {
        var speed = AashtoKValueTable.ResolveDesignSpeed(null, materialOverrideKmh: null);
        Assert.Equal(30, speed);
    }

    [Fact]
    public void ComputeCap_SagAtMotorwaySpeed_Returns57TimesGradePercent()
    {
        var cap = AashtoKValueTable.ComputeCap(
            speedKmh: 120,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 105f, blendLength: 100f);
        Assert.Equal(285f, cap, 1); // 57 × 5
    }

    [Fact]
    public void ComputeCap_CrestAtMotorwaySpeed_Returns95TimesGradePercent()
    {
        var cap = AashtoKValueTable.ComputeCap(
            speedKmh: 120,
            zJunction: 100f, mJunction: 0.05f,
            zNaturalAtL: 100f, blendLength: 100f);
        Assert.Equal(475f, cap, 1); // 95 × 5
    }

    [Fact]
    public void ComputeCap_ZeroGradeDifference_ReturnsPositiveInfinity()
    {
        var cap = AashtoKValueTable.ComputeCap(
            speedKmh: 120,
            zJunction: 100f, mJunction: 0.02f,
            zNaturalAtL: 102f, blendLength: 100f);
        Assert.Equal(float.PositiveInfinity, cap);
    }

    [Fact]
    public void ComputeCap_ZeroBlendLength_ReturnsPositiveInfinity()
    {
        var cap = AashtoKValueTable.ComputeCap(
            speedKmh: 120,
            zJunction: 100f, mJunction: 0f,
            zNaturalAtL: 105f, blendLength: 0f);
        Assert.Equal(float.PositiveInfinity, cap);
    }
}
