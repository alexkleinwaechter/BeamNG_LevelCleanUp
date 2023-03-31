using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class AppInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SteamUrl { get; set; }
        public string Manifest { get; set; }
        public string GameRoot { get; set; }
        public string Executable { get; set; }
        public string InstallDir { get; set; }

        public override string ToString()
        {
            return $"{Name} ({Id}) - {SteamUrl} - {Executable}";
        }
    }
}
