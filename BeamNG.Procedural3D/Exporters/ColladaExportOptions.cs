namespace BeamNG.Procedural3D.Exporters;

/// <summary>
/// Configuration options for Collada (DAE) export.
/// </summary>
public class ColladaExportOptions
{
    /// <summary>
    /// Apply Y-up to Z-up coordinate conversion.
    /// BeamNG.drive uses Z-up coordinate system.
    /// Default: true
    /// </summary>
    public bool ConvertToZUp { get; init; } = true;

    /// <summary>
    /// Scale factor applied to all vertex positions.
    /// Default: 1.0 (no scaling)
    /// </summary>
    public float ScaleFactor { get; init; } = 1.0f;

    /// <summary>
    /// Include UV texture coordinates in export.
    /// Default: true
    /// </summary>
    public bool IncludeUVs { get; init; } = true;

    /// <summary>
    /// Include vertex normals in export.
    /// Default: true
    /// </summary>
    public bool IncludeNormals { get; init; } = true;

    /// <summary>
    /// Flip UV V coordinate (1 - V).
    /// Some applications use different UV origin conventions.
    /// Default: false
    /// </summary>
    public bool FlipUVVertical { get; init; } = false;

    /// <summary>
    /// Flip triangle winding order (reverses face normals).
    /// Default: false
    /// </summary>
    public bool FlipWindingOrder { get; init; } = false;

    /// <summary>
    /// Name for the root node in the scene hierarchy.
    /// Default: "RootNode"
    /// </summary>
    public string RootNodeName { get; init; } = "RootNode";

    /// <summary>
    /// Gets the default export options suitable for BeamNG.drive.
    /// </summary>
    public static ColladaExportOptions Default => new();

    /// <summary>
    /// Gets export options with no coordinate system conversion.
    /// </summary>
    public static ColladaExportOptions NoConversion => new()
    {
        ConvertToZUp = false
    };
}
