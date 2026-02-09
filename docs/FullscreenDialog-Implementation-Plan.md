# Fullscreen Dialog Implementation Plan

## Overview

This document outlines the implementation plan for converting dialogs to use proper MudBlazor fullscreen dialogs with responsive design patterns. The goal is to maximize screen real estate for map interaction and provide a better user experience.

## Current State Analysis

### ? Phase 1: CropAnchorSelectorDialog (COMPLETED)

The `CropAnchorSelectorDialog` has already been converted to a true fullscreen dialog with:
- Dynamic sizing using JavaScript interop (`getElementSize`, `setupCropDialogResizeObserver`)
- Responsive map area that fills available space
- Proper aspect ratio maintenance
- ElementReference for container size measurement
- `IAsyncDisposable` pattern for cleanup

**Key patterns established:**
- `ElementSize` record for JS interop
- `CalculateDisplayDimensionsAsync()` method pattern
- ResizeObserver setup for responsive resizing
- CSS flexbox layout with `flex: 1` for dynamic areas

### OsmFeatureSelectorDialog (Current State)

**Current Implementation:**
- Standard MudDialog (not fullscreen)
- Fixed 500px height for content
- Side-by-side layout using `MudGrid`
- Preview uses fixed `PreviewPixelSize => 420` constant
- No responsive sizing

**Pain Points:**
- Dialog is not fullscreen, wasting screen space
- Preview area is small (~420px fixed)
- Cannot maximize for detailed feature selection
- `OsmFeaturePreview` doesn't adapt to container size

### OsmFeaturePreview (Current State)

**Current Implementation:**
- Fixed `PreviewPixelSize => 420` constant
- Has zoom/pan support via `OsmMapTileBackground`
- SVG feature overlay aligned with map tiles
- No responsive container awareness

**Pain Points:**
- Cannot adapt to available container space
- Fixed size limits usefulness in fullscreen context

---

## JavaScript Interop (Already Available)

The following JavaScript helpers are already implemented in `wwwroot/index.html`:

```javascript
// Get element size - already available
window.getElementSize = (element) => {
    if (!element) return { width: 0, height: 0 };
    const rect = element.getBoundingClientRect();
    return { width: rect.width, height: rect.height };
};

// ResizeObserver management - already available (with debouncing)
window.setupCropDialogResizeObserver = (element, dotNetRef) => { ... };
window.removeCropDialogResizeObserver = (element) => { ... };
```

**Note:** The existing resize observer functions use `cropDialogResizeObservers` naming but are generic enough to reuse. For Phase 2, we'll add similar functions with appropriate naming for the feature selector dialog.

---

## MudBlazor Fullscreen Dialog Pattern

Reference from MudBlazor documentation:

```csharp
// Dialog options for fullscreen
private readonly DialogOptions _fullScreen = new() 
{ 
    FullScreen = true, 
    CloseButton = true 
};

// Opening a fullscreen dialog
private Task OpenDialogAsync(DialogOptions options)
{
    return Dialog.ShowAsync<MyDialogComponent>("Title", options);
}
```

---

## Phase 2: OsmFeatureSelectorDialog Fullscreen with Responsive Preview

### Goal
Convert `OsmFeatureSelectorDialog` to fullscreen and make `OsmFeaturePreview` responsive, using the same JavaScript interop patterns established in Phase 1.

### New Design

**Two-column responsive layout with dynamic preview sizing:**

