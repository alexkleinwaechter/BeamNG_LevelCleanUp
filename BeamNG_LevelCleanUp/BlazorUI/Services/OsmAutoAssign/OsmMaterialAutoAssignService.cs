using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Services;
using static BeamNG_LevelCleanUp.BlazorUI.Components.TerrainMaterialSettings;

namespace BeamNG_LevelCleanUp.BlazorUI.Services.OsmAutoAssign;

/// <summary>
///     Result of an auto-assign run, for UI summary display.
/// </summary>
public class OsmAutoAssignResult
{
    /// <summary>
    ///     One entry per material that received an assignment.
    /// </summary>
    public List<OsmAutoAssignEntry> Assignments { get; } = new();

    /// <summary>
    ///     Non-fatal issues (skipped materials, empty feature sets, unknown presets, ...).
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    ///     Human-readable descriptions of material order changes (base to front, roads to end).
    /// </summary>
    public List<string> OrderingChanges { get; } = new();

    /// <summary>
    ///     Total number of OSM features available in the queried bounding box.
    /// </summary>
    public int TotalOsmFeatureCount { get; set; }
}

/// <summary>
///     A single material ← OSM assignment performed by the auto-assign run.
/// </summary>
public class OsmAutoAssignEntry
{
    public string MaterialName { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public int FeatureCount { get; set; }

    /// <summary>
    ///     Applied road preset name; null for polygon (non-road) assignments.
    /// </summary>
    public string? Preset { get; set; }

    public override string ToString()
    {
        var presetInfo = Preset != null ? $", preset {Preset}" : string.Empty;
        return $"{MaterialName}: {FeatureCount} OSM feature(s) via rule '{RuleName}'{presetInfo}";
    }
}

/// <summary>
///     Auto-assigns OSM features (highway lines and landuse/natural polygons) to terrain materials
///     based on the user-editable rule matrix in <see cref="OsmMaterialAutoAssignConfig" />.
///     Keeps GenerateTerrain page code small — the page only calls <see cref="AutoAssignAsync" />
///     and shows the returned summary.
/// </summary>
public class OsmMaterialAutoAssignService
{
    /// <summary>
    ///     Fetches OSM data for the bounding box (cache-aware, same pipeline as the manual
    ///     feature selector) and applies the rule matrix to the material list in place.
    /// </summary>
    /// <param name="materials">Terrain materials of the loaded level (modified in place, incl. order).</param>
    /// <param name="boundingBox">Effective geographic bounding box of the terrain.</param>
    /// <param name="config">Optional config override; defaults to the persisted user config.</param>
    public async Task<OsmAutoAssignResult> AutoAssignAsync(
        List<TerrainMaterialItemExtended> materials,
        GeoBoundingBox boundingBox,
        OsmMaterialAutoAssignConfig? config = null)
    {
        config ??= OsmMaterialAutoAssignConfigStore.LoadOrCreateDefault();

        var result = new OsmAutoAssignResult();

        OsmQueryResult queryResult;
        using (var overpassService = new ChunkedOverpassQueryService())
        {
            queryResult = await overpassService.QueryAllFeaturesChunkedAsync(boundingBox).ConfigureAwait(false);
        }

        result.TotalOsmFeatureCount = queryResult.Features.Count;
        if (queryResult.Features.Count == 0)
        {
            result.Warnings.Add("No OSM features found for the current map area.");
            return result;
        }

        // Re-runs must be idempotent: clear previous auto-assignments from every rule-matched
        // material before re-deriving them, otherwise a material that loses its tier on the
        // re-run keeps its stale road assignment while another material gains one.
        ResetPreviousAutoAssignments(config, materials);

        // Materials claimed by a rule in this run — later rules must not touch them.
        var claimedMaterials = new HashSet<TerrainMaterialItemExtended>();
        // Feature ids already assigned — the same OSM way/polygon must never end up in two materials.
        var claimedFeatureIds = new HashSet<long>();
        // Paint priority per road material assigned in this run, used for the final ordering.
        var roadPaintPriorities = new Dictionary<TerrainMaterialItemExtended, int>();

        // The base material is reserved BEFORE the polygon rules run so heath/scrub/meadow
        // only consume the REMAINING grass materials.
        var baseMaterial = SelectBaseMaterial(config, materials, result);
        if (baseMaterial != null)
            claimedMaterials.Add(baseMaterial);

        ApplyRoadRules(config, materials, queryResult, claimedMaterials, claimedFeatureIds,
            roadPaintPriorities, result);
        ApplyPolygonRules(config, materials, queryResult, claimedMaterials, claimedFeatureIds, result);

        ApplyMaterialOrdering(config, materials, baseMaterial, roadPaintPriorities, result);

        return result;
    }

