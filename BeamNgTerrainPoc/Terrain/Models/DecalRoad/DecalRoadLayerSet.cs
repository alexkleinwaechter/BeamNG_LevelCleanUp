namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerSet
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DefaultLaneCount { get; set; } = 2;
    public float DefaultLaneWidth { get; set; } = 3.5f;
    public List<DecalRoadLayerDefinition> Layers { get; set; } = [];
}
