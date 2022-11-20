﻿using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        internal void ScanMaterialsJsonFile()
        {
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(_matJsonPath), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var material = child.Value.Deserialize<MaterialJson>(BeamJsonOptions.Get());
                            //if (material.Name == "shawn2") Debugger.Break();
                            if (material?.Stages != null)
                            {
                                var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                material.MaterialFiles = fileScanner.GetMaterialFiles(material.Name);
                            }
                            else
                            {
                                var stage = child.Value.Deserialize<MaterialStage>(BeamJsonOptions.Get());
                                if (stage != null)
                                {
                                    material.Stages = new List<MaterialStage>();
                                    material.Stages.Add(stage);
                                    var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                    material.MaterialFiles = fileScanner.GetMaterialFiles(material.Name);
                                }
                            }
                            if (material?.CubeFace != null && material?.CubeFace.Count > 0)
                            {
                                foreach (var cf in material.CubeFace)
                                {
                                    var fi = new FileInfo(PathResolver.ResolvePath(_levelPath, cf, false));
                                    if (!fi.Exists)
                                    {
                                        fi = CheckMissingExtensions(fi);
                                    }
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
                            PubSubChannel.SendMessage(false, $"Read Material {material.Name}", true);
                            _materials.Add(material);

                            var temp = child.Value.EnumerateObject().ToList();
                            foreach (var item in temp)
                            {
                                if (item.Name.EndsWith("Tex"))
                                {
                                    _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, item.Value.GetString(), false));
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
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(true, $"Error {_matJsonPath}. {ex.Message}");
            }
        }

        internal FileInfo CheckMissingExtensions(FileInfo fileInfo)
        {

            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".dds");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".png");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpg");
                fileInfo = new FileInfo(ddsPath);
            }
            if (!fileInfo.Exists)
            {
                var ddsPath = Path.ChangeExtension(fileInfo.FullName, ".jpeg");
                fileInfo = new FileInfo(ddsPath);
            }
            return fileInfo;
        }
    }
}
