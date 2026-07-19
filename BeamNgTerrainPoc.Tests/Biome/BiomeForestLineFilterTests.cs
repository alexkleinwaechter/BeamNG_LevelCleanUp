using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNgTerrainPoc.Tests.Biome;

/// <summary>
/// The line filter is the fallback delete path when forest files were merged/re-saved by
/// the in-game editor: it must remove exactly the manifest-tracked items (ε-tolerant on
/// position/scale because the game reformats floats) and keep every other line verbatim.
/// </summary>
public class BiomeForestLineFilterTests
{
    private static BiomeManifestItem Record(string type, double x, double y, double z, double scale = 1.0)
        => new() { Type = type, Pos = new[] { x, y, z }, Scale = scale };

    [Fact]
    public void RemovesMatchingLine_KeepsOthersVerbatim()
    {
        var lines = new[]
        {
            """{"type":"oak","pos":[10,20,5],"rotationMatrix":[1,0,0,0,1,0,0,0,1],"scale":1}""",
            """{"type":"pine","pos":[30,40,5],"rotationMatrix":[1,0,0,0,1,0,0,0,1],"scale":1}  """,
        };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(1, result.RemovedCount);
        Assert.True(result.Changed);
        var kept = Assert.Single(result.KeptLines);
        Assert.Equal(lines[1], kept); // trailing whitespace preserved byte-for-byte
    }

    [Fact]
    public void EpsilonTolerance_MatchesReformattedFloats()
    {
        // Game re-saved the file with rounded floats: still within ε=1e-3.
        var lines = new[]
        {
            """{"type":"oak","pos":[10.0004,19.9996,5.0002],"scale":0.9996}""",
        };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(1, result.RemovedCount);
        Assert.Empty(result.KeptLines);
    }

    [Fact]
    public void BeyondEpsilon_IsKept()
    {
        var lines = new[]
        {
            """{"type":"oak","pos":[10.01,20,5],"scale":1}""",
        };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(0, result.RemovedCount);
        Assert.False(result.Changed);
        Assert.Single(result.KeptLines);
    }

    [Fact]
    public void EachRecordRemovesAtMostOneLine()
    {
        // Two identical lines (hand-placed duplicate next to a generated item), one record:
        // exactly one line may disappear.
        var line = """{"type":"oak","pos":[10,20,5],"scale":1}""";
        var lines = new[] { line, line };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(1, result.RemovedCount);
        Assert.Single(result.KeptLines);
    }

    [Fact]
    public void TypeMismatch_IsKept()
    {
        var lines = new[] { """{"type":"pine","pos":[10,20,5],"scale":1}""" };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(0, result.RemovedCount);
    }

    [Fact]
    public void ScaleMismatch_IsKept()
    {
        var lines = new[] { """{"type":"oak","pos":[10,20,5],"scale":1.4}""" };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5, scale: 1.0) });

        Assert.Equal(0, result.RemovedCount);
    }

    [Fact]
    public void NonItemAndGarbageLines_AreAlwaysKept()
    {
        var lines = new[]
        {
            "",
            "   ",
            "not json at all",
            """{"someOtherObject":true}""",
            """{"type":"oak","pos":[10,20,5],"scale":1}""",
        };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10, 20, 5) });

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(4, result.KeptLines.Count);
        Assert.Contains("not json at all", result.KeptLines);
    }

    [Fact]
    public void BucketBoundary_StillMatches()
    {
        // Position sits exactly on a 0.01 bucket boundary; the ±ε probe must find it.
        var lines = new[] { """{"type":"oak","pos":[10.0099,20,5],"scale":1}""" };

        var result = BiomeForestLineFilter.FilterLines(lines, new[] { Record("oak", 10.01, 20, 5) });

        Assert.Equal(1, result.RemovedCount);
    }

    [Fact]
    public void StreamingSink_MatchesListVersion()
    {
        var lines = new[]
        {
            """{"type":"oak","pos":[10,20,5],"scale":1}""",
            """{"type":"pine","pos":[30,40,5],"scale":1}""",
            "not json",
            "",
        };
        var records = new[] { Record("oak", 10, 20, 5) };

        var listResult = BiomeForestLineFilter.FilterLines(lines, records);

        var streamed = new List<string>();
        var removed = BiomeForestLineFilter.FilterLinesStreaming(lines, records, streamed.Add);

        Assert.Equal(listResult.RemovedCount, removed);
        Assert.Equal(listResult.KeptLines, streamed);
    }

    [Fact]
    public void NoRecords_ReturnsUnchanged()
    {
        var lines = new[] { """{"type":"oak","pos":[10,20,5],"scale":1}""" };

        var result = BiomeForestLineFilter.FilterLines(lines, Array.Empty<BiomeManifestItem>());

        Assert.False(result.Changed);
        Assert.Single(result.KeptLines);
    }
}
