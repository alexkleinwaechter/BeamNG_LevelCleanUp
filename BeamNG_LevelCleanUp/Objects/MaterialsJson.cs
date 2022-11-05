﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public class MaterialJson
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string MapTo { get; set; }
        public List <FileInfo>? Files { get; set; }
    }
}
