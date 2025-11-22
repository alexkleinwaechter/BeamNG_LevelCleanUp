namespace Grille.BeamNG.IO.Resources;

public class ZipFileResource : Resource
{
    public string ZipFilePath { get; }
    public string EntryPath { get; }

    public ZipFileResource(string name, string zipFilePath, string path, bool isGameResource) : base(name, isGameResource)
    {
        ZipFilePath = zipFilePath;
        EntryPath = path;
    }

    protected override bool TryOpen(out Stream stream, bool canThrow)
    {
        var archive = ZipFileManager.Open(ZipFilePath);
        var entry = archive.GetEntry(EntryPath);

        if (entry == null)
        {
            var ext = Path.GetExtension(EntryPath).ToLower();
            if (ext == ".png")
            {
                DynamicName = Path.ChangeExtension(Name, ".dds");
                var path = Path.ChangeExtension(EntryPath, ".dds");
                entry = archive.GetEntry(path);
            }
        }

        if (entry == null)
        {
            if (canThrow)
                throw new Exception($"Could not find '{EntryPath}' in '{ZipFilePath}'.");
            stream = null!;
            return false;
        }

        try
        {
            stream = entry.Open();
            return true;
        }
        catch
        {
            if (canThrow)
                throw;
            stream = null!;
            return false;
        }
    }

    public void Find()
    {

    }
}
