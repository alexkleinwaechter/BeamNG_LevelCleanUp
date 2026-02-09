# OSM Map Tile Background - Scroll Wheel Zoom Implementation Plan

## Overview

This document outlines the implementation plan for adding scroll wheel zoom functionality to the `OsmMapTileBackground` component and adapting all consuming components (`CropAnchorSelector` and `OsmFeaturePreview`) to work with the new zoom capability.

## Implementation Status

| Phase | Component | Status | Completed |
|-------|-----------|--------|-----------|
| Phase 1 | OsmMapTileBackground Core Zoom | ? Complete | Yes |
| Phase 2 | CropAnchorSelector Integration | ? Complete | Yes |
| Phase 3 | OsmFeaturePreview Integration | ? Complete | Yes |
| Phase 4 | UX Enhancements | ?? Pending | No |

## Current State

### Components Involved

1. **`OsmMapTileBackground.razor`** - Shared component that renders OSM tiles
   - ? Supports scroll wheel zoom when `EnableScrollZoom="true"`
   - ? Supports click-and-drag panning when zoomed
   - ? Calculates `EffectiveBoundingBox` based on zoom/pan state
   - ? Shows zoom level indicator when zoomed
   - Parameters: `BoundingBox`, `DisplayWidth`, `DisplayHeight`, `Opacity`, `ZoomLevel`, `ViewCenter`, etc.

2. **`CropAnchorSelector.razor`** - GeoTIFF selection component
   - ? Uses `OsmMapTileBackground` with zoom enabled in both minimap and enlarged view
   - ? Selection rectangle correctly scales/positions when zoomed
   - ? Dragging selection works correctly at all zoom levels
   - ? Includes "Reset Zoom" button when zoomed
   - ? Zoom state resets when new GeoTIFF is loaded

3. **`OsmFeaturePreview.razor`** - OSM feature visualization
   - ?? Not yet integrated with zoom functionality
   - Uses `OsmMapTileBackground` as background
   - Overlays SVG with geographic features

### Current Behavior

- ? Scroll wheel zoom in/out on map backgrounds
- ? Click and drag to pan when zoomed
- ? Zoom indicator shows current zoom level
- ? Reset zoom button appears when zoomed
- ? Selection rectangle remains accurate at all zoom levels

## Proposed Architecture

### Design Decision: View Window Approach

Instead of changing the original `BoundingBox`, we introduce a **view window** concept:

```
?????????????????????????????????????
?  Original BoundingBox             ?
?  ???????????????????              ?
?  ?  ViewBoundingBox?              ?  ? What's actually displayed (zoomed view)
?  ?  (subset)       ?              ?
?  ???????????????????              ?
?????????????????????????????????????
```

- **ZoomLevel**: Controls how much of the original bounding box is visible
  - 1.0 = Full bounding box visible (no zoom)
  - 2.0 = Half the area visible (2x zoom)
  - 4.0 = Quarter of the area visible (4x zoom)
  
- **ViewCenter**: The geographic center of the current view (for panning)

### Component Changes

## 1. OsmMapTileBackground Enhancement

### New Parameters

```csharp
/// <summary>
/// Zoom multiplier. 1.0 = fit entire bounding box, 2.0 = 2x zoom, etc.
/// </summary>
[Parameter]
public float ZoomLevel { get; set; } = 1.0f;

/// <summary>
/// Center of the view within the bounding box (normalized 0-1 coordinates).
/// (0.5, 0.5) = center of bounding box.
/// </summary>
[Parameter]
public (float X, float Y) ViewCenter { get; set; } = (0.5f, 0.5f);

/// <summary>
/// Callback when zoom level changes (e.g., via scroll wheel).
/// </summary>
[Parameter]
public EventCallback<float> ZoomLevelChanged { get; set; }

/// <summary>
/// Callback when view center changes (e.g., via panning).
/// </summary>
[Parameter]
public EventCallback<(float X, float Y)> ViewCenterChanged { get; set; }

/// <summary>
/// Whether scroll wheel zoom is enabled.
/// </summary>
[Parameter]
public bool EnableScrollZoom { get; set; } = false;

/// <summary>
/// Minimum zoom level (1.0 = fit all).
/// </summary>
[Parameter]
public float MinZoom { get; set; } = 1.0f;

/// <summary>
/// Maximum zoom level.
/// </summary>
[Parameter]
public float MaxZoom { get; set; } = 10.0f;
```

