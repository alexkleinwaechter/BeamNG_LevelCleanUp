using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    internal class AssetCopy
    {
        private List<Guid> _identifier { get; set; }
        private List<CopyAsset> _assetsToCopy = new List<CopyAsset>();

        internal AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList)
        {
            _identifier = identifier;
            _assetsToCopy = copyAssetList.Where(x => identifier.Contains(x.Identifier)).ToList();
        }

        internal void Copy()
        {

        }
    }
}
