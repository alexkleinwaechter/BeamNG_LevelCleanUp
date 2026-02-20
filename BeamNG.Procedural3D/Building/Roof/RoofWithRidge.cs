namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Abstract base for ridge-based roofs. Handles ridge calculation in the constructor.
/// 1:1 port of OSM2World's RoofWithRidge.java.
///
/// The constructor calculates:
///   - Ridge line (two endpoints, possibly offset inward for hipped roofs)
///   - Cap segments (the polygon edges at each end of the ridge)
///   - Maximum distance from any polygon vertex to the ridge
///   - Roof height (from tags or default calculation)
///
/// Subclasses (GabledRoof, HippedRoof) only need to implement:
///   - GetPolygon()        → whether to insert ridge endpoints into polygon
///   - GetInnerSegments()  → which line segments define the roof faces
/// </summary>
public abstract class RoofWithRidge : HeightfieldRoof
{
    /// <summary>Absolute distance of ridge endpoints to polygon outline.</summary>
    protected readonly float ridgeOffset;

    /// <summary>The ridge line segment.</summary>
    protected readonly (Vector2 P1, Vector2 P2) ridge;

    /// <summary>The polygon edge (cap segment) closer to ridge.P1.</summary>
    protected readonly (Vector2 P1, Vector2 P2) cap1;

    /// <summary>The polygon edge (cap segment) closer to ridge.P2.</summary>
    protected readonly (Vector2 P1, Vector2 P2) cap2;

    /// <summary>Maximum distance of any polygon vertex to the ridge segment.</summary>
    protected readonly float maxDistanceToRidge;

    /// <summary>
    /// Creates an instance and calculates all ridge-related fields.
    /// Port of RoofWithRidge constructor in Java.
    ///
    /// <param name="relativeRoofOffset">Distance of ridge to outline relative to
    /// length of roof cap; 0 if ridge ends at outline (gabled), 1/3 for hipped.</param>
    /// </summary>
    protected RoofWithRidge(float relativeRoofOffset, BuildingData building, List<Vector2> polygon)
        : base(building, polygon)
    {
        // Port of: SimplePolygonXZ simplifiedPolygon = outerPoly.getSimplifiedPolygon();
        var simplifiedPolygon = SimplifyPolygon(polygon);

        // Port of: VectorXZ ridgeDirection = ridgeVectorFromRoofDirection/ridgeDirection/roofOrientation
        var ridgeDirection = CalculateRidgeDirection(building, simplifiedPolygon);

        // Port of: AxisAlignedRectangleXZ.bbox(outerPoly.vertices())
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in polygon)
        {
            minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
            minY = MathF.Min(minY, v.Y); maxY = MathF.Max(maxY, v.Y);
        }
        float bboxExtent = (maxX - minX) + (maxY - minY);

        // Port of: VectorXZ p1 = outerPoly.getCentroid();
        var centroid = CalculateCentroid(polygon);

        // Port of: LineSegmentXZ intersectionLine = new LineSegmentXZ(...)
        var lineStart = centroid - ridgeDirection * bboxExtent;
        var lineEnd = centroid + ridgeDirection * bboxExtent;

        // Port of: List<Intersection> intersections = simplifiedPolygon.intersections(intersectionLine);
        var intersections = new List<(Vector2 point, Vector2 segP1, Vector2 segP2, float dist)>();
        for (int i = 0; i < simplifiedPolygon.Count; i++)
        {
            int next = (i + 1) % simplifiedPolygon.Count;
            var inter = SegmentIntersection(lineStart, lineEnd,
                simplifiedPolygon[i], simplifiedPolygon[next]);
            if (inter.HasValue)
            {
                float dist = Vector2.Distance(inter.Value, lineStart);
                intersections.Add((inter.Value, simplifiedPolygon[i], simplifiedPolygon[next], dist));
            }
        }

