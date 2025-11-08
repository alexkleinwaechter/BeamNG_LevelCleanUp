using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of GroundCover objects with material and DAE dependencies
    /// Copies entire groundcovers and suffixes all layer names
    /// </summary>
    public class GroundCoverCopier
    {
        private readonly PathConverter _pathConverter;
        private readonly FileCopyHandler _fileCopyHandler;
        private readonly MaterialCopier _materialCopier;
        private readonly DaeCopier _daeCopier;
        private readonly string _levelNameCopyFrom;
        private readonly string _namePath;
        private readonly string _targetLevelName;
        private List<string> _allGroundCoverJsonLines;
        private List<MaterialJson> _materialsJsonCopy;

        // Track which groundcovers to copy (by name)
        private readonly HashSet<string> _groundCoversToCopy;

        public GroundCoverCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler,
         MaterialCopier materialCopier, DaeCopier daeCopier, string levelNameCopyFrom, string namePath)
        {
            _pathConverter = pathConverter;
   _fileCopyHandler = fileCopyHandler;
            _materialCopier = materialCopier;
            _daeCopier = daeCopier;
         _levelNameCopyFrom = levelNameCopyFrom;
         _namePath = namePath;
            _targetLevelName = Path.GetFileName(namePath);
            _allGroundCoverJsonLines = new List<string>();
    _groundCoversToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
/// Loads scanned groundcover JSON lines from BeamFileReader
        /// </summary>
        public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
  {
            _allGroundCoverJsonLines = groundCoverJsonLines ?? new List<string>();
        }

        /// <summary>
        /// Loads scanned materials from BeamFileReader for material lookup
        /// </summary>
        public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
        {
    _materialsJsonCopy = materialsJsonCopy ?? new List<MaterialJson>();
        }

    /// <summary>
        /// PHASE 1: Collect groundcovers that reference the given terrain materials
        /// Marks entire groundcovers for copying if any of their Types reference the terrain materials
  /// </summary>
        public void CollectGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
 {
   if (!_allGroundCoverJsonLines.Any())
            {
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

            int newGroundCovers = 0;

            foreach (var jsonLine in _allGroundCoverJsonLines)
            {
    try
    {
      var jsonNode = JsonNode.Parse(jsonLine);
         if (jsonNode == null) continue;

            var groundCoverName = jsonNode["name"]?.GetValue<string>();
         if (string.IsNullOrEmpty(groundCoverName)) continue;

             // Skip if already marked
                 if (_groundCoversToCopy.Contains(groundCoverName)) continue;

   var typesArray = jsonNode["Types"] as JsonArray;
    if (typesArray == null) continue;

   // Check if ANY Type references our terrain materials
             bool hasMatchingLayer = false;
         foreach (var typeNode in typesArray)
    {
       if (typeNode == null) continue;

         var layer = typeNode["layer"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(layer) && terrainInternalNames.Contains(layer))
        {
              hasMatchingLayer = true;
             break;
}
       }

      if (hasMatchingLayer)
          {
          _groundCoversToCopy.Add(groundCoverName);
    newGroundCovers++;
}
     }
                catch (Exception ex)
{
      PubSubChannel.SendMessage(PubSubMessageType.Warning,
        $"Error collecting groundcover: {ex.Message}");
             }
  }

            if (newGroundCovers > 0)
       {
    PubSubChannel.SendMessage(PubSubMessageType.Info,
  $"Collected {newGroundCovers} groundcover(s) for copying");
          }
        }

        /// <summary>
        /// PHASE 2: Write all collected groundcovers to the target file
        /// Copies entire groundcovers with all Types, suffixing all layer names
      /// </summary>
        public void WriteAllGroundCovers()
        {
      if (!_groundCoversToCopy.Any())
            {
   return;
    }

    PubSubChannel.SendMessage(PubSubMessageType.Info,
  $"Copying {_groundCoversToCopy.Count} groundcover(s)...");

      var targetDir = Path.Join(_namePath, "main", "MissionGroup", "Level_object", "vegetation");
        Directory.CreateDirectory(targetDir);

            var targetFilePath = Path.Join(targetDir, "items.level.json");
            var targetFile = new FileInfo(targetFilePath);

            // Load existing groundcovers from target
      var existingGroundCovers = LoadExistingGroundCovers(targetFile);

            int created = 0;
    int merged = 0;

         foreach (var groundCoverName in _groundCoversToCopy)
            {
       try
         {
        // Find the original groundcover JSON line
               var originalJsonLine = _allGroundCoverJsonLines
         .Select(line => JsonNode.Parse(line))
            .FirstOrDefault(node => node?["name"]?.GetValue<string>()
       ?.Equals(groundCoverName, StringComparison.OrdinalIgnoreCase) == true);

        if (originalJsonLine == null) continue;

             var newName = $"{groundCoverName}_{_levelNameCopyFrom}";

   // Copy dependencies (materials and DAE files)
CopyGroundCoverDependencies(originalJsonLine, groundCoverName);

      // Create the final groundcover with all Types suffixed
  var finalGroundCover = BuildFinalGroundCover(originalJsonLine, groundCoverName);

            if (existingGroundCovers.ContainsKey(newName))
         {
// Replace existing
    existingGroundCovers[newName] = finalGroundCover;
         merged++;
  }
          else
         {
             // Add new
  existingGroundCovers[newName] = finalGroundCover;
         created++;
   }
           }
        catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error,
        $"Error writing groundcover '{groundCoverName}': {ex.Message}");
        }
            }

     // Write all groundcovers to file
            WriteAllGroundCoversToFile(targetFile, existingGroundCovers.Values);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
      $"Groundcover copy complete: {created} created, {merged} updated");

          // Clear for next operation
            _groundCoversToCopy.Clear();
        }

      /// <summary>
      /// Builds the final groundcover JSON with all Types and suffixed layer names
  /// </summary>
        private JsonNode BuildFinalGroundCover(JsonNode originalGroundCover, string originalName)
        {
        // Clone the original
        var result = JsonNode.Parse(originalGroundCover.ToJsonString());

         // Update name with suffix
            result["name"] = $"{originalName}_{_levelNameCopyFrom}";

            // Update persistentId with new GUID
            result["persistentId"] = Guid.NewGuid().ToString();

     // Update all Types: suffix layer names and update shapeFilename paths
         var typesArray = result["Types"] as JsonArray;
          if (typesArray != null)
      {
           foreach (var typeNode in typesArray)
         {
         if (typeNode == null) continue;

            // Suffix layer name (even if material not copied - harmless)
         var layer = typeNode["layer"]?.GetValue<string>();
 if (!string.IsNullOrEmpty(layer))
    {
    typeNode["layer"] = $"{layer}_{_levelNameCopyFrom}";
  }

      // Update shapeFilename path
   var shapeFilename = typeNode["shapeFilename"]?.GetValue<string>();
             if (!string.IsNullOrEmpty(shapeFilename))
       {
        var fileName = Path.GetFileName(shapeFilename);
      var newPath = $"/levels/{_targetLevelName}/art/shapes/groundcover/MT_{_levelNameCopyFrom}/{fileName}";
 typeNode["shapeFilename"] = newPath;
          }
    }
            }

            return result;
        }

        /// <summary>
        /// Loads existing groundcovers from the target file
        /// </summary>
        private Dictionary<string, JsonNode> LoadExistingGroundCovers(FileInfo targetFile)
        {
            var result = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

     if (!targetFile.Exists)
            {
       return result;
  }

            try
      {
       var lines = File.ReadAllLines(targetFile.FullName);
     foreach (var line in lines)
        {
       if (string.IsNullOrWhiteSpace(line)) continue;

   try
         {
   var jsonNode = JsonNode.Parse(line);
        if (jsonNode == null) continue;

       var name = jsonNode["name"]?.GetValue<string>();
          if (!string.IsNullOrEmpty(name))
              {
      result[name] = jsonNode;
     }
   }
        catch
      {
            // Skip malformed lines
           }
        }
     }
  catch (Exception ex)
      {
     PubSubChannel.SendMessage(PubSubMessageType.Warning,
$"Error loading existing groundcovers: {ex.Message}");
            }

  return result;
    }

        /// <summary>
        /// Writes all groundcovers to the target file
        /// </summary>
        private void WriteAllGroundCoversToFile(FileInfo targetFile, IEnumerable<JsonNode> groundCovers)
    {
     try
   {
    var lines = groundCovers
       .Select(gc => gc.ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions()))
  .ToList();

       File.WriteAllLines(targetFile.FullName, lines);
            }
            catch (Exception ex)
       {
   PubSubChannel.SendMessage(PubSubMessageType.Error,
              $"Error writing groundcovers to file: {ex.Message}");
        throw;
     }
}

        /// <summary>
        /// Copies materials and DAE files referenced by a groundcover
        /// </summary>
        private void CopyGroundCoverDependencies(JsonNode groundCoverNode, string groundCoverName)
        {
      // Copy groundcover's material (if it has one)
            var materialName = groundCoverNode["material"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(materialName) && _materialsJsonCopy != null)
        {
    CopyGroundCoverMaterial(materialName, groundCoverName);
   }

            // Copy DAE files from all Types
          var typesArray = groundCoverNode["Types"] as JsonArray;
     if (typesArray != null)
            {
   var daeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

             foreach (var typeNode in typesArray)
   {
 if (typeNode == null) continue;

         var shapeFilename = typeNode["shapeFilename"]?.GetValue<string>();
    if (!string.IsNullOrEmpty(shapeFilename))
{
         daeFiles.Add(shapeFilename);
   }
             }

   foreach (var daeFile in daeFiles)
        {
             CopyGroundCoverDaeFile(daeFile);
     }
       }
        }

     /// <summary>
        /// Copies a material referenced by a groundcover
        /// </summary>
  private void CopyGroundCoverMaterial(string materialName, string groundCoverName)
        {
    var material = _materialsJsonCopy.FirstOrDefault(m =>
                m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

        if (material != null)
 {
          var materialCopyAsset = new CopyAsset
       {
             CopyAssetType = CopyAssetType.Terrain,
      Name = material.Name,
          Materials = new List<MaterialJson> { material },
  TargetPath = Path.Join(_namePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
       };

          _materialCopier.Copy(materialCopyAsset);
         }
        }

        /// <summary>
  /// Copies a DAE file referenced by groundcover
     /// </summary>
        private void CopyGroundCoverDaeFile(string daeFilePath)
 {
         try
   {
    var fileName = Path.GetFileName(daeFilePath);
                var sourcePath = Logic.PathResolver.ResolvePath(Logic.PathResolver.LevelPathCopyFrom, daeFilePath, true);
var targetPath = Path.Join(_namePath, "art", "shapes", "groundcover", $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}");
                Directory.CreateDirectory(targetPath);

     var targetFilePath = Path.Join(targetPath, fileName);

 _fileCopyHandler.CopyFile(sourcePath, targetFilePath);
            }
            catch (Exception ex)
        {
           PubSubChannel.SendMessage(PubSubMessageType.Warning,
       $"Failed to copy DAE file {daeFilePath}: {ex.Message}");
 }
      }

        #region Backward Compatibility Methods (Deprecated)

        /// <summary>
        /// DEPRECATED: Use CollectGroundCoversForTerrainMaterials() + WriteAllGroundCovers() instead
        /// </summary>
     [Obsolete("Use CollectGroundCoversForTerrainMaterials() followed by WriteAllGroundCovers() instead")]
    public void CopyGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
        {
            CollectGroundCoversForTerrainMaterials(terrainMaterials);
            WriteAllGroundCovers();
 }

  #endregion
    }
}
