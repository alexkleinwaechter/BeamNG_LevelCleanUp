using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     Minimal representation of a GroundCover object for dependency extraction only.
///     The full JSON is preserved separately - this is only used to identify materials and DAE files.
/// </summary>
public class GroundCover
{
    [JsonPropertyName("name")] public string Name { get; set; }

    [JsonPropertyName("material")] public string Material { get; set; }

    [JsonPropertyName("Types")] public List<GroundCoverType> Types { get; set; }
}