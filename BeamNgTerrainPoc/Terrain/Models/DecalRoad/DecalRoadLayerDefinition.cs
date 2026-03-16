namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerDefinition
{
    public string Name { get; set; } = string.Empty;
    public DecalRoadLayerType LayerType { get; set; } = DecalRoadLayerType.Custom;
    public bool IsEnabled { get; set; } = true;
    public string Material { get; set; } = string.Empty;
    public float Width { get; set; } = 0.2f;
    public float TextureLength { get; set; } = 10.0f;
    public int RenderPriority { get; set; } = 10;
    public float Position { get; set; } // -1.0 = left edge, 0.0 = center, +1.0 = right edge
    public bool IsTrackWidth { get; set; }
    public bool IsLaneWidth { get; set; }
    public bool IsMirrored { get; set; }
    public bool IsPerLane { get; set; }
    public float FadeIn { get; set; } = 1.0f;
    public float FadeOut { get; set; } = 1.0f;
    public float[] DistanceFade { get; set; } = [1000f, 1500f];
    public bool InterruptAtJunctions { get; set; } = true;

    // AI Road properties (only relevant for LayerType == AIRoad)
    public float Drivability { get; set; } = -1.0f; // -1.0 = non-drivable, 1.0 = AI drivable
    public int LanesLeft { get; set; } = 1;
    public int LanesRight { get; set; } = 1;
    public bool OneWay { get; set; }
    public bool FlipDirection { get; set; }
}
