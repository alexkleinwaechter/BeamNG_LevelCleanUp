namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

public class WidthSegment
{
    public float StartDistance { get; set; }
    public float RoadSurfaceWidth { get; set; }
    public float SmoothingCorridorWidth { get; set; }
    public float MasterSplineWidth { get; set; }
    public int LaneCount { get; set; }
    public WidthSource Source { get; set; }
}

public enum WidthSource
{
    OsmWidthTagExact,
    OsmWidthTagEstimated,
    LaneCalculation,
    LayerSetDefault,
    ParameterFallback
}
