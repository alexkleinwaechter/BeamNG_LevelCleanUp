namespace BeamNG.Procedural3D.Building.Facade;

using System.Numerics;
using BeamNG.Procedural3D.Builders;

/// <summary>
/// Full 3D window geometry with frames, glass panes, and inner frame dividers.
/// Strict port of OSM2World's GeometryWindow.java (408 lines).
///
/// Supports all 4 window shapes (Rectangle, Circle, Triangle, Semicircle)
/// and multi-region windows (CENTER + TOP for arch-topped windows).
///
/// Geometry components:
/// 1. Glass pane — triangulated from pane outline, shifted to DEPTH inset
/// 2. Outer frame front face — ring between outline and pane outline
/// 3. Outer frame side — triangle strip connecting frame front to pane depth
/// 4. Inner frames — extruded rectangles along pane divider paths
///
/// Skipped for v1: shutters (GeometryWindow.java lines 344-403)
/// </summary>
public class GeometryWindow : IWallElement
{
    private const float Depth = 0.10f;
    private const float OuterFrameWidth = 0.1f;
    private const float InnerFrameWidth = 0.05f;
    private const float OuterFrameThickness = 0.05f;
    private const float InnerFrameThickness = 0.03f;

    private readonly WindowParameters _params;
    private readonly List<Vector2> _outline;
    private readonly List<Vector2> _paneOutline;
    private readonly List<Vector2> _frameInnerOutline;
    private readonly List<(Vector2 start, Vector2 end)> _innerFramePaths;

    /// <summary>
    /// Creates a geometry window at the given position in wall surface coordinates.
    /// Port of GeometryWindow constructor (lines 57-165).
    /// </summary>
    /// <param name="position">Bottom-center of the window in wall surface coords.</param>
    /// <param name="windowParams">Window parameters defining shape, size, panes.</param>
    public GeometryWindow(Vector2 position, WindowParameters windowParams)
    {
        _params = windowParams;

        // Build the outline from shape + regions
        bool useRegions = windowParams.Regions.ContainsKey(WindowRegion.Center)
                       && windowParams.Regions.ContainsKey(WindowRegion.Top);

        if (!useRegions)
        {
            _outline = WindowShape.BuildOutline(
                windowParams.OverallProperties.Shape,
                position,
                windowParams.OverallProperties.Width,
                windowParams.OverallProperties.Height);
        }
        else
        {
            _outline = BuildRegionOutline(position, windowParams);
        }

        // Calculate the pane outline (glass area inside the frame)
        // Port of GeometryWindow.paneOutlineFromOutline() line 167-171:
        //   scaleFactor = max(0.1, 1 - INNER_FRAME_WIDTH / outline.getDiameter())
        float diameter = WindowShape.GetDiameter(_outline);
        float paneScaleFactor = MathF.Max(0.1f, 1f - InnerFrameWidth / diameter);
        _paneOutline = WindowShape.Scale(_outline, paneScaleFactor);

        // Calculate the frame inner outline (inner edge of outer frame front face)
        // Port of: JTSBufferUtil.bufferPolygon(outlinePolygon, -OUTER_FRAME_WIDTH)
        // We approximate polygon buffering with centroid scaling (same approach as paneOutline)
        float frameScaleFactor = MathF.Max(0.1f, 1f - OuterFrameWidth / diameter);
        _frameInnerOutline = WindowShape.Scale(_outline, frameScaleFactor);

        // Calculate inner frame paths (pane divider lines)
        _innerFramePaths = [];

        if (windowParams.OverallProperties.Panes != null)
        {
            var panes = windowParams.OverallProperties.Panes;
            _innerFramePaths = panes.RadialPanes
                ? BuildRadialPanePaths(_paneOutline, panes.PanesHorizontal, panes.PanesVertical)
                : BuildGridPanePaths(_paneOutline, panes.PanesHorizontal, panes.PanesVertical);
        }
        else if (useRegions)
        {
            // Add pane paths for each region that has panes
            foreach (var (region, props) in windowParams.Regions)
            {
                if (props.Panes == null) continue;

                var regionOutline = WindowShape.BuildOutline(props.Shape,
                    position + GetRegionOffset(region, windowParams),
                    props.Width, props.Height);
                var regionPaneOutline = WindowShape.Scale(regionOutline,
                    MathF.Max(0.1f, 1f - InnerFrameWidth / WindowShape.GetDiameter(regionOutline)));

                _innerFramePaths.AddRange(props.Panes.RadialPanes
                    ? BuildRadialPanePaths(regionPaneOutline, props.Panes.PanesHorizontal, props.Panes.PanesVertical)
                    : BuildGridPanePaths(regionPaneOutline, props.Panes.PanesHorizontal, props.Panes.PanesVertical));
            }
        }
    }

