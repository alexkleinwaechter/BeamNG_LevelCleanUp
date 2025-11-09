using BeamNG_LevelCleanUp;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Shared helper class for copying groundcover dependencies (materials and DAE files)
    /// Used by both GroundCoverCopier and GroundCoverReplacer
    /// </summary>
    public class GroundCoverDependencyHelper
    {
        private readonly MaterialCopier _materialCopier;
        private readonly DaeCopier _daeCopier;
        private readonly string _levelNameCopyFrom; // source level name suffix
        private readonly string _targetNamePath; // absolute path to target level name directory (levels/<targetLevelName>)
        private readonly string _targetLevelName; // target level folder name
        private readonly HashSet<string> _copiedDaeFiles;
        private Dictionary<string, MaterialJson> _materialLookup;
        private HashSet<string> _terrainMaterialNames; // Track which materials are terrain materials

        public GroundCoverDependencyHelper(
        MaterialCopier materialCopier,
        DaeCopier daeCopier,
        string levelNameCopyFrom,
        string targetNamePath)
        {
            _materialCopier = materialCopier;
            _daeCopier = daeCopier;
            _levelNameCopyFrom = levelNameCopyFrom;
            _targetNamePath = targetNamePath; // e.g. ...\\_unpacked\\levels\\<targetLevelName>
            _targetLevelName = Path.GetFileName(_targetNamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _copiedDaeFiles = new HashSet<string>();
            _terrainMaterialNames = new HashSet<string>();
        }

        /// <summary>
        /// Loads materials for lookup
        /// </summary>
        public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
        {
            if (materialsJsonCopy == null || !materialsJsonCopy.Any())
            {
                _materialLookup = new Dictionary<string, MaterialJson>();
                _terrainMaterialNames = new HashSet<string>();
                return;
            }

            // Separate terrain materials from regular materials based on class property
            var terrainMaterials = materialsJsonCopy
                .Where(m => !string.IsNullOrEmpty(m.Class) &&
  m.Class.Equals("TerrainMaterial", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var regularMaterials = materialsJsonCopy
       .Where(m => string.IsNullOrEmpty(m.Class) ||
       !m.Class.Equals("TerrainMaterial", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Track terrain material names
            _terrainMaterialNames = terrainMaterials.Select(m => m.Name).ToHashSet();

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                     $"Loaded {terrainMaterials.Count} terrain materials and {regularMaterials.Count} regular materials from source.");

            // Build lookup dictionary: Terrain materials take priority over regular materials
            _materialLookup = new Dictionary<string, MaterialJson>();

            // Add regular materials first (lower priority)
            foreach (var group in regularMaterials.GroupBy(m => m.Name))
            {
                if (!_materialLookup.ContainsKey(group.Key))
                {
                    _materialLookup[group.Key] = group.First();
                }
            }

            // Add terrain materials second (higher priority - will overwrite regular materials)
            // This ensures terrain materials are always preferred when both exist with same name
            foreach (var group in terrainMaterials.GroupBy(m => m.Name))
            {
                _materialLookup[group.Key] = group.First(); // Overwrite if exists
            }

            // Log regular material duplicates only (informational)
            var regularDuplicates = regularMaterials
             .GroupBy(m => m.Name)
                            .Where(g => g.Count() > 1 && !_terrainMaterialNames.Contains(g.Key))
                            .Select(g => g.Key)
             .ToList();

            if (regularDuplicates.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                         $"Found {regularDuplicates.Count} duplicate non-terrain material(s). Using first occurrence for each.");
            }

            // Log if any materials have same name but different class
            var conflictingNames = terrainMaterials.Select(t => t.Name)
     .Intersect(regularMaterials.Select(r => r.Name))
      .ToList();

            if (conflictingNames.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Found {conflictingNames.Count} material(s) with same name in both TerrainMaterial and Material classes. Prioritizing TerrainMaterial version.");
            }
        }

        /// <summary>
        /// Copies materials and DAE files referenced by a groundcover
        /// Returns the new material name if a material was copied, null otherwise
        /// </summary>
        public string CopyGroundCoverDependencies(JsonNode groundCoverNode, string groundCoverName)
        {
            string newMaterialName = null;

            // Copy material if present
            if (groundCoverNode["material"] != null)
            {
                var materialName = groundCoverNode["material"].ToString();
                if (!string.IsNullOrEmpty(materialName))
                {
                    newMaterialName = CopyGroundCoverMaterial(materialName, groundCoverName);
                }
            }

            // Copy DAE files from Types
            if (groundCoverNode["Types"] is JsonArray types)
            {
                foreach (var type in types)
                {
                    if (type != null && type["shapeFilename"] != null)
                    {
                        var daeFilePath = type["shapeFilename"].ToString();
                        if (!string.IsNullOrEmpty(daeFilePath))
                        {
                            CopyGroundCoverDaeFile(daeFilePath);
                        }
                    }
                }
            }

            return newMaterialName;
        }

        /// <summary>
        /// Copies a material referenced by a groundcover with level suffix
        /// Returns the new material name with suffix
        /// </summary>
        private string CopyGroundCoverMaterial(string materialName, string groundCoverName)
        {
            try
            {
                if (_materialLookup == null || !_materialLookup.TryGetValue(materialName, out var material))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
            $"Material '{materialName}' referenced by groundcover '{groundCoverName}' not found in source materials.");
                    return null;
                }

                // Generate new material name with level suffix
                var newMaterialName = $"{materialName}_{_levelNameCopyFrom}";

                // Target path inside the TARGET level (like GroundCoverCopier)
                var targetPath = Path.Join(_targetNamePath, Constants.GroundCover, $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}");
                Directory.CreateDirectory(targetPath);

                // Create a temporary CopyAsset for the material copier
                var materialCopyAsset = new CopyAsset
                {
                    CopyAssetType = CopyAssetType.Terrain,
                    Materials = new List<MaterialJson> { material },
                    TargetPath = targetPath,
                    Name = material.Name
                };

                // Copy the material with the new name
                if (_materialCopier.Copy(materialCopyAsset, newMaterialName))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                         $"Copied groundcover material '{materialName}' as '{newMaterialName}' for groundcover '{groundCoverName}'");
                    return newMaterialName;
                }
                else
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                 $"Failed to copy groundcover material '{materialName}' for groundcover '{groundCoverName}'");
                    return null;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
               $"Error copying material '{materialName}' for groundcover '{groundCoverName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Copies a DAE file referenced by groundcover
        /// </summary>
        private void CopyGroundCoverDaeFile(string daeFilePath)
        {
            try
            {
                // Skip if already copied
                if (_copiedDaeFiles.Contains(daeFilePath))
                {
                    return;
                }

                // Create a temporary CopyAsset for the DAE copier
                var daeCopyAsset = new CopyAsset
                {
                    CopyAssetType = CopyAssetType.Dae,
                    DaeFilePath = daeFilePath,
                    Materials = new List<MaterialJson>(),
                    MaterialsDae = new List<MaterialsDae>()
                };

                if (_daeCopier.Copy(daeCopyAsset))
                {
                    _copiedDaeFiles.Add(daeFilePath);
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Copied DAE file '{Path.GetFileName(daeFilePath)}' for groundcover");
                }
                else
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                     $"Failed to copy DAE file '{daeFilePath}' for groundcover");
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                   $"Error copying DAE file '{daeFilePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the cache of copied DAE files
        /// </summary>
        public void ClearCopiedDaeCache()
        {
            _copiedDaeFiles.Clear();
        }
    }
}
