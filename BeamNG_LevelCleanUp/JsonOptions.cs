using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp
{
    public static class BeamJsonOptions
    {
        public static JsonSerializerOptions GetJsonSerializerOptions(bool ignoreNullValues = false)
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                AllowTrailingCommas = true,
                IncludeFields = true,
                WriteIndented = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                DefaultIgnoreCondition = ignoreNullValues ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never
            };
        }

        public static JsonSerializerOptions GetJsonSerializerOneLineOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                AllowTrailingCommas = true,
                IncludeFields = true,
                WriteIndented = false,
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