```
???????????????????????????????????????????????????????????????????????????
? [X] Select OSM Features for Grass                                       ?
???????????????????????????????????????????????????????????????????????????
? ?????????????????????????????????????????????????????????????????????????
? ? FILTERS (320px)      ?  PREVIEW (fills remaining space)              ?
? ?                      ?                                                ?
? ? [Category Chips]     ?  ??????????????????????????????????????????   ?
? ? ? Lines  ? Polygons  ?  ?                                        ?   ?
? ?                      ?  ?     [RESPONSIVE SQUARE PREVIEW]        ?   ?
? ? [Search...]          ?  ?     (calculates size from container)   ?   ?
? ?                      ?  ?                                        ?   ?
? ? ???????????????????? ?  ?       Features aligned with map        ?   ?
? ? ? Feature List     ? ?  ?                                        ?   ?
? ? ? (scrollable)     ? ?  ?       Zoom/Pan enabled                 ?   ?
? ? ?                  ? ?  ??????????????????????????????????????????   ?
? ? ? ? Roads: Primary ? ?                                                ?
? ? ? ? Roads: Second. ? ?  [Map toggle] [Opacity] 42 features    © OSM ?
? ? ? ? Landuse: Forest? ?                                                ?
? ? ? ...              ? ?                                                ?
? ? ???????????????????? ?                                                ?
? ?                      ?                                                ?
? ? [Select All] [Clear] ?                                                ?
? ? Types: 12 selected   ?                                                ?
? ?????????????????????????????????????????????????????????????????????????
???????????????????????????????????????????????????????????????????????????
?                               [Cancel]  [Select 42 Features]            ?
???????????????????????????????????????????????????????????????????????????
```

### Implementation Strategy

The preview area will use the same responsive pattern as `CropAnchorSelectorDialog`:
1. Container `ElementReference` for the preview area
2. JavaScript interop to measure container size
3. Calculate square preview size (min of width/height with padding)
4. Pass calculated size to `OsmFeaturePreview` as a parameter
5. ResizeObserver for responsive updates

### Changes Required

#### 1. Add JavaScript Helpers for Feature Selector (wwwroot/index.html)

Add to the existing script section:

```javascript
// ResizeObserver management for feature selector dialog
window.featureSelectorResizeObservers = new Map();

window.setupFeatureSelectorResizeObserver = (element, dotNetRef) => {
    if (!element) return;
    
    // Remove any existing observer for this element
    window.removeFeatureSelectorResizeObserver(element);
    
    const observer = new ResizeObserver(entries => {
        // Debounce to avoid excessive calls
        if (observer._timeout) clearTimeout(observer._timeout);
        observer._timeout = setTimeout(() => {
            dotNetRef.invokeMethodAsync('OnPreviewContainerResized');
        }, 100);
    });
    
    observer.observe(element);
    window.featureSelectorResizeObservers.set(element, observer);
};

window.removeFeatureSelectorResizeObserver = (element) => {
    if (!element) return;
    const observer = window.featureSelectorResizeObservers.get(element);
    if (observer) {
        if (observer._timeout) clearTimeout(observer._timeout);
        observer.disconnect();
        window.featureSelectorResizeObservers.delete(element);
    }
};
```

#### 2. Update OsmFeaturePreview.razor.cs

Add a `PreviewPixelSize` parameter to make the component responsive:

```csharp
public partial class OsmFeaturePreview
{
    // ... existing fields ...
    
    /// <summary>
    /// The pixel size for the preview. When set to 0 or less, uses default 420px.
    /// This parameter allows the parent to control the preview size responsively.
    /// </summary>
    [Parameter]
    public int PreviewPixelSize { get; set; } = 0;
    
    /// <summary>
    /// Effective pixel size - uses parameter if provided, otherwise default.
    /// </summary>
    private int EffectivePreviewPixelSize => PreviewPixelSize > 0 ? PreviewPixelSize : 420;
    
    // Update all usages of the old PreviewPixelSize property to use EffectivePreviewPixelSize
    // ... rest of existing code ...
}
```

#### 3. Update OsmFeaturePreview.razor

Update to use `EffectivePreviewPixelSize` instead of the fixed property:

```razor
@* Update all occurrences of PreviewPixelSize to EffectivePreviewPixelSize *@
<div class="osm-preview-square" @ref="_previewSquare" 
     style="position: relative; width: @(EffectivePreviewPixelSize)px; height: @(EffectivePreviewPixelSize)px;">
    
    <OsmMapTileBackground BoundingBox="@BoundingBox"
                          DisplayWidth="@EffectivePreviewPixelSize"
                          DisplayHeight="@EffectivePreviewPixelSize"
                          ... />
    
    <svg width="@EffectivePreviewPixelSize" height="@EffectivePreviewPixelSize" 
         viewBox="0 0 @EffectivePreviewPixelSize @EffectivePreviewPixelSize"
         ...>
        @* Update grid lines and all other pixel references *@
    </svg>
</div>
```

