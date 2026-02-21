namespace BeamNG.Procedural3D.Building;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Building.Facade;

/// <summary>
/// A wall surface defined by a lower boundary (flat) and an upper boundary (variable height).
/// Port of OSM2World's WallSurface.java — with wall element (window/door) support.
///
/// The lower boundary has N points and the upper boundary may have M points (M >= N)
/// when the roof polygon has extra vertices (e.g., ridge endpoints for gabled roofs).
///
/// The algorithm:
/// 1. Convert both boundaries to 2D "wall surface coordinates" (X = distance along wall, Z = height)
/// 2. Form a closed polygon outline from lower + reversed upper
/// 3. Triangulate with earcut (optionally with element outlines as holes)
/// 4. Convert triangles back to 3D space
/// </summary>
public class WallSurface
{
    private readonly List<Vector3> _lowerBoundaryXYZ;
    private readonly List<Vector3> _upperBoundaryXYZ;

    // Wall surface coordinates: X = distance along wall, Y = height above floor
    private readonly List<Vector2> _lowerBoundary2D;
    private readonly List<Vector2> _upperBoundary2D;
    private readonly List<Vector2> _wallOutline;

    private readonly float _length;
    private readonly Vector3 _wallNormal;

    // Wall elements (windows, doors) placed on this surface
    private readonly List<IWallElement> _elements = [];
    private readonly List<List<Vector2>> _elementOutlines = [];

    /// <summary>
    /// Constructs a wall surface from lower and upper boundaries in 3D space.
    /// Port of WallSurface constructor (lines 64-123 in Java).
    ///
    /// Lower boundary: typically 2 points (wall segment start/end) at constant Z.
    /// Upper boundary: may have more points if roof polygon has extra vertices
    /// (e.g., ridge endpoint inserted by GabledRoof.GetPolygon()).
    /// </summary>
    public WallSurface(List<Vector3> lowerBoundaryXYZ, List<Vector3> upperBoundaryXYZ)
    {
        if (lowerBoundaryXYZ.Count < 2)
            throw new ArgumentException("need at least two points in the lower boundary");
        if (upperBoundaryXYZ.Count < 2)
            throw new ArgumentException("need at least two points in the upper boundary");

        _lowerBoundaryXYZ = lowerBoundaryXYZ;
        _upperBoundaryXYZ = upperBoundaryXYZ;

        // Compute cumulative distances along the lower boundary (XY plane)
        float baseZ = lowerBoundaryXYZ[0].Z;
        var cumulativeDist = new float[lowerBoundaryXYZ.Count];
        cumulativeDist[0] = 0;
        for (int i = 1; i < lowerBoundaryXYZ.Count; i++)
        {
            var p0 = new Vector2(lowerBoundaryXYZ[i - 1].X, lowerBoundaryXYZ[i - 1].Y);
            var p1 = new Vector2(lowerBoundaryXYZ[i].X, lowerBoundaryXYZ[i].Y);
            cumulativeDist[i] = cumulativeDist[i - 1] + Vector2.Distance(p0, p1);
        }
        _length = cumulativeDist[^1];

        // Convert lower boundary to wall surface coords: (distance, height)
        // Port of: lowerBoundary = lowerBoundaryXYZ.stream().map(toWallCoord).collect(toList());
        _lowerBoundary2D = new List<Vector2>(lowerBoundaryXYZ.Count);
        for (int i = 0; i < lowerBoundaryXYZ.Count; i++)
        {
            _lowerBoundary2D.Add(new Vector2(cumulativeDist[i], lowerBoundaryXYZ[i].Z - baseZ));
        }

        // Convert upper boundary to wall surface coords
        // Port of: upperBoundary = upperBoundaryXYZ.stream().map(snapToLowerBoundary).map(toWallCoord)
        // Upper boundary points are projected onto the lower boundary's XY polyline
        // to get their "distance along wall" coordinate.
        _upperBoundary2D = new List<Vector2>(upperBoundaryXYZ.Count);
        for (int i = 0; i < upperBoundaryXYZ.Count; i++)
        {
            var p = new Vector2(upperBoundaryXYZ[i].X, upperBoundaryXYZ[i].Y);
            float wallX = ProjectOntoPolyline(p, lowerBoundaryXYZ);
            float wallZ = upperBoundaryXYZ[i].Z - baseZ;
            _upperBoundary2D.Add(new Vector2(wallX, wallZ));
        }

        // Build the wall outline polygon: lower boundary + reversed upper boundary
        // Port of: outerLoop = new ArrayList<>(upperBoundary); reverse(outerLoop);
        //          outerLoop.addAll(0, lowerBoundary); outerLoop.add(lowerBoundary.get(0));
        _wallOutline = BuildOutline(_lowerBoundary2D, _upperBoundary2D);

        // Compute wall normal from the first lower boundary segment (outward-facing)
        var edgeXY = new Vector2(
            _lowerBoundaryXYZ[1].X - _lowerBoundaryXYZ[0].X,
            _lowerBoundaryXYZ[1].Y - _lowerBoundaryXYZ[0].Y);
        _wallNormal = Vector3.Normalize(new Vector3(edgeXY.Y, -edgeXY.X, 0));
    }

