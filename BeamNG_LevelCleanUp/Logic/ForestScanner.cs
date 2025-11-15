using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

public class ForestScanner
{
    private readonly List<Asset> _assets = new();
    private readonly List<FileInfo> _forestJsonFiles;
    private readonly List<FileInfo> _managedItemData;

    public ForestScanner(List<Asset> assets, List<FileInfo> forestJsonFiles, List<FileInfo> managedItemData,
        string levelPath)
    {
        _assets = assets;
        _forestJsonFiles = forestJsonFiles;
        _managedItemData = managedItemData;
        _levelPath = levelPath;
    }

    private string _missiongroupPath { get; set; }
    private string _levelPath { get; }
    private List<ForestInfo> _forestInfoFiles { get; } = new();
    private List<Tuple<string, string>> _forestTypeNames { get; } = new();
    private List<Tuple<string, string, string>> _shapeNames { get; } = new();

    public void ScanForest()
    {
        RetrieveUsedForestTypes();
        foreach (var file in _managedItemData)
            if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                GetShapeNamesJson(file);
            else
                GetShapeNamesCs(file);

        foreach (var shapeName in _shapeNames.Distinct())
        {
            //todo: daescanner full path in shapename
            var fullpath = AddAsset(new Asset
            {
                Class = "TSStatic",
                ShapeName = shapeName.Item1
            });
            _forestInfoFiles.Add(new ForestInfo
            {
                DaePath = fullpath,
                ForestTypeName = shapeName.Item2,
                FileOrigin = shapeName.Item3,
                UsedInFiles = new List<string>()
            });
        }

        foreach (var item in _forestInfoFiles)
            item.UsedInFiles = _forestTypeNames.Where(_ => _.Item1 == item.ForestTypeName)
                .Select(_ => _.Item2)
                .Distinct().ToList();
    }

    public List<Tuple<string, string>> GetUsedForestTypes()
    {
        return _forestTypeNames.Distinct().OrderBy(o => o.Item1).ToList();
    }

    public List<Tuple<string, string, string>> GetShapeNames()
    {
        return _shapeNames.Distinct().OrderBy(o => o.Item1).ToList();
    }

    public List<ForestInfo> GetForestInfo()
    {
        return _forestInfoFiles.OrderBy(o => o.DaePath).ToList();
    }

    private string AddAsset(Asset? asset)
    {
        //if (asset.ShapeName != null && asset.ShapeName.ToUpperInvariant().Contains("skyscraper_22".ToUpperInvariant())) Debugger.Break();
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
        return asset.DaePath;
    }

    private void RetrieveUsedForestTypes()
    {
        foreach (var file in _forestJsonFiles)
        foreach (var line in File.ReadAllLines(file.FullName))
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromString(line, file.FullName);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
            {
                var asset = jsonObject.RootElement.Deserialize<Forest>(BeamJsonOptions.GetJsonSerializerOptions());
                if (!string.IsNullOrEmpty(asset.type))
                    //PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Foresttype {asset.Type}", true);
                    _forestTypeNames.Add(Tuple.Create(asset.type, file.FullName));
            }
        }
    }

    internal void GetShapeNamesJson(FileInfo file)
    {
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(file.FullName);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                foreach (var managedForestData in jsonObject.RootElement.EnumerateObject())
                {
                    var forestData =
                        managedForestData.Value.Deserialize<ManagedForestData>(
                            BeamJsonOptions.GetJsonSerializerOptions());
                    var name = forestData.shapeFile;
                    if (name.StartsWith("./")) name = name.Remove(0, 2);
                    if (name.Count(c => c == '/') == 0) name = Path.Join(Path.GetDirectoryName(file.FullName), name);
                    _shapeNames.Add(Tuple.Create(name, forestData.internalName, file.FullName));
                }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error DecalScanner {file.FullName}. {ex.Message}");
        }
    }

    private void GetShapeNamesCs(FileInfo file)
    {
        foreach (var typeName in _forestTypeNames.Select(_ => _.Item1).Distinct())
        {
            var hit = false;
            foreach (var line in File.ReadLines(file.FullName))
            {
                var search = $"({typeName})";
                if (line.ToUpperInvariant().Contains(search.ToUpperInvariant()))
                    //if (line.Contains("FranklinDouglasTower15flr_var2", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
                    hit = true;
                if (hit && line.ToUpperInvariant().Contains("shapefile =", StringComparison.OrdinalIgnoreCase))
                {
                    var nameParts = line.Split('"');
                    if (nameParts.Length > 1)
                    {
                        var name = nameParts[1];
                        if (name.StartsWith("./")) name = name.Remove(0, 2);
                        if (name.Count(c => c == '/') == 0)
                            name = Path.Join(Path.GetDirectoryName(file.FullName), name);
                        _shapeNames.Add(Tuple.Create(name, typeName, file.FullName));
                        hit = false;
                    }
                }
            }
        }
    }
}