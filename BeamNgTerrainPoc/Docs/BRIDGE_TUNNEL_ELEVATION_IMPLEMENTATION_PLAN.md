# Structure Elevation Profiles Implementation Plan

## Overview

This document outlines the implementation plan for calculating elevation profiles for bridge and tunnel structures. This is a **follow-on feature** to the core bridge/tunnel detection implemented in `BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md`.

**Prerequisites**: The following must be implemented first (see `BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md`):
- Bridge/tunnel detection via OSM tags on `OsmFeature`
- `IsBridge`/`IsTunnel` flags on `ParameterizedRoadSpline`
- Exclusion from terrain smoothing and material painting
- Configuration options (`ExcludeBridgesFromTerrain`, `ExcludeTunnelsFromTerrain`)

### Goals

1. **Calculate elevation profiles** for bridges and tunnels that are independent of terrain
2. **Store elevation data** for future procedural DAE geometry generation
3. **Handle entry/exit transitions** where structures connect to regular roads

## Implementation Status

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 | 🔲 TODO | Elevation Profile Data Model |
| Phase 2 | 🔲 TODO | Bridge Elevation Calculation |
| Phase 3 | 🔲 TODO | Tunnel Elevation Calculation |
| Phase 4 | 🔲 TODO | Terrain Sampling Along Structure Path |
| Phase 5 | 🔲 TODO | Integration with Cross-Section Generation |
| Phase 6 | 🔲 TODO | Configuration Parameters |

---

## How Bridges and Tunnels Handle Elevation

Unlike regular road splines that follow terrain, **bridges and tunnels have independent elevation profiles** that:
- Start at a known entry elevation (where they connect to regular road)
- End at a known exit elevation (where they reconnect to regular road)
- Follow a smooth curve between these points
- For tunnels: ensure adequate underground clearance

---

## Phase 1: Elevation Profile Data Model

### Step 1.1: Create StructureElevationProfile

**File**: `BeamNgTerrainPoc/Terrain/Osm/Models/StructureElevationProfile.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Defines the elevation profile for a bridge or tunnel structure.
/// </summary>
public class StructureElevationProfile
{
    /// <summary>Entry point elevation (where structure meets road at start).</summary>
    public float EntryElevation { get; set; }
    
    /// <summary>Exit point elevation (where structure meets road at end).</summary>
    public float ExitElevation { get; set; }
    
    /// <summary>Total length of the structure in meters.</summary>
    public float LengthMeters { get; set; }
    
    /// <summary>Type of elevation curve to use.</summary>
    public StructureElevationCurveType CurveType { get; set; }
    
    /// <summary>
    /// For tunnels: minimum clearance below terrain surface (default 5m).
    /// For bridges: minimum clearance above obstacle (water, road, etc.).
    /// </summary>
    public float MinimumClearanceMeters { get; set; } = 5.0f;
    
    /// <summary>
    /// Terrain elevations sampled along the structure centerline.
    /// Used for tunnel depth calculations.
    /// </summary>
    public float[]? TerrainElevationsAlongPath { get; set; }
    
    /// <summary>
    /// Calculated lowest point elevation for the structure.
    /// For tunnels: ensures this is at least MinimumClearanceMeters below terrain.
    /// </summary>
    public float CalculatedLowestPointElevation { get; set; }
}

/// <summary>
/// Types of elevation curves for structures.
/// </summary>
public enum StructureElevationCurveType
{
    /// <summary>Flat profile - constant grade from entry to exit.</summary>
    Linear,
    
    /// <summary>Smooth parabolic curve (sag or crest).</summary>
    Parabolic,
    
    /// <summary>S-curve for tunnels - descent, level, ascent.</summary>
    SCurve,
    
    /// <summary>Symmetric arch for bridges.</summary>
    Arch
}
```

### Step 1.2: Extend ParameterizedRoadSpline

**File**: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`

Add elevation profile storage:

```csharp
public class ParameterizedRoadSpline
{
    // ... existing properties (IsBridge, IsTunnel, etc.) ...
    
