using Grille.BeamNG.Collections;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree;

public interface ISceneTreeGroup : IKeyed
{ 
    public bool IsEmpty { get; }

    public void SaveTree(string path, bool ignoreEmpty);

    public void LoadTree(string path);

    public void LoadTree(string path, ItemClassRegistry registry);
}
