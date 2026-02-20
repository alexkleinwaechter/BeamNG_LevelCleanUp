namespace BeamNG.Procedural3D.Building;

/// <summary>
/// Computes building height, wall height (heightWithoutRoof), and min_height from OSM tags.
/// Port of OSM2World's LevelAndHeightData.java — skips underground levels,
/// indoor rendering, and level numbering (not needed for BeamNG).
///
/// IMPORTANT: Roof height is computed FIRST, then included in the default total height.
/// This matches Java line 186: height = parseHeight(tags, buildingLevels * heightPerLevel + roofHeight)
///
/// Priority: explicit height/min_height tags > levels * heightPerLevel + roofHeight > defaults
/// </summary>
public static class LevelAndHeightData
{
    /// <summary>
    /// Default ridge height for multi-level buildings (matches Java BuildingPart.DEFAULT_RIDGE_HEIGHT).
    /// </summary>
    public const float DEFAULT_RIDGE_HEIGHT = 5f;

    /// <summary>
    /// Computes height/level data from OSM tags and building type defaults.
    /// Port of LevelAndHeightData.java constructor (lines 107-348, simplified).
    /// </summary>
    /// <param name="taggedHeight">Explicit height from height= or building:height= tag (meters), or null.</param>
    /// <param name="taggedMinHeight">Explicit min_height= or building:min_height= tag (meters), or null.</param>
    /// <param name="taggedFacadeHeight">Explicit facade_height= tag (meters), or null.</param>
    /// <param name="taggedLevels">Explicit building:levels= tag, or null.</param>
    /// <param name="taggedRoofHeight">Explicit roof:height= tag (meters), or null.</param>
    /// <param name="taggedRoofLevels">Explicit roof:levels= tag, or null.</param>
    /// <param name="roofShape">Resolved roof shape string (e.g., "flat", "gabled", "hipped", "dome").</param>
    /// <param name="defaults">Default values from BuildingDefaults for this building type.</param>
    /// <param name="taggedMinLevel">Explicit building:min_level= tag, or null. Used for floating parts.</param>
    /// <param name="hasWalls">Whether the building type has walls. False for carports, roof-only structures.</param>
    /// <param name="polygonDiameter">Max distance between polygon vertices. Used for dome roof height.</param>
    public static HeightResult Compute(
        float? taggedHeight,
        float? taggedMinHeight,
        float? taggedFacadeHeight,
        int? taggedLevels,
        float? taggedRoofHeight,
        int? taggedRoofLevels,
        string roofShape,
        BuildingDefaultValues defaults,
        int? taggedMinLevel = null,
        bool hasWalls = true,
        float? polygonDiameter = null)
    {
        float heightPerLevel = defaults.HeightPerLevel;

        // 1. Determine building levels (Java lines 128-143)
        int levels;
        if (taggedLevels.HasValue)
        {
            levels = Math.Max(0, taggedLevels.Value);
        }
        else if (taggedHeight.HasValue && taggedHeight.Value > 0)
        {
            // Java line 137: max(1, (int)(parseHeight(tags, -1) / defaults.heightPerLevel))
            levels = Math.Max(1, (int)(taggedHeight.Value / heightPerLevel));
        }
        else if (taggedMinLevel.HasValue && taggedMinLevel.Value > 0)
        {
            // Java line 140: buildingMinLevelWithUnderground + 1
            levels = taggedMinLevel.Value + 1;
        }
        else
        {
            levels = defaults.Levels;
        }

        // 2. Compute roof height FIRST (Java lines 147-172, BEFORE total height)
        float roofHeight = ComputeRoofHeight(taggedRoofHeight, taggedRoofLevels, roofShape,
            levels, heightPerLevel, polygonDiameter);

        // 3. Total height includes roofHeight in default (Java line 186):
        //    height = parseHeight(tags, buildingLevels * defaults.heightPerLevel + roofHeight)
        float height = taggedHeight ?? (levels * heightPerLevel + roofHeight);
        height = MathF.Max(height, 0.01f);

        // facade_height overrides: roof height = total height - facade height
        if (taggedFacadeHeight.HasValue && taggedFacadeHeight.Value > 0 && height > taggedFacadeHeight.Value)
        {
            roofHeight = height - taggedFacadeHeight.Value;
        }

        float heightWithoutRoof = MathF.Max(0, height - roofHeight);
        // Java line 190: Math.round(heightWithoutRoof * 1e4) / 1e4
        heightWithoutRoof = MathF.Round(heightWithoutRoof * 1e4f) / 1e4f;

        // 4. Min height (Java lines 192-206)
        float minHeight;
        if (taggedMinHeight.HasValue)
        {
            minHeight = taggedMinHeight.Value;
        }
        else if (taggedMinLevel.HasValue && taggedMinLevel.Value > 0 && levels > 0)
        {
            // Java line 196: minHeight = (heightWithoutRoof / buildingLevels) * buildingMinLevel
            minHeight = (heightWithoutRoof / levels) * taggedMinLevel.Value;
        }
        else if (!hasWalls)
        {
            // Java line 198: minHeight = heightWithoutRoof - 0.3
            minHeight = heightWithoutRoof - 0.3f;
        }
        else
        {
            minHeight = 0f;
        }

        // Java lines 203-206: clamp min_height
        if (minHeight > heightWithoutRoof)
        {
            minHeight = heightWithoutRoof - 0.1f;
        }

        return new HeightResult(height, heightWithoutRoof, minHeight, roofHeight, levels, heightPerLevel);
    }

    /// <summary>
    /// Computes roof height from explicit tags or defaults.
    /// Port of roof height determination in LevelAndHeightData.java (lines 147-172).
    /// Called BEFORE total height so result can be included in default height estimate.
    /// </summary>
    private static float ComputeRoofHeight(
        float? taggedRoofHeight,
        int? taggedRoofLevels,
        string roofShape,
        int levels,
        float heightPerLevel,
        float? polygonDiameter)
    {
        // Java line 149: roofHeight = roof.calculatePreliminaryHeight()
        // → parseMeasure(tags.getValue("roof:height"))
        if (taggedRoofHeight.HasValue)
            return taggedRoofHeight.Value;

        // Java line 151-152: FlatRoof → roofHeight = 0
        if (roofShape is "flat" or "")
            return 0f;

        // Java lines 158-162: roof:levels tag
        if (taggedRoofLevels.HasValue && taggedRoofLevels.Value > 0)
            return taggedRoofLevels.Value * heightPerLevel;

        // Java lines 165-166: DomeRoof → roofHeight = outline.getDiameter() / 2
        if (roofShape == "dome" && polygonDiameter.HasValue && polygonDiameter.Value > 0)
            return polygonDiameter.Value / 2f;

        // Java lines 167-171: default ridge height
        // buildingLevels == 1 ? 1.0 : DEFAULT_RIDGE_HEIGHT
        return levels <= 1 ? 1.0f : DEFAULT_RIDGE_HEIGHT;
    }

    /// <summary>
    /// Result of height computation for a building or building part.
    /// </summary>
    public record HeightResult(
        /// <summary>Total building height in meters (including roof).</summary>
        float Height,
        /// <summary>Wall height = Height - RoofHeight.</summary>
        float HeightWithoutRoof,
        /// <summary>Base elevation above ground (for floating building parts).</summary>
        float MinHeight,
        /// <summary>Roof height in meters.</summary>
        float RoofHeight,
        /// <summary>Number of above-ground levels.</summary>
        int Levels,
        /// <summary>Height per level in meters.</summary>
        float HeightPerLevel);
}
