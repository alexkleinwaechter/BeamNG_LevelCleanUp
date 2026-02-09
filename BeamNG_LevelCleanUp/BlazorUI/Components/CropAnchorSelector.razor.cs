using System.Globalization;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
///     Interactive crop/selection component for GeoTIFF images.
///     Allows the user to drag a selection rectangle to choose which part of a larger GeoTIFF to extract.
///     Takes into account meters per pixel to calculate the correct selection size in source pixels.
/// </summary>
public partial class CropAnchorSelector
{
    // Minimap display constants
    private const int MaxMinimapSize = 300;
    private const int MinMinimapSize = 200;

    // Zoom state for minimap
    private float _minimapZoomLevel = 1.0f;
    private (float X, float Y) _minimapViewCenter = (0.5f, 0.5f);
    private GeoBoundingBox? _minimapEffectiveBoundingBox;

    private int _dragStartOffsetX;
    private int _dragStartOffsetY;
    private double _dragStartX;
    private double _dragStartY;

    // Dragging state
    private bool _isDragging;
    private bool _isInitialized;
    private float _mapOpacity = 0.85f;
    private ElementReference _minimapElement;

    // Flag to track if we need to fire event after render
    private bool _needsEventNotification;
    private GeoBoundingBox? _previousBoundingBox;
    private float _previousMetersPerPixel;
    private float _previousNativePixelSizeMeters;
    private int _previousOriginalHeight;

    // Track previous parameter values to detect changes
    private int _previousOriginalWidth;
    private int _previousTargetSize;

    // Calculated selection bounding box (updated on offset change)
    private GeoBoundingBox? _selectionBoundingBox;

    // OSM map background toggle
    private bool _showOsmBackground = true;

    // Injected JS Runtime for clipboard and window.open
    [Inject] private IJSRuntime JS { get; set; } = default!;
    
    // Injected Dialog Service for fullscreen crop dialog
    [Inject] private IDialogService DialogService { get; set; } = default!;

    /// <summary>
    ///     Title displayed at the top of the component.
    /// </summary>
    [Parameter]
    public string Title { get; set; } = "GeoTIFF Selection";

    /// <summary>
    ///     Original width of the GeoTIFF in pixels.
    /// </summary>
    [Parameter]
    public int OriginalWidth { get; set; }

    /// <summary>
    ///     Original height of the GeoTIFF in pixels.
    /// </summary>
    [Parameter]
    public int OriginalHeight { get; set; }

    /// <summary>
    ///     Target terrain size in pixels (square output).
    /// </summary>
    [Parameter]
    public int TargetSize { get; set; } = 2048;

    /// <summary>
    ///     Callback when target size changes.
    /// </summary>
    [Parameter]
    public EventCallback<int> TargetSizeChanged { get; set; }

    /// <summary>
    ///     Meters per pixel in the target terrain.
    ///     This affects how many source pixels are needed for the selection.
    /// </summary>
    [Parameter]
    public float MetersPerPixel { get; set; } = 1.0f;

    /// <summary>
    ///     Native pixel size of the GeoTIFF source in meters.
    ///     If the source is 30m/px and we want 1m/px output, we need to select a smaller area.
    /// </summary>
    [Parameter]
    public float NativePixelSizeMeters { get; set; } = 1.0f;

    /// <summary>
    ///     The original bounding box from the GeoTIFF (in WGS84 coordinates).
    /// </summary>
    [Parameter]
    public GeoBoundingBox? OriginalBoundingBox { get; set; }

    /// <summary>
    ///     Callback when the selection region changes.
    /// </summary>
    [Parameter]
    public EventCallback<CropResult> CropResultChanged { get; set; }

    /// <summary>
    ///     Currently selected anchor position (kept for backwards compatibility but not used in new UI).
    /// </summary>
    [Parameter]
    public CropAnchor SelectedAnchor { get; set; } = CropAnchor.Center;

