namespace BeamNG.Procedural3D.Builders;

using System.Numerics;

/// <summary>
/// Triangulates 2D polygons (with optional holes) using the Earcut algorithm.
/// Used for building floors, flat roofs, and any planar polygon surfaces.
/// Port of the earcut4j algorithm (same algorithm used by OSM2World).
/// </summary>
public static class PolygonTriangulator
{
    /// <summary>
    /// Triangulates a simple polygon (no holes) given as a list of 2D vertices.
    /// Returns triangle indices into the input vertex array (groups of 3).
    /// </summary>
    /// <param name="polygon">Outer ring vertices in counter-clockwise order. Must not be closed (no duplicate last vertex).</param>
    public static List<int> Triangulate(IReadOnlyList<Vector2> polygon)
    {
        return Triangulate(polygon, null);
    }

    /// <summary>
    /// Triangulates a polygon with optional holes.
    /// Returns triangle indices into a flattened vertex array (outer ring + all holes concatenated).
    /// </summary>
    /// <param name="outerRing">Outer ring vertices in counter-clockwise order.</param>
    /// <param name="holes">Optional inner rings (holes) in clockwise order.</param>
    public static List<int> Triangulate(IReadOnlyList<Vector2> outerRing, IReadOnlyList<IReadOnlyList<Vector2>>? holes)
    {
        var result = new List<int>();

        if (outerRing.Count < 3)
            return result;

        // Flatten all vertices into a single coordinate array
        var coords = new List<double>();
        var holeIndices = new List<int>();

        // Add outer ring
        foreach (var v in outerRing)
        {
            coords.Add(v.X);
            coords.Add(v.Y);
        }

        // Add holes
        if (holes != null)
        {
            foreach (var hole in holes)
            {
                holeIndices.Add(coords.Count / 2);
                foreach (var v in hole)
                {
                    coords.Add(v.X);
                    coords.Add(v.Y);
                }
            }
        }

        // Run earcut
        var triangles = Earcut(coords, holeIndices.Count > 0 ? holeIndices : null, 2);
        result.AddRange(triangles);

        return result;
    }

