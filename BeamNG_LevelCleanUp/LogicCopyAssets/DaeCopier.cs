using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of DAE (Collada) files and their associated materials
/// </summary>
public class DaeCopier
{
    private readonly FileCopyHandler _fileCopyHandler;
    private readonly MaterialCopier _materialCopier;
    private readonly PathConverter _pathConverter;

    public DaeCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler, MaterialCopier materialCopier)
    {
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
        _materialCopier = materialCopier;
    }

    public bool Copy(CopyAsset item)
    {
        Directory.CreateDirectory(item.TargetPath);

        var daeFullName = _pathConverter.GetTargetFileName(item.DaeFilePath);
        if (string.IsNullOrEmpty(daeFullName)) return true;

        try
        {
            CopyDaeFiles(item.DaeFilePath, daeFullName);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Filepath error for daefile {daeFullName}. Exception:{ex.Message}");
            return true;
        }

        return _materialCopier.Copy(item);
    }

    private void CopyDaeFiles(string sourceDaePath, string targetDaePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDaePath));
        
        // Resolve the source path if it's a BeamNG relative path (starts with / or doesn't have drive letter)
        var resolvedSourcePath = sourceDaePath;
        if (!Path.IsPathRooted(sourceDaePath) || !Path.IsPathFullyQualified(sourceDaePath))
        {
            // The source path is a BeamNG-style path like "/levels/levelname/art/shapes/..."
            // Resolve it using the source level path from PathResolver
            resolvedSourcePath = PathResolver.ResolvePath(PathResolver.LevelPathCopyFrom, sourceDaePath, true);
        }
        
        _fileCopyHandler.CopyFile(resolvedSourcePath, targetDaePath);

        // Try to copy the compiled .cdae file as well
        try
        {
            _fileCopyHandler.CopyFile(
                Path.ChangeExtension(resolvedSourcePath, ".cdae"),
                Path.ChangeExtension(targetDaePath, ".cdae")
            );
        }
        catch (Exception)
        {
            // Ignore if .cdae doesn't exist
        }
    }
}