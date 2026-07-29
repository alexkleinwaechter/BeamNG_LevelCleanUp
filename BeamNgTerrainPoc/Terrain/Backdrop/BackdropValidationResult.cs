namespace BeamNgTerrainPoc.Terrain.Backdrop;

public sealed class BackdropValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsValid => Errors.Count == 0;
}