#### 4. Update OsmFeatureSelectorDialog.razor

Convert to fullscreen with responsive preview:

```razor
@using BeamNgTerrainPoc.Terrain.GeoTiff
@using BeamNgTerrainPoc.Terrain.Osm.Models
@using BeamNgTerrainPoc.Terrain.Osm.Services
@inject ISnackbar Snackbar
@inject IJSRuntime JS

<MudDialog Class="fullscreen-selector-dialog">
    <TitleContent>
        <MudStack Row="true" AlignItems="AlignItems.Center">
            <MudIcon Icon="@Icons.Material.Filled.Map" Class="mr-2" />
            <MudText Typo="Typo.h6">Select OSM Features for @MaterialName</MudText>
        </MudStack>
    </TitleContent>
    
    <DialogContent>
        @if (_isLoading)
        {
            @* Loading state - same as before *@
        }
        else if (!string.IsNullOrEmpty(_error))
        {
            @* Error state - same as before *@
        }
        else if (_queryResult != null)
        {
            <div class="selector-content">
                @* Left Panel - Fixed Width Filters *@
                <div class="selector-filters">
                    @* Category Filter *@
                    <MudText Typo="Typo.subtitle2" Class="mb-2">Filter by Category</MudText>
                    <MudChipSet T="string" @bind-SelectedValues="_selectedCategories" 
                                SelectionMode="SelectionMode.MultiSelection"
                                Class="mb-3">
                        @foreach (var category in _availableCategories)
                        {
                            <MudChip Value="@category" 
                                     Color="@GetCategoryColor(category)"
                                     Variant="@(IsCategorySelected(category) ? Variant.Filled : Variant.Outlined)"
                                     SelectedColor="@GetCategoryColor(category)">
                                @GetCategoryDisplayName(category)
                            </MudChip>
                        }
                    </MudChipSet>
                    
                    @* Geometry Type Filter *@
                    <MudStack Row="true" Class="mb-3" Spacing="3">
                        <MudCheckBox @bind-Value="_showLines" Label="Lines (Roads)" Color="Color.Primary" />
                        <MudCheckBox @bind-Value="_showPolygons" Label="Polygons (Areas)" Color="Color.Secondary" />
                    </MudStack>
                    
                    @* Search *@
                    <MudTextField @bind-Value="_searchText"
                                  Placeholder="Search feature types..."
                                  Variant="Variant.Outlined"
                                  Adornment="Adornment.Start"
                                  AdornmentIcon="@Icons.Material.Filled.Search"
                                  Immediate="true"
                                  Class="mb-3"
                                  Clearable="true" />
                    
                    @* Feature List - fills remaining space *@
                    <MudPaper Elevation="0" Outlined="true" Class="feature-list-container">
                        <MudList T="FeatureGroup" @bind-SelectedValues="_selectedGroups"
                                 SelectionMode="SelectionMode.MultiSelection"
                                 Dense="true">
                            @foreach (var group in FilteredGroups)
                            {
                                @* Same list item content as before *@
                            }
                        </MudList>
                    </MudPaper>
                    
                    @* Quick Actions & Status *@
                    <div class="filter-footer">
                        <MudStack Row="true" Spacing="2">
                            <MudButton Variant="Variant.Text" 
                                       Size="Size.Small" 
                                       OnClick="SelectAllFiltered"
                                       StartIcon="@Icons.Material.Filled.SelectAll">
                                Select All
                            </MudButton>
                            <MudButton Variant="Variant.Text" 
                                       Size="Size.Small" 
                                       OnClick="ClearSelection"
                                       StartIcon="@Icons.Material.Filled.Clear">
                                Clear
                            </MudButton>
                        </MudStack>
                        <MudText Typo="Typo.caption" Color="Color.Secondary">
                            @FilteredGroups.Count() types · @_selectedGroups.Sum(g => g.FeatureCount) features selected
                        </MudText>
                    </div>
                </div>
                
                @* Right Panel - Preview Fills Space *@
                <div class="selector-preview">
                    <MudText Typo="Typo.subtitle2" Class="mb-2">Preview</MudText>
                    
                    @* Preview container - measured for responsive sizing *@
                    <div class="preview-container" @ref="_previewContainerRef">
                        <OsmFeaturePreview Features="@GetSelectedOsmFeatures()"
                                           BoundingBox="@BoundingBox"
                                           PreviewPixelSize="@_calculatedPreviewSize"
                                           TerrainSize="@TerrainSize" />
                    </div>
                </div>
            </div>
        }
    </DialogContent>
    
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" 
                   Variant="Variant.Filled"
                   OnClick="Confirm" 
                   Disabled="@(!_selectedGroups.Any())">
            Select @_selectedGroups.Sum(g => g.FeatureCount) Feature@(_selectedGroups.Sum(g => g.FeatureCount) != 1 ? "s" : "")
        </MudButton>
    </DialogActions>
</MudDialog>

<style>
    .fullscreen-selector-dialog .mud-dialog-content {
        padding: 16px !important;
    }
    
    .selector-content {
        display: flex;
        height: calc(100vh - 140px);
        min-height: 0;
        gap: 16px;
    }
    
    .selector-filters {
        width: 320px;
        flex-shrink: 0;
        display: flex;
        flex-direction: column;
        min-height: 0;
    }
    
    .feature-list-container {
        flex: 1;
        overflow-y: auto;
        min-height: 0;
    }
    
    .filter-footer {
        flex-shrink: 0;
        padding-top: 8px;
        display: flex;
        flex-direction: column;
        gap: 4px;
    }
    
    .selector-preview {
        flex: 1;
        display: flex;
        flex-direction: column;
        min-width: 0;
        min-height: 0;
    }
    
    .preview-container {
        flex: 1;
        min-height: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: #1a1a2e;
        border-radius: 4px;
        overflow: hidden;
    }
</style>
```

