using System.Globalization;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     W1 validation harness for Phase 1.9 junction pinning.
///     Emits per-junction residual CSV, three-band heatmap PNG, w-test summary,
///     ±d quadratic-growth check rows, and aggregate stats — all to MT_TerrainGeneration/.
///     Thresholds and statistical model from Oude Elberink &amp; Vosselman 2007.
/// </summary>
public static class JunctionPinningValidationExporter
{
    public enum DeltaBand { Green, Yellow, Red }

    public static DeltaBand ClassifyBand(float delta)
    {
        var abs = MathF.Abs(delta);
        if (abs < 0.20f) return DeltaBand.Green;
        if (abs < 0.50f) return DeltaBand.Yellow;
        return DeltaBand.Red;
    }

    public static float ComputeWStatistic(float deltaDeg, float sigmaDeg)
    {
        if (sigmaDeg <= 0f) return 0f;
        return MathF.Abs(deltaDeg) / sigmaDeg;
    }

    public static float GetSigmaPredictedDeg(string? osmRoadType)
    {
        if (string.IsNullOrEmpty(osmRoadType)) return 1.0f;
        return osmRoadType switch
        {
            "motorway" or "motorway_link" or "trunk" or "trunk_link" => 2.0f,
            _ => 1.0f
        };
    }

    private static long? GetContributorEndpointOsmNodeId(JunctionContributor c)
    {
        if (!c.IsEndpoint) return null;
        return c.IsSplineStart ? c.Spline.StartOsmNodeId : c.Spline.EndOsmNodeId;
    }

    private static long? GetJunctionOsmNodeId(NetworkJunction j)
    {
        foreach (var c in j.Contributors)
        {
            var id = GetContributorEndpointOsmNodeId(c);
            if (id.HasValue) return id;
        }
        return null;
    }

    private static string FormatOsmNodeId(long? id)
        => id?.ToString(CultureInfo.InvariantCulture) ?? "";

    public record AggregateStats(
        int JunctionCount,
        float PinResidualMean,
        float PinResidualSigma,
        float PinResidualMaxAbs,
        int WTestOutliersGt3,
        long RedBandPixelCount);

    public static AggregateStats Export(
        UnifiedRoadNetwork network,
        float[,] modifiedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var redBandCount = ExportThreeBandHeatmap(
            modifiedHeightMap, originalHeightMap,
            Path.Combine(outputDirectory, "delta_three_band.png"));

        var residualStats = ExportJunctionResidualsCsv(
            network, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "junction_residuals.csv"));

