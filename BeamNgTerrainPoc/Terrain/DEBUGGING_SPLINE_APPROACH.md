# Debugging Spline-Based Road Smoothing

## Issue Report
- **Status:** Spline detects 460m road but smoothing "almost does nothing"
- **Symptoms:** Incorrect changes around road, not on the road itself
- **Test Case:** Single road line (no intersections)

## Debug Output Added

The code now includes extensive debug output to diagnose the issue:

### 1. Centerline Extraction
```
Extracting sparse centerline for spline fitting...
Found X centerline candidates
Ordered path: Y points
Simplified to Z points for spline
```

### 2. Spline Creation
```
Centerline: Z points
Spline created: 460.0 meters total length
Sampled N points along spline
Generated N cross-sections
```

### 3. Elevation Calculation
```
Calculating target elevations for N cross-sections...
  Warning: X cross-sections used fallback (no perpendicular samples)
Target elevations calculated:
  Average: XX.XXm, Min: XX.XXm, Max: XX.XXm
  First section: XX.XXm at (X, Y)
  Last section:  XX.XXm at (X, Y)
```

### 4. Terrain Blending
```
Cross-sections with non-zero elevation: N/N
Processing 4096x4096 heightmap with 128x128 grid...
Max affected distance: X.X meters (Y pixels)
  Progress: 50.0% (X,XXX,XXX pixels modified) - ETA: 00:30
Blended X,XXX,XXX pixels in 45.2s
```

## How to Test

### Enable Spline Approach

Edit `Program.cs`:

```csharp
roadParameters = new RoadSmoothingParameters
{
    // ENABLE SPLINE APPROACH
    Approach = RoadSmoothingApproach.SplineBased,
    
    // Required for spline
    CrossSectionIntervalMeters = 2.0f,
    
    // Road geometry
    RoadWidthMeters = 6.0f,
    RoadMaxSlopeDegrees = 14.0f,
    TerrainAffectedRangeMeters = 3.0f,
    
    // Blending
    BlendFunctionType = BlendFunctionType.Cosine,
    SideMaxSlopeDegrees = 45.0f
};
```

### Run and Check Console Output

```sh
dotnet run --project BeamNgTerrainPoc -- complex
```

## Expected vs Actual Behavior

### ? Expected Console Output (Working):
```
Extracting sparse centerline for spline fitting...
Found 234 centerline candidates
Ordered path: 234 points
Simplified to 45 points for spline
Centerline: 45 points
Spline created: 460.0 meters total length
Sampled 230 points along spline
Generated 230 cross-sections
Calculating target elevations for 230 cross-sections...
Target elevations calculated:
  Average: 125.50m, Min: 120.00m, Max: 130.00m
  First section: 120.50m at (100.0, 200.0)
  Last section:  129.80m at (550.0, 350.0)
Cross-sections with non-zero elevation: 230/230
Processing 4096x4096 heightmap...
Max affected distance: 6.0 meters (6 pixels)
Blended 15,500 pixels in 38.5s
```

### ? Possible Issue Patterns:

#### Pattern 1: No Cross-Sections Generated
```
Simplified to 1 points for spline  ? TOO FEW!
Warning: Insufficient centerline points for spline
Generated 0 cross-sections  ? PROBLEM!
```
**Diagnosis:** Centerline extraction failed
**Fix:** Road mask might be too sparse or disconnected

#### Pattern 2: All Elevations Zero
```
Target elevations calculated:
  Average: 0.00m, Min: 0.00m, Max: 0.00m  ? PROBLEM!
Cross-sections with non-zero elevation: 0/230  ? PROBLEM!
```
**Diagnosis:** Height sampling failed
**Fix:** Cross-sections outside heightmap bounds OR heightmap all zeros

#### Pattern 3: No Pixels Modified
```
Blended 0 pixels in 2.1s  ? PROBLEM!
```
**Diagnosis:** Cross-sections too far from any pixels
**Fix:** Coordinate system mismatch (world vs pixel coordinates)

