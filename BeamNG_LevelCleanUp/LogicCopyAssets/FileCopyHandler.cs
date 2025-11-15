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
        var fileParts = sourceFile.Split(@"\levels\");
        if (fileParts.Count() != 2) throw new FileNotFoundException($"Could not copy file: {sourceFile}");

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
            targetFile = Path.ChangeExtension(targetFile, Path.GetExtension(destinationFilePath2));
            File.Copy(destinationFilePath2, targetFile, true);
        }
        else
        {
            throw new FileNotFoundException($"Could not extract file from zip: {sourceFile}");
        }
    }

    private string TryExtractImageWithExtensions(string beamZip, string extractPath, string filePathEnd)
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