# Road Smoothing Algorithm Implementation Instructions

## Project Context
**Project:** BeamNgTerrainPoc  
**Target Framework:** .NET 9  
**C# Version:** 13.0  
**Feature Branch:** feature/heightmap_road_smoothing  

## Overview

This document provides step-by-step instructions for implementing an algorithm that modifies terrain heightmaps to create smooth, realistic roads with proper grading and seamless integration into the surrounding terrain.

## Objective

Create an algorithm that:
- ? Flattens terrain directly under roads to ensure smooth driving surfaces
- ? Creates level roads from side-to-side (removes transverse slope)
- ? Follows natural terrain contours longitudinally (maintains up/down slopes within limits)
- ? Has configurable parameters for width, slope constraints, and blending
- ? Supports complex road networks with intersections
- ? Excludes areas like water crossings where smoothing shouldn't apply
- ? Maintains all existing terrain material creation functionality
- ? **Saves the modified heightmap to the output directory for reference and debugging**

## User Story

**As a** level designer,  
**I want** an algorithm that modifies the terrain heightmap under and around roads,  
**So that** the generated roads are smooth, flat, and seamlessly integrated into the surrounding terrain.

### Acceptance Criteria
- The algorithm flattens the terrain directly under the road to ensure a smooth driving surface
- The road width is configurable in meters
- The terrain affected range is configurable, allowing smooth blending from the road edge into the natural terrain
- The maximal slope of the road can be set in degrees, preventing unrealistic steepness
- The maximal slope of the terrain sides can be set in degrees, controlling how sharply the terrain transitions from the road to the environment
- The result is a visually and physically realistic road profile, with gradual transitions and no abrupt height changes

## Technical Analysis

### Approach Selection

After analyzing multiple solutions, we've chosen the **Cross-Sectional Leveling Approach** as the primary method because it:
- Directly addresses side-to-side leveling requirements
- Works natively with raster data (layer maps)
- Handles road networks and intersections naturally
- Is specifically designed for creating driveable road surfaces from layer maps

Optional enhancement with **Signal Processing** techniques (Butterworth Filter, Moving Average) can be applied for longitudinal profile smoothing.

### Key Concepts

1. **Road Centerline Extraction:** Use medial axis transform on road layer
2. **Perpendicular Normals:** Calculate cross-sections perpendicular to road direction
3. **Cross-Sectional Leveling:** Apply consistent elevation across each cross-section
4. **Terrain Blending:** Create smooth embankments and transitions to natural terrain
5. **Exclusion Zones:** Subtract areas (water, bridges) where smoothing shouldn't occur

## Implementation Phases

### Phase 1: Extend MaterialDefinition Model

#### 1.1 Create RoadSmoothingParameters Class

**Location:** `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

**Purpose:** Hold all parameters necessary for heightmap manipulation for road materials.

**Properties:**
```csharp
- RoadWidthMeters (float): Width of the road in meters
- TerrainAffectedRangeMeters (float): Distance from road edge to blend terrain (meters)
- RoadMaxSlopeDegrees (float): Maximum allowed road slope (degrees)
- SideMaxSlopeDegrees (float): Maximum slope for embankments/sides (degrees)
- ExclusionLayerPaths (List<string>): Paths to layer maps for water/bridges to exclude
- CrossSectionIntervalMeters (float): Distance between cross-section samples (default: 2.0)
- LongitudinalSmoothingWindowMeters (float): Window size for longitudinal smoothing (default: 20.0)
- BlendFunctionType (enum): Type of blend function (Linear, Cosine, Cubic)
```

**Recommended Defaults:**
```csharp
- RoadWidthMeters: 8.0 (typical 2-lane road)
- TerrainAffectedRangeMeters: 15.0
- RoadMaxSlopeDegrees: 8.0 (typical highway maximum)
- SideMaxSlopeDegrees: 30.0 (stable embankment)
- CrossSectionIntervalMeters: 2.0
- LongitudinalSmoothingWindowMeters: 20.0
- BlendFunctionType: Cosine
```

#### 1.2 Extend MaterialDefinition Class

**Location:** `BeamNgTerrainPoc/Terrain/Models/MaterialDefinition.cs`

**Changes:**
- Add optional property: `RoadSmoothingParameters? RoadParameters { get; set; }`
- Update constructor to accept optional RoadSmoothingParameters
- Materials with non-null RoadParameters are treated as road materials

**Important:** This is purely additive - no breaking changes to existing functionality!

---

### Phase 2: Road Geometry Extraction

#### 2.1 Create Road Geometry Models

**Location:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/`

**Classes to Create:**

**CrossSection.cs**
```csharp
- CenterPoint (Vector2): World coordinates of center
- NormalDirection (Vector2): Unit vector perpendicular to road
- TangentDirection (Vector2): Unit vector along road
- TargetElevation (float): Calculated elevation for this cross-section
- WidthMeters (float): Road width at this point
- IsExcluded (bool): Whether this section is in an exclusion zone
```

