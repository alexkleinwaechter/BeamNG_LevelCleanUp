﻿using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class TerrainScanner
    {
        private string _terrainPath { get; set; }
        private string _levelPath { get; set; }
        private List<Asset> _assets = new List<Asset>();
        private List<MaterialJson> _materialJson = new List<MaterialJson>();
        private List<string> _excludeFiles = new List<string>();
        internal TerrainScanner(string terrainPath, string levelPath, List<Asset> assets, List<MaterialJson> materialJson, List<string> excludeFiles)
        {

            _terrainPath = terrainPath;
            _assets = assets;
            _levelPath = levelPath;
            _excludeFiles = excludeFiles;
            _materialJson = materialJson;
        }
        public void ScanTerrain()
        {
            JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
            PubSubChannel.SendMessage(false, $"Scan Terrainfile {_terrainPath}");
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(_terrainPath), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    if (jsonObject.RootElement.TryGetProperty("materials", out JsonElement materials))
                    {
                        var materialList = materials.Deserialize<List<string>>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (materialList != null)
                        {
                            foreach (var name in materialList.Distinct())
                            {
                                _assets.Add(new Asset
                                {
                                    Class = "Terrain",
                                    Material = name,
                                });
                                var matJson = _materialJson.Where(x => x.InternalName == name);
                                foreach (var item in matJson)
                                {
                                    _assets.Add(new Asset
                                    {
                                        Class = "Terrain",
                                        Material = item.Name,
                                    });
                                }
                            }
                        }
                    }
                    if (jsonObject.RootElement.TryGetProperty("datafile", out JsonElement dataFile))
                    {
                        var result = dataFile.Deserialize<string>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (result != null)
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, result, false));
                        }
                    }
                    if (jsonObject.RootElement.TryGetProperty("heightmapImage", out JsonElement heightmapImage))
                    {
                        var result = heightmapImage.Deserialize<string>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (result != null)
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, result, false));
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Stopped! Error {_terrainPath}. {ex.Message}");
            }
        }
    }
}
