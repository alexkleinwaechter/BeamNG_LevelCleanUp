# Road Smoothing Parameters - Complete Guide

This guide explains every parameter available in `RoadSmoothingParameters` in simple terms, with value ranges and usage information.

---

## Table of Contents
1. [Approach Selection](#approach-selection)
2. [Road Geometry Parameters](#road-geometry-parameters)
3. [Slope Constraint Parameters](#slope-constraint-parameters)
4. [Blending Parameters](#blending-parameters)
5. [Post-Processing Smoothing Parameters](#post-processing-smoothing-parameters)
6. [Exclusion Zone Parameters](#exclusion-zone-parameters)
7. [Debug Output Parameters](#debug-output-parameters)
8. [Spline-Specific Parameters](#spline-specific-parameters)
9. [DirectMask-Specific Parameters](#directmask-specific-parameters)

---

## Approach Selection

### `Approach`
**Type:** `RoadSmoothingApproach` enum  
**Default:** `DirectMask`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `DirectMask` or `Spline`

**What it does (simple explanation):**  
This chooses which method to use for smoothing your roads. Think of it like choosing between two different tools:

- **`DirectMask`**: Like using a paint roller - simple, works everywhere, handles complex road intersections well. Good for city streets with lots of turns and intersections.
- **`Spline`**: Like using a precision airbrush - creates super smooth curves, perfect for highways and race tracks. Not recommended for complex intersections.

**Why it's necessary:**  
Different road types need different smoothing approaches. City streets with intersections need the robust DirectMask approach, while racing circuits need the ultra-smooth Spline approach.

**Example:**
```csharp
Approach = RoadSmoothingApproach.Spline  // For a smooth highway
Approach = RoadSmoothingApproach.DirectMask  // For a city with intersections
```

---

## Road Geometry Parameters

These parameters define the physical dimensions of your road and how it blends into the terrain.

### `RoadWidthMeters`
**Type:** `float`  
**Default:** `8.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `2.0` to `20.0` (realistic), up to `50.0` (extreme)

**What it does (simple explanation):**  
This is how wide the flat part of the road will be in meters. An 8-meter road is a typical 2-lane highway. The road surface within this width will be completely flattened.

**Why it's necessary:**  
Different roads have different widths. A narrow mountain path might be 4 meters, while a 4-lane highway could be 16 meters.

**Example values:**
- `4.0` - Narrow mountain road or bike path
- `8.0` - Standard 2-lane road (default)
- `12.0` - Wide 3-lane highway
- `16.0` - 4-lane highway

---

### `TerrainAffectedRangeMeters`
**Type:** `float`  
**Default:** `12.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.0` to `30.0` (realistic), up to `50.0` (extreme)

**What it does (simple explanation):**  
This is how far from the road edge the terrain will be smoothed and blended. Think of it as the "shoulder" or "embankment" distance. The terrain gradually transitions from the flat road to the natural landscape within this distance.

**Total impact width = RoadWidthMeters + (TerrainAffectedRangeMeters × 2)**

For example: 8m road + (12m × 2) = 32m total width affected

**Why it's necessary:**  
Roads need smooth transitions to look realistic. A highway cut through a mountain needs a wider transition zone than a road on flat land.

**Example values:**
- `6.0` - Tight mountain road (road hugs terrain closely)
- `12.0` - Standard highway shoulder (default)
- `20.0` - Wide highway with gentle embankments
- `25.0` - Extra wide for global leveling (see `GlobalLevelingStrength`)

**?? Important:** If using high `GlobalLevelingStrength` (>0.5), increase this to 20-25m to prevent "dotted road" artifacts!

---

### `CrossSectionIntervalMeters`
**Type:** `float`  
**Default:** `0.5`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.25` to `2.0` (realistic), up to `5.0` (low quality)

**What it does (simple explanation):**  
This controls how often the algorithm "measures" the road. Smaller values = more measurements = smoother result but slower processing.

Think of it like taking a photo every X meters along the road. At 0.5 meters, you take 2 photos per meter. At 2.0 meters, you only take 1 photo every 2 meters.

**Why it's necessary:**  
If this value is too large, you'll get gaps in the smoothing, creating a "dotted road" effect. The algorithm automatically warns you if the value is too high.

**Formula:** Should be ? `(RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3`

**Example values:**
- `0.25` - Ultra-high quality racing circuit (very slow)
- `0.5` - High quality highway (default, good balance)
- `1.0` - Standard quality local road
- `2.0` - Low quality (may show gaps)

**?? Auto-adjustment:** The code automatically reduces this value if it detects it's too high to prevent gaps.

---

### `LongitudinalSmoothingWindowMeters`
**Type:** `float`  
**Default:** `20.0`  
**Status:** ?? **PARTIALLY USED** (converted to pixel-based window size internally)  
**Value Range:** `5.0` to `100.0`

**What it does (simple explanation):**  
This controls how smooth the road is along its length direction. Higher values = smoother roads but less responsive to terrain changes.

Think of it as "how much should we average out bumps along the road length?"

**Why it's necessary:**  
Roads shouldn't have sudden elevation changes. This parameter smooths out the elevation profile along the road's path.

**Example values:**
- `10.0` - Minimal smoothing, road follows terrain closely
- `20.0` - Balanced smoothing (default)
- `50.0` - Very smooth, like a professionally engineered highway
- `100.0` - Ultra-smooth race track (may ignore terrain too much)

**Note:** This is converted to `SmoothingWindowSize` internally based on `CrossSectionIntervalMeters`.

---

## Slope Constraint Parameters

These parameters control how steep your road can be.

### `RoadMaxSlopeDegrees`
**Type:** `float`  
**Default:** `4.0`  
**Status:** ? **ACTIVELY USED** (validated in statistics)  
**Value Range:** `1.0` to `15.0` (realistic), up to `45.0` (extreme)

**What it does (simple explanation):**  
This is the maximum steepness allowed on the road surface itself. Think of it as the "incline warning" on highway signs.

- 4° = gentle highway grade
- 10° = steep mountain road
- 15° = very steep, like San Francisco hills

**Why it's necessary:**  
Prevents unrealistic or undriveable roads. Real highways rarely exceed 6-8 degrees of slope.

**Example values:**
- `1.0` - Ultra-flat race track
- `4.0` - Highway standard (default)
- `8.0` - Mountain road
- `12.0` - Steep mountain pass (still driveable)

**Note:** The algorithm validates and warns if the final road exceeds this limit, but doesn't enforce it strictly during processing.

---

### `SideMaxSlopeDegrees`
**Type:** `float`  
**Default:** `30.0`  
**Status:** ? **ACTIVELY USED** (affects embankment steepness)  
**Value Range:** `15.0` to `45.0` (realistic), up to `60.0` (extreme)

**What it does (simple explanation):**  
This controls how steep the embankment (road shoulder) can be. The transition zone from road edge to natural terrain will not exceed this steepness.

- 25° = gentle embankment (1:2.5 ratio - rises 1m for every 2.5m horizontal)
- 30° = standard embankment (1:1.7 ratio)
- 40° = steep embankment (1:1.2 ratio)

**Why it's necessary:**  
Prevents unrealistic cliff-like road edges. Real road embankments need to be stable and driveable (if you go off the road).

**Example values:**
- `20.0` - Very gentle embankment (wide, gradual)
- `30.0` - Standard embankment (default)
- `40.0` - Steep embankment (narrow transition)

---

## Blending Parameters

These control how the road blends into the surrounding terrain.

### `BlendFunctionType`
**Type:** `BlendFunctionType` enum  
**Default:** `Cosine`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `Linear`, `Cosine`, `Cubic`, `Quintic`

**What it does (simple explanation):**  
This chooses the "smoothing curve" used to blend the road into terrain. Think of it like choosing between different paint brush strokes:

- **`Linear`**: Sharp, straight blend (like a ruler edge)
- **`Cosine`**: Smooth, natural blend (like a gentle curve) - **RECOMMENDED**
- **`Cubic`**: Very smooth (like a smooth S-curve)
- **`Quintic`**: Extra smooth (like an even smoother S-curve)

**Why it's necessary:**  
Different blend functions create different visual effects. Cosine is most natural-looking for terrain transitions.

**Example:**
```csharp
BlendFunctionType = BlendFunctionType.Cosine  // Recommended default
BlendFunctionType = BlendFunctionType.Linear  // Sharp, less natural
BlendFunctionType = BlendFunctionType.Quintic  // Extra smooth
```

---

### `EnableTerrainBlending`
**Type:** `bool`  
**Default:** `true`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, the road will actually blend into the terrain. If `false`, the algorithm only extracts road geometry without modifying the heightmap (debug mode).

**Why it's necessary:**  
Useful for debugging to see the extracted road paths without actually changing the terrain.

**Example:**
```csharp
EnableTerrainBlending = true   // Normal operation - smooth the roads
EnableTerrainBlending = false  // Debug - just show road paths
```

---

## Post-Processing Smoothing Parameters

These parameters control the **NEW** post-processing blur that eliminates staircase artifacts.

### `EnablePostProcessingSmoothing`
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE)  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, applies a final smoothing pass to eliminate visible "steps" or "bumps" on the road surface. This is like applying a final polish to the road after the main smoothing is done.

**Why it's necessary:**  
The cross-section sampling can create subtle staircase artifacts that are visible when driving at high speeds. This post-processing eliminates them completely.

**Example:**
```csharp
EnablePostProcessingSmoothing = true   // RECOMMENDED - eliminates staircase artifacts
EnablePostProcessingSmoothing = false  // Faster but may show artifacts
```

---

### `SmoothingType`
**Type:** `PostProcessingSmoothingType` enum  
**Default:** `Gaussian`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE)  
**Value Range:** `Gaussian`, `Box`, `Bilateral`

**What it does (simple explanation):**  
Chooses which smoothing filter to use for post-processing:

- **`Gaussian`**: Best quality, smooth and natural (like a soft brush) - **RECOMMENDED**
- **`Box`**: Fastest, simple averaging (like a square brush)
- **`Bilateral`**: Edge-preserving (like a smart brush that avoids edges)

**Why it's necessary:**  
Different filters have different quality vs. speed trade-offs. Gaussian is best for most cases.

**Example:**
```csharp
SmoothingType = PostProcessingSmoothingType.Gaussian   // Best quality (recommended)
SmoothingType = PostProcessingSmoothingType.Box        // Fastest
SmoothingType = PostProcessingSmoothingType.Bilateral  // Preserves sharp edges
```

---

### `SmoothingKernelSize`
**Type:** `int`  
**Default:** `7`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE)  
**Value Range:** `3`, `5`, `7`, `9`, `11`, `13`, `15` (must be odd)

**What it does (simple explanation):**  
Controls how large the smoothing brush is in pixels. Larger = smoother but slower.

Think of it as the brush size:
- Size 3 = tiny brush (subtle smoothing)
- Size 7 = medium brush (good balance) - **RECOMMENDED**
- Size 11 = large brush (very smooth)

**Why it's necessary:**  
Allows you to control how aggressive the smoothing is. Larger kernels remove more artifacts but may blur details.

**Example values:**
- `3` - Minimal smoothing, preserve detail
- `5` - Light smoothing
- `7` - Medium smoothing (default, recommended)
- `9` - Heavy smoothing
- `11` - Very heavy smoothing
- `15` - Maximum smoothing (may be too much)

**?? Must be odd number:** 3, 5, 7, 9, 11, 13, 15 (not 4, 6, 8, etc.)

---

### `SmoothingSigma`
**Type:** `float`  
**Default:** `1.5`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE, Gaussian/Bilateral only)  
**Value Range:** `0.5` to `4.0`

**What it does (simple explanation):**  
Controls the "strength" of the Gaussian blur. Higher values = more aggressive smoothing.

Think of it as the brush pressure:
- 0.5 = light touch
- 1.5 = medium pressure (default)
- 3.0 = heavy pressure

**Why it's necessary:**  
Fine-tunes the smoothing intensity for Gaussian and Bilateral filters. Allows precise control over artifact removal.

**Example values:**
- `0.5` - Very light smoothing
- `1.0` - Light smoothing
- `1.5` - Medium smoothing (default, recommended)
- `2.0` - Heavy smoothing
- `3.0` - Very heavy smoothing
- `4.0` - Maximum smoothing

---

### `SmoothingMaskExtensionMeters`
**Type:** `float`  
**Default:** `6.0`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE)  
**Value Range:** `0.0` to `12.0`

**What it does (simple explanation):**  
Controls how far beyond the road edge the smoothing is applied. At 0, only the road surface is smoothed. At 6 meters, smoothing extends into the shoulder area.

**Total smoothing width = RoadWidthMeters + (SmoothingMaskExtensionMeters × 2)**

**Why it's necessary:**  
Ensures the transition from road to shoulder is also smooth, preventing a visible "seam" at the road edge.

**Example values:**
- `0.0` - Smooth road only (may show edge seam)
- `4.0` - Smooth road + near shoulder
- `6.0` - Smooth road + shoulder (default, recommended)
- `10.0` - Smooth entire blend zone
- Should be ? `TerrainAffectedRangeMeters`

---

### `SmoothingIterations`
**Type:** `int`  
**Default:** `1`  
**Status:** ? **ACTIVELY USED** (NEW FEATURE)  
**Value Range:** `1` to `5`

**What it does (simple explanation):**  
How many times to apply the smoothing filter. More iterations = smoother result but slower processing.

Think of it as:
- 1 pass = single brush stroke
- 2 passes = two brush strokes (smoother)
- 3 passes = three brush strokes (very smooth)

**Why it's necessary:**  
Sometimes a single smoothing pass isn't enough to remove all artifacts. Multiple passes can create an ultra-smooth result.

**Example values:**
- `1` - Single pass (recommended, usually sufficient)
- `2` - Double pass (smoother)
- `3` - Triple pass (very smooth, for extreme cases)
- `4+` - Rarely needed, may blur too much

**?? Performance:** Each iteration multiplies processing time (2 iterations = 2× slower).

---

## Exclusion Zone Parameters

### `ExclusionLayerPaths`
**Type:** `List<string>?`  
**Default:** `null`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** List of file paths to PNG images, or `null`

**What it does (simple explanation):**  
A list of image files that mark areas where roads should NOT be smoothed. White pixels (255) in these images indicate "don't smooth here" zones.

Useful for:
- Protecting existing structures (buildings, bridges)
- Preserving manually-sculpted terrain features
- Marking areas where roads should remain bumpy (dirt roads)

**Why it's necessary:**  
Sometimes you want to smooth most roads but leave certain areas untouched (like a dirt road section or an area near a bridge).

**Example:**
```csharp
ExclusionLayerPaths = new List<string>
{
    @"d:\terrain\exclusion_buildings.png",
    @"d:\terrain\exclusion_bridges.png"
}
```

---

## Debug Output Parameters

### `DebugOutputDirectory`
**Type:** `string?`  
**Default:** `null`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** Directory path or `null`

**What it does (simple explanation):**  
If set, the algorithm will save debug images to this folder showing the road paths, elevations, and smoothing process. Useful for understanding what the algorithm is doing.

If `null`, uses the current working directory.

**Why it's necessary:**  
Essential for troubleshooting when roads don't look right. The debug images show exactly what the algorithm "sees" and help you adjust parameters.

**Example:**
```csharp
DebugOutputDirectory = @"d:\temp\road_debug"  // Save debug images here
DebugOutputDirectory = null                    // Use current directory
```

---

## Spline-Specific Parameters

These parameters only apply when `Approach = RoadSmoothingApproach.Spline`.

### `SplineParameters`
**Type:** `SplineRoadParameters?`  
**Default:** `null` (uses defaults)  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `null` or `new SplineRoadParameters { ... }`

**What it does (simple explanation):**  
A container for all the advanced settings specific to the Spline approach. If `null`, default values are used automatically.

**Why it's necessary:**  
Allows fine-tuning of the spline extraction and smoothing process without cluttering the main parameters.

---

### Spline Parameters: Skeleton Extraction

#### `SkeletonDilationRadius`
**Type:** `int`  
**Default:** `1`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0` to `5`

**What it does (simple explanation):**  
Before extracting the road centerline, the road mask is "fattened" slightly by this many pixels. This helps connect nearly-touching road segments.

- 0 = no fattening (cleanest but may miss connections)
- 1 = minimal fattening (recommended)
- 3+ = heavy fattening (may create blobs at curves)

**Why it's necessary:**  
Road masks often have small gaps. Dilation bridges these gaps to extract a continuous centerline.

**?? Side effect:** Higher values can create "tail" artifacts at sharp hairpin turns.

**Example values:**
- `0` - No dilation (use only if road mask is perfect)
- `1` - Minimal dilation (recommended default)
- `2` - Moderate dilation (better connectivity)
- `3` - Heavy dilation (may create artifacts at curves)

---

### Spline Parameters: Path Ordering

#### `UseGraphOrdering`
**Type:** `bool`  
**Default:** `true`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, uses a smart graph-based algorithm to order skeleton points. If `false`, uses a simpler nearest-neighbor approach.

Graph-based is more robust for complex road networks.

**Why it's necessary:**  
Complex road skeletons need smart ordering to avoid jumping between disconnected fragments.

**Recommendation:** Always use `true` (graph-based).

---

#### `DensifyMaxSpacingPixels`
**Type:** `float`  
**Default:** `1.5`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.5` to `5.0`

**What it does (simple explanation):**  
After extracting the skeleton, if any two consecutive points are farther apart than this value, intermediate points are inserted to fill the gap.

Smaller values = smoother splines (more points).

**Why it's necessary:**  
Ensures the spline has enough control points to be smooth. Prevents jagged curves from sparse skeleton points.

**Example values:**
- `0.5` - Very dense (ultra-smooth, slower)
- `1.5` - Balanced (default)
- `3.0` - Sparse (faster but less smooth)

---

#### `OrderingNeighborRadiusPixels`
**Type:** `float`  
**Default:** `2.5`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `1.0` to `10.0`

**What it does (simple explanation):**  
When building the graph for ordering, points within this distance are considered neighbors. Larger values create more connections but slower processing.

**Why it's necessary:**  
Defines the connectivity of the skeleton graph. Too small = fragmented paths. Too large = slow processing.

**Example values:**
- `1.5` - Tight (only immediate neighbors)
- `2.5` - Balanced (default)
- `5.0` - Wide (connects distant points, slower)

---

#### `BridgeEndpointMaxDistancePixels`
**Type:** `float`  
**Default:** `30.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `10.0` to `100.0`

**What it does (simple explanation):**  
If two skeleton endpoints are closer than this distance, they'll be connected with a straight line to bridge the gap.

Helps connect nearly-touching road segments that the skeleton algorithm missed.

**Why it's necessary:**  
Road masks often have gaps at intersections or damaged areas. Bridging prevents fragmented paths.

**Example values:**
- `20.0` - Conservative bridging
- `30.0` - Balanced (default)
- `50.0` - Aggressive bridging (may connect unrelated fragments)

---

#### `SimplifyTolerancePixels`
**Type:** `float`  
**Default:** `0.5`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.0` to `5.0`

**What it does (simple explanation):**  
After extracting the skeleton path, remove points that don't significantly affect the path shape. This "simplifies" the path by removing unnecessary points.

- 0 = keep all points (most accurate but more processing)
- 1-2 = remove minor jitter (good balance)
- 5+ = aggressive simplification (straighter paths)

**Why it's necessary:**  
Skeleton extraction can produce thousands of closely-spaced points. Simplification removes redundant points without changing the path shape, making spline fitting faster.

**Example values:**
- `0.0` - No simplification (keep every point)
- `0.5` - Minimal simplification (default, remove tiny jitter)
- `2.0` - Moderate simplification (smooth out minor bumps)
- `5.0` - Heavy simplification (very straight paths)

---

### Spline Parameters: Junction Handling

#### `PreferStraightThroughJunctions`
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, at road intersections the algorithm prefers to continue straight rather than taking sharp turns. This extracts the "main road" through intersections.

If `false`, treats all junction branches equally (may follow side roads).

**Why it's necessary:**  
Useful for extracting highways from complex networks. Prevents the algorithm from suddenly following a side street at an intersection.

**?? Warning:** Should be `false` for simple curved roads without intersections! Only enable for actual road networks.

**Example:**
```csharp
PreferStraightThroughJunctions = false  // Simple curved roads (default)
PreferStraightThroughJunctions = true   // Complex road network with intersections
```

---

#### `JunctionAngleThreshold`
**Type:** `float`  
**Default:** `45.0`  
**Status:** ?? **CONDITIONALLY USED** (only if `PreferStraightThroughJunctions = true`)  
**Value Range:** `15.0` to `90.0` degrees

**What it does (simple explanation):**  
When `PreferStraightThroughJunctions` is enabled, defines what angle change is considered "straight through."

- 30° = strict (only nearly-straight continuations)
- 45° = balanced (default)
- 60° = loose (allows gentle curves)

**Why it's necessary:**  
Defines what "straight" means at junctions. Prevents the algorithm from following 90° turns when trying to extract the main road.

**Example values:**
- `30.0` - Very strict straight-through (highway interchanges)
- `45.0` - Balanced (default)
- `60.0` - Loose (allows gentle curves at junctions)

**Note:** Unused if `PreferStraightThroughJunctions = false`.

---

#### `MinPathLengthPixels`
**Type:** `float`  
**Default:** `20.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `10.0` to `200.0`

**What it does (simple explanation):**  
After extracting all skeleton paths, discard any paths shorter than this length. Helps remove parking lots, driveways, and small fragments.

**Why it's necessary:**  
Prevents processing tiny road fragments that aren't significant (like parking lot spur roads or artifacts from the skeleton algorithm).

**Example values:**
- `20.0` - Minimal filtering (keep most fragments)
- `50.0` - Standard filtering (default for many presets)
- `100.0` - Aggressive filtering (only major roads)
- `200.0` - Very aggressive (highways only)

---

### Spline Parameters: Curve Fitting

#### `SplineTension`
**Type:** `float`  
**Default:** `0.3`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.0` to `1.0`

**What it does (simple explanation):**  
Controls how tightly the spline curve follows the skeleton points:

- 0.0 = very loose (smooth but may deviate from path)
- 0.5 = balanced
- 1.0 = very tight (follows path closely but may be jagged)

Think of it as string tension:
- Loose string (0.0) = gentle curve
- Tight string (1.0) = sharp curve

**Why it's necessary:**  
Allows control over the smoothness vs. accuracy trade-off. Race tracks want loose (smooth), while technical roads want tighter (accurate).

**Example values:**
- `0.1` - Very loose (ultra-smooth, for race tracks)
- `0.3` - Balanced (default)
- `0.5` - Moderate tension
- `0.8` - Tight (follows skeleton closely)

---

#### `SplineContinuity`
**Type:** `float`  
**Default:** `0.5`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `-1.0` to `1.0`

**What it does (simple explanation):**  
Controls how smooth the curve is at corner points:

- -1.0 = sharp corners (allows kinks)
- 0.0 = balanced
- 1.0 = very smooth corners (no kinks)

Think of it as corner rounding:
- Sharp (-1.0) = angular corners
- Smooth (1.0) = rounded corners

**Why it's necessary:**  
Determines whether roads have sharp corners or smooth curves. Racing circuits want high continuity (smooth), while city streets might want lower values.

**Example values:**
- `-0.5` - Sharp corners (city streets)
- `0.0` - Balanced corners
- `0.5` - Smooth corners (default)
- `0.8` - Very smooth corners (highways)
- `1.0` - Maximum smoothness (race tracks)

---

#### `SplineBias`
**Type:** `float`  
**Default:** `0.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `-1.0` to `1.0`

**What it does (simple explanation):**  
Controls the direction the curve "leans" at control points:

- -1.0 = bias toward previous point (curve approaches from behind)
- 0.0 = neutral, symmetric curve (default)
- 1.0 = bias toward next point (curve approaches from ahead)

**Why it's necessary:**  
Allows asymmetric curves. Usually kept at 0.0 for natural-looking roads.

**Recommendation:** Keep at `0.0` unless you have a specific artistic reason to change it.

---

### Spline Parameters: Elevation Smoothing

#### `SmoothingWindowSize`
**Type:** `int`  
**Default:** `101`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `11` to `501` (must be odd)

**What it does (simple explanation):**  
How many cross-section samples to average together when smoothing the elevation along the road length.

Larger values = smoother elevation profile (less bumpy road).

Think of it as "how far ahead/behind should we look when smoothing elevation?"

**Why it's necessary:**  
Prevents sudden elevation changes along the road. Larger windows create smoother, more professional-looking highways.

**Example values:**
- `51` - Minimal smoothing (road follows terrain closely)
- `101` - Balanced smoothing (default)
- `201` - Heavy smoothing (highway quality)
- `301` - Very heavy smoothing (ultra-smooth race track)
- `501` - Maximum smoothing (may be too flat)

**Formula:** Window size in meters ? `SmoothingWindowSize × CrossSectionIntervalMeters`

For example: 201 window × 0.5m interval = ~100m smoothing window

---

#### `UseButterworthFilter`
**Type:** `bool`  
**Default:** `true`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
Chooses between two smoothing algorithms:

- **`true`**: Butterworth filter - Professional quality, maximally flat, sharper cutoff (recommended)
- **`false`**: Gaussian filter - Simple averaging, softer transitions

Butterworth is like a professional audio equalizer (precise, flat response).  
Gaussian is like a simple volume slider (smooth but less precise).

**Why it's necessary:**  
Butterworth produces smoother roads with better frequency response. It's the professional choice for highway-quality roads.

**Recommendation:** Use `true` (Butterworth) for best results.

---

#### `ButterworthFilterOrder`
**Type:** `int`  
**Default:** `3`  
**Status:** ? **ACTIVELY USED** (only if `UseButterworthFilter = true`)  
**Value Range:** `1` to `8`

**What it does (simple explanation):**  
Controls how "sharp" the Butterworth filter cutoff is. Higher values = sharper cutoff = flatter roads.

Think of it as filter "aggressiveness":
- Order 1-2 = gentle smoothing
- Order 3-4 = aggressive smoothing (recommended)
- Order 5-6 = very aggressive (maximum flatness)

**Why it's necessary:**  
Higher orders create smoother roads but may remove too much terrain variation. Order 3-4 is the sweet spot.

**Example values:**
- `2` - Gentle smoothing (road follows terrain)
- `3` - Balanced smoothing (default)
- `4` - Aggressive smoothing (highway quality)
- `6` - Maximum smoothing (ultra-flat race track)

**Note:** Only used if `UseButterworthFilter = true`.

---

#### `GlobalLevelingStrength`
**Type:** `float`  
**Default:** `0.0`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `0.0` to `1.0`

**What it does (simple explanation):**  
Controls how much the road should be "leveled" to a global average elevation:

- **0.0** = terrain-following (road goes up and down with terrain) - **RECOMMENDED**
- **0.5** = balanced (moderate leveling)
- **0.9** = strong leveling (forces entire road network to similar elevation)

Think of it as "should all roads be at the same height?"

**Why it's necessary:**  
For very hilly terrain, you might want roads to cut through hills rather than follow them. Global leveling achieves this.

**?? CRITICAL WARNING:**  
If using `GlobalLevelingStrength > 0.5`, you **MUST** increase `TerrainAffectedRangeMeters` to 20-25m! Otherwise, you'll get "dotted road" artifacts (disconnected segments).

**Example values:**
- `0.0` - Terrain-following (default, recommended for most cases)
- `0.3` - Slight leveling (gentle hills)
- `0.5` - Moderate leveling (moderate hills)
- `0.7` - Strong leveling (mountainous terrain)
- `0.9` - Very strong leveling (extreme mountains)

**When to use:**
- `0.0` - Default for most terrains
- `0.5-0.9` - Mountainous terrain where you want a flat road network (requires wide blend zones!)

---

### Spline Parameters: Debug Output

#### `ExportSplineDebugImage`
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, saves a debug image showing the spline centerline and road width as colored lines.

Yellow line = road centerline  
Green lines = road edges (perpendicular cross-sections)

**Why it's necessary:**  
Essential for troubleshooting. Shows exactly where the algorithm thinks the road is.

**Output file:** `spline_debug.png` in `DebugOutputDirectory`

---

#### `ExportSkeletonDebugImage`
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, saves a debug image showing the extracted skeleton (road centerline) before spline fitting.

Shows the raw skeleton as white lines on black background.

**Why it's necessary:**  
Helps diagnose skeleton extraction issues. If the skeleton is wrong, the final road will be wrong.

**Output file:** `skeleton_debug.png` in `DebugOutputDirectory`

---

#### `ExportSmoothedElevationDebugImage`
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
If `true`, saves a debug image showing the road colored by elevation:

- Blue = low elevation
- Green = medium elevation
- Red = high elevation

**Why it's necessary:**  
Visualizes the smoothed elevation profile along the road. Helps verify that elevation smoothing is working correctly.

**Output file:** `spline_smoothed_elevation_debug.png` in `DebugOutputDirectory`

---

## DirectMask-Specific Parameters

These parameters only apply when `Approach = RoadSmoothingApproach.DirectMask`.

### `DirectMaskParameters`
**Type:** `DirectMaskRoadParameters?`  
**Default:** `null` (uses defaults)  
**Status:** ? **ACTIVELY USED**  
**Value Range:** `null` or `new DirectMaskRoadParameters { ... }`

**What it does (simple explanation):**  
A container for all the advanced settings specific to the DirectMask approach. If `null`, default values are used automatically.

**Why it's necessary:**  
Allows fine-tuning of the direct mask sampling process without cluttering the main parameters.

---

### DirectMask Parameters

#### `RoadPixelSearchRadius` (DirectMask)
**Type:** `int`  
**Default:** `3`  
**Status:** ? **ACTIVELY USED** (DirectMask approach only)  
**Value Range:** `1` to `10`

**What it does (simple explanation):**  
When sampling elevation from the road mask, search within this many pixels around the sample point to find a road pixel.

Larger radius = more robust to gaps in the road mask but slower processing.

**Why it's necessary:**  
Road masks often have small gaps. A search radius helps find nearby road pixels even if the exact sample point is in a gap.

**Example values:**
- `1` - Minimal search (fast but may miss gaps)
- `3` - Balanced (default)
- `5` - Wide search (robust to gaps)
- `10` - Very wide search (very robust but slow)

---

#### `SmoothingWindowSize` (DirectMask)
**Type:** `int`  
**Default:** `10`  
**Status:** ? **ACTIVELY USED** (DirectMask approach only)  
**Value Range:** `5` to `100`

**What it does (simple explanation):**  
How many elevation samples to average together when smoothing the road elevation.

Similar to the Spline version but uses a different sampling method.

**Why it's necessary:**  
Smooths out elevation bumps along the road. Larger values = smoother roads.

**Example values:**
- `5` - Minimal smoothing
- `10` - Balanced (default)
- `20` - Heavy smoothing
- `50` - Very heavy smoothing

---

#### `UseButterworthFilter` (DirectMask)
**Type:** `bool`  
**Default:** `false`  
**Status:** ? **ACTIVELY USED** (DirectMask approach only)  
**Value Range:** `true` or `false`

**What it does (simple explanation):**  
Same as the Spline version - chooses between Butterworth (professional) or simple averaging.

**Default is `false` for DirectMask** because DirectMask is used for fast testing, so simple averaging is preferred for speed.

---

#### `ButterworthFilterOrder` (DirectMask)
**Type:** `int`  
**Default:** `3`  
**Status:** ?? **CONDITIONALLY USED** (only if DirectMask's `UseButterworthFilter = true`)  
**Value Range:** `1` to `8`

**What it does (simple explanation):**  
Same as the Spline version - controls Butterworth filter aggressiveness.

**Note:** Only used if DirectMask's `UseButterworthFilter = true`.

---

## Quick Reference Table

### Essential Parameters (Most Users)

| Parameter | Default | Recommended Range | Description |
|-----------|---------|-------------------|-------------|
| `Approach` | `DirectMask` | `Spline` for highways | Smoothing method |
| `RoadWidthMeters` | `8.0` | `4.0` - `16.0` | Road surface width |
| `TerrainAffectedRangeMeters` | `12.0` | `6.0` - `20.0` | Shoulder blend distance |
| `EnablePostProcessingSmoothing` | `false` | `true` recommended | Eliminate staircase artifacts |
| `SmoothingKernelSize` | `7` | `5` - `9` | Post-processing smoothness |

### Advanced Parameters (Power Users)

| Parameter | Default | Range | When to Change |
|-----------|---------|-------|----------------|
| `CrossSectionIntervalMeters` | `0.5` | `0.25` - `2.0` | Increase for faster processing |
| `GlobalLevelingStrength` | `0.0` | `0.0` - `0.9` | Enable for mountainous terrain |
| `SmoothingWindowSize` | `101` | `51` - `301` | Increase for smoother roads |
| `UseButterworthFilter` | `true` | - | Disable for faster processing |
| `ButterworthFilterOrder` | `3` | `2` - `6` | Increase for flatter roads |

### Rarely Changed Parameters

| Parameter | Default | When to Change |
|-----------|---------|----------------|
| `SimplifyTolerancePixels` | `0.5` | Almost never (auto-optimized) |
| `BridgeEndpointMaxDistancePixels` | `30.0` | Only for fragmented skeletons |
| `MinPathLengthPixels` | `20.0` | To filter small fragments |
| `SplineTension` | `0.3` | For artistic control |
| `SplineContinuity` | `0.5` | For corner smoothness |
| `SplineBias` | `0.0` | Almost never (keep at 0) |

---

## Common Scenarios and Settings

### 1. Racing Circuit (Maximum Smoothness)
```csharp
Approach = RoadSmoothingApproach.Spline,
RoadWidthMeters = 12.0f,
TerrainAffectedRangeMeters = 15.0f,
CrossSectionIntervalMeters = 0.3f,
RoadMaxSlopeDegrees = 2.0f,
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 9,
SmoothingSigma = 2.0f,
SmoothingIterations = 2,
SplineParameters = new SplineRoadParameters {
    SmoothingWindowSize = 301,
    UseButterworthFilter = true,
    ButterworthFilterOrder = 6,
    SplineTension = 0.1f,
    SplineContinuity = 0.9f
}
```

### 2. Highway (Balanced Quality)
```csharp
Approach = RoadSmoothingApproach.Spline,
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 12.0f,
CrossSectionIntervalMeters = 0.5f,
RoadMaxSlopeDegrees = 4.0f,
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 7,
SmoothingSigma = 1.5f,
SplineParameters = new SplineRoadParameters {
    SmoothingWindowSize = 201,
    UseButterworthFilter = true,
    ButterworthFilterOrder = 4
}
```

### 3. Mountain Road (Terrain-Following)
```csharp
Approach = RoadSmoothingApproach.Spline,
RoadWidthMeters = 6.0f,
TerrainAffectedRangeMeters = 8.0f,
CrossSectionIntervalMeters = 0.5f,
RoadMaxSlopeDegrees = 8.0f,
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 5,
SmoothingSigma = 1.0f,
SplineParameters = new SplineRoadParameters {
    SmoothingWindowSize = 101,
    UseButterworthFilter = true,
    ButterworthFilterOrder = 3,
    GlobalLevelingStrength = 0.0f  // Follow terrain
}
```

### 4. City with Intersections (Robust)
```csharp
Approach = RoadSmoothingApproach.DirectMask,  // Use DirectMask for intersections
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 10.0f,
CrossSectionIntervalMeters = 1.0f,
RoadMaxSlopeDegrees = 6.0f,
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 7
```

### 5. Mountainous with Global Leveling (Flat Network)
```csharp
Approach = RoadSmoothingApproach.Spline,
RoadWidthMeters = 8.0f,
TerrainAffectedRangeMeters = 25.0f,  // CRITICAL: Wide for global leveling!
CrossSectionIntervalMeters = 0.4f,
RoadMaxSlopeDegrees = 2.0f,
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 9,
SmoothingIterations = 2,
SplineParameters = new SplineRoadParameters {
    SmoothingWindowSize = 251,
    UseButterworthFilter = true,
    ButterworthFilterOrder = 4,
    GlobalLevelingStrength = 0.9f  // Strong leveling
}
```

---

## Parameter Dependencies

### ?? Critical Warnings

1. **High GlobalLevelingStrength requires wide TerrainAffectedRangeMeters:**
   - If `GlobalLevelingStrength > 0.5`, set `TerrainAffectedRangeMeters ? 20.0`
   - Otherwise: **DOTTED ROAD ARTIFACTS** (disconnected segments)

2. **CrossSectionIntervalMeters must be small enough:**
   - Should be ? `(RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3`
   - Otherwise: **GAP ARTIFACTS** (dotted roads)
   - Code auto-warns and adjusts if too large

3. **SmoothingMaskExtensionMeters must fit within blend zone:**
   - Should be ? `TerrainAffectedRangeMeters`
   - Otherwise: Smoothing extends beyond blended area (ineffective)

4. **Kernel sizes must be odd:**
   - `SmoothingKernelSize` must be 3, 5, 7, 9, 11, 13, or 15
   - Even numbers (4, 6, 8) will cause errors

---

## Troubleshooting Guide

### Problem: Road has visible "steps" or "bumps"
**Solution:** Enable post-processing smoothing:
```csharp
EnablePostProcessingSmoothing = true,
SmoothingKernelSize = 9,
SmoothingSigma = 2.0f
```

### Problem: Road looks "dotted" (disconnected segments)
**Solution 1:** Reduce `CrossSectionIntervalMeters` (auto-warns)  
**Solution 2:** If using `GlobalLevelingStrength > 0.5`, increase `TerrainAffectedRangeMeters` to 20-25m

### Problem: Road is too smooth (looks artificial)
**Solution:** Reduce smoothing:
```csharp
SmoothingWindowSize = 51,  // Reduce from 201
SmoothingSigma = 1.0f,     // Reduce from 1.5
SmoothingKernelSize = 5    // Reduce from 7
```

### Problem: Road doesn't follow terrain
**Solution:** Reduce global leveling:
```csharp
GlobalLevelingStrength = 0.0f  // Terrain-following mode
```

### Problem: Processing is too slow
**Solution 1:** Increase `CrossSectionIntervalMeters` to 1.0  
**Solution 2:** Reduce `SmoothingWindowSize` to 51  
**Solution 3:** Use `DirectMask` approach for faster processing  
**Solution 4:** Disable post-processing: `EnablePostProcessingSmoothing = false`

### Problem: Sharp corners aren't smooth
**Solution:** Increase spline continuity:
```csharp
SplineContinuity = 0.8f  // Increase from 0.5
SplineTension = 0.2f     // Decrease from 0.3
```

---

## Summary

This document covers **ALL 40+ parameters** in the road smoothing system. Most users only need to adjust 5-10 key parameters, while the rest can be left at defaults.

**Key Takeaways:**
- ? **Most parameters are actively used** and serve important purposes
- ?? **Start with presets** (`RoadSmoothingPresets.TerrainFollowingSmooth`)
- ? **Enable post-processing smoothing** to eliminate staircase artifacts
- ?? **Watch for critical warnings** (global leveling + narrow blend = dotted roads!)
- ?? **Fine-tune only when needed** - defaults work well for most cases
