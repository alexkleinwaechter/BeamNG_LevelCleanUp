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

    // Backdrop selection box state (spec §5)
    private SelectionRect? _backdropRect;
    private BackdropHandle? _backdropDragHandle;
    private SelectionRect? _backdropDragStartRect;
    private double _backdropDragStartX;
    private double _backdropDragStartY;
    private bool _needsBackdropNotification;

    /// <summary>
    ///     Edge/corner handles rendered around the backdrop box (Body is the box itself, dragged via
    ///     its own mousedown handler — not part of this list).
    /// </summary>
    private static readonly BackdropHandle[] BackdropHandles =
    [
        BackdropHandle.N, BackdropHandle.S, BackdropHandle.E, BackdropHandle.W,
        BackdropHandle.NE, BackdropHandle.NW, BackdropHandle.SE, BackdropHandle.SW
    ];

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
    ///     Callback when meters per pixel changes (e.g., from bbox input in fullscreen dialog).
    /// </summary>
    [Parameter]
    public EventCallback<float> MetersPerPixelChanged { get; set; }

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
    ///     Enables the backdrop selection box (spec §5). Default-off: when false, nothing renders and
    ///     the terrain box drag/resize flows are entirely unaffected.
    /// </summary>
    [Parameter]
    public bool BackdropEnabled { get; set; }

    /// <summary>
    ///     The backdrop selection rect (in source pixels). Used as the seed value on enable/initial
    ///     load; live drag state is kept in the internal <c>_backdropRect</c> field and reported back
    ///     via <see cref="BackdropSelectionChanged" /> — see also <see cref="SetBackdropSelectionAsync" />
    ///     for imperative updates (preset restore).
    /// </summary>
    [Parameter]
    public SelectionRect? BackdropSelection { get; set; }

    /// <summary>
    ///     Callback when the backdrop selection rect changes (drag end).
    /// </summary>
    [Parameter]
    public EventCallback<SelectionRect> BackdropSelectionChanged { get; set; }

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

        // Backdrop box (spec §5): initialize the first time it's enabled and still empty (covers
        // both "enabled after the GeoTIFF was already loaded" and "enabled before load, then the
        // GeoTIFF arrives" — the emptiness check makes this naturally idempotent, so no separate
        // "just flipped" edge tracking is needed), otherwise keep it live-clamped against the
        // (possibly just-changed) terrain rect. Runs after every branch above so a "recenter" from
        // any of them (new GeoTIFF, selection size change) is covered uniformly.
        if (OriginalWidth > 0 && OriginalHeight > 0)
        {
            var backdropIsEmpty = _backdropRect is not { Width: > 0, Height: > 0 };

            if (BackdropEnabled && backdropIsEmpty)
            {
                var seed = BackdropSelection is { Width: > 0, Height: > 0 } sel
                    ? sel
                    : SelectionGeometry.DefaultBackdropRect(TerrainRect(), OriginalWidth, OriginalHeight);
                _backdropRect = SelectionGeometry.ClampBackdropRect(seed, TerrainRect(), OriginalWidth, OriginalHeight);
                _needsBackdropNotification = true;
            }
            else
            {
                ReclampBackdropRect();
            }
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

        if (_needsBackdropNotification && _backdropRect is { } bd)
        {
            _needsBackdropNotification = false;
            await BackdropSelectionChanged.InvokeAsync(bd);
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
        ReclampBackdropRect();
        await NotifyCropResultChanged();
        StateHasChanged();
    }

    /// <summary>
    ///     The current terrain selection rect in source pixels — the containment target for the
    ///     backdrop box (spec §5).
    /// </summary>
    private SelectionRect TerrainRect() => new(CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels);

    /// <summary>
    ///     Re-clamps the backdrop box against the current terrain rect + mosaic bounds. A no-op when
    ///     the backdrop box hasn't been initialized yet (feature disabled or not yet enabled).
    /// </summary>
    private void ReclampBackdropRect()
    {
        if (_backdropRect is not { } bd) return;
        _backdropRect = SelectionGeometry.ClampBackdropRect(bd, TerrainRect(), OriginalWidth, OriginalHeight);
    }

    /// <summary>
    ///     Calculates how many source pixels we need to select. Delegates to
    ///     <see cref="SelectionGeometry.CalculateSelectionSizePixels" /> (spec §5 de-duplication).
    /// </summary>
    private int CalculateSelectionSizePixels() =>
        SelectionGeometry.CalculateSelectionSizePixels(TargetSize, MetersPerPixel, NativePixelSizeMeters,
            OriginalWidth, OriginalHeight);

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
    ///     Ensures offsets don't go out of bounds. Delegates to
    ///     <see cref="SelectionGeometry.ClampOffsets" /> (spec §5 de-duplication).
    /// </summary>
    private void ClampOffsets()
    {
        (CropOffsetX, CropOffsetY) = SelectionGeometry.ClampOffsets(
            CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels, OriginalWidth, OriginalHeight);
    }

    /// <summary>
    ///     Recalculates the geographic bounding box for the current selection. Delegates to
    ///     <see cref="SelectionGeometry.PixelRectToBoundingBox" /> (spec §5 de-duplication).
    /// </summary>
    private void RecalculateSelectionBoundingBox()
    {
        _selectionBoundingBox = SelectionGeometry.PixelRectToBoundingBox(
            OriginalBoundingBox, OriginalWidth, OriginalHeight, CropOffsetX, CropOffsetY,
            SelectionWidthPixels, SelectionHeightPixels);
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
        // Backdrop box drag takes priority: its mousedown handlers stopPropagation so this only
        // fires when a backdrop handle/body drag is in progress (mutually exclusive with the
        // terrain box drag below, which starts from the minimap-source's own mousedown).
        if (_backdropDragHandle is { } handle && _backdropDragStartRect is { } startRect)
        {
            var backdropDeltaX = e.ClientX - _backdropDragStartX;
            var backdropDeltaY = e.ClientY - _backdropDragStartY;

            var backdropBaseScale = GetMinimapScale();
            var (backdropSourceDeltaX, backdropSourceDeltaY) =
                SelectionGeometry.ScreenDeltaToSourceDelta(backdropDeltaX, backdropDeltaY, backdropBaseScale, _minimapZoomLevel);

            _backdropRect = SelectionGeometry.ResizeBackdropRect(startRect, handle,
                backdropSourceDeltaX, backdropSourceDeltaY, TerrainRect(), OriginalWidth, OriginalHeight);

            StateHasChanged();
            return;
        }

        if (!_isDragging) return;

        // Calculate movement in screen pixels
        var deltaX = e.ClientX - _dragStartX;
        var deltaY = e.ClientY - _dragStartY;

        // Convert screen pixels to source pixels, accounting for zoom
        // When zoomed in, the effective scale is larger (pixels represent less source area)
        var baseScale = GetMinimapScale();
        var (sourcePixelDeltaX, sourcePixelDeltaY) =
            SelectionGeometry.ScreenDeltaToSourceDelta(deltaX, deltaY, baseScale, _minimapZoomLevel);

        // Update offsets
        CropOffsetX = _dragStartOffsetX + sourcePixelDeltaX;
        CropOffsetY = _dragStartOffsetY + sourcePixelDeltaY;

        ClampOffsets();
        RecalculateSelectionBoundingBox();
        // Terrain box moved: keep the backdrop box's containment live (it may get pushed/squeezed).
        ReclampBackdropRect();

        StateHasChanged();
    }

    private async Task OnMinimapMouseUp(MouseEventArgs e)
    {
        if (_backdropDragHandle != null)
        {
            _backdropDragHandle = null;
            _backdropDragStartRect = null;
            if (_backdropRect is { } bd) await BackdropSelectionChanged.InvokeAsync(bd);
            return;
        }

        if (_isDragging)
        {
            _isDragging = false;
            await NotifyCropResultChanged();
        }
    }

    private async Task OnMinimapMouseLeave(MouseEventArgs e)
    {
        if (_backdropDragHandle != null)
        {
            _backdropDragHandle = null;
            _backdropDragStartRect = null;
            if (_backdropRect is { } bd) await BackdropSelectionChanged.InvokeAsync(bd);
            return;
        }

        if (_isDragging)
        {
            _isDragging = false;
            await NotifyCropResultChanged();
        }
    }

    /// <summary>
    ///     Starts a backdrop box drag (body move or edge/corner resize). Mirrors
    ///     <see cref="OnMinimapMouseDown" />; the handle identifies which border(s), if any, are
    ///     dragged (spec §5).
    /// </summary>
    private void OnBackdropMouseDown(MouseEventArgs e, BackdropHandle handle)
    {
        if (_backdropRect is not { } bd) return;

        _backdropDragHandle = handle;
        _backdropDragStartRect = bd;
        _backdropDragStartX = e.ClientX;
        _backdropDragStartY = e.ClientY;
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

        return $"{widthKm:F1}km � {heightKm:F1}km";
    }

    private string GetSelectionRealWorldSize()
    {
        // The selection represents the target terrain size in meters
        var sizeKm = TargetSize * MetersPerPixel / 1000.0;
        return $"{sizeKm:F1}km � {sizeKm:F1}km";
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
    /// Delegates to <see cref="SelectionGeometry.ComputeBoxRect" />/<see cref="SelectionGeometry.ToCssStyle" />
    /// (spec §5 de-duplication).
    /// </summary>
    private string GetSelectionStyleWithZoom(double baseScale, float zoomLevel, (float X, float Y) viewCenter, int displayWidth, int displayHeight)
    {
        var rect = SelectionGeometry.ComputeBoxRect(
            CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels,
            baseScale, zoomLevel, viewCenter, OriginalWidth, OriginalHeight, displayWidth, displayHeight);
        return SelectionGeometry.ToCssStyle(rect);
    }

    /// <summary>
    ///     Calculates the on-screen rect for the backdrop box, accounting for minimap zoom/pan state
    ///     (spec §5). Null when the backdrop box isn't initialized or falls outside the visible area.
    /// </summary>
    private (double Left, double Top, double Width, double Height)? GetBackdropBoxRect()
    {
        if (_backdropRect is not { } bd) return null;

        return SelectionGeometry.ComputeBoxRect(
            bd.OffsetX, bd.OffsetY, bd.Width, bd.Height,
            GetMinimapScale(), _minimapZoomLevel, _minimapViewCenter,
            OriginalWidth, OriginalHeight, GetMinimapDisplayWidth(), GetMinimapDisplayHeight());
    }

    /// <summary>
    ///     Inline CSS position (left/top only — width/height come from the .backdrop-handle CSS
    ///     class) for one backdrop resize handle, placed at the corresponding corner/edge midpoint of
    ///     the backdrop box, offset by half the handle size so it's centered on the border.
    /// </summary>
    private string GetBackdropHandleStyle(BackdropHandle handle)
    {
        if (GetBackdropBoxRect() is not { } r) return "display: none;";

        var (x, y) = handle switch
        {
            BackdropHandle.N => (r.Left + r.Width / 2, r.Top),
            BackdropHandle.S => (r.Left + r.Width / 2, r.Top + r.Height),
            BackdropHandle.E => (r.Left + r.Width, r.Top + r.Height / 2),
            BackdropHandle.W => (r.Left, r.Top + r.Height / 2),
            BackdropHandle.NE => (r.Left + r.Width, r.Top),
            BackdropHandle.NW => (r.Left, r.Top),
            BackdropHandle.SE => (r.Left + r.Width, r.Top + r.Height),
            BackdropHandle.SW => (r.Left, r.Top + r.Height),
            _ => (r.Left, r.Top)
        };

        const double halfHandleSize = 5;
        return $"left: {x - halfHandleSize}px; top: {y - halfHandleSize}px;";
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
            { x => x.InitialOffsetY, CropOffsetY },
            { x => x.BackdropEnabled, BackdropEnabled },
            { x => x.BackdropSelection, _backdropRect }
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
            // Propagate meters per pixel change to parent if it was modified in the dialog
            if (cropResult.MetersPerPixel.HasValue &&
                Math.Abs(cropResult.MetersPerPixel.Value - MetersPerPixel) > 0.05f)
            {
                MetersPerPixel = cropResult.MetersPerPixel.Value;
                _previousMetersPerPixel = MetersPerPixel;
                await MetersPerPixelChanged.InvokeAsync(MetersPerPixel);
            }

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

            // Apply the backdrop box the dialog returned (spec §5 dialog round-trip)
            if (BackdropEnabled && cropResult.BackdropSelection is { } bdSel)
            {
                _backdropRect = SelectionGeometry.ClampBackdropRect(bdSel, TerrainRect(), OriginalWidth, OriginalHeight);
                await BackdropSelectionChanged.InvokeAsync(_backdropRect);
            }
            else
            {
                ReclampBackdropRect();
            }

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
        // Terrain box moved: keep the backdrop box's containment live (spec §5).
        ReclampBackdropRect();

        if (notifyChange) await NotifyCropResultChanged();

        StateHasChanged();
    }

    /// <summary>
    ///     Sets the backdrop selection rect programmatically from a preset import (spec §5, Task 19).
    ///     Mirrors <see cref="SetCropOffsetsAsync" />. The rect is clamped against the current terrain
    ///     rect + mosaic bounds before being applied.
    /// </summary>
    /// <param name="rect">The backdrop rect to apply, in source pixels.</param>
    /// <param name="notifyChange">If true, fires the BackdropSelectionChanged event</param>
    public async Task SetBackdropSelectionAsync(SelectionRect rect, bool notifyChange = true)
    {
        _backdropRect = SelectionGeometry.ClampBackdropRect(rect, TerrainRect(), OriginalWidth, OriginalHeight);

        if (notifyChange) await BackdropSelectionChanged.InvokeAsync(_backdropRect);

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