**RoadGeometry.cs**
```csharp
- Centerline (List<Vector2>): Ordered points defining road center
- CrossSections (List<CrossSection): Generated cross-sections
- RoadMask (byte[,]): Binary mask of road pixels
- Parameters (RoadSmoothingParameters): Associated parameters
```

**SmoothingResult.cs**
```csharp
- ModifiedHeightMap (float[,]): The resulting heightmap
- DeltaMap (float[,]): Change values (new - original)
- Statistics (SmoothingStatistics): Max slopes, cuts, fills, etc.
```

#### 2.2 Create Road Extraction Interface and Implementation

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/IRoadExtractor.cs`

**Interface:**
```csharp
public interface IRoadExtractor
{
    RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel);
}
```

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/MedialAxisRoadExtractor.cs`

**Implementation Steps:**
1. Convert road layer to binary mask (threshold at 128)
2. Apply morphological operations to clean noise
3. Perform skeletonization/medial axis transform to find centerline
4. Order centerline pixels into polyline
5. Convert pixel coordinates to world coordinates using metersPerPixel
6. Calculate tangent and normal vectors at each centerline point
7. Generate CrossSection objects at regular intervals (CrossSectionIntervalMeters)
8. Store in RoadGeometry object

**Algorithm for Skeletonization:**
- Use Zhang-Suen thinning algorithm or similar
- Alternative: Use distance transform + ridge detection
- Fallback: Simple erosion-based approach

---

### Phase 3: Exclusion Zone Processing

#### 3.1 Create Exclusion Processor

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/ExclusionZoneProcessor.cs`

**Methods:**
```csharp
public byte[,] CombineExclusionLayers(List<string> layerPaths, int width, int height)
{
    // Load each exclusion layer
    // Combine using OR operation (any white pixel = excluded)
    // Return combined binary mask
}

public byte[,] ApplyExclusionsToRoadMask(byte[,] roadMask, byte[,] exclusionMask)
{
    // Subtract exclusion mask from road mask (AND NOT operation)
    // Return modified road mask for smoothing only
    // Note: Original road layer unchanged for material placement!
}

public void MarkExcludedCrossSections(RoadGeometry geometry, byte[,] exclusionMask)
{
    // For each cross-section, check if center point is in exclusion zone
    // Set IsExcluded flag accordingly
}
```

**Important:** Exclusions only affect road smoothing, NOT material placement!

---

### Phase 4: Height Calculation Algorithm

#### 4.1 Create Height Calculator Interface

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/IHeightCalculator.cs`

**Interface:**
```csharp
public interface IHeightCalculator
{
    void CalculateTargetElevations(
        RoadGeometry geometry, 
        float[,] heightMap,
        float metersPerPixel);
}
```

#### 4.2 Implement Cross-Sectional Height Calculator

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/CrossSectionalHeightCalculator.cs`

**Algorithm:**

For each CrossSection in geometry:
1. **Skip if excluded:** If `IsExcluded == true`, continue to next
2. **Sample current heights:** Sample heightmap along cross-section line
3. **Calculate initial target elevation:**
   - Option A: Use centerline point height directly
   - Option B: Use weighted average (favor center: 70% center, 15% each edge)
4. **Store initial target:** `crossSection.TargetElevation = targetHeight`

After all cross-sections have initial elevations:
5. **Apply longitudinal slope constraints:**
   - For each adjacent pair of cross-sections
   - Calculate slope = (height2 - height1) / distance
   - If abs(slope) > tan(RoadMaxSlopeDegrees), adjust heights
   - Use iterative smoothing to propagate constraints
6. **Ensure continuity:** No abrupt changes between cross-sections

**Slope Constraint Algorithm:**
```csharp
float maxSlopeRatio = tan(RoadMaxSlopeDegrees * PI / 180);
for (int i = 0; i < maxIterations; i++)
{
    bool changed = false;
    for (int j = 1; j < crossSections.Count; j++)
    {
        float distance = Distance(crossSections[j-1].CenterPoint, crossSections[j].CenterPoint);
        float currentSlope = (crossSections[j].TargetElevation - crossSections[j-1].TargetElevation) / distance;
        
        if (abs(currentSlope) > maxSlopeRatio)
        {
            // Adjust elevations to meet constraint
            float targetSlope = sign(currentSlope) * maxSlopeRatio;
            float midpoint = (crossSections[j].TargetElevation + crossSections[j-1].TargetElevation) / 2;
            
            crossSections[j-1].TargetElevation = midpoint - distance * targetSlope / 2;
            crossSections[j].TargetElevation = midpoint + distance * targetSlope / 2;
            changed = true;
        }
    }
    if (!changed) break;
}
```

---

### Phase 5: Longitudinal Profile Smoothing (Optional Enhancement)

#### 5.1 Create Elevation Filter Interface

**Location:** `BeamNgTerrainPoc/Terrain/Filters/IElevationFilter.cs`

**Interface:**
```csharp
public interface IElevationFilter
{
    List<float> SmoothElevations(List<float> elevations, RoadSmoothingParameters parameters);
}
```

#### 5.2 Implement Moving Average Filter

**Location:** `BeamNgTerrainPoc/Terrain/Filters/MovingAverageFilter.cs`

**Formula:**
```
elevationSmoothed[i] = (1 / (2n + 1)) * sum(elevation[i-n] to elevation[i+n])
```

**Window size:** Determined by `LongitudinalSmoothingWindowMeters` parameter

**Handle boundaries:** Use reflection or clamping at start/end of road

#### 5.3 Implement Butterworth Filter (Advanced)

**Location:** `BeamNgTerrainPoc/Terrain/Filters/ButterworthFilter.cs`

**Transfer Function:**
```
H(z) = (a0 + a1*z^-1 + ... + ak*z^-k) / (1 + b1*z^-1 + ... + bk*z^-k)
```

**Recommended Settings:**
- Order: 3
- Cutoff Frequency: 1.40 × 10^-5 (from research)

**Implementation Note:** This is optional and can be added later for higher accuracy.

---

### Phase 6: Terrain Blending

#### 6.1 Create Terrain Blender

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/TerrainBlender.cs`

