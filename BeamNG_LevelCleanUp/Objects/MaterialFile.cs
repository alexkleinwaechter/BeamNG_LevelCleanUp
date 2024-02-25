namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialFile
    {
        public FileInfo? File { get; set; }
        public string MapType { get; set; }
        public bool Missing { get; set; }
        public string MaterialName { get; internal set; }
    }
}
