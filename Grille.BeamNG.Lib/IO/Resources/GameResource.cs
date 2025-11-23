using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Resources;

public class GameResource : Resource
{
    public GameFileType FileType { get; }
    public string FilePath { get; }
    public string? GamePath { get; }
    public string? UserPath { get; }

    public GameResource(string path, string? gamePath, string? userPath = null, bool isGameResource = true) : base(Path.GetFileNameWithoutExtension(path), isGameResource)
    {

        FileType = GameFileType.FromFileExtension(Path.GetExtension(path));
        FilePath = path;
        GamePath = gamePath;
        UserPath = userPath;
    }

    protected override bool TryOpen([MaybeNullWhen(false)] out Stream stream, bool canThrow)
    {
        if (FileType.Main == GameFileType.MainType.Texture)
        {
            if (GameFileSystem.TryOpenStream(out var streamInfo, FilePath, GamePath, UserPath, [".dds", ".png"]))
            {
                stream = streamInfo.Stream;
                DynamicName = Path.ChangeExtension(Name, Path.GetExtension(streamInfo.Path.FilePath));
                return true;
            }
            if (canThrow)
                throw new Exception($"Could not find texture '{FilePath}' in game or user paths.");

            stream = null;
            return false;
        }

        if (GameFileSystem.TryOpenStream(out var streamInfo0, FilePath, GamePath, UserPath))
        {
            stream = streamInfo0.Stream;
            DynamicName = Path.ChangeExtension(Name, Path.GetExtension(streamInfo0.Path.FilePath));
            return true;
        }
        if (canThrow)
            throw new Exception($"Could not find texture '{FilePath}' in game or user paths.");

        stream = null;
        return false;
    }
}
