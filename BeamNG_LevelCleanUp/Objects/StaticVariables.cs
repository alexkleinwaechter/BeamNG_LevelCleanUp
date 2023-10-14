﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Objects
{
    public enum ModContext
    {
        Levels,
        Vehicles,
        Other
    }

    public static class StaticVariables
    {
        public static bool ApplicationExitRequest { get; set; }
        public static ModContext ModContext { get; set; } = ModContext.Levels;
        public static string ModPathPart { get; set; } = "levels";
    }
}
