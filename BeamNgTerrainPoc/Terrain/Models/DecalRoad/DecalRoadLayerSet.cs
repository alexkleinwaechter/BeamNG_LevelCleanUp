namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class DecalRoadLayerSet
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int DefaultLaneCount { get; set; } = 2;
    public float DefaultLaneWidth { get; set; } = 3.5f;
    public bool EnablePerSegmentWidth { get; set; } = true;
    public bool UseOsmWidthTag { get; set; } = false;

    /// <summary>
    /// Combine parallel opposite-direction oneway ways (OSM dual carriageways, e.g. the two
    /// directions of a motorway) into ONE wider spline during OSM processing. Fixes the
    /// elevation drift between twin carriageways that shows as steps between parallel bridge
    /// decks — one centerline means one elevation solve. Takes effect only on full terrain
    /// regeneration (not on DecalRoad-only regeneration from a snapshot).
    /// </summary>
    public bool MergeSplinesLaterally { get; set; } = false;
    public float SmoothingCorridorMargin { get; set; } = 2.0f;
    public float MasterSplineMargin { get; set; } = 0.0f;
    public List<DecalRoadLayerDefinition> Layers { get; set; } = [];
}
