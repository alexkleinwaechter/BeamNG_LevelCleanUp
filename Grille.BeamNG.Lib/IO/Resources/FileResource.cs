namespace Grille.BeamNG.IO.Resources;

public class FileResource : Resource
{
    public string Path { get; }

    public FileResource(string name, string path, bool isGameResource) : base(name, isGameResource)
    {
        Path = path;
    }

    protected override bool TryOpen(out Stream stream, bool canThrow)
    {
        try
        {
            stream = new FileStream(Path, FileMode.Open);
        }
        catch
        {
            if (canThrow)
                throw;
            stream = null!;
            return false;
        }

        return true;
    }
}