    /// <summary>
    /// Elevation profile for bridges/tunnels.
    /// Null for regular road splines.
    /// </summary>
    public StructureElevationProfile? ElevationProfile { get; set; }
}
```

---

## Phase 2: Bridge Elevation Calculation

**Bridges** typically have one of these profiles based on length:

```
SHORT BRIDGE (< 50m) - Linear Profile
════════════════════════════════════════════════════════
Entry ─────────────────────────────────────────── Exit
       Flat or constant grade between endpoints

MEDIUM BRIDGE (50-200m) - Slight Sag Curve  
════════════════════════════════════════════════════════
Entry ──────╲                           ╱────── Exit
             ╲─────────────────────────╱
              Gentle sag for drainage (0.5-1% dip at center)

LONG BRIDGE (> 200m) - Arch Profile
════════════════════════════════════════════════════════
                    ╱─────╲
Entry ─────────────╱       ╲─────────────── Exit
       Gradual rise to center, then descent
       (for cable-stayed/suspension bridges)
```

### Step 2.1: Bridge Elevation Algorithm

**File**: `BeamNgTerrainPoc/Terrain/Osm/Processing/StructureElevationCalculator.cs`

```csharp
public float CalculateBridgeElevation(float distanceAlongStructure, StructureElevationProfile profile)
{
    float t = distanceAlongStructure / profile.LengthMeters;  // 0 to 1
    float baseElevation = Lerp(profile.EntryElevation, profile.ExitElevation, t);
    
    return profile.CurveType switch
    {
        StructureElevationCurveType.Linear => baseElevation,
        
        StructureElevationCurveType.Parabolic => 
            // Sag curve: lowest at center (for drainage)
            baseElevation - SagCurveOffset(t, profile.LengthMeters),
            
        StructureElevationCurveType.Arch =>
            // Arch: highest at center
            baseElevation + ArchCurveOffset(t, profile.LengthMeters),
            
        _ => baseElevation
    };
}

// Sag curve: parabola with vertex at t=0.5
private float SagCurveOffset(float t, float length)
{
    // Max sag = 0.5% of length, capped at 2m
    float maxSag = Math.Min(length * 0.005f, 2.0f);
    // Parabola: 4 * maxSag * t * (1 - t) peaks at t=0.5
    return 4f * maxSag * t * (1f - t);
}

// Arch curve: for long bridges
private float ArchCurveOffset(float t, float length)
{
    if (length < 200f) return 0f;  // No arch for short bridges
    // Max rise = 1% of length, capped at 10m
    float maxRise = Math.Min(length * 0.01f, 10.0f);
    return 4f * maxRise * t * (1f - t);
}
```

---

## Phase 3: Tunnel Elevation Calculation

**Tunnels** have more complex requirements:
1. Must maintain minimum clearance below terrain surface
2. Entry/exit elevations set by connecting roads
3. May need to go deeper than entry/exit to clear terrain
4. Need smooth transition curves (no sudden drops)

```
SHORT TUNNEL (< 100m) - Simple Through-Cut
════════════════════════════════════════════════════════
Terrain:    ████████████████████████████
            ████████████████████████████
Entry ──────────────────────────────────────── Exit
            ▓▓▓▓▓▓▓▓ TUNNEL ▓▓▓▓▓▓▓▓▓
            (Linear interpolation, ensure 5m clearance)

MEDIUM TUNNEL (100-500m) - Descent and Ascent
════════════════════════════════════════════════════════
Terrain:         ████████████████████
                █████████████████████████
Entry ────╲                              ╱──── Exit
           ╲────────────────────────────╱
            ▓▓▓▓▓▓▓▓▓▓ TUNNEL ▓▓▓▓▓▓▓▓▓▓
            (S-curve to go under terrain peak)

LONG TUNNEL (> 500m) - Deep Profile
════════════════════════════════════════════════════════
Terrain:    ████████████████████████████████████
            ███████████████████████████████████████
Entry ────╲                                    ╱──── Exit
           ╲                                  ╱
            ╲────────────────────────────────╱
             ▓▓▓▓▓▓▓▓▓▓▓▓▓ TUNNEL ▓▓▓▓▓▓▓▓▓▓▓▓
             (Gradual descent, level section, gradual ascent)
