using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Container for texture lookup information used by the 3D viewer.
/// Provides fast lookups from material names/IDs to texture file paths.
/// </summary>
public class TextureLookup
{
    /// <summary>
    /// Maps various material identifiers to texture file paths.
    /// Keys can be: material name, internal name, mapTo value, DAE material ID
    /// </summary>
    public Dictionary<string, string> ColorMaps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps material identifiers to all their texture files (keyed by MapType).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> AllTextures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All MaterialJson objects for advanced lookup.
    /// </summary>
    public List<MaterialJson> Materials { get; set; } = [];

    /// <summary>
    /// DAE material mappings (MaterialId -> MaterialName).
    /// </summary>
    public List<MaterialsDae> MaterialsDae { get; set; } = [];

    /// <summary>
    /// Map type names that represent color/albedo maps.
    /// </summary>
    private static readonly HashSet<string> ColorMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "colormap", "basecolormap", "diffusemap", "basecolortex", 
        "basecolordetailtex", "basecolordetailmap"
    };

    /// <summary>
    /// Checks if a map type represents a color/albedo map.
    /// </summary>
    public static bool IsColorMap(string? mapType)
    {
        if (string.IsNullOrEmpty(mapType)) return false;

        var lower = mapType.ToLowerInvariant();
        if (ColorMapTypes.Contains(lower))
            return true;

        return lower.Contains("color") && !lower.Contains("palette");
    }

    /// <summary>
    /// Builds a TextureLookup from resolved MaterialJson objects.
    /// </summary>
    public static TextureLookup Build(List<MaterialJson>? materials, List<MaterialsDae>? materialsDae)
    {
        var lookup = new TextureLookup
        {
            Materials = materials ?? [],
            MaterialsDae = materialsDae ?? []
        };

        if (materials == null || materials.Count == 0)
            return lookup;

        foreach (var material in materials)
        {
            if (material.MaterialFiles == null || material.MaterialFiles.Count == 0)
                continue;

            // Build texture dictionary for this material
            var textureDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? colorMapPath = null;

            foreach (var file in material.MaterialFiles)
            {
                if (file.File == null)
                    continue;

                var fullPath = file.File.FullName;
                var canResolve = file.File.Exists || LinkFileResolver.CanResolve(fullPath);

                if (!canResolve) continue;

                textureDict[file.MapType ?? "Unknown"] = fullPath;

                // Track color map specifically
                if (IsColorMap(file.MapType))
                {
                    colorMapPath = fullPath;
                }
            }

            if (textureDict.Count == 0)
                continue;

            // Create all possible lookup keys for this material
            var lookupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(material.Name))
                lookupKeys.Add(material.Name);

            if (!string.IsNullOrEmpty(material.InternalName))
                lookupKeys.Add(material.InternalName);

            // MapTo is critical - it maps to the DAE's <material name="..."> attribute
            if (!string.IsNullOrEmpty(material.MapTo))
                lookupKeys.Add(material.MapTo);

            // Also check MaterialsDae for additional mappings
            if (materialsDae != null)
            {
                foreach (var daeMat in materialsDae)
                {
                    // If DAE material name matches our material, add DAE material ID as a key
                    if (!string.IsNullOrEmpty(daeMat.MaterialName) &&
                        (daeMat.MaterialName.Equals(material.Name, StringComparison.OrdinalIgnoreCase) ||
                         daeMat.MaterialName.Equals(material.InternalName, StringComparison.OrdinalIgnoreCase) ||
                         daeMat.MaterialName.Equals(material.MapTo, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!string.IsNullOrEmpty(daeMat.MaterialId))
                            lookupKeys.Add(daeMat.MaterialId);
                        if (!string.IsNullOrEmpty(daeMat.MaterialName))
                            lookupKeys.Add(daeMat.MaterialName);
                    }
                }
            }

            // Add to lookups using all keys
            foreach (var key in lookupKeys)
            {
                if (!lookup.AllTextures.ContainsKey(key))
                    lookup.AllTextures[key] = textureDict;

                if (colorMapPath != null && !lookup.ColorMaps.ContainsKey(key))
                    lookup.ColorMaps[key] = colorMapPath;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Finds a MaterialJson by key (name, internal name, or mapTo).
    /// </summary>
    public MaterialJson? FindMaterialByKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        return Materials.FirstOrDefault(m =>
            key.Equals(m.Name, StringComparison.OrdinalIgnoreCase) ||
            key.Equals(m.InternalName, StringComparison.OrdinalIgnoreCase) ||
            key.Equals(m.MapTo, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds a MaterialJson by DAE node name using MaterialsDae mappings.
    /// Handles various naming patterns from DAE files:
    /// - Exact match: nodeName == MaterialId or MaterialName
    /// - Partial match: nodeName is contained in MaterialId (e.g., "eca_bld_decking" in "eca_bld_decking-material")
    /// - Partial match: MaterialId is contained in nodeName
    /// </summary>
    public MaterialJson? FindMaterialByDaeNodeName(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName) || MaterialsDae.Count == 0)
            return null;

        foreach (var daeMat in MaterialsDae)
        {
            // Check for exact MaterialName match first (most reliable)
            if (!string.IsNullOrEmpty(daeMat.MaterialName) &&
                nodeName.Equals(daeMat.MaterialName, StringComparison.OrdinalIgnoreCase))
            {
                var material = FindMaterialByKey(daeMat.MaterialName);
                if (material != null)
                    return material;
            }

            // Check if node name matches or is contained in MaterialId
            if (!string.IsNullOrEmpty(daeMat.MaterialId))
            {
                // nodeName might be the full MaterialId
                if (nodeName.Equals(daeMat.MaterialId, StringComparison.OrdinalIgnoreCase))
                {
                    var material = FindMaterialByKey(daeMat.MaterialName);
                    if (material != null)
                        return material;
                }

                // nodeName might be contained in MaterialId (e.g., "eca_bld_decking" in "eca_bld_decking-material")
                if (daeMat.MaterialId.Contains(nodeName, StringComparison.OrdinalIgnoreCase))
                {
                    var material = FindMaterialByKey(daeMat.MaterialName);
                    if (material != null)
                        return material;
                }

                // MaterialId might be contained in nodeName
                if (nodeName.Contains(daeMat.MaterialId, StringComparison.OrdinalIgnoreCase))
                {
                    var material = FindMaterialByKey(daeMat.MaterialName);
                    if (material != null)
                        return material;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a MaterialJson using fuzzy matching against node name.
    /// </summary>
    public MaterialJson? FindMaterialByFuzzyMatch(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName))
            return null;

        return Materials.FirstOrDefault(m =>
            (!string.IsNullOrEmpty(m.Name) && 
             (nodeName.Contains(m.Name, StringComparison.OrdinalIgnoreCase) ||
              m.Name.Contains(nodeName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(m.InternalName) && 
             (nodeName.Contains(m.InternalName, StringComparison.OrdinalIgnoreCase) ||
              m.InternalName.Contains(nodeName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(m.MapTo) && 
             (nodeName.Contains(m.MapTo, StringComparison.OrdinalIgnoreCase) ||
              m.MapTo.Contains(nodeName, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Gets the first available color map texture path.
    /// </summary>
    public string? GetFirstAvailableColorMap()
    {
        return ColorMaps.Values.FirstOrDefault(p =>
            File.Exists(p) || LinkFileResolver.CanResolve(p));
    }

    /// <summary>
    /// Finds a color map texture path for a given material name.
    /// </summary>
    public string? FindColorMapForMaterial(string? materialName)
    {
        if (string.IsNullOrEmpty(materialName))
            return null;

        // Direct lookup
        if (ColorMaps.TryGetValue(materialName, out var path))
        {
            if (File.Exists(path) || LinkFileResolver.CanResolve(path))
                return path;
        }

        // Try matching against MaterialJson properties
        foreach (var mat in Materials)
        {
            if (mat.MaterialFiles == null) continue;

            var isMatch =
                materialName.Equals(mat.Name, StringComparison.OrdinalIgnoreCase) ||
                materialName.Equals(mat.InternalName, StringComparison.OrdinalIgnoreCase) ||
                materialName.Equals(mat.MapTo, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                // Find color map in this material's files
                var colorMap = mat.MaterialFiles.FirstOrDefault(f =>
                    IsColorMap(f.MapType) &&
                    f.File != null &&
                    (f.File.Exists || LinkFileResolver.CanResolve(f.File.FullName)));

                if (colorMap != null)
                    return colorMap.File!.FullName;

                // Fallback to any available texture
                var anyTexture = mat.MaterialFiles.FirstOrDefault(f =>
                    f.File != null &&
                    (f.File.Exists || LinkFileResolver.CanResolve(f.File.FullName)));

                if (anyTexture != null)
                    return anyTexture.File!.FullName;
            }
        }

        return null;
    }
}
