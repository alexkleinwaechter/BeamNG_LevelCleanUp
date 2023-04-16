using Newtonsoft.Json.Linq;
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
        public static JsonSerializerOptions GetJsonSerializerOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                AllowTrailingCommas = true,
                IncludeFields = true,
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
        }

        public static JsonDocumentOptions GetJsonDocumentOptions()
        {
            return new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
        }
    }
}
