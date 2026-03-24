using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Single-pass terrain blender implementing BeamNG's Master Spline approach.
///
/// Algorithm (from ai_docs/beamng_terraform_spline_blending.md):
/// 1. Road mask pixels (mask=255): pin to road surface elevation
/// 2. Blend zone pixels (0 &lt; dist &lt;= DOI): blend toward nearest road pixel's elevation
///    using w = (1 - dist/DOI)^falloffExp
/// 3. Outside DOI: keep original terrain
///
/// Key insight: each blend-zone pixel blends toward the elevation of its NEAREST
/// road surface pixel (tracked by the EDT), not a per-spline ownership target.
/// Per-pixel parameters (DOI, falloff) come from the spline that OWNS the nearest
/// road mask pixel, ensuring consistent blending even at junctions where cross-sections
/// from different splines are close together.
/// </summary>
public class SinglePassBlender
{
    public record BlendResult(
        float[,] HeightMap,
        int RoadPixels,
        int BlendedPixels,
        int UnmodifiedPixels);

    /// <summary>
    /// Blends terrain using the single-pass nearest-source approach.
    /// </summary>
    /// <param name="originalHeightMap">Original unmodified terrain</param>
    /// <param name="roadMask">255 = road surface, 0 = terrain</param>
    /// <param name="roadElevationMap">Road surface elevation where mask=255</param>
    /// <param name="splineOwnerMap">SplineId that owns each mask pixel (-1 = no owner)</param>
    /// <param name="edt">EDT result with distances and nearest-source tracking</param>
    /// <param name="network">The unified road network</param>
    /// <param name="metersPerPixel">Scale factor</param>
    public BlendResult Blend(
        float[,] originalHeightMap,
        byte[,] roadMask,
        float[,] roadElevationMap,
        int[,] splineOwnerMap,
        DistanceFieldResult edt,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        var height = originalHeightMap.GetLength(0);
        var width = originalHeightMap.GetLength(1);
        var result = (float[,])originalHeightMap.Clone();

        // Global early-rejection cutoff.
        // Non-Exponential splines may auto-extend DOI for steep terrain (slope constraint),
        // so we need a generous upper bound. Exponential splines use only the user's DOI
        // (the curve naturally reaches 0 at the boundary — no slope-based extension needed).
        var maxUserDoi = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);
        var maxDoi = maxUserDoi;

        var nonExpSplines = network.Splines
            .Where(s => s.Parameters.BlendFunctionType != BlendFunctionType.Exponential)
            .ToList();

