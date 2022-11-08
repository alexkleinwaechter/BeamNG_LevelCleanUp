using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MaterialScanner
    {
        private string _matJsonPath { get; set; }
        private string _levelPath { get; set; }
        private List<MaterialJson> _materials = new List<MaterialJson>();
        private List<Asset> _assets = new List<Asset>();
        private List<string> _excludeFiles = new List<string>();
        internal MaterialScanner(string matJsonPath, string levelPath, List<MaterialJson> materials, List<Asset> assets, List<string> excludeFiles)
        {
            _matJsonPath = matJsonPath;
            _materials = materials;
            _levelPath = levelPath;
            _excludeFiles = excludeFiles;
            _assets = assets;
        }

        private string ResolvePath(string resourcePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return Path.Join(_levelPath, resourcePath.Replace(toReplaceDelim, delim));

            //char delim = '\\';
            //return string.Join(
            //    new string(delim, 1),
            //    _levelPath.Split(delim).Concat(_daePath.Split(delim)).Distinct().ToArray())
            //    .Replace("\\\\", "\\");
        }

        internal void ScanMaterialsJsonFile()
        {
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            var jsonObject = JsonDocument.Parse(File.ReadAllText(_matJsonPath), docOptions).RootElement;
            if (jsonObject.ValueKind != JsonValueKind.Undefined)
            {
                try
                {
                    foreach (var child in jsonObject.EnumerateObject())
                    {
                        try
                        {
                            var material = child.Value.Deserialize<MaterialJson>(BeamJsonOptions.Get());
                            if (material?.Stages != null)
                            {
                                var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                material.MaterialFiles = fileScanner.GetMaterialFiles();
                            }
                            if (material?.CubeFace != null && material?.CubeFace.Count > 0)
                            {
                                foreach (var cf in material.CubeFace)
                                {
                                    var fi = new FileInfo(ResolvePath(cf));
                                    material.MaterialFiles.Add(new MaterialFile
                                    {
                                        File = fi,
                                        MapType = "cubeFace",
                                        Missing = !fi.Exists
                                    });
                                }
                            }
                            if (!string.IsNullOrEmpty(material.Cubemap))
                            {
                                _assets.Add(new Asset
                                {
                                    Class = "Decal",
                                    Material = material.Cubemap,
                                });
                            }
                            if (!string.IsNullOrEmpty(material.InternalName) && string.IsNullOrEmpty(material.MapTo))
                            {
                                material.MapTo = material.InternalName;
                            }
                            else if (!string.IsNullOrEmpty(material.Name) && string.IsNullOrEmpty(material.MapTo))
                            {
                                material.MapTo = material.Name;
                            }
                            _materials.Add(material);

                            var temp = child.Value.EnumerateObject().ToList();
                            foreach (var item in temp)
                            {
                                if (item.Name.EndsWith("Tex")) {
                                    _excludeFiles.Add(ResolvePath(item.Value.GetString()));
                                }
                            }

                        }
                        catch (Exception ex)
                        {
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw;
                }
            }
        }
    }
}