    /// <summary>
    ///     Callback when anchor selection changes.
    /// </summary>
    [Parameter]
    public EventCallback<CropAnchor> SelectedAnchorChanged { get; set; }

    /// <summary>
    ///     The selection width in source pixels, calculated from terrain size and scale factors.
    /// </summary>
    public int SelectionWidthPixels => CalculateSelectionSizePixels();

    /// <summary>
    ///     The selection height in source pixels (same as width for square terrain).
    /// </summary>
    public int SelectionHeightPixels => CalculateSelectionSizePixels();

    /// <summary>
    ///     Whether a subset selection is needed (selection smaller than source).
    /// </summary>
    public bool NeedsSelection => SelectionWidthPixels < OriginalWidth || SelectionHeightPixels < OriginalHeight;

    /// <summary>
    ///     Calculated X offset for the crop region (in pixels from the original image's left edge).
    /// </summary>
    public int CropOffsetX { get; private set; }

    /// <summary>
    ///     Calculated Y offset for the crop region (in pixels from the original image's top edge).
    /// </summary>
    public int CropOffsetY { get; private set; }

    protected override void OnParametersSet()
    {
        // Detect if this is initial load or if key parameters changed
        var isNewGeoTiff = OriginalWidth != _previousOriginalWidth ||
                           OriginalHeight != _previousOriginalHeight ||
                           !ReferenceEquals(OriginalBoundingBox, _previousBoundingBox);

        var selectionSizeChanged = TargetSize != _previousTargetSize ||
                                   Math.Abs(MetersPerPixel - _previousMetersPerPixel) > 0.001f ||
                                   Math.Abs(NativePixelSizeMeters - _previousNativePixelSizeMeters) > 0.001f;

        // Calculate the OLD selection center (in source pixels) before updating values
        // This allows us to keep the geographic center when selection size changes
        var oldSelectionCenterX = 0.0;
        var oldSelectionCenterY = 0.0;
        var hadValidOldSelection = false;

        if (selectionSizeChanged && _isInitialized && _previousOriginalWidth > 0 && _previousOriginalHeight > 0)
        {
            // Calculate old selection size using previous parameters
            var oldNativePixelSize = _previousNativePixelSizeMeters > 0 ? _previousNativePixelSizeMeters : 1.0f;
            var oldTargetMeters = _previousTargetSize * _previousMetersPerPixel;
            var oldSelectionSize = (int)Math.Ceiling(oldTargetMeters / oldNativePixelSize);
            oldSelectionSize = Math.Min(oldSelectionSize, Math.Min(_previousOriginalWidth, _previousOriginalHeight));

            // Calculate the center of the old selection
            oldSelectionCenterX = CropOffsetX + oldSelectionSize / 2.0;
            oldSelectionCenterY = CropOffsetY + oldSelectionSize / 2.0;
            hadValidOldSelection = true;
        }

        // Store current values for next comparison
        _previousOriginalWidth = OriginalWidth;
        _previousOriginalHeight = OriginalHeight;
        _previousTargetSize = TargetSize;
        _previousMetersPerPixel = MetersPerPixel;
        _previousNativePixelSizeMeters = NativePixelSizeMeters;
        _previousBoundingBox = OriginalBoundingBox;

        // If a new GeoTIFF was loaded, center the selection and reset zoom
        if (isNewGeoTiff && OriginalWidth > 0 && OriginalHeight > 0)
        {
            CenterSelection();
            ResetAllZoomStates();
            _isInitialized = true;
            _needsEventNotification = true; // Mark for notification in OnAfterRenderAsync
        }
        else if (selectionSizeChanged && OriginalWidth > 0 && OriginalHeight > 0)
        {
            // Selection size changed due to MetersPerPixel or TargetSize change
            // Try to keep the GEOGRAPHIC CENTER the same by adjusting offset

            if (hadValidOldSelection)
            {
                // Calculate new selection size
                var newSelectionSize = CalculateSelectionSizePixels();

                // Calculate new offset to keep the same center
                CropOffsetX = (int)Math.Round(oldSelectionCenterX - newSelectionSize / 2.0);
                CropOffsetY = (int)Math.Round(oldSelectionCenterY - newSelectionSize / 2.0);
            }

            // Clamp offsets to valid range (in case selection grew larger than source)
            ClampOffsets();
            RecalculateSelectionBoundingBox();

            // Only notify if we have valid data to report
            if (_isInitialized) _needsEventNotification = true; // Mark for notification in OnAfterRenderAsync
        }
        else if (OriginalWidth > 0 && OriginalHeight > 0)
        {
            // Just recalculate bounding box for any other changes
            RecalculateSelectionBoundingBox();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Fire the event after render when we have pending notifications
        // This ensures the parent gets the updated crop result after any parameter change
        if (_needsEventNotification && _isInitialized && OriginalWidth > 0 && OriginalHeight > 0)
        {
            _needsEventNotification = false;
            await NotifyCropResultChanged();
        }
    }

    /// <summary>
    ///     Called when parameters change that affect selection size.
    ///     This should be called by the parent when MetersPerPixel or TargetSize changes.
    /// </summary>
    public async Task OnSelectionParametersChanged()
    {
        ClampOffsets();
        RecalculateSelectionBoundingBox();
        await NotifyCropResultChanged();
        StateHasChanged();
    }

    /// <summary>
    ///     Calculates how many source pixels we need to select based on:
    ///     - Target terrain size (e.g., 2048 px)
    ///     - Target meters per pixel (e.g., 1.0 m/px = 2048m terrain)
    ///     - Source native pixel size (e.g., 30 m/px)
    ///     Formula: selectionPixels = (targetSize * metersPerPixel) / nativePixelSize
    ///     Example: (2048 * 1.0) / 30 = 68 source pixels needed
    /// </summary>
    private int CalculateSelectionSizePixels()
    {
        if (NativePixelSizeMeters <= 0)
            return TargetSize; // Fallback to 1:1 if no native size

        // Calculate how many meters the target terrain represents
        var targetMeters = TargetSize * MetersPerPixel;

        // Calculate how many source pixels that corresponds to
        var selectionPixels = (int)Math.Ceiling(targetMeters / NativePixelSizeMeters);

        // Clamp to source dimensions
        return Math.Min(selectionPixels, Math.Min(OriginalWidth, OriginalHeight));
    }

    /// <summary>
    ///     Centers the selection in the source image.
    /// </summary>
    private void CenterSelection()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        CropOffsetX = Math.Max(0, (OriginalWidth - selW) / 2);
        CropOffsetY = Math.Max(0, (OriginalHeight - selH) / 2);

        ClampOffsets();
        RecalculateSelectionBoundingBox();
    }