    // ========================================
    // BASE MATERIAL & ORDERING
    // ========================================

    /// <summary>
    ///     Clears assignments from previous runs on every rule-matched material so pressing the
    ///     button repeatedly yields the identical result. Materials with a manual PNG layer map
    ///     and materials no rule matches are left untouched.
    /// </summary>
    private static void ResetPreviousAutoAssignments(
        OsmMaterialAutoAssignConfig config,
        List<TerrainMaterialItemExtended> materials)
    {
        foreach (var material in materials)
        {
            if (material.LayerSourceType == LayerSourceType.PngFile &&
                !string.IsNullOrEmpty(material.LayerMapPath))
                continue;

            var matchesRoadRule = config.RoadRules.Any(r => r.MatchesMaterialName(material.InternalName));
            var matchesPolygonRule = config.PolygonRules.Any(r => r.MatchesMaterialName(material.InternalName));

            if (!matchesRoadRule && !matchesPolygonRule)
                continue;

            material.SelectedOsmFeatures = null;
            if (material.LayerSourceType == LayerSourceType.OsmFeatures)
                material.LayerSourceType = LayerSourceType.None;

            if (matchesRoadRule)
            {
                material.IsRoadMaterial = false;
                material.EnableRoadPainting = false;
            }
        }
    }

    /// <summary>
    ///     Picks the base (index 0) material: among the name matches the one with the lowest
    ///     numeric suffix wins (grass &lt; grass1 &lt; grass2 ...). Returns null when ordering
    ///     is disabled or nothing matches.
    /// </summary>
    private static TerrainMaterialItemExtended? SelectBaseMaterial(
        OsmMaterialAutoAssignConfig config,
        List<TerrainMaterialItemExtended> materials,
        OsmAutoAssignResult result)
    {
        var rule = config.Ordering?.BaseMaterial;
        if (config.Ordering is not { EnableAutoOrdering: true } || rule == null)
            return null;

        var candidates = materials
            .Where(m => rule.MatchesMaterialName(m.InternalName))
            .OrderBy(m => ExtractFirstNumber(m.InternalName))
            .ThenBy(m => m.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            result.Warnings.Add(
                $"No base material found (rule '{rule.Name}') — index 0 left unchanged.");
            return null;
        }

        var baseMaterial = candidates[0];

        // The base material is the fallback everywhere — OSM selections on it are pointless
        // and only show a confusing "Layer Map" chip. Drop selections from earlier runs.
        if (baseMaterial.LayerSourceType == LayerSourceType.OsmFeatures)
        {
            baseMaterial.SelectedOsmFeatures = null;
            baseMaterial.LayerSourceType = LayerSourceType.None;
        }

        return baseMaterial;
    }

    /// <summary>
    ///     First number appearing in a material name ("grass2" → 2, "grass" → 0).
    /// </summary>
    private static int ExtractFirstNumber(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : 0;
    }

    /// <summary>
    ///     Reorders the material list: base material first, then polygon/manual materials, then
    ///     road materials sorted by paint priority (highest last = highest index = paints over
    ///     the others), then materials without any layer source (they cannot claim pixels; the
    ///     generator moves them to the end at generation start anyway — doing it here keeps the
    ///     order stable).
    /// </summary>
    private static void ApplyMaterialOrdering(
        OsmMaterialAutoAssignConfig config,
        List<TerrainMaterialItemExtended> materials,
        TerrainMaterialItemExtended? baseMaterial,
        Dictionary<TerrainMaterialItemExtended, int> roadPaintPriorities,
        OsmAutoAssignResult result)
    {
        if (config.Ordering is not { EnableAutoOrdering: true })
            return;

        var current = materials.OrderBy(m => m.Order).ToList();

        var roads = current
            .Where(roadPaintPriorities.ContainsKey)
            .OrderBy(m => roadPaintPriorities[m])
            .ThenBy(m => m.Order)
            .ToList();

        var middle = current
            .Where(m => m != baseMaterial && !roadPaintPriorities.ContainsKey(m) && m.HasLayerMap)
            .ToList();

        var withoutLayerSource = current
            .Where(m => m != baseMaterial && !roadPaintPriorities.ContainsKey(m) && !m.HasLayerMap)
            .ToList();

        var newOrder = new List<TerrainMaterialItemExtended>();
        if (baseMaterial != null)
            newOrder.Add(baseMaterial);
        newOrder.AddRange(middle);
        newOrder.AddRange(roads);
        newOrder.AddRange(withoutLayerSource);

        var changed = !newOrder.SequenceEqual(current);

        for (var i = 0; i < newOrder.Count; i++)
            newOrder[i].Order = i;

        materials.Clear();
        materials.AddRange(newOrder);

        if (!changed)
            return;

        if (baseMaterial != null)
            result.OrderingChanges.Add(
                $"'{baseMaterial.InternalName}' moved to index 0 (base/fallback material).");
        if (roads.Count > 0)
            result.OrderingChanges.Add(
                "Road materials moved to the end, later entries paint over earlier ones: " +
                string.Join(" → ", roads.Select(r => r.InternalName)));
        if (withoutLayerSource.Count > 0)
            result.OrderingChanges.Add(
                "Materials without a layer source placed last (cannot claim pixels): " +
                string.Join(", ", withoutLayerSource.Select(m => m.InternalName)));
    }

