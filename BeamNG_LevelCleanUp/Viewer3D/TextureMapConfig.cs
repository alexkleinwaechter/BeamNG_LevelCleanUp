namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Central configuration for controlling which texture map types are enabled for 3D rendering.
///     Use this to debug texture rendering issues by selectively enabling/disabling map types.
/// </summary>
public static class TextureMapConfig
{
    /// <summary>
    ///     Enable/disable base color (albedo/diffuse) texture loading.
    ///     This is the primary color texture that defines the surface appearance.
    /// </summary>
    public static bool EnableBaseColorMap { get; set; } = true;

    /// <summary>
    ///     Enable/disable opacity/alpha texture loading.
    ///     When enabled, opacity maps are merged into the alpha channel of the base color texture.
    /// </summary>
    public static bool EnableOpacityMap { get; set; } = true;

    /// <summary>
    ///     Enable/disable normal map texture loading.
    ///     Normal maps add surface detail without additional geometry.
    /// </summary>
    public static bool EnableNormalMap { get; set; } = true;

    /// <summary>
    ///     Enable/disable roughness texture loading.
    ///     Roughness controls how rough/smooth a surface appears (PBR only).
    ///     Note: Currently uses scalar factor instead of map due to Helix channel format issues.
    /// </summary>
    public static bool EnableRoughnessMap { get; set; }

    /// <summary>
    ///     Enable/disable metallic texture loading.
    ///     Metallic controls how metallic a surface appears (PBR only).
    ///     Note: Currently uses scalar factor instead of map due to Helix channel format issues.
    /// </summary>
    public static bool EnableMetallicMap { get; set; }

    /// <summary>
    ///     Enable/disable ambient occlusion texture loading.
    ///     AO adds subtle shadowing in crevices and corners (PBR only).
    /// </summary>
    public static bool EnableAmbientOcclusionMap { get; set; } = true;

    /// <summary>
    ///     Enable/disable emissive/glow texture loading.
    ///     Emissive maps make surfaces appear to emit light.
    /// </summary>
    public static bool EnableEmissiveMap { get; set; } = true;

    /// <summary>
    ///     Enable/disable specular texture loading.
    ///     Specular maps control highlight intensity (Phong only).
    /// </summary>
    public static bool EnableSpecularMap { get; set; } = true;

    /// <summary>
    ///     When true, logs detailed texture loading information to Debug output.
    /// </summary>
    public static bool EnableDebugLogging { get; set; }

    // ============== LIGHTING CONFIGURATION ==============

    /// <summary>
    ///     Ambient light intensity (0.0 - 1.0). Default: 0.11
    ///     Higher values brighten the entire scene uniformly.
    /// </summary>
    public static float AmbientLightIntensity { get; set; } = 0.11f;

    /// <summary>
    ///     Directional light intensity (0.0 - 2.0). Default: 0.65
    ///     Controls the main light source brightness.
    /// </summary>
    public static float DirectionalLightIntensity { get; set; } = 0.65f;

    /// <summary>
    ///     Material ambient color multiplier (0.0 - 1.0). Default: 0.0
    ///     Lower values reduce the "washed out" appearance.
    ///     Note: Only applied when model is loaded/reloaded.
    /// </summary>
    public static float MaterialAmbientIntensity { get; set; } = 0.0f;

    /// <summary>
    ///     Material diffuse color multiplier (0.0 - 1.0). Default: 1.0
    ///     Controls how much the texture color shows through.
    ///     Note: Only applied when model is loaded/reloaded.
    /// </summary>
    public static float MaterialDiffuseIntensity { get; set; } = 1.0f;

    /// <summary>
    ///     Resets all texture map settings to their default values.
    /// </summary>
    public static void ResetToDefaults()
    {
        EnableBaseColorMap = true;
        EnableOpacityMap = true;
        EnableNormalMap = true;
        EnableRoughnessMap = false; // Disabled by default due to Helix channel format issues
        EnableMetallicMap = false; // Disabled by default due to Helix channel format issues
        EnableAmbientOcclusionMap = true;
        EnableEmissiveMap = true;
        EnableSpecularMap = true;
        EnableDebugLogging = false;

        // Reset lighting
        AmbientLightIntensity = 0.11f;
        DirectionalLightIntensity = 0.65f;
        MaterialAmbientIntensity = 0.0f;
        MaterialDiffuseIntensity = 1.0f;
    }