    /// <summary>
    ///     Ensures offsets don't go out of bounds.
    /// </summary>
    private void ClampOffsets()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        CropOffsetX = Math.Max(0, Math.Min(CropOffsetX, OriginalWidth - selW));
        CropOffsetY = Math.Max(0, Math.Min(CropOffsetY, OriginalHeight - selH));
    }

    /// <summary>
    ///     Recalculates the geographic bounding box for the current selection.
    /// </summary>
    private void RecalculateSelectionBoundingBox()
    {
        if (OriginalBoundingBox is not { } bbox || OriginalWidth <= 0 || OriginalHeight <= 0)
        {
            _selectionBoundingBox = null;
            return;
        }

        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        // Calculate the fraction of the original image that we're selecting
        var leftFraction = (double)CropOffsetX / OriginalWidth;
        var rightFraction = (double)(CropOffsetX + selW) / OriginalWidth;
        var topFraction = (double)CropOffsetY / OriginalHeight;
        var bottomFraction = (double)(CropOffsetY + selH) / OriginalHeight;

        // Calculate new bounding box coordinates
        var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
        var latRange = bbox.MaxLatitude - bbox.MinLatitude;

        var newMinLon = bbox.MinLongitude + lonRange * leftFraction;
        var newMaxLon = bbox.MinLongitude + lonRange * rightFraction;
        // Latitude: top of image = max latitude, so we subtract from max
        var newMaxLat = bbox.MaxLatitude - latRange * topFraction;
        var newMinLat = bbox.MaxLatitude - latRange * bottomFraction;

        _selectionBoundingBox = new GeoBoundingBox(newMinLon, newMinLat, newMaxLon, newMaxLat);
    }

    #region Mouse Drag Handling

    private void OnMinimapMouseDown(MouseEventArgs e)
    {
        _isDragging = true;
        _dragStartX = e.ClientX;
        _dragStartY = e.ClientY;
        _dragStartOffsetX = CropOffsetX;
        _dragStartOffsetY = CropOffsetY;
    }

    private async Task OnMinimapMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;

        // Calculate movement in screen pixels
        var deltaX = e.ClientX - _dragStartX;
        var deltaY = e.ClientY - _dragStartY;

        // Convert screen pixels to source pixels, accounting for zoom
        // When zoomed in, the effective scale is larger (pixels represent less source area)
        var baseScale = GetMinimapScale();
        var effectiveScale = baseScale * _minimapZoomLevel;
        var sourcePixelDeltaX = (int)(deltaX / effectiveScale);
        var sourcePixelDeltaY = (int)(deltaY / effectiveScale);

        // Update offsets
        CropOffsetX = _dragStartOffsetX + sourcePixelDeltaX;
        CropOffsetY = _dragStartOffsetY + sourcePixelDeltaY;

        ClampOffsets();
        RecalculateSelectionBoundingBox();

        StateHasChanged();
    }

    private async Task OnMinimapMouseUp(MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            await NotifyCropResultChanged();
        }
    }

    private async Task OnMinimapMouseLeave(MouseEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            await NotifyCropResultChanged();
        }
    }

    #endregion

    #region Coordinate Copy and Google Maps

    private async Task CopyCoordinatesToClipboard()
    {
        if (_selectionBoundingBox == null) return;

        // Format: center coordinates for easy pasting into Google Maps
        // Use InvariantCulture to ensure decimal point (.) is used, not comma
        var center = _selectionBoundingBox.Center;
        var text = string.Format(CultureInfo.InvariantCulture, "{0:F6}, {1:F6}", center.Latitude, center.Longitude);

        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private async Task OpenInGoogleMaps()
    {
        if (_selectionBoundingBox == null) return;

        var center = _selectionBoundingBox.Center;
        // Use Google Maps Search URL format with lat,lng as query
        // Format: https://www.google.com/maps/search/?api=1&query=lat,lng
        // Use InvariantCulture to ensure decimal point (.) is used, not comma
        var query = string.Format(CultureInfo.InvariantCulture, "{0:F6},{1:F6}", center.Latitude, center.Longitude);
        var url = $"https://www.google.com/maps/search/?api=1&query={query}";

        await JS.InvokeVoidAsync("window.open", url, "_blank");
    }

    #endregion

    #region Size Display Helpers

    private string GetSourceRealWorldSize()
    {
        if (NativePixelSizeMeters <= 0) return "unknown";

        var widthKm = OriginalWidth * NativePixelSizeMeters / 1000.0;
        var heightKm = OriginalHeight * NativePixelSizeMeters / 1000.0;

        return $"{widthKm:F1}km × {heightKm:F1}km";
    }

    private string GetSelectionRealWorldSize()
    {
        // The selection represents the target terrain size in meters
        var sizeKm = TargetSize * MetersPerPixel / 1000.0;
        return $"{sizeKm:F1}km × {sizeKm:F1}km";
    }

    #endregion

    #region Zoom Event Handlers

    /// <summary>
    /// Called when the minimap zoom level changes.
    /// </summary>
    private void OnMinimapZoomChanged(float newZoom)
    {
        _minimapZoomLevel = newZoom;
        StateHasChanged();
    }

    /// <summary>
    /// Called when the minimap view center changes (panning).
    /// </summary>
    private void OnMinimapViewCenterChanged((float X, float Y) newCenter)
    {
        _minimapViewCenter = newCenter;
        StateHasChanged();
    }

    /// <summary>
    /// Called when the minimap's effective bounding box changes.
    /// </summary>
    private void OnMinimapEffectiveBoundsChanged(GeoBoundingBox? bounds)
    {
        _minimapEffectiveBoundingBox = bounds;
        StateHasChanged();
    }

    /// <summary>
    /// Resets the minimap zoom to default.
    /// </summary>
    private void ResetMinimapZoom()
    {
        _minimapZoomLevel = 1.0f;
        _minimapViewCenter = (0.5f, 0.5f);
        _minimapEffectiveBoundingBox = null;
        StateHasChanged();
    }

    /// <summary>
    /// Resets all zoom states.
    /// Called when a new GeoTIFF is loaded.
    /// </summary>
    private void ResetAllZoomStates()
    {
        _minimapZoomLevel = 1.0f;
        _minimapViewCenter = (0.5f, 0.5f);
        _minimapEffectiveBoundingBox = null;
    }

    #endregion

    #region Minimap Rendering

    private double GetMinimapScale()
    {
        // Calculate scale to fit source image in minimap area
        var maxDimension = Math.Max(OriginalWidth, OriginalHeight);
        if (maxDimension <= 0) return 1.0;

        return (double)MaxMinimapSize / maxDimension;
    }

    private string GetMinimapContainerStyle()
    {
        return $"min-height: {MinMinimapSize}px;";
    }

    private string GetMinimapSourceStyle()
    {
        var scale = GetMinimapScale();
        var displayWidth = (int)(OriginalWidth * scale);
        var displayHeight = (int)(OriginalHeight * scale);

        return $"width: {displayWidth}px; height: {displayHeight}px;";
    }

    /// <summary>
    ///     Gets the minimap display width in pixels (for OsmMapTileBackground component).
    /// </summary>
    private int GetMinimapDisplayWidth()
    {
        var scale = GetMinimapScale();
        return (int)(OriginalWidth * scale);
    }

    /// <summary>
    ///     Gets the minimap display height in pixels (for OsmMapTileBackground component).
    /// </summary>
    private int GetMinimapDisplayHeight()
    {
        var scale = GetMinimapScale();
        return (int)(OriginalHeight * scale);
    }

    private string GetSelectionStyle()
    {
        return GetSelectionStyleWithZoom(
            GetMinimapScale(),
            _minimapZoomLevel,
            _minimapViewCenter,
            GetMinimapDisplayWidth(),
            GetMinimapDisplayHeight());
    }

    /// <summary>
    /// Calculates the selection style accounting for zoom and pan state.
    /// When zoomed, the selection rectangle position/size must account for the visible portion.
    /// 
    /// COORDINATE SYSTEM NOTE:
    /// - ViewCenter uses geographic convention: Y=0 is south (bottom), Y=1 is north (top)
    /// - Pixel coordinates use screen convention: Y=0 is top, Y=OriginalHeight is bottom
    /// - Therefore we must INVERT the Y axis when converting ViewCenter.Y to pixel coordinates
    /// </summary>
    private string GetSelectionStyleWithZoom(double baseScale, float zoomLevel, (float X, float Y) viewCenter, int displayWidth, int displayHeight)
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        // If not zoomed, use simple calculation
        if (zoomLevel <= 1.01f)
        {
            var simpleDisplayWidth = Math.Max(10, (int)(selW * baseScale));
            var simpleDisplayHeight = Math.Max(10, (int)(selH * baseScale));
            var simpleDisplayLeft = (int)(CropOffsetX * baseScale);
            var simpleDisplayTop = (int)(CropOffsetY * baseScale);
            return $"width: {simpleDisplayWidth}px; height: {simpleDisplayHeight}px; left: {simpleDisplayLeft}px; top: {simpleDisplayTop}px;";
        }

        // When zoomed, calculate visible portion in source pixels
        var visibleSourceWidth = OriginalWidth / zoomLevel;
        var visibleSourceHeight = OriginalHeight / zoomLevel;

        // Calculate the center of visible area in source pixels
        // X: ViewCenter.X = 0 -> left (pixel 0), ViewCenter.X = 1 -> right (pixel OriginalWidth)
        var visibleCenterX = OriginalWidth * viewCenter.X;
        // Y: ViewCenter.Y = 0 -> south/bottom (pixel OriginalHeight), ViewCenter.Y = 1 -> north/top (pixel 0)
        // We need to INVERT the Y axis: pixelY = OriginalHeight * (1 - ViewCenter.Y)
        var visibleCenterY = OriginalHeight * (1.0f - viewCenter.Y);
        
        var visibleLeft = visibleCenterX - visibleSourceWidth / 2;
        var visibleTop = visibleCenterY - visibleSourceHeight / 2;

        // Calculate selection position relative to visible area
        var relativeLeft = CropOffsetX - visibleLeft;
        var relativeTop = CropOffsetY - visibleTop;

        // Scale from visible source area to display pixels
        var scaleX = displayWidth / visibleSourceWidth;
        var scaleY = displayHeight / visibleSourceHeight;

        var displayLeft = (int)(relativeLeft * scaleX);
        var displayTop = (int)(relativeTop * scaleY);
        var scaledWidth = Math.Max(10, (int)(selW * scaleX));
        var scaledHeight = Math.Max(10, (int)(selH * scaleY));

        // Check if selection is visible (intersects with display area)
        if (displayLeft + scaledWidth < 0 || displayLeft > displayWidth ||
            displayTop + scaledHeight < 0 || displayTop > displayHeight)
        {
            return "display: none;"; // Selection is outside visible area
        }

        return $"width: {scaledWidth}px; height: {scaledHeight}px; left: {displayLeft}px; top: {displayTop}px;";
    }

    #endregion

    #region Fullscreen Dialog

    /// <summary>
    ///     Opens the fullscreen crop dialog for more precise selection.
    /// </summary>
    private async Task OpenFullScreenDialog()
    {
        var parameters = new DialogParameters<CropAnchorSelectorDialog>
        {
            { x => x.Title, Title },
            { x => x.OriginalWidth, OriginalWidth },
            { x => x.OriginalHeight, OriginalHeight },
            { x => x.TargetSize, TargetSize },
            { x => x.MetersPerPixel, MetersPerPixel },
            { x => x.NativePixelSizeMeters, NativePixelSizeMeters },
            { x => x.OriginalBoundingBox, OriginalBoundingBox },
            { x => x.InitialOffsetX, CropOffsetX },
            { x => x.InitialOffsetY, CropOffsetY }
        };

        var options = new DialogOptions
        {
            FullScreen = true,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        var dialog = await DialogService.ShowAsync<CropAnchorSelectorDialog>(
            "GeoTIFF Selection", parameters, options);
        var result = await dialog.Result;

        if (result is { Canceled: false, Data: CropDialogResult cropResult })
        {
            // Propagate terrain size change to parent if it was modified in the dialog
            if (cropResult.TargetSize > 0 && cropResult.TargetSize != TargetSize)
            {
                TargetSize = cropResult.TargetSize;

                // CRITICAL: Sync _previousTargetSize so that the upcoming OnParametersSet
                // (triggered by the parent re-render after TargetSizeChanged) does NOT
                // detect a TargetSize change and overwrite our dialog offsets with its
                // own center-preserving recalculation.
                _previousTargetSize = TargetSize;

                await TargetSizeChanged.InvokeAsync(TargetSize);
            }

            // Apply the exact offsets from the dialog
            CropOffsetX = cropResult.OffsetX;
            CropOffsetY = cropResult.OffsetY;
            ClampOffsets();
            RecalculateSelectionBoundingBox();
            await NotifyCropResultChanged();
            StateHasChanged();
        }
    }

    #endregion

    #region Crop Result

    private async Task NotifyCropResultChanged()
    {
        var result = CalculateCropResult();
        await CropResultChanged.InvokeAsync(result);
    }

    /// <summary>
    ///     Sets the crop offsets programmatically from a preset import.
    ///     This method allows external code to restore saved crop settings.
    /// </summary>
    /// <param name="offsetX">X offset in source pixels</param>
    /// <param name="offsetY">Y offset in source pixels</param>
    /// <param name="notifyChange">If true, fires the CropResultChanged event</param>
    public async Task SetCropOffsetsAsync(int offsetX, int offsetY, bool notifyChange = true)
    {
        CropOffsetX = offsetX;
        CropOffsetY = offsetY;
        ClampOffsets();
        RecalculateSelectionBoundingBox();

        if (notifyChange) await NotifyCropResultChanged();

        StateHasChanged();
    }

    /// <summary>
    ///     Calculates the complete crop result including adjusted bounding box.
    /// </summary>
    public CropResult CalculateCropResult()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        return new CropResult
        {
            OffsetX = CropOffsetX,
            OffsetY = CropOffsetY,
            CropWidth = selW,
            CropHeight = selH,
            TargetSize = TargetSize,
            NeedsCropping = NeedsSelection,
            CroppedBoundingBox = _selectionBoundingBox,
            Anchor = CropAnchor.Center // Not used in new UI but kept for compatibility
        };
    }

    #endregion
}