#### 5. Add Code-Behind for Responsive Sizing (OsmFeatureSelectorDialog - partial class or inline @code)

```csharp
// Add to the @code section or create a .razor.cs file

private ElementReference _previewContainerRef;
private DotNetObjectReference<OsmFeatureSelectorDialog>? _jsRef;
private int _calculatedPreviewSize = 420; // Default fallback
private bool _isInitialized;

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender && _queryResult != null)
    {
        // Wait for layout to stabilize
        await Task.Delay(50);
        
        // Calculate initial preview size
        await CalculatePreviewSizeAsync();
        
        // Set up resize observer
        _jsRef = DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("setupFeatureSelectorResizeObserver", _previewContainerRef, _jsRef);
        }
        catch
        {
            // Continue without resize observer
        }
        
        _isInitialized = true;
    }
}

private async Task CalculatePreviewSizeAsync()
{
    try
    {
        var size = await JS.InvokeAsync<ElementSize>("getElementSize", _previewContainerRef);
        
        if (size.Width > 0 && size.Height > 0)
        {
            // Calculate square size that fits in container with padding
            const int padding = 32;
            var availableWidth = size.Width - padding;
            var availableHeight = size.Height - padding;
            
            // Use the smaller dimension for a square preview
            _calculatedPreviewSize = (int)Math.Min(availableWidth, availableHeight);
            _calculatedPreviewSize = Math.Max(_calculatedPreviewSize, 200); // Minimum size
            
            StateHasChanged();
        }
    }
    catch
    {
        // Keep default size on error
    }
}

[JSInvokable]
public async Task OnPreviewContainerResized()
{
    if (!_isInitialized) return;
    await CalculatePreviewSizeAsync();
}

public async ValueTask DisposeAsync()
{
    if (_jsRef != null)
    {
        try
        {
            await JS.InvokeVoidAsync("removeFeatureSelectorResizeObserver", _previewContainerRef);
        }
        catch
        {
            // Ignore disposal errors
        }
        _jsRef.Dispose();
    }
}
```

#### 6. Update Opening Code in GenerateTerrain.razor.cs

Update the method that opens the dialog to use fullscreen options:

