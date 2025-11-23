using Grille.BeamNG.SceneTree.Registry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Grille.BeamNG.SceneTree.Forest;
public class ForestGroup : ISceneTreeGroup
{
    public Dictionary<string, ForestItemCollection> Dict { get; }

    public bool IsEmpty => Dict.Count == 0;

    public string? Key => null;

    public ForestGroup()
    {
        Dict = new Dictionary<string, ForestItemCollection>(StringComparer.InvariantCultureIgnoreCase);
    }

    public const string FileExtension = ".forest4.json";

    private static (string DirName, string FileName, string Extension) SplitPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var file = Path.GetFileName(filePath);
        var split = file.Split('.', 2);
        return (dir, split[0], "." + split[1]);
    }

    public ForestItemCollection GetCollection(string name)
    {
        if (Dict.TryGetValue(name, out var items))
        {
            return items;
        }
        items = new ForestItemCollection();
        Dict.Add(name, items);
        return items;
    }

    public void LoadFile(string filePath, string name)
    {
        var items = GetCollection(name);
        items.Load(filePath);
    }

    public void LoadFile(string filePath)
    {
        var split = SplitPath(filePath);
        LoadFile(filePath, split.FileName);
    }

    public void LoadTree(string dirPath, ItemClassRegistry? registry)
    {
        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            var split = SplitPath(file);
            if (split.Extension.Equals(FileExtension, StringComparison.InvariantCultureIgnoreCase))
            {
                LoadFile(file, split.FileName);
            }
        }
    }

    public void LoadTree(string dirPath)
    {
        LoadTree(dirPath, ItemClassRegistry.Instance);
    }

    public void SaveTree(string dirPath, bool ignoreEmpty = true)
    {
        foreach (var pair in Dict)
        {
            var filePath = Path.Combine(dirPath, $"{pair.Key}{FileExtension}");
            pair.Value.Save(filePath);
        }
    }

    public void Add(ForestItem item)
    {
        var items = GetCollection(item.Type);
        items.Add(item);
    }

    public IEnumerable<ForestItem> Enumerate()
    {
        foreach (var items in Dict.Values)
        {
            foreach (var item in items)
            {
                yield return item;
            }
        }
    }
}
