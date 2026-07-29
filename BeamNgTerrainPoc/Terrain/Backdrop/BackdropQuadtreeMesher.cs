using System.Numerics;
using BeamNG.Procedural3D.Core;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

internal readonly record struct LeafCell(int X, int Y, int Width, int Height);   // lattice units

/// <summary>Result of <see cref="BackdropQuadtreeMesher.MeshChunk"/> (spec §8/§9).</summary>
public sealed class BackdropChunkMeshResult
{
    public required Mesh VisualMesh { get; init; }     // triangulated surface (+ seam skirt)
    public required int LeafCount { get; init; }
    public required int SurfaceTriangleCount { get; init; }  // triangles excluding skirt
    public required int SurfaceVertexCount { get; init; }    // vertices excluding skirt
}

/// <summary>
///     Restricted-quadtree adaptive mesher for one backdrop chunk (spec §8). Refines a chunk's
///     lattice-aligned root cell down towards a per-cell vertical-error tolerance (near/far lerp)
///     plus importance-source overrides (e.g. the edge band), snapping every split that lands on a
///     chunk border to the shared <see cref="BackdropEdgeSubdivider"/> set so neighboring chunks
///     produce bitwise-identical seams. Triangulation is added by the Task-7 partial.
/// </summary>
public sealed partial class BackdropQuadtreeMesher
{
    private readonly BackdropHeightField _field;
    private readonly BackdropMesherOptions _options;
    private readonly IReadOnlyList<IBackdropImportanceSource> _importance;
    private int _fallbackCount;

    public BackdropQuadtreeMesher(BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importanceSources)
    {
        _field = field;
        _options = options;
        _importance = importanceSources;
    }

    /// <summary>
    ///     Number of times the most recent <see cref="RefineChunk"/> call accepted a leaf that still
    ///     wanted to split (tolerance-exceeded in <c>Refine</c>, or a >1-level neighbor gap in
    ///     <c>Balance</c>) but could not, because neither axis had a border-matching split point
    ///     (spec §13 invariant not fully satisfiable without breaking the border-seam guarantee).
    ///     Zero in the common case; Task 7/8 can surface a non-zero value as a quality warning.
    /// </summary>
    internal int LastFallbackCount { get; private set; }

    /// <summary>
    ///     Triangulates one chunk's refined+balanced leaves (spec §9): every leaf emits a triangle
    ///     fan from its center to its ordered boundary-vertex loop, where the loop includes every
    ///     lattice vertex any same-chunk neighbor or the shared chunk-border subdivision contributes
    ///     along its edges. A finer neighbor's corner is therefore always part of the coarser leaf's
    ///     loop, so there are no T-vertices — crack-free by construction, no transition-case analysis.
    ///     Unit cells (no extra edge vertices) skip the center fan and emit 2 triangles directly.
    /// </summary>
    public BackdropChunkMeshResult MeshChunk(BackdropChunkDefinition chunk) => MeshChunk(chunk, out _);

