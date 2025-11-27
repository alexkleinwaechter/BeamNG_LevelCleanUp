# Road Smoothing Analysis & Improvement Plan

## Current Issues

### Problems with Spline-Based Approach
1. **Jagged/Hard Blending** - Roads don't blend smoothly into terrain
2. **Stairs/Steps** - Visible discontinuities along road length
3. **No Upsampling** - Working directly at heightmap resolution causes aliasing
4. **Complex Parameter Space** - Too many parameters from trial-and-error attempts

### What's Working Well
- ? Spline recognition (MedialAxisRoadExtractor)
- ? Binary terrain generation
- ? DirectMask approach (acceptable output)
- ? Debug image export system

## Root Cause Analysis

The core problems stem from **raster vs vector mismatch**:

1. **No Internal Upscaling**: Processing at heightmap resolution (1m/pixel) creates blocky results
2. **Inadequate Anti-Aliasing**: No downsampling after modification
3. **Point Sampling**: Direct pixel manipulation without sub-pixel precision
4. **Missing SDF**: No proper signed distance field for smooth distance calculations
5. **Insufficient Smoothing**: Even with Butterworth filter, still getting stairs

## Proposed Architecture Changes

### 1. Virtual Heightfield with Upsampling/Downsampling

```
Original Heightmap (4096x4096 @ 1m/px)
    ? BICUBIC UPSAMPLE (4x)
Virtual Buffer (16384x16384 @ 0.25m/px)
    ? PROCESS ROAD SMOOTHING
Modified Virtual Buffer
    ? GAUSSIAN DOWNSAMPLE + ANTI-ALIAS
Final Heightmap (4096x4096 @ 1m/px)
```

**Benefits**:
- Sub-pixel precision for smooth blending
- Natural anti-aliasing from downsampling
- Eliminates blocky artifacts
- Smoother transitions

### 2. Signed Distance Field (SDF) for Road Geometry

Instead of nearest cross-section lookup, calculate true perpendicular distance:

```csharp
For each pixel (x,z):
    1. Find nearest point on spline ? (t, splinePoint)
    2. Calculate perpendicular distance d
    3. Get spline elevation at t
    4. Determine zone:
       - d < roadWidth/2: ROAD (force to spline height)
       - roadWidth/2 < d < blendDist: SHOULDER (blend)
       - d > blendDist: TERRAIN (unchanged)
```

### 3. Improved Blending with Better Math

Current: Linear ? SmoothStep blend
Improved: S-Curve with Cosine ? **Hermite Spline** blend

```csharp
// Current (jagged transitions)
t = (dist - roadHalfWidth) / blendWidth;
blendFactor = smoothstep(t);

// Proposed (smoother)
t = (dist - roadHalfWidth) / blendWidth;
blendFactor = hermiteBlend(t); // C² continuous
finalHeight = lerp(splineHeight, terrainHeight, blendFactor);
```

### 4. Iterative Shoulder Smoothing

After blending, apply **localized blur** only to shoulder zone:

```csharp
// Identify shoulder pixels
for each pixel in shoulderZone:
    // 5x5 Gaussian kernel
    smoothedHeight = GaussianBlur5x5(pixel);
    
// Repeat 3-5 iterations
// DON'T touch road bed (keep perfectly flat)
```

### 5. Ghost Borders for Chunk Processing

To eliminate steps at boundaries when processing in chunks:

```csharp
// Process 64x64 chunk but read 68x68 data (2 pixel apron)
var chunkData = ReadWithBorder(x, y, 64, borderSize: 2);

// Process full 68x68
ProcessRoadSmoothing(chunkData, 68, 68);

// Write back only center 64x64
WriteChunk(chunkData.Center(64, 64), x, y);
```

## Recommended Third-Party Libraries

### 1. **MathNet.Numerics** (Already Available?)
- Bicubic interpolation
- Gaussian filters
- Signal processing

### 2. **Accord.NET** (Optional)
- Advanced image processing
- Morphological operations

### 3. **SixLabors.ImageSharp.Drawing** (Already Used)
- High-quality upsampling
- Gaussian blur operations

## Implementation Plan

### Phase 1: Core Infrastructure (High Priority)
- [ ] Implement `VirtualHeightfield` class with upsample/downsample
- [ ] Add bicubic upsampling using MathNet or custom implementation
- [ ] Add Gaussian downsampling with proper anti-aliasing
- [ ] Create unit tests for upsampling/downsampling accuracy

### Phase 2: SDF-Based Distance Calculation
- [ ] Implement proper perpendicular distance to spline
- [ ] Replace grid-based nearest-section lookup with SDF
- [ ] Optimize with spatial hash (keep existing grid optimization)

### Phase 3: Improved Blending
- [ ] Implement Hermite blend function (C² continuous)
- [ ] Add configurable blend curve types (cosine, hermite, cubic)
- [ ] Test different blend functions on real data

### Phase 4: Iterative Shoulder Smoothing
- [ ] Implement 5x5 Gaussian kernel for shoulder zone
- [ ] Add mask to protect road bed from smoothing
- [ ] Make iterations configurable (3-5 passes)

### Phase 5: Parameter Cleanup
- [ ] Remove obsolete parameters from extensive trial-and-error
- [ ] Keep only essential parameters:
   - `UpscaleFactor` (2x, 4x, 8x)
   - `RoadWidthMeters`
   - `BlendDistanceMeters`
   - `BlendCurveType`
   - `ShoulderSmoothIterations`
- [ ] Update documentation

### Phase 6: Testing & Validation
- [ ] Test on simple straight road
- [ ] Test on curved highway
- [ ] Test on complex intersection (may still need DirectMask)
- [ ] Compare output quality vs DirectMask approach
- [ ] Performance benchmarks

## Simplified Parameter Set (After Cleanup)

```csharp
public class ImprovedRoadSmoothingParameters
{
    // UPSAMPLING
    public int UpscaleFactor { get; set; } = 4; // 2x, 4x, or 8x
    
    // GEOMETRY
    public float RoadWidthMeters { get; set; } = 8.0f;
    public float BlendDistanceMeters { get; set; } = 12.0f;
    
    // BLENDING
    public BlendCurveType BlendCurve { get; set; } = BlendCurveType.Hermite;
    public int ShoulderSmoothIterations { get; set; } = 3;
    
    // CONSTRAINTS
    public float MaxRoadSlopeDegrees { get; set; } = 4.0f;
    public float MaxSideSlopeDegrees { get; set; } = 30.0f;
    
    // DEBUG
    public string? DebugOutputDirectory { get; set; }
    public bool ExportDebugImages { get; set; } = false;
}
```

## Expected Results

### Quality Improvements
- ? Smooth road surface (no stairs)
- ? Natural blending into terrain
- ? No jagged edges
- ? No chunk seams
- ? Realistic embankments

### Performance Impact
- ?? 4x upsampling = 16x more pixels (slower but acceptable for terrain gen)
- ?? Bicubic + Gaussian filtering adds overhead
- ? Still faster than manual terrain editing

### Fallback Strategy
- Keep DirectMask approach for complex intersections
- Use ImprovedSplineBased for simple curved roads
- Let user choose based on road network complexity

## Migration Path

1. Create `ImprovedSplineRoadSmoothing` as new class
2. Keep existing `SplineBased` implementation for backward compatibility
3. Add new approach: `RoadSmoothingApproach.ImprovedSpline`
4. After validation, deprecate old spline approach
5. Eventually merge improvements into main spline implementation

## Next Steps

**IMMEDIATE ACTION**: Implement Phase 1 (Virtual Heightfield) as it's the foundation for all other improvements.

Would you like me to proceed with implementation?
