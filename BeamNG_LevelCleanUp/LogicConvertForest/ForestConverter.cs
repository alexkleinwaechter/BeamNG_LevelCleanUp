using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicConvertForest
{
    public class ForestConverter
    {
        private readonly List<Asset> _assetsToConvert;
        private readonly List<ForestInfo> _forestInfoList;
        public ForestConverter(List<Asset> assetsToConvert,
            List<ForestInfo> forestInfoList) { 
            _assetsToConvert = assetsToConvert.OrderBy(o => o.DaePath).ToList();
            _forestInfoList = forestInfoList.OrderBy(o => o.DaePath).ToList();
        }

        public void Convert() {
            foreach (var item in _assetsToConvert)
            {
                var existsAsForest = _forestInfoList.Where(a => a.DaePath.Equals(item.DaePath, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (existsAsForest != null)
                {
                    if(existsAsForest.UsedInFiles.Any())
                    {
                        // add to existing forest file
                    }
                    else
                    {
                        // create new forest file and add
                    }
                }
                else
                {
                    // create new type in managedItems file and add to new forest file
                }
            }
        }
    }
}
