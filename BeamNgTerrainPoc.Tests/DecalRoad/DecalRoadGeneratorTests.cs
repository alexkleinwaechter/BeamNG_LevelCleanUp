using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadGeneratorTests
{
    [Theory]
    [InlineData(2, new[] { 0.0f })]          // 2 lanes → 1 boundary at center
    [InlineData(3, new[] { -0.333f, 0.333f })] // 3 lanes → 2 boundaries
    [InlineData(4, new[] { -0.5f, 0.0f, 0.5f })] // 4 lanes → 3 boundaries
    [InlineData(1, new float[0])]             // 1 lane → no boundaries
    public void CalculateLaneBoundaryPositions_ReturnsCorrectPositions(
        int laneCount, float[] expectedApprox)
    {
        var positions = DecalRoadGenerator.CalculateLaneBoundaryPositions(laneCount);

        Assert.Equal(expectedApprox.Length, positions.Length);
        for (int i = 0; i < expectedApprox.Length; i++)
            Assert.Equal(expectedApprox[i], positions[i], precision: 2);
    }

    [Fact]
    public void ChunkNodes_SplitsWithBoundaryOverlap()
    {
        var nodes = Enumerable.Range(0, 250)
            .Select(i => new float[] { i, 0, 0, 1.0f })
            .ToList();

        var chunks = DecalRoadGenerator.ChunkNodes(nodes, maxNodesPerChunk: 100);

        // Single shared boundary node (NOT the bridge-cut one-span overlap — that double-draws
        // translucent layers at chunk seams): chunk1=0..99, chunk2=99..198, chunk3=198..249
        Assert.Equal(3, chunks.Count);
        Assert.Equal(100, chunks[0].Count);
        Assert.Equal(100, chunks[1].Count);
        Assert.Equal(52, chunks[2].Count);

        // Verify boundary nodes are shared between adjacent chunks
        Assert.Equal(chunks[0].Last()[0], chunks[1].First()[0]);
        Assert.Equal(chunks[1].Last()[0], chunks[2].First()[0]);

        // Full coverage: last chunk ends at the final node
        Assert.Equal(249f, chunks[^1][^1][0]);
    }

    [Fact]
    public void ChunkNodes_NoDegenerateTailChunk()
    {
        // 159 nodes at max 80 used to emit a duplicate 1-node tail chunk with the old
        // blind-stride loop. Every chunk must be a usable road (>= 2 nodes) and the
        // last chunk must end exactly at the final node.
        for (var total = 81; total <= 400; total++)
        {
            var nodes = Enumerable.Range(0, total)
                .Select(i => new float[] { i, 0, 0, 1.0f })
                .ToList();

            var chunks = DecalRoadGenerator.ChunkNodes(nodes);

            Assert.All(chunks, c => Assert.True(c.Count >= 2, $"total={total}: chunk with {c.Count} node(s)"));
            Assert.All(chunks, c => Assert.True(c.Count <= 80, $"total={total}: chunk with {c.Count} nodes"));
            Assert.Equal(0f, chunks[0][0][0]);
            Assert.Equal(total - 1, chunks[^1][^1][0]);
        }
    }

    [Fact]
    public void ChunkNodes_UnderLimit_ReturnsSingleChunk()
    {
        var nodes = Enumerable.Range(0, 50)
            .Select(i => new float[] { i, 0, 0, 1.0f })
            .ToList();

        var chunks = DecalRoadGenerator.ChunkNodes(nodes, maxNodesPerChunk: 100);

        Assert.Single(chunks);
        Assert.Equal(50, chunks[0].Count);
    }

    [Fact]
    public void RemoveUnsafeShortSegmentsDropsInteriorSubTwoMillimeterNode()
    {
        var nodes = new List<float[]>
        {
            new float[] { 0f, 0f, 0f, 4f },
            new float[] { 0.00112f, 0f, 0f, 5f },
            new float[] { 10f, 0f, 0f, 6f }
        };

        var removed = DecalRoadGenerator.RemoveUnsafeShortSegments(nodes);

        Assert.Equal(1, removed);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(0f, nodes[0][0]);
        Assert.Equal(10f, nodes[1][0]);
    }

    [Fact]
    public void RemoveUnsafeShortSegmentsPreservesFinalEndpoint()
    {
        var nodes = new List<float[]>
        {
            new float[] { 0f, 0f, 0f, 4f },
            new float[] { 10f, 0f, 0f, 5f },
            new float[] { 10.00104f, 0f, 0f, 6f }
        };

        var removed = DecalRoadGenerator.RemoveUnsafeShortSegments(nodes);

        Assert.Equal(1, removed);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(0f, nodes[0][0]);
        Assert.Equal(10.00104f, nodes[1][0]);
    }

    [Fact]
    public void RemoveUnsafeShortSegmentsKeepsExclusiveTwoMillimeterBoundary()
    {
        var nodes = new List<float[]>
        {
            new float[] { 0f, 0f, 0f, 4f },
            new float[] { 0.002f, 0f, 0f, 5f },
            new float[] { 1f, 0f, 0f, 6f }
        };

        Assert.Equal(0, DecalRoadGenerator.RemoveUnsafeShortSegments(nodes));
        Assert.Equal(3, nodes.Count);
    }

    [Fact]
    public void RemoveUnsafeShortSegmentsLeavesCollapsedRoadUnemittable()
    {
        var nodes = new List<float[]>
        {
            new float[] { 0f, 0f, 0f, 4f },
            new float[] { 0.001f, 0f, 0f, 5f },
            new float[] { 0.0015f, 0f, 0f, 6f }
        };

        Assert.Equal(2, DecalRoadGenerator.RemoveUnsafeShortSegments(nodes));
        Assert.Single(nodes);
    }
}
