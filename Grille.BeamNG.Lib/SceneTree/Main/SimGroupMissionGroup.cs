namespace Grille.BeamNG.SceneTree.Main;

public class SimGroupMissionGroup : SimGroup
{
    public SimGroupLevelObjects LevelObjects { get; }

    public SimGroup PlayerDropPoints { get; }

    public SimGroupMissionGroup() : base("MissionGroup")
    {
        LevelObjects = new SimGroupLevelObjects();
        PlayerDropPoints = new SimGroup("PlayerDopPoints");

        Items.Add(LevelObjects, PlayerDropPoints);
    }
}
