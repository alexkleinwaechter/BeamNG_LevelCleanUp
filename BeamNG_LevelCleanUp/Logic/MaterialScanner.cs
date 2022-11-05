using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MaterialScanner
    {
        private string _matJsonPath { get; set; }
        private string _levelPath { get; set; }
        private List<MaterialJson> _materials = new List<MaterialJson>();
        internal MaterialScanner(string matJsonPath, string levelPath, List<MaterialJson> materials)
        {
            _matJsonPath = matJsonPath;
            _materials = materials;
            _levelPath = levelPath;
        }


        internal async void ScanMaterialsJsonFile()
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
                            if (material?.Stages != null) {
                                var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                material.MaterialFiles = fileScanner.GetMaterialFiles();
                            }
                            _materials.Add(material);
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }
                    }
                    //if (!string.IsNullOrEmpty(asset?.ShapeName))
                    //{
                    //    var daeScanner = new DaeScanner(_levelPath, asset.ShapeName);
                    //    asset.MaterialsDae = daeScanner.GetMaterials();
                    //}

                }
                catch (Exception ex)
                {
                    throw;
                }
            }
        }
    }
}
