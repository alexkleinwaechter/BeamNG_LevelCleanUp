namespace Grille.BeamNG.SceneTree.Main;

public class SimGroupLevelObjects : SimGroup
{
    public SimGroup Cloud { get; }

    public SimGroup Infos { get; }

    public SimGroup Sky { get; }

    public SimGroup Terrain { get; }

    public SimGroup Time { get; }

    public SimGroup Vegatation { get; }

    public SimGroup Misc { get; }

    public SimGroupLevelObjects() : base("LevelObjects")
    {
        Cloud = new("cloud");
        Infos = new("infos");
        Sky = new("sky");
        Terrain = new("terrain");
        Time = new("time");
        Vegatation = new("vegatation");
        Misc = new("misc");

        Items.Add(Cloud, Infos, Sky, Terrain, Time, Vegatation, Misc);
    }
}