**Purpose:** Smoothly blend road surface with surrounding terrain.

**Algorithm:**

For each pixel in heightmap:

1. **Calculate distance to nearest road centerline:**
   - Use spatial index for performance (KD-tree or grid)
   - Find nearest cross-section

2. **Determine zone:**
   - If distance <= RoadWidthMeters / 2: **Road Surface Zone**
   - If distance <= RoadWidthMeters / 2 + TerrainAffectedRangeMeters: **Transition Zone**
   - Else: **Natural Terrain Zone**

3. **Calculate blended height:**

   **Road Surface Zone:**
   ```csharp
   newHeight = crossSection.TargetElevation
   ```

   **Transition Zone:**
   ```csharp
   float roadEdgeDistance = distance - (RoadWidthMeters / 2);
   float blendFactor = roadEdgeDistance / TerrainAffectedRangeMeters;
   
   // Apply blend function
   float t = ApplyBlendFunction(blendFactor, BlendFunctionType);
   
   // Check side slope constraint
   float heightDiff = originalHeight - crossSection.TargetElevation;
   float maxAllowedDiff = roadEdgeDistance * tan(SideMaxSlopeDegrees * PI / 180);
   
   if (abs(heightDiff) > maxAllowedDiff)
   {
       // Constrain to max slope (creates embankment or cutting)
       heightDiff = sign(heightDiff) * maxAllowedDiff;
   }
   
   newHeight = crossSection.TargetElevation + heightDiff * t;
   ```

   **Natural Terrain Zone:**
   ```csharp
   newHeight = originalHeight // No change
   ```

#### 6.2 Blend Functions

**Location:** `BeamNgTerrainPoc/Terrain/Algorithms/BlendFunctions.cs`

**Linear:**
```csharp
public static float Linear(float t) => t;
```

**Cosine (Recommended - Smoothest):**
```csharp
public static float Cosine(float t) => (1 - cos(t * PI)) / 2;
```

**Cubic (Hermite):**
```csharp
public static float Cubic(float t) => t * t * (3 - 2 * t);
```

**Quintic (Extra smooth):**
```csharp
public static float Quintic(float t) => t * t * t * (t * (t * 6 - 15) + 10);
```

---

### Phase 7: Main Orchestrator Service

#### 7.1 Create Road Smoothing Service

**Location:** `BeamNgTerrainPoc/Terrain/Services/RoadSmoothingService.cs`

**Dependencies:**
```csharp
- IRoadExtractor _roadExtractor
- IHeightCalculator _heightCalculator
- ExclusionZoneProcessor _exclusionProcessor
- TerrainBlender _terrainBlender
- IElevationFilter? _elevationFilter (optional)
```

**Main Method:**
```csharp
public SmoothingResult SmoothRoadsInHeightmap(
    float[,] heightMap,
    byte[,] roadLayer,
    RoadSmoothingParameters parameters,
    float metersPerPixel)
{
    // 1. Process exclusions
    byte[,] exclusionMask = null;
    if (parameters.ExclusionLayerPaths?.Any() == true)
    {
        exclusionMask = _exclusionProcessor.CombineExclusionLayers(
            parameters.ExclusionLayerPaths, 
            roadLayer.GetLength(0), 
            roadLayer.GetLength(1));
    }
    
    // 2. Extract road geometry
    byte[,] smoothingMask = exclusionMask != null 
        ? _exclusionProcessor.ApplyExclusionsToRoadMask(roadLayer, exclusionMask)
        : roadLayer;
    
    RoadGeometry geometry = _roadExtractor.ExtractRoadGeometry(
        smoothingMask, 
        parameters, 
        metersPerPixel);
    
    if (exclusionMask != null)
    {
        _exclusionProcessor.MarkExcludedCrossSections(geometry, exclusionMask);
    }
    
    // 3. Calculate target elevations
    _heightCalculator.CalculateTargetElevations(geometry, heightMap, metersPerPixel);
    
    // 4. Optional: Apply longitudinal smoothing filter
    if (_elevationFilter != null)
    {
        var elevations = geometry.CrossSections
            .Where(cs => !cs.IsExcluded)
            .Select(cs => cs.TargetElevation)
            .ToList();
        
        var smoothed = _elevationFilter.SmoothElevations(elevations, parameters);
        
        int idx = 0;
        foreach (var cs in geometry.CrossSections.Where(cs => !cs.IsExcluded))
        {
            cs.TargetElevation = smoothed[idx++];
        }
    }
    
    // 5. Blend with terrain
    float[,] newHeightMap = _terrainBlender.BlendRoadWithTerrain(
        heightMap, 
        geometry, 
        parameters, 
        metersPerPixel);
    
    // 6. Create result
    return new SmoothingResult
    {
        ModifiedHeightMap = newHeightMap,
        DeltaMap = CalculateDelta(heightMap, newHeightMap),
        Statistics = CalculateStatistics(heightMap, newHeightMap, geometry)
    };
}
```

