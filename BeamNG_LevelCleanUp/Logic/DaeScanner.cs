using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
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

        public DaeScanner(string levelPath, string daePath)
        {
            _daePath = daePath.Replace("/", "\\");
            _levelPath = levelPath;
            _resolvedDaePath = ResolvePath();
        }
        private string ResolvePath()
        {
            char delim = '\\';
            return string.Join(
                new string(delim, 1),
                _levelPath.Split(delim).Concat(_daePath.Split(delim)).Distinct().ToArray())
                .Replace("\\\\", "\\");
        }
        public bool Exists() {
            var fileInfo = new FileInfo(_resolvedDaePath);
            return fileInfo.Exists;
        }
        public string ResolvedPath()
        {
            return _resolvedDaePath;
        }
        public List<MaterialsDae> GetMaterials()
        {
            var retVal = new List<MaterialsDae>();
            //Create the XmlDocument.
            XmlDocument doc = new XmlDocument();
            doc.Load(_resolvedDaePath);

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
                    matDae.MaterialName = elem.GetAttribute("name");
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