    /// <summary>
    ///     Enables only base color texture (for debugging).
    ///     Useful to verify if the basic texture pipeline is working.
    /// </summary>
    public static void EnableCustomMaps()
    {
        EnableBaseColorMap = true;
        EnableOpacityMap = false;
        EnableNormalMap = false;
        EnableRoughnessMap = false;
        EnableMetallicMap = false;
        EnableAmbientOcclusionMap = false;
        EnableEmissiveMap = false;
        EnableSpecularMap = false;
    }

    /// <summary>
    ///     Enables all texture map types (for full quality rendering).
    ///     Note: Roughness and Metallic maps may cause issues with Helix's channel format.
    /// </summary>
    public static void EnableAll()
    {
        EnableBaseColorMap = true;
        EnableOpacityMap = true;
        EnableNormalMap = true;
        EnableRoughnessMap = true;
        EnableMetallicMap = true;
        EnableAmbientOcclusionMap = true;
        EnableEmissiveMap = true;
        EnableSpecularMap = true;
    }

    /// <summary>
    ///     Disables all texture map types (renders with solid colors only).
    ///     Useful to verify if geometry and lighting work without textures.
    /// </summary>
    public static void DisableAll()
    {
        EnableBaseColorMap = false;
        EnableOpacityMap = false;
        EnableNormalMap = false;
        EnableRoughnessMap = false;
        EnableMetallicMap = false;
        EnableAmbientOcclusionMap = false;
        EnableEmissiveMap = false;
        EnableSpecularMap = false;
    }

    /// <summary>
    ///     Sets lighting to low intensity (darker, less washed out).
    /// </summary>
    public static void SetLowLighting()
    {
        AmbientLightIntensity = 0.05f;
        DirectionalLightIntensity = 0.4f;
        MaterialAmbientIntensity = 0.0f;
        MaterialDiffuseIntensity = 1.0f;
    }

    /// <summary>
    ///     Sets lighting to medium intensity (balanced - default).
    /// </summary>
    public static void SetMediumLighting()
    {
        AmbientLightIntensity = 0.11f;
        DirectionalLightIntensity = 0.65f;
        MaterialAmbientIntensity = 0.0f;
        MaterialDiffuseIntensity = 1.0f;
    }

    /// <summary>
    ///     Sets lighting to high intensity (brighter).
    /// </summary>
    public static void SetHighLighting()
    {
        AmbientLightIntensity = 0.25f;
        DirectionalLightIntensity = 1.0f;
        MaterialAmbientIntensity = 0.1f;
        MaterialDiffuseIntensity = 1.0f;
    }

    /// <summary>
    ///     Returns a summary of the current texture map configuration.
    /// </summary>
    public static string GetConfigurationSummary()
    {
        return $"""
                Texture Map Configuration:
                - BaseColor: {EnableBaseColorMap}
                - Opacity: {EnableOpacityMap}
                - Normal: {EnableNormalMap}
                - Roughness: {EnableRoughnessMap}
                - Metallic: {EnableMetallicMap}
                - AmbientOcclusion: {EnableAmbientOcclusionMap}
                - Emissive: {EnableEmissiveMap}
                - Specular: {EnableSpecularMap}
                - DebugLogging: {EnableDebugLogging}
                
                Lighting Configuration:
                - AmbientLightIntensity: {AmbientLightIntensity:F2}
                - DirectionalLightIntensity: {DirectionalLightIntensity:F2}
                - MaterialAmbientIntensity: {MaterialAmbientIntensity:F2}
                - MaterialDiffuseIntensity: {MaterialDiffuseIntensity:F2}
                """;
    }
}