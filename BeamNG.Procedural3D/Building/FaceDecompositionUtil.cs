namespace BeamNG.Procedural3D.Building;

using System.Numerics;

/// <summary>
/// Finds faces in a planar graph of line segments.
/// 1:1 port of OSM2World's FaceDecompositionUtil.java.
///
/// Since C# uses float (not double), we use a PointPool with distance-based snapping
/// instead of Java's exact VectorXZ equality. The SNAP_DISTANCE constants are tuned
/// for float precision on building-scale geometry (1-100m coordinates).
/// </summary>
public static class FaceDecompositionUtil
{
    // Snap distance for the PointPool and intersection snapping.
    // Java uses 1e-5 with double precision. We use 1e-3 for float precision.
    private const float SNAP_DISTANCE = 1e-3f;
    private const float SNAP_DISTANCE_SQ = SNAP_DISTANCE * SNAP_DISTANCE;

    /// <summary>
    /// Splits a polygon into faces using inner line segments as boundaries.
    /// Port of OSM2World's FaceDecompositionUtil.splitPolygonIntoFaces().
    /// </summary>
    public static List<List<Vector2>> SplitPolygonIntoFaces(
        List<Vector2> outerPolygon,
        List<(Vector2 P1, Vector2 P2)> innerSegments)
    {
        // Ensure CCW winding for correct face classification
        if (SignedArea(outerPolygon) < 0)
        {
            outerPolygon = new List<Vector2>(outerPolygon);
            outerPolygon.Reverse();
        }

        // Collect all segments: polygon edges + inner segments
        var segments = new List<(Vector2 P1, Vector2 P2)>();
        for (int i = 0; i < outerPolygon.Count; i++)
        {
            int next = (i + 1) % outerPolygon.Count;
            segments.Add((outerPolygon[i], outerPolygon[next]));
        }
        segments.AddRange(innerSegments);

        // Run the face decomposition
        var allFaces = FacesFromGraph(segments);

        // Filter: only keep faces whose interior is inside the outer polygon
        // Port of: result.removeIf(p -> !polygon.contains(p.getPointInside()));
        allFaces.RemoveAll(face =>
        {
            var pointInside = GetPointInside(face);
            return !PointInPolygon(pointInside, outerPolygon);
        });

        return allFaces;
    }

    /// <summary>
    /// Finds all faces in a planar graph defined by line segments.
    /// Port of OSM2World's facesFromGraph().
    /// </summary>
    private static List<List<Vector2>> FacesFromGraph(List<(Vector2 P1, Vector2 P2)> segments)
    {
        var pool = new PointPool(SNAP_DISTANCE);

        // Register all segment endpoints as known points
        // Port of: Set<VectorXZ> knownPoints = segments.stream().flatMap(...)
        foreach (var seg in segments)
        {
            pool.GetOrAdd(seg.P1);
            pool.GetOrAdd(seg.P2);
        }

        // Find all intersections between segments
        // Port of: SimpleLineSegmentIntersectionFinder.findAllIntersections(segments)
        var intersections = FindAllIntersections(segments);

        // Snap intersection points to nearby known endpoints
        // Port of: snap + replace loop in facesFromGraph
        for (int i = 0; i < intersections.Count; i++)
        {
            var inter = intersections[i];
            int snappedId = pool.FindNearest(inter.Point);
            if (snappedId >= 0 && Vector2.DistanceSquared(pool.Get(snappedId), inter.Point) < SNAP_DISTANCE_SQ)
            {
                intersections[i] = (pool.Get(snappedId), inter.SegA, inter.SegB);
            }
            else
            {
                pool.GetOrAdd(inter.Point);
            }
        }

        // Build points-per-segment map (endpoints + intersection points)
        // Port of: Multimap<LineSegmentXZ, VectorXZ> intersectionPointsPerSegment
        var pointsPerSegment = new Dictionary<int, HashSet<int>>();
        for (int si = 0; si < segments.Count; si++)
        {
            pointsPerSegment[si] = new HashSet<int>();
            pointsPerSegment[si].Add(pool.GetOrAdd(segments[si].P1));
            pointsPerSegment[si].Add(pool.GetOrAdd(segments[si].P2));
        }
        foreach (var inter in intersections)
        {
            int pid = pool.GetOrAdd(inter.Point);
            if (pointsPerSegment.ContainsKey(inter.SegA))
                pointsPerSegment[inter.SegA].Add(pid);
            if (pointsPerSegment.ContainsKey(inter.SegB))
                pointsPerSegment[inter.SegB].Add(pid);
        }

        // Split segments at intersection points → create edges
        // Port of: sort points by distance from start, create sub-edges
        var edges = new HashSet<(int, int)>();
        foreach (var kvp in pointsPerSegment)
        {
            var seg = segments[kvp.Key];
            var pointIds = kvp.Value.ToList();

            // Sort by distance from the canonical start (min vertex by X then Y)
            // Port of: VectorXZ start = min(segment.vertices(), X_Y_COMPARATOR);
            var startId = pointIds.OrderBy(id =>
            {
                var p = pool.Get(id);
                return (p.X, p.Y);  // lexicographic
            }).First();
            var startPos = pool.Get(startId);

            pointIds.Sort((a, b) =>
                Vector2.DistanceSquared(pool.Get(a), startPos).CompareTo(
                    Vector2.DistanceSquared(pool.Get(b), startPos)));

            for (int i = 0; i + 1 < pointIds.Count; i++)
            {
                int a = pointIds[i], b = pointIds[i + 1];
                if (a != b)
                    edges.Add(a < b ? (a, b) : (b, a));
            }
        }

        return FacesFromFullyNodedGraph(pool, edges);
    }