### New Calculated Property

```csharp
/// <summary>
/// The effective bounding box being displayed (subset of original based on zoom/pan).
/// </summary>
public GeoBoundingBox? EffectiveBoundingBox { get; private set; }
```

### Implementation Details

1. **Enable pointer events** when `EnableScrollZoom` is true
2. **Handle `@onwheel`** event for zoom
3. **Calculate `EffectiveBoundingBox`** based on `ZoomLevel` and `ViewCenter`
4. **Use `EffectiveBoundingBox`** for tile calculations instead of original `BoundingBox`
5. **Clamp `ViewCenter`** to prevent panning outside the original bounds

### Zoom Calculation Logic

```csharp
private GeoBoundingBox CalculateEffectiveBoundingBox()
{
    if (BoundingBox == null || ZoomLevel <= 1.0f)
        return BoundingBox;

    var originalWidth = BoundingBox.MaxLongitude - BoundingBox.MinLongitude;
    var originalHeight = BoundingBox.MaxLatitude - BoundingBox.MinLatitude;
    
    // Calculate visible size based on zoom
    var visibleWidth = originalWidth / ZoomLevel;
    var visibleHeight = originalHeight / ZoomLevel;
    
    // Calculate center in geographic coordinates
    var centerLon = BoundingBox.MinLongitude + originalWidth * ViewCenter.X;
    var centerLat = BoundingBox.MinLatitude + originalHeight * ViewCenter.Y;
    
    // Calculate new bounds centered on ViewCenter
    var newMinLon = centerLon - visibleWidth / 2;
    var newMaxLon = centerLon + visibleWidth / 2;
    var newMinLat = centerLat - visibleHeight / 2;
    var newMaxLat = centerLat + visibleHeight / 2;
    
    // Clamp to original bounds
    // ... clamping logic ...
    
    return new GeoBoundingBox(newMinLon, newMinLat, newMaxLon, newMaxLat);
}
```

---

## 2. CropAnchorSelector Adaptation

### Challenge

The selection rectangle is defined in **source pixel coordinates**, but with zoom, we're only showing a portion of the source image. The selection rectangle needs to:

1. **Scale** based on zoom level
2. **Translate** based on view center
3. Remain **draggable** and update source pixel offsets correctly

### Approach

Track zoom state in `CropAnchorSelector` and pass to `OsmMapTileBackground`:

```csharp
// New state variables
private float _osmZoomLevel = 1.0f;
private (float X, float Y) _osmViewCenter = (0.5f, 0.5f);

// Event handlers for zoom changes from child component
private void OnOsmZoomChanged(float newZoom)
{
    _osmZoomLevel = newZoom;
    StateHasChanged();
}
```

### Selection Rectangle Transformation

When zoomed, the selection rectangle position/size in display pixels must account for:

1. **Visible portion** of source image (based on `EffectiveBoundingBox`)
2. **Mapping** from source pixels to display pixels changes with zoom

```csharp
private string GetSelectionStyle()
{
    // Calculate what portion of the source is visible
    var visibleSourceRect = CalculateVisibleSourceRect();
    
    // Check if selection intersects visible area
    if (!SelectionIntersectsVisible(visibleSourceRect))
        return "display: none;"; // Hide if selection is outside view
    
    // Calculate selection position relative to visible area
    var relativeLeft = CropOffsetX - visibleSourceRect.Left;
    var relativeTop = CropOffsetY - visibleSourceRect.Top;
    
    // Scale to display pixels
    var scaleX = DisplayWidth / visibleSourceRect.Width;
    var scaleY = DisplayHeight / visibleSourceRect.Height;
    
    var displayLeft = relativeLeft * scaleX;
    var displayTop = relativeTop * scaleY;
    var displayWidth = SelectionWidthPixels * scaleX;
    var displayHeight = SelectionHeightPixels * scaleY;
    
    return $"left: {displayLeft}px; top: {displayTop}px; width: {displayWidth}px; height: {displayHeight}px;";
}
```

### Drag Handling with Zoom

When dragging the selection with zoom active:

