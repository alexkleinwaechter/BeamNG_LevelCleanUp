﻿using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialJson
    {
        public string Name { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string Class { get; set; } = string.Empty;
        public string MapTo { get; set; } = string.Empty;
        public string PersistentId { get; set; } = string.Empty;
        [JsonPropertyName("Stages")]
        public List<MaterialStage> Stages { get; set; }
        public List<string> CubeFace { get; set; }
        public string Cubemap { get; set; } = string.Empty;
        [JsonIgnore]
        public List<MaterialFile> MaterialFiles { get; set; } = new List<MaterialFile>();
        [JsonIgnore]
        public bool NotUsed { get; set; }
        [JsonIgnore]
        public bool IsDuplicate { get; set; }
        [JsonIgnore]
        public int DuplicateCounter { get; set; } = 1;
        [JsonIgnore]
        public List<string> DuplicateFoundLocation { get; set; } = new List<string>();
        [JsonIgnore]
        public string MatJsonFileLocation { get; set; }
        public string MaterialTag0 { get; set; }
        public string MaterialTag1 { get; set; }
        public string MaterialTag2 { get; set; }
        public string TranslucentBlendOp { get; set; }
        public double? AlphaRef { get; set; }
        public bool? AlphaTest { get; set; }
        public bool? Translucent { get; set; }
        public bool? CastShadows { get; }
        public bool? TranslucentRecvShadows { get; set; }
        public bool? translucentZWrite { get; set; }

        [JsonIgnore]
        public List<string> MaterialTags
        {
            get
            {
                var retVal = new List<string>();
                if (MaterialTag0 != null) retVal.Add(MaterialTag0);
                if (MaterialTag1 != null) retVal.Add(MaterialTag1);
                if (MaterialTag2 != null) retVal.Add(MaterialTag2);
                return retVal;
            }
        }

        [JsonIgnore]
        public bool IsRoadAndPath
        {
            get
            {
                return MaterialTags.Any(x => x.ToUpperInvariant().Equals("RoadAndPath".ToUpperInvariant()));
            }
        }
    }
}
