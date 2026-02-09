using System.Diagnostics.CodeAnalysis;

namespace Grille.BeamNG.IO.Resources;

internal class GroupResource : Resource
{
    readonly Resource[] _resources;
    public GroupResource(string name, bool isGameResource, Resource[] resources) : base(name, isGameResource)
    {
        _resources = resources;
    }

    protected override bool TryOpen([MaybeNullWhen(false)] out Stream stream, bool canThrow)
    {
        foreach (var resource in _resources)
        {
            if (resource.TryOpen(out stream))
                return true;
        }

        stream = null;
        return false;
    }
}
