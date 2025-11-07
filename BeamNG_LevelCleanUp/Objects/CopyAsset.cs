namespace BeamNG_LevelCleanUp.Objects
{
    public enum CopyAssetType
    {
        Road = 0,
        Decal = 1,
        Dae = 2,
        Terrain = 3,
        // GroundCover removed - now copied automatically with Terrain
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
        public string TerrainMaterialName { get; set; }
        public string TerrainMaterialInternalName { get; set; }
        public GroundCover GroundCoverData { get; set; }
    }
}
