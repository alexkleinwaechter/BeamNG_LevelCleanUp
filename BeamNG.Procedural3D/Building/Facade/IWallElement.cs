namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;
using BeamNG.Procedural3D.Builders;

/// <summary>
/// Something that can be placed into a wall, such as a window or door.
/// Port of OSM2World's WallElement.java interface.
///
/// Wall elements define their 2D outline in wall surface coordinates:
/// - X = distance along wall (0 to wall length)
/// - Y = height above floor (0 to wall height)
///
/// The element is responsible for rendering its own geometry (frames, glass, etc.)
/// within the outline area. The wall surface triangulates around the element outline
/// to create the hole in the wall.
/// </summary>
public interface IWallElement
{
    /// <summary>
    /// Returns the space on the 2D wall surface occupied by this element.
    /// The outline is a closed polygon in wall surface coordinates.
    /// The wall surface will cut a hole matching this outline.
    /// </summary>
    List<Vector2> Outline();

    /// <summary>
    /// How deep the element is sunk into the wall. Used to render the bits of wall
    /// around the opening (the reveal/jamb faces).
    /// Can be 0 if the element is flat on the wall surface.
    /// </summary>
    float InsetDistance { get; }

    /// <summary>
    /// The material key for this element's rendered geometry.
    /// Used to route element geometry to the correct per-material MeshBuilder.
    /// </summary>
    string MaterialKey { get; }

    /// <summary>
    /// Renders the element geometry (frames, glass, door panels, etc.) into mesh builders.
    /// </summary>
    /// <param name="wallBuilder">Mesh builder for opaque wall-material geometry (e.g., inset reveal faces).</param>
    /// <param name="elementBuilder">Mesh builder for element-specific geometry (frames, door panels).</param>
    /// <param name="glassBuilder">Mesh builder for glass pane geometry (WINDOW_GLASS material). Null for LOD levels without separate glass.</param>
    /// <param name="surface">The wall surface for converting 2D → 3D coordinates.</param>
    void Render(MeshBuilder wallBuilder, MeshBuilder elementBuilder, MeshBuilder? glassBuilder, WallSurface surface);
}