    // ========================================
    // ROAD RULES (highway line features)
    // ========================================

    private static void ApplyRoadRules(
        OsmMaterialAutoAssignConfig config,
        IReadOnlyList<TerrainMaterialItemExtended> materials,
        OsmQueryResult queryResult,
        HashSet<TerrainMaterialItemExtended> claimedMaterials,
        HashSet<long> claimedFeatureIds,
        Dictionary<TerrainMaterialItemExtended, int> roadPaintPriorities,
        OsmAutoAssignResult result)
    {
        foreach (var rule in config.RoadRules)
        {
            var eligible = GetEligibleMaterials(rule, materials, claimedMaterials, result);
            if (eligible.Count == 0)
                continue;

            var assignments = ResolveRoadAssignments(rule, eligible.Count);
            if (assignments.Count == 0)
            {
                result.Warnings.Add($"Rule '{rule.Name}': no assignment configured — skipped.");
                continue;
            }

            for (var i = 0; i < eligible.Count && i < assignments.Count; i++)
            {
                var material = eligible[i];
                var assignment = assignments[i];

                var features = queryResult.Features
                    .Where(f => f.GeometryType == OsmGeometryType.LineString &&
                                f.Category == "highway" &&
                                assignment.HighwayTypes.Contains(f.SubCategory, StringComparer.OrdinalIgnoreCase) &&
                                !claimedFeatureIds.Contains(f.Id))
                    .ToList();

                if (features.Count == 0)
                {
                    result.Warnings.Add(
                        $"Rule '{rule.Name}': no OSM roads of type [{string.Join(", ", assignment.HighwayTypes)}] " +
                        $"in this area — material '{material.InternalName}' left unchanged.");
                    continue;
                }

                AssignRoadMaterial(material, features, assignment, rule, claimedFeatureIds, result);
                claimedMaterials.Add(material);
                roadPaintPriorities[material] = assignment.PaintPriority;
            }

            for (var i = assignments.Count; i < eligible.Count; i++)
            {
                result.Warnings.Add(
                    $"Rule '{rule.Name}': material '{eligible[i].InternalName}' left unassigned " +
                    "(more matching materials than configured assignments).");
            }
        }
    }

    private static List<RoadTypeAssignment> ResolveRoadAssignments(RoadMaterialRule rule, int materialCount)
    {
        if (materialCount == 1)
        {
            if (rule.SingleMaterialAssignment != null)
                return new List<RoadTypeAssignment> { rule.SingleMaterialAssignment };

            // Fall back to the first multi assignment when no dedicated single entry exists.
            return rule.MultiMaterialAssignments.Take(1).ToList();
        }

        if (rule.MultiMaterialAssignments.Count > 0)
            return rule.MultiMaterialAssignments;

        // Multiple materials but only a single assignment configured: first material gets it.
        return rule.SingleMaterialAssignment != null
            ? new List<RoadTypeAssignment> { rule.SingleMaterialAssignment }
            : new List<RoadTypeAssignment>();
    }

