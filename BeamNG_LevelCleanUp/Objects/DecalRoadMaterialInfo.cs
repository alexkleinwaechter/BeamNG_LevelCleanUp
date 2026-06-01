namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Lightweight info about a decalroad material for use in dropdowns/selectors.
/// </summary>
public class DecalRoadMaterialInfo
{
    /// <summary>Material name (used as the DecalRoad "material" property in BeamNG).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where this material comes from.</summary>
    public DecalRoadMaterialSource Source { get; set; }

    /// <summary>Base color map path (for preview and display). May be a /assets/... path.</summary>
    public string? BaseColorMap { get; set; }

    /// <summary>Material tags (e.g., "RoadAndPath").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Full MaterialJson if available (for preview). Null for game materials until preview requested.</summary>
    public MaterialJson? MaterialJson { get; set; }

    /// <summary>Display string for the autocomplete dropdown.</summary>
    public string DisplayText => Source switch
    {
        DecalRoadMaterialSource.Game => $"{Name}  [game]",
        DecalRoadMaterialSource.Level => $"{Name}  [level]",
        _ => Name
    };
}

public enum DecalRoadMaterialSource
{
    Game,   // From art_shapes.zip (BeamNG default decalroad materials)
    Level   // From level's materials.json files (tagged RoadAndPath)
}
