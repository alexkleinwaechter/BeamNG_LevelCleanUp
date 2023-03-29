using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public enum CopyAssetType
    {
        Road = 0,
        Decal = 1,
        Dae = 2,
    }
    public class CopyAsset
    {
        public Guid Identifier { get; set; } = Guid.NewGuid();
        public CopyAssetType CopyAssetType { get; set; }
        public string Name { get; set; }
        public List<MaterialJson> Materials = new List<MaterialJson>();
        public string TargetPath { get; set; }
        public string SourceMaterialJsonPath { get; set; }
        public ManagedDecalData DecalData { get; set; }
        public bool Duplicate { get; set; }
        public string DuplicateFrom { get; set; }
        public double SizeMb { get; set; }
        public string DaeFilePath { get; set; }
        public List<MaterialsDae> MaterialsDae { get; set; }
    }
}
