using System.Text.Json;
using System.Text.Json.Nodes;

namespace FbxToDae;

/// <summary>
/// Writes BeamNG main.materials.json. Each call to Add() registers one material
/// by name. Call Save() once at the end to flush to disk. If the file already
/// exists, it is loaded first so we merge-without-overwriting-new entries.
/// Duplicate names are replaced (latest wins), since re-running the tool on
/// the same input should be idempotent.
/// </summary>
public sealed class MaterialsJsonWriter
{
    private readonly JsonObject _root;

    public MaterialsJsonWriter(string? existingFile = null)
    {
        if (!string.IsNullOrEmpty(existingFile) && File.Exists(existingFile))
        {
            var text = File.ReadAllText(existingFile);
            var docOpts = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };
            try
            {
                _root = JsonNode.Parse(text, nodeOptions: null, documentOptions: docOpts)?.AsObject()
                        ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"  WARN: existing {existingFile} is malformed ({ex.Message}); starting fresh.");
                _root = new JsonObject();
            }
        }
        else
        {
            _root = new JsonObject();
        }
    }

    public void Add(string materialName, string diffuseFileName, string? normalFileName)
    {
        // Preserve existing persistentId if we're updating an existing entry,
        // so re-runs are genuinely idempotent w.r.t. BeamNG's material tracking.
        string persistentId = Guid.NewGuid().ToString();
        if (_root[materialName] is JsonObject existing &&
            existing["persistentId"] is JsonValue existingId &&
            existingId.TryGetValue<string>(out var existingIdStr) &&
            !string.IsNullOrEmpty(existingIdStr))
        {
            persistentId = existingIdStr;
        }

        var stage0 = new JsonObject
        {
            ["baseColorMap"] = diffuseFileName,
        };
        if (!string.IsNullOrEmpty(normalFileName))
            stage0["normalMap"] = normalFileName;

        var material = new JsonObject
        {
            ["class"] = "Material",
            ["name"] = materialName,
            ["mapTo"] = materialName,
            ["internalName"] = materialName,
            ["persistentId"] = persistentId,
            ["version"] = 1.5,
            ["Stages"] = new JsonArray(stage0, new JsonObject(), new JsonObject(), new JsonObject()),
        };

        _root[materialName] = material;
    }

    public void Save(string outputFile)
    {
        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(outputFile, _root.ToJsonString(opts));
    }
}
