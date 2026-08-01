using System.Globalization;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using DialogResult = MudBlazor.DialogResult;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class CropAnchorSelectorDialog : IAsyncDisposable
{
    private string _bboxEastStr = "";
    private string _bboxNorthStr = "";

    // Bounding box manual input fields (Overpass order: south, west, north, east)
    private string _bboxSouthStr = "";

    private string _bboxWestStr = "";

    // Display dimensions - calculated dynamically based on container size
    private int _displayHeight;
    private int _displayWidth;
    private int _dragStartOffsetX;
    private int _dragStartOffsetY;
    private double _dragStartX;
    private double _dragStartY;
    private GeoBoundingBox? _effectiveBoundingBox;
    private bool _isDragging;
    private bool _isInitialized;
    private bool _isMouseOverSelection;
    private bool _isSyncingFromSelector;
    private DotNetObjectReference<CropAnchorSelectorDialog>? _jsRef;

    // Element reference for measuring container size
    private ElementReference _mapAreaRef;
    private float _mapOpacity = 0.85f;
    private GeoBoundingBox? _selectionBoundingBox;
    private (float X, float Y) _viewCenter = (0.5f, 0.5f);
    private float _zoomLevel = 1.0f;
    private int CropOffsetX;
    private int CropOffsetY;

    // Backdrop selection box state (spec §5)
    private SelectionRect? _backdropRect;
    private BackdropHandle? _backdropDragHandle;
    private SelectionRect? _backdropDragStartRect;
    private double _backdropDragStartX;
    private double _backdropDragStartY;
    private bool _isSyncingBackdropFromSelector;

    // True while the mouse hovers a backdrop handle or the backdrop body (not just while dragging
    // one) -- gates EnablePanning on the OSM background ahead of any mousedown, see the comment in
    // OnMouseMoveWithHitTest.
    private bool _isMouseOverBackdrop;

    // Backdrop bounding box manual input fields (Overpass order: south, west, north, east)
    private string _backdropSouthStr = "";
    private string _backdropWestStr = "";
    private string _backdropNorthStr = "";
    private string _backdropEastStr = "";

    /// <summary>
    ///     Edge/corner handles rendered around the backdrop box (Body is the box itself, dragged via
    ///     its own mousedown handler — not part of this list). Mirrors the array of the same name in
    ///     CropAnchorSelector.
    /// </summary>
    private static readonly BackdropHandle[] BackdropHandles =
    [
        BackdropHandle.N, BackdropHandle.S, BackdropHandle.E, BackdropHandle.W,
        BackdropHandle.NE, BackdropHandle.NW, BackdropHandle.SE, BackdropHandle.SW
    ];

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Title { get; set; } = "Selection";
    [Parameter] public int OriginalWidth { get; set; }
    [Parameter] public int OriginalHeight { get; set; }
    [Parameter] public int TargetSize { get; set; } = 2048;
    [Parameter] public float MetersPerPixel { get; set; } = 1.0f;
    [Parameter] public float NativePixelSizeMeters { get; set; } = 1.0f;
    [Parameter] public GeoBoundingBox? OriginalBoundingBox { get; set; }
    [Parameter] public int InitialOffsetX { get; set; }
    [Parameter] public int InitialOffsetY { get; set; }

    /// <summary>
    ///     Enables the backdrop selection box (spec §5). Default-off: when false, nothing renders.
    /// </summary>
    [Parameter]
    public bool BackdropEnabled { get; set; }

    /// <summary>
    ///     The initial backdrop selection rect (in source pixels), seeded from the caller when the
    ///     dialog opens. The final value is returned via <see cref="CropDialogResult.BackdropSelection" />
    ///     on Confirm — the dialog does not raise change notifications mid-drag.
    /// </summary>
    [Parameter]
    public SelectionRect? BackdropSelection { get; set; }

    public int SelectionWidthPixels => CalculateSelectionSizePixels();
    public int SelectionHeightPixels => CalculateSelectionSizePixels();

    public async ValueTask DisposeAsync()
    {
        if (_jsRef != null)
        {
            try
            {
                await JS.InvokeVoidAsync("removeCropDialogResizeObserver", _mapAreaRef);
            }
            catch
            {
                // Ignore errors during disposal
            }

            _jsRef.Dispose();
        }
    }

    protected override void OnInitialized()
    {
        CropOffsetX = InitialOffsetX;
        CropOffsetY = InitialOffsetY;
        ClampOffsets();
        RecalculateSelectionBoundingBox();
        UpdateBboxInputsFromSelection();

        // Seed the backdrop box (spec §5 dialog round-trip): use the caller-supplied selection if
        // it's non-empty, otherwise compute the default inflated box, then clamp against the
        // terrain rect established just above.
        if (BackdropEnabled && OriginalWidth > 0 && OriginalHeight > 0)
        {
            var seed = BackdropSelection is { Width: > 0, Height: > 0 } sel
                ? sel
                : SelectionGeometry.DefaultBackdropRect(TerrainRect(), OriginalWidth, OriginalHeight);
            _backdropRect = SelectionGeometry.ClampBackdropRect(seed, TerrainRect(), OriginalWidth, OriginalHeight);
            UpdateBackdropBboxInputs();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Wait a bit for the dialog to fully render
            await Task.Delay(50);

            // Calculate initial dimensions
            await CalculateDisplayDimensionsAsync();

            // Set up resize observer for responsive resizing
            _jsRef = DotNetObjectReference.Create(this);
            try
            {
                await JS.InvokeVoidAsync("setupCropDialogResizeObserver", _mapAreaRef, _jsRef);
            }
            catch
            {
                // Resize observer setup failed - dimensions already calculated, continue without it
            }

            _isInitialized = true;
        }
    }

    private async Task CalculateDisplayDimensionsAsync()
    {
        try
        {
            var size = await JS.InvokeAsync<ElementSize>("getElementSize", _mapAreaRef);

            if (size.Width > 0 && size.Height > 0)
            {
                CalculateMapDimensionsFromContainer(size.Width, size.Height);
                StateHasChanged();
            }
        }
        catch
        {
            // Fallback to reasonable defaults if JS interop fails
            _displayWidth = 1200;
            _displayHeight = 800;
            StateHasChanged();
        }
    }

    private void CalculateMapDimensionsFromContainer(double containerWidth, double containerHeight)
    {
        // Account for padding (16px on each side)
        const int padding = 32;
        var availableWidth = containerWidth - padding;
        var availableHeight = containerHeight - padding;

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            _displayWidth = 800;
            _displayHeight = 600;
            return;
        }

        if (OriginalWidth <= 0 || OriginalHeight <= 0)
        {
            // No source dimensions - use container size directly
            _displayWidth = (int)availableWidth;
            _displayHeight = (int)availableHeight;
            return;
        }

        // Maintain source aspect ratio
        var sourceAspect = (double)OriginalWidth / OriginalHeight;
        var containerAspect = availableWidth / availableHeight;

        if (sourceAspect > containerAspect)
        {
            // Width-limited: source is wider than container
            _displayWidth = (int)availableWidth;
            _displayHeight = (int)(availableWidth / sourceAspect);
        }
        else
        {
            // Height-limited: source is taller than container
            _displayHeight = (int)availableHeight;
            _displayWidth = (int)(availableHeight * sourceAspect);
        }

        // Ensure minimum size
        _displayWidth = Math.Max(_displayWidth, 400);
        _displayHeight = Math.Max(_displayHeight, 300);
    }

    [JSInvokable]
    public async Task OnContainerResized()
    {
        if (!_isInitialized) return;
        await CalculateDisplayDimensionsAsync();
    }

    private string GetMapWrapperStyle()
    {
        return $"width: {_displayWidth}px; height: {_displayHeight}px;";
    }

    /// <summary>
    ///     Calculates how many source pixels we need to select. Delegates to
    ///     <see cref="SelectionGeometry.CalculateSelectionSizePixels" /> (spec §5 de-duplication).
    /// </summary>
    private int CalculateSelectionSizePixels() =>
        SelectionGeometry.CalculateSelectionSizePixels(TargetSize, MetersPerPixel, NativePixelSizeMeters,
            OriginalWidth, OriginalHeight);

    private double GetScale()
    {
        var maxDimension = Math.Max(OriginalWidth, OriginalHeight);
        if (maxDimension <= 0) return 1.0;
        return (double)Math.Max(_displayWidth, _displayHeight) / maxDimension;
    }

    private float GetMaxZoom()
    {
        // Calculate how much zoom is needed so the selection rectangle fills
        // a reasonable portion of the display (at least MinSelectionDisplaySize pixels).
        // For large source images where the selection is tiny at zoom=1, we need MORE zoom,
        // not less. Zooming in makes the selection bigger on screen.
        const int DesiredSelectionDisplaySize = 200;
        const float AbsoluteMaxZoom = 50.0f;

        var scale = GetScale();
        var selectionDisplaySize = (int)(Math.Min(SelectionWidthPixels, SelectionHeightPixels) * scale);

        if (selectionDisplaySize <= 0) return 1.0f;

        // If the selection is already large enough at zoom=1, allow moderate zoom
        if (selectionDisplaySize >= DesiredSelectionDisplaySize)
            return Math.Min((float)selectionDisplaySize / DesiredSelectionDisplaySize * 4.0f, AbsoluteMaxZoom);

        // Selection is small at zoom=1 (large source image): allow enough zoom
        // to make the selection comfortably visible and positionable
        var neededZoom = (float)DesiredSelectionDisplaySize / selectionDisplaySize;
        // Allow extra zoom beyond just making it visible, capped at absolute max
        return Math.Min(neededZoom * 3.0f, AbsoluteMaxZoom);
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

    /// <summary>
    ///     Calculates the selection style accounting for zoom and pan state. Delegates to
    ///     <see cref="SelectionGeometry.ComputeBoxRect" />/<see cref="SelectionGeometry.ToCssStyle" />
    ///     (spec §5 de-duplication).
    /// </summary>
    private string GetSelectionStyle()
    {
        var rect = SelectionGeometry.ComputeBoxRect(
            CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels,
            GetScale(), _zoomLevel, _viewCenter, OriginalWidth, OriginalHeight, _displayWidth, _displayHeight);
        return SelectionGeometry.ToCssStyle(rect);
    }

    /// <summary>
    ///     The current terrain selection rect in source pixels — the containment target for the
    ///     backdrop box (spec §5).
    /// </summary>
    private SelectionRect TerrainRect() => new(CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels);

    /// <summary>
    ///     Re-clamps the backdrop box against the current terrain rect + mosaic bounds and re-syncs
    ///     the backdrop bbox text fields. A no-op when the backdrop box hasn't been initialized yet.
    /// </summary>
    private void ReclampBackdropRect()
    {
        if (_backdropRect is not { } bd) return;
        _backdropRect = SelectionGeometry.ClampBackdropRect(bd, TerrainRect(), OriginalWidth, OriginalHeight);
        UpdateBackdropBboxInputs();
    }

    /// <summary>
    ///     Calculates the on-screen rect for the backdrop box, accounting for zoom/pan state
    ///     (spec §5). Null when the backdrop box isn't initialized or falls outside the visible area.
    /// </summary>
    private (double Left, double Top, double Width, double Height)? GetBackdropBoxRect()
    {
        if (_backdropRect is not { } bd) return null;

        return SelectionGeometry.ComputeBoxRect(
            bd.OffsetX, bd.OffsetY, bd.Width, bd.Height,
            GetScale(), _zoomLevel, _viewCenter, OriginalWidth, OriginalHeight, _displayWidth, _displayHeight);
    }

    /// <summary>
    ///     Half the backdrop resize handle's rendered size (10x10px, see the .crop-backdrop-handle
    ///     CSS class) — shared between rendering (<see cref="GetBackdropHandleStyle" />) and hit
    ///     testing (<see cref="HitTestBackdropHandle" />) so the clickable area always matches what's
    ///     drawn.
    /// </summary>
    private const double BackdropHandleHalfSize = 5;

    /// <summary>
    ///     The on-screen center point of one backdrop resize handle, given the backdrop box's on-screen
    ///     rect (as returned by <see cref="GetBackdropBoxRect" />). Shared by rendering and hit testing.
    /// </summary>
    private static (double X, double Y) GetBackdropHandleCenter(
        BackdropHandle handle, (double Left, double Top, double Width, double Height) r)
    {
        return handle switch
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
    }

    /// <summary>
    ///     Inline CSS position (left/top only — width/height come from the .crop-backdrop-handle CSS
    ///     class) for one backdrop resize handle, placed at the corresponding corner/edge midpoint of
    ///     the backdrop box, offset by half the handle size so it's centered on the border. The handle
    ///     itself is <c>pointer-events: none</c> (purely visual) — hit testing for it happens in
    ///     <see cref="OnMouseDown" /> via <see cref="HitTestBackdropHandle" />, matching the dialog's
    ///     math-based hit model (unlike the selector, which uses real element mousedown handlers).
    /// </summary>
    private string GetBackdropHandleStyle(BackdropHandle handle)
    {
        if (GetBackdropBoxRect() is not { } r) return "display: none;";

        var (x, y) = GetBackdropHandleCenter(handle, r);
        return $"left: {x - BackdropHandleHalfSize}px; top: {y - BackdropHandleHalfSize}px;";
    }

    /// <summary>
    ///     Hit-tests the 8 backdrop resize handles at the dialog's mouse-relative coordinates (same
    ///     space as <see cref="IsMouseOverSelection" />'s offsetX/offsetY). Returns the first handle
    ///     whose 10x10px rect (centered on <see cref="GetBackdropHandleCenter" />) contains the point,
    ///     or null. Null when the backdrop box isn't initialized.
    /// </summary>
    private BackdropHandle? HitTestBackdropHandle(double offsetX, double offsetY)
    {
        if (GetBackdropBoxRect() is not { } r) return null;

        foreach (var handle in BackdropHandles)
        {
            var (hx, hy) = GetBackdropHandleCenter(handle, r);
            if (offsetX >= hx - BackdropHandleHalfSize && offsetX <= hx + BackdropHandleHalfSize &&
                offsetY >= hy - BackdropHandleHalfSize && offsetY <= hy + BackdropHandleHalfSize)
                return handle;
        }

        return null;
    }

    /// <summary>
    ///     Hit-tests whether the given point (same coordinate space as <see cref="IsMouseOverSelection" />)
    ///     falls inside the backdrop box — used for the backdrop Body (move) drag, lowest priority
    ///     of the three math-based hit tests in <see cref="OnMouseDown" />.
    /// </summary>
    private bool IsMouseOverBackdrop(double offsetX, double offsetY)
    {
        if (GetBackdropBoxRect() is not { } r) return false;
        return offsetX >= r.Left && offsetX <= r.Left + r.Width &&
               offsetY >= r.Top && offsetY <= r.Top + r.Height;
    }

    private (double Left, double Top, double Width, double Height) GetSelectionBounds()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;
        var baseScale = GetScale();

        if (_zoomLevel <= 1.01f)
            return (CropOffsetX * baseScale, CropOffsetY * baseScale,
                Math.Max(10, selW * baseScale), Math.Max(10, selH * baseScale));

        var visibleSourceWidth = OriginalWidth / _zoomLevel;
        var visibleSourceHeight = OriginalHeight / _zoomLevel;
        var visibleCenterX = OriginalWidth * _viewCenter.X;
        var visibleCenterY = OriginalHeight * (1.0f - _viewCenter.Y);
        var visibleLeft = visibleCenterX - visibleSourceWidth / 2;
        var visibleTop = visibleCenterY - visibleSourceHeight / 2;

        var relativeLeft = CropOffsetX - visibleLeft;
        var relativeTop = CropOffsetY - visibleTop;
        var scaleX = _displayWidth / visibleSourceWidth;
        var scaleY = _displayHeight / visibleSourceHeight;

        return (relativeLeft * scaleX, relativeTop * scaleY,
            Math.Max(10, selW * scaleX), Math.Max(10, selH * scaleY));
    }

    private bool IsMouseOverSelection(double offsetX, double offsetY)
    {
        var (selLeft, selTop, selWidth, selHeight) = GetSelectionBounds();
        if (selWidth <= 0 || selHeight <= 0) return false;
        return offsetX >= selLeft && offsetX <= selLeft + selWidth &&
               offsetY >= selTop && offsetY <= selTop + selHeight;
    }

    /// <summary>
    ///     Starts whichever drag the click landed on, in strict priority order that mirrors the
    ///     selector's visual z-order (backdrop handles z=11 &gt; terrain box z=10 &gt; backdrop body
    ///     z=9): (1) a backdrop resize handle, (2) the terrain box, (3) the backdrop body, else
    ///     nothing (pan). This is the dialog's math-based hit model — unlike the selector, the
    ///     backdrop box/handles here render with <c>pointer-events: none</c> and have NO mousedown
    ///     handlers of their own, specifically because the backdrop box always contains the terrain
    ///     box (spec §5 containment): an element with pointer-events:auto sitting on top would
    ///     intercept every click inside the terrain box's visual bounds and make terrain dragging
    ///     impossible in the dialog. Priority (1) also means that when a backdrop edge coincides
    ///     exactly with the terrain edge (zero margin on that side), the handle wins the click over
    ///     the terrain box — the handle hit test runs first and returns before IsMouseOverSelection
    ///     is even evaluated.
    /// </summary>
    private void OnMouseDown(MouseEventArgs e)
    {
        if (BackdropEnabled && _backdropRect is not null &&
            HitTestBackdropHandle(e.OffsetX, e.OffsetY) is { } handle)
        {
            OnBackdropMouseDown(e, handle);
            return;
        }

        if (IsMouseOverSelection(e.OffsetX, e.OffsetY))
        {
            _isDragging = true;
            _dragStartX = e.ClientX;
            _dragStartY = e.ClientY;
            _dragStartOffsetX = CropOffsetX;
            _dragStartOffsetY = CropOffsetY;
            return;
        }

        if (BackdropEnabled && _backdropRect is not null && IsMouseOverBackdrop(e.OffsetX, e.OffsetY))
            OnBackdropMouseDown(e, BackdropHandle.Body);
    }

    private void OnMouseMoveWithHitTest(MouseEventArgs e)
    {
        // Backdrop box drag takes priority: it's only in progress when OnMouseDown's hit test (above)
        // started one, so this is mutually exclusive with the terrain box drag below (which starts
        // from IsMouseOverSelection's hit test).
        if (_backdropDragHandle is { } handle && _backdropDragStartRect is { } startRect)
        {
            var backdropDeltaX = e.ClientX - _backdropDragStartX;
            var backdropDeltaY = e.ClientY - _backdropDragStartY;

            var backdropBaseScale = GetScale();
            var (backdropSourceDeltaX, backdropSourceDeltaY) =
                SelectionGeometry.ScreenDeltaToSourceDelta(backdropDeltaX, backdropDeltaY, backdropBaseScale, _zoomLevel);

            _backdropRect = SelectionGeometry.ResizeBackdropRect(startRect, handle,
                backdropSourceDeltaX, backdropSourceDeltaY, TerrainRect(), OriginalWidth, OriginalHeight);
            UpdateBackdropBboxInputs();
            StateHasChanged();
            return;
        }

        var wasOver = _isMouseOverSelection;
        _isMouseOverSelection = IsMouseOverSelection(e.OffsetX, e.OffsetY);

        // Track backdrop hover continuously (handles + body), not just at drag start: EnablePanning
        // is read synchronously by OsmMapTileBackground.HandleMouseDown on the SAME native mousedown
        // event (it fires on the tile container -- the real event target, underneath the now
        // pointer-events:none backdrop overlay -- before bubbling reaches this wrapper's OnMouseDown).
        // Gating panning only on drag state (set inside OnMouseDown, which runs after the child's
        // handler) would be one event too late, so this mirrors _isMouseOverSelection's continuous
        // mousemove tracking instead.
        var wasOverBackdrop = _isMouseOverBackdrop;
        _isMouseOverBackdrop = BackdropEnabled && _backdropRect is not null &&
            (HitTestBackdropHandle(e.OffsetX, e.OffsetY) is not null || IsMouseOverBackdrop(e.OffsetX, e.OffsetY));

        if (_isDragging)
        {
            var deltaX = e.ClientX - _dragStartX;
            var deltaY = e.ClientY - _dragStartY;

            var baseScale = GetScale();
            var (sourcePixelDeltaX, sourcePixelDeltaY) =
                SelectionGeometry.ScreenDeltaToSourceDelta(deltaX, deltaY, baseScale, _zoomLevel);

            CropOffsetX = _dragStartOffsetX + sourcePixelDeltaX;
            CropOffsetY = _dragStartOffsetY + sourcePixelDeltaY;

            ClampOffsets();
            RecalculateSelectionBoundingBox();
            UpdateBboxInputsFromSelection();
            // Terrain box moved: keep the backdrop box's containment live (it may get pushed/squeezed).
            ReclampBackdropRect();
            StateHasChanged();
        }
        else if (wasOver != _isMouseOverSelection || wasOverBackdrop != _isMouseOverBackdrop)
        {
            StateHasChanged();
        }
    }

    private void OnMouseUp(MouseEventArgs e)
    {
        _isDragging = false;
        _backdropDragHandle = null;
        _backdropDragStartRect = null;
    }

    private void OnMouseLeave(MouseEventArgs e)
    {
        _isDragging = false;
        _isMouseOverSelection = false;
        _isMouseOverBackdrop = false;
        _backdropDragHandle = null;
        _backdropDragStartRect = null;
        StateHasChanged();
    }

    /// <summary>
    ///     Starts a backdrop box drag (body move or edge/corner resize). Called from
    ///     <see cref="OnMouseDown" />'s hit-test chain (not from an element's own mousedown — the
    ///     backdrop box/handles are pointer-events:none in this dialog, see <see cref="OnMouseDown" />'s
    ///     doc comment for why); the handle identifies which border(s), if any, are dragged (spec §5).
    ///     Unlike the dialog's terrain box drag, mouse-up here just clears the drag state — the final
    ///     rect is returned via <see cref="CropDialogResult.BackdropSelection" /> on Confirm.
    /// </summary>
    private void OnBackdropMouseDown(MouseEventArgs e, BackdropHandle handle)
    {
        if (_backdropRect is not { } bd) return;

        _backdropDragHandle = handle;
        _backdropDragStartRect = bd;
        _backdropDragStartX = e.ClientX;
        _backdropDragStartY = e.ClientY;
    }

    private void OnZoomChanged(float newZoom)
    {
        _zoomLevel = newZoom;
        StateHasChanged();
    }

    private void OnViewCenterChanged((float X, float Y) newCenter)
    {
        _viewCenter = newCenter;
        StateHasChanged();
    }

    private void OnEffectiveBoundsChanged(GeoBoundingBox? bounds)
    {
        _effectiveBoundingBox = bounds;
        StateHasChanged();
    }

    private void ResetZoom()
    {
        _zoomLevel = 1.0f;
        _viewCenter = (0.5f, 0.5f);
        _effectiveBoundingBox = null;
        StateHasChanged();
    }

    private string GetSourceRealWorldSize()
    {
        if (NativePixelSizeMeters <= 0) return "unknown";
        var widthKm = OriginalWidth * NativePixelSizeMeters / 1000.0;
        var heightKm = OriginalHeight * NativePixelSizeMeters / 1000.0;
        return $"{widthKm:F1}km × {heightKm:F1}km";
    }

    private string GetSelectionRealWorldSize()
    {
        var sizeKm = TargetSize * MetersPerPixel / 1000.0;
        return $"{sizeKm:F1}km × {sizeKm:F1}km";
    }

    private async Task CopyCoordinatesToClipboard()
    {
        if (_selectionBoundingBox == null) return;
        var center = _selectionBoundingBox.Center;
        var text = string.Format(CultureInfo.InvariantCulture,
            "{0:F6}, {1:F6}", center.Latitude, center.Longitude);
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private async Task OpenInGoogleMaps()
    {
        if (_selectionBoundingBox == null) return;
        var center = _selectionBoundingBox.Center;
        var query = string.Format(CultureInfo.InvariantCulture,
            "{0:F6},{1:F6}", center.Latitude, center.Longitude);
        var url = $"https://www.google.com/maps/search/?api=1&query={query}";
        await JS.InvokeVoidAsync("window.open", url, "_blank");
    }

    private void OnTargetSizeChanged(int newSize)
    {
        if (newSize == TargetSize) return;

        // Calculate the center of the current selection in source pixels
        var oldSelectionSize = CalculateSelectionSizePixels();
        var centerX = CropOffsetX + oldSelectionSize / 2.0;
        var centerY = CropOffsetY + oldSelectionSize / 2.0;

        // Update terrain size
        TargetSize = newSize;

        // Recalculate offset to keep the same geographic center
        var newSelectionSize = CalculateSelectionSizePixels();
        CropOffsetX = (int)Math.Round(centerX - newSelectionSize / 2.0);
        CropOffsetY = (int)Math.Round(centerY - newSelectionSize / 2.0);

        ClampOffsets();
        RecalculateSelectionBoundingBox();
        UpdateBboxInputsFromSelection();
        // Terrain rect size changed: keep the backdrop box's containment live (spec §5).
        ReclampBackdropRect();
        StateHasChanged();
    }

    /// <summary>
    ///     Updates the bounding box text fields from the current graphical selection.
    ///     Called whenever the selection rectangle moves or the terrain size changes.
    /// </summary>
    private void UpdateBboxInputsFromSelection()
    {
        if (_selectionBoundingBox == null) return;

        _isSyncingFromSelector = true;
        _bboxSouthStr = _selectionBoundingBox.MinLatitude.ToString("F7", CultureInfo.InvariantCulture);
        _bboxWestStr = _selectionBoundingBox.MinLongitude.ToString("F7", CultureInfo.InvariantCulture);
        _bboxNorthStr = _selectionBoundingBox.MaxLatitude.ToString("F7", CultureInfo.InvariantCulture);
        _bboxEastStr = _selectionBoundingBox.MaxLongitude.ToString("F7", CultureInfo.InvariantCulture);
        _isSyncingFromSelector = false;
    }

    /// <summary>
    ///     Updates the backdrop bounding box text fields from the current backdrop box (spec §5).
    ///     Called whenever the backdrop box moves/resizes or the terrain box pushes it. Unlike the
    ///     terrain bbox fields, this is a plain rectangular mapping — no MetersPerPixel/TargetSize
    ///     re-derivation involved.
    /// </summary>
    private void UpdateBackdropBboxInputs()
    {
        if (_backdropRect is not { } bd) return;

        var bbox = SelectionGeometry.PixelRectToBoundingBox(
            OriginalBoundingBox, OriginalWidth, OriginalHeight, bd.OffsetX, bd.OffsetY, bd.Width, bd.Height);
        if (bbox == null) return;

        _isSyncingBackdropFromSelector = true;
        _backdropSouthStr = bbox.MinLatitude.ToString("F7", CultureInfo.InvariantCulture);
        _backdropWestStr = bbox.MinLongitude.ToString("F7", CultureInfo.InvariantCulture);
        _backdropNorthStr = bbox.MaxLatitude.ToString("F7", CultureInfo.InvariantCulture);
        _backdropEastStr = bbox.MaxLongitude.ToString("F7", CultureInfo.InvariantCulture);
        _isSyncingBackdropFromSelector = false;
    }

    /// <summary>
    ///     Attempts to apply manually entered backdrop bounding box coordinates to the graphical
    ///     selector (spec §5). Unlike <see cref="TryApplyBboxInputsToSelector" />, this is a plain
    ///     rectangular mapping over <see cref="OriginalBoundingBox" /> — no MetersPerPixel/TargetSize
    ///     re-derivation — via <see cref="SelectionGeometry.BoundingBoxToPixelRect" />, then clamped
    ///     to keep containing the terrain rect.
    /// </summary>
    private void TryApplyBackdropBboxInputsToSelector()
    {
        if (_isSyncingBackdropFromSelector) return;
        if (!BackdropEnabled || OriginalBoundingBox == null || OriginalWidth <= 0 || OriginalHeight <= 0) return;

        if (!double.TryParse(_backdropSouthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var south) ||
            !double.TryParse(_backdropWestStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var west) ||
            !double.TryParse(_backdropNorthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var north) ||
            !double.TryParse(_backdropEastStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var east))
            return;

        // Validate ordering
        if (south >= north || west >= east) return;

        var targetBbox = new GeoBoundingBox(west, south, east, north);
        var pixelRect = SelectionGeometry.BoundingBoxToPixelRect(OriginalBoundingBox, OriginalWidth, OriginalHeight, targetBbox);
        if (pixelRect == null) return;

        _backdropRect = SelectionGeometry.ClampBackdropRect(pixelRect, TerrainRect(), OriginalWidth, OriginalHeight);

        // Re-sync the text fields to reflect the clamped/final position
        UpdateBackdropBboxInputs();
        StateHasChanged();
    }

    private void OnBackdropSouthChanged(string value)
    {
        _backdropSouthStr = value;
        TryApplyBackdropBboxInputsToSelector();
    }

    private void OnBackdropWestChanged(string value)
    {
        _backdropWestStr = value;
        TryApplyBackdropBboxInputsToSelector();
    }

    private void OnBackdropNorthChanged(string value)
    {
        _backdropNorthStr = value;
        TryApplyBackdropBboxInputsToSelector();
    }

    private void OnBackdropEastChanged(string value)
    {
        _backdropEastStr = value;
        TryApplyBackdropBboxInputsToSelector();
    }

    /// <summary>
    ///     Allowed terrain sizes for the TargetSize selector, in ascending order.
    /// </summary>
    private static readonly int[] AllowedTerrainSizes = [256, 512, 1024, 2048, 4096, 8192, 16384];

    /// <summary>
    ///     Attempts to apply manually entered bounding box coordinates to the graphical selector.
    ///     Calculates the real-world extent of the entered bbox, finds the best matching TargetSize
    ///     that covers that extent, then positions the selection centered on the entered bbox center.
    /// </summary>
    private void TryApplyBboxInputsToSelector()
    {
        if (_isSyncingFromSelector) return;
        if (OriginalBoundingBox == null || OriginalWidth <= 0 || OriginalHeight <= 0) return;

        if (!double.TryParse(_bboxSouthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var south) ||
            !double.TryParse(_bboxWestStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var west) ||
            !double.TryParse(_bboxNorthStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var north) ||
            !double.TryParse(_bboxEastStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var east))
            return;

        // Validate ordering
        if (south >= north || west >= east) return;

        // Calculate the center of the entered bbox
        var centerLat = (south + north) / 2.0;
        var centerLon = (west + east) / 2.0;

        // Validate that the center is within the original bounding box
        var bbox = OriginalBoundingBox;
        if (centerLat < bbox.MinLatitude || centerLat > bbox.MaxLatitude ||
            centerLon < bbox.MinLongitude || centerLon > bbox.MaxLongitude)
            return;

        // Calculate real-world extent of the entered bbox in meters
        var centerLatRad = centerLat * Math.PI / 180.0;
        const double MetersPerDegreeLat = 111_320.0;
        var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(centerLatRad);

        var enteredHeightMeters = (north - south) * MetersPerDegreeLat;
        var enteredWidthMeters = (east - west) * metersPerDegreeLon;

        // Use the larger dimension so the selection fully covers the entered bbox
        var requiredExtentMeters = Math.Max(enteredWidthMeters, enteredHeightMeters);

        // Calculate the ideal MetersPerPixel so that TargetSize * MetersPerPixel
        // exactly matches the entered bbox extent. Then find the best TargetSize.
        // Strategy: keep the current TargetSize if possible, adjust MetersPerPixel to fit.
        // If that would produce an unreasonable MetersPerPixel (<0.1 or >100), pick a different TargetSize.
        var bestMpp = (float)(requiredExtentMeters / TargetSize);
        var bestSize = TargetSize;

        if (bestMpp < 0.1f || bestMpp > 100.0f)
        {
            // Current TargetSize can't produce a reasonable MetersPerPixel, find a better one
            bestSize = AllowedTerrainSizes[^1];
            foreach (var candidate in AllowedTerrainSizes)
            {
                var candidateMpp = (float)(requiredExtentMeters / candidate);
                if (candidateMpp >= 0.1f && candidateMpp <= 100.0f)
                {
                    bestSize = candidate;
                    bestMpp = candidateMpp;
                    break;
                }
            }

            bestMpp = (float)(requiredExtentMeters / bestSize);
        }

        // Round to one decimal for a clean UI value
        bestMpp = (float)Math.Round(bestMpp, 1);
        bestMpp = Math.Max(0.1f, bestMpp);

        if (bestSize != TargetSize)
            TargetSize = bestSize;

        if (Math.Abs(MetersPerPixel - bestMpp) > 0.05f)
            MetersPerPixel = bestMpp;

        // Convert the center geo coordinate to source pixel position
        var lonFraction = (centerLon - bbox.MinLongitude) / bbox.Width;
        var latFraction = (centerLat - bbox.MinLatitude) / bbox.Height;

        // In pixel space, Y=0 is top (north), so invert latitude fraction
        var centerPixelX = lonFraction * OriginalWidth;
        var centerPixelY = (1.0 - latFraction) * OriginalHeight;

        // Position the selection rectangle centered on this pixel
        var selSize = CalculateSelectionSizePixels();
        CropOffsetX = (int)Math.Round(centerPixelX - selSize / 2.0);
        CropOffsetY = (int)Math.Round(centerPixelY - selSize / 2.0);

        ClampOffsets();
        RecalculateSelectionBoundingBox();
        // Re-sync the text fields to reflect the clamped/final position
        UpdateBboxInputsFromSelection();
        // Terrain box moved: keep the backdrop box's containment live (spec §5).
        ReclampBackdropRect();
        StateHasChanged();
    }

    private void OnBboxSouthChanged(string value)
    {
        _bboxSouthStr = value;
        TryApplyBboxInputsToSelector();
    }

    private void OnBboxWestChanged(string value)
    {
        _bboxWestStr = value;
        TryApplyBboxInputsToSelector();
    }

    private void OnBboxNorthChanged(string value)
    {
        _bboxNorthStr = value;
        TryApplyBboxInputsToSelector();
    }

    private void OnBboxEastChanged(string value)
    {
        _bboxEastStr = value;
        TryApplyBboxInputsToSelector();
    }

    private void Confirm()
    {
        var result = new CropDialogResult
        {
            OffsetX = CropOffsetX,
            OffsetY = CropOffsetY,
            TargetSize = TargetSize,
            MetersPerPixel = MetersPerPixel,
            SelectionBoundingBox = _selectionBoundingBox,
            BackdropSelection = _backdropRect
        };
        MudDialog.Close(DialogResult.Ok(result));
    }

    private void Cancel()
    {
        MudDialog.Cancel();
    }
}

/// <summary>
///     Record for receiving element size from JavaScript.
/// </summary>
public record ElementSize(double Width, double Height);