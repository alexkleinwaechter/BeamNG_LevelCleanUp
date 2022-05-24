using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MissionGroupScanner
    {
        private string _path { get; set; }
        internal MissionGroupScanner(string path)
        {
            _path = path;
        }

        internal void scanMissionGroupFile()
        {
            var assetList = new List<Asset>();
            foreach (string line in File.ReadLines(_path))
            {
                var jsonObject = JsonNode.Parse(line);
                var x = jsonObject!["Employee"]["Name"];
            }
        }
    }
}
