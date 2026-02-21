namespace BeamNG.Procedural3D.Exporters;

using BeamNG.Procedural3D.Core;

/// <summary>
/// Describes a BeamNG-compatible DAE scene with LOD hierarchy and collision mesh.
///
/// BeamNG DAE node structure:
///   base00 (parent of everything)
///   ├── nulldetail{N}      → cull threshold: object hidden when smaller than N pixels (optional)
///   ├── start01            → parent for all visual LOD meshes and Colmesh
///   │   ├── Colmesh-1      → collision geometry (LOD0 mesh without materials)
///   │   ├── {name}_a{px}   → LOD0 meshes (lowest detail, smallest pixel threshold)
///   │   ├── {name}_a{px}   → LOD1 meshes (medium detail)
///   │   └── {name}_a{px}   → LOD2 meshes (highest detail, largest pixel threshold)
///   └── collision-1        → empty marker node
///
/// The pixel number in the LOD name ({px}) indicates the screen-space pixel size
/// at which the object switches to this LOD level. Higher numbers = more detail,
/// shown when the object is larger on screen. The number MUST be preceded by a
/// letter (e.g., "_a250" works, "_250" fails on import).
/// </summary>
public class BeamNgDaeScene
{
    /// <summary>
    /// Base name for LOD mesh nodes (e.g., "building_12345").
    /// Each LOD level will be named "{BaseName}_a{PixelSize}".
    /// </summary>
    public required string BaseName { get; set; }

    /// <summary>
    /// LOD levels ordered by ascending detail (lowest detail first).
    /// Each level has a pixel threshold and a set of meshes.
    /// </summary>
    public List<LodLevel> LodLevels { get; set; } = [];

    /// <summary>
    /// Collision geometry placed under "Colmesh-1" node inside start01.
    /// Built from LOD0 meshes merged together without material assignment.
    /// If null, no collision mesh is generated.
    /// </summary>
    public List<Mesh>? ColmeshMeshes { get; set; }

    /// <summary>
    /// Pixel-size cull threshold. When the object is smaller than this many pixels on screen,
    /// it is not rendered at all. Creates a "nulldetail{N}" node under base00.
    /// 0 or negative = no nulldetail node (object always rendered regardless of size).
    /// </summary>
    public int NullDetailPixelSize { get; set; }
}

/// <summary>
/// A single LOD level containing meshes and the pixel threshold at which it activates.
/// </summary>
public class LodLevel
{
    /// <summary>
    /// Screen-space pixel size threshold. When the object covers this many pixels or more,
    /// this LOD level is displayed (and lower LODs are hidden).
    /// </summary>
    public int PixelSize { get; }

    /// <summary>
    /// The LOD suffix for node naming (e.g., "a40", "a100", "a250").
    /// Auto-generated from PixelSize with "a" prefix.
    /// </summary>
    public string Suffix => $"a{PixelSize}";

    /// <summary>
    /// All meshes for this LOD level. May include multiple meshes for different materials
    /// (e.g., walls, roof, windows, frames).
    /// </summary>
    public List<Mesh> Meshes { get; }

    public LodLevel(int pixelSize, List<Mesh> meshes)
    {
        if (pixelSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "Pixel size must be positive.");
        PixelSize = pixelSize;
        Meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
    }

    public LodLevel(int pixelSize, params Mesh[] meshes)
        : this(pixelSize, meshes.ToList())
    {
    }
}
