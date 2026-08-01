using System.Numerics;
using BeamNgTerrainPoc.Terrain.Backdrop;
using BeamNG.Procedural3D.Core;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropTriangulationTests
{
    // Reuse the Setup/Chunk helpers of BackdropQuadtreeMesherTests (copy them in; keep tests self-contained).
    private const int Size = 32;
    private const float U = 1.0f;
    private const double Half = 16.0;

    private static (BackdropHeightField Field, BackdropMesherOptions Options, List<IBackdropImportanceSource> Importance)
        Setup(Func<double, double, double> demElevation, double band = 4.0)
    {
        var terrain = new float[Size, Size];
        var window = new PixelRect(0, 0, 160, 160);
        var far = new float[160 * 160];
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 160; x++)
        {
            var (wx, wy) = mapper.SourcePixelToWorld(x + 0.5, y + 0.5);
            far[y * 160 + x] = (float)demElevation(wx, wy);
        }
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, window), [],
            terrain, mapper, Size, U, 0f, 0.0, band);
        var options = new BackdropMesherOptions
            { EdgeBandMeters = band, MaxMarginMeters = 64.0, LatticeUnitMeters = U, HalfSizeMeters = Half };
        return (field, options, [new EdgeBandImportanceSource(Half, band, U)]);
    }

    private static BackdropChunkDefinition Chunk(int lx, int ly, int lw, int lh, int cx = 0, int cy = 0) => new()
    {
        Cx = cx, Cy = cy, LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
        WorldMinX = lx * U - Half, WorldMinY = ly * U - Half,
        WorldMaxX = (lx + lw) * U - Half, WorldMaxY = (ly + lh) * U - Half,
        SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
        DaeFileName = $"backdrop_{cx}_{cy}.dae", TextureFileName = $"backdrop_{cx}_{cy}.color.png",
        MaterialName = $"mt_backdrop_{cx}_{cy}", TextureSize = 256, DistanceToTerrainMeters = 0
    };

    /// <summary>Every interior edge must be used exactly twice with opposite direction (watertight),
    /// AND no axis-aligned edge may skip over a lattice point some other triangle actually uses as a
    /// vertex — that skipped point is a hanging node / T-vertex crack that edge-count alone misses
    /// (a coarse leaf's 0→4 edge next to fine neighbors' 0→2 / 2→4 edges is watertight by the
    /// edge-count metric alone: three distinct edges, each used exactly once).</summary>
    private static void AssertWatertight(Mesh mesh, int surfaceTriangles)
    {
        var edgeUse = new Dictionary<(int A, int B), int>();
        // Half-unit lattice keys (round(value * 2)): genuine lattice-grid vertices always land on an
        // EVEN key on both axes; fan centers land on an ODD key on at least one axis whenever their
        // leaf has odd width/height (the common case). Restricting the interior scan below to
        // even/even candidates means it can never misfire on a fan spoke that happens to be
        // axis-aligned (leaf width/height both even) — in a correct partition no other vertex can
        // ever sit strictly inside a leaf's interior, so the scan is exact either way.
        static (long X, long Y) Key(float x, float y) =>
            ((long)Math.Round(x * 2, MidpointRounding.AwayFromZero), (long)Math.Round(y * 2, MidpointRounding.AwayFromZero));

        var usedPositions = new HashSet<(long X, long Y)>();
        var edgeEndpoints = new List<((long X, long Y) A, (long X, long Y) B)>();
        for (var t = 0; t < surfaceTriangles; t++)
        {
            var tri = mesh.Triangles[t];
            var p0 = mesh.Vertices[tri.V0].Position;
            var p1 = mesh.Vertices[tri.V1].Position;
            var p2 = mesh.Vertices[tri.V2].Position;
            var k0 = Key(p0.X, p0.Y);
            var k1 = Key(p1.X, p1.Y);
            var k2 = Key(p2.X, p2.Y);
            usedPositions.Add(k0); usedPositions.Add(k1); usedPositions.Add(k2);
            edgeEndpoints.Add((k0, k1)); edgeEndpoints.Add((k1, k2)); edgeEndpoints.Add((k2, k0));

            foreach (var (a, b) in new[] { (tri.V0, tri.V1), (tri.V1, tri.V2), (tri.V2, tri.V0) })
            {
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }
        Assert.DoesNotContain(edgeUse, kv => kv.Value > 2);   // >2 = non-manifold; 1 = boundary edge (allowed)

        foreach (var (a, b) in edgeEndpoints)
        {
            if (a.X == b.X && a.Y != b.Y)
            {
                var lo = Math.Min(a.Y, b.Y);
                var hi = Math.Max(a.Y, b.Y);
                for (var y = lo + 1; y < hi; y++)
                {
                    if (y % 2 != 0) continue;                  // odd = fan-center half-unit, not a lattice point
                    Assert.False(usedPositions.Contains((a.X, y)),
                        $"hanging node at half-unit ({a.X},{y}) on edge ({a.X},{a.Y})-({b.X},{b.Y})");
                }
            }
            else if (a.Y == b.Y && a.X != b.X)
            {
                var lo = Math.Min(a.X, b.X);
                var hi = Math.Max(a.X, b.X);
                for (var x = lo + 1; x < hi; x++)
                {
                    if (x % 2 != 0) continue;
                    Assert.False(usedPositions.Contains((x, a.Y)),
                        $"hanging node at half-unit ({x},{a.Y}) on edge ({a.X},{a.Y})-({b.X},{b.Y})");
                }
            }
        }
    }

    [Fact]
    public void MeshChunk_IsWatertight_OnBumpyTerrain()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 32, 32));
        Assert.True(result.SurfaceTriangleCount > 0);
        AssertWatertight(result.VisualMesh, result.SurfaceTriangleCount);
    }

    [Fact]
    public void MeshChunk_NoTriangleInsideTerrainRect()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 32, 32));   // chunk east of terrain
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var c = (result.VisualMesh.Vertices[tri.V0].Position +
                     result.VisualMesh.Vertices[tri.V1].Position +
                     result.VisualMesh.Vertices[tri.V2].Position) / 3f;
            Assert.False(Math.Abs(c.X) < Half - 1e-3 && Math.Abs(c.Y) < Half - 1e-3,
                $"triangle centroid {c} lies inside the terrain rect");
        }
    }

    [Fact]
    public void MeshChunk_CoversChunkAreaExactly()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 4 * Math.Sin(x / 2.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 16, 16));
        double area = 0;
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var a = result.VisualMesh.Vertices[tri.V0].Position;
            var b = result.VisualMesh.Vertices[tri.V1].Position;
            var c = result.VisualMesh.Vertices[tri.V2].Position;
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0;
        }
        Assert.Equal(16.0 * 16.0, area, 3);   // XY-projected area = lattice area (ring cutout exact, spec §13)
    }

    [Fact]
    public void MeshChunk_TrianglesWoundCounterClockwise_SeenFromAbove()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(40, 8, 8, 8));
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var a = result.VisualMesh.Vertices[tri.V0].Position;
            var b = result.VisualMesh.Vertices[tri.V1].Position;
            var c = result.VisualMesh.Vertices[tri.V2].Position;
            var cross = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            Assert.True(cross > 0, $"triangle {t} wound clockwise");
        }
    }

    [Fact]
    public void MeshChunk_IsDeterministic()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var m1 = new BackdropQuadtreeMesher(field, options, importance).MeshChunk(Chunk(32, 0, 32, 32));
        var m2 = new BackdropQuadtreeMesher(field, options, importance).MeshChunk(Chunk(32, 0, 32, 32));
        Assert.Equal(m1.VisualMesh.Vertices.Count, m2.VisualMesh.Vertices.Count);
        for (var i = 0; i < m1.VisualMesh.Vertices.Count; i++)
            Assert.Equal(m1.VisualMesh.Vertices[i].Position, m2.VisualMesh.Vertices[i].Position); // bitwise
        Assert.Equal(m1.VisualMesh.Triangles.Select(t => (t.V0, t.V1, t.V2)),
                     m2.VisualMesh.Triangles.Select(t => (t.V0, t.V1, t.V2)));
    }

    [Fact]
    public void SeamVertices_MatchTerrainEdgeExactly_AtPixelCorners()
    {
        // Terrain with a distinctive edge profile; DEM deliberately offset.
        var terrain = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            terrain[y, x] = 2f * y;                       // ramp northward
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        var far = Enumerable.Repeat(500f, 160 * 160).ToArray();
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, new PixelRect(0, 0, 160, 160)),
            [], terrain, mapper, Size, U, 10f, 400.0, 4.0);
        var options = new BackdropMesherOptions
            { EdgeBandMeters = 4.0, MaxMarginMeters = 64.0, LatticeUnitMeters = U, HalfSizeMeters = Half };
        var mesher = new BackdropQuadtreeMesher(field, options, [new EdgeBandImportanceSource(Half, 4.0, U)]);

        var result = mesher.MeshChunk(Chunk(32, 0, 16, 32));   // chunk hugging the east seam, full terrain height
        // Every vertex with X == +half must sit exactly at TerrainEdgeWorldZ (spec §7.1) and at integer lattice Y.
        var seamVertices = result.VisualMesh.Vertices.Take(result.SurfaceVertexCount)
            .Where(v => Math.Abs(v.Position.X - Half) < 1e-6).ToList();
        Assert.True(seamVertices.Count >= Size + 1, "expected a seam vertex per terrain pixel corner");
        foreach (var v in seamVertices)
        {
            var expected = field.TerrainEdgeWorldZ(Half, v.Position.Y);
            Assert.Equal(expected, v.Position.Z, 4);
            var lattice = (v.Position.Y + Half) / U;
            Assert.Equal(Math.Round(lattice), lattice, 6);
        }
    }

    [Fact]
    public void AdjacentChunks_ShareBitwiseIdenticalBorderVertices()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var left = mesher.MeshChunk(Chunk(32, 0, 16, 32, cx: 0));
        var right = mesher.MeshChunk(Chunk(48, 0, 16, 32, cx: 1));
        // Border x = lattice 48 → world 32.
        static List<(float Y, float Z)> Border(BackdropChunkMeshResult r, float x) =>
            r.VisualMesh.Vertices.Take(r.SurfaceVertexCount)
                .Where(v => v.Position.X == x)
                .Select(v => (v.Position.Y, v.Position.Z))
                .OrderBy(v => v.Item1).ToList();
        var borderLeft = Border(left, 32f);
        var borderRight = Border(right, 32f);
        Assert.NotEmpty(borderLeft);
        Assert.Equal(borderLeft, borderRight);   // bitwise float equality (spec §13)
    }

    [Fact]
    public void SeamSkirt_AppendedAfterSurface_ExcludedFromSurfaceCounts()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 16, 32));   // touches the east seam → skirt exists
        Assert.True(result.VisualMesh.Triangles.Count > result.SurfaceTriangleCount, "skirt missing");
        // The west border spans the full terrain height and sits entirely inside the edge band, so the
        // EdgeBandImportanceSource forces it to full lattice resolution: exactly Size unit segments →
        // exactly 2*Size skirt triangles (one quad = 2 triangles per segment).
        Assert.Equal(2 * Size, result.VisualMesh.Triangles.Count - result.SurfaceTriangleCount);
        // Skirt quads: bottom vertices exactly SeamSkirtDepthMeters below their seam vertex.
        var skirtVerts = result.VisualMesh.Vertices.Skip(result.SurfaceVertexCount)
            .Where(v => Math.Abs(v.Position.X - Half) < 1e-6).ToList();
        Assert.NotEmpty(skirtVerts);
        foreach (var v in skirtVerts)
            Assert.Equal(field.TerrainEdgeWorldZ(Half, v.Position.Y) - 2.0, v.Position.Z, 4);
    }

    [Fact]
    public void SeamSkirt_FacesTowardTheTerrain_NotBackfaceCulledFromInside()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        // This chunk sits east of the terrain; its skirted border is the terrain's EAST edge (world X =
        // +Half). A camera inside the terrain looking out through the seam crack looks in the +X
        // direction, so the flange's front face (the side a single-sided material renders) must point
        // back toward the terrain interior, i.e. −X. Every skirt triangle's geometric normal (cross
        // product of its edges in stored vertex order) must therefore have a negative X component.
        var result = mesher.MeshChunk(Chunk(32, 0, 16, 32));
        Assert.True(result.VisualMesh.Triangles.Count > result.SurfaceTriangleCount, "skirt missing");
        for (var t = result.SurfaceTriangleCount; t < result.VisualMesh.Triangles.Count; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var a = result.VisualMesh.Vertices[tri.V0].Position;
            var b = result.VisualMesh.Vertices[tri.V1].Position;
            var c = result.VisualMesh.Vertices[tri.V2].Position;
            var normal = Vector3.Cross(b - a, c - a);
            Assert.True(normal.X < 0, $"skirt triangle {t} faces away from the terrain (normal.X={normal.X})");
        }
    }

    [Fact]
    public void NoSkirt_WhenChunkDoesNotTouchTheSeam()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(48, 0, 16, 16));   // 16 m away from the seam
        Assert.Equal(result.SurfaceTriangleCount, result.VisualMesh.Triangles.Count);
    }

    [Fact]
    public void Normals_AreSmoothAndUpwardFacing()
    {
        var (field, options, importance) = Setup((x, _) => 100 + 0.5 * x);   // constant eastward slope
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(48, 0, 16, 16));
        foreach (var v in result.VisualMesh.Vertices.Take(result.SurfaceVertexCount))
        {
            Assert.True(v.Normal.Z > 0.5f, "normal not upward");
            Assert.Equal(1.0f, v.Normal.Length(), 3);
            // Constant slope 0.5 in x → normal ≈ normalize(−0.5, 0, 1) inside the far field.
            if (v.Position.X > Half + 8)
                Assert.Equal(-0.5f / MathF.Sqrt(1.25f), v.Normal.X, 2);
        }
    }

    [Fact]
    public void UVs_ArePlanarOverTheChunkRect()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var chunk = Chunk(48, 8, 8, 8);
        var result = mesher.MeshChunk(chunk);
        foreach (var v in result.VisualMesh.Vertices.Take(result.SurfaceVertexCount))
        {
            var expectedU = (v.Position.X - chunk.WorldMinX) / (chunk.WorldMaxX - chunk.WorldMinX);
            var expectedV = (v.Position.Y - chunk.WorldMinY) / (chunk.WorldMaxY - chunk.WorldMinY);
            Assert.Equal((float)expectedU, v.UV.X, 5);
            Assert.Equal((float)expectedV, v.UV.Y, 5);
        }
    }
}
