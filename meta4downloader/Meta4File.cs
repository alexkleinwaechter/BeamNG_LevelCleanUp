namespace meta4downloader;

/// <summary>
/// Represents a file entry in a meta4 metalink document
/// </summary>
public class Meta4File
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256Hash { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
