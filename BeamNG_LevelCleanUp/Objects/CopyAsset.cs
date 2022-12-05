﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    internal enum AssetType
    {
        Road = 0
    }
    internal class CopyAsset
    {
        public AssetType AssetType { get; set; }
        public string Name { get; set; }
        public List<MaterialJson> Materials = new List<MaterialJson>();
        public string TargetPath { get; set; }
        public string SourceMaterialJsonPath { get; set; }
    }
}
