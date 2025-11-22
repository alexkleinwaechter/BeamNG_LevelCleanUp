using Grille.BeamNG;
using Grille.BeamNG.IO.Binary;
using Grille.BeamNG.IO.Text;
using Grille.BeamNG.SceneTree.Art;
using SixLabors.ImageSharp;
//using ProjNet.CoordinateSystems;

using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;

namespace BeamNGTerrainGenerator.BeamNG;

using SDColor = System.Drawing.Color;

public class LevelExporter
{
    public TerrainMaterial? TemplateMaterial { get; set; }

    public string Namespace { get; set; }

    /// <summary>
    /// ...\AppData\Local\BeamNG.drive\0.34\levels
    /// </summary>
    public string LevelsPath { get; set; }

    public int BaseTextureSize { get; set; }

    /// <summary>
    /// Information on how to generate the terrain. Includes material names.
    /// </summary>
    public TerrainTemplate TerrainInfo { get; }

    /// <summary>
    /// Normalized Heightmap Image.
    /// </summary>
    public ImageProjector<L16> Height { get; }

    /// <summary>
    /// Material Indices
    /// </summary>
    public ImageProjector<L16> Material { get; }

    public ImageProjector<Rgba32> Color { get; }

    public LevelGameInfo Info { get; }

    public LevelExporter()
    {
        BaseTextureSize = 1024;
        TerrainInfo = new();
        Info = new();

        Namespace = "NewWorldLevel";
        LevelsPath = $@"C:\Users\{Environment.UserName}\AppData\Local\BeamNG\BeamNG.drive\current\levels";

        Height = new();
        Material = new();
        Color = new();
    }

    void BuildArtGroupTerrain(ArtGroupRoot root)
    {
        if (TemplateMaterial == null) return;

        var dirpath = Path.Combine(LevelsPath, $"{Namespace}/art/terrains");
        var gamepath = $"/levels/{Namespace}/art/terrains";

        const string tBaseB = "t_base_b.png";
        const string tBaseNm = "t_base_nm.png";
        const string tBaseR = "t_base_r.png";
        const string tBaseAo = "t_base_ao.png";
        const string tBaseH = "t_base_h.png";

        var group = root.Terrains;
        var materials = group.MaterialItems;

        foreach (var item in materials.Enumerate<TerrainMaterialTextureSet>())
        {
            item.BaseTexSize.Value = new Vector2(BaseTextureSize);
        }

        string tBaseColorPath = Path.Combine(dirpath, tBaseB);
        if (Color.Image != null)
        {
            Color.UpdateRect(BaseTextureSize);
            using var image = Color.CreateBaseColorRgba32Texture(BaseTextureSize);
            image.SaveAsPng(tBaseColorPath);
        }
        else
        {
            SolidColorResource.Save(SDColor.Green, BaseTextureSize, tBaseColorPath);
        }

        SolidColorResource.Save(SDColor.Gray, BaseTextureSize, Path.Combine(dirpath, tBaseNm));
        SolidColorResource.Save(SDColor.Gray, BaseTextureSize, Path.Combine(dirpath, tBaseR));
        SolidColorResource.Save(SDColor.White, BaseTextureSize, Path.Combine(dirpath, tBaseAo));
        SolidColorResource.Save(SDColor.Black, BaseTextureSize, Path.Combine(dirpath, tBaseH));

        foreach (var name in TerrainInfo.MaterialNames)
        {
            var dict = new JsonDict(TemplateMaterial.Dict);
            var material = new TerrainMaterial(dict);

            material.InternalName.Value = name;
            material.Name.Value = $"{name}_id";

            void Setup(TerrainMaterialTexture texture, string name)
            {
                texture.Base.Texture.Value = Path.Combine(gamepath, name);
                texture.Base.MappingScale.Value = TerrainInfo.Resolution;
            }

            Setup(material.BaseColor, tBaseB);
            Setup(material.Normal, tBaseNm);
            Setup(material.Roughness, tBaseR);
            Setup(material.AmbientOcclusion, tBaseAo);
            Setup(material.Height, tBaseH);

            materials.Add(material);
        }
    }

    public void BuildTerrain(int size, string path)
    {
        var terrain = new TerrainV9Binary(size);

        if (Height.Image != null)
        {
            Height.UpdateRect(TerrainInfo.Resolution);
            Height.ProjectL16ToHeight(terrain);
        }

        if (Material.Image != null)
        {
            Material.UpdateRect(TerrainInfo.Resolution);
            Material.ProjectL8ToMaterials(terrain);
        }

        terrain.MaterialNames = TerrainInfo.MaterialNames.ToArray();

        using var stream = File.Create(path);
        TerrainV9Serializer.Serialize(stream, terrain);
    }


    public void Save()
    {
        var levelPath = Path.Combine(LevelsPath, Namespace);
        Directory.CreateDirectory(levelPath);

        var level = new LevelBuilder(Namespace);

        level.SaveTerrain = false;
        level.Info = Info;
        level.Terrain = TerrainInfo;

        level.SetupDefaultLevelObjects();
        level.SetupDefaultSpawn();
        level.SetupTerrain();

        BuildArtGroupTerrain(level.ArtTree);

        var terrainPath = Path.Combine(levelPath, "terrain.ter");
        BuildTerrain(TerrainInfo.Resolution, terrainPath);

        level.Save(levelPath);
    }
}
