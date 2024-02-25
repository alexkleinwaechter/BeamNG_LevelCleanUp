using System.Text.Json;

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
