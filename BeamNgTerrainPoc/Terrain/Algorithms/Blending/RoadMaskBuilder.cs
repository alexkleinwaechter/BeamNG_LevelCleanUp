using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Result of building a combined road mask with elevation data.
/// The mask itself is the protection mechanism — road pixels are pinned.
/// SplineOwnerMap records which spline filled each pixel so the blender
/// can look up the correct per-spline parameters via EDT source tracking.
/// </summary>
public record CombinedMaskResult(
    byte[,] Mask,
    float[,] ElevationMap,
    int[,] SplineOwnerMap,    // SplineId that owns each mask pixel (-1 = no owner)
    int MaskedPixels);

/// <summary>
/// Builds road masks for terrain blending.
///
/// The road mask is used for distance field computation (binary mask of road pixels).
/// </summary>
public class RoadMaskBuilder
{
    /// <summary>
    /// Builds a combined road core mask from all splines in the network.
    /// White pixels (255) indicate road core areas.
    /// </summary>
    /// <param name="network">The unified road network</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <returns>Binary mask where 255 = road core</returns>
    public byte[,] BuildCombinedRoadCoreMask(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel)
    {
        var mask = new byte[height, width];
        var sectionsProcessed = 0;

        foreach (var cs in network.CrossSections.Where(s => !s.IsExcluded))
        {
            // Skip cross-sections with invalid elevations
            if (float.IsNaN(cs.TargetElevation) || float.IsInfinity(cs.TargetElevation))
                continue;

            // Get road half-width for this cross-section
            var halfWidth = cs.EffectiveRoadWidth / 2.0f;

            // Rasterize cross-section line (left to right edge)
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

            var x0 = (int)(left.X / metersPerPixel);
            var y0 = (int)(left.Y / metersPerPixel);
            var x1 = (int)(right.X / metersPerPixel);
            var y1 = (int)(right.Y / metersPerPixel);

            RasterizationUtils.DrawLine(mask, x0, y0, x1, y1, width, height);
            sectionsProcessed++;
        }

        TerrainCreationLogger.Current?.Detail($"Rasterized {sectionsProcessed} cross-sections into combined road mask");
        return mask;
    }

    /// <summary>
    /// Builds a combined road mask with banking-aware elevation at each road pixel.
    /// Uses filled polygons between consecutive cross-section pairs for complete coverage.
    /// </summary>
    /// <param name="network">The unified road network</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <returns>Combined mask with elevation data</returns>
    public CombinedMaskResult BuildCombinedMaskWithElevation(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel)
    {
        var mask = new byte[height, width];
        var elevation = new float[height, width];
        var splineOwner = new int[height, width];

        // Initialize elevation to NaN, owner to -1
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            elevation[y, x] = float.NaN;
            splineOwner[y, x] = -1;
        }

        var maskedPixels = 0;
        var overlapWarnings = 0;

        // Allocate intersection buffer once outside loops to avoid CA2014 (stackalloc in loop)
        Span<float> intersections = stackalloc float[4];

        // Build spline parameter lookup
        var splineParams = network.Splines.ToDictionary(s => s.SplineId, s => s.Parameters);

        // Group cross-sections by spline, ordered by local index
        var crossSectionsBySpline = network.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // Process splines WIDEST-FIRST (then by priority descending).
        // With "first writer wins", the widest/highest-priority road claims its pixels
        // first. When a narrower side road's corridor extends into the wide road's
        // surface, those pixels are already claimed and won't be overwritten.
        var splineLookup = network.Splines.ToDictionary(s => s.SplineId);

        // Phase A.8.2: per-owner overlap metadata for contested-pixel resolution.
        // Priority encodes OSM-type + material-order; TotalLengthMeters is the
        // length-tier tiebreaker (terminating side roads are typically shorter).
        var metadataByOwnerId = network.Splines.ToDictionary(
            s => s.SplineId,
            s => new SplineOverlapMetadata(s.SplineId, s.Priority, s.TotalLengthMeters));

        var processingOrder = crossSectionsBySpline.Keys
            .Where(id => splineLookup.ContainsKey(id))
            .OrderByDescending(id => splineLookup[id].Parameters.RoadWidthMeters)
            .ThenByDescending(id => splineLookup[id].Priority)
            .ToList();

        // Look up JunctionHarmonizationParameters from the first spline (matches the
        // pattern used in UnifiedJunctionProfileBlender). All splines share the same
        // parameters block in current code; A.8 only reads the new flag.
        var jhParams = network.Splines.FirstOrDefault()?.Parameters.JunctionHarmonizationParameters
                       ?? new JunctionHarmonizationParameters();