```csharp
private void OnMinimapMouseMove(MouseEventArgs e)
{
    if (!_isDragging) return;

    var deltaX = e.ClientX - _dragStartX;
    var deltaY = e.ClientY - _dragStartY;

    // Account for zoom when converting display pixels to source pixels
    var effectiveScale = GetMinimapScale() * _osmZoomLevel;
    var sourcePixelDeltaX = (int)(deltaX / effectiveScale);
    var sourcePixelDeltaY = (int)(deltaY / effectiveScale);

    CropOffsetX = _dragStartOffsetX + sourcePixelDeltaX;
    CropOffsetY = _dragStartOffsetY + sourcePixelDeltaY;
    
    ClampOffsets();
    // ... rest of handler
}
```

### Auto-Pan to Selection (Optional Enhancement)

When zoomed in, if the selection is moved outside the visible area, auto-pan to keep it visible:

```csharp
private void EnsureSelectionVisible()
{
    if (_osmZoomLevel <= 1.0f) return;
    
    // Calculate selection center in normalized coordinates
    var selectionCenterX = (CropOffsetX + SelectionWidthPixels / 2.0f) / OriginalWidth;
    var selectionCenterY = (CropOffsetY + SelectionHeightPixels / 2.0f) / OriginalHeight;
    
    // If selection center is outside view, pan to it
    // ... pan logic ...
}
```

---

## 3. OsmFeaturePreview Adaptation

### Challenge

Features are rendered in SVG using pixel coordinates transformed from geographic coordinates. With zoom:

1. Features outside the view should be clipped (or not rendered)
2. Feature positions must be recalculated based on `EffectiveBoundingBox`
3. Feature line widths might need adjustment for better visibility

### Approach

#### Option A: Use EffectiveBoundingBox for Transformation (Recommended)

Get the effective bounding box from `OsmMapTileBackground` and use it for coordinate transformation:

```csharp
// Store reference to get effective bounds
private GeoBoundingBox? _effectiveBoundingBox;

private void OnEffectiveBoundsChanged(GeoBoundingBox? bounds)
{
    _effectiveBoundingBox = bounds;
    StateHasChanged();
}

private List<(float X, float Y)> TransformToPixel(List<GeoCoordinate> coords)
{
    // Use effective bounds instead of original for transformation
    var bbox = _effectiveBoundingBox ?? BoundingBox;
    
    // ... existing transformation logic using bbox ...
}
```

#### Option B: Transform in Parent, Let SVG Clip

Keep using original bounds for transformation but apply SVG viewBox transformation:

```html
<svg viewBox="@GetViewBox()" ...>
    @* Features rendered in original coordinate space *@
</svg>
```

```csharp
private string GetViewBox()
{
    if (_osmZoomLevel <= 1.0f)
        return $"0 0 {PreviewPixelSize} {PreviewPixelSize}";
    
    // Calculate visible portion in pixel coordinates
    var visibleWidth = PreviewPixelSize / _osmZoomLevel;
    var visibleHeight = PreviewPixelSize / _osmZoomLevel;
    var viewX = (_osmViewCenter.X - 0.5f / _osmZoomLevel) * PreviewPixelSize;
    var viewY = (_osmViewCenter.Y - 0.5f / _osmZoomLevel) * PreviewPixelSize;
    
    return $"{viewX} {viewY} {visibleWidth} {visibleHeight}";
}
```

**Recommendation**: Option A is cleaner and ensures features align perfectly with tiles.

---

## 4. Implementation Phases

### Phase 1: OsmMapTileBackground Core Zoom ? COMPLETE

**Status:** Implemented and working.

**What was implemented:**
1. ? Added new parameters (`ZoomLevel`, `ViewCenter`, `EnableScrollZoom`, `MinZoom`, `MaxZoom`)
2. ? Implemented `CalculateEffectiveBoundingBox()` method
3. ? Added scroll wheel event handler with zoom-toward-mouse behavior
4. ? Added click-and-drag panning when zoomed
5. ? Updated tile calculation to use effective bounds
6. ? Added `EffectiveBoundingBoxChanged` callback
7. ? Added zoom level indicator overlay
8. ? Added `ResetZoom()` public method
9. ? Added `EnablePanning` parameter to disable panning independently
10. ? Added `@onwheel:stopPropagation` to prevent page scrolling when `EnableScrollZoom` is true

**Files Modified:**
- `BeamNG_LevelCleanUp/BlazorUI/Components/OsmMapTileBackground.razor`