---

### Phase 8: Integration with TerrainCreator

#### 8.1 Extend TerrainCreator

**Location:** `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

**Changes:**
1. **Add dependency:**
   ```csharp
   private readonly RoadSmoothingService _roadSmoothingService;
   ```

2. **Add method to process road materials:**
   ```csharp
   public SmoothingResult? ApplyRoadSmoothing(
       float[,] heightMap, 
       List<MaterialDefinition> materials,
       float metersPerPixel)
   {
       float[,] currentHeightMap = heightMap;
       SmoothingResult? finalResult = null;
       
       foreach (var material in materials.Where(m => m.RoadParameters != null))
       {
           // Load road layer
           byte[,] roadLayer = LoadLayerImage(material.LayerImagePath);
           
           // Apply smoothing
           var result = _roadSmoothingService.SmoothRoadsInHeightmap(
               currentHeightMap,
               roadLayer,
               material.RoadParameters,
               metersPerPixel);
           
           currentHeightMap = result.ModifiedHeightMap;
           finalResult = result; // Keep last result for statistics
           
           // Optional: Log statistics
           LogSmoothingStatistics(material.MaterialName, result.Statistics);
       }
       
       return finalResult;
   }
   ```

3. **Add method to save modified heightmap:**
   ```csharp
   private void SaveModifiedHeightmap(
       float[,] modifiedHeights,
       string outputPath,
       float maxHeight,
       int size)
   {
       try
       {
           // Create output path for heightmap (same directory as .ter file)
           var outputDir = Path.GetDirectoryName(outputPath);
           var terrainName = Path.GetFileNameWithoutExtension(outputPath);
           var heightmapOutputPath = Path.Combine(outputDir!, $"{terrainName}_smoothed_heightmap.png");
           
           // Convert float heights back to 16-bit heightmap
           using var heightmapImage = new Image<L16>(size, size);
           
           for (int y = 0; y < size; y++)
           {
               for (int x = 0; x < size; x++)
               {
                   // Convert height (0.0 to maxHeight) to 16-bit value (0 to 65535)
                   float normalizedHeight = modifiedHeights[y * size + x] / maxHeight;
                   ushort pixelValue = (ushort)(normalizedHeight * 65535f);
                   heightmapImage[x, y] = new L16(pixelValue);
               }
           }
           
           heightmapImage.SaveAsPng(heightmapOutputPath);
           Console.WriteLine($"Saved modified heightmap to: {heightmapOutputPath}");
       }
       catch (Exception ex)
       {
           Console.WriteLine($"Warning: Failed to save modified heightmap: {ex.Message}");
           // Don't throw - this is optional output
       }
   }
   ```

4. **Update CreateTerrainFileAsync to integrate road smoothing and save heightmap:**
   ```csharp
   public async Task<bool> CreateTerrainFileAsync(
       string outputPath,
       TerrainCreationParameters parameters)
   {
       // ... existing validation code ...
       
       try
       {
           // ... existing heightmap loading code ...
           
           // 3. Process heightmap
           Console.WriteLine("Processing heightmap...");
           var heights = HeightmapProcessor.ProcessHeightmap(
               heightmapImage,
               parameters.MaxHeight);
           
           // 3a. Apply road smoothing if road materials exist
           SmoothingResult? smoothingResult = null;
           if (parameters.Materials.Any(m => m.RoadParameters != null))
           {
               Console.WriteLine("Applying road smoothing...");
               
               // Calculate meters per pixel
               float metersPerPixel = CalculateMetersPerPixel(parameters);
               
               smoothingResult = ApplyRoadSmoothing(
                   heights, 
                   parameters.Materials, 
                   metersPerPixel);
               
               if (smoothingResult != null)
               {
                   heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
                   Console.WriteLine("Road smoothing completed successfully!");
               }
           }
           
           // 4. Process material layers
           Console.WriteLine("Processing material layers...");
           var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
               parameters.Materials,
               parameters.Size);
           
           // ... existing terrain building code ...
           
           // 7. Save terrain file
           Console.WriteLine($"Writing terrain file to {outputPath}...");
           
           // Ensure output directory exists
           var outputDir = Path.GetDirectoryName(outputPath);
           if (!string.IsNullOrEmpty(outputDir))
           {
               Directory.CreateDirectory(outputDir);
           }
           
           // Save synchronously (the Save method is synchronous)
           await Task.Run(() => terrain.Save(outputPath, parameters.MaxHeight));
           
           // 7a. Save modified heightmap if road smoothing was applied
           if (smoothingResult != null)
           {
               Console.WriteLine("Saving modified heightmap...");
               SaveModifiedHeightmap(
                   smoothingResult.ModifiedHeightMap,
                   outputPath,
                   parameters.MaxHeight,
                   parameters.Size);
           }
           
           Console.WriteLine("Terrain file created successfully!");
           
           // ... existing statistics display code ...
           
           return true;
       }
       catch (Exception ex)
       {
           // ... existing error handling ...
       }
       finally
       {
           // ... existing cleanup ...
       }
   }
   
   private float CalculateMetersPerPixel(TerrainCreationParameters parameters)
   {
       // This should be configurable, but for now use a reasonable default
       // BeamNG default: 1024 size terrain = 2048 meters (2m per pixel)
       // Can be added to TerrainCreationParameters later
       return 2.0f;
   }
   
   private float[] ConvertTo1DArray(float[,] array2D)
   {
       int size = (int)Math.Sqrt(array2D.Length);
       float[] result = new float[array2D.Length];
       
       for (int y = 0; y < size; y++)
       {
           for (int x = 0; x < size; x++)
           {
               result[y * size + x] = array2D[y, x];
           }
       }
       
       return result;
   }
   ```

**Important:** Road smoothing should occur AFTER heightmap processing but BEFORE material texture placement. The material layers themselves are used for BOTH smoothing AND texture placement!

**Output Files:**
- Original: `{outputPath}.ter` - The terrain file
- **New**: `{outputPath}_smoothed_heightmap.png` - The modified heightmap (16-bit PNG) for reference and debugging

---

### Phase 9: Extend TerrainCreationParameters (Optional Enhancement)

#### 9.1 Add MetersPerPixel Configuration

**Location:** `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

