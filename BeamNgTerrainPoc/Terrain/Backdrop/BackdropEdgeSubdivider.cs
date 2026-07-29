namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Computes a chunk border's subdivision ONCE so both neighboring chunks split their shared
///     edge identically (spec §8 "computed once per edge" — the source of bitwise-identical borders).
/// </summary>
public static class BackdropEdgeSubdivider
{
    /// <summary>
    ///     Deterministic 1D subdivision of a chunk-border segment given in lattice coords.
    ///     Returns sorted lattice positions INCLUDING both endpoints. Bisection at floor((a+b)/2)
    ///     while the predicate demands refinement — identical for both adjacent chunks by construction.
    /// </summary>
    public static IReadOnlyList<int> Subdivide(
        int fixedCoord, bool verticalEdge, int from, int to,
        BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importance)
    {
        var result = new SortedSet<int> { from, to };
        SubdivideRange(fixedCoord, verticalEdge, from, to, field, options, importance, result);
        return result.ToList();
    }

    private static void SubdivideRange(int fixedCoord, bool verticalEdge, int a, int b,
        BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importance, SortedSet<int> result)
    {
        if (b - a < 2) return;
        if (!NeedsSplit(fixedCoord, verticalEdge, a, b, field, options, importance)) return;

        var mid = DyadicMid(a, b);
        result.Add(mid);
        SubdivideRange(fixedCoord, verticalEdge, a, mid, field, options, importance, result);
        SubdivideRange(fixedCoord, verticalEdge, mid, b, field, options, importance, result);
    }

    /// <summary>
    ///     Split coordinate on the GLOBAL dyadic lattice: the multiple of the largest possible
    ///     power of two strictly inside (a, b), nearest to the midpoint. The naive floor-midpoint
    ///     produced un-dyadic children on un-dyadic chunk widths (the planner emits e.g. 1365-wide
    ///     chunks = 12288/9), whose ceil(log2) levels cannot form a clean 2:1 ladder against the
    ///     edge band's forced unit cells — the balance pass then cascades 1–2 m cells across the
    ///     whole chunk (kattenesbackdrop 2026-07-29: 855k leaves in a 1365x1024 chunk, 2.72 GB of
    ///     DAEs). Dyadic-aligned splits restore the geometric grading ladder; for power-of-two
    ///     ranges this picks the exact midpoint, so previously-healthy chunks are unchanged.
    ///     Shared by the mesher's interior splits and this border subdivider so both neighbor
    ///     chunks still derive identical border sets.
    /// </summary>
    internal static int DyadicMid(int a, int b)
    {
        for (var bit = 30; bit >= 0; bit--)
        {
            var step = 1L << bit;
            // Smallest/largest multiples of step STRICTLY inside (a, b); floor-division via
            // Math.Floor handles negative lattice coordinates (chunks south/west of the terrain).
            var first = (long)Math.Floor(a / (double)step) * step + step;
            var last = (long)Math.Ceiling(b / (double)step) * step - step;
            if (first > last) continue;

            var k = (long)Math.Round((a + b) / 2.0 / step);
            return (int)Math.Clamp(k * step, first, last);
        }
        return FloorMid(a, b);   // unreachable for b - a >= 2, kept as a safe fallback
    }