    /// <summary>
    /// Core face enumeration algorithm.
    /// Port of OSM2World's facesFromFullyNodedGraph().
    /// </summary>
    private static List<List<Vector2>> FacesFromFullyNodedGraph(
        PointPool pool,
        HashSet<(int, int)> undirectedEdges)
    {
        // Create directed edges (both directions)
        var directedEdges = new HashSet<(int from, int to)>();
        foreach (var (a, b) in undirectedEdges)
        {
            directedEdges.Add((a, b));
            directedEdges.Add((b, a));
        }

        // For each node, build sorted outgoing edge list
        // Port of: outgoingEdges.sort(Comparator.comparingDouble(e -> e.getDirection().angle()));
        var outgoingEdgesMap = new Dictionary<int, List<int>>();
        foreach (var (from, to) in directedEdges)
        {
            if (!outgoingEdgesMap.ContainsKey(from))
                outgoingEdgesMap[from] = new List<int>();
            outgoingEdgesMap[from].Add(to);
        }
        foreach (var kvp in outgoingEdgesMap)
        {
            var nodePos = pool.Get(kvp.Key);
            kvp.Value.Sort((a, b) =>
            {
                float angleA = DirectionAngle(pool.Get(a) - nodePos);
                float angleB = DirectionAngle(pool.Get(b) - nodePos);
                return angleA.CompareTo(angleB);
            });
        }

        // Face enumeration: walk edges using "next clockwise" rule
        // Port of: the while (!remainingEdges.isEmpty()) loop in Java
        var remainingEdges = new HashSet<(int, int)>(directedEdges);
        var faces = new List<List<Vector2>>();

        while (remainingEdges.Count > 0)
        {
            var currentPath = new List<(int from, int to)>();
            var firstEdge = remainingEdges.First();
            currentPath.Add(firstEdge);

            bool valid = true;
            while (true)
            {
                var lastEdge = currentPath[^1];

                // Check if path closed (last edge returns to start of first edge)
                if (currentPath.Count > 1 && lastEdge.to == currentPath[0].from)
                    break;

                if (!outgoingEdgesMap.TryGetValue(lastEdge.to, out var outEdges) || outEdges.Count == 0)
                {
                    valid = false;
                    break;
                }

                // Find the reverse of incoming edge in the outgoing list
                // Java: int incomingIndex = outgoingEdges.indexOf(currentEdge.reverse());
                int reverseTarget = lastEdge.from;
                int incomingIndex = outEdges.IndexOf(reverseTarget);

                // Java silently falls back to index 0 when indexOf returns -1:
                // (-1 + 1) % size = 0. We match this behavior.
                int outIndex = ((incomingIndex < 0 ? -1 : incomingIndex) + 1) % outEdges.Count;
                int nextTarget = outEdges[outIndex];
                currentPath.Add((lastEdge.to, nextTarget));

                if (currentPath.Count > 10000)
                {
                    Console.WriteLine("[FaceDecomp] Path too long (>10000), likely infinite loop");
                    valid = false;
                    break;
                }
            }

            // Remove all path edges from remaining
            foreach (var edge in currentPath)
                remainingEdges.Remove(edge);

            if (!valid || currentPath.Count < 3)
                continue;

            // Build vertex loop
            var vertexLoop = new List<Vector2>();
            foreach (var edge in currentPath)
                vertexLoop.Add(pool.Get(edge.from));

            if (vertexLoop.Count >= 3)
            {
                float area = SignedArea(vertexLoop);
                if (MathF.Abs(area) > 1e-6f)
                    faces.Add(vertexLoop);
            }
        }

        // Separate outer rings (CCW) from inner rings (CW)
        // Port of: outerRings = faces.stream().filter(f -> !f.isClockwise()).collect(toList());
        var outerRings = new List<List<Vector2>>();
        foreach (var face in faces)
        {
            if (SignedArea(face) > 0)
                outerRings.Add(face);
        }

        return outerRings;
    }