**Add property:**
```csharp
/// <summary>
/// Meters per pixel for the terrain (used for road smoothing calculations).
/// Default is 2.0 (typical for BeamNG: 1024 terrain = 2048 meters).
/// </summary>
public float MetersPerPixel { get; set; } = 2.0f;
```

**Update TerrainCreator to use this:**
```csharp
private float CalculateMetersPerPixel(TerrainCreationParameters parameters)
{
    return parameters.MetersPerPixel;
}
```

---

## Implementation Order

### Milestone 1: Foundation (Week 1)
1. ? Create `RoadSmoothingParameters` model
2. ? Extend `MaterialDefinition` with `RoadParameters` property
3. ? Create geometry models (`CrossSection`, `RoadGeometry`, `SmoothingResult`)
4. ? Write unit tests for models

### Milestone 2: Road Extraction (Week 2)
5. ? Implement `IRoadExtractor` interface
6. ? Implement basic `MedialAxisRoadExtractor`
7. ? Test centerline extraction on simple straight road
8. ? Test centerline extraction on curved road
9. ? Add cross-section generation

### Milestone 3: Exclusion Zones (Week 3)
10. ? Implement `ExclusionZoneProcessor`
11. ? Test exclusion layer combination
12. ? Test marking excluded cross-sections

### Milestone 4: Height Calculation (Week 4)
13. ? Implement `CrossSectionalHeightCalculator`
14. ? Add slope constraint algorithm
15. ? Test on flat terrain
16. ? Test on sloped terrain
17. ? Test slope limit enforcement

### Milestone 5: Terrain Blending (Week 5)
18. ? Implement blend functions
19. ? Implement `TerrainBlender`
20. ? Test blending on simple cases
21. ? Test side slope constraints
22. ? Verify smooth transitions

### Milestone 6: Integration (Week 6)
23. ? Implement `RoadSmoothingService`
24. ? Integrate with `TerrainCreator`
25. ? **Add SaveModifiedHeightmap method**
26. ? **Wire up heightmap saving in CreateTerrainFileAsync**
27. ? End-to-end testing
28. ? Performance optimization

### Milestone 7: Enhancement (Week 7 - Optional)
29. ? Implement `MovingAverageFilter`
30. ? Implement `ButterworthFilter`
31. ? Comparative testing

### Milestone 8: Polish (Week 8)
32. ? Documentation
33. ? Example configurations
34. ? Visual debugging tools
35. ? Performance profiling

---

## Testing Strategy

### Unit Tests

**RoadSmoothingParametersTests.cs**
- Validate default values
- Validate property constraints (e.g., positive values)

**MedialAxisRoadExtractorTests.cs**
- Test straight road extraction
- Test curved road extraction
- Test road network with intersection
- Test L-shaped road
- Test T-intersection

**CrossSectionalHeightCalculatorTests.cs**
- Test elevation calculation on flat terrain
- Test slope constraint on steep terrain
- Test continuity between cross-sections

**TerrainBlenderTests.cs**
- Test blend functions output ranges [0,1]
- Test smooth transition zone
- Test side slope constraint
- Test embankment creation (road above terrain)
- Test cutting creation (road below terrain)

