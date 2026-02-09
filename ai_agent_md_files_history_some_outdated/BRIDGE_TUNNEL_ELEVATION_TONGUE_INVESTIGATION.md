# Bridge/Tunnel Elevation Tongue Investigation

## Problem Statement

When bridges and tunnels are excluded from terrain processing (via `ExcludeBridgesFromTerrain` / `ExcludeTunnelsFromTerrain` flags), unpainted elevation "tongues" appear at structure endpoints.

**Visual symptoms:**
- Elevation changes WITHOUT material painting (grey asphalt material doesn't extend over the sandy river)
- Tongues extend INTO the bridge area, out of the road area
- Creates visible terrain deformation where excluded structures should leave terrain unchanged

## Root Cause Analysis

The elevation tongues are caused by blend zones from adjacent non-excluded roads extending into areas where excluded structures exist. The current exclusion system:

1. Marks bridge/tunnel cross-sections with `IsExcluded = true`
2. Filters excluded cross-sections from elevation calculations
3. **BUT** blend zones from connecting roads still calculate elevation values in those areas

## Approaches Attempted

### Approach 1: Exclusion Boundary Spatial Index (FAILED)

**Implementation:** Created `ExclusionBoundarySpatialIndex.cs` to detect pixels "beyond" excluded road boundaries using directional projection.

**Files modified:**
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ExclusionBoundarySpatialIndex.cs` (NEW)
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs`

**Issues encountered:**
1. **Inverted directional logic:** Initially had start/end projection checks backwards
   - Fixed: Start boundary suppresses projection > 0, end boundary suppresses projection < 0
2. **Insufficient spatial coverage:** Boundaries only indexed in single grid cell
   - Fixed: Extended to index all grid cells within influence radius
3. **Still didn't work:** Only 1-2 pixels suppressed despite fix

**Console output:**
```
[19:18:28.216] ExclusionBoundarySpatialIndex: Indexed 2 excluded road endpoints
[19:18:29.108] Suppressing pixel at (876,0, 1280,0): Beyond start of excluded spline 42, dist=12,5m, projection=0,7m
```

Only one suppression message appeared, indicating the approach wasn't catching most pixels.

### Approach 2: Protection Mask Extension (FAILED)

**Implementation:** Extended `RoadMaskBuilder.BuildRoadCoreProtectionMaskWithOwnership()` to rasterize excluded road areas into the protection mask.

**Rationale:** Simpler approach - mark excluded road pixels as protected so blend zones can't modify them at all.

**Files modified:**
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

**Code added:**
```csharp
// Second pass: Protect excluded road areas (bridges/tunnels) from blend zone modification
var excludedCrossSectionsBySpline = network.CrossSections
    .Where(cs => cs.IsExcluded)
    .GroupBy(cs => cs.OwnerSplineId)
    .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

foreach (var (splineId, crossSections) in excludedCrossSectionsBySpline)
{
    if (crossSections.Count < 2) continue;

    // Rasterize polygon segments into protection mask
    for (var i = 0; i < crossSections.Count - 1; i++)
    {
        var filled = FillConvexPolygonProtectionOnly(protectionMask, corners, width, height);
        excludedPixelsProtected += filled;
    }
}
```

**Console output:**
```
[19:34:27.699] Protection mask: 425.195 road core pixels protected
[19:34:27.699] Excluded roads: 210 pixels protected from blend zones
```

**Critical finding:** Only **210 pixels** protected for excluded roads - far too few!

A typical 50-100m bridge should protect thousands of pixels, not hundreds.

## Current Diagnostic State

**Latest build includes enhanced logging** to diagnose why protection coverage is so sparse:

```csharp
TerrainCreationLogger.Current?.Detail(
    $"Found {excludedCrossSectionsBySpline.Count} excluded splines with total " +
    $"{excludedCrossSectionsBySpline.Sum(kvp => kvp.Value.Count)} excluded cross-sections");

foreach (var (splineId, crossSections) in excludedCrossSectionsBySpline)
{
    TerrainCreationLogger.Current?.Detail(
        $"Excluded spline {splineId}: {crossSections.Count} cross-sections");

    // ... after rasterization ...

    TerrainCreationLogger.Current?.Detail(
        $"  Spline {splineId}: Protected {splinePixelsProtected} pixels from {crossSections.Count - 1} segments");
}
```

**Expected output patterns:**
- If only 1-2 excluded splines exist: Not enough structures marked for exclusion
- If excluded splines have < 2 cross-sections: Can't form polygon segments (will be skipped)
- If cross-sections are very sparse: Large gaps → few polygon segments → low pixel coverage

## Key Statistics from Last Run

```
Total cross-sections: 118,785
Total splines: 223
Splines with banking: 211
Excluded road pixels protected: 210 (SUSPICIOUSLY LOW)
Road core pixels protected: 425,195
```

## Hypothesis for Low Protection Count

**Most likely:** Excluded bridges/tunnels have very sparse cross-section sampling:
- OSM data typically generates cross-sections every ~10-20 meters
- A 50m bridge with 10m spacing = only 5-6 cross-sections = 4-5 polygon segments
- If cross-sections are at 20m spacing, a 50m bridge = only 2-3 cross-sections = 1-2 segments
- Each segment might only be 10-20 pixels wide × 20 pixels long = ~200-400 pixels per segment
- **This matches the 210 pixels observed!**

## Next Steps for Investigation

### 1. Collect Full Diagnostic Output
Run terrain generation with diagnostic logging and capture:
```
Found N excluded splines with total M excluded cross-sections
Excluded spline X: Y cross-sections
  Skipping spline X: need at least 2 cross-sections, has 1
  Spline X: Protected Z pixels from W segments
```

### 2. Verify Exclusion Metadata
Check if bridges/tunnels are properly tagged:
- In OSM data: `bridge=yes`, `tunnel=yes` tags
- In spline metadata: `IsBridge`, `IsTunnel` flags set correctly
- In configuration: `ExcludeBridgesFromTerrain`, `ExcludeTunnelsFromTerrain` enabled

### 3. Increase Cross-Section Density (Potential Fix)
If sparse cross-sections are confirmed as the issue:
- Modify road spline generation to add more cross-sections for excluded structures
- Target: 1 cross-section every 2-5 meters instead of 10-20 meters
- Location: Where OSM roads are converted to splines with cross-sections

### 4. Alternative: Extend Protection Beyond Polygon Segments
Instead of only protecting rasterized polygon segments:
- For each excluded spline, calculate its full bounding box or path
- Protect all pixels within road width + buffer along entire spline length
- Don't rely on consecutive cross-section pairs

### 5. Consider Post-Processing Material Application
Instead of preventing elevation changes:
- Let terrain blend normally
- Apply excluded structure materials AFTER terrain blending
- This would paint over any elevation tongues with the structure material

## File Reference

**Modified files in current implementation:**
- `d:\Source\beamng_mapping_pro\BeamNgTerrainPoc\Terrain\Algorithms\Blending\RoadMaskBuilder.cs`
  - Lines 168-235: Second pass for excluded road protection
  - Lines 373-413: `FillConvexPolygonProtectionOnly()` helper method

**Files with unused code (can be cleaned up):**
- `d:\Source\beamng_mapping_pro\BeamNgTerrainPoc\Terrain\Algorithms\Blending\ExclusionBoundarySpatialIndex.cs`
  - Created but not currently used
  - Contains boundary detection logic that didn't work

## Code Architecture Notes

### How Protection Mask Works
1. `RoadMaskBuilder.BuildRoadCoreProtectionMaskWithOwnership()` creates a boolean mask
2. `protectionMask[y, x] = true` means pixel is protected from blend zone modification
3. In `ElevationMapBuilder`, pixels with `protectionMask[y, x] == true` are skipped:
   ```csharp
   if (protectionMask[y, x])
       continue; // Skip protected pixels
   ```

### How Cross-Sections Form Road Geometry
```
Cross-section at position i:
  CenterPoint: road centerline position
  NormalDirection: perpendicular to road (points right)
  EffectiveRoadWidth: total road width

Polygon segment from CS[i] to CS[i+1]:
  Corner 1: CS[i].CenterPoint - NormalDirection * HalfWidth
  Corner 2: CS[i].CenterPoint + NormalDirection * HalfWidth
  Corner 3: CS[i+1].CenterPoint + NormalDirection * HalfWidth
  Corner 4: CS[i+1].CenterPoint - NormalDirection * HalfWidth
```

Each consecutive pair forms one quadrilateral road segment.

### Why Sparse Cross-Sections = Low Protection
```
Example: 50m bridge with 20m cross-section spacing
  Cross-sections at: 0m, 20m, 40m (3 total)
  Polygon segments: 0m→20m, 20m→40m (2 segments)
  Each segment: ~10m wide × 20m long = ~200 pixels
  Total protected: ~400 pixels
```

This matches the observed ~210 pixels, suggesting **only 1 polygon segment is being formed** (possibly only 2 cross-sections found).

## Questions for Next Session

1. **How many excluded splines are found?** (from diagnostic output)
2. **How many cross-sections per excluded spline?** (need ≥2 to form segments)
3. **Where are cross-sections generated for OSM roads?** (to increase density)
4. **Are IsBridge/IsTunnel flags set correctly on splines?** (verify exclusion metadata)
5. **Should we densify cross-sections or use a different protection strategy?**

## Relevant Configuration

**User's settings:**
- Bridges/tunnels excluded from terrain processing
- OSM data source (not PNG skeleton extraction)
- ~120K total cross-sections across ~220 splines
- 1m/pixel terrain resolution

## Contact/Session Info

**Branch:** `feature/bridge_tunnel_splines`
**Main branch:** `develop`
**Session date:** 2026-01-31
**Build status:** ✅ Compiles successfully with diagnostic logging
