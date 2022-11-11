using BeamNG_LevelCleanUp.Communication;
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

        public DaeScanner(string levelPath, string daePath, bool fullDaePathProvided = false)
        {
            _daePath = daePath.Replace("/", "\\");
            _levelPath = levelPath;
            _resolvedDaePath = fullDaePathProvided ? _daePath : ResolvePath();
        }
        private string ResolvePath()
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return Path.Join(_levelPath, _daePath.Replace(toReplaceDelim, delim));

            //char delim = '\\';
            //return string.Join(
            //    new string(delim, 1),
            //    _levelPath.Split(delim).Concat(_daePath.Split(delim)).Distinct().ToArray())
            //    .Replace("\\\\", "\\");
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
            try
            {
                doc.Load(_resolvedDaePath);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(true, $"Collada format error in {_resolvedDaePath}. Exception:{ex.Message}");
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
