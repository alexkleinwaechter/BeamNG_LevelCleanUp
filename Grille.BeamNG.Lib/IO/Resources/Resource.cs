using Grille.BeamNG.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Grille.BeamNG.IO.Resources;

public abstract class Resource : IKeyed
{
    string IKeyed.Key => Name;

    /// <summary>Resource is delivered as part of the game.</summary>
    public bool IsGameResource { get; protected set; }

    public string Name { get; }

    /// <summary>Gets set when resource is opened.</summary>
    public string DynamicName { get; protected set; }

    public Resource(string name, bool isGameResource)
    {
        Name = name;
        DynamicName = name;
        IsGameResource = isGameResource;
    }

    protected abstract bool TryOpen([MaybeNullWhen(false)] out Stream stream, bool canThrow);

    public bool TryOpen([MaybeNullWhen(false)] out Stream stream)
    {
        return TryOpen(out stream, false);
    }

    public Stream Open()
    {
        var result = TryOpen(out var stream, true);
        if (!result || stream == null)
            throw new InvalidOperationException("Could not open stream.");
        return stream;
    }

    public void SaveToDirectory(string dirPath)
    {
        using var stream = Open();
        var dstpath = Path.Combine(dirPath, DynamicName);
        using var file = File.OpenWrite(dstpath);
        stream.CopyTo(file);
    }

    public void Save(string filePath)
    {
        using var src = Open();
        using var dst = File.Create(filePath);
        src.CopyTo(dst);
    }
}