#### Pattern 4: Very Few Pixels Modified
```
Blended 150 pixels in 35.2s  ? TOO FEW for 460m road!
```
**Diagnosis:** Max affected distance too small OR cross-sections sparse
**Fix:** Increase `TerrainAffectedRangeMeters` or reduce `CrossSectionIntervalMeters`

## Common Issues & Fixes

### Issue 1: "Almost does nothing"
**Symptoms:** Few pixels modified, changes "around" road not "on" road

**Possible Causes:**
1. **Cross-sections outside road mask**
   - Centerline extraction places points off-road
   - Fix: Check if road mask is clean (no noise)

2. **Elevation sampling fails**
   - Cross-sections sample outside heightmap
   - Fix: Check world coordinate ? pixel coordinate conversion

3. **Max affected distance too small**
   - Road width 6m + range 3m = only 9m total
   - With 1m/pixel, only ~9 pixels affected
   - Fix: Increase `TerrainAffectedRangeMeters`

### Issue 2: "Incorrect changes around road"
**Symptoms:** Modifications happen but in wrong places

**Possible Causes:**
1. **Spatial index mismatch**
   - Grid cells indexed incorrectly
   - Fix: Check `metersPerPixel` is consistent

2. **Y-axis flip**
   - Heightmap Y-coordinates inverted
   - Already fixed in previous sessions, but verify

3. **Cross-section positions wrong**
   - Spline samples at wrong locations
   - Fix: Check control points are in correct coordinate system

## Diagnostic Steps

### Step 1: Verify Console Output

Run the program and check:
- [ ] How many cross-sections generated?
- [ ] What are min/max/average elevations?
- [ ] How many pixels modified?
- [ ] Any warnings about fallback sampling?

### Step 2: Check Coordinates

Add this to see first few cross-sections:
```csharp
// In MedialAxisRoadExtractor.cs after generating cross-sections
for (int i = 0; i < Math.Min(5, geometry.CrossSections.Count); i++)
{
    var cs = geometry.CrossSections[i];
    Console.WriteLine($"  CS[{i}]: Center({cs.CenterPoint.X:F1}, {cs.CenterPoint.Y:F1}), " +
                     $"Tangent({cs.TangentDirection.X:F2}, {cs.TangentDirection.Y:F2}), " +
                     $"Normal({cs.NormalDirection.X:F2}, {cs.NormalDirection.Y:F2})");
}
```

### Step 3: Verify Heightmap Values

Check if heightmap has valid data:
```csharp
// In RoadSmoothingService before smoothing
float minH = float.MaxValue, maxH = float.MinValue;
for (int y = 0; y < heightMap.GetLength(0); y++)
for (int x = 0; x < heightMap.GetLength(1); x++)
{
    minH = Math.Min(minH, heightMap[y, x]);
    maxH = Math.Max(maxH, heightMap[y, x]);
}
Console.WriteLine($"Heightmap range: {minH:F2}m to {maxH:F2}m");
```

### Step 4: Check Road Mask

Verify road mask has pixels:
```csharp
int roadPixels = 0;
for (int y = 0; y < roadMask.GetLength(0); y++)
for (int x = 0; x < roadMask.GetLength(1); x++)
    if (roadMask[y, x] > 128) roadPixels++;
Console.WriteLine($"Road mask has {roadPixels} pixels");
```

## Quick Fixes to Try

### Fix 1: Increase Affected Range
```csharp
TerrainAffectedRangeMeters = 10.0f,  // Was 3.0f
```

### Fix 2: Reduce Cross-Section Interval
```csharp
CrossSectionIntervalMeters = 1.0f,  // Was 2.0f ? More cross-sections
```

### Fix 3: Increase Road Width
```csharp
RoadWidthMeters = 10.0f,  // Was 6.0f ? Wider sampling
```

### Fix 4: Try Different Blend Function
```csharp
BlendFunctionType = BlendFunctionType.Linear,  // Was Cosine
```

## Next Steps

1. **Run with debug output** and share the console log
2. **Check the values** from debug output above
3. **Try Quick Fixes** one at a time
4. **If still failing**, we'll add more detailed logging or try DirectMask approach

---

**Status:** Debugging in progress  
**Test Case:** Single 460m road line  
**Expected Result:** Smooth, level road along entire 460m length
