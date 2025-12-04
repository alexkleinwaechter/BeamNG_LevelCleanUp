# Making Upscale Factor Configurable (Optional Enhancement)

## Current Situation

The upscale factor is currently hardcoded:
```csharp
private const int DefaultUpscaleFactor = 1; // Fixed at 1x (no upsampling)
```

## How to Make It Configurable

### Option 1: Quick Change (Edit Constant)

**For testing at 1x (fast):**
```csharp
private const int DefaultUpscaleFactor = 1;
```

**For final quality at 4x:**
```csharp
private const int DefaultUpscaleFactor = 4;
```

### Option 2: Add Parameter (Better!)

If you want runtime control, add to `RoadSmoothingParameters`:

#### Step 1: Add to SplineRoadParameters
```csharp
public class SplineRoadParameters
{
    /// <summary>
    /// Upscale factor for virtual heightfield (ImprovedSpline only).
    /// 1 = no upsampling (fast, good quality)
    /// 2 = 2x upsampling (4x more pixels)
    /// 4 = 4x upsampling (16x more pixels, best quality)
    /// 8 = 8x upsampling (64x more pixels, ultra quality)
    /// Default: 1 (no upsampling)
    /// </summary>
    public int VirtualHeightfieldUpscaleFactor { get; set; } = 1;
    
    // ... rest of parameters
}
```

#### Step 2: Use in ImprovedSplineTerrainBlender
```csharp
public float[,] BlendRoadWithTerrain(
    float[,] originalHeightMap,
    RoadGeometry geometry,
    RoadSmoothingParameters parameters,
    float metersPerPixel)
{
    // ...
    
    // Get upscale factor from parameters
    int upscaleFactor = parameters.GetSplineParameters().VirtualHeightfieldUpscaleFactor;
    
    // Clamp to valid range
    upscaleFactor = Math.Clamp(upscaleFactor, 1, 8);
    
    // ... rest of method
}
```

#### Step 3: Use in Program.cs
```csharp
roadParameters = new RoadSmoothingParameters
{
    Approach = RoadSmoothingApproach.ImprovedSpline,
    
    SplineParameters = new SplineRoadParameters
    {
        // 1 = fast testing (no quality loss with bounding box optimization!)
        // 4 = final export (ultra smooth)
        VirtualHeightfieldUpscaleFactor = 1, // or 4 for final
        
        // ... other parameters
    }
};
```

## Recommended Settings

### For Development/Testing
```csharp
VirtualHeightfieldUpscaleFactor = 1  // Fast iterations
```
- **Speed**: Very fast (~2-3s for 4096×4096)
- **Quality**: Good (bounding box optimization maintains quality!)
- **Use case**: Testing, iteration, previews

### For Final Export
```csharp
VirtualHeightfieldUpscaleFactor = 4  // Best quality
```
- **Speed**: Slower (~8-12s for 4096×4096 highway)
- **Quality**: Excellent (ultra-smooth transitions)
- **Use case**: Final terrain export for distribution

### For Ultra Quality (Rarely Needed)
```csharp
VirtualHeightfieldUpscaleFactor = 8  // Overkill
```
- **Speed**: Very slow (~30-60s for 4096×4096)
- **Quality**: Marginal improvement over 4x
- **Use case**: Only if you see artifacts at 4x

## Performance Comparison

| Factor | Pixels Processed | Speed | Quality |
|--------|-----------------|-------|---------|
| **1x** | 16.7M | **100%** (baseline) | ???? Good |
| **2x** | 66.8M | ~30% | ????? Excellent |
| **4x** | 267M | ~10% | ????? Excellent+ |
| **8x** | 1.07B | ~3% | ????? Overkill |

**Note**: With bounding box optimization, actual pixels processed is much lower!

## When to Use What?

### Use 1x (No Upsampling) When:
- ? Testing parameters
- ? Previewing results
- ? Iterating on design
- ? Road is already quite smooth
- ? Time is limited
- ? **Actually produces great results now!**

### Use 4x (Default Upsampling) When:
- ? Final export for players
- ? Racing circuits (need ultra-smooth)
- ? Visible jagged edges at 1x
- ? Professional quality needed

### Use 8x (Overkill) When:
- ?? You have patience
- ?? 4x still shows artifacts (very rare)
- ?? Marketing screenshots

## Current Recommendation

**Just use 1x for everything!** 

With the bounding box optimization, the quality difference between 1x and 4x is minimal for most roads, but the speed difference is huge!

Only use 4x if you:
1. Actually see jagged edges in BeamNG
2. Are making final export
3. Have time to wait

## Example Workflow

### Development Phase
```csharp
// Fast iterations
VirtualHeightfieldUpscaleFactor = 1
```
- Change parameters
- Test in BeamNG
- Repeat quickly

### Final Polish Phase
```csharp
// Try 4x for comparison
VirtualHeightfieldUpscaleFactor = 4
```
- Compare with 1x version in BeamNG
- Only use 4x if visibly better
- Otherwise stick with 1x!

## Summary

**Current State**: 
- Hardcoded to 1x (no upsampling)
- Already produces good results
- Bounding box makes it fast

**Recommendation**:
- Keep it at 1x
- Only make configurable if you need runtime control
- Add parameter if you want users to choose quality/speed tradeoff

**Reality Check**:
Most roads will look fine at 1x with the new optimizations. Only complex curved roads with steep terrain might benefit from 4x upsampling, and the difference is often subtle.
