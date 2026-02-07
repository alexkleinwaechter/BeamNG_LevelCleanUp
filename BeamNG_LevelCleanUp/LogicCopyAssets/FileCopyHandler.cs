using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles file copy operations with fallback to zip extraction
/// </summary>
public class FileCopyHandler
{
    private readonly string _levelNameCopyFrom;

    public FileCopyHandler(string levelNameCopyFrom)
    {
        _levelNameCopyFrom = levelNameCopyFrom;
    }

    public void CopyFile(string sourceFile, string targetFile)
    {
        try
        {
            File.Copy(sourceFile, targetFile, true);
        }
        catch (Exception)
        {
            TryExtractFromZip(sourceFile, targetFile);
        }
    }

    private void TryExtractFromZip(string sourceFile, string targetFile)
    {
        // First, try extracting from game content ZIPs (assets.zip, etc.)
        if (TryExtractFromContentZip(sourceFile, targetFile))
            return;

        // Fall back to level-specific ZIP extraction
        TryExtractFromLevelZip(sourceFile, targetFile);
    }

    /// <summary>
    /// Tries to extract a file from BeamNG's content ZIP archives (assets.zip, etc.)
    /// Handles paths like: .../levels/assets/materials/tileable/stone/...
    /// </summary>
    private bool TryExtractFromContentZip(string sourceFile, string targetFile)
    {
        // Check if the path contains "assets" after "levels" - this indicates a content asset
        var levelsIndex = sourceFile.IndexOf(@"\levels\", StringComparison.OrdinalIgnoreCase);
        if (levelsIndex < 0)
            return false;

        var afterLevels = sourceFile.Substring(levelsIndex + 8); // Skip "\levels\"

        // Check if this looks like a content asset path (starts with "assets\")
        if (!afterLevels.StartsWith("assets", StringComparison.OrdinalIgnoreCase))
            return false;

        // Try to extract using ZipAssetExtractor
        using var stream = ZipAssetExtractor.ExtractAsset(afterLevels);
        if (stream == null)
            return false;

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        using var fileStream = File.Create(targetFile);
        stream.CopyTo(fileStream);
        return true;
    }

    /// <summary>
    /// Tries to extract a file from level-specific ZIP archives.
    /// </summary>
    private void TryExtractFromLevelZip(string sourceFile, string targetFile)
    {
        var fileParts = sourceFile.Split(@"\levels\");
        if (fileParts.Length != 2)
            throw new FileNotFoundException($"Could not copy file: {sourceFile}");

        var thisLevelName = fileParts[1].Split(@"\").FirstOrDefault() ?? string.Empty;
        var beamDir = Path.Join(Steam.GetBeamInstallDir(), Constants.BeamMapPath, thisLevelName);
        var beamZip = beamDir + ".zip";

        if (!new FileInfo(beamZip).Exists)
            throw new FileNotFoundException($"Could not find zip file: {beamZip}");

        if (thisLevelName.Equals(_levelNameCopyFrom, StringComparison.InvariantCultureIgnoreCase))
        {
            // Same level, extract directly
        }

        var extractPath = fileParts[0];
        var filePathEnd = fileParts[1];

        // Check if we're looking for a .link file
        if (sourceFile.EndsWith(".link", StringComparison.OrdinalIgnoreCase))
        {
            var destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
            if (destinationFilePath != null)
            {
                File.Copy(destinationFilePath, targetFile, true);
                return;
            }
        }

        // Try different image extensions
        var destinationFilePath2 = TryExtractImageWithExtensions(beamZip, extractPath, filePathEnd);

        if (destinationFilePath2 != null)
        {
            if (FileUtils.IsLinkFile(destinationFilePath2))
            {
                // For .link files, append .link to preserve the full extension chain
                // e.g., target.png → target.png.link (not target.link)
                if (!FileUtils.IsLinkFile(targetFile))
                    targetFile += FileUtils.LinkExtension;
            }
            else
            {
                targetFile = Path.ChangeExtension(targetFile, Path.GetExtension(destinationFilePath2));
            }

            File.Copy(destinationFilePath2, targetFile, true);
        }
        else
        {
            throw new FileNotFoundException($"Could not extract file from zip: {sourceFile}");
        }
    }

    private string? TryExtractImageWithExtensions(string beamZip, string extractPath, string filePathEnd)
    {
        var imageextensions = new List<string> { ".dds", ".png", ".jpg", ".jpeg", ".link" };

        // Add extension if not present
        if (!imageextensions.Any(x => filePathEnd.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            filePathEnd = filePathEnd + ".png";

        // Try various extensions and their .link variants
        var extensionsToTry = new[] { ".png", ".dds", ".jpg", ".jpeg" };

        foreach (var ext in extensionsToTry)
        {
            var testPath = Path.ChangeExtension(filePathEnd.Replace(".link", ""), ext);

            // Try direct file
            var result = ZipReader.ExtractFile(beamZip, extractPath, testPath);
            if (result != null) return result;

            // Try .link version
            result = ZipReader.ExtractFile(beamZip, extractPath, testPath + ".link");
            if (result != null) return result;
        }

        return null;
    }
}