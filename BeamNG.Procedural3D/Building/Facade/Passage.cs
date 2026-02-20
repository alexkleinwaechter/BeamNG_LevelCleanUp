namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;
using BeamNG.Procedural3D.Builders;

/// <summary>
/// A building passage (full-height or clearance-height opening in a wall).
/// Port of OSM2World's building passage handling (BuildingPart.java lines 152-267).
///
/// Placed where a road/path tagged with tunnel=building_passage shares nodes
/// with the building outline. The opening is centered on the shared node position
/// and extends to the passage width (derived from road width).
///
/// Unlike doors, passages have no rendered geometry — they are pure cutouts
/// in the wall surface, with inset reveal faces on the sides.
/// </summary>
public class Passage : IWallElement
{
    /// <summary>
    /// Default clearance height above the passage (meters).
    /// Port of OSM2World BuildingPart.java: clearingAbovePassage = 2.5.
    /// </summary>
    public const float DEFAULT_CLEARANCE = 2.5f;

    private readonly List<Vector2> _outline;
    private readonly float _insetDistance;

    /// <summary>
    /// Creates a passage opening at the given position in wall surface coordinates.
    /// </summary>
    /// <param name="position">Center-bottom of the passage opening in wall surface coords
    /// (X = distance along wall, Y = height above floor).</param>
    /// <param name="width">Width of the passage in meters (from road width).</param>
    /// <param name="height">Height of the passage in meters (clearance height).</param>
    /// <param name="wallThickness">Wall thickness for inset reveal depth. 0 = no reveal.</param>
    public Passage(Vector2 position, float width, float height, float wallThickness = 0.3f)
    {
        _insetDistance = wallThickness;

        float halfW = width / 2f;
        _outline = new List<Vector2>
        {
            position + new Vector2(-halfW, 0),
            position + new Vector2(+halfW, 0),
            position + new Vector2(+halfW, height),
            position + new Vector2(-halfW, height)
        };
    }

    public List<Vector2> Outline() => _outline;

    public float InsetDistance => _insetDistance;

    /// <summary>
    /// Passages render no geometry — they are pure wall cutouts.
    /// The inset reveal/jamb faces are handled by WallSurface.RenderInsetFaces().
    /// </summary>
    public void Render(MeshBuilder wallBuilder, MeshBuilder elementBuilder, MeshBuilder? glassBuilder, WallSurface surface)
    {
        // No rendered geometry — passage is an open hole through the building.
    }
}
