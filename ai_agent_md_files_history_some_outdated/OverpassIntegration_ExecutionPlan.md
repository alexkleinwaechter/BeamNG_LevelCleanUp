# Overpass API Integration for OSM Shape Selection - Execution Plan

## Overview

This document describes the detailed implementation plan for integrating OpenStreetMap (OSM) Overpass API data as an alternative layer map source for terrain materials in the BeamNG terrain generation workflow.

## Current State Analysis

### Existing Architecture

1. **HeightmapSource Options**: The `GenerateTerrain.razor` page already supports:
   - PNG Heightmap
   - GeoTIFF File
   - GeoTIFF Directory (tiles)

2. **TerrainMaterialSettings Component**: Each material item supports:
   - `LayerMapPath` - path to PNG layer map
   - `IsRoadMaterial` - toggle for road smoothing
   - Road smoothing parameters (spline extraction from skeleton, etc.)

3. **GeoBoundingBox**: Already extracted from GeoTIFF and displayed in UI with Overpass bbox format

4. **MaterialDefinition**: Currently accepts `LayerImagePath` for PNG layer maps

5. **Road Smoothing Flow**:
   - For road materials, the `MedialAxisRoadExtractor` extracts centerlines from a binary PNG mask
   - It uses skeletonization to find paths, then converts to splines
   - The splines are used for elevation smoothing

### Key Insight

When using OSM Overpass data:
- **Polygons** (landuse like forest, grass) ? Rasterize to layer map (similar to current PNG approach)
- **Lines** (roads, rivers) ? Use directly as vector splines, **bypassing skeleton extraction**

---

## Implementation Plan

### Phase 1: Overpass API Service Layer (BeamNgTerrainPoc)

#### 1.1 Create OSM Data Models

**File:** `BeamNgTerrainPoc/Terrain/Osm/Models/OsmElement.cs`

```csharp
// Models for OSM elements (nodes, ways, relations)
public class OsmNode { long Id; double Lat; double Lon; Dictionary<string, string> Tags; }
public class OsmWay { long Id; List<long> NodeRefs; Dictionary<string, string> Tags; bool IsClosed; }
public class OsmRelation { long Id; List<OsmMember> Members; Dictionary<string, string> Tags; }
public class OsmMember { string Type; long Ref; string Role; }
```

**File:** `BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs`

```csharp
// Processed feature with resolved geometry
public class OsmFeature
{
    public long Id { get; set; }
    public OsmFeatureType Type { get; set; } // Node, Way, Relation
    public OsmGeometryType GeometryType { get; set; } // Point, LineString, Polygon
    public List<GeoCoordinate> Coordinates { get; set; } // Resolved coordinates
    public Dictionary<string, string> Tags { get; set; }
    public string DisplayName { get; } // Derived from tags
    public string Category { get; } // highway, landuse, natural, etc.
}

public enum OsmFeatureType { Node, Way, Relation }
public enum OsmGeometryType { Point, LineString, Polygon }
```

**File:** `BeamNgTerrainPoc/Terrain/Osm/Models/OsmQueryResult.cs`

```csharp
public class OsmQueryResult
{
    public GeoBoundingBox BoundingBox { get; set; }
    public List<OsmFeature> Features { get; set; }
    public DateTime QueryTime { get; set; }
    public bool IsFromCache { get; set; }
}
```

#### 1.2 Create Overpass Service

**File:** `BeamNgTerrainPoc/Terrain/Osm/Services/OverpassApiService.cs`

```csharp
public interface IOverpassApiService
{
    Task<OsmQueryResult> QueryAllFeaturesAsync(GeoBoundingBox bbox, CancellationToken ct = default);
    Task<OsmQueryResult> QueryByTagsAsync(GeoBoundingBox bbox, Dictionary<string, string?> tagFilter, CancellationToken ct = default);
}

public class OverpassApiService : IOverpassApiService
{
    private readonly HttpClient _httpClient;
    private const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";
    private const int TimeoutSeconds = 120;

    // Query template for all features in bbox
    private string BuildAllFeaturesQuery(GeoBoundingBox bbox) => $"""
        [out:xml][timeout:{TimeoutSeconds}];
        (
          nwr({bbox.MinLatitude},{bbox.MinLongitude},{bbox.MaxLatitude},{bbox.MaxLongitude});
          <;
          >;
        );
        out meta;
        """;

    public async Task<OsmQueryResult> QueryAllFeaturesAsync(GeoBoundingBox bbox, CancellationToken ct = default)
    {
        var query = BuildAllFeaturesQuery(bbox);
        var response = await ExecuteQueryAsync(query, ct);
        return ParseResponse(response, bbox);
    }

    private async Task<string> ExecuteQueryAsync(string query, CancellationToken ct);
    private OsmQueryResult ParseResponse(string xmlResponse, GeoBoundingBox bbox);
}
```