### Phase 2: CropAnchorSelector Integration ? COMPLETE

**Status:** Implemented and working.

**What was implemented:**
1. ? Added zoom state tracking for enlarged view only (minimap zoom disabled)
2. ? Disabled zoom on minimap to prevent page scroll interference
3. ? Wired up zoom/pan callbacks from `OsmMapTileBackground` for enlarged view
4. ? Updated selection rectangle calculation with `GetSelectionStyleWithZoom()`:
   - Correctly scales selection when zoomed
   - Hides selection when outside visible area
   - Positions selection relative to visible viewport
5. ? Updated drag handling to account for zoom level in enlarged view
6. ? Added zoom UI controls:
   - Reset zoom button in header of enlarged view (more prominent, Warning color)
   - Zoom hint text in footer
7. ? Added `GetEnlargedMaxZoom()` to calculate dynamic max zoom based on selection size:
   - Ensures selection rectangle never becomes smaller than 50px display size
   - Prevents zooming to the point where selection is too small to see/interact with
8. ? Added `ResetAllZoomStates()` called when new GeoTIFF is loaded
9. ? **Fixed panning vs. selection drag conflict:**
   - Added `_isMouseOverEnlargedSelection` state tracking
   - Added `IsMouseOverEnlargedSelection()` hit-testing method
   - Added `GetEnlargedSelectionBounds()` to calculate selection bounds in display coordinates
   - Map panning is disabled when mouse is over the selection box
   - Selection dragging only starts when clicking inside the selection box
   - Combined mouse move handler `OnEnlargedMouseMoveWithHitTest()` handles both hit-testing and selection dragging

**Files Modified:**
- `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelector.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelector.razor.cs`

### Phase 3: OsmFeaturePreview Integration (Priority: High) ? COMPLETE

**Status:** Implemented and working.

**What was implemented:**
1. ? Added zoom state tracking (`_zoomLevel`, `_viewCenter`, `_effectiveBoundingBox`)
2. ? Enabled `EnableScrollZoom` on `OsmMapTileBackground` component
3. ? Implemented `TransformToPixelWithZoom()` that uses `EffectiveBoundingBox` for accurate coordinate transformation
4. ? Added zoom/pan event handlers (`OnZoomLevelChanged`, `OnViewCenterChanged`, `OnEffectiveBoundingBoxChanged`)
5. ? Added zoom level indicator overlay (matches Phase 1 style)
6. ? Added Reset Zoom button in footer (appears when zoomed)
7. ? Added `ResetZoomState()` called when `BoundingBox` parameter changes
8. ? Made SVG `pointer-events: none` so mouse events pass through to map for zoom/pan
9. ? Adjusted stroke widths based on zoom level for better visibility at different scales
10. ? Features align perfectly with map tiles at all zoom levels

**Key Implementation Details:**

The critical insight for perfect feature alignment is using `EffectiveBoundingBox` from `OsmMapTileBackground`:

```csharp
private void OnEffectiveBoundingBoxChanged(GeoBoundingBox? bounds)
{
    _effectiveBoundingBox = bounds;
    StateHasChanged();
}

private List<(float X, float Y)> TransformToPixelWithZoom(List<GeoCoordinate> coords)
{
    // Use effective bounding box when zoomed, otherwise use original
    var bbox = _effectiveBoundingBox ?? BoundingBox;
    
    // Transform coordinates relative to the effective (zoomed) bounds
    foreach (var coord in coords)
    {
        var normalizedX = (coord.Longitude - bbox.MinLongitude) / bboxWidth;
        var normalizedY = (coord.Latitude - bbox.MinLatitude) / bboxHeight;
        // ... convert to pixel coordinates
    }
}
```

**Files Modified:**
- `BeamNG_LevelCleanUp/BlazorUI/Components/OsmFeaturePreview.razor`

**Estimated Effort:** 2-3 hours (actual: ~1 hour)

### Phase 4: UX Enhancements (Priority: Low) ?? PENDING

**Status:** Partially complete (zoom indicator already implemented in Phase 1).

**Remaining work:**
1. ? Zoom indicator overlay (done in Phase 1)
2. ? "Reset Zoom" button (done in Phase 2)
3. ?? Add keyboard shortcuts (e.g., +/- for zoom)
4. ?? Add touch gesture support (pinch zoom)
5. ?? Smooth zoom animations

