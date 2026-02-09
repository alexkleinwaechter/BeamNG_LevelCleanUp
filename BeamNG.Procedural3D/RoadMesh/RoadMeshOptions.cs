namespace BeamNG.Procedural3D.RoadMesh;

/// <summary>
/// Configuration options for road mesh generation.
/// </summary>
public class RoadMeshOptions
{
    /// <summary>
    /// Name for the generated mesh.
    /// </summary>
    public string MeshName { get; set; } = "RoadMesh";

    /// <summary>
    /// Material name to assign to the mesh.
    /// </summary>
    public string MaterialName { get; set; } = "road_asphalt";

    /// <summary>
    /// UV repeat distance in meters along the road (U axis).
    /// Default: 10m means the texture repeats every 10 meters along the road.
    /// </summary>
    public float TextureRepeatMetersU { get; set; } = 10f;

    /// <summary>
    /// UV scale across the road width (V axis).
    /// Default: 1.0 means V goes from 0 to 1 across the full road width.
    /// Set to road width in meters for consistent texel density.
    /// </summary>
    public float TextureRepeatMetersV { get; set; } = 1f;

    /// <summary>
    /// Generate shoulder geometry alongside the road.
    /// </summary>
    public bool IncludeShoulders { get; set; } = false;

    /// <summary>
    /// Shoulder width in meters (if shoulders enabled).
    /// </summary>
    public float ShoulderWidthMeters { get; set; } = 1.5f;

    /// <summary>
    /// Shoulder drop height in meters (how much lower the shoulder outer edge is).
    /// </summary>
    public float ShoulderDropMeters { get; set; } = 0.1f;

    /// <summary>
    /// Material name for shoulder geometry.
    /// </summary>
    public string ShoulderMaterialName { get; set; } = "road_shoulder";

    /// <summary>
    /// Generate curb geometry.
    /// </summary>
    public bool IncludeCurbs { get; set; } = false;

    /// <summary>
    /// Curb height in meters.
    /// </summary>
    public float CurbHeightMeters { get; set; } = 0.15f;

    /// <summary>
    /// Curb width in meters.
    /// </summary>
    public float CurbWidthMeters { get; set; } = 0.15f;

    /// <summary>
    /// Material name for curb geometry.
    /// </summary>
    public string CurbMaterialName { get; set; } = "road_curb";

    /// <summary>
    /// Calculate smooth normals (true) or flat normals (false).
    /// Smooth normals give better visual appearance for curved roads.
    /// </summary>
    public bool SmoothNormals { get; set; } = true;

    /// <summary>
    /// Generate end caps for the road mesh (close the start and end).
    /// </summary>
    public bool GenerateEndCaps { get; set; } = false;

    /// <summary>
    /// Minimum number of cross-sections required to generate a mesh.
    /// </summary>
    public int MinimumCrossSections { get; set; } = 2;

    /// <summary>
    /// Creates a copy of these options with the specified mesh name.
    /// </summary>
    public RoadMeshOptions WithMeshName(string meshName)
    {
        return new RoadMeshOptions
        {
            MeshName = meshName,
            MaterialName = MaterialName,
            TextureRepeatMetersU = TextureRepeatMetersU,
            TextureRepeatMetersV = TextureRepeatMetersV,
            IncludeShoulders = IncludeShoulders,
            ShoulderWidthMeters = ShoulderWidthMeters,
            ShoulderDropMeters = ShoulderDropMeters,
            ShoulderMaterialName = ShoulderMaterialName,
            IncludeCurbs = IncludeCurbs,
            CurbHeightMeters = CurbHeightMeters,
            CurbWidthMeters = CurbWidthMeters,
            CurbMaterialName = CurbMaterialName,
            SmoothNormals = SmoothNormals,
            GenerateEndCaps = GenerateEndCaps,
            MinimumCrossSections = MinimumCrossSections
        };
    }
}
