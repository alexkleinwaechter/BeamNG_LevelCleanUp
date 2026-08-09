using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Logic;

internal class FileDeleter
{
    private readonly List<FileInfo> _fileList;
    private readonly string _levelPath;
    private readonly string _summaryFileName;

    internal FileDeleter(List<FileInfo> fileList, string levelPath, string summaryFileName, bool dryRun)
    {
        _fileList = fileList;
        _levelPath = levelPath;
        _summaryFileName = summaryFileName;
        _dryRun = dryRun;
    }

    private bool _dryRun { get; }

    public void Delete()
    {
        if (string.IsNullOrWhiteSpace(_levelPath))
            throw new InvalidOperationException("Cannot delete files without a valid extracted level root.");

        var levelRoot = Path.GetFullPath(_levelPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(levelRoot))
            throw new DirectoryNotFoundException($"Extracted level root not found: {levelRoot}");

        var levelPrefix = levelRoot + Path.DirectorySeparatorChar;
        var textLines = new List<string>();
        var textLinesNotFound = new List<string>();
        var rejectedPaths = new List<string>();
        var counterFound = 0;
        var counterNotFound = 0;
        foreach (var file in _fileList)
        {
            var fullPath = Path.GetFullPath(file.FullName);
            if (!fullPath.StartsWith(levelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                rejectedPaths.Add(fullPath);
                continue;
            }

            if (file.Exists)
            {
                if (!_dryRun) File.Delete(fullPath);
                textLines.Add(fullPath);
                counterFound++;
            }
            else
            {
                textLinesNotFound.Add(fullPath);
                counterNotFound++;
            }
        }

        var dryrunText = _dryRun ? "_dry_run_not_deleted" : string.Empty;
        File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}{dryrunText}.txt"), textLines);
        if (textLinesNotFound.Count > 0)
            File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}_files_not_found.txt"), textLinesNotFound);
        if (rejectedPaths.Count > 0)
        {
            File.WriteAllLines(Path.Join(_levelPath, $"{_summaryFileName}_rejected_outside_level.txt"), rejectedPaths);
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Safety check refused to delete {rejectedPaths.Count} file(s) outside the extracted level root.");
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"{counterFound} files deleted. {counterNotFound} files not found. Dry Run: {_dryRun}. See directory {_levelPath} for logfiles.");
    }
}
