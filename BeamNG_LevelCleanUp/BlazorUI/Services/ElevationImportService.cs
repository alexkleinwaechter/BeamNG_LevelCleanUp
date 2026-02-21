using System.IO.Compression;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Service that orchestrates unified elevation data import.
///     Handles format detection, ZIP extraction, EPSG auto-detection, and metadata reading.
///     Supports GeoTIFF (.tif/.tiff), XYZ ASCII (.xyz/.txt), ZIP archives (.zip), and PNG (.png).
/// </summary>
public class ElevationImportService
{
    private static readonly string[] GeoTiffExtensions = [".tif", ".tiff", ".geotiff"];
    private static readonly string[] XyzExtensions = [".xyz", ".txt"];
    private static readonly string[] PngExtensions = [".png"];
    private static readonly string[] ZipExtensions = [".zip"];

    private readonly GeoTiffMetadataService _metadataService = new();
    private string? _tempExtractionPath;

    /// <summary>
    ///     Imports elevation data from one or more selected files.
    ///     Auto-detects format from file extensions and handles ZIP extraction.
    /// </summary>
    public async Task<ElevationImportResult> ImportFilesAsync(string[] filePaths)
    {
        if (filePaths.Length == 0)
            throw new ArgumentException("No files selected.");

        // Check for ZIP files first — extract and treat contents as the real input
        var resolvedPaths = new List<string>();
        string? tempExtractionPath = null;
        var wasZip = false;

        foreach (var path in filePaths)
        {
            if (IsZipFile(path))
            {
                tempExtractionPath = ExtractZip(path);
                wasZip = true;
                // Scan extracted contents for supported files
                var extracted = ScanDirectoryForSupportedFiles(tempExtractionPath);
                resolvedPaths.AddRange(extracted);
            }
            else
            {
                resolvedPaths.Add(path);
            }
        }

        if (resolvedPaths.Count == 0)
            throw new InvalidOperationException(
                "No supported elevation files found. Supported formats: GeoTIFF (.tif, .tiff), XYZ (.xyz, .txt), PNG (.png).");

        _tempExtractionPath = tempExtractionPath;

        return await ClassifyAndReadMetadata(resolvedPaths.ToArray(), tempExtractionPath, wasZip);
    }

    /// <summary>
    ///     Imports elevation data from a folder (scans for supported files).
    /// </summary>
    public async Task<ElevationImportResult> ImportFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

        var supportedFiles = ScanDirectoryForSupportedFiles(folderPath);

        if (supportedFiles.Length == 0)
            throw new InvalidOperationException(
                $"No supported elevation files found in '{folderPath}'. " +
                "Supported extensions: .tif, .tiff, .xyz, .txt, .png");

        // Check if all files are the same type
        var geoTiffs = supportedFiles.Where(f => IsGeoTiffFile(f)).ToArray();
        var xyzFiles = supportedFiles.Where(f => IsXyzFile(f)).ToArray();

        // Prefer GeoTIFF tiles if found (existing behavior)
        if (geoTiffs.Length > 0 && xyzFiles.Length == 0)
        {
            // Use the existing directory-based tile reading
            var metadata = await _metadataService.ReadFromDirectoryAsync(folderPath);
            return new ElevationImportResult
            {
                SourceType = ElevationSourceType.GeoTiffMultiple,
                FilePaths = geoTiffs,
                FileNames = geoTiffs.Select(Path.GetFileName).ToArray()!,
                FileCount = geoTiffs.Length,
                FormatLabel = "GeoTIFF",
                Metadata = metadata,
                ResolvedGeoTiffDirectory = folderPath
            };
        }