    #region Intersection Finding

    /// <summary>
    /// Port of OSM2World's SimpleLineSegmentIntersectionFinder.findAllIntersections().
    /// Uses getTrueLineSegmentIntersection to skip shared endpoints.
    /// </summary>
    private static List<(Vector2 Point, int SegA, int SegB)> FindAllIntersections(
        List<(Vector2 P1, Vector2 P2)> segments)
    {
        var result = new List<(Vector2, int, int)>();
        for (int i = 0; i < segments.Count; i++)
        {
            for (int j = i + 1; j < segments.Count; j++)
            {
                var inter = GetTrueSegmentIntersection(
                    segments[i].P1, segments[i].P2,
                    segments[j].P1, segments[j].P2);
                if (inter.HasValue)
                    result.Add((inter.Value, i, j));
            }
        }
        return result;
    }

    /// <summary>
    /// Port of OSM2World's GeometryUtil.getTrueLineSegmentIntersection().
    /// Returns null if segments share an endpoint (within snap distance for float precision).
    /// </summary>
    private static Vector2? GetTrueSegmentIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        if (Vector2.DistanceSquared(a1, b1) < SNAP_DISTANCE_SQ ||
            Vector2.DistanceSquared(a1, b2) < SNAP_DISTANCE_SQ ||
            Vector2.DistanceSquared(a2, b1) < SNAP_DISTANCE_SQ ||
            Vector2.DistanceSquared(a2, b2) < SNAP_DISTANCE_SQ)
        {
            return null;
        }
        return GetSegmentIntersection(a1, a2, b1, b2);
    }

    /// <summary>
    /// Port of OSM2World's GeometryUtil.getLineSegmentIntersection().
    /// </summary>
    private static Vector2? GetSegmentIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
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

    #endregion

    #region Geometry Helpers

    /// <summary>
    /// Direction angle matching OSM2World's VectorXZ.angle().
    /// Returns angle in [0, 2π), measured clockwise from +Y axis.
    /// </summary>
    private static float DirectionAngle(Vector2 dir)
    {
        float angle = MathF.Atan2(dir.X, dir.Y);
        if (angle < 0) angle += 2f * MathF.PI;
        return angle;
    }

    /// <summary>
    /// Signed area of a polygon (open vertex list).
    /// Positive = CCW, Negative = CW.
    /// </summary>
    public static float SignedArea(List<Vector2> polygon)
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
    /// Gets a point inside the polygon for containment testing.
    /// Port of OSM2World's PolygonShapeXZ.getPointInside():
    ///   1. Try the polygon centroid
    ///   2. If centroid is outside, fall back to centroid of first triangulated triangle
    /// </summary>
    private static Vector2 GetPointInside(List<Vector2> polygon)
    {
        if (polygon.Count < 3) return polygon[0];

        // First try: polygon centroid (matches Java getCentroid())
        var centroid = PolygonCentroid(polygon);
        if (PointInPolygon(centroid, polygon))
            return centroid;

        // Fallback: centroid of the first triangle from ear-clipping
        // For concave polygons where centroid is outside
        return (polygon[0] + polygon[1] + polygon[2]) / 3f;
    }

    /// <summary>
    /// Computes the centroid of a polygon using the shoelace-based formula.
    /// Port of OSM2World's SimplePolygonXZ.getCentroid().
    /// </summary>
    private static Vector2 PolygonCentroid(List<Vector2> polygon)
    {
        float area = SignedArea(polygon);
        if (MathF.Abs(area) < 1e-10f)
        {
            var sum = Vector2.Zero;
            foreach (var p in polygon) sum += p;
            return sum / polygon.Count;
        }

        float cx = 0, cy = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            float factor = polygon[i].X * polygon[next].Y - polygon[next].X * polygon[i].Y;
            cx += (polygon[i].X + polygon[next].X) * factor;
            cy += (polygon[i].Y + polygon[next].Y) * factor;
        }
        float areaFactor = 1f / (6f * area);
        return new Vector2(areaFactor * cx, areaFactor * cy);
    }

    /// <summary>
    /// Point-in-polygon test using ray casting.
    /// Port of OSM2World's SimplePolygonShapeXZ.contains().
    /// </summary>
    public static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                (point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y)
                 / (polygon[j].Y - polygon[i].Y) + polygon[i].X))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Insert a point into the polygon on its closest edge.
    /// Port of OSM2World's GeometryUtil.insertIntoPolygon().
    /// If point is within snapDistance of an existing vertex, returns polygon unchanged.
    /// </summary>
    public static List<Vector2> InsertIntoPolygon(List<Vector2> polygon, Vector2 point, float snapDistance)
    {
        int closestIdx = -1;
        float closestDist = float.MaxValue;

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            float dist = DistancePointToSegment(point, polygon[i], polygon[next]);
            if (dist < closestDist)
            {
                closestDist = dist;
                closestIdx = i;
            }
        }
        if (closestIdx < 0) return new List<Vector2>(polygon);

        int closestNext = (closestIdx + 1) % polygon.Count;
        if (Vector2.Distance(polygon[closestIdx], point) <= snapDistance ||
            Vector2.Distance(polygon[closestNext], point) <= snapDistance)
        {
            return new List<Vector2>(polygon);
        }

        var result = new List<Vector2>(polygon);
        result.Insert(closestIdx + 1, point);
        return result;
    }

    /// <summary>
    /// Distance from a point to a line segment.
    /// Port of JTS LineSegment.distance() used by OSM2World.
    /// </summary>
    public static float DistancePointToSegment(Vector2 point, Vector2 segA, Vector2 segB)
    {
        var ab = segB - segA;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-10f) return Vector2.Distance(point, segA);
        float t = MathF.Max(0, MathF.Min(1, Vector2.Dot(point - segA, ab) / lenSq));
        return Vector2.Distance(point, segA + ab * t);
    }

    /// <summary>
    /// Distance from a point to an infinite line defined by two points.
    /// Port of OSM2World's GeometryUtil.distanceFromLine().
    /// Unlike DistancePointToSegment, this does NOT clamp to segment endpoints.
    /// </summary>
    public static float DistanceFromLine(Vector2 point, Vector2 lineP1, Vector2 lineP2)
    {
        var lineDir = lineP2 - lineP1;
        float lineLen = lineDir.Length();
        if (lineLen < 1e-10f) return Vector2.Distance(point, lineP1);
        // Perpendicular distance = |cross(lineDir, point - lineP1)| / |lineDir|
        float cross = lineDir.X * (point.Y - lineP1.Y) - lineDir.Y * (point.X - lineP1.X);
        return MathF.Abs(cross) / lineLen;
    }

    /// <summary>
    /// Linear interpolation of a value between two points based on distance.
    /// Port of OSM2World's GeometryUtil.interpolateValue().
    /// </summary>
    public static float InterpolateValue(Vector2 pos, Vector2 p1, float val1, Vector2 p2, float val2)
    {
        float d1 = Vector2.Distance(p1, pos);
        float d2 = Vector2.Distance(pos, p2);
        float total = d1 + d2;
        if (total < 1e-10f) return val1;
        float ratio = d1 / total;
        return val1 * (1f - ratio) + val2 * ratio;
    }

    #endregion

    #region PointPool

    /// <summary>
    /// Manages unique 2D points with distance-based snapping.
    /// Replacement for Java's exact VectorXZ equality, needed for float precision.
    /// </summary>
    private class PointPool
    {
        private readonly List<Vector2> _points = new();
        private readonly float _snapDistSq;

        public PointPool(float snapDistance) => _snapDistSq = snapDistance * snapDistance;
        public int Count => _points.Count;
        public Vector2 Get(int id) => _points[id];

        public int GetOrAdd(Vector2 p)
        {
            int existing = FindNearest(p);
            if (existing >= 0 && Vector2.DistanceSquared(_points[existing], p) < _snapDistSq)
                return existing;
            _points.Add(p);
            return _points.Count - 1;
        }

        public int FindNearest(Vector2 p)
        {
            if (_points.Count == 0) return -1;
            int bestId = 0;
            float bestDist = Vector2.DistanceSquared(_points[0], p);
            for (int i = 1; i < _points.Count; i++)
            {
                float dist = Vector2.DistanceSquared(_points[i], p);
                if (dist < bestDist) { bestDist = dist; bestId = i; }
            }
            return bestId;
        }
    }

    #endregion
}