```

### Step 3.1: Tunnel Profile Calculation

```csharp
public StructureElevationProfile CalculateTunnelProfile(
    ParameterizedRoadSpline tunnelSpline,
    float entryElevation,
    float exitElevation,
    float[] terrainElevationsAlongPath,
    float minClearance = 5.0f,
    float maxGradePercent = 6.0f)
{
    var profile = new StructureElevationProfile
    {
        EntryElevation = entryElevation,
        ExitElevation = exitElevation,
        LengthMeters = tunnelSpline.Spline.TotalLength,
        MinimumClearanceMeters = minClearance,
        TerrainElevationsAlongPath = terrainElevationsAlongPath
    };
    
    // Find the maximum terrain elevation along the tunnel path
    float maxTerrainElevation = terrainElevationsAlongPath.Max();
    
    // Required tunnel floor elevation to maintain clearance
    // (assuming 5m tunnel height, need 5m clearance above tunnel ceiling)
    float tunnelHeight = 5.0f;
    float requiredFloorElevation = maxTerrainElevation - minClearance - tunnelHeight;
    
    // Check if linear interpolation between entry/exit provides enough depth
    float midpointLinear = (entryElevation + exitElevation) / 2f;
    float midpointTerrain = terrainElevationsAlongPath[terrainElevationsAlongPath.Length / 2];
    
    if (midpointLinear <= midpointTerrain - minClearance - tunnelHeight)
    {
        // Linear profile is deep enough
        profile.CurveType = StructureElevationCurveType.Linear;
        profile.CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation);
    }
    else
    {
        // Need to go deeper - use S-curve
        profile.CurveType = StructureElevationCurveType.SCurve;
        profile.CalculatedLowestPointElevation = CalculateRequiredLowestPoint(
            terrainElevationsAlongPath, minClearance, tunnelHeight);
        
        // Validate that the required grade is achievable
        ValidateTunnelGrade(profile, maxGradePercent);
    }
    
    return profile;
}
```

### Step 3.2: Tunnel Elevation at Distance

```csharp
public float CalculateTunnelElevation(float distanceAlongStructure, StructureElevationProfile profile)
{
    float t = distanceAlongStructure / profile.LengthMeters;  // 0 to 1
    
    return profile.CurveType switch
    {
        StructureElevationCurveType.Linear => 
            Lerp(profile.EntryElevation, profile.ExitElevation, t),
            
        StructureElevationCurveType.SCurve =>
            CalculateSCurveElevation(t, profile),
            
        _ => Lerp(profile.EntryElevation, profile.ExitElevation, t)
    };
}

/// <summary>
/// S-curve profile for tunnels that need to dip below terrain.
/// Divides tunnel into: descent (25%), level (50%), ascent (25%)
/// </summary>
private float CalculateSCurveElevation(float t, StructureElevationProfile profile)
{
    float lowestPoint = profile.CalculatedLowestPointElevation;
    
    if (t <= 0.25f)
    {
        // Descent phase: smooth transition from entry to lowest point
        float localT = t / 0.25f;  // 0 to 1 within this phase
        float smoothT = SmoothStep(localT);  // Ease in/out
        return Lerp(profile.EntryElevation, lowestPoint, smoothT);
    }
    else if (t <= 0.75f)
    {
        // Level phase: constant elevation at lowest point
        return lowestPoint;
    }
    else
    {
        // Ascent phase: smooth transition from lowest point to exit
        float localT = (t - 0.75f) / 0.25f;  // 0 to 1 within this phase
        float smoothT = SmoothStep(localT);
        return Lerp(lowestPoint, profile.ExitElevation, smoothT);
    }
}

// Smooth interpolation (ease in/out)
private float SmoothStep(float t) => t * t * (3f - 2f * t);
```

---

## Phase 4: Sampling Terrain Along Structure Path

Before calculating tunnel profiles, we need terrain elevations along the path:

```csharp
public float[] SampleTerrainAlongStructure(
    ParameterizedRoadSpline structureSpline,
    float[,] heightMap,
    float metersPerPixel,
    int sampleCount = 20)
{
    var elevations = new float[sampleCount];
    
    for (int i = 0; i < sampleCount; i++)
    {
        float t = i / (float)(sampleCount - 1);
        
        // Sample position along spline
        var sample = structureSpline.Spline.SampleAt(t);
        
        // Convert to heightmap coordinates
        int pixelX = (int)(sample.Position.X / metersPerPixel);
        int pixelY = (int)(sample.Position.Y / metersPerPixel);
        
        // Sample terrain (with bounds checking)
        elevations[i] = SampleHeightmapSafe(heightMap, pixelX, pixelY);
    }
    
    return elevations;
}

