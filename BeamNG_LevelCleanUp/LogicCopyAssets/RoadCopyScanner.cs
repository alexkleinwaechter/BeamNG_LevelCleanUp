using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    internal class RoadCopyScanner
    {
        string _sourceLevelPath;
        string _sourceLevelName;
        string _targetLevelPath;
        List<CopyAsset> AssetsToCopy = new List<CopyAsset>();
        
        internal RoadCopyScanner(string sourceLevelPath, string sourceLevelName, string targetLevelPath) { 
            _sourceLevelPath = sourceLevelPath;
            _sourceLevelName= sourceLevelName;
            _targetLevelPath= targetLevelPath; 
        }

        internal void ReadRoadMaterials() { 
        
        }
    }
}
