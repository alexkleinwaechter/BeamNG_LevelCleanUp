using Grille.BeamNG.IO.Resources;
using Grille.BeamNG.SceneTree.Art;
using Grille.BeamNG.SceneTree.Forest;
using Grille.BeamNG.SceneTree.Main;
using System.IO;
using System.Reflection.Emit;

namespace Grille.BeamNG;

/// <summary>
/// Class to help create a level folder structure.
/// </summary>
/// <remarks>
/// Most functions are single use, an instance should not be reused to export multiple levels.
/// </remarks>
public class LevelBuilder 
{
    public string Namespace { get; set; }

    public LevelGameInfo Info { get; set; }

    public TerrainTemplate Terrain { get; set; }

    public SimGroupRoot MainTree {  get; set; }

    public ArtGroupRoot ArtTree { get; set; }

    public Resource? Preview { get; set; }

    /// <summary>
    /// Save <see cref="TerrainTemplate"/> to ".../level/terrain.ter" on <see cref="Save"/>
    /// </summary>
    public bool SaveTerrain { get; set; }

    public LevelBuilder(string @namespace)
    {
        Namespace = @namespace;

        Info = new LevelGameInfo();
        Terrain = new TerrainTemplate();

        MainTree = new SimGroupRoot();
        ArtTree = new ArtGroupRoot();

        SaveTerrain = true;
    }

    public void SetupDefaultLevelObjects()
    {
        if (MainTree.Items.EnumerateRecursive<ScatterSky>().Count == 0)
        {
            MainTree.MissionGroup.LevelObjects.Sky.Items.Add(new ScatterSky());
        }
        if (MainTree.Items.EnumerateRecursive<TimeOfDay>().Count == 0)
        {
            MainTree.MissionGroup.LevelObjects.Time.Items.Add(new TimeOfDay());
        }
        if (MainTree.Items.EnumerateRecursive<LevelInfo>().Count == 0)
        {
            MainTree.MissionGroup.LevelObjects.Infos.Items.Add(new LevelInfo());
        }
    }

    /// <summary>Creates default <see cref="SpawnSphere"/> object.</summary>
    /// <remarks>Should only be called once.</remarks>
    public void SetupDefaultSpawn()
    {
        var spawn = new SpawnSphere(Terrain.Height);
        MainTree.MissionGroup.PlayerDropPoints.Items.Add(spawn);
    }

    /// <summary>Creates <see cref="TerrainBlock"/> and <see cref="TerrainMaterialTextureSet"/> objects base on <see cref="Terrain"/> and <see cref="Namespace"/>.</summary>
    /// <remarks>Should only be called once.</remarks>
    public void SetupTerrain()
    {
        var textureSetName = $"{Namespace}_TerrainMaterialTextureSet";

        var terrain = new TerrainBlock(Terrain);
        terrain.TerrainFile.Value = $"\\levels\\{Namespace}\\terrain.ter";
        terrain.MaterialTextureSet.Value = textureSetName;
        MainTree.MissionGroup.LevelObjects.Terrain.Items.Add(terrain);

        var textureSet = new TerrainMaterialTextureSet(textureSetName);
        ArtTree.Terrains.MaterialItems.Add(textureSet);
    }

    /// <summary>
    /// Find all <see cref="TerrainMaterial"/> items in <see cref="ArtTree"/> and adds them to <see cref="Terrain"/>.
    /// </summary>
    public void SetupTerrainMaterialNames()
    {
        var names = new List<string>();
        foreach (var item in ArtTree.MaterialItems.EnumerateRecursive<TerrainMaterial>())
        {
            names.Add(item.InternalName);
        }
        Terrain.MaterialNames = names.ToArray();
    }

    public void Save(string dirPath)
    {
        ZipFileManager.BeginPooling();

        var infoPath = Path.Combine(dirPath, "info.json");
        Info.Size.Value = new Vector2(Terrain.WorldSize);
        Info.Save(infoPath);

        var simPath = Path.Combine(dirPath, "main");
        MainTree.SaveTree(simPath);

        var artPath = Path.Combine(dirPath, "art");
        ArtTree.SaveTree(artPath);

        if (SaveTerrain)
        {
            var terrainPath = Path.Combine(dirPath, "terrain.ter");
            Terrain.Save(terrainPath);
        }

        if (Info.Previews.Exists && Info.Previews.Value.Length > 0 && Preview != null)
        {
            var previewPath = Path.Combine(dirPath, Info.Previews.Value[0]);
            Preview.Save(previewPath);
        }

        ZipFileManager.EndPooling();
    }
}