**ExclusionZoneProcessorTests.cs**
- Test single exclusion layer
- Test multiple exclusion layers
- Test exclusion applied to road mask

### Integration Tests

**RoadSmoothingServiceIntegrationTests.cs**

Test scenarios:
1. **Simple Straight Road on Slope**
   - Input: 512x512 heightmap with constant slope, straight road layer
   - Expected: Road is level side-to-side, follows slope longitudinally
   - Validate: Max transverse slope ~0°, longitudinal slope within limits

2. **Curved Road with Varying Terrain**
   - Input: Heightmap with hills/valleys, curved road layer
   - Expected: Smooth road following curve, proper banking
   - Validate: No discontinuities, smooth curvature

3. **Road Network with Intersection**
   - Input: Two roads crossing at 90°
   - Expected: Both roads smoothed, proper blending at intersection
   - Validate: Elevation continuity at intersection

4. **Road with Water Crossing (Exclusion)**
   - Input: Road layer crossing water, water exclusion layer
   - Expected: Road smoothed except where it crosses water
   - Validate: No modification in exclusion zone

5. **Road on Steep Terrain (Slope Limit Test)**
   - Input: Very steep terrain (>30° slope), road layer
   - Expected: Road slope constrained to RoadMaxSlopeDegrees
   - Validate: No segment exceeds max slope

### Validation Metrics

For each test result, calculate:

```csharp
public class SmoothingStatistics
{
    public float MaxRoadSlope { get; set; }          // Should be <= RoadMaxSlopeDegrees
    public float MaxSideSlope { get; set; }          // Should be <= SideMaxSlopeDegrees
    public float MaxTransverseSlope { get; set; }    // Should be ~0°
    public float MaxDiscontinuity { get; set; }      // Max height jump between adjacent pixels
    public float TotalCutVolume { get; set; }        // Cubic meters removed
    public float TotalFillVolume { get; set; }       // Cubic meters added
    public int PixelsModified { get; set; }          // Count of changed pixels
    public bool MeetsAllConstraints { get; set; }    // All metrics within limits
}
```

**Output Files for Validation:**
- `{terrainName}_smoothed_heightmap.png` - Modified heightmap (can be compared to original)
- Optional delta maps and debug visualizations (see Debugging section)

---

## Performance Considerations

### Expected Performance Targets

- **Heightmap Size:** 2048x2048 pixels
- **Road Network:** Up to 10 road materials with complex networks
- **Processing Time Target:** < 30 seconds total
- **Memory Usage:** < 2 GB peak

### Optimization Strategies

1. **Spatial Indexing:**
   - Use KD-tree or grid-based spatial index for nearest centerline queries
   - Pre-compute road influence areas

2. **Parallel Processing:**
   - Process independent road materials in parallel
   - Use `Parallel.For` for pixel-level operations

3. **Caching:**
   - Cache computed blend factors
   - Cache distance calculations

4. **Early Exit:**
   - Skip pixels outside `TerrainAffectedRange` quickly
   - Use bounding boxes for each road segment

5. **Memory Efficiency:**
   - Use `Span<T>` for array operations
   - Avoid unnecessary allocations

**Example Parallel Processing:**
```csharp
Parallel.For(0, height, y =>
{
    for (int x = 0; x < width; x++)
    {
        // Process pixel (x, y)
    }
});
```

---

## Configuration Examples

### Example 1: Highway

```csharp
new MaterialDefinition(
    "asphalt_highway", 
    "layers/highway_network.png")
{
    RoadParameters = new RoadSmoothingParameters
    {
        RoadWidthMeters = 12.0f,              // 3-lane highway
        TerrainAffectedRangeMeters = 25.0f,   // Wide smooth transition
        RoadMaxSlopeDegrees = 6.0f,           // Gentle highway grade
        SideMaxSlopeDegrees = 25.0f,          // Moderate embankments
        ExclusionLayerPaths = new List<string> { "layers/water.png", "layers/bridges.png" },
        BlendFunctionType = BlendFunctionType.Cosine
    }
}
```

### Example 2: Dirt Road

```csharp
new MaterialDefinition(
    "dirt_road", 
    "layers/dirt_roads.png")
{
    RoadParameters = new RoadSmoothingParameters
    {
        RoadWidthMeters = 5.0f,               // Narrow single-track
        TerrainAffectedRangeMeters = 8.0f,    // Tight transition
        RoadMaxSlopeDegrees = 15.0f,          // Can be steeper
        SideMaxSlopeDegrees = 35.0f,          // Steeper banks OK
        BlendFunctionType = BlendFunctionType.Linear  // Less smooth (more natural)
    }
}
```

### Example 3: Mountain Pass

```csharp
new MaterialDefinition(
    "mountain_road", 
    "layers/mountain_pass.png")
{
    RoadParameters = new RoadSmoothingParameters
    {
        RoadWidthMeters = 6.0f,
        TerrainAffectedRangeMeters = 10.0f,
        RoadMaxSlopeDegrees = 12.0f,          // Steeper for mountain context
        SideMaxSlopeDegrees = 40.0f,          // Steep dropoffs allowed
        ExclusionLayerPaths = new List<string> { "layers/cliffs.png" },
        LongitudinalSmoothingWindowMeters = 30.0f,  // More smoothing for safety
        BlendFunctionType = BlendFunctionType.Cubic
    }
}
```

