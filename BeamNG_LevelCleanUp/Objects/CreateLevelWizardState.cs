namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     State management for the Create Level wizard workflow
/// </summary>
public class CreateLevelWizardState
{
    /// <summary>
    ///     Target level path (folder name, e.g., "my_custom_map")
    /// </summary>
    public string TargetLevelPath { get; set; }

    /// <summary>
    ///     Display name for the level (e.g., "My Custom Map")
    /// </summary>
    public string LevelName { get; set; }

    /// <summary>
    ///     Full path to the source level directory
    /// </summary>
    public string SourceLevelPath { get; set; }

    /// <summary>
    ///     Name of the source level
    /// </summary>
    public string SourceLevelName { get; set; }

    /// <summary>
    ///     Full path to the target level root directory
    /// </summary>
    public string TargetLevelRootPath { get; set; }

    /// <summary>
    ///     Current step in the wizard (0-based)
    /// </summary>
    public int CurrentStep { get; set; }

    /// <summary>
    ///     Indicates if the wizard is currently active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    ///     MissionGroup assets copied from source level
    /// </summary>
    public List<Asset> CopiedMissionGroupAssets { get; set; } = new();

    /// <summary>
    ///     Terrain materials copied to the new level
    /// </summary>
    public List<MaterialJson> CopiedTerrainMaterials { get; set; } = new();

    /// <summary>
    ///     Ground covers copied to the new level
    /// </summary>
    public List<string> CopiedGroundCovers { get; set; } = new();

    /// <summary>
    ///     Files copied during the wizard process
    /// </summary>
    public List<string> CopiedFiles { get; set; } = new();

    /// <summary>
    ///     Step 1: Setup and initialization complete
    /// </summary>
    public bool Step1_SetupComplete { get; set; }

    /// <summary>
    ///     Step 2: MissionGroup data copied
    /// </summary>
    public bool Step2_MissionGroupsCopied { get; set; }

    /// <summary>
    ///     Step 3: Terrain materials selected and copied
    /// </summary>
    public bool Step3_TerrainMaterialsSelected { get; set; }

    /// <summary>
    ///     Resets the wizard state to initial values
    /// </summary>
    public void Reset()
    {
        TargetLevelPath = null;
        LevelName = null;
        SourceLevelPath = null;
        SourceLevelName = null;
        TargetLevelRootPath = null;
        CurrentStep = 0;
        IsActive = false;
        CopiedMissionGroupAssets = new List<Asset>();
        CopiedTerrainMaterials = new List<MaterialJson>();
        CopiedGroundCovers = new List<string>();
        CopiedFiles = new List<string>();
        Step1_SetupComplete = false;
        Step2_MissionGroupsCopied = false;
        Step3_TerrainMaterialsSelected = false;
    }

    /// <summary>
    ///     Gets the completion status of a specific step
    /// </summary>
    public bool IsStepComplete(int stepIndex)
    {
        return stepIndex switch
        {
            0 => Step1_SetupComplete,
            1 => Step2_MissionGroupsCopied,
            2 => Step3_TerrainMaterialsSelected,
            _ => false
        };
    }

    /// <summary>
    ///     Gets a summary of the wizard progress
    /// </summary>
    public string GetProgressSummary()
    {
        var completedSteps = new[] { Step1_SetupComplete, Step2_MissionGroupsCopied, Step3_TerrainMaterialsSelected }
            .Count(x => x);
        return $"{completedSteps}/3 steps completed";
    }
}
