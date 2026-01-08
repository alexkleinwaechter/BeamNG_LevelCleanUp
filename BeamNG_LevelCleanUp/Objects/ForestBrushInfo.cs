namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Represents a forest brush with its elements for copying.
/// ForestBrushes are painting templates used in BeamNG's World Editor Forest tool.
/// </summary>
public class ForestBrushInfo
{
    /// <summary>
    /// Full name of the brush (e.g., "ForestBrush_Trees_Tropical_1")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Internal name of the brush (e.g., "Trees_Tropical_1")
    /// </summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier for the brush (GUID)
    /// </summary>
    public string PersistentId { get; set; } = string.Empty;

    /// <summary>
    /// Parent group name (typically "ForestBrushGroup")
    /// </summary>
    public string ParentName { get; set; } = string.Empty;

    /// <summary>
    /// Direct forestItemData reference if brush has no elements (single-item brush)
    /// </summary>
    public string DirectForestItemData { get; set; }

    /// <summary>
    /// List of brush elements that define which ForestItemData meshes are included
    /// </summary>
    public List<ForestBrushElementInfo> Elements { get; set; } = new();

    /// <summary>
    /// List of all ForestItemData names referenced by this brush and its elements
    /// </summary>
    public List<string> ReferencedItemDataNames { get; set; } = new();

    /// <summary>
    /// Raw JSON text from source file to preserve unknown properties.
    /// When copying, we parse this, update specific fields, and write back.
    /// </summary>
    public string RawJson { get; set; }
}

/// <summary>
/// Represents a brush element linking to ForestItemData.
/// Elements define which meshes are included in a brush and with what properties.
/// </summary>
public class ForestBrushElementInfo
{
    /// <summary>
    /// Internal name of the element (typically matches ForestItemData name)
    /// </summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier for the element (GUID)
    /// </summary>
    public string PersistentId { get; set; } = string.Empty;

    /// <summary>
    /// Reference to ForestItemData by name
    /// </summary>
    public string ForestItemDataRef { get; set; } = string.Empty;

    /// <summary>
    /// Parent brush name (from __parent property)
    /// </summary>
    public string ParentBrushName { get; set; } = string.Empty;

    // Known optional properties (used for display/filtering, but RawJson is authoritative for copying)
    public float? ScaleMin { get; set; }
    public float? ScaleMax { get; set; }
    public float? Probability { get; set; }
    public float? SinkMin { get; set; }
    public float? SinkMax { get; set; }
    public float? SlopeMin { get; set; }
    public float? SlopeMax { get; set; }
    public float? ElevationMin { get; set; }
    public float? ElevationMax { get; set; }
    public int? RotationRange { get; set; }

    /// <summary>
    /// Raw JSON text from source file to preserve unknown properties.
    /// When copying, we parse this, update specific fields (persistentId, __parent), and write back.
    /// </summary>
    public string RawJson { get; set; } = string.Empty;
}

/// <summary>
/// Represents ForestItemData with shape file reference.
/// This is the template definition that links a forest type name to a 3D mesh.
/// </summary>
public class ForestItemDataInfo
{
    /// <summary>
    /// Name of the item data (key in managedItemData.json)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Internal name of the item data
    /// </summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (GUID)
    /// </summary>
    public string PersistentId { get; set; } = string.Empty;

    /// <summary>
    /// Path to the shape file (.dae) in BeamNG format (e.g., "/levels/levelname/art/shapes/tree.dae")
    /// </summary>
    public string ShapeFile { get; set; } = string.Empty;

    /// <summary>
    /// Class name (typically "TSForestItemData")
    /// </summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>
    /// Physics properties
    /// </summary>
    public float? Radius { get; set; }
    public float? Mass { get; set; }
    public float? Rigidity { get; set; }
    public float? DampingCoefficient { get; set; }
    public float? TightnessCoefficient { get; set; }

    /// <summary>
    /// Wind animation properties
    /// </summary>
    public float? WindScale { get; set; }
    public float? BranchAmp { get; set; }
    public float? TrunkBendScale { get; set; }
    public float? DetailAmp { get; set; }
    public float? DetailFreq { get; set; }

    /// <summary>
    /// All other properties stored as raw JSON text to preserve unknown fields
    /// </summary>
    public string RawJsonText { get; set; }
}
