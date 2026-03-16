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

        // With overlap: chunk1=0..99, chunk2=99..198, chunk3=198..249
        Assert.Equal(3, chunks.Count);
        Assert.Equal(100, chunks[0].Count);
        Assert.Equal(100, chunks[1].Count);
        Assert.Equal(52, chunks[2].Count);

        // Verify boundary nodes are shared between adjacent chunks
        Assert.Equal(chunks[0].Last()[0], chunks[1].First()[0]);
        Assert.Equal(chunks[1].Last()[0], chunks[2].First()[0]);
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
}
