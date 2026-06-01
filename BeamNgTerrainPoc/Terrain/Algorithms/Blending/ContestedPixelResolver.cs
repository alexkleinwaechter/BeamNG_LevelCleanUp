namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
///     Slim spline metadata consumed by <see cref="ContestedPixelResolver" /> when
///     resolving a contested mask claim in Pass 1 of
///     <see cref="RoadMaskBuilder.RasterizeSplinePolygons" />. Carries the three signals
///     used by the comparison cascade: <see cref="Priority" /> (OSM type × 100 +
///     material order), <see cref="TotalLengthMeters" /> (longer ≈ more-likely-continuous),
///     and <see cref="SplineId" /> (deterministic final tiebreaker).
/// </summary>
public readonly record struct SplineOverlapMetadata(
    int SplineId,
    int Priority,
    float TotalLengthMeters);

/// <summary>
///     Phase A.8.2 — pure per-pixel resolver for contested mask claims in
///     <see cref="RoadMaskBuilder.RasterizeSplinePolygons" />. Decides whether the
///     candidate spline takes ownership of a pixel that is already claimed.
///     Geometric-only — does not consult terrain elevation or grade.
/// </summary>
public static class ContestedPixelResolver
{
    public readonly record struct ResolveOutcome(bool TakeOwnership, float NewElevation);

    /// <summary>
    ///     Resolves a contested pixel. Encodes the per-case decision; for the
    ///     priority-override branch, delegates to <see cref="CompareForOverlap" />.
    ///     Cases:
    ///       1. existing.SplineId == -1 (unclaimed; defensive): candidate always wins.
    ///       2. existing.SplineId == candidate.SplineId (same spline re-stamp): keep
    ///          lower elevation for banking refinement.
    ///       3. Different splines, flag off OR Pass 2: keep existing (legacy first-writer-wins).
    ///       4. Different splines, flag on AND Pass 1: candidate wins iff
    ///          <see cref="CompareForOverlap" />(candidate, existing) &gt; 0.
    /// </summary>
    public static ResolveOutcome Resolve(
        SplineOverlapMetadata existing, float existingElev,
        SplineOverlapMetadata candidate, float candidateElev,
        bool useSurfaceWidthOnly,
        bool enableSurfacePriorityOverride)
    {
        // Case 1: unclaimed → candidate wins
        if (existing.SplineId == -1)
            return new ResolveOutcome(TakeOwnership: true, NewElevation: candidateElev);

        // Case 2: same spline → keep lower (banking refinement)
        if (existing.SplineId == candidate.SplineId)
        {
            var lower = candidateElev < existingElev ? candidateElev : existingElev;
            return new ResolveOutcome(TakeOwnership: false, NewElevation: lower);
        }

        // Case 3: different splines, flag off OR Pass 2 → keep existing
        if (!enableSurfacePriorityOverride || !useSurfaceWidthOnly)
            return new ResolveOutcome(TakeOwnership: false, NewElevation: existingElev);

        // Case 4: different splines, flag on, Pass 1 → cascade comparison
        if (CompareForOverlap(candidate, existing) > 0)
            return new ResolveOutcome(TakeOwnership: true, NewElevation: candidateElev);

        return new ResolveOutcome(TakeOwnership: false, NewElevation: existingElev);
    }

    /// <summary>
    ///     Multi-key cascade comparator. Returns +1 if <paramref name="a" /> strictly wins,
    ///     -1 if <paramref name="b" /> strictly wins, 0 if all tiers are tied.
    ///     Tiers (strict ordering, first decisive tier wins):
    ///       1. Priority (higher wins) — encodes OSM type + material order
    ///       2. TotalLengthMeters (longer wins) — terminating side roads are typically shorter
    ///       3. SplineId (lower wins) — deterministic fallback, no semantic meaning
    /// </summary>
    internal static int CompareForOverlap(SplineOverlapMetadata a, SplineOverlapMetadata b)
    {
        if (a.Priority != b.Priority)
            return a.Priority > b.Priority ? 1 : -1;

        if (a.TotalLengthMeters != b.TotalLengthMeters)
            return a.TotalLengthMeters > b.TotalLengthMeters ? 1 : -1;

        if (a.SplineId != b.SplineId)
            return a.SplineId < b.SplineId ? 1 : -1;

        return 0;
    }
}
