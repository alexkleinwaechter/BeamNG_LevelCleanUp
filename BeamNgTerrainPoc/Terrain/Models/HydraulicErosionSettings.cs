namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Settings for the optional hydraulic erosion terrain post-processing pass.
/// </summary>
public class HydraulicErosionSettings
{
    public bool Enabled { get; set; }
    public int IterationCount { get; set; } = 1_000_000;
    public int ErosionRadius { get; set; } = 3;
    public float Inertia { get; set; } = 0.05f;
    public float SedimentCapacityFactor { get; set; } = 4.0f;
    public float MinSedimentCapacity { get; set; } = 0.01f;
    public float ErodeSpeed { get; set; } = 0.3f;
    public float DepositSpeed { get; set; } = 0.3f;
    public float EvaporateSpeed { get; set; } = 0.01f;
    public float Gravity { get; set; } = 4.0f;
    public int MaxDropletLifetime { get; set; } = 30;
    public float InitialWaterVolume { get; set; } = 1.0f;
    public float InitialSpeed { get; set; } = 1.0f;
    public int RandomSeed { get; set; } = 12345;

    public HydraulicErosionSettings Clone() => new()
    {
        Enabled = Enabled,
        IterationCount = IterationCount,
        ErosionRadius = ErosionRadius,
        Inertia = Inertia,
        SedimentCapacityFactor = SedimentCapacityFactor,
        MinSedimentCapacity = MinSedimentCapacity,
        ErodeSpeed = ErodeSpeed,
        DepositSpeed = DepositSpeed,
        EvaporateSpeed = EvaporateSpeed,
        Gravity = Gravity,
        MaxDropletLifetime = MaxDropletLifetime,
        InitialWaterVolume = InitialWaterVolume,
        InitialSpeed = InitialSpeed,
        RandomSeed = RandomSeed
    };
}