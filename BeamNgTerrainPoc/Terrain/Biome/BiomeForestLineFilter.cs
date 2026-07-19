using System.Text.Json;

namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Result of filtering one forest4.json file's lines against manifest records.
/// </summary>
public sealed record BiomeLineFilterResult(
    List<string> KeptLines,
    int RemovedCount,
    bool Changed);

/// <summary>Decides whether one parsed forest item line should be removed (foreign-item cleanup).</summary>
public delegate bool BiomeItemPredicate(string type, double x, double y, double z, double scale);

/// <summary>
/// Removes manifest-tracked items from forest4.json NDJSON content while preserving
/// every other line byte-for-byte. This is the fallback delete path for when the
/// in-game editor merged or re-saved forest files (the fast path just deletes the
/// hash-verified owned file). Matching is ε-tolerant on position and scale because
/// the game re-serializes floats with its own formatting.
/// </summary>
public static class BiomeForestLineFilter
{
    public const double DefaultEpsilon = 1e-3;

    // Bucket quantum for the XY lookup; must be > 2*epsilon so a probe of the
    // floor((v±eps)/quantum) cells per axis covers all candidates.
    private const double Quantum = 0.01;

    /// <summary>
    /// Filters NDJSON lines, dropping lines that match a record in <paramref name="itemsToRemove"/>.
    /// Each record removes at most one line (duplicates in the file need duplicate records).
    /// Unparseable or non-item lines are always kept verbatim.
    /// </summary>
    public static BiomeLineFilterResult FilterLines(
        IReadOnlyList<string> lines,
        IReadOnlyCollection<BiomeManifestItem> itemsToRemove,
        double epsilon = DefaultEpsilon)
    {
        var kept = new List<string>(lines.Count);
        var removed = FilterLinesStreaming(lines, itemsToRemove, kept.Add, epsilon);
        return new BiomeLineFilterResult(kept, removed, removed > 0);
    }

    /// <summary>
    /// Streaming core: kept lines go to <paramref name="keptLineSink"/> as they are read —
    /// suitable for filtering large forest files line-by-line into a temp file without
    /// buffering the whole content. Returns the number of removed lines.
    /// </summary>
    public static int FilterLinesStreaming(
        IEnumerable<string> lines,
        IReadOnlyCollection<BiomeManifestItem> itemsToRemove,
        Action<string> keptLineSink,
        double epsilon = DefaultEpsilon)
    {
        var removed = 0;

        if (itemsToRemove.Count == 0)
        {
            foreach (var line in lines)
            {
                keptLineSink(line);
            }
            return 0;
        }

        var index = BuildIndex(itemsToRemove);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                keptLineSink(line);
                continue;
            }

            if (TryParseItemLine(line, out var type, out var x, out var y, out var z, out var scale)
                && TryMatchAndConsume(index, type, x, y, z, scale, epsilon))
            {
                removed++;
                continue;
            }

            keptLineSink(line);
        }

        return removed;
    }

    /// <summary>
    /// Predicate variant of the streaming filter (negative-list foreign-item cleanup):
    /// drops every parseable item line for which <paramref name="shouldRemove"/> returns
    /// true; non-item and malformed lines are always kept verbatim. Returns the number
    /// of removed lines.
    /// </summary>
    public static int FilterLinesWhereStreaming(
        IEnumerable<string> lines,
        BiomeItemPredicate shouldRemove,
        Action<string> keptLineSink)
    {
        var removed = 0;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line)
                && TryParseItemLine(line, out var type, out var x, out var y, out var z, out var scale)
                && shouldRemove(type, x, y, z, scale))
            {
                removed++;
                continue;
            }

            keptLineSink(line);
        }
        return removed;
    }

    /// <summary>Dry-run counterpart of <see cref="FilterLinesWhereStreaming"/> — counts matches without writing.</summary>
    public static int CountLinesWhere(IEnumerable<string> lines, BiomeItemPredicate predicate)
    {
        return FilterLinesWhereStreaming(lines, predicate, static _ => { });
    }

    private static Dictionary<(string, long, long), List<BiomeManifestItem>> BuildIndex(
        IReadOnlyCollection<BiomeManifestItem> items)
    {
        var index = new Dictionary<(string, long, long), List<BiomeManifestItem>>();
        foreach (var item in items)
        {
            if (item.Pos.Length < 3)
            {
                continue;
            }
            var key = (item.Type, Bucket(item.Pos[0]), Bucket(item.Pos[1]));
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<BiomeManifestItem>();
                index[key] = list;
            }
            list.Add(item);
        }
        return index;
    }

    private static long Bucket(double value) => (long)Math.Floor(value / Quantum);

    private static bool TryMatchAndConsume(
        Dictionary<(string, long, long), List<BiomeManifestItem>> index,
        string type, double x, double y, double z, double scale, double epsilon)
    {
        // Probe the buckets that could hold a record within ±epsilon of (x, y).
        Span<long> xb = stackalloc long[2] { Bucket(x - epsilon), Bucket(x + epsilon) };
        Span<long> yb = stackalloc long[2] { Bucket(y - epsilon), Bucket(y + epsilon) };

        for (var xi = 0; xi < 2; xi++)
        {
            if (xi == 1 && xb[1] == xb[0]) break;
            for (var yi = 0; yi < 2; yi++)
            {
                if (yi == 1 && yb[1] == yb[0]) break;
                if (!index.TryGetValue((type, xb[xi], yb[yi]), out var candidates))
                {
                    continue;
                }
                for (var i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (Math.Abs(c.Pos[0] - x) <= epsilon
                        && Math.Abs(c.Pos[1] - y) <= epsilon
                        && Math.Abs(c.Pos[2] - z) <= epsilon
                        && Math.Abs(c.Scale - scale) <= epsilon)
                    {
                        candidates.RemoveAt(i);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryParseItemLine(
        string line, out string type, out double x, out double y, out double z, out double scale)
    {
        type = string.Empty;
        x = y = z = 0;
        scale = 1.0;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || !root.TryGetProperty("pos", out var posProp)
                || posProp.ValueKind != JsonValueKind.Array
                || posProp.GetArrayLength() < 3)
            {
                return false;
            }

            type = typeProp.GetString() ?? string.Empty;
            x = posProp[0].GetDouble();
            y = posProp[1].GetDouble();
            z = posProp[2].GetDouble();
            if (root.TryGetProperty("scale", out var scaleProp)
                && scaleProp.ValueKind == JsonValueKind.Number)
            {
                scale = scaleProp.GetDouble();
            }
            return type.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
