using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Grille.BeamNG.IO.Resources;

namespace Grille.BeamNG.IO;
public static class GameFileSystem
{
    public record struct AbsolutePath(string FilePath, string? ZipPath, bool IsGameResource)
    {
        public Stream? TryOpenStream()
        {
            if (ZipPath == null)
            {
                if (File.Exists(FilePath))
                {
                    return new FileStream(FilePath, FileMode.Open);
                }
                return null;
            }
            if (File.Exists(ZipPath))
            {
                var archive = ZipFileManager.Open(ZipPath);
                var entry = archive.GetEntry(FilePath);
                if (entry != null)
                {
                    return entry.Open();
                }
            }
            return null;
        }

        public bool TryOpenStream([MaybeNullWhen(false)] out Stream stream)
        {
            stream = TryOpenStream();
            return stream != null;
        }
    }

    public record struct StreamInfo(Stream Stream, AbsolutePath Path) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    private static string[] Split(string entry)
    {
        return entry.ToLower().Split([Path.PathSeparator, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    public static IReadOnlyList<AbsolutePath> GetAbsolutePaths(string entry, string? gamePath, string? userPath = null, string[]? extensions = null)
    {
        var split = Split(entry);
        var list = new List<AbsolutePath>();

        void AddFile(string root, bool isGameResource = true)
        {
            if (extensions == null)
            {
                list.Add(new(Path.Join(userPath, "current", entry), null, isGameResource));
            }
            else
            {
                foreach (var ext in extensions)
                {
                    list.Add(new(Path.Join(userPath, "current", Path.ChangeExtension(entry, ext)), null, isGameResource));
                }
            }
        }

        void AddZip(string root, string subpath, bool isGameResource = true)
        {
            var zippath = Path.Join(root, subpath);
            if (extensions == null)
            {
                list.Add(new(entry, zippath, isGameResource));
            }
            else
            {
                foreach (var ext in extensions)
                {
                    list.Add(new(Path.ChangeExtension(entry, ext), zippath, isGameResource));
                }
            }
        }

        if (userPath != null)
        {
            AddFile(userPath, false);
            if (split.Length > 2)
            {
                AddZip(userPath, $"current/mods/{split[1]}.zip", false);
            }
        }
        if (gamePath == null)
        {
            return list;
        }
        if (split[0] == "levels" || split[0] == "vehicles")
        {
            AddZip(gamePath, $"content/{split[0]}/{split[1]}.zip");
        }
        else if (split[0] == "art" || split[0] == "core")
        {
            AddZip(gamePath, $"gameengine.zip");
        }
        else if (split[0] == "assets")
        {
            if (split[1] == "materials")
            {
                AddZip(gamePath, $"content/assets/materials/{split[2]}.zip");
            }
            else if (split[1] == "meshes")
            {
                AddZip(gamePath, $"content/assets/meshes.zip");
            }
        }

        return list;
    }

    public static bool TryOpenStream(out StreamInfo streamInfo, string entry, string? gamePath, string? userPath = null, string[]? extensions = null)
    {
        if (gamePath == null && userPath == null)
        {
            throw new ArgumentException("Either gamePath or userPath must be provided.");
        }
        var paths = GetAbsolutePaths(entry, gamePath, userPath, extensions);
        foreach (var path in paths)
        {
            if (path.TryOpenStream(out var stream))
            {
                streamInfo = new StreamInfo(stream, path);
                return true;
            }
            var linkPaths = new AbsolutePath($"{path.FilePath}.link", path.ZipPath, path.IsGameResource);
            if (linkPaths.TryOpenStream(out stream))
            {
                var link = AssetLink.Deserialize(stream);
                if (link.Type != "normal") 
                    throw new NotSupportedException($"Asset link type '{link.Type}' is not supported.\n{link.Path}");
                streamInfo = OpenStream(link.Path, gamePath, userPath, extensions);
                return true;
            }
        }
        streamInfo = default;
        return false;
    }

    public static StreamInfo OpenStream(string entry, string? gamePath, string? userPath = null, string[]? extensions = null)
    {
        if (!TryOpenStream(out var streamInfo, entry, gamePath, userPath, extensions))
            throw new FileNotFoundException($"Could not find '{entry}' in the game or user file system.");
        return streamInfo;
    }
}
