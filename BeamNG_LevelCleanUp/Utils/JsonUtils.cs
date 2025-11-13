using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using JsonRepairUtils;

namespace BeamNG_LevelCleanUp.Utils;

public static class JsonUtils
{
    public static JsonDocument GetValidJsonDocumentFromFilePath(string filePath)
    {
        var docOptions = BeamJsonOptions.GetJsonDocumentOptions();

        JsonDocument jsonObject;
        var jsonString = File.ReadAllText(filePath);
        try
        {
            jsonObject = JsonDocument.Parse(jsonString, docOptions);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Try to repair jsonfile {filePath} because of error: {ex.Message}");
            var jsonRepair = new JsonRepair();
            // dirty: westcoast dealerships.facilities.json file has a ,, in it
            jsonString = jsonRepair.Repair(jsonString.Replace(",,", ","));
            jsonObject = JsonDocument.Parse(jsonString, docOptions);
        }

        var hasDuplicateKeys = HasDuplicateKeys(jsonObject.RootElement, filePath);
        if (hasDuplicateKeys)
            jsonObject = JsonDocument.Parse(RemoveDuplicateKeys(jsonObject.RootElement).GetRawText(), docOptions);
        return jsonObject;
    }

    public static JsonDocument GetValidJsonDocumentFromString(string jsonString, string filePathForLog)
    {
        var docOptions = BeamJsonOptions.GetJsonDocumentOptions();

        JsonDocument jsonObject;
        try
        {
            jsonObject = JsonDocument.Parse(jsonString, docOptions);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Try to repair jsonfile {filePathForLog} because of error: {ex.Message}");
            var jsonRepair = new JsonRepair();
            jsonString = jsonRepair.Repair(jsonString);
            jsonObject = JsonDocument.Parse(jsonString, docOptions);
        }

        var hasDuplicateKeys = HasDuplicateKeys(jsonObject.RootElement, filePathForLog);
        if (hasDuplicateKeys)
            jsonObject = JsonDocument.Parse(RemoveDuplicateKeys(jsonObject.RootElement).GetRawText(), docOptions);
        return jsonObject;
    }

    public static JsonNode GetValidJsonNodeFromFilePath(string filePath)
    {
        var docOptions = BeamJsonOptions.GetJsonDocumentOptions();

        JsonNode jsonNode;
        var jsonString = File.ReadAllText(filePath);
        try
        {
            jsonNode = JsonNode.Parse(jsonString, null, docOptions);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Try to repair jsonfile {filePath} because of error: {ex.Message}");
            var jsonRepair = new JsonRepair();
            jsonString = jsonRepair.Repair(jsonString);
            jsonNode = JsonNode.Parse(jsonString, null, docOptions);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"jsonfile {filePath} sucessfully repaired.");
        }

        var hasDuplicateKeys = JsonNodeHasDuplicateKeys(jsonNode, filePath);
        if (hasDuplicateKeys) jsonNode = RemoveDuplicateKeysFromJsonNode(jsonNode);
        return jsonNode;
    }


    public static JsonNode GetValidJsonNodeFromString(string jsonString, string filePathForLog)
    {
        var docOptions = BeamJsonOptions.GetJsonDocumentOptions();

        JsonNode jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(jsonString, null, docOptions);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Try to repair jsonfile {filePathForLog} because of error: {ex.Message}");
            var jsonRepair = new JsonRepair();
            jsonString = jsonRepair.Repair(jsonString);
            jsonNode = JsonNode.Parse(jsonString, null, docOptions);
        }

        var hasDuplicateKeys = JsonNodeHasDuplicateKeys(jsonNode, filePathForLog);
        if (hasDuplicateKeys) jsonNode = RemoveDuplicateKeysFromJsonNode(jsonNode);

        return jsonNode;
    }

    public static bool HasDuplicateKeys(JsonElement jsonElement, string fileName)
    {
        if (jsonElement.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();

            foreach (var property in jsonElement.EnumerateObject())
            {
                if (keys.Contains(property.Name))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Removed duplicate key {property.Name} from {fileName}");
                    return true;
                }

                keys.Add(property.Name);

                if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object)
                    if (HasDuplicateKeys(property.Value, fileName))
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Removed duplicate key {property.Name} from {fileName}");
                        return true;
                    }
            }
        }
        else if (jsonElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jsonElement.EnumerateArray())
                if (HasDuplicateKeys(item, fileName))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Removed duplicate key {item.ToString()} from {fileName}");
                    return true;
                }
        }

        return false;
    }

    public static bool JsonNodeHasDuplicateKeys(JsonNode jsonNode, string fileName)
    {
        // Convert JsonNode to JsonElement
        var jsonElement = JsonDocument.Parse(jsonNode.ToJsonString()).RootElement;
        return HasDuplicateKeys(jsonElement, fileName);
    }

    public static JsonNode RemoveDuplicateKeysFromJsonNode(JsonNode jsonNode)
    {
        // Convert JsonNode to JsonElement
        var jsonElement = JsonDocument.Parse(jsonNode.ToJsonString()).RootElement;
        jsonElement = RemoveDuplicateKeys(jsonElement);
        // Convert JsonElement to JsonNode
        var jsonString = JsonSerializer.Serialize(jsonElement);
        return JsonNode.Parse(jsonString);
    }

    public static JsonElement RemoveDuplicateKeys(JsonElement jsonElement)
    {
        if (jsonElement.ValueKind == JsonValueKind.Object)
        {
            var jsonObject = new Dictionary<string, JsonElement>();

            foreach (var property in jsonElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object)
                {
                    jsonObject[property.Name] = RemoveDuplicateKeys(property.Value);
                }
                else
                {
                    if (!jsonObject.ContainsKey(property.Name)) jsonObject[property.Name] = property.Value;
                }

            return JsonDocument.Parse(JsonSerializer.Serialize(jsonObject)).RootElement;
        }

        if (jsonElement.ValueKind == JsonValueKind.Array)
        {
            var jsonArray = new List<JsonElement>();

            foreach (var item in jsonElement.EnumerateArray()) jsonArray.Add(RemoveDuplicateKeys(item));

            return JsonDocument.Parse(JsonSerializer.Serialize(jsonArray)).RootElement;
        }

        return jsonElement;
    }
}