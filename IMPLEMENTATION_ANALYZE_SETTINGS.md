# Implementation: Analyze Settings Feature

## Overview

This document describes the implementation plan for the **"Analyze Settings"** feature in the GenerateTerrain page. This feature allows users to perform a pre-generation analysis that:

1. Runs the complete spline extraction and junction detection pipeline
2. Visualizes all detected splines and junctions interactively
3. Allows users to manually exclude problematic junctions before terrain generation
4. Preserves these exclusions when running the actual terrain generation

The feature is inspired by the junction debug image (showing cyan T-junctions, pink mid-spline crossings, etc.) but provides an interactive UI where users can click to exclude junctions that cause terrain artifacts.

---

## Architecture Analysis

### Current Flow (GenerateTerrain → TerrainCreator)

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  GenerateTerrain.razor / .razor.cs                                              │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  TerrainGenerationOrchestrator.ExecuteAsync(state)                       │   │
│  │    ├── ProcessMaterialAsync() for each material                          │   │
│  │    │   └── Builds MaterialDefinition with PreBuiltSplines (from OSM)     │   │
│  │    └── creator.CreateTerrainFileAsync(outputPath, parameters)            │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│  TerrainCreator.cs                                                              │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  CreateTerrainFileAsync()                                                │   │
│  │    ├── Load/process heightmap (PNG or GeoTIFF)                           │   │
│  │    ├── ApplyRoadSmoothing() ─────────────────────────────────────────┐   │   │
│  │    │                                                                  │   │   │
│  │    │   UnifiedRoadSmoother.SmoothAllRoads()                          │   │   │
│  │    │     ├── UnifiedRoadNetworkBuilder.BuildNetwork()                │   │   │
│  │    │     │   └── Builds ParameterizedRoadSplines + UnifiedCrossSections  │   │
│  │    │     ├── CalculateNetworkElevations()                            │   │   │
│  │    │     ├── NetworkJunctionDetector.DetectJunctions() ◄─── TARGET   │   │   │
│  │    │     │   └── Returns List<NetworkJunction>                       │   │   │
│  │    │     ├── NetworkJunctionHarmonizer.HarmonizeNetwork() ◄── TARGET │   │   │
│  │    │     │   └── Modifies cross-section elevations                   │   │   │
│  │    │     ├── UnifiedTerrainBlender.BlendNetworkWithTerrain()         │   │   │
│  │    │     └── MaterialPainter.PaintMaterials()                        │   │   │
│  │    │                                                                  │   │   │
│  │    ├── Process material layers                                        │   │   │
│  │    └── Save .ter file                                                 │   │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Key Classes Involved

| Class | Location | Purpose |
|-------|----------|---------|
| `UnifiedRoadSmoother` | `BeamNgTerrainPoc/Terrain/Services/` | Orchestrates the entire road smoothing pipeline |
| `UnifiedRoadNetworkBuilder` | `BeamNgTerrainPoc/Terrain/Algorithms/` | Builds splines and cross-sections from materials |
| `NetworkJunctionDetector` | `BeamNgTerrainPoc/Terrain/Algorithms/` | Detects junctions (T, Y, X, MidSplineCrossing) |
| `NetworkJunctionHarmonizer` | `BeamNgTerrainPoc/Terrain/Algorithms/` | Harmonizes elevations at junctions |
| `NetworkJunction` | `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/` | Represents a detected junction |
| `UnifiedRoadNetwork` | `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/` | Contains all splines, cross-sections, and junctions |

---

## Proposed Implementation

### 1. New Analyzer Service in Library

Create a new service that exposes the analysis phase separately from full generation:

**File: `BeamNgTerrainPoc/Terrain/Services/TerrainAnalyzer.cs`**

