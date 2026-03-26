namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerSet
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DefaultLaneCount { get; set; } = 2;
    public float DefaultLaneWidth { get; set; } = 3.5f;
    public bool EnablePerSegmentWidth { get; set; } = true;
    public bool UseOsmWidthTag { get; set; } = false;
    public float SmoothingCorridorMargin { get; set; } = 2.0f;
    public float MasterSplineMargin { get; set; } = 0.0f;
    public List<DecalRoadLayerDefinition> Layers { get; set; } = [];
}
