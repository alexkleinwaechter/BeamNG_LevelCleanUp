# Junction-Aware Road Extraction Improvements

## Overview
Enhanced the skeleton-based road extraction to prefer straight-through paths at junctions rather than taking sharp turns. This helps extract main road corridors from complex road networks.

## Key Features Added

### 1. **Junction Direction Awareness**
The algorithm now:
- Detects the incoming direction when approaching a junction
- Scores available exit paths based on angle deviation from incoming direction
- Prefers paths that continue in roughly the same direction (< 45° deviation)
- Still extracts all paths, but prioritizes main thoroughfares

### 2. **New Parameters in `RoadSmoothingParameters`**

#### Junction Control
- **`PreferStraightThroughJunctions`** (bool, default: false)
  - Enable junction-aware path selection
  - When true, the algorithm prefers paths aligned with incoming direction

- **`JunctionAngleThreshold`** (float, default: 45.0°)
  - Maximum angle deviation to consider a path "straight through"
  - Lower values = stricter straight-through requirement
  - Higher values = more lenient (allows gentle curves)

- **`MinPathLengthPixels`** (float, default: 20.0)
  - Minimum path length to keep
  - Filters out short segments (driveways, parking lots, fragments)

#### Spline Control
- **`SimplifyTolerancePixels`** (float, default: 0.5)
  - Path simplification tolerance
  - Lower = preserve more detail
  - Higher = smoother but less accurate

- **`SplineTension`** (float, 0-1, default: 0.5)
  - How tightly the spline follows control points
  - Lower (0.3) = straighter through junctions
  - Higher (0.8) = tighter curve following

- **`SplineContinuity`** (float, -1 to 1, default: 0.0)
  - Sharpness of corners
  - -1 = sharp corners
  - 0 = balanced
  - 1 = very smooth

- **`SplineBias`** (float, -1 to 1, default: 0.0)
  - Directional curve bias
  - Usually kept at 0 for neutral behavior

- **`SmoothingWindowSize`** (int, default: 10)
  - Number of cross-sections to average for elevation smoothing
  - Larger = smoother but less responsive to terrain changes

## Algorithm Changes

### Path Extraction (`ExtractPathsFromEndpointsAndJunctions`)
```
FOR each control point (endpoint or junction):
    Get available unwalked neighbors
    
    IF junction-aware mode AND multiple neighbors:
        Find incoming direction from existing paths
        Sort neighbors by angle deviation (smallest first)
        Prefer neighbor most aligned with incoming direction
    
    Walk each available path
    Mark as walked to avoid duplication
```

### Direction Scoring
When multiple paths are available at a junction:
1. Calculate incoming direction vector from previous path segment
2. For each potential exit:
   - Calculate exit direction vector
   - Compute angle between incoming and exit directions
   - Score based on angle (0° = perfect alignment)
3. Select path with smallest angle first

## Recommended Settings

### For Urban Road Networks
```csharp
PreferStraightThroughJunctions = true,
JunctionAngleThreshold = 45.0f,      // Allow moderate curves
MinPathLengthPixels = 50.0f,         // Filter small fragments
SplineTension = 0.3f,                // Straighter through junctions
BridgeEndpointMaxDistancePixels = 40.0f
```

### For Mountain/Rural Roads
```csharp
PreferStraightThroughJunctions = true,
JunctionAngleThreshold = 60.0f,      // More lenient for winding roads
MinPathLengthPixels = 30.0f,         // Keep shorter segments
SplineTension = 0.5f,                // More natural curves
BridgeEndpointMaxDistancePixels = 50.0f
```

### For Highway/Expressway
```csharp
PreferStraightThroughJunctions = true,
JunctionAngleThreshold = 30.0f,      // Very strict (highways are straight)
MinPathLengthPixels = 100.0f,        // Ignore ramps and short segments
SplineTension = 0.2f,                // Very straight
BridgeEndpointMaxDistancePixels = 60.0f
```

## Benefits

1. **Better Main Road Extraction**: Follows primary routes instead of branching at every intersection
2. **Reduced Fragmentation**: Filters out short segments that aren't part of main road network
3. **Smoother Curves**: Configurable spline parameters for optimal curve following
4. **Flexible Control**: Can be disabled (set `PreferStraightThroughJunctions = false`) for old behavior

## Debug Output

When junction awareness is enabled, you'll see console output like:
```
Junction awareness enabled: preferring paths within 45° of current direction
  Junction: preferred path at 12.3° vs alternatives at 78.5°, 134.2°
```

This helps verify the algorithm is making sensible decisions at intersections.

## Usage Example

See `Program.cs` method `CreateTerrainWithMultipleMaterials()` for a complete example with optimized parameters.
