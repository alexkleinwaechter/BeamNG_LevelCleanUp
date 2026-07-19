namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public enum DecalRoadLayerType
{
    DirectionDivider,
    LaneMarking,
    EdgeLine,
    EdgeBlend,
    TreadMarks,
    AIRoad,
    Custom,

    /// <summary>
    ///     Full-width road surface decal (asphalt, dirt, …), rendered below all marking/wear layers.
    ///     Typically IsTrackWidth with Position 0. Combine with the render scope flags to give e.g.
    ///     bridge decks or tunnel stretches their own surface material.
    /// </summary>
    RoadSurface
}