---

## Debugging and Visualization Tools

### Suggested Debug Outputs

1. **Centerline Visualization:**
   - Export centerline as vector path overlay on heightmap
   - Verify extraction accuracy

2. **Cross-Section Visualization:**
   - Draw perpendicular lines at each cross-section
   - Color-code by slope value

3. **Elevation Profile:**
   - Plot elevation along centerline before/after smoothing
   - Show slope values between points

4. **Delta Map Export:**
   - Export cut/fill map as colored image
   - Red = cut (terrain lowered), Blue = fill (terrain raised)

5. **Slope Heatmap:**
   - Visualize road slopes across entire network
   - Highlight areas exceeding constraints

6. **Modified Heightmap (AUTOMATIC):**
   - Saved automatically as `{terrainName}_smoothed_heightmap.png`
   - 16-bit grayscale PNG
   - Same format as input heightmap
   - Can be diff'd against original heightmap

### Debug Configuration

```csharp
public class RoadSmoothingDebugOptions
{
    public bool ExportCenterline { get; set; }
    public bool ExportCrossSections { get; set; }
    public bool ExportElevationProfile { get; set; }
    public bool ExportDeltaMap { get; set; }
    public bool ExportSlopeHeatmap { get; set; }
    public bool SaveModifiedHeightmap { get; set; } = true; // Default: always save
    public string DebugOutputPath { get; set; }
}
```

---

## API Usage Examples

### Basic Usage

```csharp
// 1. Define road material with smoothing parameters
var roadMaterial = new MaterialDefinition(
    "asphalt_road",
    "textures/layers/road_network.png")
{
    RoadParameters = new RoadSmoothingParameters
    {
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 15.0f,
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 30.0f,
        ExclusionLayerPaths = new List<string> 
        { 
            "textures/layers/water.png" 
        }
    }
};

// 2. Create terrain with road smoothing
var parameters = new TerrainCreationParameters
{
    Size = 2048,
    MaxHeight = 500.0f,
    HeightmapPath = "heightmaps/terrain_2048.png",
    MetersPerPixel = 2.0f, // Optional: configure world scale
    Materials = new List<MaterialDefinition>
    {
        roadMaterial,
        new MaterialDefinition("grass", "textures/layers/grass.png"),
        new MaterialDefinition("rock", "textures/layers/rock.png")
    }
};

var terrainCreator = new TerrainCreator();
var success = terrainCreator.CreateTerrainFile(
    "output/myTerrain.ter",
    parameters);

// Output files created:
// - output/myTerrain.ter (terrain file)
// - output/myTerrain_smoothed_heightmap.png (modified heightmap)
```

### Advanced Usage - Comparing Before/After

```csharp
// Load original heightmap
using var originalHeightmap = Image.Load<L16>("heightmaps/terrain.png");

// Create terrain with road smoothing
var terrainCreator = new TerrainCreator();
terrainCreator.CreateTerrainFile("output/terrain.ter", parameters);

// Load modified heightmap (automatically saved)
using var modifiedHeightmap = Image.Load<L16>("output/terrain_smoothed_heightmap.png");

// Compare pixel differences
for (int y = 0; y < originalHeightmap.Height; y++)
{
    for (int x = 0; x < originalHeightmap.Width; x++)
    {
        var originalValue = originalHeightmap[x, y].PackedValue;
        var modifiedValue = modifiedHeightmap[x, y].PackedValue;
        var diff = Math.Abs(originalValue - modifiedValue);
        
        if (diff > 100) // Significant change
        {
            Console.WriteLine($"Pixel ({x}, {y}): {originalValue} -> {modifiedValue} (?{diff})");
        }
    }
}
```

---

## Common Pitfalls and Solutions

### Pitfall 1: Intersections Create Conflicts
**Problem:** Two roads at an intersection may try to set different elevations for the same pixel.

**Solution:** Process roads in priority order (wider/higher priority roads first). Later roads blend into earlier roads in overlap areas.

### Pitfall 2: Exclusion Zones Not Working
**Problem:** Road smoothing still occurs over water despite exclusion layer.

**Solution:** Verify exclusion layer format (white = exclude), check layer is properly loaded, ensure subtraction logic is correct.

### Pitfall 3: Road Looks "Faceted" or Stepped
**Problem:** Visible discontinuities or terracing along road.

**Solution:** 
- Reduce `CrossSectionIntervalMeters` (more samples)
- Use smoother blend function (Cosine or Quintic)
- Increase `LongitudinalSmoothingWindowMeters`

### Pitfall 4: Embankments Too Steep or Too Gentle
**Problem:** Unrealistic transition from road to terrain.

**Solution:** Adjust `SideMaxSlopeDegrees` and `TerrainAffectedRangeMeters` to achieve desired profile.

### Pitfall 5: Performance Issues on Large Heightmaps
**Problem:** Processing takes too long.

