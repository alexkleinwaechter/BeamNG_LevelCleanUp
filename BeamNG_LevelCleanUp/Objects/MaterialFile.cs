namespace BeamNG_LevelCleanUp.Objects;

public class MaterialFile
{
    public FileInfo? File { get; set; }
    public string MapType { get; set; }
    public bool Missing { get; set; }
    public string MaterialName { get; internal set; }

    /// <summary>
    ///     The original path as it appears in the JSON file (before resolution to Windows path)
    /// </summary>
    public string OriginalJsonPath { get; set; }

    /// <summary>
    ///     True if this file references a BeamNG core game asset (path starts with /assets/).
    ///     These should not be copied — the game resolves them from its own content archives.
    /// </summary>
    public bool IsGameAsset { get; set; }
}