    public List<Vector2> Outline() => _outline;

    public float InsetDistance => Depth - OuterFrameThickness;

    public string MaterialKey => "WINDOW_FRAME";

    /// <summary>
    /// Renders the full window geometry: pane, outer frame, inner frames.
    /// Port of GeometryWindow.renderTo() lines 256-342.
    ///
    /// Glass pane renders to glassBuilder (WINDOW_GLASS material) when provided,
    /// matching OSM2World's separate glass/frame material approach.
    /// Frame geometry (outer frame, inner dividers) renders to elementBuilder (WINDOW_FRAME material).
    /// </summary>
    public void Render(MeshBuilder wallBuilder, MeshBuilder elementBuilder, MeshBuilder? glassBuilder, WallSurface surface)
    {
        // Port: normalAt(outline().getCentroid()) — per-point normal at window center
        var centroid = WindowShape.GetCentroid(_outline);
        var normal = surface.NormalAt(centroid);
        var toBack = normal * (-Depth);
        var toOuterFrame = normal * (-Depth + OuterFrameThickness);

        // 1. Glass pane — triangulated pane outline at full depth
        // Port of OSM2World: target.drawTriangles(params.opaqueWindowMaterial(), paneTriangles, ...)
        RenderPane(glassBuilder ?? elementBuilder, surface, normal, toBack);

        // 2. Outer frame front face — ring between outline and pane outline
        RenderOuterFrameFront(elementBuilder, surface, normal, toOuterFrame);

        // 3. Outer frame side — strip connecting frame front to pane depth
        RenderOuterFrameSide(elementBuilder, surface, normal, toOuterFrame, toBack);

        // 4. Inner frames — extruded along pane divider paths
        RenderInnerFrames(elementBuilder, surface, normal, toBack);
    }

    /// <summary>
    /// Renders the glass pane: triangulate pane outline, shift to DEPTH.
    /// </summary>
    private void RenderPane(MeshBuilder builder, WallSurface surface, Vector3 normal, Vector3 toBack)
    {
        var indices = PolygonTriangulator.Triangulate(_paneOutline);
        if (indices.Count < 3) return;

        int baseIdx = builder.VertexCount;
        foreach (var v in _paneOutline)
        {
            var pos3D = surface.ConvertTo3D(v) + toBack;
            builder.AddVertex(pos3D, normal, v); // UV = wall surface coords
        }

        for (int i = 0; i < indices.Count; i += 3)
        {
            builder.AddTriangle(
                baseIdx + indices[i],
                baseIdx + indices[i + 1],
                baseIdx + indices[i + 2]);
        }
    }

    /// <summary>
    /// Renders the outer frame front face: ring between outline and frame inner outline.
    /// Port of GeometryWindow.renderTo() lines 296-309 (outer frame front).
    /// In OSM2World: triangulate(outlinePolygon, JTSBufferUtil.bufferPolygon(outline, -OUTER_FRAME_WIDTH))
    /// We approximate the JTS buffer with centroid scaling via _frameInnerOutline.
    /// </summary>
    private void RenderOuterFrameFront(MeshBuilder builder, WallSurface surface, Vector3 normal, Vector3 toOuterFrame)
    {
        // Triangulate the ring (outline with frame inner outline as hole)
        var indices = PolygonTriangulator.Triangulate(_outline,
            new List<IReadOnlyList<Vector2>> { ReversedCopy(_frameInnerOutline) });
        if (indices.Count < 3) return;

        // Build flattened vertex list: outline + reversed frame inner outline
        var allVerts = new List<Vector2>(_outline);
        allVerts.AddRange(ReversedCopy(_frameInnerOutline));

        int baseIdx = builder.VertexCount;
        foreach (var v in allVerts)
        {
            var pos3D = surface.ConvertTo3D(v) + toOuterFrame;
            builder.AddVertex(pos3D, normal, v);
        }

        for (int i = 0; i < indices.Count; i += 3)
        {
            builder.AddTriangle(
                baseIdx + indices[i],
                baseIdx + indices[i + 1],
                baseIdx + indices[i + 2]);
        }
    }

