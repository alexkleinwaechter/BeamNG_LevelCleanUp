namespace BeamNG.Procedural3D.Building;

/// <summary>
/// Derives individual PBR channel filenames from a combined ORM texture filename.
/// ORM follows the glTF 2.0 convention: R=AmbientOcclusion, G=Roughness, B=Metallic.
///
/// BeamNG requires separate texture files for each PBR channel:
///   - ambientOcclusionMap → *_AO.data.png
///   - roughnessMap        → *_Roughness.data.png
///   - metallicMap         → *_Metallic.data.png
///
/// This helper is shared by both BeamNG.Procedural3D (model layer) and
/// BeamNgTerrainPoc (texture processing layer).
/// </summary>
public static class OrmTextureHelper
{
    /// <summary>
    /// Derives the AO (ambient occlusion) filename from an ORM filename.
    /// E.g., "Bricks029_ORM.data.png" → "Bricks029_AO.data.png"
    /// </summary>
    public static string DeriveAoFileName(string ormFileName)
        => ormFileName.Replace("_ORM", "_AO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the roughness filename from an ORM filename.
    /// E.g., "Bricks029_ORM.data.png" → "Bricks029_Roughness.data.png"
    /// </summary>
    public static string DeriveRoughnessFileName(string ormFileName)
        => ormFileName.Replace("_ORM", "_Roughness", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the metallic filename from an ORM filename.
    /// E.g., "Bricks029_ORM.data.png" → "Bricks029_Metallic.data.png"
    /// </summary>
    public static string DeriveMetallicFileName(string ormFileName)
        => ormFileName.Replace("_ORM", "_Metallic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether a filename follows the ORM naming convention (contains "_ORM").
    /// Used as a guard before attempting filename derivation.
    /// </summary>
    public static bool IsOrmFileName(string fileName)
        => fileName.Contains("_ORM", StringComparison.OrdinalIgnoreCase);
}
