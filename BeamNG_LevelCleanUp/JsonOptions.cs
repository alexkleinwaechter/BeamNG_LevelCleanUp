using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp
{
    public static class BeamJsonOptions
    {
        public static JsonSerializerOptions Get()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                IncludeFields = true,
                WriteIndented = true
            };
        }
    }
}