        var wStats = ExportWTestSummary(
            network, modifiedHeightMap, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "w_test_summary.csv"));

        ExportQuadraticGrowthCsv(
            network, modifiedHeightMap, originalHeightMap, metersPerPixel,
            Path.Combine(outputDirectory, "quadratic_growth.csv"));

        return new AggregateStats(
            JunctionCount: residualStats.Count,
            PinResidualMean: residualStats.Mean,
            PinResidualSigma: residualStats.Sigma,
            PinResidualMaxAbs: residualStats.MaxAbs,
            WTestOutliersGt3: wStats.OutliersGt3,
            RedBandPixelCount: redBandCount);
    }

    private static long ExportThreeBandHeatmap(float[,] modified, float[,] original, string path)
    {
        var h = modified.GetLength(0);
        var w = modified.GetLength(1);
        using var image = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 255));
        long redCount = 0;

        var green = new Rgba32(40, 180, 60, 255);
        var yellow = new Rgba32(230, 200, 30, 255);
        var red = new Rgba32(220, 40, 40, 255);
        var black = new Rgba32(0, 0, 0, 255);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var m = modified[y, x];
                    var o = original[y, x];
                    if (float.IsNaN(m) || float.IsNaN(o))
                    {
                        row[x] = black;
                        continue;
                    }
                    var band = ClassifyBand(m - o);
                    row[x] = band switch
                    {
                        DeltaBand.Green => green,
                        DeltaBand.Yellow => yellow,
                        DeltaBand.Red => red,
                        _ => black
                    };
                    if (band == DeltaBand.Red) redCount++;
                }
            }
        });

        image.SaveAsPng(path);
        return redCount;
    }

    public record ResidualStats(int Count, float Mean, float Sigma, float MaxAbs);

    public static ResidualStats ComputeResidualStats(IReadOnlyList<float> residuals)
    {
        if (residuals.Count == 0) return new ResidualStats(0, 0f, 0f, 0f);
        var mean = residuals.Average();
        var sumSq = residuals.Sum(r => (r - mean) * (r - mean));
        var sigma = MathF.Sqrt(sumSq / residuals.Count);
        var maxAbs = residuals.Max(MathF.Abs);
        return new ResidualStats(residuals.Count, mean, sigma, maxAbs);
    }

    private static ResidualStats ExportJunctionResidualsCsv(
        UnifiedRoadNetwork network, float[,] original, float metersPerPixel, string path)
    {
        var mapHeight = original.GetLength(0);
        var mapWidth = original.GetLength(1);
        var residuals = new List<float>();

        using var writer = new StreamWriter(path);
        writer.WriteLine("junction_id,type,position_x,position_y,pinned_z,terrain_z," +
                         "max_contributor_z,min_contributor_z,mean_contributor_z," +
                         "residual_pinned_minus_terrain,residual_max_minus_min,n_contributors,osm_node_id");

        foreach (var j in network.Junctions.Where(j => !j.IsExcluded))
        {
            var px = Math.Clamp((int)(j.Position.X / metersPerPixel), 0, mapWidth - 1);
            var py = Math.Clamp((int)(j.Position.Y / metersPerPixel), 0, mapHeight - 1);
            var terrainZ = original[py, px];
            var pinned = j.HarmonizedElevation;

            var contribElevs = j.Contributors
                .Select(c => c.CrossSection.TargetElevation)
                .Where(z => !float.IsNaN(z))
                .ToList();
            var maxZ = contribElevs.Count > 0 ? contribElevs.Max() : float.NaN;
            var minZ = contribElevs.Count > 0 ? contribElevs.Min() : float.NaN;
            var meanZ = contribElevs.Count > 0 ? contribElevs.Average() : float.NaN;

            var resPinTerr = float.IsNaN(pinned) || float.IsNaN(terrainZ) ? float.NaN : pinned - terrainZ;
            var resMaxMin = contribElevs.Count > 0 ? maxZ - minZ : float.NaN;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{j.JunctionId},{j.Type},{j.Position.X:F2},{j.Position.Y:F2}," +
                $"{pinned:F3},{terrainZ:F3},{maxZ:F3},{minZ:F3},{meanZ:F3}," +
                $"{resPinTerr:F3},{resMaxMin:F3},{j.Contributors.Count},{FormatOsmNodeId(GetJunctionOsmNodeId(j))}"));

            if (!float.IsNaN(resPinTerr)) residuals.Add(resPinTerr);
        }

        return ComputeResidualStats(residuals);
    }

    private record WTestStats(int OutliersGt3);

    private static WTestStats ExportWTestSummary(
        UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
    {
        var outliers = 0;

        using var writer = new StreamWriter(path);
        writer.WriteLine("junction_id,spline_id,is_start,tangent_at_node_deg,tangent_past_ramp_deg," +
                         "delta_deg,sigma_predicted_deg,w,osm_node_id");

        foreach (var j in network.Junctions.Where(j => !j.IsExcluded && !float.IsNaN(j.HarmonizedElevation)))
        {
            foreach (var c in j.Contributors.Where(c => c.IsEndpoint))
            {
                var sigma = GetSigmaPredictedDeg(c.Spline.OsmRoadType);
                var blendDist = c.Spline.Parameters.JunctionHarmonizationParameters
                    ?.GetEffectiveBlendDistance(c.Spline.Parameters.RoadWidthMeters) ?? 30f;

                var nodeAngle = SampleTangentAngleDeg(modified, metersPerPixel, c, 0f);
                var pastAngle = SampleTangentAngleDeg(modified, metersPerPixel, c, blendDist * 1.05f);
                var delta = pastAngle - nodeAngle;
                var w = ComputeWStatistic(delta, sigma);
                if (w > 3f) outliers++;

                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{j.JunctionId},{c.Spline.SplineId},{c.IsSplineStart}," +
                    $"{nodeAngle:F2},{pastAngle:F2},{delta:F2},{sigma:F2},{w:F2}," +
                    $"{FormatOsmNodeId(GetContributorEndpointOsmNodeId(c))}"));
            }
        }
        return new WTestStats(outliers);
    }

    private static float SampleTangentAngleDeg(
        float[,] heightMap, float metersPerPixel, JunctionContributor c, float distanceFromNodeMeters)
    {
        var spline = c.Spline.Spline;
        var totalLen = spline.TotalLength;

        float SignedDist(float d) => c.IsSplineStart ? d : totalLen - d;

        var distBefore = MathF.Max(0f, distanceFromNodeMeters - 1f);
        var distAfter = MathF.Min(totalLen, distanceFromNodeMeters + 1f);

        var posBefore = spline.GetPointAtDistance(SignedDist(distBefore));
        var posAfter = spline.GetPointAtDistance(SignedDist(distAfter));

        var mapH = heightMap.GetLength(0);
        var mapW = heightMap.GetLength(1);
        float ZAt(System.Numerics.Vector2 p)
        {
            var px = Math.Clamp((int)(p.X / metersPerPixel), 0, mapW - 1);
            var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, mapH - 1);
            return heightMap[py, px];
        }

        var dz = ZAt(posAfter) - ZAt(posBefore);
        var dx = System.Numerics.Vector2.Distance(posAfter, posBefore);
        if (dx < 0.01f) return 0f;
        return MathF.Atan2(dz, dx) * (180f / MathF.PI);
    }

    private static void ExportQuadraticGrowthCsv(
        UnifiedRoadNetwork network, float[,] modified, float[,] original, float metersPerPixel, string path)
    {
        var distances = new[] { 5f, 15f, 30f, 60f };
        var mapH = modified.GetLength(0);
        var mapW = modified.GetLength(1);

        using var writer = new StreamWriter(path);
        writer.Write("junction_id,spline_id,is_start");
        foreach (var d in distances)
            writer.Write(string.Create(CultureInfo.InvariantCulture, $",delta_{d:0}m"));
        writer.WriteLine(",osm_node_id");

        float DeltaAt(System.Numerics.Vector2 p)
        {
            var px = Math.Clamp((int)(p.X / metersPerPixel), 0, mapW - 1);
            var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, mapH - 1);
            return modified[py, px] - original[py, px];
        }

        foreach (var j in network.Junctions.Where(j => !j.IsExcluded && !float.IsNaN(j.HarmonizedElevation)))
        foreach (var c in j.Contributors.Where(c => c.IsEndpoint))
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{j.JunctionId},{c.Spline.SplineId},{c.IsSplineStart}"));
            var totalLen = c.Spline.Spline.TotalLength;
            foreach (var d in distances)
            {
                var distFromStart = c.IsSplineStart ? d : MathF.Max(0f, totalLen - d);
                var samplePos = c.Spline.Spline.GetPointAtDistance(distFromStart);
                writer.Write(string.Create(CultureInfo.InvariantCulture, $",{DeltaAt(samplePos):F3}"));
            }
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $",{FormatOsmNodeId(GetContributorEndpointOsmNodeId(c))}"));
        }
    }
}