    private static void AssignRoadMaterial(
        TerrainMaterialItemExtended material,
        List<OsmFeature> features,
        RoadTypeAssignment assignment,
        RoadMaterialRule rule,
        HashSet<long> claimedFeatureIds,
        OsmAutoAssignResult result)
    {
        material.SelectedOsmFeatures = features.Select(OsmFeatureSelection.FromFeature).ToList();
        material.LayerSourceType = LayerSourceType.OsmFeatures;
        material.IsRoadMaterial = true;
        material.EnableRoadPainting = true;

        string? appliedPreset = null;
        if (Enum.TryParse<RoadPresetType>(assignment.Preset, ignoreCase: true, out var presetType) &&
            presetType != RoadPresetType.Custom)
        {
            var presetParameters = GetPresetParameters(presetType);
            if (presetParameters != null)
            {
                material.SelectedPreset = presetType;
                material.ApplyPreset(presetParameters);
                appliedPreset = presetType.ToString();
            }
        }
        else
        {
            result.Warnings.Add(
                $"Rule '{rule.Name}': unknown road preset '{assignment.Preset}' — " +
                $"material '{material.InternalName}' keeps its current smoothing parameters.");
        }

        foreach (var feature in features)
            claimedFeatureIds.Add(feature.Id);

        result.Assignments.Add(new OsmAutoAssignEntry
        {
            MaterialName = material.InternalName,
            RuleName = rule.Name,
            FeatureCount = features.Count,
            Preset = appliedPreset
        });
    }

    // ========================================
    // POLYGON RULES (landuse/natural area features)
    // ========================================

    private static void ApplyPolygonRules(
        OsmMaterialAutoAssignConfig config,
        IReadOnlyList<TerrainMaterialItemExtended> materials,
        OsmQueryResult queryResult,
        HashSet<TerrainMaterialItemExtended> claimedMaterials,
        HashSet<long> claimedFeatureIds,
        OsmAutoAssignResult result)
    {
        foreach (var rule in config.PolygonRules)
        {
            var eligible = GetEligibleMaterials(rule, materials, claimedMaterials, result);
            if (eligible.Count == 0)
                continue;

            // Road materials keep their road configuration — polygon rules only fill non-road slots.
            eligible = eligible.Where(m => !m.IsRoadMaterial).ToList();
            if (eligible.Count == 0)
                continue;

            var maxMaterials = Math.Max(1, rule.MaxMaterials);
            foreach (var material in eligible.Take(maxMaterials))
            {
                var features = queryResult.Features
                    .Where(f => f.GeometryType == OsmGeometryType.Polygon &&
                                !claimedFeatureIds.Contains(f.Id) &&
                                MatchesAnyOsmType(f, rule.OsmTypes))
                    .ToList();

                if (features.Count == 0)
                {
                    result.Warnings.Add(
                        $"Rule '{rule.Name}': no matching OSM areas in this map — " +
                        $"material '{material.InternalName}' left unchanged.");
                    continue;
                }

                material.SelectedOsmFeatures = features.Select(OsmFeatureSelection.FromFeature).ToList();
                material.LayerSourceType = LayerSourceType.OsmFeatures;

                foreach (var feature in features)
                    claimedFeatureIds.Add(feature.Id);

                claimedMaterials.Add(material);
                result.Assignments.Add(new OsmAutoAssignEntry
                {
                    MaterialName = material.InternalName,
                    RuleName = rule.Name,
                    FeatureCount = features.Count
                });
            }
        }
    }

    private static bool MatchesAnyOsmType(OsmFeature feature, List<OsmTypeReference> osmTypes)
    {
        return osmTypes.Any(t =>
            feature.Category.Equals(t.Category, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(t.SubCategory) ||
             feature.SubCategory.Equals(t.SubCategory, StringComparison.OrdinalIgnoreCase)));
    }

    // ========================================
    // SHARED HELPERS
    // ========================================

    /// <summary>
    ///     Materials matching the rule by name, minus already-claimed materials and materials
    ///     with a manually configured PNG layer map (never clobber those). Sorted by numeric
    ///     name suffix then name (asphalt &lt; asphalt2) — deliberately NOT by list position,
    ///     which the previous run's auto-ordering changed; position-based tiers would swap
    ///     materials on every repeated button press.
    /// </summary>
    private static List<TerrainMaterialItemExtended> GetEligibleMaterials(
        MaterialNameRule rule,
        IReadOnlyList<TerrainMaterialItemExtended> materials,
        HashSet<TerrainMaterialItemExtended> claimedMaterials,
        OsmAutoAssignResult result)
    {
        var matched = materials
            .Where(m => rule.MatchesMaterialName(m.InternalName))
            .Where(m => !claimedMaterials.Contains(m))
            .OrderBy(m => ExtractFirstNumber(m.InternalName))
            .ThenBy(m => m.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pngProtected = matched
            .Where(m => m.LayerSourceType == LayerSourceType.PngFile && !string.IsNullOrEmpty(m.LayerMapPath))
            .ToList();

        foreach (var material in pngProtected)
        {
            result.Warnings.Add(
                $"Rule '{rule.Name}': material '{material.InternalName}' has a manually selected " +
                "PNG layer map and was skipped.");
        }

        return matched.Except(pngProtected).ToList();
    }
}
