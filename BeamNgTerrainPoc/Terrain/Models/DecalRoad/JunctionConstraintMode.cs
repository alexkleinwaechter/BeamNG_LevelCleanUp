namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Controls how a layer behaves at road junctions where it overlaps
/// another road's surface footprint.
/// </summary>
public enum JunctionConstraintMode
{
    /// <summary>No junction handling — layer continues uninterrupted through junctions.</summary>
    None,

    /// <summary>Layer is removed (split) where it overlaps another road's surface.</summary>
    Interrupt,

    /// <summary>Layer's material/width/textureLength are replaced in junction overlap zones.</summary>
    Replace
}