#### 1.3 Create OSM XML Parser

**File:** `BeamNgTerrainPoc/Terrain/Osm/Parsing/OsmXmlParser.cs`

```csharp
public class OsmXmlParser
{
    public OsmQueryResult Parse(string xml, GeoBoundingBox bbox)
    {
        // Parse nodes, ways, relations from OSM XML
        // Resolve way node references to actual coordinates
        // Detect geometry type (polygon if closed way, linestring if open)
        // Build OsmFeature list with resolved geometries
    }
}
```

#### 1.4 Create Geometry Cropper & Transformer

**File:** `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`

```csharp
public class OsmGeometryProcessor
{
    /// <summary>
    /// Crops features to the terrain bounding box and transforms to pixel coordinates.
    /// </summary>
    public List<OsmFeature> CropToBoundingBox(List<OsmFeature> features, GeoBoundingBox bbox);

    /// <summary>
    /// Transforms geographic coordinates to pixel coordinates for a given terrain size.
    /// </summary>
    public List<Vector2> TransformToPixelCoordinates(
        List<GeoCoordinate> geoCoords, 
        GeoBoundingBox bbox, 
        int terrainSize);

    /// <summary>
    /// Rasterizes polygon features to a binary layer map image.
    /// </summary>
    public byte[,] RasterizePolygonsToLayerMap(
        List<OsmFeature> polygonFeatures, 
        GeoBoundingBox bbox, 
        int terrainSize);

    /// <summary>
    /// Converts line features to RoadSpline objects for road smoothing.
    /// </summary>
    public List<RoadSpline> ConvertLinesToSplines(
        List<OsmFeature> lineFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel);
}
```

---

### Phase 2: Layer Source Abstraction (BeamNgTerrainPoc)

#### 2.1 Create Layer Source Types

**File:** `BeamNgTerrainPoc/Terrain/Models/LayerSource.cs`

```csharp
public enum LayerSourceType
{
    None,
    PngFile,
    OsmFeatures
}

public class LayerSource
{
    public LayerSourceType SourceType { get; set; } = LayerSourceType.None;
    
    // For PngFile source
    public string? PngFilePath { get; set; }
    
    // For OsmFeatures source
    public List<OsmFeatureSelection>? SelectedOsmFeatures { get; set; }
}

public class OsmFeatureSelection
{
    public long FeatureId { get; set; }
    public string DisplayName { get; set; }
    public OsmGeometryType GeometryType { get; set; }
    public Dictionary<string, string> Tags { get; set; }
    // The actual geometry will be fetched from cached OsmQueryResult
}
```

#### 2.2 Update MaterialDefinition

**File:** `BeamNgTerrainPoc/Terrain/Models/MaterialDefinition.cs` (MODIFIED)

```csharp
public class MaterialDefinition
{
    public string MaterialName { get; set; }
    
    // Legacy - still supported
    public string? LayerImagePath { get; set; }
    
    // New - alternative layer source
    public LayerSource? LayerSource { get; set; }
    
    public RoadSmoothingParameters? RoadParameters { get; set; }
    
    // Computed property
    public bool HasLayerSource => 
        !string.IsNullOrEmpty(LayerImagePath) || 
        (LayerSource?.SourceType != LayerSourceType.None);
}
```

#### 2.3 Update RoadSmoothingParameters

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` (MODIFIED)

Add property to specify spline source:

```csharp
public class RoadSmoothingParameters
{
    // ... existing properties ...
    