    /// <summary>Same as <see cref="MeshChunk(BackdropChunkDefinition)"/> but also surfaces the
    /// refined+balanced leaves so callers needing them (the quadtree-level debug artifact) don't
    /// have to run the whole refinement a second time (perf plan §2). Refinement is deterministic,
    /// so the surfaced list is identical to what a separate <see cref="RefineChunk"/> would return.</summary>
    internal BackdropChunkMeshResult MeshChunk(BackdropChunkDefinition chunk, out IReadOnlyList<LeafCell> refinedLeaves)
    {
        var borders = ComputeBorderSets(chunk);
        var leaves = RefineChunk(chunk, borders);
        refinedLeaves = leaves;

        // Index all leaf-corner lattice vertices by column and row for edge-vertex lookup.
        var byColumn = new Dictionary<int, SortedSet<int>>();
        var byRow = new Dictionary<int, SortedSet<int>>();
        void Register(int ix, int iy)
        {
            if (!byColumn.TryGetValue(ix, out var col)) byColumn[ix] = col = [];
            col.Add(iy);
            if (!byRow.TryGetValue(iy, out var row)) byRow[iy] = row = [];
            row.Add(ix);
        }
        foreach (var leaf in leaves)
        {
            Register(leaf.X, leaf.Y); Register(leaf.X + leaf.Width, leaf.Y);
            Register(leaf.X, leaf.Y + leaf.Height); Register(leaf.X + leaf.Width, leaf.Y + leaf.Height);
        }
        // Chunk-border vertices from the shared subdivision (both neighbors see the same set) —
        // the same four sets RefineChunk snapped its border splits to (perf plan §3).
        foreach (var iy in borders.West)
            Register(chunk.LatticeX, iy);
        foreach (var iy in borders.East)
            Register(chunk.LatticeX + chunk.LatticeWidth, iy);
        foreach (var ix in borders.South)
            Register(ix, chunk.LatticeY);
        foreach (var ix in borders.North)
            Register(ix, chunk.LatticeY + chunk.LatticeHeight);

        var mesh = new Mesh { Name = Path.GetFileNameWithoutExtension(chunk.DaeFileName), MaterialName = chunk.MaterialName };
        var vertexLookup = new Dictionary<(int, int), int>();
        int GetLatticeVertex(int ix, int iy)
        {
            if (vertexLookup.TryGetValue((ix, iy), out var idx)) return idx;
            var wx = ix * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var wy = iy * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var z = _field.SampleWorldZ(wx, wy);
            idx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex(new Vector3((float)wx, (float)wy, (float)z)));  // normal/uv filled in below
            vertexLookup[(ix, iy)] = idx;
            return idx;
        }

