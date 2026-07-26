namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Detected elevation source type after format auto-detection.
/// </summary>
public enum ElevationSourceType
{
    Png,
    GeoTiffSingle,
    GeoTiffMultiple,
    XyzFile,
    XyzMultiple
}

/// <summary>
///     Result of importing elevation data files.
///     Contains detected format, file paths, metadata, and resolved paths for the generation pipeline.
/// </summary>
public class ElevationImportResult
{
    public ElevationSourceType SourceType { get; init; }
    public string[] FilePaths { get; init; } = [];
    public string[] FileNames { get; init; } = [];
    public int FileCount { get; init; }

    /// <summary>
    ///     Human-readable format label (e.g., "GeoTIFF", "XYZ ASCII", "PNG").
    /// </summary>
    public string FormatLabel { get; init; } = "";

    /// <summary>
    ///     Metadata read from the elevation source.
    /// </summary>
    public GeoTiffMetadataService.GeoTiffMetadataResult? Metadata { get; init; }

    // EPSG handling: always true for XYZ (no embedded CRS); true for GeoTIFF only when the
    // embedded CRS is unusable and the user must supply an EPSG code manually.
    // Settable because the page flips it when late metadata reads reveal an unresolvable CRS.
    public bool NeedsEpsgCode { get; set; }
    public int? DetectedEpsgCode { get; init; }
    public int EpsgCode { get; set; }

    // ZIP extraction tracking
    public string? TempExtractionPath { get; init; }
    public bool WasExtractedFromZip { get; init; }

    // PNG-specific metadata
    public int PngWidth { get; init; }
    public int PngHeight { get; init; }
    public int PngBitDepth { get; init; }

    // Resolved paths for the generation pipeline
    public string? ResolvedGeoTiffPath { get; init; }
    public string? ResolvedGeoTiffDirectory { get; init; }
    public string? ResolvedXyzPath { get; init; }
    public string[]? ResolvedXyzFilePaths { get; init; }
    public string? ResolvedHeightmapPath { get; init; }
}