    /// <summary>
    /// When set, these pre-built splines are used instead of extracting from layer map.
    /// Used when OSM line features are selected as the layer source.
    /// </summary>
    public List<RoadSpline>? PreBuiltSplines { get; set; }
    
    /// <summary>
    /// When true, skip skeleton extraction and path finding - use PreBuiltSplines directly.
    /// </summary>
    public bool UsePreBuiltSplines => PreBuiltSplines?.Any() == true;
}
```

#### 2.4 Update RoadSmoothingService

**File:** `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` (MODIFIED)

Modify `SmoothRoadsInHeightmap` to handle pre-built splines:

```csharp
public SmoothingResult SmoothRoadsInHeightmap(...)
{
    // ... existing validation ...
    
    RoadGeometry geometry;
    
    if (parameters.UsePreBuiltSplines)
    {
        // Skip extraction - build geometry from pre-built splines
        TerrainLogger.Info("Using pre-built splines from OSM data...");
        geometry = BuildGeometryFromSplines(
            roadLayer, 
            parameters.PreBuiltSplines!, 
            parameters, 
            metersPerPixel);
    }
    else
    {
        // Existing path - extract from road layer mask
        geometry = _roadExtractor!.ExtractRoadGeometry(roadLayer, parameters, metersPerPixel);
    }
    
    // ... rest of method unchanged ...
}

private RoadGeometry BuildGeometryFromSplines(
    byte[,] roadMask,
    List<RoadSpline> splines,
    RoadSmoothingParameters parameters,
    float metersPerPixel)
{
    var geometry = new RoadGeometry(roadMask, parameters);
    
    // Generate cross-sections from each spline
    foreach (var spline in splines)
    {
        var samples = spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
        foreach (var sample in samples)
        {
            geometry.CrossSections.Add(new CrossSection(
                sample.Position,
                sample.Normal,
                sample.Distance,
                parameters.RoadWidthMeters,
                parameters.TerrainAffectedRangeMeters));
        }
    }
    
    return geometry;
}
```

---

### Phase 3: UI Components (BeamNG_LevelCleanUp)

#### 3.1 Create OSM Feature Selector Dialog

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/OsmFeatureSelectorDialog.razor`

A MudBlazor dialog for selecting OSM features:

