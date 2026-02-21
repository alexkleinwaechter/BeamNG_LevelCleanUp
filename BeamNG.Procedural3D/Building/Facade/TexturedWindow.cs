namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;
using BeamNG.Procedural3D.Builders;

/// <summary>
/// A simple textured window quad for medium LOD rendering.
/// Port of OSM2World's TexturedWindow.java.
///
/// Renders a single rectangular quad (with window texture) recessed into the wall by 0.1m.
/// Used for LOD1 where full 3D window geometry (GeometryWindow) is too expensive.
///
/// The outline defines a rectangular hole in the wall surface.
/// The window quad is rendered at inset depth behind the wall plane.
/// </summary>
public class TexturedWindow : IWallElement
{
    private const float Depth = 0.10f;

    private readonly Vector2 _position;
    private readonly WindowParameters _params;
    private readonly List<Vector2> _outline;

    /// <summary>
    /// Creates a textured window at the given position in wall surface coordinates.
    /// </summary>
    /// <param name="position">Bottom-center of the window in wall surface coords (X=along wall, Y=height).</param>
    /// <param name="windowParams">Window parameters defining width, height.</param>
    public TexturedWindow(Vector2 position, WindowParameters windowParams)
    {
        _position = position;
        _params = windowParams;

        // Build rectangular outline
        float halfW = _params.OverallProperties.Width / 2f;
        float h = _params.OverallProperties.Height;
        _outline = new List<Vector2>
        {
            _position + new Vector2(-halfW, 0),
            _position + new Vector2(+halfW, 0),
            _position + new Vector2(+halfW, h),
            _position + new Vector2(-halfW, h)
        };
    }

    public List<Vector2> Outline() => _outline;

    public float InsetDistance => Depth;

    public string MaterialKey => "WINDOW_SINGLE";

    /// <summary>
    /// Renders a single textured quad at inset depth.
    /// Port of TexturedWindow.renderTo() in Java.
    /// </summary>
    public void Render(MeshBuilder wallBuilder, MeshBuilder elementBuilder, MeshBuilder? glassBuilder, WallSurface surface)
    {
        var outline = _outline;
        // Port: normalAt(outline().getCentroid()) — per-point normal at window center
        var centroid = WindowShape.GetCentroid(outline);
        var normal = surface.NormalAt(centroid);
        var toBack = normal * (-Depth);

        // Convert outline to 3D and shift to back (inset depth)
        var bl = surface.ConvertTo3D(outline[0]) + toBack;
        var br = surface.ConvertTo3D(outline[1]) + toBack;
        var tr = surface.ConvertTo3D(outline[2]) + toBack;
        var tl = surface.ConvertTo3D(outline[3]) + toBack;

        // Render as a single quad with fit-to-window UVs
        var i0 = elementBuilder.AddVertex(tl, normal, new Vector2(0, 1));
        var i1 = elementBuilder.AddVertex(bl, normal, new Vector2(0, 0));
        var i2 = elementBuilder.AddVertex(tr, normal, new Vector2(1, 1));
        var i3 = elementBuilder.AddVertex(br, normal, new Vector2(1, 0));

        // Triangle strip: TL, BL, TR, BR → two triangles
        elementBuilder.AddTriangle(i0, i1, i2);
        elementBuilder.AddTriangle(i2, i1, i3);
    }
}
