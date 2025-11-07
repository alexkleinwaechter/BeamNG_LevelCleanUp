using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNG_LevelCleanUp.Logic;
using System.Text.Json;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Scans vegetation items.level.json files for GroundCover objects and their dependencies
    /// </summary>
    public class GroundCoverCopyScanner
    {
        private readonly string _levelPathCopyFrom;
        private readonly List<MaterialJson> _materialsJsonCopy;
        private readonly List<CopyAsset> _copyAssets;

        public GroundCoverCopyScanner(string levelPathCopyFrom, List<MaterialJson> materialsJsonCopy, List<CopyAsset> copyAssets)
        {
            _levelPathCopyFrom = levelPathCopyFrom;
            _materialsJsonCopy = materialsJsonCopy;
            _copyAssets = copyAssets;
        }

        /// <summary>
        /// Scans items.level.json files in vegetation folders for GroundCover objects
        /// </summary>
        public void ScanGroundCovers(FileInfo itemsLevelFile)
        {
            if (!itemsLevelFile.Exists)
            {
                return;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Scanning groundcovers from {itemsLevelFile.FullName}");

            foreach (string line in File.ReadAllLines(itemsLevelFile.FullName))
            {
                try
                {
                    using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromString(line, itemsLevelFile.FullName);
                    if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                    {
                        var jsonElement = jsonObject.RootElement;

                        // Check if this is a GroundCover object
                        if (jsonElement.TryGetProperty("class", out var classProperty) &&
                     classProperty.GetString() == "GroundCover")
                        {
                            var groundCover = jsonElement.Deserialize<GroundCover>(BeamJsonOptions.GetJsonSerializerOptions());
                            if (groundCover != null)
                            {
                                ProcessGroundCover(groundCover, line, itemsLevelFile.FullName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                     $"Error parsing GroundCover line in {itemsLevelFile.FullName}: {ex.Message}");
                }
            }
        }

        private void ProcessGroundCover(GroundCover groundCover, string originalJsonLine, string sourceFilePath)
        {
            var materials = new List<MaterialJson>();
            var daeFiles = new List<string>();
            var terrainLayers = new HashSet<string>();

            // Extract material reference from "material" property
            if (!string.IsNullOrEmpty(groundCover.Material))
            {
                var material = _materialsJsonCopy.FirstOrDefault(m =>
                        m.Name.Equals(groundCover.Material, StringComparison.OrdinalIgnoreCase));
                if (material != null && !materials.Contains(material))
                {
                    materials.Add(material);
                }
            }

            // Extract DAE files and terrain layer references from Types array
            if (groundCover.Types != null)
            {
                foreach (var type in groundCover.Types)
                {
                    // Collect terrain layer references
                    if (!string.IsNullOrEmpty(type.Layer))
                    {
                        terrainLayers.Add(type.Layer);
                    }

                    // Collect DAE file references
                    if (!string.IsNullOrEmpty(type.ShapeFilename))
                    {
                        daeFiles.Add(type.ShapeFilename);
                    }
                }
            }

            // Create CopyAsset for this GroundCover
            var copyAsset = new CopyAsset
            {
                CopyAssetType = CopyAssetType.GroundCover,
                Name = groundCover.Name ?? "Unnamed GroundCover",
                Materials = materials,
                GroundCoverData = groundCover,
                // IMPORTANT: Store the original JSON line to preserve all properties
                SourceMaterialJsonPath = originalJsonLine  // Store original JSON, not file path
            };

            // Calculate size
            copyAsset.SizeMb = Math.Round(
         (materials.SelectMany(x => x.MaterialFiles).Sum(mf => mf.File?.Exists == true ? mf.File.Length : 0) / 1024f) / 1024f, 2);

            // Add DAE files information (they will be processed separately)
            foreach (var daeFile in daeFiles.Distinct())
            {
                try
                {
                    var daeScanner = new DaeScanner(_levelPathCopyFrom, daeFile);
                    if (daeScanner.Exists())
                    {
                        var daeMaterials = daeScanner.GetMaterials();
                        if (copyAsset.MaterialsDae == null)
                        {
                            copyAsset.MaterialsDae = new List<MaterialsDae>();
                        }
                        copyAsset.MaterialsDae.AddRange(daeMaterials);

                        // Find and add materials used by the DAE
                        foreach (var daeMat in daeMaterials)
                        {
                            var material = _materialsJsonCopy.FirstOrDefault(m =>
                       m.Name.Equals(daeMat.MaterialName, StringComparison.OrdinalIgnoreCase));
                            if (material != null && !materials.Contains(material))
                            {
                                materials.Add(material);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                 $"Error scanning DAE file {daeFile} for GroundCover {groundCover.Name}: {ex.Message}");
                }
            }

            // Store terrain layer information for later reference renaming
            if (terrainLayers.Any())
            {
                // Store as comma-separated list in a property
                copyAsset.TerrainMaterialInternalName = string.Join(",", terrainLayers);
            }

            _copyAssets.Add(copyAsset);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found GroundCover: {copyAsset.Name} with {materials.Count} materials, {daeFiles.Count} DAE files, {terrainLayers.Count} terrain layers");
        }
    }
}
