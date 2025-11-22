using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO;

public readonly struct GameFileType
{
    public enum MainType
    {
        Unknown,
        Config,
        Script,
        Texture,
        Model,
    }

    public enum SubType
    {
        Unknown,
        Json,
        Lua,
        Png,
        Dds,
        Jpg,
        Dae,
        Cdae,
    }

    public static readonly GameFileType Texture = new(MainType.Texture, SubType.Unknown);
    public static readonly GameFileType TexturePng = new(MainType.Texture, SubType.Png);
    public static readonly GameFileType TextureDds = new(MainType.Texture, SubType.Dds);
    public static readonly GameFileType TextureJpg = new(MainType.Texture, SubType.Jpg);

    public readonly MainType Main;
    public readonly SubType Sub;

    private GameFileType(MainType mainType, SubType subType)
    {
        Main = mainType;
        Sub = subType;
    }

    public static GameFileType FromFileExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".json" => new GameFileType(MainType.Config, SubType.Json),
            ".lua" => new GameFileType(MainType.Script, SubType.Lua),
            ".png" => TexturePng,
            ".dds" => TextureDds,
            ".jpg" or ".jpeg" => TextureJpg,
            ".dae" => new GameFileType(MainType.Model, SubType.Dae),
            ".cdae" => new GameFileType(MainType.Model, SubType.Cdae),
            _ => new GameFileType(MainType.Unknown, SubType.Unknown),
        };
    }
}
