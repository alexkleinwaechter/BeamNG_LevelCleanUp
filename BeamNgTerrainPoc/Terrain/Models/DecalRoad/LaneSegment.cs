namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class LaneSegment
{
    public int StartPointIndex { get; set; }
    public float StartDistance { get; set; }
    public OsmLaneInfo LaneInfo { get; set; } = null!;
}