        if (nonExpSplines.Count > 0)
        {
            var minSlopeDeg = nonExpSplines.Min(s => s.Parameters.SideMaxSlopeDegrees);
            if (minSlopeDeg > 0.1f && minSlopeDeg < 89f)
            {
                // Estimate max possible elevation difference from the heightmap range
                var minElev = float.MaxValue;
                var maxElev = float.MinValue;
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var h = originalHeightMap[y, x];
                    if (h < minElev) minElev = h;
                    if (h > maxElev) maxElev = h;
                }
                var elevRange = maxElev - minElev;
                var maxSlopeDist = elevRange / MathF.Tan(minSlopeDeg * MathF.PI / 180f);
                maxDoi = MathF.Max(maxUserDoi, MathF.Min(maxSlopeDist, 500f)); // cap at 500m
            }
        }

        // Build spline parameter lookup
        var splineParams = network.Splines.ToDictionary(
            s => s.SplineId,
            s => (
                HalfWidth: s.Parameters.RoadWidthMeters / 2.0f,
                DOI: s.Parameters.TerrainAffectedRangeMeters,
                FalloffExp: s.Parameters.FalloffExponent,
                BlendFunction: s.Parameters.BlendFunctionType,
                MaxSlopeDeg: s.Parameters.SideMaxSlopeDegrees
            ));

        var roadPixels = 0;
        var blendedPixels = 0;
        var unmodifiedPixels = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, height, options, y =>
        {
            var localRoad = 0;
            var localBlend = 0;
            var localUnmod = 0;

            for (var x = 0; x < width; x++)
            {
                var dist = edt.Distances[y, x];

                // Case 1: Road surface pixel — pin to road elevation
                if (roadMask[y, x] == 255)
                {
                    var roadElev = roadElevationMap[y, x];
                    if (!float.IsNaN(roadElev))
                        result[y, x] = roadElev;
                    localRoad++;
                    continue;
                }

                // Early rejection: outside maximum influence zone
                if (dist > maxDoi || dist <= 0)
                {
                    localUnmod++;
                    continue;
                }

                // Find the nearest road pixel's elevation via EDT source tracking
                var nearX = edt.NearestSourceX[y, x];
                var nearY = edt.NearestSourceY[y, x];
                if (nearX < 0 || nearY < 0)
                {
                    localUnmod++;
                    continue;
                }

                var ribbonZ = roadElevationMap[nearY, nearX];
                if (float.IsNaN(ribbonZ))
                {
                    localUnmod++;
                    continue;
                }

                // Look up parameters from the spline that OWNS the nearest road mask pixel.
                // This is critical at junctions: the EDT tells us which road's surface is
                // closest, and we must use THAT road's DOI/falloff — not the nearest
                // cross-section center (which could belong to a different road).
                var nearestSplineId = splineOwnerMap[nearY, nearX];
                if (nearestSplineId < 0 || !splineParams.TryGetValue(nearestSplineId, out var sp))
                {
                    localUnmod++;
                    continue;
                }

                // Calculate effective DOI for this pixel.
                // Exponential: (1-t)^exp naturally reaches 0 at DOI boundary — no extension needed.
                // Non-Exponential: DOI may be too short for steep terrain — auto-extend
                // so the max slope line always reaches natural terrain (prevents cliffs).
                var doi = sp.DOI;
                if (sp.BlendFunction != BlendFunctionType.Exponential)
                {
                    var elevDiff = MathF.Abs(ribbonZ - originalHeightMap[y, x]);
                    if (sp.MaxSlopeDeg > 0.1f && sp.MaxSlopeDeg < 89f && elevDiff > 0.1f)
                    {
                        var tanMaxSlope = MathF.Tan(sp.MaxSlopeDeg * MathF.PI / 180f);
                        var slopeRequiredDist = elevDiff / tanMaxSlope;
                        doi = MathF.Max(doi, slopeRequiredDist);
                    }
                }

                // Case 2: Within effective DOI — blend
                if (dist <= doi)
                {
                    // Normalized distance: 0 at road edge, 1 at DOI boundary
                    var t = dist / doi;

                    // Compute blend weight using configured function
                    var w = BlendFunctions.Apply(t, sp.BlendFunction, sp.FalloffExp);
                    // Exponential: w goes from 1 (road edge) to 0 (DOI boundary)
                    // Existing functions (Cosine/Cubic/Quintic): w goes 0→1, so we invert
                    if (sp.BlendFunction != BlendFunctionType.Exponential)
                        w = 1f - w;

                    // Blend: road elevation × w + original terrain × (1 - w)
                    var blendedH = ribbonZ * w + originalHeightMap[y, x] * (1f - w);

                    // Enforce max side slope constraint — only for non-Exponential modes.
                    // Exponential curve shape is controlled entirely by FalloffExponent.
                    if (sp.BlendFunction != BlendFunctionType.Exponential)
                    {
                        blendedH = EnforceSideMaxSlope(
                            ribbonZ, originalHeightMap[y, x], blendedH,
                            dist, doi, sp.MaxSlopeDeg);
                    }

                    if (MathF.Abs(result[y, x] - blendedH) > 0.001f)
                        result[y, x] = blendedH;

                    localBlend++;
                }
                else
                {
                    // Case 3: Outside effective DOI — keep original
                    localUnmod++;
                }
            }

            Interlocked.Add(ref roadPixels, localRoad);
            Interlocked.Add(ref blendedPixels, localBlend);
            Interlocked.Add(ref unmodifiedPixels, localUnmod);
        });

        TerrainLogger.Info($"SinglePassBlender: {roadPixels:N0} road, {blendedPixels:N0} blended, {unmodifiedPixels:N0} unmodified");

        // Smooth only at Voronoi boundaries where nearest-source switches between
        // two different roads. This prevents terrain cliffs where blend zones overlap
        // without over-smoothing the rest of the terrain.
        SmoothVoronoiBoundaries(result, roadMask, edt, splineOwnerMap);

        return new BlendResult(result, roadPixels, blendedPixels, unmodifiedPixels);
    }

    /// <summary>
    /// Smooths ONLY at Voronoi boundaries where the nearest-source road switches
    /// between two different splines. Detects boundary pixels by checking if any
    /// 4-neighbor has a different spline owner, then applies a local box blur
    /// within a small radius around those boundary pixels only.
    /// Does NOT touch: road surfaces, single-road blend zones, unmodified terrain.
    /// </summary>
    private static void SmoothVoronoiBoundaries(
        float[,] heightMap, byte[,] roadMask,
        DistanceFieldResult edt, int[,] splineOwnerMap)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        const int blurRadius = 4;
        const int boundaryExpandRadius = 4; // how far from the boundary to smooth

        // Step 1: Detect Voronoi boundary pixels — blend-zone pixels where a
        // 4-neighbor has a different nearest-source spline owner.
        var isBoundary = new bool[height, width];
        var boundaryCount = 0;

        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            // Only consider blend-zone pixels (not road surface, not unmodified)
            if (roadMask[y, x] == 255) continue;
            var srcX = edt.NearestSourceX[y, x];
            var srcY = edt.NearestSourceY[y, x];
            if (srcX < 0 || srcX >= width || srcY < 0 || srcY >= height) continue;

            var myOwner = splineOwnerMap[srcY, srcX];
            if (myOwner < 0) continue;

            // Check 4-neighbors for different owner
            bool atBoundary = false;
            for (var dir = 0; dir < 4; dir++)
            {
                var ny = y + (dir < 2 ? 0 : dir == 2 ? 1 : -1);
                var nx = x + (dir < 2 ? (dir == 0 ? 1 : -1) : 0);
                var nSrcX = edt.NearestSourceX[ny, nx];
                var nSrcY = edt.NearestSourceY[ny, nx];
                if (nSrcX < 0 || nSrcX >= width || nSrcY < 0 || nSrcY >= height) continue;

                var neighborOwner = splineOwnerMap[nSrcY, nSrcX];
                if (neighborOwner >= 0 && neighborOwner != myOwner)
                {
                    atBoundary = true;
                    break;
                }
            }

            if (atBoundary)
            {
                isBoundary[y, x] = true;
                boundaryCount++;
            }
        }

        if (boundaryCount == 0)
            return; // No Voronoi boundaries — nothing to smooth

        // Step 2: Expand the boundary mask by boundaryExpandRadius so the blur
        // covers a smooth band around the boundary, not just the 1px edge.
        var smoothMask = new bool[height, width];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!isBoundary[y, x]) continue;

            for (var dy = -boundaryExpandRadius; dy <= boundaryExpandRadius; dy++)
            for (var dx = -boundaryExpandRadius; dx <= boundaryExpandRadius; dx++)
            {
                var ny = y + dy;
                var nx = x + dx;
                if (ny >= 0 && ny < height && nx >= 0 && nx < width
                    && roadMask[ny, nx] != 255) // never include road surface pixels
                {
                    smoothMask[ny, nx] = true;
                }
            }
        }

        // Step 3: Separable box blur on smoothMask pixels only
        var temp = new float[height, width];

        // Horizontal pass
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                if (!smoothMask[y, x])
                {
                    temp[y, x] = heightMap[y, x];
                    continue;
                }

                var sum = 0f;
                var count = 0;
                for (var k = -blurRadius; k <= blurRadius; k++)
                {
                    var nx = x + k;
                    if (nx >= 0 && nx < width)
                    {
                        sum += heightMap[y, nx];
                        count++;
                    }
                }
                temp[y, x] = count > 0 ? sum / count : heightMap[y, x];
            }
        });

        // Vertical pass
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                if (!smoothMask[y, x]) continue;

                var sum = 0f;
                var count = 0;
                for (var k = -blurRadius; k <= blurRadius; k++)
                {
                    var ny = y + k;
                    if (ny >= 0 && ny < height)
                    {
                        sum += temp[ny, x];
                        count++;
                    }
                }
                heightMap[y, x] = count > 0 ? sum / count : temp[y, x];
            }
        });

        TerrainLogger.Info($"  Voronoi boundary smoothing: {boundaryCount:N0} boundary pixels detected, smoothed band ±{boundaryExpandRadius}px");
    }

    /// <summary>
    /// Enforces maximum side slope constraint.
    /// Prevents terrain from exceeding a configured maximum slope angle in the transition zone.
    /// </summary>
    private static float EnforceSideMaxSlope(
        float roadElevation, float terrainElevation, float blendedElevation,
        float distFromRoadEdge, float doi, float maxSlopeDegrees)
    {
        if (distFromRoadEdge < 0.01f) return blendedElevation;
        var elevDiff = MathF.Abs(roadElevation - terrainElevation);
        if (elevDiff < 0.1f) return blendedElevation;

        var tanMaxSlope = MathF.Tan(maxSlopeDegrees * MathF.PI / 180f);
        var maxElevChange = distFromRoadEdge * tanMaxSlope;
        bool isCut = roadElevation > terrainElevation;

        var slopeConstrained = isCut
            ? MathF.Max(roadElevation - maxElevChange, terrainElevation)
            : MathF.Min(roadElevation + maxElevChange, terrainElevation);

        var exceedsSlope = isCut
            ? blendedElevation < slopeConstrained
            : blendedElevation > slopeConstrained;

        return exceedsSlope ? slopeConstrained : blendedElevation;
    }
}