```csharp
namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Analyzes terrain road network without performing full generation.
/// Extracts splines, detects junctions, and allows interactive modification
/// before final terrain generation.
/// </summary>
public class TerrainAnalyzer
{
    /// <summary>
    /// Result of terrain analysis containing the unified road network.
    /// </summary>
    public class AnalysisResult
    {
        public bool Success { get; init; }
        public UnifiedRoadNetwork? Network { get; init; }
        public string? ErrorMessage { get; init; }
        public Dictionary<int, float> PreHarmonizationElevations { get; init; } = new();
        
        // Debug image data
        public byte[]? JunctionDebugImage { get; init; }
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
    }

    /// <summary>
    /// Analyzes the road network and detects all junctions without modifying terrain.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        TerrainCreationParameters parameters,
        float[,] heightMap);

    /// <summary>
    /// Marks specific junctions as excluded (won't be harmonized).
    /// Call this after user interactively selects junctions to exclude.
    /// </summary>
    public void ExcludeJunctions(UnifiedRoadNetwork network, IEnumerable<int> junctionIds);

    /// <summary>
    /// Gets the network for use in full terrain generation with exclusions applied.
    /// </summary>
    public UnifiedRoadNetwork? GetModifiedNetwork();
}
```

### 2. Junction Exclusion Support in Existing Models

Modify `NetworkJunction` to support exclusion:

**File: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs`** (existing)

Add property:
```csharp
/// <summary>
/// Whether this junction is excluded from harmonization.
/// Excluded junctions are skipped during elevation harmonization,
/// allowing the original terrain elevation to be used at this location.
/// </summary>
public bool IsExcluded { get; set; } = false;

/// <summary>
/// Reason for exclusion (user-provided or auto-detected).
/// </summary>
public string? ExclusionReason { get; set; }
```

### 3. New Blazor Component for Analysis Viewer

**File: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainAnalysisViewer.razor`**

```razor
@* Interactive viewer showing detected junctions and splines *@
@* Features:
   - Canvas/SVG rendering of the terrain preview
   - Junction markers (colored by type) that can be clicked
   - Checkbox to toggle junction exclusion
   - Spline centerlines visualization
   - Zoom and pan controls
   - Export modified settings back to parent
*@
```

**File: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainAnalysisViewer.razor.cs`**

```csharp
public partial class TerrainAnalysisViewer : ComponentBase
{
    [Parameter] public AnalysisResult? AnalysisResult { get; set; }
    [Parameter] public EventCallback<List<int>> OnJunctionsExcluded { get; set; }
    [Parameter] public int TerrainSize { get; set; }
    [Parameter] public float MetersPerPixel { get; set; }

    // Tracks which junctions user has excluded
    private HashSet<int> _excludedJunctionIds = new();
    
    // Pan/zoom state
    private float _zoom = 1.0f;
    private float _panX = 0;
    private float _panY = 0;
}
```

### 4. Analysis State Management

**File: `BeamNG_LevelCleanUp/BlazorUI/State/TerrainAnalysisState.cs`**

```csharp
namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>
/// Holds the state of terrain analysis, including detected junctions
/// and user-modified exclusions.
/// </summary>
public class TerrainAnalysisState
{
    /// <summary>
    /// The analyzed road network (null if not yet analyzed).
    /// </summary>
    public UnifiedRoadNetwork? Network { get; set; }

    /// <summary>
    /// Junction IDs that user has marked for exclusion.
    /// </summary>
    public HashSet<int> ExcludedJunctionIds { get; } = new();

    /// <summary>
    /// Whether analysis has been performed.
    /// </summary>
    public bool HasAnalysis => Network != null;

    /// <summary>
    /// Pre-harmonization elevations for comparison.
    /// </summary>
    public Dictionary<int, float> PreHarmonizationElevations { get; set; } = new();

    /// <summary>
    /// Clears all analysis state.
    /// </summary>
    public void Reset()
    {
        Network = null;
        ExcludedJunctionIds.Clear();
        PreHarmonizationElevations.Clear();
    }

