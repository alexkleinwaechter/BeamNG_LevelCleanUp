namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;
using BeamNG.Procedural3D.Builders;

/// <summary>
/// A door that can be placed into a wall surface.
/// Strict port of OSM2World's Door.java.
///
/// Renders a rectangular door at the bottom of the wall, recessed by the inset distance.
/// Supports single and double wing doors.
/// Material depends on door type (hinged → entrance, overhead → garage, no → invisible).
/// </summary>
public class Door : IWallElement
{
    private readonly Vector2 _position;
    private readonly DoorParameters _params;
    private readonly List<Vector2> _outline;

    /// <summary>
    /// Creates a door at the given position in wall surface coordinates.
    /// Position is at the bottom-center of the door.
    /// </summary>
    public Door(Vector2 position, DoorParameters doorParams)
    {
        _position = position;
        _params = doorParams;

        // Build rectangular outline (same as Java Door.outline())
        float halfW = _params.Width / 2f;
        _outline = new List<Vector2>
        {
            _position + new Vector2(-halfW, 0),
            _position + new Vector2(+halfW, 0),
            _position + new Vector2(+halfW, _params.Height),
            _position + new Vector2(-halfW, _params.Height)
        };
    }

    public List<Vector2> Outline() => _outline;

    public float InsetDistance => _params.Inset;

    /// <summary>
    /// The material key for this door's surface.
    /// </summary>
    public string MaterialKey => _params.Material;

    /// <summary>
    /// Renders the door geometry into mesh builders.
    /// Port of Door.renderTo() in Java.
    /// </summary>
    public void Render(MeshBuilder wallBuilder, MeshBuilder elementBuilder, MeshBuilder? glassBuilder, WallSurface surface)
    {
        // "no" type doors are invisible cutouts (just the hole in the wall)
        if (_params.Type == "no") return;

        var normal = surface.GetWallNormal();
        var toBack = normal * (-_params.Inset);

        // Convert outline to 3D at the back (inset) depth
        var bl = surface.ConvertTo3D(_outline[0]) + toBack;
        var br = surface.ConvertTo3D(_outline[1]) + toBack;
        var tr = surface.ConvertTo3D(_outline[2]) + toBack;
        var tl = surface.ConvertTo3D(_outline[3]) + toBack;

        if (_params.NumberOfWings == 1 || _params.Type != "hinged")
        {
            // Single wing or non-hinged: one quad
            RenderQuad(elementBuilder, tl, bl, tr, br, normal);
        }
        else
        {
            // Double wing: two quads split at center
            var bottomCenter = Vector3.Lerp(bl, br, 0.5f);
            var topCenter = Vector3.Lerp(tl, tr, 0.5f);

            // Left wing
            RenderQuad(elementBuilder, tl, bl, topCenter, bottomCenter, normal);
            // Right wing (mirrored UVs)
            RenderQuadMirrored(elementBuilder, topCenter, bottomCenter, tr, br, normal);
        }
    }

    private static void RenderQuad(MeshBuilder builder,
        Vector3 topLeft, Vector3 bottomLeft, Vector3 topRight, Vector3 bottomRight,
        Vector3 normal)
    {
        var i0 = builder.AddVertex(topLeft, normal, new Vector2(0, 1));
        var i1 = builder.AddVertex(bottomLeft, normal, new Vector2(0, 0));
        var i2 = builder.AddVertex(topRight, normal, new Vector2(1, 1));
        var i3 = builder.AddVertex(bottomRight, normal, new Vector2(1, 0));

        builder.AddTriangle(i0, i1, i2);
        builder.AddTriangle(i2, i1, i3);
    }

    private static void RenderQuadMirrored(MeshBuilder builder,
        Vector3 topLeft, Vector3 bottomLeft, Vector3 topRight, Vector3 bottomRight,
        Vector3 normal)
    {
        // Mirrored UVs (1→0 instead of 0→1 horizontally)
        var i0 = builder.AddVertex(topLeft, normal, new Vector2(1, 1));
        var i1 = builder.AddVertex(bottomLeft, normal, new Vector2(1, 0));
        var i2 = builder.AddVertex(topRight, normal, new Vector2(0, 1));
        var i3 = builder.AddVertex(bottomRight, normal, new Vector2(0, 0));

        builder.AddTriangle(i0, i1, i2);
        builder.AddTriangle(i2, i1, i3);
    }
}
