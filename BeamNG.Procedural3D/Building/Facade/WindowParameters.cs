namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;

/// <summary>
/// Parametric description of a window. Immutable after construction.
/// Strict port of OSM2World's WindowParameters.java.
///
/// Supports:
/// - 4 window shapes (Rectangle, Circle, Triangle, Semicircle)
/// - Pane layout (NxM grid or radial)
/// - Window regions (CENTER + TOP for composed shapes like arch-topped windows)
/// - Dimensions: width, height, breast (sill height)
/// - Materials: frame, glass
///
/// Skipped for v1: shutters (can be added later by porting shutter code from GeometryWindow.java)
/// </summary>
public class WindowParameters
{
    private const float DefaultWidth = 1.0f;
    private const float DefaultHeightRelativeToLevel = 0.5f;
    private const float DefaultBreastRelativeToLevel = 0.3f;

    /// <summary>
    /// Sill height — distance from floor level to bottom of window.
    /// Port of Java: breast = parseMeasure(tags.getValue("window:breast"), DEFAULT_BREAST * levelHeight)
    /// </summary>
    public float Breast { get; }

    /// <summary>
    /// Overall window properties (width, height, shape, panes).
    /// </summary>
    public RegionProperties OverallProperties { get; }

    /// <summary>
    /// Per-region properties (CENTER, TOP, etc.) for composed window shapes.
    /// </summary>
    public IReadOnlyDictionary<WindowRegion, RegionProperties> Regions { get; }

    /// <summary>
    /// Explicit window count for this wall (from window:count tag), or null for auto-calculation.
    /// </summary>
    public int? NumberWindows { get; }

    /// <summary>
    /// Material identifier for the window frame.
    /// </summary>
    public string FrameMaterial { get; init; } = "WINDOW_FRAME";

    /// <summary>
    /// Material identifier for the window glass.
    /// </summary>
    public string GlassMaterial { get; init; } = "WINDOW_GLASS";

    /// <summary>
    /// Creates window parameters with default values scaled to the given level height.
    /// Port of WindowParameters(TagSet tags, double levelHeight, O2WConfig config) constructor.
    /// </summary>
    public WindowParameters(float levelHeight,
        float? width = null,
        float? height = null,
        float? breast = null,
        WindowShapeType? shape = null,
        PaneLayout? panes = null,
        WindowShapeType? topShape = null,
        float? topHeight = null,
        int? numberWindows = null)
    {
        float w = width ?? DefaultWidth;
        float h = height ?? DefaultHeightRelativeToLevel * levelHeight;
        Breast = breast ?? DefaultBreastRelativeToLevel * levelHeight;
        NumberWindows = numberWindows;

        var mainShape = shape ?? WindowShapeType.Rectangle;

        // Determine if we have regions
        if (topShape != null)
        {
            // Multi-region: CENTER + TOP
            float centerHeight = h;
            float tHeight = topHeight ?? h * 0.4f;

            var regions = new Dictionary<WindowRegion, RegionProperties>
            {
                [WindowRegion.Center] = new(w, centerHeight, mainShape, panes),
                [WindowRegion.Top] = new(w, tHeight, topShape.Value, null)
            };
            Regions = regions;

            // Overall dimensions span both regions
            OverallProperties = new RegionProperties(w, centerHeight + tHeight, mainShape, panes);
        }
        else
        {
            // Single region: CENTER only
            var regions = new Dictionary<WindowRegion, RegionProperties>
            {
                [WindowRegion.Center] = new(w, h, mainShape, panes)
            };
            Regions = regions;
            OverallProperties = new RegionProperties(w, h, mainShape, panes);
        }
    }

    /// <summary>
    /// Creates simple rectangular window parameters with the given dimensions.
    /// </summary>
    public static WindowParameters Simple(float width, float height, float breast)
    {
        return new WindowParameters(
            levelHeight: height / DefaultHeightRelativeToLevel,
            width: width,
            height: height,
            breast: breast);
    }
}

/// <summary>
/// Window region identifiers for composed window shapes.
/// Port of WindowParameters.WindowRegion enum.
/// </summary>
public enum WindowRegion
{
    Center,
    Top,
    Left,
    Right,
    Bottom
}

/// <summary>
/// Properties for a single region of a window (or the overall window).
/// Port of WindowParameters.RegionProperties class.
/// </summary>
public class RegionProperties
{
    public float Width { get; }
    public float Height { get; }
    public WindowShapeType Shape { get; }
    public PaneLayout? Panes { get; }

    public RegionProperties(float width, float height, WindowShapeType shape, PaneLayout? panes)
    {
        Width = width;
        Height = height;
        Shape = shape;
        Panes = panes;
    }
}

/// <summary>
/// Pane layout defining how a window is subdivided into panes.
/// Port of WindowParameters.PaneLayout class.
/// </summary>
public class PaneLayout
{
    public int PanesHorizontal { get; }
    public int PanesVertical { get; }
    public bool RadialPanes { get; }

    public PaneLayout(int panesHorizontal, int panesVertical, bool radialPanes = false)
    {
        if (panesHorizontal <= 0) throw new ArgumentOutOfRangeException(nameof(panesHorizontal));
        if (panesVertical <= 0) throw new ArgumentOutOfRangeException(nameof(panesVertical));

        PanesHorizontal = panesHorizontal;
        PanesVertical = panesVertical;
        RadialPanes = radialPanes;
    }

    /// <summary>
    /// Parses a pane layout from a string like "3x2".
    /// Returns null if the string is not a valid pane layout.
    /// </summary>
    public static PaneLayout? Parse(string? value, bool radial = false)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var parts = value.Split('x');
        if (parts.Length != 2) return null;

        if (int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int v) && h > 0 && v > 0)
            return new PaneLayout(h, v, radial);

        return null;
    }
}
