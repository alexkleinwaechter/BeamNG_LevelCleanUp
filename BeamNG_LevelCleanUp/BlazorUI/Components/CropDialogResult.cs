using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
/// Result returned from the CropAnchorSelectorDialog when the user confirms their selection.
/// Contains the crop offsets and the calculated bounding box for the selected region.
/// </summary>
public class CropDialogResult
{
    /// <summary>
    /// X offset in pixels from the left edge of the original image.
    /// </summary>
    public int OffsetX { get; init; }

    /// <summary>
    /// Y offset in pixels from the top edge of the original image.
    /// </summary>
    public int OffsetY { get; init; }

    /// <summary>
    /// The target terrain size in pixels. May differ from the original if the user changed it in the dialog.
    /// </summary>
    public int TargetSize { get; init; }

    /// <summary>
    /// The geographic bounding box for the selected region.
    /// This is recalculated based on the selection position within the original image.
    /// </summary>
    public GeoBoundingBox? SelectionBoundingBox { get; init; }
}
