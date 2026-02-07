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
    // Display dimensions - calculated dynamically based on container size
    private int _displayHeight;
    private int _displayWidth;
    private int _dragStartOffsetX;
    private int _dragStartOffsetY;
    private double _dragStartX;
    private double _dragStartY;
    private GeoBoundingBox? _effectiveBoundingBox;
    private bool _isDragging;
    private bool _isMouseOverSelection;
    private float _mapOpacity = 0.85f;
    private GeoBoundingBox? _selectionBoundingBox;
    private (float X, float Y) _viewCenter = (0.5f, 0.5f);
    private float _zoomLevel = 1.0f;
    private int CropOffsetX;
    private int CropOffsetY;
    
    // Element reference for measuring container size
    private ElementReference _mapAreaRef;
    private DotNetObjectReference<CropAnchorSelectorDialog>? _jsRef;
    private bool _isInitialized;
    
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
    public int SelectionWidthPixels => CalculateSelectionSizePixels();
    public int SelectionHeightPixels => CalculateSelectionSizePixels();

    protected override void OnInitialized()
    {
        CropOffsetX = InitialOffsetX;
        CropOffsetY = InitialOffsetY;
        ClampOffsets();
        RecalculateSelectionBoundingBox();
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

    private string GetMapWrapperStyle()
    {
        return $"width: {_displayWidth}px; height: {_displayHeight}px;";
    }

    private int CalculateSelectionSizePixels()
    {
        if (NativePixelSizeMeters <= 0) return TargetSize;
        var targetMeters = TargetSize * MetersPerPixel;
        var selectionPixels = (int)Math.Ceiling(targetMeters / NativePixelSizeMeters);
        return Math.Min(selectionPixels, Math.Min(OriginalWidth, OriginalHeight));
    }

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

    private void ClampOffsets()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;
        CropOffsetX = Math.Max(0, Math.Min(CropOffsetX, OriginalWidth - selW));
        CropOffsetY = Math.Max(0, Math.Min(CropOffsetY, OriginalHeight - selH));
    }

    private void RecalculateSelectionBoundingBox()
    {
        if (OriginalBoundingBox is not { } bbox || OriginalWidth <= 0 || OriginalHeight <= 0)
        {
            _selectionBoundingBox = null;
            return;
        }

        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;

        var leftFraction = (double)CropOffsetX / OriginalWidth;
        var rightFraction = (double)(CropOffsetX + selW) / OriginalWidth;
        var topFraction = (double)CropOffsetY / OriginalHeight;
        var bottomFraction = (double)(CropOffsetY + selH) / OriginalHeight;

        var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
        var latRange = bbox.MaxLatitude - bbox.MinLatitude;

        var newMinLon = bbox.MinLongitude + lonRange * leftFraction;
        var newMaxLon = bbox.MinLongitude + lonRange * rightFraction;
        var newMaxLat = bbox.MaxLatitude - latRange * topFraction;
        var newMinLat = bbox.MaxLatitude - latRange * bottomFraction;

        _selectionBoundingBox = new GeoBoundingBox(newMinLon, newMinLat, newMaxLon, newMaxLat);
    }

    private string GetSelectionStyle()
    {
        var selW = SelectionWidthPixels;
        var selH = SelectionHeightPixels;
        var baseScale = GetScale();

        if (_zoomLevel <= 1.01f)
        {
            var simpleDisplayWidth = Math.Max(10, (int)(selW * baseScale));
            var simpleDisplayHeight = Math.Max(10, (int)(selH * baseScale));
            var simpleDisplayLeft = (int)(CropOffsetX * baseScale);
            var simpleDisplayTop = (int)(CropOffsetY * baseScale);
            return
                $"width: {simpleDisplayWidth}px; height: {simpleDisplayHeight}px; left: {simpleDisplayLeft}px; top: {simpleDisplayTop}px;";
        }

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

        var displayLeft = (int)(relativeLeft * scaleX);
        var displayTop = (int)(relativeTop * scaleY);
        var scaledWidth = Math.Max(10, (int)(selW * scaleX));
        var scaledHeight = Math.Max(10, (int)(selH * scaleY));

        if (displayLeft + scaledWidth < 0 || displayLeft > _displayWidth ||
            displayTop + scaledHeight < 0 || displayTop > _displayHeight)
            return "display: none;";

        return $"width: {scaledWidth}px; height: {scaledHeight}px; left: {displayLeft}px; top: {displayTop}px;";
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

    private void OnMouseDown(MouseEventArgs e)
    {
        if (!IsMouseOverSelection(e.OffsetX, e.OffsetY)) return;

        _isDragging = true;
        _dragStartX = e.ClientX;
        _dragStartY = e.ClientY;
        _dragStartOffsetX = CropOffsetX;
        _dragStartOffsetY = CropOffsetY;
    }

    private void OnMouseMoveWithHitTest(MouseEventArgs e)
    {
        var wasOver = _isMouseOverSelection;
        _isMouseOverSelection = IsMouseOverSelection(e.OffsetX, e.OffsetY);

        if (_isDragging)
        {
            var deltaX = e.ClientX - _dragStartX;
            var deltaY = e.ClientY - _dragStartY;

            var baseScale = GetScale();
            var effectiveScale = baseScale * _zoomLevel;
            var sourcePixelDeltaX = (int)(deltaX / effectiveScale);
            var sourcePixelDeltaY = (int)(deltaY / effectiveScale);

            CropOffsetX = _dragStartOffsetX + sourcePixelDeltaX;
            CropOffsetY = _dragStartOffsetY + sourcePixelDeltaY;

            ClampOffsets();
            RecalculateSelectionBoundingBox();
            StateHasChanged();
        }
        else if (wasOver != _isMouseOverSelection)
        {
            StateHasChanged();
        }
    }

    private void OnMouseUp(MouseEventArgs e)
    {
        _isDragging = false;
    }

    private void OnMouseLeave(MouseEventArgs e)
    {
        _isDragging = false;
        _isMouseOverSelection = false;
        StateHasChanged();
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

    private void Confirm()
    {
        var result = new CropDialogResult
        {
            OffsetX = CropOffsetX,
            OffsetY = CropOffsetY,
            SelectionBoundingBox = _selectionBoundingBox
        };
        MudDialog.Close(DialogResult.Ok(result));
    }

    private void Cancel()
    {
        MudDialog.Cancel();
    }
}

/// <summary>
/// Record for receiving element size from JavaScript.
/// </summary>
public record ElementSize(double Width, double Height);