    /// <summary>
    /// Applies exclusions to the network before generation.
    /// </summary>
    public void ApplyExclusions()
    {
        if (Network == null) return;
        
        foreach (var junction in Network.Junctions)
        {
            junction.IsExcluded = ExcludedJunctionIds.Contains(junction.JunctionId);
        }
    }
}
```

### 5. Analysis Orchestrator Service

**File: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainAnalysisOrchestrator.cs`**

```csharp
namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
/// Orchestrates terrain analysis similar to TerrainGenerationOrchestrator
/// but stops after junction detection without modifying terrain.
/// </summary>
public class TerrainAnalysisOrchestrator
{
    /// <summary>
    /// Performs analysis phase only (spline extraction + junction detection).
    /// </summary>
    public async Task<TerrainAnalyzer.AnalysisResult> AnalyzeAsync(TerrainGenerationState state);

    /// <summary>
    /// Generates debug image data for the analysis result.
    /// </summary>
    public byte[] GenerateAnalysisImage(
        UnifiedRoadNetwork network,
        int terrainSize,
        float metersPerPixel);
}
```

### 6. UI Integration in GenerateTerrain.razor

Add the Analyze Settings button and dialog:

```razor
@* In the Generate Button section *@
<MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center">
    <div>
        <MudText Typo="Typo.body1">
            <strong>Output:</strong> @GetOutputPath()
        </MudText>
    </div>
    <MudStack Row="true" Spacing="2">
        @* NEW: Analyze Settings Button *@
        <MudButton Variant="Variant.Outlined"
                   Color="Color.Secondary"
                   Size="Size.Large"
                   StartIcon="@Icons.Material.Filled.Analytics"
                   OnClick="ExecuteAnalysis"
                   Disabled="@(!CanGenerate() || _isGenerating || _isAnalyzing)">
            @if (_isAnalyzing)
            {
                <MudProgressCircular Size="Size.Small" Indeterminate="true" Class="mr-2" />
                <span>Analyzing...</span>
            }
            else
            {
                <span>Analyze Settings</span>
            }
        </MudButton>
        
        @* Existing Generate Button *@
        <MudButton Variant="Variant.Filled" ...>
            Generate Terrain
        </MudButton>
    </MudStack>
</MudStack>

@* Analysis Results Dialog *@
<MudDialog @bind-Visible="_showAnalysisDialog" Options="@_analysisDialogOptions">
    <TitleContent>
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.Analytics" Class="mr-2" />
            Terrain Analysis Results
        </MudText>
    </TitleContent>
    <DialogContent>
        <TerrainAnalysisViewer 
            AnalysisResult="_analysisResult"
            TerrainSize="_terrainSize"
            MetersPerPixel="_metersPerPixel"
            OnJunctionsExcluded="OnJunctionsExcludedFromAnalysis" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="CloseAnalysisDialog">Cancel</MudButton>
        <MudButton Color="Color.Primary" OnClick="ApplyAnalysisAndGenerate">
            Apply & Generate
        </MudButton>
    </DialogActions>
</MudDialog>
```

---

## File Structure Summary

### New Files to Create

| File | Project | Purpose |
|------|---------|---------|
| `TerrainAnalyzer.cs` | BeamNgTerrainPoc | Library service for analysis |
| `TerrainAnalysisOrchestrator.cs` | BeamNG_LevelCleanUp | UI-level orchestrator |
| `TerrainAnalysisState.cs` | BeamNG_LevelCleanUp | Analysis state management |
| `TerrainAnalysisViewer.razor` | BeamNG_LevelCleanUp | Interactive visualization |
| `TerrainAnalysisViewer.razor.cs` | BeamNG_LevelCleanUp | Code-behind |

### Files to Modify

| File | Changes |
|------|---------|
| `NetworkJunction.cs` | Add `IsExcluded` property |
| `NetworkJunctionHarmonizer.cs` | Skip excluded junctions |
| `GenerateTerrain.razor` | Add Analyze button and dialog |
| `GenerateTerrain.razor.cs` | Add analysis state and methods |
| `TerrainGenerationOrchestrator.cs` | Accept pre-analyzed network |

