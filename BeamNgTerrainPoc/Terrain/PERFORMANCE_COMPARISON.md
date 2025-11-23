# Spline vs DirectMask Performance Comparison

## Your Current Situation

**Spline Approach Status:**
- ? Working correctly (450 cross-sections, 20m affected distance)
- ?? **VERY SLOW** - Processing 4096×4096 heightmap may take 10-30 minutes!

## Why Spline is Slow

### For Each of 16.7 Million Pixels:
1. Find nearest cross-section (spatial index search)
2. Check 2-3 adjacent cross-sections
3. Calculate perpendicular distance to road segment
4. Interpolate elevation along segment
5. Calculate blended height

**With your settings:**
- 450 cross-sections to search
- 20-pixel radius to check
- 16,777,216 pixels to process

**Estimated time:** ~15-25 minutes for 4K heightmap

## DirectMask Performance

**DirectMask does:**
- No cross-sections (works directly on road pixels)
- Simple grid-aligned sampling
- Fast distance calculations

**Estimated time:** ~2-5 minutes for 4K heightmap

### Performance Comparison

| Approach | 4K Heightmap | Quality on Curves | Handles Intersections |
|----------|--------------|-------------------|----------------------|
| **Spline** | ~20 min | ????? Perfect | ? Fails |
| **Direct** | ~3 min | ????? Good | ? Works |

## Recommendation for Your Single Road

Since you have:
- ? Single road (no intersections)
- ? 448 meters long
- ?? Need reasonable processing time

**Two options:**

### Option 1: Wait for Spline (Best Quality)
```
Current run will take ~20 minutes
Result: Perfectly level road on all curves
Worth waiting if quality is critical
```

### Option 2: Switch to DirectMask (Fast, Good Quality)
```
Takes ~3 minutes
Result: Good smooth road, slight tilt possible on tight curves
Recommended for iteration/testing
```

## How to Switch to DirectMask

Edit `Program.cs`:

```csharp
roadParameters = new RoadSmoothingParameters
{
    // SWITCH TO DIRECTMASK (FAST)
    Approach = RoadSmoothingApproach.DirectMask,
    
    // Keep the good parameters
    RoadWidthMeters = 10.0f,
    TerrainAffectedRangeMeters = 15.0f,
    RoadMaxSlopeDegrees = 14.0f,
    SideMaxSlopeDegrees = 45.0f,
    BlendFunctionType = BlendFunctionType.Cosine
    
    // Remove CrossSectionIntervalMeters (not used by DirectMask)
};
```

## Expected Progress Output

### Spline (Current - Slow):
```
Max affected distance: 20,0 meters (20 pixels)
  Progress: 1.2% (12,545 pixels modified) - Elapsed: 00:15, ETA: 19:30
  Progress: 2.4% (25,123 pixels modified) - Elapsed: 00:30, ETA: 19:45
  ...continues for 20+ minutes...
```

### DirectMask (Fast):
```
Processing 4096x4096 heightmap with direct road mask approach...
Calculating road elevations...
  Calculating elevations: 25.0%
  Calculating elevations: 50.0%
  Calculating elevations: 75.0%
Applying slope constraints...
Applying road smoothing and blending...
  Blending: 25.0%
  Blending: 50.0%
  Blending: 75.0%
Blended 45,234 pixels in 185.3s
```

## Hybrid Workflow Suggestion

For development/testing:
1. **Use DirectMask** for quick iterations (3 min)
2. Test road placement, parameters, etc.
3. Once satisfied, **run Spline once** for final quality (20 min)

## Current Status

You're seeing:
```
Max affected distance: 20,0 meters (20 pixels)
```

And then it stopped showing progress? 

**This means it's processing!** It's just slow. You should see progress updates every ~30-60 seconds showing:
```
Progress: X.X% (YYY pixels modified) - Elapsed: mm:ss, ETA: mm:ss
```

If you're not seeing ANY progress updates after 2-3 minutes, the process might be stuck. Press Ctrl+C and:

1. **Try DirectMask instead** (much faster)
2. **Or reduce affected distance** to 10m (will be 2x faster)
3. **Or wait** - it should complete eventually!

## Bottom Line

**For your 448m single road:**

- **Spline:** Perfect quality, ~20 minutes ??
- **DirectMask:** Very good quality, ~3 minutes ?

**Both will work!** Choose based on whether you prefer quality or speed.

---

**My recommendation:** Try DirectMask first to see the results quickly, then decide if you need the extra quality from Spline.