        if (jhParams.EnableSurfaceWidthProtection)
        {
            // Pass 1: stamp each spline's painted-surface polygon at SurfaceWidth/2 + a small
            // surface-protection margin (closes convex chord-slivers so they don't read as a bite
            // out of an elevated road edge — see SurfaceProtectionMarginMeters).
            var surfaceMargin = MathF.Max(0f, jhParams.SurfaceProtectionMarginMeters);
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId,
                    splineMetadata: metadataByOwnerId[splineId],
                    metadataByOwnerId: metadataByOwnerId,
                    enableSurfacePriorityOverride: jhParams.EnableSurfacePriorityOverride,
                    surfaceMargin,
                    useSurfaceWidthOnly: true,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }

            // Pass 2: extend with corridor + edge protection buffer. Pixels claimed
            // by Pass 1 (mask != 0) are not overwritten — the first-writer-wins rule
            // inside RasterizeSplinePolygons enforces this naturally.
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                var margin = splineParams.TryGetValue(splineId, out var p)
                    ? p.RoadEdgeProtectionBufferMeters
                    : 2.0f;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId,
                    splineMetadata: metadataByOwnerId[splineId],
                    metadataByOwnerId: metadataByOwnerId,
                    enableSurfacePriorityOverride: jhParams.EnableSurfacePriorityOverride,
                    margin,
                    useSurfaceWidthOnly: false,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }
        }
        else
        {
            // Legacy single-pass: rasterize at corridor + edge buffer width, first-writer-wins.
            foreach (var splineId in processingOrder)
            {
                var sections = crossSectionsBySpline[splineId];
                if (sections.Count < 2) continue;

                var margin = splineParams.TryGetValue(splineId, out var p)
                    ? p.RoadEdgeProtectionBufferMeters
                    : 2.0f;

                maskedPixels += RasterizeSplinePolygons(
                    sections, splineId,
                    splineMetadata: metadataByOwnerId[splineId],
                    metadataByOwnerId: metadataByOwnerId,
                    enableSurfacePriorityOverride: jhParams.EnableSurfacePriorityOverride,
                    margin,
                    useSurfaceWidthOnly: false,
                    mask, elevation, splineOwner,
                    width, height, metersPerPixel, intersections);
            }
        }

        if (overlapWarnings > 0)
            TerrainCreationLogger.Current?.Warning(
                $"Combined mask: {overlapWarnings} pixels had elevation overlap > 2.0m");

        // Fill junction gaps: where roads meet at angles, per-spline quad polygons
        // leave triangle-shaped gaps. Pixels in those gaps are NOT in the mask, so
        // the blender treats them as terrain and blends with raw heightmap → hard cliff.
        // Fix: at each junction, fill a circular area using the max road width of all
        // contributing roads, pinned to the junction's harmonized elevation.
        var junctionPixelsFilled = 0;
        var deckJunctionSkips = 0;
        foreach (var junction in network.Junctions.Where(j =>
                     (!j.IsExcluded
                      // Roundabout parent junctions are IsExcluded by the harmonizer, but on the no-blend
                      // path §3 pins a valid HarmonizedElevation (the local ring Z). Fill their connector-
                      // mouth gaps too — otherwise the skipped fill leaves a hard cliff at every ring↔
                      // connector seam. Legacy path never sets it (NaN) → roundabout fill stays skipped.
                      || (j.Type == JunctionType.Roundabout && !float.IsNaN(j.HarmonizedElevation)))
                     && j.Contributors.Count >= 2))
        {
            // Doc 13 §3.3: a junction whose EVERY contributor section is an excluded deck (deck-deck
            // merge — a ramp span landing mid-span on a trunk) has no stamped road pixels around it;
            // the disk would be a pure ground-to-deck terrain pillar at deck Z under a mid-air joint.
            // Opt-in via EnableBridgeToBridgeAbutmentSuppression on any contributor spline (off ⇒
            // legacy fill, byte-identical).
            if (junction.Contributors.All(c => c.CrossSection.IsExcluded) &&
                junction.Contributors.Any(c =>
                    c.Spline.Parameters.BridgeRules?.EnableBridgeToBridgeAbutmentSuppression == true))
            {
                deckJunctionSkips++;
                continue;
            }

            // Use the max half-width + margin across all contributors
            var maxHalfWidth = 0f;
            var maxMargin = 0f;
            var junctionSplineId = -1;
            foreach (var c in junction.Contributors)
            {
                var hw = c.CrossSection.EffectiveRoadWidth / 2.0f;
                var m = splineParams.TryGetValue(c.Spline.SplineId, out var cp)
                    ? cp.RoadEdgeProtectionBufferMeters
                    : 2.0f;
                if (hw + m > maxHalfWidth + maxMargin)
                {
                    maxHalfWidth = hw;
                    maxMargin = m;
                    junctionSplineId = c.Spline.SplineId;
                }
            }

            var fillRadius = maxHalfWidth + maxMargin;
            var fillRadiusPx = (int)MathF.Ceiling(fillRadius / metersPerPixel) + 1;
            var jcPx = (int)(junction.Position.X / metersPerPixel);
            var jcPy = (int)(junction.Position.Y / metersPerPixel);

            var jMinX = Math.Max(0, jcPx - fillRadiusPx);
            var jMaxX = Math.Min(width - 1, jcPx + fillRadiusPx);
            var jMinY = Math.Max(0, jcPy - fillRadiusPx);
            var jMaxY = Math.Min(height - 1, jcPy + fillRadiusPx);

            var fillRadiusSq = fillRadius * fillRadius;

            // Determine junction elevation: use harmonized elevation if valid,
            // otherwise average the contributors' target elevations
            var junctionElev = junction.HarmonizedElevation;
            if (float.IsNaN(junctionElev))
            {
                var validElevs = junction.Contributors
                    .Select(c => c.CrossSection.TargetElevation)
                    .Where(e => !float.IsNaN(e) && !float.IsInfinity(e))
                    .ToList();
                junctionElev = validElevs.Count > 0 ? validElevs.Average() : float.NaN;
            }

            if (float.IsNaN(junctionElev))
                continue;

            for (var y = jMinY; y <= jMaxY; y++)
            for (var x = jMinX; x <= jMaxX; x++)
            {
                var worldX = x * metersPerPixel;
                var worldY = y * metersPerPixel;
                var dx = worldX - junction.Position.X;
                var dy = worldY - junction.Position.Y;

                if (dx * dx + dy * dy > fillRadiusSq)
                    continue;

                if (mask[y, x] == 0)
                {
                    // Gap pixel — fill it
                    mask[y, x] = 255;
                    elevation[y, x] = junctionElev;
                    splineOwner[y, x] = junctionSplineId;
                    maskedPixels++;
                    junctionPixelsFilled++;
                }
                // Don't overwrite existing mask pixels — they have per-pixel
                // banking-aware elevation which is more accurate than the flat junction elevation
            }
        }

        if (junctionPixelsFilled > 0)
            TerrainCreationLogger.Current?.Detail(
                $"Filled {junctionPixelsFilled:N0} junction gap pixels across {network.Junctions.Count(j => !j.IsExcluded)} junctions");

        if (deckJunctionSkips > 0)
            TerrainCreationLogger.Current?.Detail(
                $"[BRIDGE-B2B] junction fill skipped at {deckJunctionSkips} all-excluded deck junction(s)");

        TerrainCreationLogger.Current?.Detail(
            $"Built combined mask with elevation: {maskedPixels} road pixels across {crossSectionsBySpline.Count} splines");

        return new CombinedMaskResult(mask, elevation, splineOwner, maskedPixels);
    }

    /// <summary>
    ///     Phase A.8 — rasterize a single spline's per-segment polygons into mask/elevation/owner.
    ///     Used by both Pass 1 (surface) and Pass 2 (corridor) when EnableSurfaceWidthProtection is on.
    ///     <paramref name="useSurfaceWidthOnly" /> = true → halfWidth = SurfaceWidth/2 (Pass 1).
    ///     <paramref name="useSurfaceWidthOnly" /> = false → halfWidth = EffectiveRoadWidth/2 + margin (Pass 2).
    /// </summary>
    internal static int RasterizeSplinePolygons(
        List<UnifiedCrossSection> sections,
        int splineId,
        SplineOverlapMetadata splineMetadata,
        Dictionary<int, SplineOverlapMetadata> metadataByOwnerId,
        bool enableSurfacePriorityOverride,
        float margin,
        bool useSurfaceWidthOnly,
        byte[,] mask,
        float[,] elevation,
        int[,] splineOwner,
        int width,
        int height,
        float metersPerPixel,
        Span<float> intersections)
    {
        var maskedPixels = 0;

        for (var i = 0; i < sections.Count - 1; i++)
        {
            var cs1 = sections[i];
            var cs2 = sections[i + 1];

            // Never stitch a corridor quad across an EXCLUDED gap. `sections` has already had its excluded
            // cross-sections filtered out, so two list-consecutive sections whose LocalIndex jumps by more than
            // 1 straddle a removed run — a bridge/tunnel span (or another excluded zone). Bridging it rasterizes
            // one giant deck-height quad over ground the structure must NOT terraform: the rule "a bridge spline
            // does not change the terrain it spans". The span stays unmasked so the terrain under the deck keeps
            // its natural shape (only the approach ramps, which are real sections, build embankments). For a
            // normal road LocalIndex is always consecutive, so this is a no-op (byte-identical).
            if (cs2.LocalIndex - cs1.LocalIndex > 1)
                continue;

            if (!IsValidTargetElevation(cs1.TargetElevation) ||
                !IsValidTargetElevation(cs2.TargetElevation))
                continue;

            // Pass 1 (surface): SurfaceWidth/2 + a small protection margin so chord-slivers on the
            // convex edge of curved/junction segments stay in the flat protected zone instead of
            // dipping into Pass 2's smoothing corridor (the "bite" out of an elevated road's edge).
            // Pass 2 (corridor): EffectiveRoadWidth/2 + the edge-protection buffer.
            var halfWidth1 = useSurfaceWidthOnly
                ? cs1.SurfaceWidth / 2.0f + margin
                : cs1.EffectiveRoadWidth / 2.0f + margin;
            var halfWidth2 = useSurfaceWidthOnly
                ? cs2.SurfaceWidth / 2.0f + margin
                : cs2.EffectiveRoadWidth / 2.0f + margin;

            var corners = new Vector2[4];
            corners[0] = cs1.CenterPoint - cs1.NormalDirection * halfWidth1;
            corners[1] = cs1.CenterPoint + cs1.NormalDirection * halfWidth1;
            corners[2] = cs2.CenterPoint + cs2.NormalDirection * halfWidth2;
            corners[3] = cs2.CenterPoint - cs2.NormalDirection * halfWidth2;

            var pixelCorners = new Vector2[4];
            for (var c = 0; c < 4; c++)
                pixelCorners[c] = new Vector2(corners[c].X / metersPerPixel, corners[c].Y / metersPerPixel);

            var minY = Math.Max(0, (int)MathF.Floor(pixelCorners.Min(c => c.Y)));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(pixelCorners.Max(c => c.Y)));
            var minX = Math.Max(0, (int)MathF.Floor(pixelCorners.Min(c => c.X)));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(pixelCorners.Max(c => c.X)));

            for (var y = minY; y <= maxY; y++)
            {
                var scanY = y + 0.5f;
                var intersectionCount = 0;
                for (var e = 0; e < 4; e++)
                {
                    var p1 = pixelCorners[e];
                    var p2 = pixelCorners[(e + 1) % 4];

                    if ((p1.Y <= scanY && p2.Y > scanY) || (p2.Y <= scanY && p1.Y > scanY))
                    {
                        var t = (scanY - p1.Y) / (p2.Y - p1.Y);
                        intersections[intersectionCount++] = p1.X + t * (p2.X - p1.X);
                    }
                }

                if (intersectionCount < 2)
                    continue;

                for (var si = 1; si < intersectionCount; si++)
                {
                    var key = intersections[si];
                    var sj = si - 1;
                    while (sj >= 0 && intersections[sj] > key)
                    {
                        intersections[sj + 1] = intersections[sj];
                        sj--;
                    }
                    intersections[sj + 1] = key;
                }

                for (var pair = 0; pair + 1 < intersectionCount; pair += 2)
                {
                    var xStart = Math.Max(minX, (int)MathF.Ceiling(intersections[pair]));
                    var xEnd = Math.Min(maxX, (int)MathF.Floor(intersections[pair + 1]));

                    for (var x = xStart; x <= xEnd; x++)
                    {
                        var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                        var pixelElevation = BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos);

                        if (mask[y, x] == 0)
                        {
                            mask[y, x] = 255;
                            elevation[y, x] = pixelElevation;
                            splineOwner[y, x] = splineId;
                            maskedPixels++;
                        }
                        else
                        {
                            // Phase A.8.2: contested pixel — defer to ContestedPixelResolver.
                            // Same-spline re-stamp keeps lower elevation (banking refinement).
                            // Different-spline conflict: flag-off OR Pass-2 → keep existing; flag-on AND
                            // Pass-1 → multi-key cascade (Priority → TotalLength → SplineId) decides.
                            var existingOwnerId = splineOwner[y, x];
                            var existingMeta = metadataByOwnerId.TryGetValue(existingOwnerId, out var em)
                                ? em
                                : new SplineOverlapMetadata(SplineId: existingOwnerId, Priority: 0, TotalLengthMeters: 0f);

                            var outcome = ContestedPixelResolver.Resolve(
                                existing: existingMeta, existingElev: elevation[y, x],
                                candidate: splineMetadata, candidateElev: pixelElevation,
                                useSurfaceWidthOnly,
                                enableSurfacePriorityOverride);

                            elevation[y, x] = outcome.NewElevation;
                            if (outcome.TakeOwnership)
                                splineOwner[y, x] = splineId;
                        }
                    }
                }
            }
        }

        return maskedPixels;
    }

    /// <summary>
    /// Validates that a target elevation is valid.
    /// </summary>
    internal static bool IsValidTargetElevation(float elevation)
    {
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;
        if (elevation < -1000.0f)
            return false;
        return true;
    }
}
