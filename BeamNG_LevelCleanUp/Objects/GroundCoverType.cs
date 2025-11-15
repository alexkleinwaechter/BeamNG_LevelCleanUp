using System.Text.Json.Serialization;

namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
///     Minimal representation of a GroundCover type/variant for dependency extraction only.
///     Only includes properties needed to identify terrain layers and DAE files.
/// </summary>
public class GroundCoverType
{
    [JsonPropertyName("layer")] public string Layer { get; set; }

    [JsonPropertyName("shapeFilename")] public string ShapeFilename { get; set; }
}