        // Port of: if (intersections.size() < 2) throw InvalidGeometryException
        if (intersections.Count < 2)
        {
            Console.WriteLine($"[RoofGen] WARNING: Building {building.OsmId}: " +
                $"Ridge line found {intersections.Count} intersections with polygon, need >= 2");
            // Degenerate fallback
            ridge = (polygon[0], polygon[polygon.Count / 2]);
            cap1 = (polygon[0], polygon[MathF.Min(1, polygon.Count - 1) > 0 ? 1 : 0]);
            cap2 = (polygon[polygon.Count / 2],
                polygon[(polygon.Count / 2 + 1) % polygon.Count]);
            ridgeOffset = 0;
            maxDistanceToRidge = 1f;
            roofHeight = CalculateRoofHeight();
            return;
        }

        // Port of: intersections.sort(comparingDouble(i -> i.point().distanceTo(intersectionLine.p1)));
        intersections.Sort((a, b) => a.dist.CompareTo(b.dist));
        var i1 = intersections[0];
        var i2 = intersections[^1];

        // Port of: cap1 = i1.segment(); cap2 = i2.segment();
        cap1 = (i1.segP1, i1.segP2);
        cap2 = (i2.segP1, i2.segP2);

        var c1 = i1.point;
        var c2 = i2.point;

        // Port of: ridgeOffset = min(cap1.getLength() * relativeRoofOffset, 0.4 * c1.distanceTo(c2));
        float cap1Length = Vector2.Distance(cap1.P1, cap1.P2);
        ridgeOffset = MathF.Min(
            cap1Length * relativeRoofOffset,
            0.4f * Vector2.Distance(c1, c2));

        // Port of: if (relativeRoofOffset == 0) ridge = new LineSegmentXZ(c1, c2);
        if (relativeRoofOffset == 0)
        {
            ridge = (c1, c2);
        }
        else
        {
            // Port of: c1.add(p1.subtract(c1).normalize().mult(ridgeOffset))
            var toCenter1 = centroid - c1;
            var toCenter2 = centroid - c2;
            float len1 = toCenter1.Length();
            float len2 = toCenter2.Length();

            var rp1 = len1 > 1e-6f ? c1 + Vector2.Normalize(toCenter1) * ridgeOffset : c1;
            var rp2 = len2 > 1e-6f ? c2 + Vector2.Normalize(toCenter2) * ridgeOffset : c2;
            ridge = (rp1, rp2);
        }

        // Port of: maxDistanceToRidge = outerPoly.getVertices().stream()
        //     .mapToDouble(v -> distanceFromLineSegment(v, ridge)).max().getAsDouble();
        float maxDist = 0;
        foreach (var v in polygon)
        {
            float d = FaceDecompositionUtil.DistancePointToSegment(v, ridge.P1, ridge.P2);
            maxDist = MathF.Max(maxDist, d);
        }
        maxDistanceToRidge = MathF.Max(maxDist, 0.01f);

