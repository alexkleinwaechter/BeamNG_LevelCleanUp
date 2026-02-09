namespace Grille.BeamNG.SceneTree.Main;

public class SimGroupRoot : SimGroup
{
    public SimGroupMissionGroup MissionGroup { get; }

    public SimGroupRoot() : base("main")
    {
        IsMain = true;

        MissionGroup = new SimGroupMissionGroup();

        Items.Add(MissionGroup);
    }
}
