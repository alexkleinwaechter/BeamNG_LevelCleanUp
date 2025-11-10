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
}