        foreach (var leaf in leaves)
        {
            // Boundary loop counter-clockwise starting at SW corner:
            var loop = new List<int>();
            void EdgePoints(bool vertical, int fixedCoord, int from, int to, bool ascending)
            {
                var set = vertical ? byColumn[fixedCoord] : byRow[fixedCoord];
                var range = set.GetViewBetween(Math.Min(from, to), Math.Max(from, to));
                var points = ascending ? range.ToList() : range.Reverse().ToList();
                points.RemoveAt(points.Count - 1);          // end corner belongs to the next edge
                foreach (var v in points)
                    loop.Add(vertical ? GetLatticeVertex(fixedCoord, v) : GetLatticeVertex(v, fixedCoord));
            }
            EdgePoints(false, leaf.Y, leaf.X, leaf.X + leaf.Width, ascending: true);              // south edge W→E
            EdgePoints(true, leaf.X + leaf.Width, leaf.Y, leaf.Y + leaf.Height, ascending: true); // east edge S→N
            EdgePoints(false, leaf.Y + leaf.Height, leaf.X + leaf.Width, leaf.X, ascending: false); // north E→W
            EdgePoints(true, leaf.X, leaf.Y + leaf.Height, leaf.Y, ascending: false);             // west N→S

            if (loop.Count == 4)
            {
                mesh.Triangles.Add(new Triangle(loop[0], loop[1], loop[2]));
                mesh.Triangles.Add(new Triangle(loop[0], loop[2], loop[3]));
                continue;
            }
            // Fan from the leaf center (unique vertex, not on the lattice dictionary).
            var cx = (leaf.X + leaf.X + leaf.Width) * 0.5 * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var cy = (leaf.Y + leaf.Y + leaf.Height) * 0.5 * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var centerIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex(new Vector3((float)cx, (float)cy, (float)_field.SampleWorldZ(cx, cy))));
            for (var i = 0; i < loop.Count; i++)
                mesh.Triangles.Add(new Triangle(centerIdx, loop[i], loop[(i + 1) % loop.Count]));
        }

        var surfaceTriangles = mesh.Triangles.Count;
        var surfaceVertices = mesh.Vertices.Count;

        // Normals from the height-field gradient (central differences, step = lattice unit — spec §8:
        // gradient normals, not per-face, so lighting doesn't reveal the triangulation).
        var h = _options.LatticeUnitMeters;
        for (var i = 0; i < mesh.Vertices.Count; i++)
        {
            var pos = mesh.Vertices[i].Position;
            var dzdx = (_field.SampleWorldZ(pos.X + h, pos.Y) - _field.SampleWorldZ(pos.X - h, pos.Y)) / (2 * h);
            var dzdy = (_field.SampleWorldZ(pos.X, pos.Y + h) - _field.SampleWorldZ(pos.X, pos.Y - h)) / (2 * h);
            var normal = Vector3.Normalize(new Vector3((float)-dzdx, (float)-dzdy, 1f));
            var uv = new Vector2(
                (float)((pos.X - chunk.WorldMinX) / (chunk.WorldMaxX - chunk.WorldMinX)),
                (float)((pos.Y - chunk.WorldMinY) / (chunk.WorldMaxY - chunk.WorldMinY)));
            mesh.Vertices[i] = mesh.Vertices[i].WithNormal(normal).WithUV(uv);
        }

        // No physics copy of the surface is built: backdrop collision (when enabled) is scene-level —
        // the TSStatic entry's collisionType "Visible Mesh Final" makes the game build physics from
        // this visual mesh itself, so an embedded Colmesh would only double the DAE payload.

        // Seam skirt (spec §7.5): vertical flange along whichever of the chunk's 4 borders coincide
        // with a terrain-rect edge (lattice coordinate 0 or `terrainSize`), dropping straight down from
        // each already-registered seam vertex by SeamSkirtDepthMeters. Reuses `byColumn`/`byRow` (already
        // populated with the FULL shared border subdivision via the Register() calls above) and
        // `vertexLookup`/`GetLatticeVertex` — every one of those points is guaranteed to already be a
        // mesh vertex: leaves touching a chunk border have their split coordinates constrained to that
        // border's subdivision set (see Refine/Balance's `mustMatch`), and the Register() loop above adds
        // every subdivision point regardless of whether any leaf corner actually lands there. Appended
        // AFTER SurfaceTriangleCount/SurfaceVertexCount were captured, so the skirt stays identifiable.
        // The flange faces TOWARD the terrain interior (toward world origin), not away from it: a camera
        // standing on the terrain looks OUT through the LOD hairline crack at the seam, so the skirt's
        // front face must be the side visible from inside the terrain rect, or single-sided materials
        // cull it and the crack stays visible.
        void AppendSeamSkirt(Mesh visualMesh, BackdropChunkDefinition c)
        {
            var terrainSize = (int)Math.Round(2 * _options.HalfSizeMeters / _options.LatticeUnitMeters);
            var depth = (float)_options.SeamSkirtDepthMeters;

            Triangle Oriented(int i0, int i1, int i2, Vector3 towardTerrain)
            {
                var p0 = visualMesh.Vertices[i0].Position;
                var p1 = visualMesh.Vertices[i1].Position;
                var p2 = visualMesh.Vertices[i2].Position;
                var n = Vector3.Cross(p1 - p0, p2 - p0);
                var tri = new Triangle(i0, i1, i2);
                return Vector3.Dot(n, towardTerrain) >= 0 ? tri : tri.Reversed();
            }

            void EmitBorder(bool vertical, int fixedCoord, int rangeFrom, int rangeTo)
            {
                if (fixedCoord != 0 && fixedCoord != terrainSize) return;      // not on a terrain-rect edge
                var registered = vertical ? byColumn : byRow;
                if (!registered.TryGetValue(fixedCoord, out var points)) return;
                var lo = Math.Max(rangeFrom, 0);
                var hi = Math.Min(rangeTo, terrainSize);
                if (hi <= lo) return;                                          // corner touch only, no seam here

                var ordered = points.GetViewBetween(lo, hi).ToList();
                // fixedCoord == 0 is the terrain's west/south edge → terrain interior is +X/+Y from here;
                // fixedCoord == terrainSize is the east/north edge → terrain interior is −X/−Y from here.
                var towardTerrain = vertical
                    ? new Vector3(fixedCoord == 0 ? 1f : -1f, 0f, 0f)
                    : new Vector3(0f, fixedCoord == 0 ? 1f : -1f, 0f);
                var bottomCache = new Dictionary<int, int>();

                int Bottom(int topIdx)
                {
                    if (bottomCache.TryGetValue(topIdx, out var cached)) return cached;
                    var top = visualMesh.Vertices[topIdx];
                    var idx = visualMesh.Vertices.Count;
                    visualMesh.Vertices.Add(new Vertex(top.Position - new Vector3(0f, 0f, depth), towardTerrain, top.UV));
                    bottomCache[topIdx] = idx;
                    return idx;
                }

                for (var k = 0; k + 1 < ordered.Count; k++)
                {
                    var topA = vertical ? GetLatticeVertex(fixedCoord, ordered[k]) : GetLatticeVertex(ordered[k], fixedCoord);
                    var topB = vertical ? GetLatticeVertex(fixedCoord, ordered[k + 1]) : GetLatticeVertex(ordered[k + 1], fixedCoord);
                    var bottomA = Bottom(topA);
                    var bottomB = Bottom(topB);
                    visualMesh.Triangles.Add(Oriented(topA, topB, bottomB, towardTerrain));
                    visualMesh.Triangles.Add(Oriented(topA, bottomB, bottomA, towardTerrain));
                }
            }

            EmitBorder(true, c.LatticeX, c.LatticeY, c.LatticeY + c.LatticeHeight);                       // west
            EmitBorder(true, c.LatticeX + c.LatticeWidth, c.LatticeY, c.LatticeY + c.LatticeHeight);      // east
            EmitBorder(false, c.LatticeY, c.LatticeX, c.LatticeX + c.LatticeWidth);                       // south
            EmitBorder(false, c.LatticeY + c.LatticeHeight, c.LatticeX, c.LatticeX + c.LatticeWidth);     // north
        }

        if (_options.SeamSkirt)
            AppendSeamSkirt(mesh, chunk);

        return new BackdropChunkMeshResult
        {
            VisualMesh = mesh, LeafCount = leaves.Count,
            SurfaceTriangleCount = surfaceTriangles, SurfaceVertexCount = surfaceVertices
        };
    }

    internal IReadOnlyList<LeafCell> RefineChunk(BackdropChunkDefinition chunk)
        => RefineChunk(chunk, ComputeBorderSets(chunk));

    /// <summary>The shared border subdivisions of one chunk's four borders (Task 6 border rule) —
    /// computed once per chunk and consumed by both <see cref="RefineChunk(BackdropChunkDefinition)"/>
    /// (split snapping) and <see cref="MeshChunk(BackdropChunkDefinition, out IReadOnlyList{LeafCell})"/>
    /// (border-vertex registration), instead of each running the four
    /// <see cref="BackdropEdgeSubdivider.Subdivide"/> calls again (perf plan §3).</summary>
    private readonly record struct BorderSets(IReadOnlyList<int> West, IReadOnlyList<int> East,
        IReadOnlyList<int> South, IReadOnlyList<int> North);

    private BorderSets ComputeBorderSets(BackdropChunkDefinition chunk) => new(
        West: BackdropEdgeSubdivider.Subdivide(chunk.LatticeX, true,
            chunk.LatticeY, chunk.LatticeY + chunk.LatticeHeight, _field, _options, _importance),
        East: BackdropEdgeSubdivider.Subdivide(chunk.LatticeX + chunk.LatticeWidth, true,
            chunk.LatticeY, chunk.LatticeY + chunk.LatticeHeight, _field, _options, _importance),
        South: BackdropEdgeSubdivider.Subdivide(chunk.LatticeY, false,
            chunk.LatticeX, chunk.LatticeX + chunk.LatticeWidth, _field, _options, _importance),
        North: BackdropEdgeSubdivider.Subdivide(chunk.LatticeY + chunk.LatticeHeight, false,
            chunk.LatticeX, chunk.LatticeX + chunk.LatticeWidth, _field, _options, _importance));

    private IReadOnlyList<LeafCell> RefineChunk(BackdropChunkDefinition chunk, BorderSets borders)
    {
        _fallbackCount = 0;
        var leaves = new List<LeafCell>();
        Refine(new LeafCell(chunk.LatticeX, chunk.LatticeY, chunk.LatticeWidth, chunk.LatticeHeight),
            chunk, borders.West, borders.East, borders.South, borders.North, leaves);
        Balance(leaves, chunk, borders.West, borders.East, borders.South, borders.North);
        LastFallbackCount = _fallbackCount;
        return leaves;
    }

    private void Refine(LeafCell cell, BackdropChunkDefinition chunk,
        IReadOnlyList<int> west, IReadOnlyList<int> east,
        IReadOnlyList<int> south, IReadOnlyList<int> north, List<LeafCell> leaves)
    {
        var u = _options.LatticeUnitMeters;
        var half = _options.HalfSizeMeters;
        double minX = cell.X * u - half, minY = cell.Y * u - half;
        double maxX = minX + cell.Width * u, maxY = minY + cell.Height * u;
        var cellSize = Math.Max(cell.Width, cell.Height) * u;

        var needSplit = false;
        foreach (var source in _importance)
            if (source.RequiredMaxCellSizeMeters(minX, minY, maxX, maxY) is { } limit && cellSize > limit + 1e-9)
                needSplit = true;
        if (!needSplit && (cell.Width > 1 || cell.Height > 1))
            needSplit = ProbeVerticalError(minX, minY, maxX, maxY) > ToleranceAt(minX, minY, maxX, maxY);
        if (!needSplit || (cell.Width <= 1 && cell.Height <= 1))
        {
            leaves.Add(cell);
            return;
        }

        // X-split creates new vertices on the cell's south/north edges; if such an edge lies on a
        // chunk border, the split coordinate must belong to that border's shared subdivision.
        var splitX = ChooseSplit(cell.X, cell.X + cell.Width,
            mustMatch: CollectBorderSets(
                onSouthBorder: cell.Y == chunk.LatticeY ? south : null,
                onNorthBorder: cell.Y + cell.Height == chunk.LatticeY + chunk.LatticeHeight ? north : null));
        var splitY = ChooseSplit(cell.Y, cell.Y + cell.Height,
            mustMatch: CollectBorderSets(
                onSouthBorder: cell.X == chunk.LatticeX ? west : null,
                onNorthBorder: cell.X + cell.Width == chunk.LatticeX + chunk.LatticeWidth ? east : null));

        // splitX/splitY are null when that axis cannot split (size 1, or border set has no interior point).
        if (splitX == null && splitY == null) { _fallbackCount++; leaves.Add(cell); return; }

        foreach (var child in Split(cell, splitX, splitY))
            Refine(child, chunk, west, east, south, north, leaves);
    }

    // Grid-hash bucket side length (lattice units) for Balance's neighbor index. Any positive
    // constant is correct — it only trades bucket count vs. candidates-per-bucket; leaves are
    // registered along their PERIMETER only (see PerimeterBuckets), so a huge leaf costs
    // O(perimeter / BucketSize) buckets, not O(area / BucketSize²).
    // Must stay small: the edge band forces unit leaves, so a 64×64 bucket there held up to
    // ~4096 leaves and every neighbor query degenerated into a near-linear bucket scan
    // (perf plan §1 — estimated ~60–80 s per edge chunk at production defaults).
    private const int BucketSize = 4;

    /// <summary>
    ///     Restricted-quadtree balance pass: split any leaf whose shared-edge neighbor is more than
    ///     one level finer, iterating to a fixpoint. Re-splitting still snaps to the border sets so
    ///     the seam invariant survives the extra splits (spec §13).
    ///     Worklist-driven with a spatial-hash neighbor index (not an O(n²) full-rescan per pass):
    ///     production chunks can carry tens of thousands of edge-band-forced unit leaves, and the
    ///     original nested-loop-plus-restart-on-every-split implementation is O(n²) per pass with a
    ///     full rescan after each individual split — effectively O(n³) end to end, which hangs at
    ///     that scale. Only the neighbors of a leaf that actually changed are re-examined.
    /// </summary>
    private void Balance(List<LeafCell> leaves, BackdropChunkDefinition chunk,
        IReadOnlyList<int> west, IReadOnlyList<int> east, IReadOnlyList<int> south, IReadOnlyList<int> north)
    {
        var byId = new Dictionary<int, LeafCell>(leaves.Count);
        var spatial = new Dictionary<(int, int), HashSet<int>>();
        var nextId = 0;

        int AddLeaf(LeafCell cell)
        {
            var id = nextId++;
            byId[id] = cell;
            foreach (var bucket in PerimeterBuckets(cell))
            {
                if (!spatial.TryGetValue(bucket, out var set))
                    spatial[bucket] = set = [];
                set.Add(id);
            }
            return id;
        }

        void RemoveLeaf(int id)
        {
            var cell = byId[id];
            foreach (var bucket in PerimeterBuckets(cell))
                if (spatial.TryGetValue(bucket, out var set))
                {
                    set.Remove(id);
                    if (set.Count == 0) spatial.Remove(bucket);
                }
            byId.Remove(id);
        }

        // Deterministic worklist: always process the leaf ordered lowest (X, Y, size) first, so the
        // fixpoint is reached the same way regardless of dictionary/hash-set enumeration order.
        var worklist = new SortedSet<WorkItem>();
        var queued = new HashSet<int>();

        void Enqueue(int id)
        {
            if (!queued.Add(id)) return;
            var cell = byId[id];
            worklist.Add(new WorkItem(cell.X, cell.Y, Math.Max(cell.Width, cell.Height), id));
        }

        foreach (var cell in leaves)
            Enqueue(AddLeaf(cell));

        while (worklist.Count > 0)
        {
            var item = worklist.Min;
            worklist.Remove(item);
            queued.Remove(item.Id);
            if (!byId.TryGetValue(item.Id, out var a)) continue;   // already replaced by a split

            var la = Level(a);
            var neighborIds = FindNeighborIds(a, item.Id, byId, spatial);

            var violates = false;
            foreach (var nid in neighborIds)
                if (la - Level(byId[nid]) > 1) { violates = true; break; }
            if (!violates) continue;

            var splitX = ChooseSplit(a.X, a.X + a.Width, CollectBorderSets(
                onSouthBorder: a.Y == chunk.LatticeY ? south : null,
                onNorthBorder: a.Y + a.Height == chunk.LatticeY + chunk.LatticeHeight ? north : null));
            var splitY = ChooseSplit(a.Y, a.Y + a.Height, CollectBorderSets(
                onSouthBorder: a.X == chunk.LatticeX ? west : null,
                onNorthBorder: a.X + a.Width == chunk.LatticeX + chunk.LatticeWidth ? east : null));
            if (splitX == null && splitY == null) { _fallbackCount++; continue; }   // can't legally split, accept it

            RemoveLeaf(item.Id);
            foreach (var child in Split(a, splitX, splitY))
                Enqueue(AddLeaf(child));
            // The split leaf shrank; former neighbors may now be more than 1 level coarser than the
            // new children and need re-examining even if they weren't touched themselves.
            foreach (var nid in neighborIds)
                if (byId.ContainsKey(nid)) Enqueue(nid);
        }

        leaves.Clear();
        leaves.AddRange(byId.Values.OrderBy(c => c.X).ThenBy(c => c.Y));
    }

    private readonly record struct WorkItem(int X, int Y, int Size, int Id) : IComparable<WorkItem>
    {
        public int CompareTo(WorkItem other)
        {
            var c = X.CompareTo(other.X);
            if (c != 0) return c;
            c = Y.CompareTo(other.Y);
            if (c != 0) return c;
            c = Size.CompareTo(other.Size);
            if (c != 0) return c;
            return Id.CompareTo(other.Id);
        }
    }

    /// <summary>Buckets overlapping a 1-unit-thick ring just INSIDE the cell's 4 edges (not the full
    /// interior) — enough for any neighbor's edge-strip query to find this cell, at O(perimeter)
    /// cost instead of O(area).</summary>
    private static IEnumerable<(int, int)> PerimeterBuckets(LeafCell cell)
    {
        var seen = new HashSet<(int, int)>();
        void AddRange(int minX, int minY, int maxX, int maxY)
        {
            var bx0 = FloorDiv(minX, BucketSize);
            var bx1 = FloorDiv(maxX - 1, BucketSize);
            var by0 = FloorDiv(minY, BucketSize);
            var by1 = FloorDiv(maxY - 1, BucketSize);
            for (var by = by0; by <= by1; by++)
            for (var bx = bx0; bx <= bx1; bx++)
                seen.Add((bx, by));
        }
        AddRange(cell.X, cell.Y, cell.X + cell.Width, cell.Y + 1);                              // south row
        AddRange(cell.X, cell.Y + cell.Height - 1, cell.X + cell.Width, cell.Y + cell.Height);   // north row
        AddRange(cell.X, cell.Y, cell.X + 1, cell.Y + cell.Height);                              // west column
        AddRange(cell.X + cell.Width - 1, cell.Y, cell.X + cell.Width, cell.Y + cell.Height);    // east column
        return seen;
    }

    /// <summary>True edge-sharing neighbors of <paramref name="a"/>, found via the 1-unit-thick
    /// strips just OUTSIDE each of its 4 edges (bucket lookup is a superset — verified with
    /// <see cref="SharesEdge"/> before returning).</summary>
    private static HashSet<int> FindNeighborIds(LeafCell a, int selfId,
        Dictionary<int, LeafCell> byId, Dictionary<(int, int), HashSet<int>> spatial)
    {
        var candidates = new HashSet<int>();
        void ScanStrip(int minX, int minY, int maxX, int maxY)
        {
            var bx0 = FloorDiv(minX, BucketSize);
            var bx1 = FloorDiv(maxX - 1, BucketSize);
            var by0 = FloorDiv(minY, BucketSize);
            var by1 = FloorDiv(maxY - 1, BucketSize);
            for (var by = by0; by <= by1; by++)
            for (var bx = bx0; bx <= bx1; bx++)
                if (spatial.TryGetValue((bx, by), out var ids))
                    foreach (var id in ids)
                        if (id != selfId) candidates.Add(id);
        }
        ScanStrip(a.X - 1, a.Y, a.X, a.Y + a.Height);                            // west
        ScanStrip(a.X + a.Width, a.Y, a.X + a.Width + 1, a.Y + a.Height);        // east
        ScanStrip(a.X, a.Y - 1, a.X + a.Width, a.Y);                            // south
        ScanStrip(a.X, a.Y + a.Height, a.X + a.Width, a.Y + a.Height + 1);       // north

        candidates.RemoveWhere(id => !SharesEdge(a, byId[id]));
        return candidates;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private static IReadOnlyCollection<int>? CollectBorderSets(
        IReadOnlyList<int>? onSouthBorder, IReadOnlyList<int>? onNorthBorder)
    {
        if (onSouthBorder == null) return onNorthBorder;
        if (onNorthBorder == null) return onSouthBorder;
        var set = new HashSet<int>(onSouthBorder);
        set.IntersectWith(onNorthBorder);
        return set;
    }

    /// <summary>
    ///     Default candidate on the global dyadic lattice (see
    ///     <see cref="BackdropEdgeSubdivider.DyadicMid"/> — un-dyadic floor-midpoint children made
    ///     the balance pass cascade unit cells across whole un-dyadic-width chunks), snapped to the
    ///     nearest strictly-interior member of <paramref name="mustMatch"/> when non-empty. Null
    ///     when the axis can't split at all.
    /// </summary>
    private static int? ChooseSplit(int from, int to, IReadOnlyCollection<int>? mustMatch)
    {
        if (to - from < 2) return null;
        var candidate = BackdropEdgeSubdivider.DyadicMid(from, to);
        if (mustMatch == null || mustMatch.Count == 0) return candidate;

        int? best = null;
        var bestDist = int.MaxValue;
        foreach (var m in mustMatch.OrderBy(v => v))
        {
            if (m <= from || m >= to) continue;
            var dist = Math.Abs(m - candidate);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = m;
            }
        }
        return best;
    }

    /// <summary>4 children when both axes split, 2 when only one does. Deterministic order SW, SE, NW, NE.</summary>
    private static IEnumerable<LeafCell> Split(LeafCell cell, int? splitX, int? splitY)
    {
        if (splitX is { } sx && splitY is { } sy)
        {
            yield return new LeafCell(cell.X, cell.Y, sx - cell.X, sy - cell.Y);
            yield return new LeafCell(sx, cell.Y, cell.X + cell.Width - sx, sy - cell.Y);
            yield return new LeafCell(cell.X, sy, sx - cell.X, cell.Y + cell.Height - sy);
            yield return new LeafCell(sx, sy, cell.X + cell.Width - sx, cell.Y + cell.Height - sy);
        }
        else if (splitX is { } sxOnly)
        {
            yield return new LeafCell(cell.X, cell.Y, sxOnly - cell.X, cell.Height);
            yield return new LeafCell(sxOnly, cell.Y, cell.X + cell.Width - sxOnly, cell.Height);
        }
        else if (splitY is { } syOnly)
        {
            yield return new LeafCell(cell.X, cell.Y, cell.Width, syOnly - cell.Y);
            yield return new LeafCell(cell.X, syOnly, cell.Width, cell.Y + cell.Height - syOnly);
        }
    }

    private double ProbeVerticalError(double minX, double minY, double maxX, double maxY)
    {
        var n = _options.ErrorProbeGridSize;
        double z00 = _field.SampleWorldZ(minX, minY), z10 = _field.SampleWorldZ(maxX, minY);
        double z01 = _field.SampleWorldZ(minX, maxY), z11 = _field.SampleWorldZ(maxX, maxY);

        var worst = 0.0;
        for (var j = 0; j <= n; j++)
        for (var i = 0; i <= n; i++)
        {
            double fx = (double)i / n, fy = (double)j / n;
            var plane = (z00 * (1 - fx) + z10 * fx) * (1 - fy) + (z01 * (1 - fx) + z11 * fx) * fy;
            var actual = _field.SampleWorldZ(minX + fx * (maxX - minX), minY + fy * (maxY - minY));
            worst = Math.Max(worst, Math.Abs(actual - plane));
        }
        return worst;
    }

    private double ToleranceAt(double minX, double minY, double maxX, double maxY)
    {
        var d = Math.Max(0, Math.Min(Math.Min(_field.SignedDistanceToTerrainRect(minX, minY),
            _field.SignedDistanceToTerrainRect(maxX, minY)), Math.Min(
            _field.SignedDistanceToTerrainRect(minX, maxY), _field.SignedDistanceToTerrainRect(maxX, maxY))));
        var t = Math.Clamp(d / _options.MaxMarginMeters, 0, 1);
        return _options.MaxVerticalErrorNearMeters +
               (_options.MaxVerticalErrorFarMeters - _options.MaxVerticalErrorNearMeters) * t;
    }

    private static int Level(LeafCell c) => (int)Math.Ceiling(Math.Log2(Math.Max(c.Width, c.Height)));

    private static bool SharesEdge(LeafCell a, LeafCell b) =>
        (a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height
        || (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width;

}
