using System.Text.Json.Nodes;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
///     Three-way JSON merge that upgrades a user's decalroad-defaults file when the
///     hardcoded code defaults (<see cref="DecalRoadDefaultLayerSets" />) change.
///     <para>
///         Inputs: <c>user</c> (the AppData file), <c>baseline</c> (snapshot of the code
///         defaults as of the last merge, may be null on first migration) and
///         <c>current</c> (today's code defaults). The user tree is mutated in place:
///     </para>
///     <list type="bullet">
///         <item>Road-type keys missing from the user file are added.</item>
///         <item>Layers missing from a user set are added, unless the baseline shows the
///         user deleted them deliberately.</item>
///         <item>Fields missing from a user object are added.</item>
///         <item>Fields whose user value still equals the baseline value (user never
///         overwrote them) are updated to the new code default.</item>
///         <item>Everything the user changed, added or removed is left untouched.</item>
///     </list>
/// </summary>
public static class DecalRoadDefaultsMerger
{
    private const string LayersProperty = "layers";
    private const string NameProperty = "name";

    /// <summary>
    ///     Merges the current code defaults into the user's layer-set dictionary.
    /// </summary>
    /// <param name="user">Parsed user file; mutated in place.</param>
    /// <param name="baseline">Code defaults as of the last merge, or null if unknown.</param>
    /// <param name="current">Current code defaults.</param>
    /// <returns>True if the user tree was modified and should be re-saved.</returns>
    public static bool Merge(JsonObject user, JsonObject? baseline, JsonObject current)
    {
        var changed = false;
        foreach (var (roadType, currentSetNode) in current)
        {
            if (currentSetNode is not JsonObject currentSet) continue;

            if (user[roadType] is not JsonObject userSet)
            {
                // New road type in code defaults (or non-object garbage) → take it wholesale
                user[roadType] = currentSet.DeepClone();
                changed = true;
                continue;
            }

            var baselineSet = baseline?[roadType] as JsonObject;
            changed |= MergeLayerSet(userSet, baselineSet, currentSet);
        }

        // Keys only present in the user file (unknown/legacy road types) are kept as-is.
        return changed;
    }

    private static bool MergeLayerSet(JsonObject user, JsonObject? baseline, JsonObject current)
    {
        var changed = false;
        foreach (var (property, currentValue) in current)
        {
            if (property == LayersProperty)
                changed |= MergeLayers(user, baseline, currentValue as JsonArray);
            else
                changed |= MergeProperty(user, baseline, property, currentValue);
        }

        return changed;
    }

    private static bool MergeLayers(JsonObject userSet, JsonObject? baselineSet, JsonArray? currentLayers)
    {
        if (currentLayers is null) return false;

        if (userSet[LayersProperty] is not JsonArray userLayers)
        {
            userSet[LayersProperty] = currentLayers.DeepClone();
            return true;
        }

        var baselineLayers = baselineSet?[LayersProperty] as JsonArray;
        var changed = false;

        for (var i = 0; i < currentLayers.Count; i++)
        {
            if (currentLayers[i] is not JsonObject currentLayer) continue;
            var layerName = currentLayer[NameProperty]?.GetValue<string>();
            if (layerName is null) continue;

            var userLayer = FindLayerByName(userLayers, layerName);
            var baselineLayer = FindLayerByName(baselineLayers, layerName);

            if (userLayer is not null)
            {
                foreach (var (property, currentValue) in currentLayer)
                    changed |= MergeProperty(userLayer, baselineLayer, property, currentValue);
            }
            else if (baselineLayer is null)
            {
                // Layer is new in the code defaults → insert at its default position.
                // If the baseline knows it, the user deleted it deliberately → leave it out.
                userLayers.Insert(Math.Min(i, userLayers.Count), currentLayer.DeepClone());
                changed = true;
            }
        }

        // Layers only present in the user file (custom/duplicated layers) are kept.
        return changed;
    }

    /// <summary>
    ///     Merges one scalar-ish property (numbers, strings, bools, plain value arrays like
    ///     distanceFade). Missing → add; equal to baseline → follow the new code default;
    ///     otherwise the user overwrote it → keep.
    /// </summary>
    private static bool MergeProperty(JsonObject user, JsonObject? baseline, string property, JsonNode? currentValue)
    {
        if (!user.ContainsKey(property))
        {
            user[property] = currentValue?.DeepClone();
            return true;
        }

        var userValue = user[property];
        if (JsonNode.DeepEquals(userValue, currentValue)) return false;

        if (baseline is not null
            && baseline.TryGetPropertyValue(property, out var baselineValue)
            && JsonNode.DeepEquals(userValue, baselineValue))
        {
            user[property] = currentValue?.DeepClone();
            return true;
        }

        return false;
    }

    private static JsonObject? FindLayerByName(JsonArray? layers, string name)
    {
        if (layers is null) return null;
        foreach (var node in layers)
        {
            if (node is JsonObject layer
                && layer[NameProperty]?.GetValue<string>() is { } layerName
                && string.Equals(layerName, name, StringComparison.Ordinal))
            {
                return layer;
            }
        }

        return null;
    }
}
