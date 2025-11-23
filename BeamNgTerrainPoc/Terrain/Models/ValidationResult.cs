namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Result of terrain parameter validation.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Indicates if the validation passed
    /// </summary>
    public bool IsValid { get; set; }
    
    /// <summary>
    /// List of validation errors (prevent terrain creation)
    /// </summary>
    public List<string> Errors { get; set; } = new();
    
    /// <summary>
    /// List of validation warnings (don't prevent creation but indicate potential issues)
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
