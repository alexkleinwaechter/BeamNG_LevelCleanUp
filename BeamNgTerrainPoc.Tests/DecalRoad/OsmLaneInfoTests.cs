using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class OsmLaneInfoTests
{
    // Priority 1: lanes:forward + lanes:backward
    [Fact]
    public void TryParse_ForwardAndBackward_UsesDirectly()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "4", ["lanes:forward"] = "3", ["lanes:backward"] = "1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(4, info.TotalLanes);
        Assert.Equal(3, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
        Assert.False(info.IsOneWay);
    }

    // Priority 2: oneway=yes + lanes
    [Fact]
    public void TryParse_OnewayYes_AllForward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["oneway"] = "yes"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(3, info.TotalLanes);
        Assert.Equal(3, info.LanesForward);
        Assert.Equal(0, info.LanesBackward);
        Assert.True(info.IsOneWay);
    }

    // Priority 3: oneway=-1 + lanes
    [Fact]
    public void TryParse_OnewayReverse_AllBackward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "2", ["oneway"] = "-1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.TotalLanes);
        Assert.Equal(0, info.LanesForward);
        Assert.Equal(2, info.LanesBackward);
        Assert.True(info.IsOneWay);
    }

    // Priority 4: lanes:forward + lanes (no backward)
    [Fact]
    public void TryParse_ForwardAndTotal_ComputesBackward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["lanes:forward"] = "2"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 5: lanes:backward + lanes (no forward)
    [Fact]
    public void TryParse_BackwardAndTotal_ComputesForward()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "3", ["lanes:backward"] = "1"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 6: lanes only (two-way, even)
    [Fact]
    public void TryParse_LanesOnlyEven_EvenSplit()
    {
        var tags = new Dictionary<string, string> { ["lanes"] = "4" };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(2, info.LanesBackward);
        Assert.False(info.IsOneWay);
    }

    // Priority 6: lanes only (two-way, odd - extra to forward)
    [Fact]
    public void TryParse_LanesOnlyOdd_ExtraToForward()
    {
        var tags = new Dictionary<string, string> { ["lanes"] = "3" };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal(2, info.LanesForward);
        Assert.Equal(1, info.LanesBackward);
    }

    // Priority 7: no lane tags
    [Fact]
    public void TryParse_NoLaneTags_ReturnsNull()
    {
        var tags = new Dictionary<string, string> { ["highway"] = "residential" };
        Assert.Null(OsmLaneInfo.TryParse(tags));
    }

    [Fact]
    public void TryParse_EmptyTags_ReturnsNull()
    {
        Assert.Null(OsmLaneInfo.TryParse(new Dictionary<string, string>()));
    }

    // Future-use fields parsed
    [Fact]
    public void TryParse_ParsesFutureFields()
    {
        var tags = new Dictionary<string, string>
        {
            ["lanes"] = "2",
            ["turn:lanes:forward"] = "left|through",
            ["turn:lanes:backward"] = "through|right",
            ["maxspeed"] = "50",
            ["surface"] = "asphalt"
        };
        var info = OsmLaneInfo.TryParse(tags);
        Assert.NotNull(info);
        Assert.Equal("left|through", info.TurnLanesForward);
        Assert.Equal("through|right", info.TurnLanesBackward);
        Assert.Equal("50", info.MaxSpeed);
        Assert.Equal("asphalt", info.Surface);
    }

    [Fact]
    public void Reversed_SwapsForwardBackward()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 4, LanesForward = 3, LanesBackward = 1,
            IsOneWay = false,
            TurnLanesForward = "left|through|right",
            TurnLanesBackward = "through"
        };

        var reversed = info.Reversed();

        Assert.Equal(4, reversed.TotalLanes);
        Assert.Equal(1, reversed.LanesForward);
        Assert.Equal(3, reversed.LanesBackward);
        Assert.False(reversed.IsOneWay);
        Assert.Equal("through", reversed.TurnLanesForward);
        Assert.Equal("left|through|right", reversed.TurnLanesBackward);
    }

    [Fact]
    public void Reversed_Twice_ReturnsOriginalValues()
    {
        var info = new OsmLaneInfo
        {
            TotalLanes = 3, LanesForward = 2, LanesBackward = 1, IsOneWay = false
        };
        var roundTrip = info.Reversed().Reversed();

        Assert.Equal(info.TotalLanes, roundTrip.TotalLanes);
        Assert.Equal(info.LanesForward, roundTrip.LanesForward);
        Assert.Equal(info.LanesBackward, roundTrip.LanesBackward);
    }
}
