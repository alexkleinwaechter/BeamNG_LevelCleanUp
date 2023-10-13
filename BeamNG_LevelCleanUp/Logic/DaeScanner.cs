using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace BeamNG_LevelCleanUp.Logic
{
    public class DaeScanner
    {
        string _levelPath;
        string _daePath;
        string _resolvedDaePath;
        FileInfo _resolvedDaeFile;

        public DaeScanner(string levelPath, string daePath, bool fullDaePathProvided = false)
        {
            _daePath = daePath.Replace("/", "\\");
            _levelPath = levelPath;
            _resolvedDaePath = fullDaePathProvided ? _daePath : PathResolver.ResolvePath(_levelPath, _daePath, false);
            _resolvedDaeFile = new FileInfo(_resolvedDaePath);
        }
        public bool Exists() {
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
            XmlDocument doc = new XmlDocument();
            try
            {
                if (!new FileInfo(path).Exists)
                {
                    throw new Exception($"File not found and cdae can't be scanned: {path}");
                }
                doc.Load(path);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Collada format error in {_resolvedDaeFile}. Exception:{ex.Message}");
            }

            //Display all the book titles.
            XmlNodeList elemList = doc.GetElementsByTagName("material");
            for (int i = 0; i < elemList.Count; i++)
            {
                var matDae = new MaterialsDae();
                XmlElement elem = (XmlElement)elemList[i];
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
                if (!string.IsNullOrEmpty(matDae.MaterialId))
                {
                    retVal.Add(matDae);
                }
            }
            return retVal;
        }
    }
}
