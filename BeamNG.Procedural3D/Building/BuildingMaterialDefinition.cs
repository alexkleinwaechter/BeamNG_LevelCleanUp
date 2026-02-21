namespace BeamNG.Procedural3D.Building;

using System.Drawing;
using System.Numerics;

/// <summary>
/// Defines a building material with texture paths and UV scaling.
/// Maps an internal material key (e.g., "BRICK") to actual texture file names
/// and BeamNG material properties.
/// </summary>
public class BuildingMaterialDefinition
{
    /// <summary>
    /// Internal material key (e.g., "BUILDING_DEFAULT", "BRICK", "ROOF_DEFAULT").
    /// Used by BuildingData.WallMaterial / RoofMaterial to reference this definition.
    /// </summary>
    public required string MaterialKey { get; init; }

    /// <summary>
    /// Display name used in the BeamNG material system (e.g., "building_brick", "building_plaster").
    /// This becomes the material name in materials.json and DAE files.
    /// </summary>
    public required string MaterialName { get; init; }

    /// <summary>
    /// Name of the texture folder in the OSM2World-default-style/textures/cc0textures/ directory
    /// (e.g., "Bricks029"). Used to locate textures from the OSM2World asset library.
    /// When null, falls back to direct file name lookup.
    /// </summary>
    public string? TextureFolder { get; init; }

    /// <summary>
    /// Filename of the color/albedo texture (e.g., "Bricks029_Color.jpg").
    /// Relative to the texture source/deployment directory.
    /// </summary>
    public required string ColorMapFile { get; init; }

    /// <summary>
    /// Filename of the normal map texture (e.g., "Bricks029_Normal.jpg").
    /// </summary>
    public string? NormalMapFile { get; init; }

    /// <summary>
    /// Filename of the ORM (Occlusion/Roughness/Metallic) packed texture (e.g., "Bricks029_ORM.data.png").
    /// Used as source for automatic channel splitting into AO/Roughness/Metallic.
    /// </summary>
    public string? OrmMapFile { get; init; }

    /// <summary>
    /// Optional explicit ambient occlusion texture filename (e.g., "Bricks029_AO.data.png").
    /// When null, auto-derived from OrmMapFile via <see cref="OrmTextureHelper"/>.
    /// Can be set explicitly in osm2world-style.json to use a custom texture.
    /// </summary>
    public string? AoMapFile { get; init; }

    /// <summary>
    /// Optional explicit roughness texture filename (e.g., "Bricks029_Roughness.data.png").
    /// When null, auto-derived from OrmMapFile via <see cref="OrmTextureHelper"/>.
    /// Can be set explicitly in osm2world-style.json to use a custom texture.
    /// </summary>
    public string? RoughnessMapFile { get; init; }

    /// <summary>
    /// Optional explicit metallic texture filename (e.g., "Bricks029_Metallic.data.png").
    /// When null, auto-derived from OrmMapFile via <see cref="OrmTextureHelper"/>.
    /// Can be set explicitly in osm2world-style.json to use a custom texture.
    /// </summary>
    public string? MetallicMapFile { get; init; }

    /// <summary>
    /// Texture scale in U direction (meters per texture repeat on walls).
    /// For walls: U = distance along wall. For roofs: U = world X.
    /// </summary>
    public float TextureScaleU { get; init; } = 3.0f;

    /// <summary>
    /// Texture scale in V direction (meters per texture repeat).
    /// For walls: V = height. For roofs: V = world Y.
    /// </summary>
    public float TextureScaleV { get; init; } = 3.0f;

    /// <summary>
    /// Whether this is a roof material (affects UV mapping approach).
    /// </summary>
    public bool IsRoofMaterial { get; init; }

    /// <summary>
    /// Default RGB color for this material (0-255 per channel).
    /// Used for placeholder texture generation and as diffuse color fallback.
    /// </summary>
    public Vector3 DefaultColor { get; init; } = new(180, 180, 180);

    /// <summary>
    /// Material opacity (0 = fully transparent, 1 = fully opaque).
    /// Used for glass materials. Port of OSM2World's Transparency enum.
    /// </summary>
    public float Opacity { get; init; } = 1.0f;

    /// <summary>
    /// Whether the material should be rendered double-sided.
    /// Port of OSM2World's Material.doubleSided property — used for glass
    /// so both sides are visible.
    /// </summary>
    public bool DoubleSided { get; init; }

    /// <summary>
    /// Whether this material supports per-instance color tinting via BeamNG's instanceDiffuse system.
    /// When true, the material's Stage[0] will include "instanceDiffuse": true, and each TSStatic
    /// can specify its own "instanceColor": [R, G, B, 1] to tint the texture.
    /// Used for non-clustered building export where each building has its own TSStatic.
    /// </summary>
    public bool InstanceDiffuse { get; init; }

