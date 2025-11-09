using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles replacing groundcover Types and materials while preserving target identity
    /// </summary>
    public class GroundCoverReplacer
    {
        private readonly GroundCoverDependencyHelper _dependencyHelper;
        private readonly string _namePath;
        private List<string> _allGroundCoverJsonLines;
        private Dictionary<string, JsonNode> _parsedGroundCovers;
        // Track which groundcovers to replace (by target material name -> source materials)
        private readonly Dictionary<string, List<MaterialJson>> _groundCoversToReplace;

        public GroundCoverReplacer(
         GroundCoverDependencyHelper dependencyHelper,
            string namePath)
        {
            _dependencyHelper = dependencyHelper;
            _namePath = namePath;
            _groundCoversToReplace = new Dictionary<string, List<MaterialJson>>();
            _parsedGroundCovers = new Dictionary<string, JsonNode>();
        }

        /// <summary>
        /// Loads scanned groundcover JSON lines from BeamFileReader
        /// </summary>
        public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
        {
            _allGroundCoverJsonLines = groundCoverJsonLines ?? new List<string>();
            ParseAllGroundCovers();
        }

        /// <summary>
        /// Loads scanned materials from BeamFileReader for material lookup
        /// </summary>
        public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
        {
            _dependencyHelper.LoadMaterialsJsonCopy(materialsJsonCopy);
        }

        /// <summary>
        /// Marks groundcovers for replacement based on terrain material replacement
        /// </summary>
        public void ReplaceGroundCoversForTerrainMaterial(string targetMaterialName, List<MaterialJson> sourceMaterials)
        {
            if (string.IsNullOrEmpty(targetMaterialName) || sourceMaterials == null || !sourceMaterials.Any())
            {
                return;
            }

            _groundCoversToReplace[targetMaterialName] = sourceMaterials;
        }

        /// <summary>
        /// Writes all marked groundcover replacements
        /// </summary>
        public void WriteAllGroundCoverReplacements()
        {
            if (!_groundCoversToReplace.Any())
            {
                return;
            }

            var targetFile = new FileInfo(Path.Join(_namePath, "main", "MissionGroup", "Level_object", "vegetation", "items.level.json"));
            if (!targetFile.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
             $"Target groundcover file not found at {targetFile.FullName}. Skipping groundcover replacement.");
                return;
            }

            // Load existing groundcovers from target
            var existingGroundCovers = LoadExistingGroundCovers(targetFile);
            var replacedGroundCovers = new List<JsonNode>();

            foreach (var kvp in _groundCoversToReplace)
            {
                var targetMaterialName = kvp.Key;
                var sourceMaterials = kvp.Value;

                // Find groundcovers in target that reference this material
                var targetGroundCoversToReplace = existingGroundCovers
            .Where(gc => gc.Value["material"]?.ToString() == targetMaterialName)
             .ToList();

                foreach (var targetGC in targetGroundCoversToReplace)
                {
                    var targetName = targetGC.Value["name"]?.ToString();
                    if (string.IsNullOrEmpty(targetName))
                    {
                        continue;
                    }

                    // Find corresponding source groundcover from parsed groundcovers
                    var sourceGroundCover = FindSourceGroundCoverForMaterial(sourceMaterials.First());
                    if (sourceGroundCover == null)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"No source groundcover found for material. Skipping groundcover '{targetName}'.");
                        continue;
                    }

                    // Replace the groundcover
                    var replacedGC = ReplaceGroundCover(targetGC.Value, sourceGroundCover, targetName);
                    if (replacedGC != null)
                    {
                        replacedGroundCovers.Add(replacedGC);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                          $"Replaced groundcover '{targetName}' Types and material");
                    }
                }
            }

            // Write replaced groundcovers back to file
            if (replacedGroundCovers.Any())
            {
                WriteReplacedGroundCoversToFile(targetFile, existingGroundCovers, replacedGroundCovers);
            }

            _groundCoversToReplace.Clear();
        }

        /// <summary>
        /// Parses all groundcover JSON lines for fast lookup
        /// </summary>
        private void ParseAllGroundCovers()
        {
            _parsedGroundCovers.Clear();
            if (_allGroundCoverJsonLines == null)
            {
                return;
            }

            foreach (var line in _allGroundCoverJsonLines)
            {
                try
                {
                    var gcNode = JsonUtils.GetValidJsonNodeFromString(line, "groundcover");
                    if (gcNode != null && gcNode["name"] != null)
                    {
                        var name = gcNode["name"].ToString();
                        _parsedGroundCovers[name] = gcNode;
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                  $"Error parsing groundcover line: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Finds source groundcover that references the given material
        /// </summary>
        private JsonNode FindSourceGroundCoverForMaterial(MaterialJson material)
        {
            foreach (var gc in _parsedGroundCovers.Values)
            {
                if (gc["material"]?.ToString() == material.Name)
                {
                    return gc;
                }
            }
            return null;
        }

        /// <summary>
        /// Replaces a target groundcover while preserving identity
        /// </summary>
        private JsonNode ReplaceGroundCover(JsonNode targetGC, JsonNode sourceGC, string targetName)
        {
            try
            {
                // Create a copy of source groundcover
                var replacedGC = JsonNode.Parse(sourceGC.ToJsonString());

                // Preserve target properties: name, persistentId, class
                if (targetGC["name"] != null)
                {
                    replacedGC["name"] = targetGC["name"].ToString();
                }
                if (targetGC["persistentId"] != null)
                {
                    replacedGC["persistentId"] = targetGC["persistentId"].ToString();
                }
                if (targetGC["class"] != null)
                {
                    replacedGC["class"] = targetGC["class"].ToString();
                }

                // Copy dependencies (material and DAE files) from source
                var newMaterialName = _dependencyHelper.CopyGroundCoverDependencies(sourceGC, targetName);

                // Update material property to the new copied material name
                if (!string.IsNullOrEmpty(newMaterialName))
                {
                    replacedGC["material"] = newMaterialName;
                }

                // Replace Types from source (this includes shapeFilename and layer properties)
                if (sourceGC["Types"] != null)
                {
                    replacedGC["Types"] = JsonNode.Parse(sourceGC["Types"].ToJsonString());
                }
                else
                {
                    // If source has no Types, delete Types from target
                    if (replacedGC["Types"] != null)
                    {
                        replacedGC.AsObject().Remove("Types");
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                         $"Removed Types from groundcover '{targetName}' (source has no Types)");
                    }
                }

                return replacedGC;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
         $"Error replacing groundcover '{targetName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads existing groundcovers from the target file
        /// </summary>
        private Dictionary<string, JsonNode> LoadExistingGroundCovers(FileInfo targetFile)
        {
            var existingGroundCovers = new Dictionary<string, JsonNode>();
            try
            {
                var lines = File.ReadAllLines(targetFile.FullName);
                foreach (var line in lines)
                {
                    try
                    {
                        var gcNode = JsonUtils.GetValidJsonNodeFromString(line, targetFile.FullName);
                        if (gcNode != null && gcNode["name"] != null)
                        {
                            var name = gcNode["name"].ToString();
                            existingGroundCovers[name] = gcNode;
                        }
                    }
                    catch
                    {
                        // Skip invalid lines
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error loading existing groundcovers: {ex.Message}");
            }
            return existingGroundCovers;
        }

        /// <summary>
        /// Writes replaced groundcovers back to file
        /// </summary>
        private void WriteReplacedGroundCoversToFile(
                   FileInfo targetFile,
          Dictionary<string, JsonNode> existingGroundCovers,
         List<JsonNode> replacedGroundCovers)
        {
            try
            {
                // Update existing dictionary with replaced groundcovers
                foreach (var replacedGC in replacedGroundCovers)
                {
                    var name = replacedGC["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        existingGroundCovers[name] = replacedGC;
                    }
                }

                // Write all groundcovers to file
                var lines = existingGroundCovers.Values
             .Select(gc => gc.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()))
                .ToList();

                File.WriteAllLines(targetFile.FullName, lines);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
             $"Replaced {replacedGroundCovers.Count} groundcover(s) in {targetFile.FullName}");
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                   $"Error writing replaced groundcovers: {ex.Message}");
            }
        }
    }
}