    /// <summary>
    /// The length of this wall surface along the lower boundary (in meters).
    /// </summary>
    public float Length => _length;

    /// <summary>
    /// The outward-facing wall normal vector.
    /// Computed from the first segment of the lower boundary.
    /// </summary>
    public Vector3 GetWallNormal() => _wallNormal;

    /// <summary>
    /// Computes the outward-facing normal at a specific point on the wall surface.
    /// Port of WallSurface.normalAt() (lines 302-321 in Java):
    ///   Places 3 points close together on the surface and computes the normal
    ///   of that triangle: (top - center) × (right - center).
    /// For straight wall segments this equals GetWallNormal(), but for curved
    /// or multi-segment walls it produces a locally correct normal.
    /// </summary>
    public Vector3 NormalAt(Vector2 wallCoord)
    {
        float smallXDist = MathF.Min(0.01f, _length / 3f);
        float smallZDist = 0.01f;

        var v = wallCoord;

        // If near the right edge, shift left so we don't go past the end
        if (v.X + smallXDist > _length)
        {
            v = new Vector2(v.X - smallXDist, v.Y);
        }

        var vXYZ = ConvertTo3D(v);
        var vXYZRight = ConvertTo3D(new Vector2(v.X + smallXDist, v.Y));
        var vXYZTop = ConvertTo3D(new Vector2(v.X, v.Y + smallZDist));

        var toTop = vXYZTop - vXYZ;
        var toRight = vXYZRight - vXYZ;
        // OSM2World uses Cross(top, right) in Y-up coords.
        // In our Z-up system, Cross(right, top) gives the equivalent outward normal.
        var cross = Vector3.Cross(toRight, toTop);

        return cross.LengthSquared() > 1e-12f
            ? Vector3.Normalize(cross)
            : _wallNormal; // fallback to precomputed normal
    }

    /// <summary>
    /// Renders the wall surface into a MeshBuilder (no element holes).
    /// Port of WallSurface.renderTo() — triangulation + 3D conversion only.
    /// </summary>
    public void Render(MeshBuilder builder, string material, Vector2 textureScale)
    {
        if (_wallOutline.Count < 3) return;

        float area = MathF.Abs(FaceDecompositionUtil.SignedArea(_wallOutline));
        if (area < 0.01f) return;

        // Triangulate the wall outline in 2D wall surface coordinates
        var indices = PolygonTriangulator.Triangulate(_wallOutline);
        if (indices.Count < 3) return;

        RenderTriangles(builder, _wallOutline, indices, textureScale);
    }

