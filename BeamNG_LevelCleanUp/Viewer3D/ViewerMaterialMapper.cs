using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Maps BeamNG material properties to texture lookup keys.
/// </summary>
public static class ViewerMaterialMapper
{
    /// <summary>
    /// BeamNG material property names that contain color/diffuse textures.
    /// </summary>
    private static readonly HashSet<string> ColorMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ColorMap",
        "BaseColorMap",
        "DiffuseMap",
        "BaseColorTex",
        "BaseColorDetailTex"
    };

    /// <summary>
    /// BeamNG material property names that contain normal textures.
    /// </summary>
    private static readonly HashSet<string> NormalMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NormalMap",
        "NormalDetailTex",
        "BumpMap"
    };

    /// <summary>
    /// BeamNG material property names that contain specular textures.
    /// </summary>
    private static readonly HashSet<string> SpecularMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SpecularMap",
        "SpecularTex"
    };

    /// <summary>
    /// BeamNG material property names that contain roughness textures.
    /// </summary>
    private static readonly HashSet<string> RoughnessMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoughnessMap",
        "RoughnessDetailTex",
        "RoughnessTex"
    };

    /// <summary>
    /// BeamNG material property names that contain metallic textures.
    /// </summary>
    private static readonly HashSet<string> MetallicMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MetallicMap",
        "MetallicTex"
    };

    /// <summary>
    /// BeamNG material property names that contain AO textures.
    /// </summary>
    private static readonly HashSet<string> AoMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AoMap",
        "AoDetailTex",
        "AmbientOcclusionMap"
    };

    /// <summary>
    /// Determines if a map type is a color/diffuse map.
    /// </summary>
    public static bool IsColorMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && ColorMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Determines if a map type is a normal map.
    /// </summary>
    public static bool IsNormalMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && NormalMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Determines if a map type is a specular map.
    /// </summary>
    public static bool IsSpecularMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && SpecularMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Determines if a map type is a roughness map.
    /// </summary>
    public static bool IsRoughnessMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && RoughnessMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Determines if a map type is a metallic map.
    /// </summary>
    public static bool IsMetallicMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && MetallicMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Determines if a map type is an AO map.
    /// </summary>
    public static bool IsAoMap(string? mapType)
    {
        return !string.IsNullOrEmpty(mapType) && AoMapTypes.Contains(mapType);
    }

    /// <summary>
    /// Gets the primary color map path from a list of materials.
    /// </summary>
    public static string? GetPrimaryColorMap(List<MaterialJson> materials)
    {
        foreach (var material in materials)
        {
            foreach (var file in material.MaterialFiles)
            {
                if (file.File?.Exists == true && IsColorMap(file.MapType))
                {
                    return file.File.FullName;
                }
            }
        }

        // Fallback to first available texture
        return materials
            .SelectMany(m => m.MaterialFiles)
            .FirstOrDefault(f => f.File?.Exists == true)
            ?.File?.FullName;
    }

    /// <summary>
    /// Gets the normal map path from a list of materials.
    /// </summary>
    public static string? GetNormalMap(List<MaterialJson> materials)
    {
        foreach (var material in materials)
        {
            foreach (var file in material.MaterialFiles)
            {
                if (file.File?.Exists == true && IsNormalMap(file.MapType))
                {
                    return file.File.FullName;
                }
            }
        }

        return null;
    }
}
