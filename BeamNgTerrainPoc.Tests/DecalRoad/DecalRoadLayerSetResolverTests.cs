using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadLayerSetResolverTests
{
    [Fact]
    public void OsmTypeOverride_TakesPrecedence()
    {
        var settings = new DecalRoadSettings
        {
            OsmLayerSets = { ["motorway"] = new DecalRoadLayerSet { Name = "Motorway Override" } },
            MaterialLayerSets = { ["Asphalt"] = new DecalRoadLayerSet { Name = "Asphalt Fallback" } }
        };
        var defaults = new Dictionary<string, DecalRoadLayerSet>
        {
            ["motorway"] = new() { Name = "Default Motorway" }
        };

        var result = DecalRoadLayerSetResolver.Resolve("motorway", "Asphalt", settings, defaults);

        Assert.NotNull(result);
        Assert.Equal("Motorway Override", result!.Name);
    }

    [Fact]
    public void MaterialFallback_WhenNoOsmOverride()
    {
        var settings = new DecalRoadSettings
        {
            MaterialLayerSets = { ["Asphalt"] = new DecalRoadLayerSet { Name = "Asphalt Material" } }
        };

        var result = DecalRoadLayerSetResolver.Resolve("residential", "Asphalt", settings, new Dictionary<string, DecalRoadLayerSet>());

        Assert.NotNull(result);
        Assert.Equal("Asphalt Material", result!.Name);
    }

    [Fact]
    public void AppDataDefaults_WhenNoProjectOverrides()
    {
        var settings = new DecalRoadSettings();
        var defaults = new Dictionary<string, DecalRoadLayerSet>
        {
            ["primary"] = new() { Name = "Default Primary" }
        };

        var result = DecalRoadLayerSetResolver.Resolve("primary", "Unknown", settings, defaults);

        Assert.NotNull(result);
        Assert.Equal("Default Primary", result!.Name);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var settings = new DecalRoadSettings();

        var result = DecalRoadLayerSetResolver.Resolve("footway", "GrassMaterial", settings, new Dictionary<string, DecalRoadLayerSet>());

        Assert.Null(result);
    }

    [Fact]
    public void NullOsmType_SkipsOsmLookup()
    {
        var settings = new DecalRoadSettings
        {
            MaterialLayerSets = { ["DirtRoad"] = new DecalRoadLayerSet { Name = "Dirt" } }
        };

        var result = DecalRoadLayerSetResolver.Resolve(null, "DirtRoad", settings, new Dictionary<string, DecalRoadLayerSet>());

        Assert.NotNull(result);
        Assert.Equal("Dirt", result!.Name);
    }

    [Fact]
    public void DisabledLayerSet_StillReturned()
    {
        var settings = new DecalRoadSettings
        {
            OsmLayerSets = { ["motorway"] = new DecalRoadLayerSet { Name = "MW", IsEnabled = false } }
        };

        var result = DecalRoadLayerSetResolver.Resolve("motorway", "Asphalt", settings, new Dictionary<string, DecalRoadLayerSet>());

        Assert.NotNull(result);
        Assert.False(result!.IsEnabled);
    }
}
