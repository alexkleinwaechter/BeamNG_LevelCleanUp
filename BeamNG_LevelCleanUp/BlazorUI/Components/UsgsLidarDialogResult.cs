using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Lidar;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public sealed record UsgsLidarDialogResult(
    GeoBoundingBox Bounds,
    IReadOnlyList<UsgsLidarProduct> Products);
