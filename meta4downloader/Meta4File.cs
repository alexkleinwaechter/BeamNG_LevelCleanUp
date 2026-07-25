namespace meta4downloader;

/// <summary>
/// Represents a file entry in a meta4 metalink document
/// </summary>
public class Meta4File
{
    public string Name { get; set; } = string.Empty;

    /// <summary>File size in bytes; 0 = unknown (no size verification possible).</summary>
    public long Size { get; set; }

    /// <summary>SHA-256 hex string; empty = unknown (no hash verification possible).</summary>
    public string Sha256Hash { get; set; } = string.Empty;

    /// <summary>Mirror URLs, deduplicated, in document order. First entry is the primary.</summary>
    public List<string> Urls { get; set; } = new();

    public string Url => Urls.Count > 0 ? Urls[0] : string.Empty;
}