    /// <summary>
    /// Renders the outer frame side: triangle strip connecting frame front edge to pane depth.
    /// Port of GeometryWindow.renderTo() lines 316-323 (frame side strip).
    /// In OSM2World: createTriangleStripBetween(innerOutline + toOuterFrame, innerOutline + toBack)
    /// where innerOutline = JTSBufferUtil.bufferPolygon(outline, -OUTER_FRAME_WIDTH).
    /// We use _frameInnerOutline as our approximation of the JTS buffer.
    /// </summary>
    private void RenderOuterFrameSide(MeshBuilder builder, WallSurface surface,
        Vector3 normal, Vector3 toOuterFrame, Vector3 toBack)
    {
        // Create triangle strip between frame inner outline at frame depth and at full depth
        for (int i = 0; i < _frameInnerOutline.Count; i++)
        {
            int next = (i + 1) % _frameInnerOutline.Count;

            var frontA = surface.ConvertTo3D(_frameInnerOutline[i]) + toOuterFrame;
            var frontB = surface.ConvertTo3D(_frameInnerOutline[next]) + toOuterFrame;
            var backA = surface.ConvertTo3D(_frameInnerOutline[i]) + toBack;
            var backB = surface.ConvertTo3D(_frameInnerOutline[next]) + toBack;

            // Calculate side face normal pointing inward toward window center.
            // Cross(edgeDir, depthDir) gives inward normal for CCW outline.
            var edgeDir = Vector3.Normalize(frontB - frontA);
            var depthDir = Vector3.Normalize(backA - frontA);
            var sideNormal = Vector3.Normalize(Vector3.Cross(edgeDir, depthDir));

            var i0 = builder.AddVertex(frontA, sideNormal, Vector2.Zero);
            var i1 = builder.AddVertex(backA, sideNormal, new Vector2(0, 1));
            var i2 = builder.AddVertex(frontB, sideNormal, new Vector2(1, 0));
            var i3 = builder.AddVertex(backB, sideNormal, new Vector2(1, 1));

            // Flipped winding to match inward-facing normal
            builder.AddTriangle(i0, i2, i1);
            builder.AddTriangle(i2, i3, i1);
        }
    }

    /// <summary>
    /// Renders inner frame dividers as extruded rectangles along pane border paths.
    /// Port of GeometryWindow.renderTo() lines 325-342 (inner frame extrusion).
    /// </summary>
    private void RenderInnerFrames(MeshBuilder builder, WallSurface surface, Vector3 normal, Vector3 toBack)
    {
        foreach (var (start, end) in _innerFramePaths)
        {
            var start3D = surface.ConvertTo3D(start) + toBack;
            var end3D = surface.ConvertTo3D(end) + toBack;

            // Extrude a small rectangle along the path
            var pathDir = Vector3.Normalize(end3D - start3D);
            var up = Vector3.Normalize(Vector3.Cross(pathDir, normal));

            float halfW = InnerFrameWidth / 2f;
            float halfT = InnerFrameThickness / 2f;

            // 4 corners of the rectangle cross-section at start and end
            var startVerts = new Vector3[4];
            var endVerts = new Vector3[4];

            startVerts[0] = start3D + up * halfW + normal * halfT;  // top-front
            startVerts[1] = start3D + up * halfW - normal * halfT;  // top-back
            startVerts[2] = start3D - up * halfW - normal * halfT;  // bottom-back
            startVerts[3] = start3D - up * halfW + normal * halfT;  // bottom-front

            endVerts[0] = end3D + up * halfW + normal * halfT;
            endVerts[1] = end3D + up * halfW - normal * halfT;
            endVerts[2] = end3D - up * halfW - normal * halfT;
            endVerts[3] = end3D - up * halfW + normal * halfT;

            // Render 4 side faces of the extruded rectangle
            for (int face = 0; face < 4; face++)
            {
                int nextFace = (face + 1) % 4;
                var faceNormal = Vector3.Normalize(
                    Vector3.Cross(endVerts[face] - startVerts[face],
                                  startVerts[nextFace] - startVerts[face]));

                var vi0 = builder.AddVertex(startVerts[face], faceNormal, Vector2.Zero);
                var vi1 = builder.AddVertex(startVerts[nextFace], faceNormal, Vector2.Zero);
                var vi2 = builder.AddVertex(endVerts[face], faceNormal, Vector2.Zero);
                var vi3 = builder.AddVertex(endVerts[nextFace], faceNormal, Vector2.Zero);

                builder.AddTriangle(vi0, vi1, vi2);
                builder.AddTriangle(vi2, vi1, vi3);
            }
        }
    }