    /// <summary>
    /// Gets the effective AO filename: explicit AoMapFile if set, otherwise derived from OrmMapFile.
    /// Returns null if neither is available.
    /// </summary>
    public string? EffectiveAoMapFile =>
        AoMapFile ?? (OrmMapFile != null ? OrmTextureHelper.DeriveAoFileName(OrmMapFile) : null);

    /// <summary>
    /// Gets the effective roughness filename: explicit RoughnessMapFile if set, otherwise derived from OrmMapFile.
    /// Returns null if neither is available.
    /// </summary>
    public string? EffectiveRoughnessMapFile =>
        RoughnessMapFile ?? (OrmMapFile != null ? OrmTextureHelper.DeriveRoughnessFileName(OrmMapFile) : null);

    /// <summary>
    /// Gets the effective metallic filename: explicit MetallicMapFile if set, otherwise derived from OrmMapFile.
    /// Returns null if neither is available.
    /// </summary>
    public string? EffectiveMetallicMapFile =>
        MetallicMapFile ?? (OrmMapFile != null ? OrmTextureHelper.DeriveMetallicFileName(OrmMapFile) : null);

    /// <summary>
    /// Creates a Procedural3D Material for use with ColladaExporter.
    /// Texture paths are set relative to the DAE file location.
    /// </summary>
    /// <param name="textureRelativePath">Relative path from DAE to textures folder (e.g., "textures/").</param>
    public Core.Material ToExportMaterial(string textureRelativePath = "textures/")
    {
        return ToExportMaterial(null, textureRelativePath);
    }

    /// <summary>
    /// Creates a Procedural3D Material for use with ColladaExporter,
    /// with optional filename resolution for PoT dimension renames.
    /// </summary>
    /// <param name="resolveFileName">Optional function to resolve deployed texture filenames.
    /// When null, original filenames are used.</param>
    /// <param name="textureRelativePath">Relative path from DAE to textures folder (e.g., "textures/").</param>
    public Core.Material ToExportMaterial(Func<string, string>? resolveFileName, string textureRelativePath = "textures/")
    {
        var resolve = resolveFileName ?? (f => f);
        var path = textureRelativePath.TrimEnd('/') + "/";
        return new Core.Material
        {
            Name = MaterialName,
            DiffuseColor = new Vector3(DefaultColor.X / 255f, DefaultColor.Y / 255f, DefaultColor.Z / 255f),
            Opacity = Opacity,
            DiffuseTexturePath = path + resolve(ColorMapFile),
            NormalTexturePath = NormalMapFile != null ? path + resolve(NormalMapFile) : null,
            SpecularTexturePath = OrmMapFile != null ? path + resolve(OrmMapFile) : null
        };
    }

    /// <summary>
    /// Gets all texture filenames referenced by this material definition.
    /// </summary>
    public IEnumerable<string> GetTextureFiles()
    {
        yield return ColorMapFile;
        if (NormalMapFile != null) yield return NormalMapFile;

        // Yield the effective PBR channel filenames.
        // Uses explicit overrides (AoMapFile, RoughnessMapFile, MetallicMapFile) if set,
        // otherwise auto-derives from OrmMapFile.
        if (EffectiveAoMapFile != null) yield return EffectiveAoMapFile;
        if (EffectiveRoughnessMapFile != null) yield return EffectiveRoughnessMapFile;
        if (EffectiveMetallicMapFile != null) yield return EffectiveMetallicMapFile;
    }

    /// <summary>
    /// Creates a color variant of this material for clustered building export.
    /// The variant shares all texture files but has a unique MaterialKey/MaterialName
    /// and bakes the color into DefaultColor (used as baseColorFactor in materials.json).
    /// InstanceDiffuse is false because the color is baked into the material.
    /// </summary>
    /// <param name="color">The building wall color from OSM building:colour tag.</param>
    /// <param name="hexSuffix">Lowercase hex color suffix (e.g., "aabbcc").</param>
    public BuildingMaterialDefinition CreateColorVariant(Color color, string hexSuffix)
    {
        return new BuildingMaterialDefinition
        {
            MaterialKey = $"{MaterialKey}_{hexSuffix}",
            MaterialName = $"{MaterialName}_{hexSuffix}",
            TextureFolder = TextureFolder,
            ColorMapFile = ColorMapFile,
            NormalMapFile = NormalMapFile,
            OrmMapFile = OrmMapFile,
            AoMapFile = AoMapFile,
            RoughnessMapFile = RoughnessMapFile,
            MetallicMapFile = MetallicMapFile,
            TextureScaleU = TextureScaleU,
            TextureScaleV = TextureScaleV,
            IsRoofMaterial = IsRoofMaterial,
            DefaultColor = new Vector3(color.R, color.G, color.B),
            Opacity = Opacity,
            DoubleSided = DoubleSided,
            InstanceDiffuse = false
        };
    }
}