**Solution:**
- Implement spatial indexing (KD-tree)
- Use parallel processing
- Pre-compute road influence bounding boxes
- Process only pixels within `TerrainAffectedRange` of any road

---

## Extension Points for Future Enhancements

### 1. Super-Elevation (Banking) on Curves
Add curvature detection and tilt cross-sections for realistic racing circuits.

### 2. Multi-Lane Roads with Different Slopes
Support separate smoothing parameters for each lane (e.g., climbing lanes).

### 3. Tunnels and Bridges
Add "bridge mode" where road is elevated above terrain with supporting structure.

### 4. Drainage and Camber
Add subtle cross-sectional crown for water runoff.

### 5. Material-Aware Smoothing
Different smoothing strategies for asphalt vs. gravel vs. cobblestone.

### 6. AI-Driven Road Placement
Automatically generate optimal road paths between points considering terrain.

---

## Success Criteria

The implementation is considered successful when:

? A straight road on sloped terrain is level side-to-side  
? Road slopes respect `RoadMaxSlopeDegrees` constraint  
? Embankments respect `SideMaxSlopeDegrees` constraint  
? Transitions from road to terrain are smooth (no visible seams)  
? Road networks with intersections process correctly  
? Exclusion zones (water, bridges) prevent smoothing  
? Existing terrain material placement functionality unchanged  
? **Modified heightmap is saved to output directory**  
? **Saved heightmap can be loaded and reused**  
? Performance is acceptable (<30s for 2048x2048 with complex road network)  
? Visual results look realistic and driveable in BeamNG  

---

## References and Resources

### Algorithms
- Zhang-Suen thinning algorithm for skeletonization
- Medial axis transform for centerline extraction
- Butterworth filter for signal smoothing

### Libraries (Potential Dependencies)
- **ImageSharp**: For image loading/processing (if not already used)
- **MathNet.Numerics**: For advanced filtering (Butterworth)
- **System.Numerics**: For Vector2/Vector3 operations

### Research Papers
- "Butterworth Filter for Highway Elevation Smoothing" (referenced in your input)
- Morphological image processing techniques

---

## Project Structure Summary

```
BeamNgTerrainPoc/
??? Terrain/
    ??? Models/
    ?   ??? MaterialDefinition.cs (MODIFIED - add RoadParameters)
    ?   ??? RoadSmoothingParameters.cs (NEW)
    ?   ??? RoadGeometry/
    ?       ??? CrossSection.cs (NEW)
    ?       ??? RoadGeometry.cs (NEW)
    ?       ??? SmoothingResult.cs (NEW)
    ??? Algorithms/
    ?   ??? IRoadExtractor.cs (NEW)
    ?   ??? MedialAxisRoadExtractor.cs (NEW)
    ?   ??? IHeightCalculator.cs (NEW)
    ?   ??? CrossSectionalHeightCalculator.cs (NEW)
    ?   ??? ExclusionZoneProcessor.cs (NEW)
    ?   ??? TerrainBlender.cs (NEW)
    ?   ??? BlendFunctions.cs (NEW)
    ??? Filters/
    ?   ??? IElevationFilter.cs (NEW)
    ?   ??? MovingAverageFilter.cs (NEW)
    ?   ??? ButterworthFilter.cs (NEW - Optional)
    ??? Services/
    ?   ??? RoadSmoothingService.cs (NEW)
    ??? TerrainCreator.cs (MODIFIED - integrate road smoothing)
    ??? Tests/
        ??? Unit/
        ?   ??? RoadSmoothingParametersTests.cs
        ?   ??? MedialAxisRoadExtractorTests.cs
        ?   ??? CrossSectionalHeightCalculatorTests.cs
        ?   ??? TerrainBlenderTests.cs
        ??? Integration/
            ??? RoadSmoothingServiceIntegrationTests.cs
```

---

## Getting Started

### Step 1: Review This Document
Ensure you understand the overall architecture and goals.

### Step 2: Set Up Development Environment
- Ensure .NET 9 SDK is installed
- Open solution in Visual Studio or Rider
- Check out feature branch: `feature/heightmap_road_smoothing`

### Step 3: Begin Implementation
Start with **Milestone 1** (Foundation):
1. Create `RoadSmoothingParameters.cs`
2. Modify `MaterialDefinition.cs`
3. Create geometry model classes

### Step 4: Iterative Development
- Implement one milestone at a time
- Write tests for each component
- Validate with simple test cases before moving to complex scenarios

### Step 5: Integration
- Integrate with `TerrainCreator`
- Test with real BeamNG terrain data
- Optimize performance

---

## Questions and Support

If you encounter issues or have questions during implementation:

1. **Check this document** for guidance on the specific component
2. **Review test cases** to understand expected behavior
3. **Consult referenced algorithms** (Zhang-Suen, medial axis, etc.)
4. **Profile performance** if processing is slow
5. **Visualize intermediate results** using debug outputs

---

## Document Version

**Version:** 1.0  
**Created:** 2024  
**Last Updated:** 2024  
**Status:** Ready for Implementation  

---

**End of Implementation Instructions**
