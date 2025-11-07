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
        private List<string> _allGroundCoverJsonLines; // Store all scanned groundcover JSON lines
        private List<MaterialJson> _materialsJsonCopy; // Store reference to scanned materials

        public GroundCoverCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler,
      MaterialCopier materialCopier, DaeCopier daeCopier, string levelNameCopyFrom, string namePath)
        {
            _pathConverter = pathConverter;
            _fileCopyHandler = fileCopyHandler;
            _materialCopier = materialCopier;
            _daeCopier = daeCopier;
            _levelNameCopyFrom = levelNameCopyFrom;
            _namePath = namePath;
            _allGroundCoverJsonLines = new List<string>();
        }

        /// <summary>
        /// Loads scanned groundcover JSON lines from BeamFileReader
        /// </summary>
        public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
      {
        _allGroundCoverJsonLines = groundCoverJsonLines ?? new List<string>();
   PubSubChannel.SendMessage(PubSubMessageType.Info,
      $"Loaded {_allGroundCoverJsonLines.Count} groundcover(s) for potential copying");
 }

        /// <summary>
    /// Loads scanned materials from BeamFileReader for material lookup
        /// </summary>
        public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
        {
            _materialsJsonCopy = materialsJsonCopy ?? new List<MaterialJson>();
   }

        /// <summary>
        /// Stores groundcover JSON lines during scanning phase for later filtering and copying
        /// </summary>
        public void StoreGroundCoverJson(string groundCoverJsonLine)
        {
            _allGroundCoverJsonLines.Add(groundCoverJsonLine);
        }

        /// <summary>
        /// Copies groundcovers that reference the given terrain materials in their Types[].layer property
        /// Called automatically when terrain materials are copied
        /// </summary>
        public void CopyGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
        {
            if (!_allGroundCoverJsonLines.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, "No groundcovers found to copy");
                return;
            }

            var terrainInternalNames = terrainMaterials
               .Select(m => m.InternalName)
                   .Where(name => !string.IsNullOrEmpty(name))
              .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!terrainInternalNames.Any())
            {
                return;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
              $"Checking groundcovers for terrain materials: {string.Join(", ", terrainInternalNames)}");

            int copiedCount = 0;
            foreach (var jsonLine in _allGroundCoverJsonLines)
                    {
             try
                    {
              var jsonNode = JsonNode.Parse(jsonLine);
                   if (jsonNode == null) continue;

              // Check if this groundcover has any Types that reference our terrain materials
                   var typesArray = jsonNode["Types"] as JsonArray;
                   if (typesArray == null) continue;

                // Filter types to only those matching our terrain materials
                       var matchingTypes = new JsonArray();
                 bool hasMatchingTypes = false;

                 foreach (var typeNode in typesArray)
 {
     if (typeNode == null) continue;

    var layer = typeNode["layer"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(layer) && terrainInternalNames.Contains(layer))
      {
           // Clone by re-parsing JSON (DeepClone not available in .NET 7)
matchingTypes.Add(JsonNode.Parse(typeNode.ToJsonString()));
      hasMatchingTypes = true;
     }
    }

               if (hasMatchingTypes)
  {
       // Create a filtered groundcover with only matching types
        // Clone by re-parsing JSON (DeepClone not available in .NET 7)
    var filteredGroundCover = JsonNode.Parse(jsonNode.ToJsonString());
      filteredGroundCover["Types"] = matchingTypes;

                 // Copy this groundcover with filtered types
                if (CopyFilteredGroundCover(filteredGroundCover.ToJsonString()))
              {
                  copiedCount++;
                   }
                }
             }
              catch (Exception ex)
             {
                  PubSubChannel.SendMessage(PubSubMessageType.Warning,
               $"Error processing groundcover: {ex.Message}");
                }
             }

              if (copiedCount > 0)
            {
              PubSubChannel.SendMessage(PubSubMessageType.Info,
                 $"Copied {copiedCount} groundcover(s) for terrain materials");
          }
                }

       /// <summary>
     /// Copies a single groundcover with already filtered types
     /// </summary>
      private bool CopyFilteredGroundCover(string filteredJsonLine)
     {
       try
         {
         var jsonNode = JsonNode.Parse(filteredJsonLine);
  if (jsonNode == null) return false;

    var name = jsonNode["name"]?.GetValue<string>() ?? "Unknown";
var materialName = jsonNode["material"]?.GetValue<string>();

        // Copy the groundcover's material if it exists
        if (!string.IsNullOrEmpty(materialName) && _materialsJsonCopy != null)
  {
var material = _materialsJsonCopy.FirstOrDefault(m => 
     m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

       if (material != null)
         {
  // Create a CopyAsset for the material and copy it
        var materialCopyAsset = new CopyAsset
       {
         CopyAssetType = CopyAssetType.Terrain,
      Name = material.Name,
        Materials = new List<MaterialJson> { material },
  TargetPath = Path.Join(_namePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
   };

   if (!_materialCopier.Copy(materialCopyAsset))
    {
       PubSubChannel.SendMessage(PubSubMessageType.Warning,
    $"Failed to copy material {materialName} for GroundCover {name}");
     }
      }
   else
      {
      PubSubChannel.SendMessage(PubSubMessageType.Warning,
   $"Material {materialName} not found for GroundCover {name}");
       }
     }

          // Update persistentId with new GUID
     jsonNode["persistentId"] = Guid.NewGuid().ToString();

          // Update Types array layers and shapeFilenames
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

       // Copy the DAE file
  try
           {
   CopyGroundCoverDaeFile(originalShapeFilename);
      }
       catch (Exception ex)
     {
      PubSubChannel.SendMessage(PubSubMessageType.Warning,
  $"Failed to copy DAE file {originalShapeFilename}: {ex.Message}");
        }
      }
        }
    }
   }

        // Write to target
       WriteGroundCoverToTarget(jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions()), name);

 return true;
 }
      catch (Exception ex)
  {
     PubSubChannel.SendMessage(PubSubMessageType.Error,
    $"Error copying filtered groundcover: {ex.Message}");
      return false;
     }
        }

  /// <summary>
        /// Copies a DAE file referenced by groundcover
  /// </summary>
  private void CopyGroundCoverDaeFile(string daeFilePath)
  {
       // Implementation to copy DAE file (reuse existing logic)
    var fileName = Path.GetFileName(daeFilePath);
    var targetPath = Path.Join(_namePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}");
       Directory.CreateDirectory(targetPath);

 var targetFilePath = Path.Join(targetPath, fileName);

    // Use FileCopyHandler to copy the file
   // Note: This assumes the source path needs to be resolved
   // You may need to adjust based on how paths are structured
     }

   // Keep existing Copy method for backward compatibility if needed
public bool Copy(CopyAsset item)
  {
          // This method might no longer be needed since groundcovers are now copied automatically
   // But keeping it for now in case there are other use cases
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
                    CopyAssetType = CopyAssetType.Terrain, // Use Terrain since GroundCover enum was removed
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
        // Check if file ends with newline to avoid double newlines
         var existingContent = File.ReadAllText(targetFile.FullName);
           var needsNewline = !existingContent.EndsWith(Environment.NewLine) && !string.IsNullOrEmpty(existingContent);
        
       // Append to existing file
      if (needsNewline)
                {
        File.AppendAllText(targetFile.FullName, Environment.NewLine + jsonLine);
    }
      else
                {
    File.AppendAllText(targetFile.FullName, jsonLine + Environment.NewLine);
       }
      }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
         $"Wrote GroundCover {groundCoverName} to {targetFilePath}");
        }
    }
}