    // ==========================================
    // Outline construction helpers
    // ==========================================

    /// <summary>
    /// Builds a combined outline from CENTER + TOP regions.
    /// Port of GeometryWindow constructor lines 75-116.
    /// </summary>
    private static List<Vector2> BuildRegionOutline(Vector2 position, WindowParameters windowParams)
    {
        var centerProps = windowParams.Regions[WindowRegion.Center];
        var topProps = windowParams.Regions[WindowRegion.Top];

        // Build center outline
        var centerOutline = WindowShape.BuildOutline(centerProps.Shape, position,
            centerProps.Width, centerProps.Height);

        // Find the top edge of the center outline
        var (_, _, _, maxY) = WindowShape.GetBoundingBox(centerOutline);
        var centroid = WindowShape.GetCentroid(centerOutline);

        // Top segment: the horizontal line at the top of center region
        var topSegLeft = new Vector2(centroid.X - centerProps.Width / 2f, maxY);
        var topSegRight = new Vector2(centroid.X + centerProps.Width / 2f, maxY);

        // Build top region shape on the top segment
        var topOutline = WindowShape.BuildOutlineOnBase(topProps.Shape,
            topSegLeft, topSegRight, topProps.Height);

        // Combine: walk center outline CCW from topSegRight, then top outline from topSegLeft to topSegRight
        var combined = new List<Vector2>();

        // Find indices in center outline closest to top segment endpoints
        int rightIdx = FindClosestIndex(centerOutline, topSegRight);
        int leftIdx = FindClosestIndex(centerOutline, topSegLeft);

        // Walk center from right to left (the bottom half)
        int n = centerOutline.Count;
        for (int i = rightIdx; ; i = (i + 1) % n)
        {
            combined.Add(centerOutline[i]);
            if (i == leftIdx) break;
        }

        // Walk top outline (skip first and last if they duplicate center endpoints)
        for (int i = 0; i < topOutline.Count; i++)
        {
            var v = topOutline[i];
            if (i == 0 && Vector2.Distance(v, topSegLeft) < 0.01f) continue;
            if (i == topOutline.Count - 1 && Vector2.Distance(v, topSegRight) < 0.01f) continue;
            combined.Add(v);
        }

        return combined;
    }

    private static Vector2 GetRegionOffset(WindowRegion region, WindowParameters windowParams)
    {
        if (region == WindowRegion.Top && windowParams.Regions.ContainsKey(WindowRegion.Center))
        {
            return new Vector2(0, windowParams.Regions[WindowRegion.Center].Height);
        }
        return Vector2.Zero;
    }

    // ==========================================
    // Pane border path construction
    // ==========================================

    /// <summary>
    /// Builds grid-pattern inner frame paths.
    /// Port of GeometryWindow.innerPaneBorderPaths() lines 173-205.
    /// </summary>
    private static List<(Vector2 start, Vector2 end)> BuildGridPanePaths(
        IReadOnlyList<Vector2> paneOutline, int panesHorizontal, int panesVertical)
    {
        var result = new List<(Vector2, Vector2)>();
        var (minX, minY, maxX, maxY) = WindowShape.GetBoundingBox(paneOutline);
        var center = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
        float sizeX = maxX - minX;
        float sizeY = maxY - minY;

        // Horizontal dividers (between vertical panes)
        for (int i = 0; i < panesVertical - 1; i++)
        {
            float t = (i + 1f) / panesVertical;
            float y = minY + t * sizeY;
            // Find intersections with outline at this y level
            var left = new Vector2(minX - sizeX, y);
            var right = new Vector2(maxX + sizeX, y);
            var intersections = FindLineIntersections(paneOutline, y, true);
            if (intersections.Count >= 2)
            {
                intersections.Sort((a, b) => a.X.CompareTo(b.X));
                result.Add((intersections[0], intersections[^1]));
            }
        }

        // Vertical dividers (between horizontal panes)
        for (int i = 0; i < panesHorizontal - 1; i++)
        {
            float t = (i + 1f) / panesHorizontal;
            float x = minX + t * sizeX;
            var intersections = FindLineIntersections(paneOutline, x, false);
            if (intersections.Count >= 2)
            {
                intersections.Sort((a, b) => a.Y.CompareTo(b.Y));
                result.Add((intersections[0], intersections[^1]));
            }
        }

        return result;
    }

