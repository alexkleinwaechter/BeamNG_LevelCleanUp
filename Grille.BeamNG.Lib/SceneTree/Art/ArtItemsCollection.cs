using Grille.BeamNG.SceneTree.Main;
using Grille.BeamNG.SceneTree.Registry;

namespace Grille.BeamNG.SceneTree.Art;

public abstract class ArtItemsCollection<T> : JsonDictWrapperKeyedCollection<T> where T : ArtItem
{
    public ArtGroup? Owner { get; }

    public ArtItemsCollection(ArtGroup? owner)
    {
        Owner = owner;
    }

    public override void Serialize(Stream stream)
    {
        ArtItemsJsonSerializer.Serialize(stream, this);
    }

    public override void Deserialize(Stream stream, ItemClassRegistry registry)
    {
        foreach (var dict in ArtItemsJsonSerializer.Deserialize(stream))
        {
            var className = (string)dict["class"];

            if (registry.TryCreate<T>(className, dict, out var obj))
            {
                Add(obj);
            }
            else
            {
                throw new Exception();
            }
        }
    }

    public bool TrySaveToDirectory(string dirPath, string name)
    {
        if (Count == 0)
            return false;

        var materialsPath = Path.Combine(dirPath, name);
        Save(materialsPath);
        return true;
    }

    public bool TryLoadFromDirectory(string dirPath, string name, ItemClassRegistry registry)
    {
        var filePath = Path.Combine(dirPath, name);
        if (!File.Exists(filePath))
            return false;
        Load(filePath, registry);
        return true;
    }
}