    /// <summary>
    /// Renders the wall surface with element outlines as holes, then renders each element.
    /// Each element's outline is cut out of the wall polygon and the element renders its own
    /// geometry (frames, glass, door panels) via IWallElement.Render().
    /// Also renders inset reveal/jamb faces around each element hole.
    /// </summary>
    /// <param name="wallBuilder">Mesh builder for the wall surface (with holes).</param>
    /// <param name="getElementBuilder">Factory that returns a MeshBuilder for a given material key.
    /// Each element uses its MaterialKey to get the correct builder.</param>
    /// <param name="glassBuilder">Mesh builder for glass pane geometry. Null for LOD levels without separate glass.</param>
    /// <param name="textureScale">Texture scale for the wall material UVs.</param>
    public void RenderWithElements(MeshBuilder wallBuilder, Func<string, MeshBuilder> getElementBuilder, MeshBuilder? glassBuilder, Vector2 textureScale)
    {
        if (_wallOutline.Count < 3) return;

        float area = MathF.Abs(FaceDecompositionUtil.SignedArea(_wallOutline));
        if (area < 0.01f) return;

        if (_elements.Count == 0)
        {
            // No elements: render as plain wall
            var indices = PolygonTriangulator.Triangulate(_wallOutline);
            if (indices.Count >= 3)
                RenderTriangles(wallBuilder, _wallOutline, indices, textureScale);
            return;
        }

        // Triangulate wall with element outlines as holes
        var holes = new List<IReadOnlyList<Vector2>>(_elements.Count);
        foreach (var outline in _elementOutlines)
        {
            // Earcut expects holes in CW winding (opposite to outer CCW)
            var reversed = new List<Vector2>(outline);
            reversed.Reverse();
            holes.Add(reversed);
        }

        var wallIndices = PolygonTriangulator.Triangulate(_wallOutline, holes);

        if (wallIndices.Count >= 3)
        {
            // Build flattened vertex list: outline + all reversed hole outlines
            var allVerts = new List<Vector2>(_wallOutline);
            foreach (var hole in holes)
            {
                allVerts.AddRange(hole);
            }
            RenderTriangles(wallBuilder, allVerts, wallIndices, textureScale);
        }

        // Render inset reveal faces and element geometry for each element
        for (int e = 0; e < _elements.Count; e++)
        {
            var element = _elements[e];
            var outline = _elementOutlines[e];

            // Render inset jamb/reveal faces (the sides of the hole)
            if (element.InsetDistance > 0.001f)
            {
                RenderInsetFaces(wallBuilder, outline, element.InsetDistance, textureScale);
            }

            // Render element geometry (windows, doors, etc.)
            element.Render(wallBuilder, getElementBuilder(element.MaterialKey), glassBuilder, this);
        }
    }

    /// <summary>
    /// Adds an element to this wall surface if it fits within the wall and doesn't overlap
    /// any existing elements. Returns true if the element was added.
    /// </summary>
    public bool AddElementIfSpaceFree(IWallElement element)
    {
        var outline = element.Outline();

        // Check that outline fits within wall bounds
        if (!ContainsOutline(outline)) return false;

        // Check no overlap with existing elements
        if (OverlapsExisting(outline)) return false;

        _elements.Add(element);
        _elementOutlines.Add(outline);
        return true;
    }

    /// <summary>
    /// Returns the list of elements currently placed on this surface.
    /// </summary>
    public IReadOnlyList<IWallElement> Elements => _elements;

    /// <summary>
    /// Converts a 2D wall surface coordinate back to 3D world space.
    /// Port of WallSurface.convertTo3D(VectorXZ):
    ///   double ratio = v.x / getLength();
    ///   VectorXYZ point = interpolateOn(lowerBoundaryXYZ, ratio);
    ///   return point.addY(v.z);
    /// </summary>
    public Vector3 ConvertTo3D(Vector2 wallCoord)
    {
        if (_length < 1e-6f)
            return new Vector3(_lowerBoundaryXYZ[0].X, _lowerBoundaryXYZ[0].Y,
                _lowerBoundaryXYZ[0].Z + wallCoord.Y);

        float ratio = MathF.Max(0, MathF.Min(1, wallCoord.X / _length));

        // Interpolate along the lower boundary polyline
        float targetDist = ratio * _length;
        float cumDist = 0;

        for (int i = 0; i + 1 < _lowerBoundaryXYZ.Count; i++)
        {
            var a = _lowerBoundaryXYZ[i];
            var b = _lowerBoundaryXYZ[i + 1];
            float segLen = Vector2.Distance(
                new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
            if (segLen < 1e-8f) continue;

            if (cumDist + segLen >= targetDist - 1e-6f)
            {
                float t = (targetDist - cumDist) / segLen;
                t = MathF.Max(0, MathF.Min(1, t));
                var basePoint = Vector3.Lerp(a, b, t);
                return new Vector3(basePoint.X, basePoint.Y, basePoint.Z + wallCoord.Y);
            }
            cumDist += segLen;
        }

        // Fallback: use last point
        var last = _lowerBoundaryXYZ[^1];
        return new Vector3(last.X, last.Y, last.Z + wallCoord.Y);
    }

    /// <summary>
    /// Projects a 2D point onto a 3D polyline and returns the distance along the polyline.
    /// Port of: lowerXZ.offsetOf(p.xz()) in Java's toWallCoord lambda.
    /// </summary>
    private static float ProjectOntoPolyline(Vector2 point, List<Vector3> polyline)
    {
        float bestDist = float.MaxValue;
        float bestOffset = 0;
        float cumDist = 0;

        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            var a = new Vector2(polyline[i].X, polyline[i].Y);
            var b = new Vector2(polyline[i + 1].X, polyline[i + 1].Y);
            float segLen = Vector2.Distance(a, b);
            if (segLen < 1e-8f) { cumDist += segLen; continue; }

            // Project point onto this segment
            var ab = b - a;
            float t = MathF.Max(0, MathF.Min(1, Vector2.Dot(point - a, ab) / (segLen * segLen)));
            var projected = a + ab * t;
            float dist = Vector2.Distance(point, projected);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestOffset = cumDist + t * segLen;
            }
            cumDist += segLen;
        }