        // For XYZ files in folder, delegate to file-based classification
        // (handles both single and multi-XYZ)
        return await ClassifyAndReadMetadata(supportedFiles, null, false);
    }

    /// <summary>
    ///     Re-reads metadata after the user changes the EPSG code (for XYZ files).
    /// </summary>
    public async Task<ElevationImportResult> ReloadWithEpsgAsync(ElevationImportResult previous, int epsgCode)
    {
        if (previous.SourceType != ElevationSourceType.XyzFile &&
            previous.SourceType != ElevationSourceType.XyzMultiple)
            return previous;

        GeoTiffMetadataService.GeoTiffMetadataResult? metadata;

        if (previous.SourceType == ElevationSourceType.XyzMultiple &&
            previous.ResolvedXyzFilePaths is { Length: > 1 })
        {
            // Multiple XYZ tiles — read combined metadata from all tiles
            metadata = await _metadataService.ReadFromXyzFilesAsync(previous.ResolvedXyzFilePaths, epsgCode);
        }
        else
        {
            // Single XYZ file
            var xyzPathForMetadata = previous.ResolvedXyzPath ??
                                     previous.ResolvedXyzFilePaths?.FirstOrDefault();
            if (string.IsNullOrEmpty(xyzPathForMetadata))
                return previous;

            metadata = await _metadataService.ReadFromXyzFileAsync(xyzPathForMetadata, epsgCode);
        }

        return new ElevationImportResult
        {
            SourceType = previous.SourceType,
            FilePaths = previous.FilePaths,
            FileNames = previous.FileNames,
            FileCount = previous.FileCount,
            FormatLabel = previous.FormatLabel,
            Metadata = metadata,
            NeedsEpsgCode = true,
            DetectedEpsgCode = previous.DetectedEpsgCode,
            EpsgCode = epsgCode,
            TempExtractionPath = previous.TempExtractionPath,
            WasExtractedFromZip = previous.WasExtractedFromZip,
            ResolvedXyzPath = previous.ResolvedXyzPath,
            ResolvedXyzFilePaths = previous.ResolvedXyzFilePaths
        };
    }

    /// <summary>
    ///     Cleans up temporary files from ZIP extraction.
    /// </summary>
    public void CleanupTempFiles()
    {
        if (!string.IsNullOrEmpty(_tempExtractionPath) && Directory.Exists(_tempExtractionPath))
        {
            try
            {
                Directory.Delete(_tempExtractionPath, true);
            }
            catch
            {
                // Ignore cleanup errors
            }

            _tempExtractionPath = null;
        }
    }

    private async Task<ElevationImportResult> ClassifyAndReadMetadata(
        string[] filePaths, string? tempExtractionPath, bool wasZip)
    {
        var geoTiffs = filePaths.Where(IsGeoTiffFile).ToArray();
        var xyzFiles = filePaths.Where(IsXyzFile).ToArray();
        var pngFiles = filePaths.Where(IsPngFile).ToArray();

        // Validate: don't mix formats
        var formatCount = (geoTiffs.Length > 0 ? 1 : 0) +
                          (xyzFiles.Length > 0 ? 1 : 0) +
                          (pngFiles.Length > 0 ? 1 : 0);

        if (formatCount > 1)
            throw new InvalidOperationException(
                "Mixed file formats detected. Please select files of a single format (GeoTIFF, XYZ, or PNG).");

        if (formatCount == 0)
            throw new InvalidOperationException(
                "No supported elevation files found. Supported: .tif, .tiff, .xyz, .txt, .png");

        // PNG
        if (pngFiles.Length > 0)
        {
            if (pngFiles.Length > 1)
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Multiple PNG files selected — only the first one will be used.");

            var (pngWidth, pngHeight, pngBitDepth) = ReadPngHeader(pngFiles[0]);

            return new ElevationImportResult
            {
                SourceType = ElevationSourceType.Png,
                FilePaths = [pngFiles[0]],
                FileNames = [Path.GetFileName(pngFiles[0])],
                FileCount = 1,
                FormatLabel = "PNG",
                TempExtractionPath = tempExtractionPath,
                WasExtractedFromZip = wasZip,
                ResolvedHeightmapPath = pngFiles[0],
                PngWidth = pngWidth,
                PngHeight = pngHeight,
                PngBitDepth = pngBitDepth
            };
        }

        // GeoTIFF
        if (geoTiffs.Length > 0)
        {
            GeoTiffMetadataService.GeoTiffMetadataResult? metadata;

            if (geoTiffs.Length == 1)
            {
                metadata = await _metadataService.ReadFromFileAsync(geoTiffs[0]);
                return new ElevationImportResult
                {
                    SourceType = ElevationSourceType.GeoTiffSingle,
                    FilePaths = geoTiffs,
                    FileNames = geoTiffs.Select(Path.GetFileName).ToArray()!,
                    FileCount = 1,
                    FormatLabel = "GeoTIFF",
                    Metadata = metadata,
                    TempExtractionPath = tempExtractionPath,
                    WasExtractedFromZip = wasZip,
                    ResolvedGeoTiffPath = geoTiffs[0]
                };
            }

            // Multiple GeoTIFF tiles — use directory of the first file, or temp extraction path
            var tileDirectory = tempExtractionPath ?? Path.GetDirectoryName(geoTiffs[0])!;
            metadata = await _metadataService.ReadFromDirectoryAsync(tileDirectory);
            return new ElevationImportResult
            {
                SourceType = ElevationSourceType.GeoTiffMultiple,
                FilePaths = geoTiffs,
                FileNames = geoTiffs.Select(Path.GetFileName).ToArray()!,
                FileCount = geoTiffs.Length,
                FormatLabel = "GeoTIFF",
                Metadata = metadata,
                TempExtractionPath = tempExtractionPath,
                WasExtractedFromZip = wasZip,
                ResolvedGeoTiffDirectory = tileDirectory
            };
        }

        // XYZ
        if (xyzFiles.Length > 0)
        {
            // Use first file for EPSG auto-detection
            var firstXyz = xyzFiles[0];

            // Auto-detect EPSG code
            var detectedEpsg = GeoTiffReader.AutoDetectEpsg(firstXyz);
            var epsgCode = detectedEpsg ?? 25832; // Default to German UTM 32N

            if (detectedEpsg.HasValue)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Auto-detected EPSG:{detectedEpsg} from XYZ coordinate ranges");

            if (xyzFiles.Length == 1)
            {
                GeoTiffMetadataService.GeoTiffMetadataResult? singleMetadata = null;
                try
                {
                    singleMetadata = await _metadataService.ReadFromXyzFileAsync(firstXyz, epsgCode);
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Could not read XYZ metadata: {ex.Message}");
                }

                return new ElevationImportResult
                {
                    SourceType = ElevationSourceType.XyzFile,
                    FilePaths = [firstXyz],
                    FileNames = [Path.GetFileName(firstXyz)],
                    FileCount = 1,
                    FormatLabel = "XYZ ASCII",
                    Metadata = singleMetadata,
                    NeedsEpsgCode = true,
                    DetectedEpsgCode = detectedEpsg,
                    EpsgCode = epsgCode,
                    TempExtractionPath = tempExtractionPath,
                    WasExtractedFromZip = wasZip,
                    ResolvedXyzPath = firstXyz
                };
            }

            // Multiple XYZ tiles — read combined metadata from all tiles
            GeoTiffMetadataService.GeoTiffMetadataResult? multiMetadata = null;
            try
            {
                multiMetadata = await _metadataService.ReadFromXyzFilesAsync(xyzFiles, epsgCode);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Could not read combined XYZ metadata: {ex.Message}");
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"{xyzFiles.Length} XYZ tiles detected — will be combined during generation");

            return new ElevationImportResult
            {
                SourceType = ElevationSourceType.XyzMultiple,
                FilePaths = xyzFiles,
                FileNames = xyzFiles.Select(Path.GetFileName).ToArray()!,
                FileCount = xyzFiles.Length,
                FormatLabel = "XYZ ASCII",
                Metadata = multiMetadata,
                NeedsEpsgCode = true,
                DetectedEpsgCode = detectedEpsg,
                EpsgCode = epsgCode,
                TempExtractionPath = tempExtractionPath,
                WasExtractedFromZip = wasZip,
                ResolvedXyzFilePaths = xyzFiles
            };
        }

        throw new InvalidOperationException("Unexpected: no files matched any known format.");
    }

    private string ExtractZip(string zipPath)
    {
        var extractDir = Path.Combine(
            AppPaths.TempFolder,
            $"_elevation_import_{Guid.NewGuid():N}");

        Directory.CreateDirectory(extractDir);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Extracting ZIP: {Path.GetFileName(zipPath)}...");

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var fileCount = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories).Length;
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Extracted {fileCount} files from ZIP");

        return extractDir;
    }

    private static string[] ScanDirectoryForSupportedFiles(string directoryPath)
    {
        return Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Where(f => IsGeoTiffFile(f) || IsXyzFile(f) || IsPngFile(f))
            .OrderBy(f => f)
            .ToArray();
    }

    private static bool IsGeoTiffFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return GeoTiffExtensions.Contains(ext);
    }

    private static bool IsXyzFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return XyzExtensions.Contains(ext);
    }

    private static bool IsPngFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return PngExtensions.Contains(ext);
    }

    private static bool IsZipFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ZipExtensions.Contains(ext);
    }

    /// <summary>
    ///     Reads width, height, and bit depth from a PNG file's IHDR chunk header
    ///     without loading the full image into memory.
    /// </summary>
    internal static (int Width, int Height, int BitDepth) ReadPngHeader(string pngPath)
    {
        try
        {
            // PNG layout: 8-byte signature, then IHDR chunk (4-byte length, 4-byte type, 13-byte data).
            // IHDR data: width (4 bytes BE), height (4 bytes BE), bit depth (1 byte), ...
            var header = new byte[26];
            using var fs = File.OpenRead(pngPath);
            if (fs.Read(header, 0, 26) < 26)
                return (0, 0, 0);

            // Bytes 16-19: width, 20-23: height (big-endian), 24: bit depth
            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            var bitDepth = header[24];

            return (width, height, bitDepth);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
