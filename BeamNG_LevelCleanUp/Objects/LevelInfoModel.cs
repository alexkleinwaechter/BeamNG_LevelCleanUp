namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     Model for BeamNG level info.json metadata fields.
///     Used by LevelInfoForm component for both CreateLevel and RenameMap pages.
/// </summary>
public class LevelInfoModel
{
    public string LevelPath { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public string Country { get; set; }
    public string Region { get; set; }
    public string Biome { get; set; }
    public string Roads { get; set; }
    public string SuitableFor { get; set; }
    public string Features { get; set; }
    public string Authors { get; set; }

    public void Reset()
    {
        LevelPath = null;
        DisplayName = null;
        Description = null;
        Country = null;
        Region = null;
        Biome = null;
        Roads = null;
        SuitableFor = null;
        Features = null;
        Authors = null;
    }
}