**Estimated Effort:** 2-3 hours

---

## 5. API Design Summary

### OsmMapTileBackground New Interface

```csharp
// New parameters
[Parameter] public float ZoomLevel { get; set; } = 1.0f;
[Parameter] public (float X, float Y) ViewCenter { get; set; } = (0.5f, 0.5f);
[Parameter] public bool EnableScrollZoom { get; set; } = false;
[Parameter] public bool EnablePanning { get; set; } = true;  // NEW: Can disable panning independently
[Parameter] public float MinZoom { get; set; } = 1.0f;
[Parameter] public float MaxZoom { get; set; } = 10.0f;

// New callbacks
[Parameter] public EventCallback<float> ZoomLevelChanged { get; set; }
[Parameter] public EventCallback<(float X, float Y)> ViewCenterChanged { get; set; }
[Parameter] public EventCallback<GeoBoundingBox?> EffectiveBoundingBoxChanged { get; set; }

// New public property
public GeoBoundingBox? EffectiveBoundingBox { get; }
```

### Key Improvements

1. **Page Scroll Prevention**: Added `@onwheel:stopPropagation` when `EnableScrollZoom` is true to prevent page scrolling when mouse is over the component.

2. **Enable/Disable Panning**: New `EnablePanning` parameter allows disabling click-and-drag panning while keeping scroll zoom enabled (or vice versa).

3. **Dynamic MaxZoom in CropAnchorSelector**: The `GetEnlargedMaxZoom()` method calculates the maximum zoom based on selection size, ensuring the selection rectangle never becomes smaller than 50px in display size.

### Usage Example in CropAnchorSelector (Actual Implementation)

```razor
@* Minimap view *@
<OsmMapTileBackground BoundingBox="@OriginalBoundingBox"
                      DisplayWidth="@GetMinimapDisplayWidth()"
                      DisplayHeight="@GetMinimapDisplayHeight()"
                      IsVisible="@_showOsmBackground"
                      Opacity="@_mapOpacity"
                      ShowAttribution="false"
                      EnableScrollZoom="true"
                      ZoomLevel="@_minimapZoomLevel"
                      ZoomLevelChanged="@OnMinimapZoomChanged"
                      ViewCenter="@_minimapViewCenter"
                      ViewCenterChanged="@OnMinimapViewCenterChanged"
                      EffectiveBoundingBoxChanged="@OnMinimapEffectiveBoundsChanged"
                      MinZoom="1.0f"
                      MaxZoom="8.0f"
                      ZIndex="1" />

@* Enlarged view *@
<OsmMapTileBackground BoundingBox="@OriginalBoundingBox"
                      DisplayWidth="@GetEnlargedDisplayWidth()"
                      DisplayHeight="@GetEnlargedDisplayHeight()"
                      IsVisible="true"
                      Opacity="@_mapOpacity"
                      MaxTiles="100"
                      ShowAttribution="false"
                      EnableScrollZoom="true"
                      ZoomLevel="@_enlargedZoomLevel"
                      ZoomLevelChanged="@OnEnlargedZoomChanged"
                      ViewCenter="@_enlargedViewCenter"
                      ViewCenterChanged="@OnEnlargedViewCenterChanged"
                      EffectiveBoundingBoxChanged="@OnEnlargedEffectiveBoundsChanged"
                      MinZoom="1.0f"
                      MaxZoom="10.0f"
                      ZIndex="1" />
```

### Selection Style Calculation with Zoom (Actual Implementation)

```csharp
private string GetSelectionStyleWithZoom(double baseScale, float zoomLevel, 
    (float X, float Y) viewCenter, int displayWidth, int displayHeight)
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
        return $"width: {simpleDisplayWidth}px; height: {simpleDisplayHeight}px; " +
               $"left: {simpleDisplayLeft}px; top: {simpleDisplayTop}px;";
    }

    // When zoomed, calculate visible portion in source pixels
    var visibleSourceWidth = OriginalWidth / zoomLevel;
    var visibleSourceHeight = OriginalHeight / zoomLevel;

    // Calculate the top-left corner of visible area in source pixels
    var visibleCenterX = OriginalWidth * viewCenter.X;
    var visibleCenterY = OriginalHeight * viewCenter.Y;
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

    return $"width: {scaledWidth}px; height: {scaledHeight}px; " +
           $"left: {displayLeft}px; top: {displayTop}px;";
}
```

