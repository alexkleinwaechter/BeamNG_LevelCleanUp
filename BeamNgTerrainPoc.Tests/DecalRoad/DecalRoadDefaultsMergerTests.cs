using System.Text.Json.Nodes;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadDefaultsMergerTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void NewRoadTypeKey_IsAddedToUserFile()
    {
        var user = Parse("""{ "primary": { "name": "Primary", "layers": [] } }""");
        var current = Parse("""
            {
              "primary": { "name": "Primary", "layers": [] },
              "roundabout": { "name": "Roundabout", "layers": [] }
            }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.True(changed);
        Assert.Equal("Roundabout", user["roundabout"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void UserOnlyRoadTypeKey_IsKept()
    {
        var user = Parse("""
            {
              "primary": { "name": "Primary", "layers": [] },
              "myCustomType": { "name": "Custom", "layers": [] }
            }
            """);
        var current = Parse("""{ "primary": { "name": "Primary", "layers": [] } }""");

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.False(changed);
        Assert.NotNull(user["myCustomType"]);
    }

    [Fact]
    public void NewLayer_IsInsertedAtDefaultPosition()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 },
                { "name": "AIRoad", "width": 0 }
            ] } }
            """);
        var current = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 },
                { "name": "BridgeTunnelSurface", "width": 0 },
                { "name": "AIRoad", "width": 0 }
            ] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.True(changed);
        var layers = user["primary"]!["layers"]!.AsArray();
        Assert.Equal(3, layers.Count);
        Assert.Equal("BridgeTunnelSurface", layers[1]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void LayerDeletedByUser_IsNotResurrected_WhenBaselineKnowsIt()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 }
            ] } }
            """);
        var baseline = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 },
                { "name": "Cracks", "width": 0 }
            ] } }
            """);
        var current = baseline.DeepClone().AsObject();

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline, current);

        Assert.False(changed);
        Assert.Single(user["primary"]!["layers"]!.AsArray());
    }

    [Fact]
    public void NewField_IsAddedFromCodeDefaults()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 }
            ] } }
            """);
        var current = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25, "renderOnBridges": false }
            ] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.True(changed);
        Assert.False(user["primary"]!["layers"]![0]!["renderOnBridges"]!.GetValue<bool>());
    }

    [Fact]
    public void ChangedDefault_IsAdopted_WhenUserNeverOverwroteIt()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.5, "layers": [
                { "name": "EdgeLine", "material": "m_line_white" }
            ] } }
            """);
        var baseline = user.DeepClone().AsObject();
        var current = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.75, "layers": [
                { "name": "EdgeLine", "material": "m_line_white_new" }
            ] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline, current);

        Assert.True(changed);
        Assert.Equal(3.75, user["primary"]!["defaultLaneWidth"]!.GetValue<double>());
        Assert.Equal("m_line_white_new",
            user["primary"]!["layers"]![0]!["material"]!.GetValue<string>());
    }

    [Fact]
    public void ChangedDefault_IsIgnored_WhenUserOverwroteTheField()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 4.0, "layers": [] } }
            """);
        var baseline = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.5, "layers": [] } }
            """);
        var current = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.75, "layers": [] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline, current);

        Assert.False(changed);
        Assert.Equal(4.0, user["primary"]!["defaultLaneWidth"]!.GetValue<double>());
    }

    [Fact]
    public void ChangedDefault_IsIgnored_WithoutBaseline()
    {
        // Without a baseline we cannot tell user edits from stale defaults — keep the user value.
        var user = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.5, "layers": [] } }
            """);
        var current = Parse("""
            { "primary": { "name": "Primary", "defaultLaneWidth": 3.75, "layers": [] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.False(changed);
        Assert.Equal(3.5, user["primary"]!["defaultLaneWidth"]!.GetValue<double>());
    }

    [Fact]
    public void UserAddedLayer_IsKept()
    {
        var user = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 },
                { "name": "My Custom Layer", "width": 1.0 }
            ] } }
            """);
        var current = Parse("""
            { "primary": { "name": "Primary", "layers": [
                { "name": "EdgeLine", "width": 0.25 }
            ] } }
            """);

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline: null, current);

        Assert.False(changed);
        Assert.Equal(2, user["primary"]!["layers"]!.AsArray().Count);
    }

    [Fact]
    public void IdenticalTrees_ReportNoChange()
    {
        var current = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            DecalRoadDefaultLayerSets.GetDefaults(),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(
                    System.Text.Json.JsonNamingPolicy.CamelCase) }
            }))!.AsObject();
        var user = current.DeepClone().AsObject();
        var baseline = current.DeepClone().AsObject();

        var changed = DecalRoadDefaultsMerger.Merge(user, baseline, current);

        Assert.False(changed);
        Assert.True(JsonNode.DeepEquals(user, current));
    }
}