    private static bool NeedsSplit(int fixedCoord, bool verticalEdge, int a, int b,
        BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importance)
    {
        var u = options.LatticeUnitMeters;
        var half = options.HalfSizeMeters;
        var fixedWorld = fixedCoord * u - half;
        double aWorld = a * u - half, bWorld = b * u - half;

        // Zero-thickness border segment inflated by u/2 on the fixed axis (spec §8 note).
        double minX, minY, maxX, maxY;
        if (verticalEdge)
        {
            minX = fixedWorld - u / 2; maxX = fixedWorld + u / 2;
            minY = aWorld; maxY = bWorld;
        }
        else
        {
            minY = fixedWorld - u / 2; maxY = fixedWorld + u / 2;
            minX = aWorld; maxX = bWorld;
        }

        var segmentSize = bWorld - aWorld;
        foreach (var source in importance)
            if (source.RequiredMaxCellSizeMeters(minX, minY, maxX, maxY) is { } limit &&
                segmentSize > limit + 1e-9)
                return true;

        var tol = ToleranceAt(field, options, minX, minY, maxX, maxY);
        var err = ProbeChordError(field, options, fixedWorld, verticalEdge, aWorld, bWorld);
        if (err > tol + 1e-9) return true;

        // Approved plan adjustment (the brief's 1-D-only chord check samples ONLY along the border
        // line, so it can miss 2-D variation transverse to it, leaving border-locked leaves over
        // tolerance — see task-6-report.md). Additionally probe the two dyadic squares straddling
        // the border, side length = segment length, with the SAME 2-D grid probe/tolerance used for
        // interior cells. Symmetric about the border line → both neighbor chunks derive the same
        // two squares and thus the same decision (determinism preserved). OR'd with the checks
        // above, so this can only make border sets finer, never coarser, than the brief's rule.
        var length = segmentSize;
        double loMinX, loMinY, loMaxX, loMaxY, hiMinX, hiMinY, hiMaxX, hiMaxY;
        if (verticalEdge)
        {
            loMinX = fixedWorld - length; loMaxX = fixedWorld; loMinY = aWorld; loMaxY = bWorld;
            hiMinX = fixedWorld; hiMaxX = fixedWorld + length; hiMinY = aWorld; hiMaxY = bWorld;
        }
        else
        {
            loMinY = fixedWorld - length; loMaxY = fixedWorld; loMinX = aWorld; loMaxX = bWorld;
            hiMinY = fixedWorld; hiMaxY = fixedWorld + length; hiMinX = aWorld; hiMaxX = bWorld;
        }

        if (ProbeGridError(field, options, loMinX, loMinY, loMaxX, loMaxY) >
            ToleranceAt(field, options, loMinX, loMinY, loMaxX, loMaxY) + 1e-9)
            return true;
        if (ProbeGridError(field, options, hiMinX, hiMinY, hiMaxX, hiMaxY) >
            ToleranceAt(field, options, hiMinX, hiMinY, hiMaxX, hiMaxY) + 1e-9)
            return true;

        return false;
    }

    /// <summary>2-D (n+1)×(n+1) grid probe vs. the corner-bilinear plane — same math as the mesher's
    /// interior cell check, reused here so the dyadic straddling squares are judged identically.</summary>
    private static double ProbeGridError(BackdropHeightField field, BackdropMesherOptions options,
        double minX, double minY, double maxX, double maxY)
    {
        var n = options.ErrorProbeGridSize;
        double z00 = field.SampleWorldZ(minX, minY), z10 = field.SampleWorldZ(maxX, minY);
        double z01 = field.SampleWorldZ(minX, maxY), z11 = field.SampleWorldZ(maxX, maxY);

        var worst = 0.0;
        for (var j = 0; j <= n; j++)
        for (var i = 0; i <= n; i++)
        {
            double fx = (double)i / n, fy = (double)j / n;
            var plane = (z00 * (1 - fx) + z10 * fx) * (1 - fy) + (z01 * (1 - fx) + z11 * fx) * fy;
            var actual = field.SampleWorldZ(minX + fx * (maxX - minX), minY + fy * (maxY - minY));
            worst = Math.Max(worst, Math.Abs(actual - plane));
        }
        return worst;
    }

    /// <summary>Max |actual − lerp(endpoints)| along the 1D chord, probed at ErrorProbeGridSize points.</summary>
    private static double ProbeChordError(BackdropHeightField field, BackdropMesherOptions options,
        double fixedWorld, bool verticalEdge, double aWorld, double bWorld)
    {
        (double X, double Y) Pos(double t)
        {
            var v = aWorld + t * (bWorld - aWorld);
            return verticalEdge ? (fixedWorld, v) : (v, fixedWorld);
        }

        var n = options.ErrorProbeGridSize;
        var (x0, y0) = Pos(0);
        var (x1, y1) = Pos(1);
        double z0 = field.SampleWorldZ(x0, y0), z1 = field.SampleWorldZ(x1, y1);

        var worst = 0.0;
        for (var i = 0; i <= n; i++)
        {
            var t = (double)i / n;
            var lerp = z0 * (1 - t) + z1 * t;
            var (x, y) = Pos(t);
            var actual = field.SampleWorldZ(x, y);
            worst = Math.Max(worst, Math.Abs(actual - lerp));
        }
        return worst;
    }

    private static double ToleranceAt(BackdropHeightField field, BackdropMesherOptions o,
        double minX, double minY, double maxX, double maxY)
    {
        var d = Math.Max(0, Math.Min(Math.Min(field.SignedDistanceToTerrainRect(minX, minY),
            field.SignedDistanceToTerrainRect(maxX, minY)), Math.Min(
            field.SignedDistanceToTerrainRect(minX, maxY), field.SignedDistanceToTerrainRect(maxX, maxY))));
        var t = Math.Clamp(d / o.MaxMarginMeters, 0, 1);
        return o.MaxVerticalErrorNearMeters + (o.MaxVerticalErrorFarMeters - o.MaxVerticalErrorNearMeters) * t;
    }

    private static int FloorMid(int a, int b) => (int)Math.Floor((a + b) / 2.0);
}