        // Calculate roof height (port of calculatePreliminaryHeight + fallback)
        roofHeight = CalculateRoofHeight();
    }

    /// <summary>
    /// Inner points: empty for both gabled and hipped roofs.
    /// Port of GabledRoof.getInnerPoints() and HippedRoof.getInnerPoints(),
    /// both return emptyList() in Java.
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Roof height at a 2D position. Linear falloff from ridge to eave.
    /// Port of GabledRoof/HippedRoof.getRoofHeightAt_noInterpolation():
    ///   double distRidge = distanceFromLineSegment(pos, ridge);
    ///   double relativePlacement = distRidge / maxDistanceToRidge;
    ///   return roofHeight - roofHeight * relativePlacement;
    ///
    /// This formula works identically for both gabled and hipped roofs.
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        float distRidge = FaceDecompositionUtil.DistancePointToSegment(pos, ridge.P1, ridge.P2);
        float relativePlacement = distRidge / maxDistanceToRidge;
        return roofHeight - roofHeight * relativePlacement;
    }

    /// <summary>
    /// Diagnostic logging: checks if face count matches expected value.
    /// </summary>
    protected override void LogFaceCount(int faceCount, int innerSegmentCount, int polyVertexCount)
    {
        string roofType = GetType().Name;
        int expectedFaces = this is GabledRoof ? 2 : (this is HippedRoof ? 4 : -1);

        if (faceCount == 0)
        {
            Console.WriteLine($"[RoofGen] WARNING: Building {building.OsmId} ({roofType}): " +
                $"FaceDecomposition returned 0 faces! polygon={polyVertexCount}v, " +
                $"innerSegments={innerSegmentCount}");
        }
        else if (expectedFaces > 0 && faceCount != expectedFaces)
        {
            Console.WriteLine($"[RoofGen] Building {building.OsmId} ({roofType}): " +
                $"got {faceCount} faces (expected {expectedFaces}), polygon={polyVertexCount}v");
        }
    }

    #region Utility: FindNearestVertex

    /// <summary>
    /// Finds the polygon vertex closest to the target point.
    /// Used to snap inner segment endpoints to actual polygon vertices,
    /// ensuring the planar graph in FaceDecompositionUtil is properly connected.
    /// </summary>
    protected static Vector2 FindNearestVertex(List<Vector2> polygon, Vector2 target)
    {
        float bestDistSq = float.MaxValue;
        var bestVertex = target;
        foreach (var v in polygon)
        {
            float distSq = Vector2.DistanceSquared(v, target);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestVertex = v;
            }
        }
        return bestVertex;
    }

    #endregion

    #region Roof Height Calculation

    /// <summary>
    /// Calculates roof height from building tags.
    /// Port of RoofWithRidge.calculatePreliminaryHeight() with fallback default.
    ///
    /// Priority:
    /// 1. Explicit roof:height tag
    /// 2. roof:angle tag → height = tan(angle) * maxDistanceToRidge
    /// 3. Default: ~30° pitch proportional to building width
    /// </summary>
    private float CalculateRoofHeight()
    {
        // Port of: parseMeasure(tags.getValue("roof:height"))
        if (building.RoofHeight > 0)
            return building.RoofHeight;

        // Port of: parseAngle(tags.getValue("roof:angle"))
        //   roofHeight = tan(toRadians(angle)) * maxDistanceToRidge;
        if (building.RoofAngle.HasValue && building.RoofAngle.Value > 0 && building.RoofAngle.Value < 90)
        {
            float angleRad = building.RoofAngle.Value * MathF.PI / 180f;
            return MathF.Tan(angleRad) * maxDistanceToRidge;
        }

        // Default (not in Java — Java lets BuildingPart assign the height)
        return MathF.Max(1.5f, maxDistanceToRidge * 0.4f);
    }

    #endregion

    #region Ridge Direction (Port of RoofWithRidge direction methods)

    /// <summary>
    /// Determines the ridge direction vector.
    /// Port of RoofWithRidge's three-step direction detection:
    /// 1. ridgeVectorFromRoofDirection() — explicit roof:direction tag
    /// 2. ridgeVectorFromRidgeDirection() — explicit roof:ridge:direction tag (not implemented)
    /// 3. ridgeVectorFromRoofOrientation() — longest bounding box side
    /// </summary>
    private static Vector2 CalculateRidgeDirection(BuildingData building, List<Vector2> polygon)
    {
        // Step 1: roof:direction tag (compass bearing of slope direction)
        // Ridge is perpendicular to slope direction
        // Port of: ridgeVectorFromRoofDirection()
        if (building.RoofDirection.HasValue)
        {
            float bearingRad = building.RoofDirection.Value * MathF.PI / 180f;
            // Port of: VectorXZ.fromAngle(angle) = (sin(angle), cos(angle))
            var slopeDir = new Vector2(MathF.Sin(bearingRad), MathF.Cos(bearingRad));
            // Port of: rightNormal() = (z, -x) → perpendicular
            var ridgeDir = new Vector2(slopeDir.Y, -slopeDir.X);

            // Snap to polygon edge if close (port of snapDirection)
            var snapped = SnapToPolygonEdge(ridgeDir, polygon);
            return snapped ?? Vector2.Normalize(ridgeDir);
        }

        // Step 2: roof:orientation tag or auto-detect from building shape
        // Port of: ridgeVectorFromRoofOrientation()
        // Uses longest edge direction (simplified version of minimumRotatedBoundingBox)
        var longestEdgeDir = FindLongestEdgeDirection(polygon);

        // Port of: if (tags.contains("roof:orientation", "across")) result = result.rightNormal();
        if (building.RoofOrientation == "across")
            return new Vector2(longestEdgeDir.Y, -longestEdgeDir.X);

        return longestEdgeDir;
    }

    /// <summary>
    /// Finds the direction of the longest polygon edge.
    /// Simplified version of OSM2World's polygon.minimumRotatedBoundingBox() approach.
    /// Port of: max(rotatedBbox.getSegments(), comparingDouble(s -> s.getLength())).getDirection()
    /// </summary>
    private static Vector2 FindLongestEdgeDirection(List<Vector2> polygon)
    {
        float maxLenSq = 0;
        var bestDir = Vector2.UnitX;
        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            var edge = polygon[next] - polygon[i];
            float lenSq = edge.LengthSquared();
            if (lenSq > maxLenSq)
            {
                maxLenSq = lenSq;
                bestDir = edge;
            }
        }
        return maxLenSq > 0 ? Vector2.Normalize(bestDir) : Vector2.UnitX;
    }

    /// <summary>
    /// Attempts to snap a direction to a parallel or perpendicular polygon segment direction.
    /// Port of Roof.snapDirection().
    /// Returns null if no snap is warranted.
    /// </summary>
    internal static Vector2? SnapToPolygonEdge(Vector2 direction, List<Vector2> polygon)
    {
        const float acceptableAngleDiff = MathF.PI / 4f; // 45° for cardinal directions

        float bestAngleDiff = float.MaxValue;
        Vector2? bestDir = null;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            var edge = polygon[next] - polygon[i];
            float len = edge.Length();
            if (len < 0.01f) continue;
            var edgeDir = edge / len;

            // Check edge direction and its perpendicular (+ reverse variants)
            Vector2[] candidates =
            {
                edgeDir,
                new(edgeDir.Y, -edgeDir.X),
                -edgeDir,
                new(-edgeDir.Y, edgeDir.X)
            };

            foreach (var candidate in candidates)
            {
                float dot = Vector2.Dot(direction, candidate);
                float angleDiff = MathF.Acos(MathF.Min(1f, MathF.Abs(dot)));
                if (dot > 0 && angleDiff < bestAngleDiff)
                {
                    bestAngleDiff = angleDiff;
                    bestDir = candidate;
                }
            }
        }

        if (bestAngleDiff <= acceptableAngleDiff && bestDir.HasValue)
            return Vector2.Normalize(bestDir.Value);

        return null;
    }

    #endregion

    #region Polygon Simplification

    /// <summary>
    /// Removes nearly-collinear vertices from a polygon.
    /// Port of OSM2World's SimplePolygonXZ.getSimplifiedPolygon().
    /// </summary>
    internal static List<Vector2> SimplifyPolygon(List<Vector2> polygon)
    {
        if (polygon.Count <= 3) return polygon;

        var result = SimplifyPolygonWithTolerance(polygon, 0.05f);
        if (result == null || result.Count < 3)
            return polygon;

        // Check if simplification changed area too much (> 10%)
        float origArea = MathF.Abs(SignedArea(polygon));
        float newArea = MathF.Abs(SignedArea(result));
        if (origArea > 0 && MathF.Abs(newArea - origArea) / origArea > 0.1f)
        {
            result = SimplifyPolygonWithTolerance(polygon, 0.001f);
            if (result == null || result.Count < 3)
                return polygon;
        }

        return result;
    }

    private static List<Vector2>? SimplifyPolygonWithTolerance(List<Vector2> polygon, float maxDotProduct)
    {
        int n = polygon.Count;
        var delete = new bool[n];
        int deleteCount = 0;

        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            int next = (i + 1) % n;

            var segBefore = polygon[i] - polygon[prev];
            var segAfter = polygon[next] - polygon[i];

            float lenBefore = segBefore.Length();
            float lenAfter = segAfter.Length();
            if (lenBefore < 1e-6f || lenAfter < 1e-6f) continue;

            float dot = Vector2.Dot(segBefore / lenBefore, segAfter / lenAfter);
            if (MathF.Abs(dot - 1f) < maxDotProduct)
            {
                delete[i] = true;
                deleteCount++;
            }
        }

        if (deleteCount == 0) return polygon;
        if (deleteCount > n - 3) return polygon;

        var result = new List<Vector2>();
        for (int i = 0; i < n; i++)
            if (!delete[i])
                result.Add(polygon[i]);

        return result.Count >= 3 ? result : null;
    }

    #endregion

    #region Geometry Utilities

    /// <summary>
    /// Signed area of a polygon (open vertex list). Positive = CCW, Negative = CW.
    /// </summary>
    protected static float SignedArea(List<Vector2> polygon)
    {
        float sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            sum += polygon[i].X * polygon[next].Y;
            sum -= polygon[next].X * polygon[i].Y;
        }
        return sum / 2f;
    }

    /// <summary>
    /// Centroid of a polygon using the shoelace-based formula.
    /// Port of OSM2World's SimplePolygonXZ.getCentroid().
    /// </summary>
    internal static Vector2 CalculateCentroid(List<Vector2> polygon)
    {
        int n = polygon.Count;
        if (n == 0) return Vector2.Zero;

        float area = SignedArea(polygon);
        if (MathF.Abs(area) < 1e-10f)
        {
            var sum = Vector2.Zero;
            foreach (var p in polygon) sum += p;
            return sum / n;
        }

        float cx = 0, cy = 0;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            float factor = polygon[i].X * polygon[next].Y - polygon[next].X * polygon[i].Y;
            cx += (polygon[i].X + polygon[next].X) * factor;
            cy += (polygon[i].Y + polygon[next].Y) * factor;
        }

        float areaFactor = 1f / (6f * area);
        return new Vector2(areaFactor * cx, areaFactor * cy);
    }

    /// <summary>
    /// Intersection of two line segments. Returns the intersection point or null.
    /// Port of OSM2World's GeometryUtil.getLineSegmentIntersection().
    /// </summary>
    internal static Vector2? SegmentIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        float vx = a2.X - a1.X, vy = a2.Y - a1.Y;
        float qx = b2.X - b1.X, qy = b2.Y - b1.Y;

        float denom = vy * qx - vx * qy;
        if (MathF.Abs(denom) < 1e-10f) return null;

        float invDenom = 1f / denom;
        float amcx = b1.X - a1.X;
        float amcy = b1.Y - a1.Y;

        float t = (amcy * qx - amcx * qy) * invDenom;
        if (t < 0 || t > 1) return null;

        float s = (amcy * vx - amcx * vy) * invDenom;
        if (s < 0 || s > 1) return null;

        return new Vector2(a1.X + t * vx, a1.Y + t * vy);
    }

    /// <summary>
    /// Removes the closing vertex if the polygon is explicitly closed (last == first).
    /// </summary>
    protected static List<Vector2> RemoveClosingVertex(List<Vector2> polygon)
    {
        if (polygon.Count > 1 && Vector2.DistanceSquared(polygon[0], polygon[^1]) < 0.0001f)
            return polygon.Take(polygon.Count - 1).ToList();
        return new List<Vector2>(polygon);
    }

    /// <summary>
    /// Prepares a building footprint for roof generation:
    /// 1. Remove closing vertex (if explicitly closed)
    /// 2. Ensure CCW winding
    /// Returns null if polygon is degenerate (fewer than 3 vertices).
    /// </summary>
    public static List<Vector2>? PreparePolygon(List<Vector2> footprint)
    {
        if (footprint.Count < 3) return null;

        var polygon = RemoveClosingVertex(footprint);
        if (polygon.Count < 3) return null;

        if (SignedArea(polygon) < 0)
            polygon.Reverse();

        return polygon;
    }

    #endregion
}
