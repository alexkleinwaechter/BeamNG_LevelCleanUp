using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Resolves which DecalRoadLayerSet applies to a given spline using a 3-level cascade:
/// 1. OSM type override (project preset)
/// 2. Material name fallback (project preset)
/// 3. AppData defaults (per OSM type)
/// Returns null if no match at any level.
/// </summary>
public static class DecalRoadLayerSetResolver
{
    public static DecalRoadLayerSet? Resolve(
        string? osmRoadType,
        string materialName,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults)
    {
        // 1. OSM type override in project preset
        if (osmRoadType != null &&
            settings.OsmLayerSets.TryGetValue(osmRoadType, out var osmOverride))
            return osmOverride;

        // 2. Material name fallback in project preset
        if (settings.MaterialLayerSets.TryGetValue(materialName, out var materialFallback))
            return materialFallback;

        // 3. AppData defaults by OSM type
        if (osmRoadType != null &&
            appDataDefaults.TryGetValue(osmRoadType, out var appDefault))
            return appDefault;

        // No match
        return null;
    }
}