```csharp
private async Task OpenOsmFeatureSelector(TerrainMaterialConfig material)
{
    var parameters = new DialogParameters<OsmFeatureSelectorDialog>
    {
        { x => x.MaterialName, material.InternalName },
        { x => x.BoundingBox, _selectionBoundingBox },
        { x => x.TerrainSize, _terrainSize },
        { x => x.IsRoadMaterial, material.IsRoadMaterial },
        { x => x.ExistingSelections, material.OsmFeatureSelections }
    };
    
    // Use fullscreen dialog options
    var options = new DialogOptions 
    { 
        FullScreen = true, 
        CloseButton = true,
        CloseOnEscapeKey = true
    };
    
    var dialog = await DialogService.ShowAsync<OsmFeatureSelectorDialog>(
        $"Select OSM Features for {material.InternalName}", 
        parameters, 
        options);
    
    var result = await dialog.Result;
    
    if (!result.Canceled && result.Data is List<OsmFeatureSelection> selections)
    {
        material.OsmFeatureSelections = selections;
        StateHasChanged();
    }
}
```

### Estimated Effort: 4-5 hours

---

## Phase 3: OsmFeaturePreviewDialog (Optional - Standalone Fullscreen Preview)

### Goal
Create a standalone fullscreen dialog for viewing OSM features in detail. This is optional and can be added later if needed.

### Use Cases
1. "Expand" button in `OsmFeatureSelectorDialog`
2. Standalone preview from terrain generation page
3. Review selected features before generation

### Implementation
Similar to `CropAnchorSelectorDialog`:
- Fullscreen dialog with responsive preview
- Same JavaScript interop pattern
- Pass features and bounding box as parameters

### Estimated Effort: 2-3 hours (if needed)

---

## Summary

| Phase | Component | Status | Effort |
|-------|-----------|--------|--------|
| 1 | CropAnchorSelectorDialog | ? COMPLETED | - |
| 2 | OsmFeatureSelectorDialog + OsmFeaturePreview | ? COMPLETED | 4-5 hours |
| 3 | OsmFeaturePreviewDialog (optional) | Removed | - |

**Phase 2 Implementation Complete!**

---

## Testing Checklist for Phase 2

### OsmFeatureSelectorDialog
- [ ] Opens in fullscreen mode (`FullScreen = true`)
- [ ] Close button visible and works
- [ ] Escape key closes dialog
- [ ] Two-column layout displays correctly
- [ ] Filter panel has fixed width (320px)
- [ ] Filter panel scrolls when list is long
- [ ] Preview panel fills remaining space

### OsmFeaturePreview (Responsive)
- [ ] Preview size calculated from container
- [ ] Preview maintains square aspect ratio
- [ ] ResizeObserver updates size on window resize
- [ ] Map tiles load at calculated size
- [ ] SVG features align with map tiles
- [ ] Zoom/pan works correctly
- [ ] Map toggle and opacity work
- [ ] Minimum size enforced (200px)

### Integration
- [ ] Features selected in dialog returned correctly
- [ ] Multiple selections work
- [ ] Clear and Select All work
- [ ] Search filter works
- [ ] Category filter works
- [ ] Geometry type filter works

---

## Key Patterns Reference

### ElementSize Record (already exists in CropAnchorSelectorDialog.razor.cs)

```csharp
public record ElementSize(double Width, double Height);
```

### Responsive Sizing Pattern

```csharp
private async Task CalculateSizeAsync()
{
    var size = await JS.InvokeAsync<ElementSize>("getElementSize", _containerRef);
    if (size.Width > 0 && size.Height > 0)
    {
        // Calculate dimensions
        _calculatedSize = CalculateFromContainer(size.Width, size.Height);
        StateHasChanged();
    }
}
```

### Dialog Options Pattern

```csharp
var options = new DialogOptions 
{ 
    FullScreen = true, 
    CloseButton = true,
    CloseOnEscapeKey = true
};
```

---

## Dependencies

- MudBlazor v8+ (already in project)
- Existing JavaScript interop (`getElementSize`)
- `ElementSize` record (already defined)
- No additional NuGet packages required

---

## References

- [MudBlazor Dialog Documentation](https://mudblazor.com/components/dialog)
- [CSS Flexbox Guide](https://css-tricks.com/snippets/css/a-guide-to-flexbox/)
- Existing implementation: `CropAnchorSelectorDialog.razor.cs`
- Existing implementation: `OsmMapZoomFeature-Implementation-Plan.md`
