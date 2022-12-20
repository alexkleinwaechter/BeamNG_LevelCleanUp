using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public enum CopyAssetType
    {
        Road = 0
    }
    public class CopyAsset
    {
        public Guid Identifier { get; set; } = Guid.NewGuid();
        public CopyAssetType CopyAssetType { get; set; }
        public string Name { get; set; }
        public List<MaterialJson> Materials = new List<MaterialJson>();
        public string TargetPath { get; set; }
        public string SourceMaterialJsonPath { get; set; }
    }
}