```razor
@inject IDialogService DialogService

<MudDialog>
    <TitleContent>
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.Map" Class="mr-2"/>
            Select OSM Features for @MaterialName
        </MudText>
    </TitleContent>
    <DialogContent>
        @if (_isLoading)
        {
            <MudProgressCircular Indeterminate="true"/>
            <MudText>Fetching OSM data...</MudText>
        }
        else if (_error != null)
        {
            <MudAlert Severity="Severity.Error">@_error</MudAlert>
        }
        else
        {
            <!-- Category Filter -->
            <MudChipSet T="string" @bind-SelectedValues="_selectedCategories" 
                        SelectionMode="SelectionMode.MultiSelection">
                <MudChip Value="@("highway")">Roads</MudChip>
                <MudChip Value="@("landuse")">Landuse</MudChip>
                <MudChip Value="@("natural")">Natural</MudChip>
                <MudChip Value="@("building")">Buildings</MudChip>
                <MudChip Value="@("waterway")">Water</MudChip>
            </MudChipSet>
            
            <!-- Geometry Type Filter -->
            <MudStack Row="true" Class="mt-2">
                <MudCheckBox @bind-Value="_showLines" Label="Lines (Roads)"/>
                <MudCheckBox @bind-Value="_showPolygons" Label="Polygons (Areas)"/>
            </MudStack>
            
            <!-- Feature List with Preview -->
            <MudGrid>
                <MudItem xs="6">
                    <MudList T="OsmFeature" @bind-SelectedValues="_selectedFeatures"
                             SelectionMode="SelectionMode.MultiSelection"
                             Dense="true">
                        @foreach (var feature in FilteredFeatures)
                        {
                            <MudListItem Value="@feature">
                                <MudStack Row="true" AlignItems="AlignItems.Center">
                                    <MudIcon Icon="@GetFeatureIcon(feature)" Size="Size.Small"/>
                                    <MudText Typo="Typo.body2">@feature.DisplayName</MudText>
                                    <MudChip T="string" Size="Size.Small" Color="@GetGeometryColor(feature)">
                                        @feature.GeometryType
                                    </MudChip>
                                </MudStack>
                            </MudListItem>
                        }
                    </MudList>
                </MudItem>
                <MudItem xs="6">
                    <!-- Preview Canvas showing cropped features -->
                    <MudPaper Class="pa-2" Style="height: 400px; background: #1a1a2e;">
                        <OsmFeaturePreview Features="@_selectedFeatures.ToList()"
                                           BoundingBox="@BoundingBox"
                                           TerrainSize="@TerrainSize"/>
                    </MudPaper>
                </MudItem>
            </MudGrid>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" OnClick="Confirm" 
                   Disabled="@(!_selectedFeatures.Any())">
            Select @_selectedFeatures.Count Features
        </MudButton>
    </DialogActions>
</MudDialog>
```

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/OsmFeatureSelectorDialog.razor.cs`

```csharp
public partial class OsmFeatureSelectorDialog : ComponentBase
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; }
    
    [Parameter] public string MaterialName { get; set; }
    [Parameter] public GeoBoundingBox BoundingBox { get; set; }
    [Parameter] public int TerrainSize { get; set; }
    [Parameter] public bool IsRoadMaterial { get; set; }
    [Parameter] public List<OsmFeatureSelection>? ExistingSelections { get; set; }
    
    private OsmQueryResult? _queryResult;
    private IReadOnlyCollection<OsmFeature> _selectedFeatures = new List<OsmFeature>();
    private IReadOnlyCollection<string> _selectedCategories = new List<string>();
    private bool _showLines = true;
    private bool _showPolygons = true;
    private bool _isLoading = true;
    private string? _error;
    
    protected override async Task OnInitializedAsync()
    {
        try
        {
            // Fetch OSM data for bbox
            var service = new OverpassApiService();
            _queryResult = await service.QueryAllFeaturesAsync(BoundingBox);
            
            // Pre-filter based on material type
            if (IsRoadMaterial)
            {
                _showLines = true;
                _showPolygons = false;
                _selectedCategories = new List<string> { "highway" };
            }
            else
            {
                _showLines = false;
                _showPolygons = true;
                _selectedCategories = new List<string> { "landuse", "natural" };
            }
            
            // Restore existing selections
            if (ExistingSelections?.Any() == true)
            {
                _selectedFeatures = _queryResult.Features
                    .Where(f => ExistingSelections.Any(s => s.FeatureId == f.Id))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _error = $"Failed to fetch OSM data: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }
    
    private IEnumerable<OsmFeature> FilteredFeatures => _queryResult?.Features
        .Where(f => 
            (_showLines && f.GeometryType == OsmGeometryType.LineString ||
             _showPolygons && f.GeometryType == OsmGeometryType.Polygon) &&
            (!_selectedCategories.Any() || _selectedCategories.Contains(f.Category)))
        ?? Enumerable.Empty<OsmFeature>();
    
    private void Confirm()
    {
        var selections = _selectedFeatures.Select(f => new OsmFeatureSelection
        {
            FeatureId = f.Id,
            DisplayName = f.DisplayName,
            GeometryType = f.GeometryType,
            Tags = f.Tags
        }).ToList();
        
        MudDialog.Close(DialogResult.Ok(selections));
    }
    
    private void Cancel() => MudDialog.Cancel();
}
```

#### 3.2 Create Feature Preview Component

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/OsmFeaturePreview.razor`

SVG-based preview showing selected features cropped to terrain bounds:

```razor
<svg width="100%" height="100%" viewBox="0 0 @TerrainSize @TerrainSize"
     style="background: #2d2d3a;">
    <!-- Grid -->
    @for (var i = 0; i <= TerrainSize; i += TerrainSize / 8)
    {
        <line x1="@i" y1="0" x2="@i" y2="@TerrainSize" stroke="#444" stroke-width="1"/>
        <line x1="0" y1="@i" x2="@TerrainSize" y2="@i" stroke="#444" stroke-width="1"/>
    }
    
    <!-- Features -->
    @foreach (var feature in Features)
    {
        var pixelCoords = TransformToPixel(feature.Coordinates);
        @if (feature.GeometryType == OsmGeometryType.Polygon)
        {
            <polygon points="@GetPointsString(pixelCoords)" 
                     fill="@GetFillColor(feature)" 
                     stroke="@GetStrokeColor(feature)"
                     stroke-width="2"
                     opacity="0.6"/>
        }
        else if (feature.GeometryType == OsmGeometryType.LineString)
        {
            <polyline points="@GetPointsString(pixelCoords)"
                      fill="none"
                      stroke="@GetStrokeColor(feature)"
                      stroke-width="3"/>
        }
    }
</svg>
```

#### 3.3 Update TerrainMaterialSettings Component

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` (MODIFIED)

Add OSM source option alongside Layer Map selection:

```razor
<!-- Layer Source Selection (visible when GeoTIFF source AND bbox available) -->
<MudItem xs="12">
    <MudText Typo="Typo.subtitle2" Class="mb-2">Layer Source</MudText>
    
    <MudButtonGroup OverrideStyles="false" Class="mb-2">
        <MudButton Variant="@(Material.LayerSourceType == LayerSourceType.None ? Variant.Filled : Variant.Outlined)"
                   OnClick="@(() => SetLayerSourceType(LayerSourceType.None))"
                   Size="Size.Small">
            None
        </MudButton>
        <MudButton Variant="@(Material.LayerSourceType == LayerSourceType.PngFile ? Variant.Filled : Variant.Outlined)"
                   OnClick="@(() => SetLayerSourceType(LayerSourceType.PngFile))"
                   Size="Size.Small">
            PNG File
        </MudButton>
        <MudButton Variant="@(Material.LayerSourceType == LayerSourceType.OsmFeatures ? Variant.Filled : Variant.Outlined)"
                   OnClick="@(() => SetLayerSourceType(LayerSourceType.OsmFeatures))"
                   Size="Size.Small"
                   Disabled="@(!HasGeoBoundingBox)">
            OSM Shapes
        </MudButton>
    </MudButtonGroup>
    
    @if (Material.LayerSourceType == LayerSourceType.PngFile)
    {
        <!-- Existing PNG file selection UI -->
        <div class="d-flex align-center gap-2">
            <MudTextField @bind-Value="Material.LayerMapPath" ... />
            <MudButton OnClick="SelectLayerMap">Browse</MudButton>
        </div>
    }
    else if (Material.LayerSourceType == LayerSourceType.OsmFeatures)
    {
        <!-- OSM Features selection -->
        <div class="d-flex align-center gap-2">
            <MudChip T="string" Size="Size.Small">
                @(Material.SelectedOsmFeatures?.Count ?? 0) features selected
            </MudChip>
            <MudButton Variant="Variant.Filled" 
                       Color="Color.Primary"
                       StartIcon="@Icons.Material.Filled.Map"
                       OnClick="OpenOsmFeatureSelector">
                Select OSM Shapes
            </MudButton>
        </div>
        
        @if (Material.SelectedOsmFeatures?.Any() == true)
        {
            <MudStack Row="true" Wrap="Wrap.Wrap" Class="mt-2">
                @foreach (var feature in Material.SelectedOsmFeatures)
                {
                    <MudChip T="string" 
                             Size="Size.Small" 
                             OnClose="@(() => RemoveOsmFeature(feature))"
                             Color="@GetFeatureChipColor(feature)">
                        @feature.DisplayName
                    </MudChip>
                }
            </MudStack>
        }
    }
</MudItem>
```

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` (MODIFIED)

Add new properties and methods:

```csharp
public partial class TerrainMaterialSettings
{
    [Parameter] public GeoBoundingBox? GeoBoundingBox { get; set; }
    [Parameter] public int TerrainSize { get; set; }
    
    [Inject] private IDialogService DialogService { get; set; } = null!;
    
    private bool HasGeoBoundingBox => GeoBoundingBox != null;
    
    private async Task OpenOsmFeatureSelector()
    {
        var parameters = new DialogParameters
        {
            { nameof(OsmFeatureSelectorDialog.MaterialName), Material.InternalName },
            { nameof(OsmFeatureSelectorDialog.BoundingBox), GeoBoundingBox },
            { nameof(OsmFeatureSelectorDialog.TerrainSize), TerrainSize },
            { nameof(OsmFeatureSelectorDialog.IsRoadMaterial), Material.IsRoadMaterial },
            { nameof(OsmFeatureSelectorDialog.ExistingSelections), Material.SelectedOsmFeatures }
        };
        
        var options = new DialogOptions 
        { 
            MaxWidth = MaxWidth.Large, 
            FullWidth = true,
            CloseOnEscapeKey = true
        };
        
        var dialog = await DialogService.ShowAsync<OsmFeatureSelectorDialog>(
            "Select OSM Features", parameters, options);
        var result = await dialog.Result;
        
        if (!result.Canceled && result.Data is List<OsmFeatureSelection> selections)
        {
            Material.SelectedOsmFeatures = selections;
            Material.LayerSourceType = LayerSourceType.OsmFeatures;
            await OnMaterialChanged.InvokeAsync(Material);
        }
    }
    
    private void RemoveOsmFeature(OsmFeatureSelection feature)
    {
        Material.SelectedOsmFeatures?.Remove(feature);
        if (Material.SelectedOsmFeatures?.Count == 0)
        {
            Material.LayerSourceType = LayerSourceType.None;
        }
        OnMaterialChanged.InvokeAsync(Material);
    }
}
```

#### 3.4 Update TerrainMaterialItemExtended

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` (MODIFIED)

Extend the `TerrainMaterialItemExtended` class:

```csharp
public class TerrainMaterialItemExtended
{
    // ... existing properties ...
    
    // New: Layer source type
    public LayerSourceType LayerSourceType { get; set; } = LayerSourceType.None;
    
    // New: Selected OSM features
    public List<OsmFeatureSelection>? SelectedOsmFeatures { get; set; }
    
    // Updated: HasLayerMap now considers OSM source
    public bool HasLayerMap => 
        LayerSourceType == LayerSourceType.PngFile && !string.IsNullOrEmpty(LayerMapPath) ||
        LayerSourceType == LayerSourceType.OsmFeatures && SelectedOsmFeatures?.Any() == true;
}
```

#### 3.5 Update GenerateTerrain Page

**File:** `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` (MODIFIED)

Pass GeoBoundingBox to material settings:

```razor
<TerrainMaterialSettings Material="@context"
                         OnMaterialChanged="OnMaterialSettingsChanged"
                         GeoBoundingBox="@_geoBoundingBox"
                         TerrainSize="@_terrainSize" />
```

**File:** `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` (MODIFIED)

Update `ExecuteTerrainGeneration` to handle OSM layer sources:

```csharp
private async Task ExecuteTerrainGeneration()
{
    // ... existing validation ...
    
    // Build material definitions with OSM processing
    var orderedMaterials = _terrainMaterials.OrderBy(m => m.Order).ToList();
    var materialDefinitions = new List<MaterialDefinition>();
    
    foreach (var mat in orderedMaterials)
    {
        RoadSmoothingParameters? roadParams = null;
        string? layerImagePath = null;
        
        // Process layer source
        if (mat.LayerSourceType == LayerSourceType.OsmFeatures && 
            mat.SelectedOsmFeatures?.Any() == true && 
            _geoBoundingBox != null)
        {
            var processor = new OsmGeometryProcessor();
            
            if (mat.IsRoadMaterial)
            {
                // For road materials: convert lines to splines
                var lineFeatures = mat.SelectedOsmFeatures
                    .Where(f => f.GeometryType == OsmGeometryType.LineString)
                    .ToList();
                
                // Get full features from cached query result
                var fullFeatures = await GetOsmFeaturesById(lineFeatures.Select(f => f.FeatureId));
                
                var splines = processor.ConvertLinesToSplines(
                    fullFeatures, 
                    _geoBoundingBox, 
                    _terrainSize, 
                    _metersPerPixel);
                
                roadParams = mat.BuildRoadSmoothingParameters(debugPath);
                roadParams.PreBuiltSplines = splines;
                
                // Also rasterize for road mask
                layerImagePath = await RasterizeAndSaveTemp(fullFeatures, mat.InternalName);
            }
            else
            {
                // For non-road materials: rasterize polygons to layer map
                var polygonFeatures = mat.SelectedOsmFeatures
                    .Where(f => f.GeometryType == OsmGeometryType.Polygon)
                    .ToList();
                
                var fullFeatures = await GetOsmFeaturesById(polygonFeatures.Select(f => f.FeatureId));
                
                layerImagePath = await RasterizeAndSaveTemp(fullFeatures, mat.InternalName);
            }
        }
        else if (mat.LayerSourceType == LayerSourceType.PngFile)
        {
            layerImagePath = mat.LayerMapPath;
            
            if (mat.IsRoadMaterial)
            {
                roadParams = mat.BuildRoadSmoothingParameters(debugPath);
            }
        }
        
        materialDefinitions.Add(new MaterialDefinition(
            mat.InternalName,
            layerImagePath,
            roadParams));
    }
    
    // ... rest of generation ...
}
```

---

### Phase 4: Caching & Performance

#### 4.1 OSM Query Cache

**File:** `BeamNgTerrainPoc/Terrain/Osm/Services/OsmQueryCache.cs`

```csharp
public class OsmQueryCache
{
    private readonly Dictionary<string, OsmQueryResult> _cache = new();
    private readonly string _cacheDirectory;
    
    public OsmQueryCache(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG_LevelCleanUp", "OsmCache");
        Directory.CreateDirectory(_cacheDirectory);
    }
    
    public string GetCacheKey(GeoBoundingBox bbox) => 
        $"osm_{bbox.ToFileNameString()}.json";
    
    public async Task<OsmQueryResult?> GetAsync(GeoBoundingBox bbox);
    public async Task SetAsync(GeoBoundingBox bbox, OsmQueryResult result);
    public void Invalidate(GeoBoundingBox bbox);
}
```

#### 4.2 Rasterization Temp File Management

```csharp
// Temporary rasterized layer maps are saved to MT_TerrainGeneration folder
// and cleaned up after terrain generation completes
private async Task<string> RasterizeAndSaveTemp(
    List<OsmFeature> features, 
    string materialName)
{
    var processor = new OsmGeometryProcessor();
    var layerMap = processor.RasterizePolygonsToLayerMap(
        features, _geoBoundingBox!, _terrainSize);
    
    var tempPath = Path.Combine(
        GetDebugPath(), 
        $"{SanitizeFolderName(materialName)}_osm_layer.png");
    
    await SaveLayerMapToPng(layerMap, tempPath);
    return tempPath;
}
```

---

### Phase 5: Road Smoothing Adaptations

#### 5.1 Skip Skeleton Extraction for OSM Lines

The existing `MedialAxisRoadExtractor` extracts paths via skeletonization. When using OSM lines, we bypass this entirely by using `PreBuiltSplines`.

Key changes in `RoadSmoothingService`:

```csharp
// When parameters.UsePreBuiltSplines is true:
// 1. Skip _roadExtractor.ExtractRoadGeometry()
// 2. Build RoadGeometry directly from splines
// 3. All other processing (elevation smoothing, blending) works the same
```

#### 5.2 Handle Multiple Lines Per Material

OSM data may have many separate line features (e.g., many road segments). These need to be:
1. Merged where endpoints meet (junction detection)
2. Kept as separate splines where they don't connect
3. Processed individually by the elevation smoother

```csharp
public List<RoadSpline> ConvertLinesToSplines(
    List<OsmFeature> lineFeatures,
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel)
{
    var splines = new List<RoadSpline>();
    
    foreach (var feature in lineFeatures)
    {
        // Transform to pixel coordinates
        var pixelCoords = TransformToPixelCoordinates(
            feature.Coordinates, bbox, terrainSize);
        
        // Crop to terrain bounds
        var croppedCoords = CropLineToTerrain(pixelCoords, terrainSize);
        
        if (croppedCoords.Count >= 2)
        {
            splines.Add(new RoadSpline(croppedCoords));
        }
    }
    
    return splines;
}
```

---

### Phase 6: Testing & Validation

#### 6.1 Test Scenarios

1. **Basic Polygon Rasterization**
   - Select forest landuse polygon from OSM
   - Verify correct rasterization to layer map
   - Verify material layer appears correctly in generated terrain

2. **Basic Road Line Processing**
   - Select highway from OSM
   - Verify conversion to splines
   - Verify road smoothing applies correctly without skeleton extraction

3. **Multiple Feature Merge**
   - Select multiple forest polygons
   - Verify they merge into single layer map

4. **Cross-Type Mixed Selection**
   - Select both highways and forest areas
   - Assign to different materials
   - Verify each processes correctly

5. **Edge Cases**
   - Features partially outside terrain bounds (cropping)
   - Very small features (filtering)
   - Self-intersecting polygons (validation)

#### 6.2 Debug Output

Enable debug image export:
- `{material}_osm_layer.png` - Rasterized layer map from OSM
- `{material}_osm_splines.png` - Spline visualization (for roads)
- `{material}_osm_cropped.png` - Features after cropping to terrain

---

## File Summary

### New Files (BeamNgTerrainPoc)

| Path | Purpose |
|------|---------|
| `Terrain/Osm/Models/OsmNode.cs` | OSM node data model |
| `Terrain/Osm/Models/OsmWay.cs` | OSM way data model |
| `Terrain/Osm/Models/OsmRelation.cs` | OSM relation data model |
| `Terrain/Osm/Models/OsmFeature.cs` | Processed feature with geometry |
| `Terrain/Osm/Models/OsmFeatureSelection.cs` | UI selection model |
| `Terrain/Osm/Models/OsmQueryResult.cs` | Query result container |
| `Terrain/Osm/Services/IOverpassApiService.cs` | Service interface |
| `Terrain/Osm/Services/OverpassApiService.cs` | Overpass API client |
| `Terrain/Osm/Services/OsmQueryCache.cs` | Query result caching |
| `Terrain/Osm/Parsing/OsmXmlParser.cs` | OSM XML parser |
| `Terrain/Osm/Processing/OsmGeometryProcessor.cs` | Geometry processing |
| `Terrain/Models/LayerSource.cs` | Layer source abstraction |

### New Files (BeamNG_LevelCleanUp)

| Path | Purpose |
|------|---------|
| `BlazorUI/Components/OsmFeatureSelectorDialog.razor` | Feature selection dialog UI |
| `BlazorUI/Components/OsmFeatureSelectorDialog.razor.cs` | Dialog code-behind |
| `BlazorUI/Components/OsmFeaturePreview.razor` | SVG feature preview |
| `BlazorUI/Components/OsmFeaturePreview.razor.cs` | Preview code-behind |

### Modified Files

| Path | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/MaterialDefinition.cs` | Add LayerSource property |
| `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` | Add PreBuiltSplines |
| `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs` | Handle PreBuiltSplines |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Add OSM source UI |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Add OSM handling |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Pass bbox to materials |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | OSM processing in generation |

---

## Dependencies

### NuGet Packages (may need to add)

```xml
<!-- For polygon rasterization -->
<PackageReference Include="SixLabors.ImageSharp.Drawing" Version="2.1.4" />
```

### HttpClient Configuration

```csharp
// In Program.cs or service registration
services.AddHttpClient<IOverpassApiService, OverpassApiService>(client =>
{
    client.BaseAddress = new Uri("https://overpass-api.de/api/");
    client.Timeout = TimeSpan.FromSeconds(120);
    client.DefaultRequestHeaders.Add("User-Agent", "BeamNG_LevelCleanUp/1.0");
});
```

---

## Implementation Order

1. **Phase 1** - Overpass API service (can be tested independently)
2. **Phase 2** - Layer source abstraction (minimal UI changes)
3. **Phase 3** - UI components (OsmFeatureSelectorDialog)
4. **Phase 4** - Caching (improve UX)
5. **Phase 5** - Road smoothing adaptations
6. **Phase 6** - Testing

Estimated effort: 3-4 days for core functionality, +1-2 days for polish and edge cases.

---

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Overpass API rate limiting | Implement caching, use fallback endpoints |
| Large OSM responses | Stream parsing, pagination if available |
| Complex polygon rasterization | Use SixLabors.ImageSharp.Drawing |
| Coordinate precision loss | Use double throughout, convert to float only for final pixel coords |
| Multi-polygon relations | Flatten to individual polygons |
| Self-intersecting polygons | Validate and simplify before rasterization |
