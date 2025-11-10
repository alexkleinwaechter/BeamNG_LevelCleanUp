using System.Xml;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Logic;

public class DaeScanner
{
    private readonly string _daePath;
    private readonly string _levelPath;
    private readonly FileInfo _resolvedDaeFile;
    private readonly string _resolvedDaePath;

    public DaeScanner(string levelPath, string daePath, bool fullDaePathProvided = false)
    {
        _daePath = daePath.Replace("/", "\\");
        _levelPath = levelPath;
        _resolvedDaePath = fullDaePathProvided ? _daePath : PathResolver.ResolvePath(_levelPath, _daePath, false);
        _resolvedDaeFile = new FileInfo(_resolvedDaePath);
    }

    public bool Exists()
    {
        return _resolvedDaeFile.Exists;
    }

    public bool IsCdae()
    {
        return _resolvedDaeFile.Extension.Equals(".cdae", StringComparison.OrdinalIgnoreCase);
    }

    public string ResolvedPath()
    {
        return _resolvedDaePath;
    }

    public List<MaterialsDae> GetMaterials()
    {
        var path = IsCdae() ? Path.ChangeExtension(_resolvedDaePath, ".dae") : _resolvedDaePath;

        var retVal = new List<MaterialsDae>();
        //Create the XmlDocument.
        var doc = new XmlDocument();
        try
        {
            if (!new FileInfo(path).Exists) throw new Exception($"File not found and cdae can't be scanned: {path}");
            doc.Load(path);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Collada format error in {_resolvedDaeFile}. Exception:{ex.Message}");
        }

        //Display all the book titles.
        var elemList = doc.GetElementsByTagName("material");
        for (var i = 0; i < elemList.Count; i++)
        {
            var matDae = new MaterialsDae();
            var elem = (XmlElement)elemList[i];
            if (elem.HasAttribute("id"))
            {
                matDae.MaterialId = elem.GetAttribute("id");
                matDae.DaeLocation = _resolvedDaePath;
            }

            if (elem.HasAttribute("name"))
            {
                var nameParts = elem.GetAttribute("name").Split(" ");
                matDae.MaterialName = nameParts.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(matDae.MaterialId)) retVal.Add(matDae);
        }

        return retVal;
    }
}