namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadSettings
{
    public bool Enabled { get; set; }
    public float NodeSpacingMeters { get; set; } = 2.0f;
    public float JunctionExclusionMarginMeters { get; set; } = 0.0f;
    public Dictionary<string, DecalRoadLayerSet> MaterialLayerSets { get; set; } = new();
    public Dictionary<string, DecalRoadLayerSet> OsmLayerSets { get; set; } = new();
}