private float SampleHeightmapSafe(float[,] heightMap, int x, int y)
{
    int width = heightMap.GetLength(0);
    int height = heightMap.GetLength(1);
    
    x = Math.Clamp(x, 0, width - 1);
    y = Math.Clamp(y, 0, height - 1);
    
    return heightMap[x, y];
}
```

---

## Phase 5: Integration with Cross-Section Generation

When generating cross-sections for structure splines:

```csharp
// In UnifiedRoadNetworkBuilder or cross-section generation:

foreach (var spline in network.Splines.Where(s => s.IsStructure))
{
    // Get entry/exit elevations from connecting road splines
    float entryElev = GetConnectingRoadElevation(spline, isEntry: true);
    float exitElev = GetConnectingRoadElevation(spline, isEntry: false);
    
    // Sample terrain along structure for tunnel calculations
    var terrainSamples = SampleTerrainAlongStructure(spline, heightMap, metersPerPixel);
    
    // Calculate elevation profile for this structure
    var profile = spline.IsTunnel 
        ? CalculateTunnelProfile(spline, entryElev, exitElev, terrainSamples)
        : CalculateBridgeProfile(spline, entryElev, exitElev);
    
    // Store profile for later use (DAE generation)
    spline.ElevationProfile = profile;
    
    // Generate cross-sections with calculated elevations
    foreach (var crossSection in GetCrossSectionsForSpline(spline.SplineId))
    {
        float distance = crossSection.DistanceAlongSpline;
        
        // Override terrain-based elevation with structure elevation
        crossSection.TargetElevation = spline.IsTunnel
            ? CalculateTunnelElevation(distance, profile)
            : CalculateBridgeElevation(distance, profile);
        
        // Mark as structure (already done by base implementation)
        crossSection.IsStructure = true;
    }
}

private float GetConnectingRoadElevation(ParameterizedRoadSpline structureSpline, bool isEntry)
{
    // Find the road spline that connects at the entry/exit point
    // Return its elevation at the connection point
    // Fall back to terrain elevation if no connecting road found
    // Implementation depends on junction detection data
}
```

---

## Phase 6: Configuration Parameters

**File**: `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

```csharp
// ========================================
// STRUCTURE ELEVATION PARAMETERS
// ========================================

/// <summary>
/// Minimum vertical clearance for tunnels below terrain surface (meters).
/// This is the distance from terrain surface to tunnel ceiling.
/// Default: 5.0m (reasonable rock/soil cover)
/// </summary>
public float TunnelMinClearanceMeters { get; set; } = 5.0f;

/// <summary>
/// Assumed tunnel interior height (floor to ceiling) in meters.
/// Used to calculate required floor elevation from clearance.
/// Default: 5.0m (standard road tunnel height)
/// </summary>
public float TunnelInteriorHeightMeters { get; set; } = 5.0f;

/// <summary>
/// Maximum grade (slope) percentage allowed for tunnel approaches.
/// Steeper grades may be uncomfortable or unsafe for vehicles.
/// Default: 6.0% (typical maximum for road tunnels)
/// </summary>
public float TunnelMaxGradePercent { get; set; } = 6.0f;

/// <summary>
/// Minimum clearance for bridges above the obstacle (water, road, etc.).
/// This affects bridge deck elevation calculation.
/// Default: 5.0m (reasonable clearance for most obstacles)
/// </summary>
public float BridgeMinClearanceMeters { get; set; } = 5.0f;
```

---

