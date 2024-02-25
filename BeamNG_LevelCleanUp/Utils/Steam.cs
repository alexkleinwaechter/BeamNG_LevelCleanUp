using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace BeamNG_LevelCleanUp.Utils
{
    public static class Steam
    {
        public static List<string> SteamGameDirs = new List<string>();
        public static string BeamInstallDir = string.Empty;
        public static string GetBeamInstallDir()
        {
            if (BeamInstallDir != string.Empty)
            {
                return BeamInstallDir;
            }
            try
            {
                SearchSteamPaths();
                foreach (var item in SteamGameDirs)
                {
                    var tryDir = item + "BeamNG.drive";
                    if (new DirectoryInfo(tryDir).Exists)
                    {
                        BeamInstallDir = tryDir;
                        return tryDir;
                    }
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void SearchSteamPaths()
        {
            SteamGameDirs.Clear();
            string steam32 = "SOFTWARE\\VALVE\\";
            string steam64 = "SOFTWARE\\Wow6432Node\\Valve\\";
            string steam32path;
            string steam64path;
            string config32path;
            string config64path;
            RegistryKey key32 = Registry.LocalMachine.OpenSubKey(steam32);
            RegistryKey key64 = Registry.LocalMachine.OpenSubKey(steam64);
            if (key64.ToString() == null || key64.ToString() == "")
            {
                foreach (string k32subKey in key32.GetSubKeyNames())
                {
                    using (RegistryKey subKey = key32.OpenSubKey(k32subKey))
                    {
                        steam32path = subKey.GetValue("InstallPath").ToString();
                        config32path = steam32path + "/steamapps/libraryfolders.vdf";
                        string driveRegex = @"[A-Z]:\\";
                        if (File.Exists(config32path))
                        {
                            string[] configLines = File.ReadAllLines(config32path);
                            foreach (var item in configLines)
                            {
                                Console.WriteLine("32:  " + item);
                                Match match = Regex.Match(item, driveRegex);
                                if (item != string.Empty && match.Success)
                                {
                                    string matched = match.ToString();
                                    string item2 = item.Substring(item.IndexOf(matched));
                                    item2 = item2.Replace("\\\\", "\\");
                                    item2 = item2.Replace("\"", "\\steamapps\\common\\");
                                    SteamGameDirs.Add(item2);
                                }
                            }
                            SteamGameDirs.Add(steam32path + "\\steamapps\\common\\");
                        }
                    }
                }
            }
            foreach (string k64subKey in key64.GetSubKeyNames())
            {
                using (RegistryKey subKey = key64.OpenSubKey(k64subKey))
                {
                    var installName = subKey.GetValueNames().FirstOrDefault(name => name.Contains("installpath"));
                    if (installName == null) { continue; }
                    steam64path = subKey.GetValue(installName).ToString();
                    config64path = steam64path + "/steamapps/libraryfolders.vdf";
                    string driveRegex = @"[A-Z]:\\";
                    if (File.Exists(config64path))
                    {
                        string[] configLines = File.ReadAllLines(config64path);
                        foreach (var item in configLines)
                        {
                            Console.WriteLine("64:  " + item);
                            Match match = Regex.Match(item, driveRegex);
                            if (item != string.Empty && match.Success)
                            {
                                string matched = match.ToString();
                                string item2 = item.Substring(item.IndexOf(matched));
                                item2 = item2.Replace("\\\\", "\\");
                                item2 = item2.Replace("\"", "\\steamapps\\common\\");
                                SteamGameDirs.Add(item2);
                            }
                        }
                        SteamGameDirs.Add(steam64path + "\\steamapps\\common\\");
                    }
                }
            }
        }
    }
}
