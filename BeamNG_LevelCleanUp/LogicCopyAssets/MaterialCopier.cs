using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of general material files and their textures
/// </summary>
public class MaterialCopier
{
    private readonly FileCopyHandler _fileCopyHandler;
    private readonly PathConverter _pathConverter;

    public MaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler)
    {
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
    }

    public bool Copy(CopyAsset item)
    {
        return Copy(item, null);
    }

    public bool Copy(CopyAsset item, string newMaterialName)
    {
        if (item.TargetPath == null) return true;

        Directory.CreateDirectory(item.TargetPath);

        foreach (var material in item.Materials)
            if (!CopyMaterial(material, item.TargetPath, newMaterialName))
                return false;

        return true;
    }

    private bool CopyMaterial(MaterialJson material, string targetPath, string newMaterialName = null)
    {
        try
        {
            var jsonString = File.ReadAllText(material.MatJsonFileLocation);
            var sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);

            var sourceMaterialNode = sourceJsonNode.AsObject()
                .FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);

            if (sourceMaterialNode.Value == null) return true;

            // Parse the material JSON as JsonNode to update properties
            var materialNode = JsonNode.Parse(sourceMaterialNode.Value.ToJsonString());
            if (materialNode == null) return true;

            // Generate new GUID for persistentId
            materialNode["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();

            // Update name if newMaterialName is provided
            string materialKey;
            string materialNameToWrite;

            if (!string.IsNullOrEmpty(newMaterialName))
            {
                materialNode["name"] = newMaterialName;
                materialKey = newMaterialName;
                materialNameToWrite = newMaterialName;
            }
            else
            {
                materialKey = material.Name;
                materialNameToWrite = material.Name;
            }

            var toText = materialNode.ToJsonString();
            var targetJsonPath = Path.Join(targetPath, Path.GetFileName(material.MatJsonFileLocation));
            var targetJsonFile = new FileInfo(targetJsonPath);

            // Copy material files and update paths
            toText = CopyMaterialFilesAndUpdatePaths(material, toText);

            // Write or update the JSON file
            WriteMaterialJson(targetJsonFile, materialKey, materialNameToWrite, toText);

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
            if (string.IsNullOrEmpty(targetFullName)) continue;

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
                _pathConverter.GetBeamNgJsonPathOrFileName(matFile.File.FullName),
                _pathConverter.GetBeamNgJsonPathOrFileName(targetFullName),
                StringComparison.OrdinalIgnoreCase
            );
        }

        return materialJson;
    }

    private void WriteMaterialJson(FileInfo targetJsonFile, string materialKey, string materialName,
        string materialJson)
    {
        if (!targetJsonFile.Exists)
        {
            var jsonObject = new JsonObject(
                new[]
                {
                    KeyValuePair.Create<string, JsonNode?>(materialKey, JsonNode.Parse(materialJson))
                }
            );
            File.WriteAllText(targetJsonFile.FullName,
                jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }
        else
        {
            var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

            if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == materialName))
                targetJsonNode.AsObject().Add(
                    KeyValuePair.Create<string, JsonNode?>(materialKey, JsonNode.Parse(materialJson))
                );

            File.WriteAllText(targetJsonFile.FullName,
                targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }
    }
}