    /// <summary>
    /// Builds radial inner frame paths from center outward.
    /// Port of GeometryWindow.innerPaneBorderPathsRadial() lines 208-243.
    /// </summary>
    private static List<(Vector2 start, Vector2 end)> BuildRadialPanePaths(
        IReadOnlyList<Vector2> paneOutline, int panesHorizontal, int panesVertical)
    {
        var result = new List<(Vector2, Vector2)>();
        var center = WindowShape.GetCentroid(paneOutline);
        float diameter = WindowShape.GetDiameter(paneOutline);

        for (int i = 0; i < panesHorizontal; i++)
        {
            float angle = 2f * MathF.PI * i / panesHorizontal;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var farPoint = center + direction * diameter;

            // Find intersection with outline
            var intersections = FindRayIntersections(paneOutline, center, farPoint);
            if (intersections.Count > 0)
            {
                // Use closest intersection
                var closest = intersections.OrderBy(p => Vector2.Distance(center, p)).First();
                result.Add((center, closest));
            }
        }

        return result;
    }

    // ==========================================
    // Geometry helpers
    // ==========================================

    /// <summary>
    /// Finds intersections of a horizontal (isHorizontal=true) or vertical line with a polygon.
    /// </summary>
    private static List<Vector2> FindLineIntersections(IReadOnlyList<Vector2> polygon, float value, bool isHorizontal)
    {
        var intersections = new List<Vector2>();

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            var a = polygon[i];
            var b = polygon[next];

            if (isHorizontal)
            {
                // Horizontal line at y = value
                if ((a.Y <= value && b.Y >= value) || (a.Y >= value && b.Y <= value))
                {
                    if (MathF.Abs(b.Y - a.Y) < 1e-8f) continue;
                    float t = (value - a.Y) / (b.Y - a.Y);
                    if (t >= 0 && t <= 1)
                    {
                        intersections.Add(new Vector2(a.X + t * (b.X - a.X), value));
                    }
                }
            }
            else
            {
                // Vertical line at x = value
                if ((a.X <= value && b.X >= value) || (a.X >= value && b.X <= value))
                {
                    if (MathF.Abs(b.X - a.X) < 1e-8f) continue;
                    float t = (value - a.X) / (b.X - a.X);
                    if (t >= 0 && t <= 1)
                    {
                        intersections.Add(new Vector2(value, a.Y + t * (b.Y - a.Y)));
                    }
                }
            }
        }

        return intersections;
    }

    /// <summary>
    /// Finds intersections of a ray from origin through farPoint with a polygon.
    /// </summary>
    private static List<Vector2> FindRayIntersections(IReadOnlyList<Vector2> polygon, Vector2 origin, Vector2 farPoint)
    {
        var intersections = new List<Vector2>();

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            var c = polygon[i];
            var d = polygon[next];

            var intersection = LineSegmentIntersection(origin, farPoint, c, d);
            if (intersection.HasValue)
            {
                intersections.Add(intersection.Value);
            }
        }

        return intersections;
    }

    /// <summary>
    /// Computes the intersection point of two line segments (a-b) and (c-d).
    /// Returns null if segments don't intersect.
    /// </summary>
    private static Vector2? LineSegmentIntersection(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var ab = b - a;
        var cd = d - c;
        float denom = ab.X * cd.Y - ab.Y * cd.X;
        if (MathF.Abs(denom) < 1e-10f) return null;

        var ac = c - a;
        float t = (ac.X * cd.Y - ac.Y * cd.X) / denom;
        float u = (ac.X * ab.Y - ac.Y * ab.X) / denom;

        if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
        {
            return a + ab * t;
        }
        return null;
    }

    private static int FindClosestIndex(IReadOnlyList<Vector2> polygon, Vector2 target)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < polygon.Count; i++)
        {
            float d = Vector2.Distance(polygon[i], target);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private static List<Vector2> ReversedCopy(IReadOnlyList<Vector2> list)
    {
        var result = new List<Vector2>(list);
        result.Reverse();
        return result;
    }
}
