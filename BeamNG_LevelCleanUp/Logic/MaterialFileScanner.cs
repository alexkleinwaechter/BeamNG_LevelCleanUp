using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class MaterialFileScanner
{
    private readonly string _levelPath;
    private readonly string _matJsonPath;

    public MaterialFileScanner(string levelPath, List<MaterialStage> stages, string matJsonPath)
    {
        _levelPath = levelPath;
        _stages = stages;
        _matJsonPath = matJsonPath;
    }

    private List<MaterialStage> _stages { get; } = new();

    public List<MaterialFile> GetMaterialFiles(string materialName)
    {
        var retVal = new List<MaterialFile>();
        foreach (var stage in _stages)
        foreach (var prop in stage.GetType().GetProperties())
        {
            // Only process string properties (texture paths), skip collections and other types
            if (prop.PropertyType != typeof(string)) continue;

            var val = prop.GetValue(stage, null) != null ? prop.GetValue(stage, null).ToString() : string.Empty;

            if (!string.IsNullOrEmpty(val))
            {
                if (val.StartsWith("./")) val = val.Remove(0, 2);
                if (val.Count(c => c == '/') == 0) val = Path.Join(Path.GetDirectoryName(_matJsonPath), val);
                var filePath = PathResolver.ResolvePath(_levelPath, val, false);
                var fileInfo = FileUtils.ResolveImageFileName(filePath);
                retVal.Add(new MaterialFile
                {
                    MaterialName = materialName,
                    Missing = !fileInfo.Exists,
                    File = fileInfo,
                    MapType = prop.Name,
                    OriginalJsonPath = val
                });
            }
        }

        return retVal;
    }
}