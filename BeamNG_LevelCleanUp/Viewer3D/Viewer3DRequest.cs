using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Display mode for the 3D viewer.
/// </summary>
public enum Viewer3DMode
{
    /// <summary>Load a .dae file with materials applied.</summary>
    DaeModel,

    /// <summary>Show material textures on a plane (for general materials, terrain). Uses square plane.</summary>
    MaterialOnPlane,

    /// <summary>Show road material on a rectangular plane (4:1 aspect ratio).</summary>
    RoadOnPlane,

    /// <summary>Show decal material on a square plane.</summary>
    DecalOnPlane,

    /// <summary>Show a single texture on a plane.</summary>
    TextureOnly
}

/// <summary>
/// Request to open the 3D viewer. Generic - not tied to CopyAssetType.
/// Can be created from CopyAsset, MaterialJson, or direct file paths.
/// </summary>
public class Viewer3DRequest
{
    /// <summary>Display mode.</summary>
    public Viewer3DMode Mode { get; set; }

    /// <summary>Path to DAE file (for DaeModel mode). Also used for single texture path in TextureOnly mode.</summary>
    public string? DaeFilePath { get; set; }

    /// <summary>Materials to display. Already resolved via MaterialScanner.</summary>
    public List<MaterialJson> Materials { get; set; } = new();

    /// <summary>
    /// DAE material mappings from DaeScanner. Maps DAE material IDs to BeamNG material names.
    /// Used to correctly match mesh materials to texture files.
    /// </summary>
    public List<MaterialsDae>? MaterialsDae { get; set; }

    /// <summary>Display name for the viewer title bar.</summary>
    public string DisplayName { get; set; } = "3D Asset Viewer";

    /// <summary>
    /// Optional: source level path for PathResolver.
    /// If null, uses PathResolver.LevelPathCopyFrom or PathResolver.LevelPath.
    /// </summary>
    public string? LevelPath { get; set; }

    /// <summary>
    /// Creates a request from a CopyAsset (for CopyAssets page compatibility).
    /// </summary>
    public static Viewer3DRequest FromCopyAsset(CopyAsset asset)
    {
        var mode = asset.CopyAssetType switch
        {
            CopyAssetType.Dae => Viewer3DMode.DaeModel,
            CopyAssetType.Road => Viewer3DMode.RoadOnPlane,
            CopyAssetType.Decal => Viewer3DMode.DecalOnPlane,
            CopyAssetType.Terrain => Viewer3DMode.MaterialOnPlane,
            CopyAssetType.ForestBrush => Viewer3DMode.DaeModel, // Forest brushes reference DAE shapes
            _ => Viewer3DMode.MaterialOnPlane
        };

        return new Viewer3DRequest
        {
            Mode = mode,
            DaeFilePath = asset.DaeFilePath,
            // Ensure Materials is never null - use empty list if asset.Materials is null
            Materials = asset.Materials ?? new List<MaterialJson>(),
            // Pass DAE material mappings for proper texture matching
            MaterialsDae = asset.MaterialsDae,
            DisplayName = asset.Name ?? "Unknown Asset",
            LevelPath = PathResolver.LevelPathCopyFrom
        };
    }

    /// <summary>
    /// Creates a request from a single MaterialJson.
    /// </summary>
    public static Viewer3DRequest FromMaterial(MaterialJson material, string? levelPath = null)
    {
        return new Viewer3DRequest
        {
            Mode = Viewer3DMode.MaterialOnPlane,
            Materials = new List<MaterialJson> { material },
            DisplayName = material.Name ?? material.InternalName ?? "Material Preview",
            LevelPath = levelPath
        };
    }

    /// <summary>
    /// Creates a request for a single texture file.
    /// </summary>
    public static Viewer3DRequest FromTexture(string texturePath, string? displayName = null)
    {
        return new Viewer3DRequest
        {
            Mode = Viewer3DMode.TextureOnly,
            DaeFilePath = texturePath, // Reuse this field for single texture path
            DisplayName = displayName ?? Path.GetFileName(texturePath),
            Materials = new List<MaterialJson>() // Ensure empty list, not null
        };
    }
}
