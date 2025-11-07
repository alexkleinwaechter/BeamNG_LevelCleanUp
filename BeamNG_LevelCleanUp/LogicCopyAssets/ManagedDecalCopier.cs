using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of managed decal data
    /// </summary>
    public class ManagedDecalCopier
    {
        public void Copy(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return;
            }

            Directory.CreateDirectory(item.TargetPath);
            var targetJsonPath = Path.Join(item.TargetPath, "managedDecalData.json");
            var targetJsonFile = new FileInfo(targetJsonPath);

            if (!targetJsonFile.Exists)
            {
                CreateNewManagedDecalFile(targetJsonFile, item.DecalData);
            }
            else
            {
                UpdateExistingManagedDecalFile(targetJsonFile, item.DecalData);
            }
        }

        private void CreateNewManagedDecalFile(FileInfo targetJsonFile, ManagedDecalData decalData)
        {
            var jsonObject = new JsonObject(
                new[]
     {
    KeyValuePair.Create<string, JsonNode?>(
             decalData.Name,
          JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(decalData, BeamJsonOptions.GetJsonSerializerOptions()))
      ),
     }
       );
            File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }

        private void UpdateExistingManagedDecalFile(FileInfo targetJsonFile, ManagedDecalData decalData)
        {
            var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

            if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == decalData.Name))
            {
                targetJsonNode.AsObject().Add(
                               KeyValuePair.Create<string, JsonNode?>(
                          decalData.Name,
             JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(decalData, BeamJsonOptions.GetJsonSerializerOptions()))
              )
                );
            }

            File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }
    }
}
