using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of GroundCover objects with material and DAE dependencies
    /// </summary>
    public class GroundCoverCopier
    {
        private readonly PathConverter _pathConverter;
        private readonly FileCopyHandler _fileCopyHandler;
        private readonly MaterialCopier _materialCopier;
        private readonly DaeCopier _daeCopier;
        private readonly string _levelNameCopyFrom;
        private readonly string _namePath;

        public GroundCoverCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler,
      MaterialCopier materialCopier, DaeCopier daeCopier, string levelNameCopyFrom, string namePath)
        {
            _pathConverter = pathConverter;
            _fileCopyHandler = fileCopyHandler;
            _materialCopier = materialCopier;
            _daeCopier = daeCopier;
            _levelNameCopyFrom = levelNameCopyFrom;
            _namePath = namePath;
        }

        public bool Copy(CopyAsset item)
        {
            if (item.GroundCoverData == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                  $"GroundCover data is null for {item.Name}");
                return true;
            }

            try
            {
                // 1. Copy materials referenced by the GroundCover
                if (!CopyGroundCoverMaterials(item))
                {
                    return false;
                }

                // 2. Copy DAE files referenced in Types[].shapeFilename
                if (!CopyGroundCoverDaeFiles(item))
                {
                    return false;
                }

                // 3. Parse the original JSON line and update only necessary fields
                //    This preserves all properties, even ones we don't know about
                var updatedJson = UpdateGroundCoverJson(item);

                // 4. Write the updated JSON to target items.level.json
                WriteGroundCoverToTarget(updatedJson, item.Name);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Successfully copied GroundCover: {item.Name}");

                return true;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                  $"Error copying GroundCover {item.Name}: {ex.Message}");
                return false;
            }
        }

        private bool CopyGroundCoverMaterials(CopyAsset item)
        {
            if (item.Materials == null || !item.Materials.Any())
            {
                return true; // No materials to copy
            }

            try
            {
                // Use existing MaterialCopier to copy materials
                var materialCopyAsset = new CopyAsset
                {
                    CopyAssetType = CopyAssetType.GroundCover,
                    Name = item.Name,
                    Materials = item.Materials,
                    TargetPath = Path.Join(_namePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
                };

                return _materialCopier.Copy(materialCopyAsset);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                  $"Error copying materials for GroundCover {item.Name}: {ex.Message}");
                return false;
            }
        }

        private bool CopyGroundCoverDaeFiles(CopyAsset item)
        {
            if (item.GroundCoverData.Types == null)
            {
                return true;
            }

            try
            {
                var daeFiles = item.GroundCoverData.Types
                   .Where(t => !string.IsNullOrEmpty(t.ShapeFilename))
                  .Select(t => t.ShapeFilename)
                     .Distinct()
                       .ToList();

                foreach (var daeFile in daeFiles)
                {
                    // Create a CopyAsset for each DAE file
                    var daeCopyAsset = new CopyAsset
                    {
                        CopyAssetType = CopyAssetType.Dae,
                        Name = Path.GetFileName(daeFile),
                        DaeFilePath = daeFile,
                        MaterialsDae = item.MaterialsDae?.Where(m => m.DaeLocation == daeFile).ToList(),
                        TargetPath = Path.Join(_namePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
                    };

                    // Find materials for this DAE
                    if (daeCopyAsset.MaterialsDae != null)
                    {
                        daeCopyAsset.Materials = item.Materials
                       .Where(m => daeCopyAsset.MaterialsDae
                          .Any(dm => dm.MaterialName.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                                     .ToList();
                    }

                    if (!_daeCopier.Copy(daeCopyAsset))
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                       $"Failed to copy DAE file {daeFile} for GroundCover {item.Name}");
                        // Continue with other DAE files even if one fails
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
            $"Error copying DAE files for GroundCover {item.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates the GroundCover JSON preserving all original properties
        /// Only modifies: persistentId, Types[].layer, Types[].shapeFilename
        /// </summary>
        private string UpdateGroundCoverJson(CopyAsset item)
        {
            // Get the original JSON from SourceMaterialJsonPath (stored by scanner)
            // We need to re-read the original line to preserve everything
            var originalJson = item.SourceMaterialJsonPath; // This should contain the original JSON line

            // Parse as JsonNode to allow manipulation while preserving unknown properties
            var jsonNode = JsonNode.Parse(originalJson);

            if (jsonNode == null)
            {
                throw new Exception($"Failed to parse GroundCover JSON for {item.Name}");
            }

            // Update persistentId with new GUID
            jsonNode["persistentId"] = Guid.NewGuid().ToString();

            // Update Types array if it exists
            if (jsonNode["Types"] is JsonArray typesArray)
            {
                var levelName = Path.GetFileName(_namePath);

                foreach (var typeNode in typesArray)
                {
                    if (typeNode == null) continue;

                    // Update layer name with suffix
                    if (typeNode["layer"] != null)
                    {
                        var originalLayer = typeNode["layer"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(originalLayer))
                        {
                            typeNode["layer"] = $"{originalLayer}_{_levelNameCopyFrom}";
                        }
                    }

                    // Update shapeFilename path
                    if (typeNode["shapeFilename"] != null)
                    {
                        var originalShapeFilename = typeNode["shapeFilename"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(originalShapeFilename))
                        {
                            var fileName = Path.GetFileName(originalShapeFilename);
                            var newPath = $"/levels/{levelName}/art/shapes/groundcover/{Constants.MappingToolsPrefix}{_levelNameCopyFrom}/{fileName}";
                            typeNode["shapeFilename"] = newPath;
                        }
                    }
                }
            }

            // Serialize back to one-line JSON format
            return jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions());
        }

        private void WriteGroundCoverToTarget(string jsonLine, string groundCoverName)
        {
            // Target path: /main/MissionGroup/Level_object/vegetation/items.level.json
            var targetDir = Path.Join(_namePath, "main", "MissionGroup", "Level_object", "vegetation");
            Directory.CreateDirectory(targetDir);

            var targetFilePath = Path.Join(targetDir, "items.level.json");
            var targetFile = new FileInfo(targetFilePath);

            if (!targetFile.Exists)
            {
                // Create new file with this entry
                File.WriteAllText(targetFile.FullName, jsonLine);
            }
            else
            {
                // Append to existing file
                File.AppendAllText(targetFile.FullName, Environment.NewLine + jsonLine);
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
         $"Wrote GroundCover {groundCoverName} to {targetFilePath}");
        }
    }
}