        return bestOffset;
    }

    /// <summary>
    /// Builds the closed wall outline polygon from lower and upper boundaries.
    /// Port of Java lines 98-123:
    ///   outerLoop = new ArrayList<>(upperBoundary);
    ///   // remove duplicates at ends
    ///   reverse(outerLoop);
    ///   outerLoop.addAll(0, lowerBoundary);
    ///   outerLoop.add(lowerBoundary.get(0)); // close polygon
    /// Then passed to SimplePolygonXZ (which stores as open loop for us).
    /// </summary>
    private static List<Vector2> BuildOutline(List<Vector2> lower, List<Vector2> upper)
    {
        var reversedUpper = new List<Vector2>(upper);

        // Remove near-duplicate endpoints where upper meets lower
        // Port of: if (upperBoundary.get(0).distanceTo(lowerBoundary.get(0)) < 0.01)
        if (reversedUpper.Count > 0 && lower.Count > 0 &&
            Vector2.Distance(reversedUpper[0], lower[0]) < 0.01f)
        {
            reversedUpper.RemoveAt(0);
        }
        if (reversedUpper.Count > 0 && lower.Count > 0 &&
            Vector2.Distance(reversedUpper[^1], lower[^1]) < 0.01f)
        {
            reversedUpper.RemoveAt(reversedUpper.Count - 1);
        }

        reversedUpper.Reverse();

        // Build outline: lower + reversed upper (open polygon, no closing vertex)
        var outline = new List<Vector2>(lower.Count + reversedUpper.Count);
        outline.AddRange(lower);
        outline.AddRange(reversedUpper);

        return outline;
    }

    // ==========================================
    // Triangle rendering helper
    // ==========================================

    /// <summary>
    /// Renders triangulated 2D wall surface coords into a MeshBuilder as 3D geometry.
    /// Shared between Render() and RenderWithElements().
    /// </summary>
    private void RenderTriangles(MeshBuilder builder, IReadOnlyList<Vector2> vertices,
        IReadOnlyList<int> indices, Vector2 textureScale)
    {
        for (int i = 0; i < indices.Count; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];

            var p0 = ConvertTo3D(vertices[i0]);
            var p1 = ConvertTo3D(vertices[i1]);
            var p2 = ConvertTo3D(vertices[i2]);

            // Check triangle area to skip degenerate triangles
            var triNormal = Vector3.Cross(p1 - p0, p2 - p0);
            if (triNormal.LengthSquared() < 1e-10f) continue;

            // UV: wall surface coords / texture scale
            var uv0 = new Vector2(vertices[i0].X / textureScale.X, vertices[i0].Y / textureScale.Y);
            var uv1 = new Vector2(vertices[i1].X / textureScale.X, vertices[i1].Y / textureScale.Y);
            var uv2 = new Vector2(vertices[i2].X / textureScale.X, vertices[i2].Y / textureScale.Y);

            var vi0 = builder.AddVertex(p0, _wallNormal, uv0);
            var vi1 = builder.AddVertex(p1, _wallNormal, uv1);
            var vi2 = builder.AddVertex(p2, _wallNormal, uv2);

            builder.AddTriangle(vi0, vi1, vi2);
        }
    }

    // ==========================================
    // Inset reveal/jamb faces
    // ==========================================

    /// <summary>
    /// Renders the inset reveal/jamb faces around an element hole.
    /// These are the sides of the rectangular "tunnel" from the wall surface to the recessed element.
    /// Each edge of the element outline becomes a small quad connecting the wall face to the inset depth.
    /// </summary>
    private void RenderInsetFaces(MeshBuilder builder, List<Vector2> outline, float insetDistance, Vector2 textureScale)
    {
        var toBack = _wallNormal * (-insetDistance);

        for (int i = 0; i < outline.Count; i++)
        {
            int next = (i + 1) % outline.Count;

            var frontA = ConvertTo3D(outline[i]);
            var frontB = ConvertTo3D(outline[next]);
            var backA = frontA + toBack;
            var backB = frontB + toBack;

            // Compute inward-facing normal for this reveal face.
            // The outline is CCW, so Cross(depthDir, edgeDir) points outward from the window center.
            // We need inward (toward window center) so the faces are visible from outside looking in.
            var edgeDir = Vector3.Normalize(frontB - frontA);
            var depthDir = Vector3.Normalize(backA - frontA);
            var revealNormal = Vector3.Normalize(Vector3.Cross(edgeDir, depthDir));

            float edgeLen = Vector2.Distance(outline[i], outline[next]);

            var vi0 = builder.AddVertex(frontA, revealNormal, new Vector2(0, 0));
            var vi1 = builder.AddVertex(backA, revealNormal, new Vector2(insetDistance / textureScale.X, 0));
            var vi2 = builder.AddVertex(frontB, revealNormal, new Vector2(0, edgeLen / textureScale.Y));
            var vi3 = builder.AddVertex(backB, revealNormal,
                new Vector2(insetDistance / textureScale.X, edgeLen / textureScale.Y));

            // Flipped winding to match inward-facing normal
            builder.AddTriangle(vi0, vi2, vi1);
            builder.AddTriangle(vi2, vi3, vi1);
        }
    }

    // ==========================================
    // Element overlap detection
    // ==========================================

    /// <summary>
    /// Checks that all vertices of the element outline lie within the wall outline polygon.
    /// Uses a simple bounding box pre-check + point-in-polygon test.
    /// </summary>
    private bool ContainsOutline(List<Vector2> outline)
    {
        // Quick bounding box check against wall extent
        foreach (var p in outline)
        {
            if (p.X < -0.01f || p.X > _length + 0.01f) return false;
            if (p.Y < -0.01f) return false;
        }

        // Point-in-polygon test against the wall outline
        foreach (var p in outline)
        {
            if (!PointInPolygon(p, _wallOutline)) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks that the given outline doesn't overlap with any already-placed element outline.
    /// Uses bounding box overlap as a fast pre-check, then polygon edge intersection.
    /// </summary>
    private bool OverlapsExisting(List<Vector2> outline)
    {
        var (minX, minY, maxX, maxY) = GetBounds(outline);

        foreach (var existing in _elementOutlines)
        {
            var (eMinX, eMinY, eMaxX, eMaxY) = GetBounds(existing);

            // Fast AABB overlap check
            if (maxX < eMinX || minX > eMaxX || maxY < eMinY || minY > eMaxY)
                continue;

            // Check for edge intersections
            if (PolygonsIntersect(outline, existing)) return true;

            // Check if one is fully inside the other
            if (PointInPolygon(outline[0], existing)) return true;
            if (PointInPolygon(existing[0], outline)) return true;
        }
        return false;
    }

    private static (float minX, float minY, float maxX, float maxY) GetBounds(List<Vector2> polygon)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in polygon)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Ray-casting point-in-polygon test.
    /// </summary>
    private static bool PointInPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y) &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y)
                / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Checks if any edges of polygon A intersect any edges of polygon B.
    /// </summary>
    private static bool PolygonsIntersect(List<Vector2> a, List<Vector2> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            int ni = (i + 1) % a.Count;
            for (int j = 0; j < b.Count; j++)
            {
                int nj = (j + 1) % b.Count;
                if (SegmentsIntersect(a[i], a[ni], b[j], b[nj]))
                    return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var ab = b - a;
        var cd = d - c;
        float denom = ab.X * cd.Y - ab.Y * cd.X;
        if (MathF.Abs(denom) < 1e-10f) return false;

        var ac = c - a;
        float t = (ac.X * cd.Y - ac.Y * cd.X) / denom;
        float u = (ac.X * ab.Y - ac.Y * ab.X) / denom;

        return t > 0.001f && t < 0.999f && u > 0.001f && u < 0.999f;
    }
}