---

## 6. Edge Cases and Considerations

### Zoom Limits
- **Minimum**: 1.0 (full bounding box visible) - prevents zooming out beyond data
- **Maximum**: 10.0 or based on tile availability - prevents showing empty tiles

### Pan Limits
- View center should be clamped so the effective bounding box stays within the original
- When zoomed out to 1.0, view center is forced to (0.5, 0.5)

### Selection vs. View Independence
- In `CropAnchorSelector`, the selection is independent of the view
- User can zoom/pan to inspect different areas without changing selection
- Selection rectangle updates when dragged, not when zoomed

### Performance
- Limit tile requests when rapidly zooming
- Consider debouncing zoom events
- Cache tiles that are still valid after small zoom changes

### Accessibility
- Ensure zoom can be controlled via keyboard
- Provide visual feedback for current zoom level
- Consider users who cannot use scroll wheel

---

## 7. Testing Checklist

### OsmMapTileBackground ?
- [x] Scroll zoom in/out works
- [x] Zoom respects min/max limits
- [x] Tiles update correctly on zoom
- [x] Callbacks fire with correct values
- [x] EffectiveBoundingBox is calculated correctly
- [x] No tile flickering during zoom
- [x] Pan by dragging when zoomed
- [x] Zoom indicator shows current level

### CropAnchorSelector ?
- [x] Selection rectangle scales correctly with zoom
- [x] Selection rectangle position is correct when panned
- [x] Dragging selection works correctly when zoomed
- [x] Selection pixel coordinates remain accurate
- [x] CropResult coordinates are independent of zoom
- [x] Enlarged view also supports zoom
- [x] Reset zoom button works
- [x] Zoom resets when new GeoTIFF loaded
- [x] Panning only works when cursor is outside selection box
- [x] Selection dragging only starts when clicking inside selection box
- [x] Selection box remains fixed on geographic location during pan/zoom

### OsmFeaturePreview ?
- [x] Features align with map tiles at all zoom levels
- [x] Features outside view are handled gracefully (clipped by SVG viewport)
- [x] Feature visibility/styling appropriate when zoomed (stroke widths adjust)
- [x] Coordinate transformation is accurate (uses EffectiveBoundingBox)
- [x] Zoom indicator shows current level
- [x] Reset zoom button works
- [x] Zoom resets when BoundingBox changes

---

## 8. Alternative Approaches Considered

### A. Leaflet/OpenLayers Integration
**Pros:** Full-featured map library, handles zoom/pan/tiles automatically
**Cons:** Large dependency, complex integration with Blazor, overkill for this use case

### B. Scale Transform on Container
**Pros:** Simple CSS transform
**Cons:** Doesn't load higher-detail tiles, blurry when zoomed

### C. Separate Zoomed Component
**Pros:** Simpler implementation, no changes to existing component
**Cons:** Code duplication, inconsistent behavior

**Decision:** Enhance existing component with zoom parameters (chosen approach) provides the best balance of functionality and maintainability.

---

## 9. Future Enhancements

- **Double-click to zoom**: Zoom in centered on click location
- **Zoom to selection**: Button to zoom the view to fit the current selection
- **Minimap overview**: Show small overview when zoomed in
- **Smooth animations**: Animate zoom transitions
- **Touch support**: Pinch-to-zoom for touch devices
- **Tile caching**: Cache downloaded tiles for faster navigation
- **Keyboard shortcuts**: +/- keys for zoom control

---

## 10. Changelog

### 2024-01-XX - Phase 1 & 2 Complete

**Phase 1: OsmMapTileBackground**
- Added zoom/pan parameters and callbacks
- Implemented `CalculateEffectiveBoundingBox()` 
- Added scroll wheel zoom with zoom-toward-mouse behavior
- Added click-and-drag panning
- Added zoom level indicator overlay
- CSS class toggles for interactive mode

**Phase 2: CropAnchorSelector**
- Added separate zoom state for minimap and enlarged view
- Implemented `GetSelectionStyleWithZoom()` for accurate selection positioning
- Updated drag handlers to account for zoom level
- Added zoom hint text and reset buttons
- Added `ResetAllZoomStates()` for new file loads