---

## Detailed Component Designs

### TerrainAnalysisViewer Component

The viewer should display:

1. **Background**: Debug image (junction_harmonization_debug.png style)
2. **Splines**: Drawn as colored polylines
   - See OsmFeaturePreview.razor for reference how it can be done with svg/canvas
   - the planned elevation changes should be visible by coloring splines like in debug image
   - have in mind that the functionality can expanded later with spline selection etc.
3. **Junctions**: Clickable markers with:
   - Color coding by type (Cyan=T, Pink=MidSpline, Green=Y, etc.)
   - Checkbox overlay or click-to-toggle exclusion
   - Hover tooltip showing junction details

**Interaction Features**:
- Click junction to toggle exclusion (strikethrough/dimmed)
- Pan: drag background
- Zoom: scroll wheel or buttons
- Legend showing junction types and colors

**Visual Design** (matching existing debug image):
```
Junction Type        | Color          | Shape
---------------------|----------------|--------
Endpoint             | Yellow         | Small circle
T-Junction           | Cyan           | Circle
Y-Junction           | Green          | Circle
CrossRoads           | Orange         | Circle
Complex              | Magenta        | Circle
MidSplineCrossing    | Pink/Coral     | Circle
Cross-Material       | White outline  | Ring
Excluded             | Gray + Strike  | Dimmed
```

### Analysis Flow

```
User clicks "Analyze Settings"
         │
         ▼
┌─────────────────────────────────────────┐
│ TerrainAnalysisOrchestrator.AnalyzeAsync │
│   ├── Build material definitions        │
│   ├── Load heightmap                    │
│   ├── TerrainAnalyzer.AnalyzeAsync()    │
│   │   ├── BuildNetwork()                │
│   │   ├── CalculateElevations()         │
│   │   ├── DetectJunctions()             │
│   │   └── Return UnifiedRoadNetwork     │
│   └── Generate debug image              │
└─────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│ TerrainAnalysisViewer displays results  │
│   ├── Render splines and junctions      │
│   ├── User clicks to exclude junctions  │
│   └── Update ExcludedJunctionIds        │
└─────────────────────────────────────────┘
         │
         ▼
User clicks "Apply & Generate"
         │
         ▼
┌─────────────────────────────────────────┐
│ TerrainGenerationOrchestrator.ExecuteAsync │
│   ├── Use pre-analyzed network          │
│   ├── Apply junction exclusions         │
│   ├── Skip junction detection (reuse)   │
│   └── Continue with harmonization       │
└─────────────────────────────────────────┘
```

---

## Implementation Order

### Phase 1: Library Support (BeamNgTerrainPoc)
1. Add `IsExcluded` property to `NetworkJunction`
2. Modify `NetworkJunctionHarmonizer.HarmonizeNetwork()` to skip excluded junctions
3. Create `TerrainAnalyzer.cs` service

### Phase 2: State Management (BeamNG_LevelCleanUp)
1. Create `TerrainAnalysisState.cs`
2. Create `TerrainAnalysisOrchestrator.cs`

### Phase 3: UI Component (BeamNG_LevelCleanUp)
1. Create `TerrainAnalysisViewer.razor` skeleton
2. Implement debug image rendering (reuse existing image generation)
3. Add junction markers as clickable elements
4. Implement exclusion toggle logic

### Phase 4: Integration
1. Modify `GenerateTerrain.razor` to add Analyze button
2. Add dialog for analysis results
3. Modify `TerrainGenerationOrchestrator` to accept pre-analyzed network
4. Test end-to-end flow

---

## Technical Considerations

### Reusing Existing Debug Image Generation

The `NetworkJunctionHarmonizer.ExportJunctionDebugImage()` method already generates the exact visualization needed. We can:

1. Extract the image generation logic into a separate helper
2. Generate the image to a byte array (instead of file)
3. Display in Blazor using `data:image/png;base64,...` or better make own SVG representation for interactivity
### Performance

- Analysis should be fast (~1-3 seconds) since it stops before terrain blending
- Debug image generation is O(n) where n = cross-sections
- Junction detection is already optimized with spatial index

### State Preservation

When switching between Analyze and Generate:
- Pre-analyzed network is stored in `TerrainAnalysisState`
- Exclusions are stored as `HashSet<int>` of junction IDs
- If materials change, analysis state is invalidated

---

## Alternative Approaches Considered

### Option A: Fullscreen Modal Dialog (Selected)
- **Pros**: Clear separation, focused interaction, doesn't clutter main page
- **Cons**: Extra click to access

### Option B: Inline Expansion Panel
- **Pros**: Always visible
- **Cons**: Makes page longer, complex state management

### Option C: Separate Page
- **Pros**: Full screen real estate
- **Cons**: Navigation overhead, state passing complexity

**Decision**: Option A (Modal Dialog) balances usability with code complexity.

---

## Mockup UI Layout

```
┌────────────────────────────────────────────────────────────────────┐
│  🔍 Terrain Analysis Results                              [X]     │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │                                                              │ │
│  │           [Interactive Debug Image Canvas]                   │ │
│  │                                                              │ │
│  │     ○ T-Junction (cyan)                                      │ │
│  │           \                                                  │ │
│  │            ───○ MidSpline (pink)                            │ │
│  │           /                                                  │ │
│  │     ⊘ Excluded (gray)                                        │ │
│  │                                                              │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌─ Legend ───────────────────────────────────────────────────┐   │
│  │ ● T-Junction (3)  ● Y-Junction (2)  ● MidSpline (1)        │   │
│  │ ● CrossRoads (0)  ● Complex (0)     ⊘ Excluded (1)         │   │
│  └────────────────────────────────────────────────────────────┘   │
│                                                                    │
│  ┌─ Selected Junction ─────────────────────────────────────────┐  │
│  │ Junction #4: T-Junction                                      │  │
│  │ Position: (512.3, 789.1)                                     │  │
│  │ Contributing roads: MainRoad, SideStreet                     │  │
│  │ Harmonized elevation: 45.3m                                  │  │
│  │                                                              │  │
│  │ [☐] Exclude from harmonization                               │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                    │
│  ┌─ Summary ───────────────────────────────────────────────────┐  │
│  │ Detected: 6 junctions, 12 splines                           │  │
│  │ Excluded: 1 junction                                        │  │
│  └─────────────────────────────────────────────────────────────┘  │
│                                                                    │
├────────────────────────────────────────────────────────────────────┤
│                        [Cancel]  [Apply & Generate]               │
└────────────────────────────────────────────────────────────────────┘
```

---

## Testing Plan

1. **Unit Tests** (BeamNgTerrainPoc):
   - `TerrainAnalyzer` returns correct network structure
   - Junction exclusion is properly applied
   - Harmonizer skips excluded junctions

2. **Integration Tests** (BeamNG_LevelCleanUp):
   - Analysis button triggers analysis
   - Exclusions are preserved after dialog close/reopen
   - "Apply & Generate" uses modified network

3. **Manual Testing**:
   - Verify junction markers match debug image
   - Test exclusion toggle updates display
   - Verify excluded junction is skipped in generation
   - Test with various map types (many junctions, few junctions, no junctions)

---

## Notes for Implementation

1. **Keep GenerateTerrain.razor.cs length manageable**: Extract analysis-related code into the orchestrator
2. **Reuse existing code**: Don't duplicate junction detection logic; call existing services
3. **Consider future extensions**: The analysis viewer could later show:
   - Elevation profiles along splines
   - Cut/fill volume preview
   - Material layer preview

---

*Document created: [Current Date]*
*Last updated: [Current Date]*
*Status: Ready for Implementation*