    /// <summary>
    /// Computes the signed area of a polygon. Positive = CCW, Negative = CW.
    /// </summary>
    public static float SignedArea(IReadOnlyList<Vector2> polygon)
    {
        float area = 0;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            area += (polygon[j].X - polygon[i].X) * (polygon[i].Y + polygon[j].Y);
        }
        return area * 0.5f;
    }

    /// <summary>
    /// Ensures a polygon is in counter-clockwise winding order.
    /// Returns a new list if reversal was needed, otherwise returns the input.
    /// </summary>
    public static IReadOnlyList<Vector2> EnsureCCW(IReadOnlyList<Vector2> polygon)
    {
        if (SignedArea(polygon) < 0)
            return polygon; // Already CCW (negative area in screen coords = CCW in math coords)

        // For our coordinate system: positive area = CCW
        if (SignedArea(polygon) > 0)
            return polygon;

        var reversed = new List<Vector2>(polygon);
        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Ensures a polygon is in clockwise winding order (for holes).
    /// </summary>
    public static IReadOnlyList<Vector2> EnsureCW(IReadOnlyList<Vector2> polygon)
    {
        if (SignedArea(polygon) < 0)
            return polygon;

        var reversed = new List<Vector2>(polygon);
        reversed.Reverse();
        return reversed;
    }

    #region Earcut Implementation

    // Earcut algorithm ported from earcut4j / mapbox/earcut
    // License: ISC (compatible with our project)

    private static List<int> Earcut(List<double> data, List<int>? holeIndices, int dim)
    {
        var result = new List<int>();
        bool hasHoles = holeIndices != null && holeIndices.Count > 0;
        int outerLen = hasHoles ? holeIndices![0] * dim : data.Count;

        var outerNode = LinkedList(data, 0, outerLen, dim, true);
        if (outerNode == null || outerNode.next == outerNode.prev)
            return result;

        if (hasHoles)
            outerNode = EliminateHoles(data, holeIndices!, outerNode, dim);

        double minX = 0, minY = 0, maxX = 0, maxY = 0, invSize = 0;

        // If the polygon is large enough, index it for faster point-in-triangle checks
        if (data.Count > 80 * dim)
        {
            minX = maxX = data[0];
            minY = maxY = data[1];

            for (int i = dim; i < outerLen; i += dim)
            {
                double x = data[i];
                double y = data[i + 1];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

            invSize = Math.Max(maxX - minX, maxY - minY);
            invSize = invSize != 0 ? 32767.0 / invSize : 0;
        }

        EarcutLinked(outerNode, result, dim, minX, minY, invSize, 0);

        return result;
    }

    private sealed class Node
    {
        public int i;
        public double x;
        public double y;
        public Node prev = null!;
        public Node next = null!;
        public int z;
        public Node? prevZ;
        public Node? nextZ;
        public bool steiner;

        public Node(int i, double x, double y)
        {
            this.i = i;
            this.x = x;
            this.y = y;
        }
    }

    private static Node? LinkedList(List<double> data, int start, int end, int dim, bool clockwise)
    {
        Node? last = null;

        if (clockwise == (SignedAreaDouble(data, start, end, dim) > 0))
        {
            for (int i = start; i < end; i += dim)
                last = InsertNode(i, data[i], data[i + 1], last);
        }
        else
        {
            for (int i = end - dim; i >= start; i -= dim)
                last = InsertNode(i, data[i], data[i + 1], last);
        }

        if (last != null && Equals(last, last.next))
        {
            RemoveNode(last);
            last = last.next;
        }

        if (last == null) return null;

        last.next.prev = last;
        last.prev.next = last;

        return last;
    }

    private static Node FilterPoints(Node start, Node? end = null)
    {
        end ??= start;

        var p = start;
        bool again;
        do
        {
            again = false;

            if (!p.steiner && (Equals(p, p.next) || Area(p.prev, p, p.next) == 0))
            {
                RemoveNode(p);
                p = end = p.prev;
                if (p == p.next) break;
                again = true;
            }
            else
            {
                p = p.next;
            }
        } while (again || p != end);

        return end;
    }

    private static void EarcutLinked(Node? ear, List<int> triangles, int dim, double minX, double minY, double invSize, int pass)
    {
        if (ear == null) return;

        if (pass == 0 && invSize != 0)
            IndexCurve(ear, minX, minY, invSize);

        var stop = ear;
        Node? prev, next;

        while (ear!.prev != ear.next)
        {
            prev = ear.prev;
            next = ear.next;

            if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
            {
                triangles.Add(prev.i / dim);
                triangles.Add(ear.i / dim);
                triangles.Add(next.i / dim);

                RemoveNode(ear);

                ear = next.next;
                stop = next.next;
                continue;
            }

            ear = next;

            if (ear == stop)
            {
                if (pass == 0)
                {
                    EarcutLinked(FilterPoints(ear), triangles, dim, minX, minY, invSize, 1);
                }
                else if (pass == 1)
                {
                    ear = CureLocalIntersections(FilterPoints(ear), triangles, dim);
                    EarcutLinked(ear, triangles, dim, minX, minY, invSize, 2);
                }
                else if (pass == 2)
                {
                    SplitEarcut(ear, triangles, dim, minX, minY, invSize);
                }

                break;
            }
        }
    }

    private static bool IsEar(Node ear)
    {
        var a = ear.prev;
        var b = ear;
        var c = ear.next;

        if (Area(a, b, c) >= 0) return false;

        var p = ear.next.next;
        while (p != ear.prev)
        {
            if (PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, p.x, p.y) &&
                Area(p.prev, p, p.next) >= 0)
                return false;
            p = p.next;
        }

        return true;
    }

    private static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
    {
        var a = ear.prev;
        var b = ear;
        var c = ear.next;

        if (Area(a, b, c) >= 0) return false;

        double minTX = Math.Min(a.x, Math.Min(b.x, c.x));
        double minTY = Math.Min(a.y, Math.Min(b.y, c.y));
        double maxTX = Math.Max(a.x, Math.Max(b.x, c.x));
        double maxTY = Math.Max(a.y, Math.Max(b.y, c.y));

        int minZ = ZOrder(minTX, minTY, minX, minY, invSize);
        int maxZ = ZOrder(maxTX, maxTY, minX, minY, invSize);

        var p = ear.prevZ;
        var n = ear.nextZ;

        while (p != null && p.z >= minZ && n != null && n.z <= maxZ)
        {
            if (p.i != a.i && p.i != c.i && PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, p.x, p.y) && Area(p.prev, p, p.next) >= 0)
                return false;
            p = p.prevZ;

            if (n.i != a.i && n.i != c.i && PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, n.x, n.y) && Area(n.prev, n, n.next) >= 0)
                return false;
            n = n.nextZ;
        }

        while (p != null && p.z >= minZ)
        {
            if (p.i != a.i && p.i != c.i && PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, p.x, p.y) && Area(p.prev, p, p.next) >= 0)
                return false;
            p = p.prevZ;
        }

        while (n != null && n.z <= maxZ)
        {
            if (n.i != a.i && n.i != c.i && PointInTriangle(a.x, a.y, b.x, b.y, c.x, c.y, n.x, n.y) && Area(n.prev, n, n.next) >= 0)
                return false;
            n = n.nextZ;
        }

        return true;
    }

    private static Node CureLocalIntersections(Node start, List<int> triangles, int dim)
    {
        var p = start;
        do
        {
            var a = p.prev;
            var b = p.next.next;

            if (!Equals(a, b) && Intersects(a, p, p.next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                triangles.Add(a.i / dim);
                triangles.Add(p.i / dim);
                triangles.Add(b.i / dim);

                RemoveNode(p);
                RemoveNode(p.next);

                p = start = b;
            }

            p = p.next;
        } while (p != start);

        return FilterPoints(p);
    }

    private static void SplitEarcut(Node start, List<int> triangles, int dim, double minX, double minY, double invSize)
    {
        var a = start;
        do
        {
            var b = a.next.next;
            while (b != a.prev)
            {
                if (a.i != b.i && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);

                    a = FilterPoints(a, a.next);
                    c = FilterPoints(c, c.next);

                    EarcutLinked(a, triangles, dim, minX, minY, invSize, 0);
                    EarcutLinked(c, triangles, dim, minX, minY, invSize, 0);
                    return;
                }

                b = b.next;
            }

            a = a.next;
        } while (a != start);
    }

    private static Node EliminateHoles(List<double> data, List<int> holeIndices, Node outerNode, int dim)
    {
        var queue = new List<Node>();

        for (int i = 0; i < holeIndices.Count; i++)
        {
            int start = holeIndices[i] * dim;
            int end = i < holeIndices.Count - 1 ? holeIndices[i + 1] * dim : data.Count;
            var list = LinkedList(data, start, end, dim, false);
            if (list != null)
            {
                if (list == list.next) list.steiner = true;
                queue.Add(GetLeftmost(list));
            }
        }

        queue.Sort((a, b) => a.x.CompareTo(b.x));

        foreach (var node in queue)
        {
            outerNode = EliminateHole(node, outerNode);
        }

        return outerNode;
    }

    private static Node EliminateHole(Node hole, Node outerNode)
    {
        var bridge = FindHoleBridge(hole, outerNode);
        if (bridge == null)
        {
            return outerNode;
        }

        var bridgeReverse = SplitPolygon(bridge, hole);

        FilterPoints(bridgeReverse, bridgeReverse.next);
        return FilterPoints(bridge, bridge.next);
    }

    private static Node? FindHoleBridge(Node hole, Node outerNode)
    {
        var p = outerNode;
        double hx = hole.x;
        double hy = hole.y;
        double qx = double.NegativeInfinity;
        Node? m = null;

        do
        {
            if (hy <= p.y && hy >= p.next.y && p.next.y != p.y)
            {
                double x = p.x + (hy - p.y) * (p.next.x - p.x) / (p.next.y - p.y);
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = p.x < p.next.x ? p : p.next;
                    if (x == hx) return m;
                }
            }

            p = p.next;
        } while (p != outerNode);

        if (m == null) return null;

        var stop = m;
        double mx = m.x;
        double my = m.y;
        double tanMin = double.PositiveInfinity;

        p = m;
        do
        {
            if (hx >= p.x && p.x >= mx && hx != p.x &&
                PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.x, p.y))
            {
                double tan = Math.Abs(hy - p.y) / (hx - p.x);

                if (LocallyInside(p, hole) && (tan < tanMin || (tan == tanMin && (p.x > m.x || (p.x == m.x && SectorContainsSector(m, p))))))
                {
                    m = p;
                    tanMin = tan;
                }
            }

            p = p.next;
        } while (p != stop);

        return m;
    }

    private static bool SectorContainsSector(Node m, Node p)
    {
        return Area(m.prev, m, p.prev) < 0 && Area(p.next, m, m.next) < 0;
    }

    private static void IndexCurve(Node start, double minX, double minY, double invSize)
    {
        var p = start;
        do
        {
            if (p.z == 0)
                p.z = ZOrder(p.x, p.y, minX, minY, invSize);

            p.prevZ = p.prev;
            p.nextZ = p.next;
            p = p.next;
        } while (p != start);

        p.prevZ!.nextZ = null;
        p.prevZ = null;

        SortLinked(p);
    }

    private static Node SortLinked(Node? list)
    {
        int inSize = 1;
        int numMerges;

        do
        {
            var p = list;
            list = null;
            Node? tail = null;
            numMerges = 0;

            while (p != null)
            {
                numMerges++;
                var q = p;
                int pSize = 0;
                for (int i = 0; i < inSize; i++)
                {
                    pSize++;
                    q = q.nextZ;
                    if (q == null) break;
                }

                int qSize = inSize;

                while (pSize > 0 || (qSize > 0 && q != null))
                {
                    Node e;
                    if (pSize != 0 && (qSize == 0 || q == null || p!.z <= q.z))
                    {
                        e = p!;
                        p = p!.nextZ;
                        pSize--;
                    }
                    else
                    {
                        e = q!;
                        q = q!.nextZ;
                        qSize--;
                    }

                    if (tail != null) tail.nextZ = e;
                    else list = e;

                    e.prevZ = tail;
                    tail = e;
                }

                p = q;
            }

            tail!.nextZ = null;
            inSize *= 2;
        } while (numMerges > 1);

        return list!;
    }

    private static int ZOrder(double x, double y, double minX, double minY, double invSize)
    {
        int lx = (int)(((x - minX) * invSize) + 0.5);
        int ly = (int)(((y - minY) * invSize) + 0.5);

        lx = (lx | (lx << 8)) & 0x00FF00FF;
        lx = (lx | (lx << 4)) & 0x0F0F0F0F;
        lx = (lx | (lx << 2)) & 0x33333333;
        lx = (lx | (lx << 1)) & 0x55555555;

        ly = (ly | (ly << 8)) & 0x00FF00FF;
        ly = (ly | (ly << 4)) & 0x0F0F0F0F;
        ly = (ly | (ly << 2)) & 0x33333333;
        ly = (ly | (ly << 1)) & 0x55555555;

        return lx | (ly << 1);
    }

    private static Node GetLeftmost(Node start)
    {
        var p = start;
        var leftmost = start;
        do
        {
            if (p.x < leftmost.x || (p.x == leftmost.x && p.y < leftmost.y))
                leftmost = p;
            p = p.next;
        } while (p != start);

        return leftmost;
    }

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        return (cx - px) * (ay - py) - (ax - px) * (cy - py) >= 0 &&
               (ax - px) * (by - py) - (bx - px) * (ay - py) >= 0 &&
               (bx - px) * (cy - py) - (cx - px) * (by - py) >= 0;
    }

    private static bool IsValidDiagonal(Node a, Node b)
    {
        return a.next.i != b.i && a.prev.i != b.i && !IntersectsPolygon(a, b) &&
               ((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b)) &&
                (Area(a.prev, a, b.prev) != 0 || Area(a, b.prev, b) != 0) ||
                Equals(a, b) && Area(a.prev, a, a.next) > 0 && Area(b.prev, b, b.next) > 0);
    }

    private static double Area(Node p, Node q, Node r)
    {
        return (q.y - p.y) * (r.x - q.x) - (q.x - p.x) * (r.y - q.y);
    }

    private static bool Equals(Node p1, Node p2)
    {
        return p1.x == p2.x && p1.y == p2.y;
    }

    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Sign(Area(p1, q1, p2));
        int o2 = Sign(Area(p1, q1, q2));
        int o3 = Sign(Area(p2, q2, p1));
        int o4 = Sign(Area(p2, q2, q1));

        if (o1 != o2 && o3 != o4) return true;

        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;

        return false;
    }

    private static bool OnSegment(Node p, Node q, Node r)
    {
        return q.x <= Math.Max(p.x, r.x) && q.x >= Math.Min(p.x, r.x) &&
               q.y <= Math.Max(p.y, r.y) && q.y >= Math.Min(p.y, r.y);
    }

    private static int Sign(double num)
    {
        return num > 0 ? 1 : num < 0 ? -1 : 0;
    }

    private static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.i != a.i && p.next.i != a.i && p.i != b.i && p.next.i != b.i &&
                Intersects(p, p.next, a, b))
                return true;
            p = p.next;
        } while (p != a);

        return false;
    }

    private static bool LocallyInside(Node a, Node b)
    {
        return Area(a.prev, a, a.next) < 0
            ? Area(a, b, a.next) >= 0 && Area(a, a.prev, b) >= 0
            : Area(a, b, a.prev) < 0 || Area(a, a.next, b) < 0;
    }

    private static bool MiddleInside(Node a, Node b)
    {
        var p = a;
        bool inside = false;
        double px = (a.x + b.x) / 2;
        double py = (a.y + b.y) / 2;
        do
        {
            if ((p.y > py) != (p.next.y > py) && p.next.y != p.y &&
                px < (p.next.x - p.x) * (py - p.y) / (p.next.y - p.y) + p.x)
                inside = !inside;
            p = p.next;
        } while (p != a);

        return inside;
    }

    private static Node SplitPolygon(Node a, Node b)
    {
        var a2 = new Node(a.i, a.x, a.y);
        var b2 = new Node(b.i, b.x, b.y);
        var an = a.next;
        var bp = b.prev;

        a.next = b;
        b.prev = a;

        a2.next = an;
        an.prev = a2;

        b2.next = a2;
        a2.prev = b2;

        bp.next = b2;
        b2.prev = bp;

        return b2;
    }

    private static Node InsertNode(int i, double x, double y, Node? last)
    {
        var p = new Node(i, x, y);

        if (last == null)
        {
            p.prev = p;
            p.next = p;
        }
        else
        {
            p.next = last.next;
            p.prev = last;
            last.next.prev = p;
            last.next = p;
        }

        return p;
    }

    private static void RemoveNode(Node p)
    {
        p.next.prev = p.prev;
        p.prev.next = p.next;

        if (p.prevZ != null) p.prevZ.nextZ = p.nextZ;
        if (p.nextZ != null) p.nextZ.prevZ = p.prevZ;
    }

    private static double SignedAreaDouble(List<double> data, int start, int end, int dim)
    {
        double sum = 0;
        for (int i = start, j = end - dim; i < end; i += dim)
        {
            sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]);
            j = i;
        }
        return sum;
    }

    #endregion
}
