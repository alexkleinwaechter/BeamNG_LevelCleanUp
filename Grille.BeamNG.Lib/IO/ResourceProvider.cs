using Grille.BeamNG.IO.Resources;
using System.Threading;

namespace Grille.BeamNG.IO;

public static class ResourceProvider
{
    public static string[] Split(string entry)
    {
        return entry.ToLower().Split([Path.PathSeparator, Path.AltDirectorySeparatorChar]);
    }

    public static BeamEnvironment.Product Product { get; set; } = BeamEnvironment.Drive;

    public static Func<string, string?, string?, Resource?>? ResolveGetResource { get; set; }

    static public Resource Get(string filePath)
    {
        return Get(filePath, Product.GameDirectory, Product.UserDirectory);
    }
   
    static public Resource Get(string filePath, string? gamePath, string? userPath)
    {

        filePath = filePath.Replace("\\", "/");

        if (ResolveGetResource != null)
        {
            var res = ResolveGetResource(filePath, gamePath, userPath);
            if (res != null)
                return res;
        }

        return new GameResource(filePath, gamePath, userPath, true);
    }
}
