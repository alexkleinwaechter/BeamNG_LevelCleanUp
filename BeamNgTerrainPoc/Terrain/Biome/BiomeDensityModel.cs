namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Maps the UI density slider (0–100 %) to physical item counts.
/// 100 % is anchored to the packing density of the item's footprint
/// (radius from managedItemData, mean scale, spacing factor), so the slider
/// feels the same for a bush (r=0.5) and a big oak (r=2).
/// </summary>
public static class BiomeDensityModel
{
    public const double DefaultSpacingFactor = 1.0;
    public const double DefaultRadiusMeters = 0.5;

    /// <summary>
    /// Minimum footprint radius for the density anchor. Many managedItemData entries carry
    /// tiny radii (grass tufts: 0.05–0.2 m); anchoring 100 % density to those produced
    /// 100+ items/m² and multi-gigabyte forest files. 0.25 m caps the anchor at ~5 items/m².
    /// The spacing rule in the sampler still uses the real radius.
    /// </summary>
    public const double MinFootprintRadiusMeters = 0.25;

    /// <summary>
    /// Items per square meter at 100 % density: one item per footprint circle of radius
    /// max(itemRadius, floor) * meanScale. Deliberately independent of the spacing factor —
    /// spacing thins the realized forest, it must not inflate the target count.
    /// </summary>
    public static double MaxDensityPerSquareMeter(double radiusMeters, double meanScale)
    {
        var r = Math.Max(radiusMeters, MinFootprintRadiusMeters) * Math.Max(meanScale, 0.1);
        return 1.0 / (Math.PI * r * r);
    }

    public static double DensityPerSquareMeter(
        double densityPercent,
        double radiusMeters,
        double scaleMin,
        double scaleMax)
    {
        var pct = Math.Clamp(densityPercent, 0.0, 100.0) / 100.0;
        var meanScale = (scaleMin + scaleMax) / 2.0;
        return pct * MaxDensityPerSquareMeter(radiusMeters, meanScale);
    }

    /// <summary>
    /// Expected item count for a zone. The sampler treats this as a target; with strict
    /// filters or crowded zones the realized count saturates below it (by design).
    /// </summary>
    public static long EstimateCount(
        long zonePixelCount,
        float metersPerPixel,
        double densityPercent,
        double radiusMeters,
        double scaleMin,
        double scaleMax)
    {
        if (zonePixelCount <= 0 || densityPercent <= 0)
        {
            return 0;
        }

        var areaM2 = zonePixelCount * (double)metersPerPixel * metersPerPixel;
        var density = DensityPerSquareMeter(densityPercent, radiusMeters, scaleMin, scaleMax);
        return (long)Math.Round(areaM2 * density);
    }
}