## Data Flow Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    STRUCTURE ELEVATION PROFILE FLOW                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  1. INPUT: Marked Structure Splines (from BRIDGE_TUNNEL_IMPLEMENTATION_PLAN) │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ ParameterizedRoadSpline                            │                  │
│     │ - IsBridge = true OR IsTunnel = true               │                  │
│     │ - StructureType set                                │                  │
│     │ - Layer set                                        │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  2. SAMPLE TERRAIN ALONG PATH                                                │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ SampleTerrainAlongStructure()                      │                  │
│     │ - Sample heightmap at N points along spline        │                  │
│     │ - Output: float[] terrain elevations               │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  3. DETERMINE ENTRY/EXIT ELEVATIONS                                          │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ GetConnectingRoadElevation()                       │                  │
│     │ - Find connecting road splines at structure ends   │                  │
│     │ - Get their elevation at connection point          │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  4. CALCULATE ELEVATION PROFILE                                              │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ Bridge: CalculateBridgeProfile()                   │                  │
│     │ - Determine curve type (Linear/Parabolic/Arch)     │                  │
│     │ - Based on length                                  │                  │
│     ├────────────────────────────────────────────────────┤                  │
│     │ Tunnel: CalculateTunnelProfile()                   │                  │
│     │ - Check if linear profile has enough clearance     │                  │
│     │ - If not, use S-curve with calculated depth        │                  │
│     │ - Validate max grade constraint                    │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  5. STORE PROFILE ON SPLINE                                                  │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ spline.ElevationProfile = profile                  │                  │
│     │ - Available for cross-section generation           │                  │
│     │ - Available for future DAE generation              │                  │
│     └────────────────────────┬───────────────────────────┘                  │
│                              │                                               │
│  6. OUTPUT: Cross-Sections with Structure Elevations                         │
│                              ▼                                               │
│     ┌────────────────────────────────────────────────────┐                  │
│     │ crossSection.TargetElevation = calculated value    │                  │
│     │ - Used for future DAE mesh generation              │                  │
│     │ - NOT used for terrain modification (excluded)     │                  │
│     └────────────────────────────────────────────────────┘                  │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## File Changes Summary

```
BeamNgTerrainPoc/
├── Terrain/
│   ├── Osm/
│   │   ├── Models/
│   │   │   └── StructureElevationProfile.cs    (NEW)
│   │   └── Processing/
│   │       └── StructureElevationCalculator.cs (NEW)
│   └── Models/
│       ├── TerrainCreationParameters.cs        (MODIFIED - add elevation params)
│       └── RoadGeometry/
│           └── ParameterizedRoadSpline.cs      (MODIFIED - add ElevationProfile)
└── Docs/
    └── BRIDGE_TUNNEL_ELEVATION_IMPLEMENTATION_PLAN.md (THIS FILE)
```

---

## Testing Strategy

### Unit Tests

1. **Bridge Elevation Calculation**
   - Linear profile for short bridges
   - Sag curve offset calculation
   - Arch curve for long bridges
   - Boundary conditions (t=0, t=0.5, t=1)

2. **Tunnel Elevation Calculation**
   - Linear profile when naturally deep enough
   - S-curve calculation when additional depth needed
   - Grade validation
   - Clearance calculation

3. **Terrain Sampling**
   - Correct sampling along spline path
   - Bounds checking for edge cases

### Integration Tests

1. **Profile Assignment**
   - Verify profiles are calculated for all structure splines
   - Verify profiles are stored on splines
   - Verify cross-sections receive calculated elevations

2. **Visual Verification**
   - Export elevation profile debug data
   - Verify tunnel depth is sufficient
   - Verify bridge profiles are reasonable

---

## Future Extensions (Out of Scope)

1. **Procedural Bridge DAE Generation**
   - Use elevation profile for deck positioning
   - Generate supports based on profile

2. **Procedural Tunnel DAE Generation**
   - Use elevation profile for tunnel floor
   - Generate portal geometry at entry/exit

3. **Multi-Span Bridge Support**
   - Handle bridges with intermediate supports
   - Variable elevation profiles per span

---

## Dependencies

### Prerequisites (from BRIDGE_TUNNEL_IMPLEMENTATION_PLAN.md)
- `OsmFeature.IsBridge` / `OsmFeature.IsTunnel` properties
- `ParameterizedRoadSpline.IsBridge` / `IsTunnel` flags
- `UnifiedCrossSection.IsStructure` flag
- Configuration: `ExcludeBridgesFromTerrain`, `ExcludeTunnelsFromTerrain`

### Existing Dependencies (No Changes)
- `UnifiedRoadNetwork` - Road network container
- `ParameterizedRoadSpline` - Spline with parameters
- Heightmap data for terrain sampling

### New Dependencies
- None required - all functionality builds on existing infrastructure