/// <summary>
///     Anchor positions for cropping (kept for backwards compatibility).
/// </summary>
public enum CropAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    CenterLeft,
    Center,
    CenterRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
///     Result of crop calculation including adjusted bounding box.
/// </summary>
public class CropResult
{
    /// <summary>
    ///     X offset in pixels from the left edge of the original image.
    /// </summary>
    public int OffsetX { get; init; }

    /// <summary>
    ///     Y offset in pixels from the top edge of the original image.
    /// </summary>
    public int OffsetY { get; init; }

    /// <summary>
    ///     Width of the cropped region in source pixels.
    /// </summary>
    public int CropWidth { get; init; }

    /// <summary>
    ///     Height of the cropped region in source pixels.
    /// </summary>
    public int CropHeight { get; init; }

    /// <summary>
    ///     The target terrain size in output pixels.
    /// </summary>
    public int TargetSize { get; init; }

    /// <summary>
    ///     Whether any cropping/selection is needed.
    /// </summary>
    public bool NeedsCropping { get; init; }

    /// <summary>
    ///     The bounding box adjusted for the selected region.
    ///     This is crucial for correct OSM feature alignment.
    /// </summary>
    public GeoBoundingBox? CroppedBoundingBox { get; init; }

    /// <summary>
    ///     The anchor position used (for compatibility, always Center in new UI).
    /// </summary>
    public CropAnchor Anchor { get; init; }

    /// <summary>
    ///     Minimum elevation in the selected region (in meters).
    ///     Set by the parent component after reading from GeoTIFF.
    /// </summary>
    public double? CroppedMinElevation { get; set; }

    /// <summary>
    ///     Maximum elevation in the selected region (in meters).
    ///     Set by the parent component after reading from GeoTIFF.
    /// </summary>
    public double? CroppedMaxElevation { get; set; }

    /// <summary>
    ///     Calculated elevation range (MaxElevation - MinElevation) for the selected region.
    /// </summary>
    public double? CroppedElevationRange =>
        CroppedMinElevation.HasValue && CroppedMaxElevation.HasValue
            ? CroppedMaxElevation.Value - CroppedMinElevation.Value
            : null;

    /// <summary>
    ///     Returns true if upscaling is needed (source selection smaller than target output).
    /// </summary>
    public bool NeedsUpscaling => CropWidth < TargetSize || CropHeight < TargetSize;
}