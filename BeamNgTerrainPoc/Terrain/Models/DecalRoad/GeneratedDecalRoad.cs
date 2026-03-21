using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// A single generated DecalRoad object ready for scene writing.
/// Each node is [x, y, z, width] in BeamNG world coordinates.
/// </summary>
public class GeneratedDecalRoad
{
    public required string Name { get; init; }
    public required string ParentGroupName { get; init; }
    public required string Material { get; init; }
    public float TextureLength { get; init; } = 10.0f;
    public int RenderPriority { get; init; } = 10;
    public float[] StartEndFade { get; init; } = [0f, 0f];
    public float[] DistanceFade { get; init; } = [1000f, 1500f];
    public float Drivability { get; init; } = -1.0f;
    public required List<float[]> Nodes { get; init; } // Each: [x, y, z, width]
    public Vector3 Position => Nodes.Count > 0
        ? new Vector3(Nodes[0][0], Nodes[0][1], Nodes[0][2])
        : Vector3.Zero;

    // AI Road properties
    public bool IsAIRoad { get; init; }
    public bool AutoLanes { get; set; } = true;
    public int LanesLeft { get; set; } = 1;
    public int LanesRight { get; set; } = 1;
    public bool OneWay { get; set; }
    public bool FlipDirection { get; set; }
    public bool GatedRoad { get; set; }

    // Post-processor metadata
    public int SplineId { get; init; }
    public bool InterruptAtJunctions { get; init; }
    public bool IsRoundaboutRoad { get; init; }
    public bool PreserveContinuity { get; init; }

    // Rendering
    public bool OverObjects { get; init; }

    // Spline behaviour
    public bool ImprovedSpline { get; init; } = true;
    public float Smoothness { get; init; } = 0.5f;
    public float Detail { get; init; } = 0.1f;
}
