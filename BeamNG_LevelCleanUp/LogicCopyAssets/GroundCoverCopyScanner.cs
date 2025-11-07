using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNG_LevelCleanUp.Logic;
using System.Text.Json;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Scans vegetation items.level.json files for GroundCover objects
    /// Stores complete JSON lines for later filtering based on terrain materials
    /// </summary>
    public class GroundCoverCopyScanner
    {
        private readonly string _levelPathCopyFrom;

        public List<string> GroundCoverJsonLines { get; private set; } = new List<string>();

        public GroundCoverCopyScanner(string levelPathCopyFrom)
        {
            _levelPathCopyFrom = levelPathCopyFrom;
        }

        /// <summary>
        /// Scans items.level.json files in vegetation folders for GroundCover objects
        /// Stores them for later filtering and copying based on terrain materials
        /// </summary>
        public void ScanGroundCovers(FileInfo itemsLevelFile)
        {
            if (!itemsLevelFile.Exists)
 {
         return;
            }

  PubSubChannel.SendMessage(PubSubMessageType.Info, $"Scanning groundcovers from {itemsLevelFile.FullName}");

            int groundCoverCount = 0;
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
           // Store the complete JSON line for later filtering and copying
       GroundCoverJsonLines.Add(line);
   groundCoverCount++;
            }
    }
 }
                catch (Exception ex)
     {
  PubSubChannel.SendMessage(PubSubMessageType.Warning, 
        $"Error parsing GroundCover line in {itemsLevelFile.FullName}: {ex.Message}");
   }
    }

            if (groundCoverCount > 0)
      {
           PubSubChannel.SendMessage(PubSubMessageType.Info, 
    $"Found {groundCoverCount} groundcover(s) - will be copied automatically with terrain materials");
   }
        }
    }
}
