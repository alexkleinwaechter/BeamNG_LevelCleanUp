using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of general material files and their textures
    /// </summary>
    public class MaterialCopier
    {
        private readonly PathConverter _pathConverter;
        private readonly FileCopyHandler _fileCopyHandler;

        public MaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler)
        {
            _pathConverter = pathConverter;
            _fileCopyHandler = fileCopyHandler;
        }

        public bool Copy(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return true;
            }

            Directory.CreateDirectory(item.TargetPath);

            foreach (var material in item.Materials)
            {
                if (!CopyMaterial(material, item.TargetPath))
                {
                    return false;
                }
            }

            return true;
        }

        private bool CopyMaterial(MaterialJson material, string targetPath)
        {
            try
            {
                var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                var sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);

                var sourceMaterialNode = sourceJsonNode.AsObject()
                 .FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);

                if (sourceMaterialNode.Value == null)
                {
                    return true;
                }

                var toText = sourceMaterialNode.Value.ToJsonString();
                var targetJsonPath = Path.Join(targetPath, Path.GetFileName(material.MatJsonFileLocation));
                var targetJsonFile = new FileInfo(targetJsonPath);

                // Copy material files and update paths
                toText = CopyMaterialFilesAndUpdatePaths(material, toText);

                // Write or update the JSON file
                WriteMaterialJson(targetJsonFile, material.Name, toText);

                return true;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
           $"materials.json {material.MatJsonFileLocation} can't be parsed. Exception:{ex.Message}");
                return false;
            }
        }

        private string CopyMaterialFilesAndUpdatePaths(MaterialJson material, string materialJson)
        {
            foreach (var matFile in material.MaterialFiles)
            {
                var targetFullName = _pathConverter.GetTargetFileName(matFile.File.FullName);
                if (string.IsNullOrEmpty(targetFullName))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFullName));
                    _fileCopyHandler.CopyFile(matFile.File.FullName, targetFullName);
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Filepath error for material {material.Name}. Exception:{ex.Message}");
                }

                materialJson = materialJson.Replace(
              _pathConverter.GetBeamNgJsonFileName(matFile.File.FullName),
                _pathConverter.GetBeamNgJsonFileName(targetFullName),
            StringComparison.OrdinalIgnoreCase
              );
            }

            return materialJson;
        }

        private void WriteMaterialJson(FileInfo targetJsonFile, string materialName, string materialJson)
        {
            if (!targetJsonFile.Exists)
            {
                var jsonObject = new JsonObject(
          new[]
              {
   KeyValuePair.Create<string, JsonNode?>(materialName, JsonNode.Parse(materialJson)),
                  }
                 );
                File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            else
            {
                var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

                if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == materialName))
                {
                    targetJsonNode.AsObject().Add(
                      KeyValuePair.Create<string, JsonNode?>(materialName, JsonNode.Parse(materialJson))
                  );
                }

                File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
        }
    }
}
