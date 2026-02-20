namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;

/// <summary>
/// Window shape types. Port of OSM2World's WindowParameters.WindowShape enum.
/// Each shape can build a 2D polygon outline at a given position and size.
/// </summary>
public enum WindowShapeType
{
    Rectangle,
    Circle,
    Triangle,
    Semicircle
}

/// <summary>
/// Builds 2D polygon outlines for different window shapes.
/// Port of WindowShape.buildShapeXZ() from OSM2World.
///
/// All shapes are centered horizontally at the given position.
/// The position is at the bottom-center of the window.
/// Polygons are returned in counter-clockwise order (open, no closing vertex).
/// </summary>
public static class WindowShape
{
    private const int CircleSegments = 16;

    /// <summary>
    /// Parses a window shape from a string tag value. Returns null if not recognized.
    /// Port of WindowShape.getValue() in Java.
    /// </summary>
    public static WindowShapeType? Parse(string? shapeName)
    {
        if (string.IsNullOrEmpty(shapeName)) return null;

        return shapeName.ToLowerInvariant() switch
        {
            "rectangle" => WindowShapeType.Rectangle,
            "circle" => WindowShapeType.Circle,
            "triangle" => WindowShapeType.Triangle,
            "semicircle" => WindowShapeType.Semicircle,
            _ => null
        };
    }

    /// <summary>
    /// Builds a 2D polygon outline for the given window shape.
    /// Position is at the bottom-center of the window.
    /// </summary>
    public static List<Vector2> BuildOutline(WindowShapeType shape, Vector2 position, float width, float height)
    {
        return shape switch
        {
            WindowShapeType.Rectangle => BuildRectangle(position, width, height),
            WindowShapeType.Circle => BuildCircle(position, width, height),
            WindowShapeType.Triangle => BuildTriangle(position, width, height),
            WindowShapeType.Semicircle => BuildSemicircle(position, width, height),
            _ => BuildRectangle(position, width, height)
        };
    }

    /// <summary>
    /// Builds a 2D polygon outline for a shape sitting on a base segment (used for window regions).
    /// Port of WindowShape.buildShapeXZ(LineSegmentXZ baseSegment, double height).
    /// The base segment defines the bottom edge; the shape extends upward by height.
    /// </summary>
    public static List<Vector2> BuildOutlineOnBase(WindowShapeType shape, Vector2 baseLeft, Vector2 baseRight, float height)
    {
        var center = (baseLeft + baseRight) / 2f;
        float baseWidth = Vector2.Distance(baseLeft, baseRight);

        return shape switch
        {
            WindowShapeType.Triangle => new List<Vector2>
            {
                baseRight,
                baseLeft,
                center + new Vector2(0, height)
            },
            WindowShapeType.Semicircle => BuildSemicircleOnBase(baseLeft, baseRight, height),
            _ => new List<Vector2>
            {
                baseLeft,
                baseRight,
                baseRight + new Vector2(0, height),
                baseLeft + new Vector2(0, height)
            }
        };
    }

    private static List<Vector2> BuildRectangle(Vector2 position, float width, float height)
    {
        float halfW = width / 2f;
        return new List<Vector2>
        {
            position + new Vector2(-halfW, 0),
            position + new Vector2(+halfW, 0),
            position + new Vector2(+halfW, height),
            position + new Vector2(-halfW, height)
        };
    }

    private static List<Vector2> BuildCircle(Vector2 position, float width, float height)
    {
        // Circle centered at position + (0, height/2), radius = max(height, width) / 2
        // Port of Java: new CircleXZ(position.add(0, height/2), max(height, width)/2)
        float radius = MathF.Max(height, width) / 2f;
        var center = position + new Vector2(0, height / 2f);
        var points = new List<Vector2>(CircleSegments);

        for (int i = 0; i < CircleSegments; i++)
        {
            float angle = 2f * MathF.PI * i / CircleSegments;
            points.Add(center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
        }

        return points;
    }

    private static List<Vector2> BuildTriangle(Vector2 position, float width, float height)
    {
        // Apex at top, base at bottom
        // Port of Java: new TriangleXZ(position.add(0, height), position.add(-width/2, 0), position.add(+width/2, 0))
        float halfW = width / 2f;
        return new List<Vector2>
        {
            position + new Vector2(-halfW, 0),
            position + new Vector2(+halfW, 0),
            position + new Vector2(0, height)
        };
    }

    private static List<Vector2> BuildSemicircle(Vector2 position, float width, float height)
    {
        // Half-circle: flat base at bottom, arc at top
        // Port of Java: CircularSectorXZ(-90°, 90°) scaled to height
        float halfW = width / 2f;
        var center = position;
        var points = new List<Vector2>();

        // Bottom-left to bottom-right (straight base)
        // Arc from right to left (top half)
        int arcSegments = CircleSegments / 2;
        for (int i = 0; i <= arcSegments; i++)
        {
            float t = (float)i / arcSegments;
            float angle = MathF.PI * t; // 0 to PI (right to left over top)
            float x = halfW * MathF.Cos(angle);
            float y = height * MathF.Sin(angle);
            points.Add(center + new Vector2(-x, y)); // negate x because cos(0)=right, we want CCW
        }

        return points;
    }

    private static List<Vector2> BuildSemicircleOnBase(Vector2 baseLeft, Vector2 baseRight, float height)
    {
        var center = (baseLeft + baseRight) / 2f;
        float halfW = Vector2.Distance(baseLeft, baseRight) / 2f;

        var points = new List<Vector2> { baseLeft };

        // Arc from left to right over top
        int arcSegments = CircleSegments / 2;
        for (int i = 1; i < arcSegments; i++)
        {
            float t = (float)i / arcSegments;
            float angle = MathF.PI * (1f - t); // PI to 0 (left to right over top)
            float x = halfW * MathF.Cos(angle);
            float y = height * MathF.Sin(angle);
            points.Add(center + new Vector2(x, y));
        }

        points.Add(baseRight);
        return points;
    }

    /// <summary>
    /// Gets the centroid of a polygon.
    /// </summary>
    public static Vector2 GetCentroid(IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count == 0) return Vector2.Zero;

        var sum = Vector2.Zero;
        foreach (var v in polygon)
            sum += v;
        return sum / polygon.Count;
    }

    /// <summary>
    /// Gets the diameter (max extent) of a polygon.
    /// </summary>
    public static float GetDiameter(IReadOnlyList<Vector2> polygon)
    {
        if (polygon.Count < 2) return 0;

        float maxDist = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            for (int j = i + 1; j < polygon.Count; j++)
            {
                float d = Vector2.Distance(polygon[i], polygon[j]);
                if (d > maxDist) maxDist = d;
            }
        }
        return maxDist;
    }

    /// <summary>
    /// Scales a polygon toward/away from its centroid.
    /// Port of SimpleClosedShapeXZ.scale(scaleFactor) in Java.
    /// </summary>
    public static List<Vector2> Scale(IReadOnlyList<Vector2> polygon, float scaleFactor)
    {
        var centroid = GetCentroid(polygon);
        var result = new List<Vector2>(polygon.Count);
        foreach (var v in polygon)
        {
            result.Add(centroid + (v - centroid) * scaleFactor);
        }
        return result;
    }

    /// <summary>
    /// Gets the axis-aligned bounding box of a polygon.
    /// Returns (minX, minY, maxX, maxY).
    /// </summary>
    public static (float minX, float minY, float maxX, float maxY) GetBoundingBox(IReadOnlyList<Vector2> polygon)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in polygon)
        {
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
        }
        return (minX, minY, maxX, maxY);
    }
}
