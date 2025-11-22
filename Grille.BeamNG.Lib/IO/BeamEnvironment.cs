using Grille.BeamNG.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Grille.BeamNG.IO;
public static class BeamEnvironment
{
    public class Product
    {
        const string NullVersion = "0.0.0";
        const string SteamGameDir = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\BeamNG.drive";

        readonly string Name;

        public string UserDirectory { get; private set; } = default!;

        public string GameVersion { get; private set; } = default!;

        public string GameDirectory { get; private set; } = default!;

        internal Product(string name)
        {
            Name = name;
        }

        [MemberNotNullWhen(true, nameof(GameDirectory))]
        public bool IsGameDirectoryValid()
        {
            if (GameDirectory == null)
                return false;

            if (!Directory.Exists(GameDirectory))
                return false;

            var exePath = Path.Combine(GameDirectory, $"BeamNG.{Name}.exe");

            if (!File.Exists(exePath))
                return false;

            return true;
        }

        public void LoadIni(string filePath)
        {
            var ini = IniDictSerializer.Load(filePath);

            if (ini.TryGetValue<string>("version", out var version))
                GameVersion = version;
            else
                GameVersion = NullVersion;

            if (ini.TryGetValue<string>("installPath", out var path))
                GameDirectory = path;
            else
                GameDirectory = SteamGameDir;
        }

        internal void Update(string userDir)
        {
            UserDirectory = Path.Combine(userDir, $"BeamNG.{Name})");

            if (!IsUserDirectoryValid())
                return;

            try
            {
                LoadIni(Path.Combine(UserRootDirectory, $"BeamNG.{Name}.ini"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load BeamNG.{Name}.ini: {ex.Message}");
            }
        }

    }

    private static string _userDirectory;

    public static Product Drive { get; }

    public static Product Tech { get; }

    public static string UserRootDirectory { 
        get => _userDirectory;
        [MemberNotNull(nameof(_userDirectory))]
        set {
            _userDirectory = value;
            Drive.Update(_userDirectory);
            Tech.Update(_userDirectory);
        }
    }

    static BeamEnvironment()
    {
        Drive = new Product("drive");
        Tech = new Product("tech");
        UserRootDirectory = GetDefaultUserRootDirectory();
    }

    [MemberNotNullWhen(true, nameof(UserRootDirectory))]
    public static bool IsUserDirectoryValid()
    {
        if (UserRootDirectory == null)
            return false;

        if (!Directory.Exists(UserRootDirectory))
            return false;

        return true;
    }

    public static string GetDefaultUserRootDirectory()
    {
        return GetDefaultUserRootDirectory(Environment.UserName);
    }

    public static string GetDefaultUserRootDirectory(string userName)
    {
        return $"C:\\Users\\{userName}\\AppData\\Local\\BeamNG";
    }
}
 