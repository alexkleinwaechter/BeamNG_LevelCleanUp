# Road Smoothing - Alternative Simpler Approach

## Problem Diagnosis

The current implementation has fundamental issues:

1. **Centerline extraction creates 130,000+ cross-sections** for a 4K map
2. **Disconnected segments** create circular artifacts
3. **Distance transform + local maxima** finds too many "centerline" points
4. **Greedy nearest-neighbor ordering** doesn't work for road networks

## Recommended Alternative: Direct Road Pixel Approach

Instead of extracting centerlines, we should:

### Approach 1: Direct Road Mask Smoothing (SIMPLEST)

For each road pixel in the mask:
1. Set elevation to average of surrounding non-excluded heightmap values
2. Apply slope constraints
3. Blend edges with terrain

**Pros:**
- No centerline extraction needed
- No cross-section generation
- Handles complex road networks naturally
- Much faster

**Cons:**
- Less control over road profile
- May not follow exact centerline

### Approach 2: Simplified Centerline (RECOMMENDED)

1. Sample centerline points at larger intervals (every 50-100 pixels instead of every 2m)
2. Use **Ramer-Douglas-Peucker** algorithm to simplify the path
3. Generate far fewer cross-sections (hundreds instead of thousands)

**Implementation:**
```csharp
// Sample every N pixels along road mask
for (int y = 0; y < height; y += sampleInterval)
{
    for (int x = 0; x < width; x += sampleInterval)
    {
        if (roadMask[y, x] > 128 && IsApproximateCenterline(x, y))
        {
            centerlinePoints.Add(new Vector2(x, y));
        }
    }
}

// Simplify with Douglas-Peucker
var simplifiedPath = DouglasPeucker(centerlinePoints, tolerance);

// Generate cross-sections (should be < 1000 for 4K map)
```

## Immediate Fix

Should we:

**Option A:** Implement the simpler direct road pixel approach (no centerline)?
**Option B:** Fix the centerline extraction with aggressive sampling/simplification?
**Option C:** Use a completely different algorithm (e.g., just smooth pixels under the road mask)?

Which approach would you prefer?
