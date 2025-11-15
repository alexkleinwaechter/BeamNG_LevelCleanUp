using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class PrefabScanner
{
    private readonly List<Asset> _assets = new();

    internal PrefabScanner(List<Asset> assets, string levelPath)
    {
        _assets = assets;
        _levelPath = levelPath;
    }

    private string _levelPath { get; }

    internal void AddPrefabDaeFiles(string prefabFileName)
    {
        var prefabFileNames = new List<string>();
        var file = new FileInfo(PathResolver.ResolvePath(_levelPath, prefabFileName, false));
        if (file.Exists)
        {
            prefabFileNames = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? GetPrefabFilesJson(file)
                : GetPrefabFilesCs(file);
            var prefabFileInfos = prefabFileNames
                .Select(x => new FileInfo(PathResolver.ResolvePath(_levelPath, x, false))).ToList();
            AddPrefabDaeFiles(file);
            if (prefabFileInfos.Any())
                foreach (var item in prefabFileInfos)
                    if (item.Exists)
                        AddPrefabDaeFiles(item);
        }
    }

    internal void AddPrefabDaeFiles(FileInfo file)
    {
        var shapeNames = new List<string>();
        shapeNames = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? GetShapeNamesJson(file)
            : GetShapeNamesCs(file);
        var counter = 0;
        foreach (var shapeName in shapeNames)
        {
            counter++;
            var asset = new Asset
            {
                Name = $"{file.Name}_{counter}",
                Class = "TSStatic",
                ShapeName = shapeName
            };
            AddAsset(asset);
            //PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Prefab asset {asset.Name}", true);
        }
    }

    private List<string> GetShapeNamesCs(FileInfo file)
    {
        var shapeNames = new List<string>();
        foreach (var line in File.ReadLines(file.FullName))
        {
            if (line.ToUpperInvariant().Contains("shapename =", StringComparison.OrdinalIgnoreCase))
            {
                var nameParts = line.Split('"');
                if (nameParts.Length > 1)
                {
                    var name = nameParts[1];
                    if (name.StartsWith("./")) name = name.Remove(0, 2);
                    if (name.Count(c => c == '/') == 0) name = Path.Join(file.Directory.FullName, name);
                    shapeNames.Add(name);
                }
            }

            if (line.ToUpperInvariant().Contains("material =", StringComparison.OrdinalIgnoreCase))
            {
                var nameParts = line.Split('"');
                if (nameParts.Length > 1)
                    _assets.Add(new Asset
                    {
                        Class = "Decal",
                        Material = nameParts[1]
                    });
            }
        }

        return shapeNames;
    }

    private List<string> GetPrefabFilesCs(FileInfo file)
    {
        var prefabFileNames = new List<string>();
        foreach (var line in File.ReadLines(file.FullName))
            if (line.ToUpperInvariant().Contains("filename =", StringComparison.OrdinalIgnoreCase))
            {
                var nameParts = line.Split('"');
                if (nameParts.Length > 1)
                {
                    var name = nameParts[1];
                    if (name.StartsWith("./")) name = name.Remove(0, 2);
                    if (name.Count(c => c == '/') == 0) name = Path.Join(file.Directory.FullName, name);
                    prefabFileNames.Add(name);
                }
            }

        return prefabFileNames;
    }

    private List<string> GetShapeNamesJson(FileInfo file)
    {
        var shapeNames = new List<string>();
        foreach (var line in File.ReadAllLines(file.FullName))
            try
            {
                using var jsonObject = JsonUtils.GetValidJsonDocumentFromString(line, file.FullName);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                {
                    var asset = jsonObject.RootElement.Deserialize<Asset>(BeamJsonOptions.GetJsonSerializerOptions());
                    if (!string.IsNullOrEmpty(asset.ShapeName)) shapeNames.Add(asset.ShapeName);
                    if (!string.IsNullOrEmpty(asset.Material)) _assets.Add(asset);
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {file.FullName}. {ex.Message}.");
            }

        return shapeNames;
    }

    private List<string> GetPrefabFilesJson(FileInfo file)
    {
        var prefabFileNames = new List<string>();
        foreach (var line in File.ReadAllLines(file.FullName))
        {
            var docOptions = BeamJsonOptions.GetJsonDocumentOptions();
            try
            {
                using var jsonObject = JsonUtils.GetValidJsonDocumentFromString(line, file.FullName);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                {
                    var asset = jsonObject.RootElement.Deserialize<Asset>(BeamJsonOptions.GetJsonSerializerOptions());
                    if (!string.IsNullOrEmpty(asset.Filename)) prefabFileNames.Add(asset.Filename);
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {file.FullName}. {ex.Message}.");
            }
        }

        return prefabFileNames;
    }

    private void AddAsset(Asset? asset)
    {
        //if (asset.ShapeName != null && asset.ShapeName.Equals("/levels/east_coast_rework/art/shapes/rails/track_straight_long.dae", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
        if (!string.IsNullOrEmpty(asset?.ShapeName))
        {
            var daeScanner = new DaeScanner(_levelPath, asset.ShapeName);
            asset.DaeExists = daeScanner.Exists();
            if (asset.DaeExists.HasValue && asset.DaeExists.Value)
            {
                asset.DaePath = daeScanner.ResolvedPath();
                asset.MaterialsDae = daeScanner.GetMaterials();
            }
        }

        _assets.Add(asset);
    }
}