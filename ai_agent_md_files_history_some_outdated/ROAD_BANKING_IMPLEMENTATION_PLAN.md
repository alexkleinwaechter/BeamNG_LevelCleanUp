# Road Banking (Superelevation) Implementation Plan

## Overview

This document provides a comprehensive implementation plan for adding road banking (superelevation) support to the BeamNG terrain generation system. Road banking tilts the road surface on curves to counteract centrifugal forces, improving vehicle handling and realism.

> ?? **CRITICAL DESIGN DECISION: Junction Harmonization & Priority-Based Banking**
> 
> The existing system has sophisticated junction harmonization that creates smooth elevation transitions where roads meet. Banking MUST work correctly with this system!
> 
> **Key Principle: Higher-priority roads maintain their banking. Lower-priority roads adapt.**
> 
> | Scenario | Higher-Priority Road | Lower-Priority Road |
> |----------|---------------------|---------------------|
> | Highway × Dirt Road | ? Maintains full banking | ?? Adapts to match banked highway |
> | Primary × Secondary | ? Maintains full banking | ?? Adapts to match |
> | Same-Priority Roads | ?? Both reduce to flat | ?? Both reduce to flat |
> | Endpoint (dead end) | ?? Fades to flat | N/A |
> 
> **Why?** A highway driver at 130 km/h should NOT suddenly hit a flat curve because a dirt road crosses! The highway maintains banking, and the dirt road adapts its edge elevations to smoothly meet the banked highway surface.
>
> See **Phase 5.5: Priority-Aware Junction Banking** for implementation details.

### How Road Banking Works in Real Life

**Physics of Banking:**
- On a curved road, vehicles experience centrifugal force pushing them outward
- Banking tilts the road surface so that the normal force from the road partially counteracts this outward force
- Optimal banking angle depends on: curve radius, vehicle speed, and friction coefficient
- Formula: `tan(?) = v²/(r×g)` where ? = bank angle, v = speed, r = radius, g = gravity

**Real-World Constraints:**
- Highway design standards (AASHTO, European norms) specify maximum superelevation rates
- Typical ranges: 4-12% (2.3° to 6.9°) for highways, up to 15% for racetracks
- Transition zones (spiral curves) gradually introduce banking before/after curves
- Lower banking in urban areas due to low speeds and frequent stops
- Drainage requirements prevent banking on flat curves (minimum 2% cross-slope)

**Design Speed Considerations:**
- Motorways (130 km/h): Higher banking allowed for high-speed comfort
- Rural roads (80 km/h): Moderate banking
- Urban roads (50 km/h): Minimal banking, often just drainage slope
- Mountain switchbacks: Maximum banking for tight, slow curves

## Current System Architecture

> ?? **CODE ORGANIZATION GUIDELINE**
> 
> Due to the large file sizes of some existing code files (e.g., `UnifiedTerrainBlender.cs`, `UnifiedRoadSmoother.cs`), 
> **all new banking functionality MUST be implemented in separate, dedicated files**. This improves:
> - Maintainability and readability
> - Code review efficiency
> - Unit test isolation
> - Merge conflict reduction
>
> **Rule: One class per file, one responsibility per class.**

### Key Components

1. **`RoadSpline`** (`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`)
   - Stores control points and provides interpolation
   - Calculates position, tangent, and **normal** at any distance along spline
   - `GetNormalAtDistance()` returns the 2D perpendicular vector

2. **`SplineSample`** (same file)
   - Data structure containing: Distance, Position, Tangent, Normal
   - Used for cross-section generation

3. **`UnifiedCrossSection`** (`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`)
   - Contains `NormalDirection` and `TangentDirection` as 2D vectors
   - `TargetElevation` is currently a single float (assumes flat road cross-section)

4. **`ParameterizedRoadSpline`** (`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`)
   - Links `RoadSpline` with `RoadSmoothingParameters`
   - Perfect place to add per-spline banking overrides

5. **`RoadSmoothingParameters`** (`BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`)
   - Contains per-material road settings
   - Has `SplineRoadParameters` sub-object for spline-specific settings

6. **`MasterSplineExporter`** (`BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs`)
   - Exports to BeamNG JSON format
   - Already has `autoBankFalloff`, `bankStrength`, `isAutoBanking` fields (unused)
   - `Nmls` array stores per-node normals (currently all `[0,0,1]`)

### Data Flow for Elevation

```
RoadSpline 
  ? SampleByDistance() 
  ? SplineSample[] 
  ? UnifiedCrossSection.FromSplineSample() 
  ? UnifiedCrossSection 
  ? OptimizedElevationSmoother.CalculateTargetElevations() 
  ? TargetElevation set
  ? UnifiedTerrainBlender.BlendNetworkWithTerrain()
  ? Heightmap modified
```

### Current Limitations

1. `NormalDirection` is 2D (X, Y only) - assumes road surface is always horizontal
2. `TargetElevation` is a single value - no left/right edge differentiation
3. No curvature calculation at cross-sections
4. No banking-aware terrain blending

---

## File Structure & Code Organization

> ?? **IMPORTANT**: Keep code modular! Each new class goes in its own file.

### New Files to Create

```
BeamNgTerrainPoc/
??? Terrain/
?   ??? Algorithms/
?   ?   ??? Banking/                          # NEW FOLDER for banking algorithms
?   ?   ?   ??? CurvatureCalculator.cs        # Step 2.1 - Curvature calculation
?   ?   ?   ??? BankingCalculator.cs          # Step 3.1 - Bank angle calculation
?   ?   ?   ??? BankingFalloffBlender.cs      # Step 3.1 - Extract falloff logic
?   ?   ?   ??? BankedNormalCalculator.cs     # Step 3.1 - Extract normal rotation
?   ?   ?   ??? BankedElevationCalculator.cs  # Step 4.1 - Edge elevations
?   ?   ?   ??? BankedTerrainHelper.cs        # Step 5.1 - Helper for blender
?   ?   ?
?   ?   ??? Junction/                         # NEW FOLDER for junction banking
?   ?       ??? JunctionBankingBehavior.cs    # Step 5.5.1 - Enum only
?   ?       ??? PriorityAwareJunctionBankingCalculator.cs  # Step 5.5.2
?   ?       ??? JunctionBankingAdapter.cs     # Step 5.5.4 - Edge adaptation
?   ?
?   ??? Models/
?   ?   ??? RoadGeometry/
?   ?   ?   ??? SplineBankingOverride.cs      # Step 9.1 - Override model
?   ?   ?   ??? CrossSectionBankingExtensions.cs  # Step 1.3 - Extension methods
?   ?   ?
?   ?   ??? BankingParameters.cs              # Step 1.1 - Extract to own class
?   ?
?   ??? Services/
?       ??? BankingOrchestrator.cs            # Step 6.1 - Coordinate all banking
?
BeamNG_LevelCleanUp/
??? BlazorUI/
    ??? Components/
        ??? BankingSettingsPanel.razor        # Step 8.1 - Separate UI component
```

### Existing Files to Modify (Minimal Changes Only)

| File | Change Type | Description |
|------|-------------|-------------|
| `SplineRoadParameters.cs` | Add property | `public BankingParameters? Banking { get; set; }` |
| `UnifiedCrossSection.cs` | Add properties | Banking-related properties (keep small) |
| `UnifiedRoadSmoother.cs` | Add 1 method call | `_bankingOrchestrator.ApplyBanking(network)` |
| `UnifiedTerrainBlender.cs` | Add 1 helper call | `BankedTerrainHelper.GetElevation(cs, offset)` |
| `MasterSplineExporter.cs` | Add 1 method call | `BankedNormalExporter.Export(spline)` |
| `TerrainMaterialSettings.razor` | Add 1 component | `<BankingSettingsPanel ... />` |

### Class Responsibilities (Single Responsibility Principle)

| Class | Single Responsibility |
|-------|----------------------|
| `CurvatureCalculator` | Calculate curvature at cross-sections |
| `BankingCalculator` | Calculate bank angles from curvature |
| `BankingFalloffBlender` | Apply distance-based falloff blending |
| `BankedNormalCalculator` | Rotate normals around tangent axis |
| `BankedElevationCalculator` | Calculate left/right edge elevations |
| `PriorityAwareJunctionBankingCalculator` | Determine junction behavior by priority |
| `JunctionBankingAdapter` | Adapt lower-priority road edges |
| `BankingOrchestrator` | Coordinate all banking calculations in correct order |
| `BankedTerrainHelper` | Static helpers for terrain blending |
| `BankingSettingsPanel` | UI component for banking settings |

### Why This Structure?

1. **Testability**: Each class can be unit tested in isolation
2. **Readability**: Small, focused files are easier to understand
3. **Maintainability**: Changes to curvature calculation don't risk breaking falloff blending
4. **Code Review**: Reviewers can approve/reject specific functionality
5. **Parallel Development**: Multiple developers can work on different banking components
6. **Reusability**: `CurvatureCalculator` could be reused for other purposes

### Anti-Patterns to Avoid

? **Don't**: Add 500+ lines to `UnifiedTerrainBlender.cs`
? **Do**: Create `BankedTerrainHelper.cs` with static methods, call from blender

? **Don't**: Put all banking logic in one giant `BankingService.cs`
? **Do**: Split into focused classes by responsibility

? **Don't**: Add banking UI directly to `TerrainMaterialSettings.razor`
? **Do**: Create `BankingSettingsPanel.razor` component and include it

? **Don't**: Modify `SplineSample` struct extensively (it's used everywhere)
? **Do**: Add minimal fields, use extension methods for complex operations

---

## Implementation Steps

### Phase 1: Data Structures (Foundation)

#### Step 1.1: Create `BankingParameters` Class

**New File:** `BeamNgTerrainPoc/Terrain/Models/BankingParameters.cs`

> ?? **Why a separate class?** Banking has 7+ parameters. Keeping them in a dedicated class:
> - Makes `SplineRoadParameters` cleaner
> - Allows banking-specific validation
> - Enables future serialization for presets

```csharp
namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters controlling road banking (superelevation) behavior.
/// Banking tilts the road surface on curves for improved vehicle handling.
/// </summary>
public class BankingParameters
{
// ========================================
// BANKING (SUPERELEVATION) PARAMETERS
// ========================================

/// <summary>
/// Enable automatic banking (superelevation) on curves.
/// When enabled, the road surface tilts based on curve curvature.
/// Default: false
/// </summary>
public bool EnableAutoBanking { get; set; } = false;

/// <summary>
/// Maximum bank angle in degrees.
/// Real-world highways typically use 4-8°, race tracks up to 15°.
/// Default: 8.0 (moderate banking suitable for highways)
/// </summary>
public float MaxBankAngleDegrees { get; set; } = 8.0f;

/// <summary>
/// Banking strength multiplier (0-1).
/// 0 = no banking, 1 = full banking based on curvature.
/// Use lower values for urban roads, higher for highways/race tracks.
/// Default: 0.5
/// </summary>
public float BankStrength { get; set; } = 0.5f;

/// <summary>
/// Controls how banking transitions at curve boundaries.
/// Higher values = sharper falloff (banking drops faster from curve apex).
/// Range: 0.3-2.0
/// Default: 0.6 (smooth transitions)
/// </summary>
public float AutoBankFalloff { get; set; } = 0.6f;

/// <summary>
/// Curvature scale factor for bank angle calculation.
/// Formula: bankAngle = min(curvature * CurvatureToBankScale, 1) * maxBankAngle
/// Higher values = more aggressive banking on gentle curves.
/// Default: 500.0 (empirically tuned for driving simulation)
/// </summary>
public float CurvatureToBankScale { get; set; } = 500.0f;

/// <summary>
/// Minimum curve radius (meters) below which maximum banking is applied.
/// Curves tighter than this get full MaxBankAngleDegrees.
/// Default: 50.0 (tight curves like hairpins)
/// </summary>
public float MinCurveRadiusForMaxBank { get; set; } = 50.0f;

/// <summary>
    /// Transition length (meters) for banking changes.
    /// Banking fades in/out over this distance at curve entries/exits.
    /// Default: 30.0 (smooth transitions)
    /// </summary>
    public float BankTransitionLengthMeters { get; set; } = 30.0f;

    /// <summary>
    /// Validates banking parameters and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (MaxBankAngleDegrees < 0 || MaxBankAngleDegrees > 45)
            errors.Add("MaxBankAngleDegrees must be between 0 and 45");
        if (BankStrength < 0 || BankStrength > 1)
            errors.Add("BankStrength must be between 0 and 1");
        if (AutoBankFalloff < 0.1f || AutoBankFalloff > 3.0f)
            errors.Add("AutoBankFalloff must be between 0.1 and 3.0");
        if (CurvatureToBankScale < 1 || CurvatureToBankScale > 2000)
            errors.Add("CurvatureToBankScale must be between 1 and 2000");
        if (BankTransitionLengthMeters < 1 || BankTransitionLengthMeters > 200)
            errors.Add("BankTransitionLengthMeters must be between 1 and 200");
            
        return errors;
    }

    /// <summary>
    /// Creates default banking parameters suitable for highways.
    /// </summary>
    public static BankingParameters Highway => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 8.0f,
        BankStrength = 0.7f,
        AutoBankFalloff = 0.5f
    };

    /// <summary>
    /// Creates default banking parameters suitable for race tracks.
    /// </summary>
    public static BankingParameters RaceTrack => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 15.0f,
        BankStrength = 1.0f,
        AutoBankFalloff = 0.4f
    };

    /// <summary>
    /// Creates default banking parameters for gentle rural roads.
    /// </summary>
    public static BankingParameters RuralRoad => new()
    {
        EnableAutoBanking = true,
        MaxBankAngleDegrees = 5.0f,
        BankStrength = 0.4f,
        AutoBankFalloff = 0.8f
    };
}
```

**Then update `SplineRoadParameters.cs`** to reference this class:

```csharp
// In SplineRoadParameters.cs, add:

/// <summary>
/// Banking (superelevation) parameters for curved roads.
/// Null = banking disabled.
/// </summary>
public BankingParameters? Banking { get; set; }

/// <summary>
/// Gets banking parameters, creating defaults if not set.
/// </summary>
public BankingParameters GetBankingParameters() => Banking ??= new BankingParameters();

/// <summary>
/// Convenience property: true if banking is enabled.
/// </summary>
public bool IsBankingEnabled => Banking?.EnableAutoBanking == true;
```

---

#### Step 1.2: Extend `SplineSample` for Banking Data

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`

Modify the `SplineSample` struct:

```csharp
/// <summary>
/// A sample point along the road spline with optional banking data.
/// </summary>
public struct SplineSample
{
    public float Distance;      // Distance along road from start
    public Vector2 Position;    // World position (X, Y in meters)
    public Vector2 Tangent;     // Direction of road (normalized, 2D)
    public Vector2 Normal;      // Perpendicular to road (normalized, 2D)
    
    // === Banking Data (Phase 1) ===
    
    /// <summary>
    /// Curvature at this point (1/radius in 1/meters).
    /// Positive = curving left, Negative = curving right.
    /// </summary>
    public float Curvature;
    
    /// <summary>
    /// Calculated bank angle at this point in radians.
    /// Positive = tilted right-side-up (for left curve).
    /// </summary>
    public float BankAngleRadians;
    
    /// <summary>
    /// 3D normal after banking applied.
    /// For flat road: (Normal.X, Normal.Y, 0) normalized
    /// For banked road: rotated around tangent axis by BankAngleRadians
    /// </summary>
    public Vector3 BankedNormal;
}
```

---

#### Step 1.3: Extend `UnifiedCrossSection` for Banking

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`

Add new properties:

```csharp
/// <summary>
/// Curvature at this cross-section (1/radius).
/// Calculated from adjacent cross-sections.
/// </summary>
public float Curvature { get; set; }

/// <summary>
/// Bank angle at this cross-section in radians.
/// Positive = right side higher than left.
/// </summary>
public float BankAngleRadians { get; set; }

/// <summary>
/// 3D normal vector after banking applied.
/// Z component indicates tilt from horizontal.
/// </summary>
public Vector3 BankedNormal3D { get; set; } = new Vector3(0, 0, 1);

/// <summary>
/// Elevation at the left road edge (meters).
/// LeftEdgeElevation = TargetElevation - (RoadWidth/2 * sin(BankAngle))
/// </summary>
public float LeftEdgeElevation { get; set; } = float.NaN;

/// <summary>
/// Elevation at the right road edge (meters).
/// RightEdgeElevation = TargetElevation + (RoadWidth/2 * sin(BankAngle))
/// </summary>
public float RightEdgeElevation { get; set; } = float.NaN;
```

---

### Phase 2: Curvature Calculation

#### Step 2.1: Create `CurvatureCalculator` Service

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/CurvatureCalculator.cs`

> ?? **Note the new `Banking` subfolder** - keeps all banking algorithms together.

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates road curvature at each cross-section for banking purposes.
/// Uses the angle between adjacent segment vectors divided by segment length.
/// Curvature sign indicates turn direction (positive = left, negative = right).
/// </summary>
public class CurvatureCalculator
{
    /// <summary>
    /// Calculates curvature for all cross-sections of a spline.
    /// Uses central differencing where possible, one-sided at endpoints.
    /// </summary>
    /// <param name="crossSections">Ordered list of cross-sections for a single spline</param>
    public void CalculateCurvature(List<UnifiedCrossSection> crossSections)
    {
        if (crossSections.Count < 3)
        {
            // Not enough points to calculate curvature
            foreach (var cs in crossSections)
                cs.Curvature = 0;
            return;
        }

        for (int i = 0; i < crossSections.Count; i++)
        {
            if (i == 0)
            {
                // First point: forward difference
                cs.Curvature = CalculateCurvatureForward(crossSections, 0);
            }
            else if (i == crossSections.Count - 1)
            {
                // Last point: backward difference
                cs.Curvature = CalculateCurvatureBackward(crossSections, i);
            }
            else
            {
                // Interior point: central difference
                cs.Curvature = CalculateCurvatureCentral(crossSections, i);
            }
        }
    }

    /// <summary>
    /// Central difference curvature calculation.
    /// curvature = (angle between prev?curr and curr?next) / avg_segment_length
    /// </summary>
    private float CalculateCurvatureCentral(List<UnifiedCrossSection> sections, int index)
    {
        var prev = sections[index - 1].CenterPoint;
        var curr = sections[index].CenterPoint;
        var next = sections[index + 1].CenterPoint;

        var v1 = curr - prev; // Vector from prev to curr
        var v2 = next - curr; // Vector from curr to next

        var len1 = v1.Length();
        var len2 = v2.Length();
        
        if (len1 < 0.001f || len2 < 0.001f)
            return 0;

        // Normalize vectors
        v1 /= len1;
        v2 /= len2;

        // Calculate angle between vectors
        var dot = Vector2.Dot(v1, v2);
        dot = Math.Clamp(dot, -1f, 1f);
        var angle = MathF.Acos(dot);

        // Determine sign via cross product (2D cross = v1.X*v2.Y - v1.Y*v2.X)
        var cross = v1.X * v2.Y - v1.Y * v2.X;
        var signedAngle = cross >= 0 ? angle : -angle;

        // Curvature = angle / average segment length
        var avgLength = (len1 + len2) / 2f;
        return signedAngle / avgLength;
    }

    private float CalculateCurvatureForward(List<UnifiedCrossSection> sections, int index)
    {
        // Use next two points
        if (sections.Count < 3) return 0;
        return CalculateCurvatureCentral(sections, 1);
    }

    private float CalculateCurvatureBackward(List<UnifiedCrossSection> sections, int index)
    {
        // Use previous two points
        if (sections.Count < 3) return 0;
        return CalculateCurvatureCentral(sections, sections.Count - 2);
    }

    /// <summary>
    /// Converts curvature to approximate curve radius.
    /// </summary>
    public static float CurvatureToRadius(float curvature)
    {
        if (MathF.Abs(curvature) < 0.0001f)
            return float.MaxValue; // Essentially straight
        return 1f / MathF.Abs(curvature);
    }
}
```

---

### Phase 3: Bank Angle Calculation

#### Step 3.1: Create Banking Calculation Classes

> ?? **Split into 3 focused classes** instead of one large class:

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingCalculator.cs`
- Main entry point, orchestrates the calculation pipeline

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingFalloffBlender.cs`  
- Handles distance-based falloff blending (extracted for testability)

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankedNormalCalculator.cs`
- Handles 3D normal rotation using Rodrigues' formula

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates bank angles for road cross-sections based on curvature.
/// Implements BeamNG-style banking with falloff transitions.
/// </summary>
public class BankingCalculator
{
    private readonly CurvatureCalculator _curvatureCalculator = new();

    /// <summary>
    /// Calculates bank angles for all cross-sections of a spline.
    /// </summary>
    /// <param name="crossSections">Ordered cross-sections for a single spline</param>
    /// <param name="parameters">Spline parameters containing banking settings</param>
    public void CalculateBanking(
        List<UnifiedCrossSection> crossSections,
        SplineRoadParameters parameters)
    {
        if (!parameters.EnableAutoBanking || crossSections.Count < 2)
        {
            // No banking - set all to zero
            foreach (var cs in crossSections)
            {
                cs.BankAngleRadians = 0;
                cs.BankedNormal3D = new Vector3(0, 0, 1);
            }
            return;
        }

        // Step 1: Calculate curvature at each point
        _curvatureCalculator.CalculateCurvature(crossSections);

        // Step 2: Calculate raw bank angles from curvature
        var maxBankRad = parameters.MaxBankAngleDegrees * MathF.PI / 180f;
        var rawBankAngles = new float[crossSections.Count];

        for (int i = 0; i < crossSections.Count; i++)
        {
            var curvature = crossSections[i].Curvature;
            
            // Convert curvature to normalized banking factor [0, 1]
            var bankFactor = MathF.Min(MathF.Abs(curvature) * parameters.CurvatureToBankScale, 1f);
            
            // Apply strength multiplier
            bankFactor *= parameters.BankStrength;
            
            // Calculate angle (preserve sign for direction)
            var sign = MathF.Sign(curvature);
            rawBankAngles[i] = sign * bankFactor * maxBankRad;
        }

        // Step 3: Apply falloff blending for smooth transitions
        ApplyFalloffBlending(crossSections, rawBankAngles, parameters);

        // Step 4: Calculate 3D banked normals
        CalculateBankedNormals(crossSections);
    }

    /// <summary>
    /// Applies distance-based falloff to smooth banking transitions.
    /// Based on BeamNG's autoBankFalloff algorithm.
    /// </summary>
    private void ApplyFalloffBlending(
        List<UnifiedCrossSection> crossSections,
        float[] rawBankAngles,
        SplineRoadParameters parameters)
    {
        var smoothedAngles = new float[crossSections.Count];
        var falloff = parameters.AutoBankFalloff;

        // For each cross-section, blend bank angles from all nodes based on distance
        for (int i = 0; i < crossSections.Count; i++)
        {
            var currentDist = crossSections[i].DistanceAlongSpline;
            var blendedAngle = 0f;
            var totalWeight = 0f;

            for (int j = 0; j < crossSections.Count; j++)
            {
                var nodeDist = crossSections[j].DistanceAlongSpline;
                var distFromNode = MathF.Abs(currentDist - nodeDist);
                
                // Calculate falloff weight: max(0, 1 - |dist| * falloff / transitionLength)
                var transitionLength = parameters.BankTransitionLengthMeters;
                var weight = MathF.Max(0f, 1f - distFromNode * falloff / transitionLength);
                
                if (weight > 0.001f)
                {
                    blendedAngle += rawBankAngles[j] * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight > 0.001f)
            {
                smoothedAngles[i] = blendedAngle / totalWeight;
            }
            else
            {
                smoothedAngles[i] = rawBankAngles[i];
            }
        }

        // Apply smoothed angles
        for (int i = 0; i < crossSections.Count; i++)
        {
            crossSections[i].BankAngleRadians = smoothedAngles[i];
        }
    }

    /// <summary>
    /// Calculates the 3D banked normal by rotating the horizontal normal
    /// around the tangent axis by the bank angle.
    /// </summary>
    private void CalculateBankedNormals(List<UnifiedCrossSection> crossSections)
    {
        foreach (var cs in crossSections)
        {
            if (MathF.Abs(cs.BankAngleRadians) < 0.0001f)
            {
                // No banking - use vertical normal
                cs.BankedNormal3D = new Vector3(0, 0, 1);
                continue;
            }

            // Start with horizontal normal (perpendicular to road in 2D plane)
            var horizontalNormal = new Vector3(cs.NormalDirection.X, cs.NormalDirection.Y, 0);
            
            // Tangent axis (3D, along road direction)
            var tangentAxis = Vector3.Normalize(
                new Vector3(cs.TangentDirection.X, cs.TangentDirection.Y, 0));

            // Rotate horizontal normal around tangent by bank angle
            cs.BankedNormal3D = RotateAroundAxis(horizontalNormal, tangentAxis, cs.BankAngleRadians);
            
            // The banked normal should point generally upward
            // Ensure Z component is positive (normal points up, not down)
            if (cs.BankedNormal3D.Z < 0)
            {
                cs.BankedNormal3D = -cs.BankedNormal3D;
            }
            
            cs.BankedNormal3D = Vector3.Normalize(cs.BankedNormal3D);
        }
    }

    /// <summary>
    /// Rotates a vector around an axis using Rodrigues' rotation formula.
    /// </summary>
    private static Vector3 RotateAroundAxis(Vector3 v, Vector3 axis, float angleRadians)
    {
        var cos = MathF.Cos(angleRadians);
        var sin = MathF.Sin(angleRadians);
        
        return v * cos 
             + Vector3.Cross(axis, v) * sin 
             + axis * Vector3.Dot(axis, v) * (1 - cos);
    }
}
```

---

### Phase 4: Edge Elevation Calculation

#### Step 4.1: Create `BankedElevationCalculator` Service

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankedElevationCalculator.cs`

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates left and right road edge elevations based on banking.
/// </summary>
public class BankedElevationCalculator
{
    /// <summary>
    /// Calculates edge elevations for all cross-sections based on bank angle.
    /// Must be called AFTER TargetElevation is set and BankAngleRadians is calculated.
    /// </summary>
    /// <param name="crossSections">Cross-sections with TargetElevation and BankAngleRadians set</param>
    /// <param name="roadHalfWidth">Half the road width in meters</param>
    public void CalculateEdgeElevations(List<UnifiedCrossSection> crossSections, float roadHalfWidth)
    {
        foreach (var cs in crossSections)
        {
            if (float.IsNaN(cs.TargetElevation))
            {
                cs.LeftEdgeElevation = float.NaN;
                cs.RightEdgeElevation = float.NaN;
                continue;
            }

            // Calculate elevation delta from center to edge
            // delta = halfWidth * sin(bankAngle)
            var elevationDelta = roadHalfWidth * MathF.Sin(cs.BankAngleRadians);

            // For positive bank angle (left curve), right side is higher
            cs.LeftEdgeElevation = cs.TargetElevation - elevationDelta;
            cs.RightEdgeElevation = cs.TargetElevation + elevationDelta;
        }
    }

    /// <summary>
    /// Gets the elevation at a specific lateral offset from road center.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="lateralOffset">Offset from center (negative = left, positive = right)</param>
    /// <returns>Interpolated elevation at the offset</returns>
    public static float GetElevationAtOffset(UnifiedCrossSection cs, float lateralOffset)
    {
        if (float.IsNaN(cs.TargetElevation))
            return float.NaN;

        // Linear interpolation based on bank angle
        var elevationDelta = lateralOffset * MathF.Sin(cs.BankAngleRadians);
        return cs.TargetElevation + elevationDelta;
    }
}
```

---

### Phase 5: Terrain Blending Integration

#### Step 5.1: Create `BankedTerrainHelper` (Minimal Changes to Existing Files)

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankedTerrainHelper.cs`

> ?? **Why a helper class?** `UnifiedTerrainBlender.cs` is already large. 
> Instead of adding banking logic directly, we create a static helper that the blender calls.

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
/// Static helper methods for banking-aware terrain blending.
/// Keeps banking logic out of the large UnifiedTerrainBlender class.
/// </summary>
public static class BankedTerrainHelper
{
    /// <summary>
    /// Gets the elevation at a world position considering road banking.
    /// </summary>
    /// <param name="cs">The nearest cross-section</param>
    /// <param name="worldPos">World position to query</param>
    /// <returns>Elevation considering banking, or TargetElevation if no banking</returns>
    public static float GetBankedElevation(UnifiedCrossSection cs, Vector2 worldPos)
    {
        if (float.IsNaN(cs.TargetElevation))
            return float.NaN;

        if (MathF.Abs(cs.BankAngleRadians) < 0.0001f)
            return cs.TargetElevation; // No banking

        var lateralOffset = CalculateLateralOffset(worldPos, cs);
        return BankedElevationCalculator.GetElevationAtOffset(cs, lateralOffset);
    }

    /// <summary>
    /// Calculates the lateral offset from road center for a world position.
    /// </summary>
    public static float CalculateLateralOffset(Vector2 worldPos, UnifiedCrossSection cs)
    {
        var toPoint = worldPos - cs.CenterPoint;
        return Vector2.Dot(toPoint, cs.NormalDirection);
    }

    /// <summary>
    /// Checks if a cross-section has significant banking applied.
    /// </summary>
    public static bool HasBanking(UnifiedCrossSection cs)
    {
        return MathF.Abs(cs.BankAngleRadians) > 0.0001f;
    }
}
```

**Minimal change to `UnifiedTerrainBlender.cs`:**

```csharp
// In ApplyProtectedBlending, replace direct elevation calculation with:
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;

// ... inside the pixel loop:
if (d <= halfWidth)
{
    // ROAD CORE - Use helper for banking-aware elevation
    newH = BankedTerrainHelper.GetBankedElevation(cs, worldPos);
    localCore++;
}
```

This keeps the change to `UnifiedTerrainBlender.cs` to just 2-3 lines!

---

### Phase 5.5: Priority-Aware Junction Banking (CRITICAL)

> ?? **CRITICAL**: This phase ensures banking works correctly with junction harmonization!
> 
> ?? **File Organization**: All junction banking logic goes in `Terrain/Algorithms/Junction/` folder.

#### The Problem

At junctions, the current system carefully harmonizes elevations between meeting roads. If banking is handled incorrectly:

1. **Naive approach (WRONG)**: Suppress banking on ALL roads near junctions
   - Problem: A highway driver at 130 km/h suddenly hits a flat curve because a dirt road crosses!
   - This is **dangerous** and unrealistic

2. **Correct approach**: Use road priority to determine banking behavior
   - Higher-priority roads (highways) **maintain their banking**
   - Lower-priority roads **adapt their edge elevations** to meet the banked surface
   - Same-priority roads both reduce banking at mutual junctions

#### The Solution: Priority-Based Banking at Junctions

**Key Principle**: The road with higher priority "wins" and keeps its banking. Lower-priority roads must adapt.

| Junction Type | Higher-Priority Road | Lower-Priority Road |
|--------------|---------------------|---------------------|
| Highway × Dirt Road | Full banking maintained | Adapts edge elevations to match banked highway |
| Primary × Secondary | Full banking maintained | Adapts edge elevations to match |
| Same Priority | Both reduce banking to flat | Both reduce banking to flat |
| Endpoint (dead end) | Fade banking to flat | N/A |

#### Step 5.5.1: Create `JunctionBankingBehavior` Enum

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Junction/JunctionBankingBehavior.cs`

> ?? **Separate file for the enum** - makes it easy to find and reference.

```csharp
namespace BeamNgTerrainPoc.Terrain.Algorithms.Junction;

/// <summary>
/// Defines how banking behaves at/near a junction for a cross-section.
/// </summary>
public enum JunctionBankingBehavior
{
    /// <summary>
    /// Normal banking based on curvature (no junction nearby).
    /// </summary>
    Normal,
    
    /// <summary>
    /// This road has highest priority at the junction - maintain full banking.
    /// Lower-priority roads will adapt to us.
    /// </summary>
    MaintainBanking,
    
    /// <summary>
    /// This road has lower priority - adapt edge elevations to match
    /// the higher-priority road's banked surface.
    /// </summary>
    AdaptToHigherPriority,
    
    /// <summary>
    /// Equal priority roads meeting - both reduce banking to flat.
    /// Also used for endpoints (dead ends).
    /// </summary>
    SuppressBanking
}
```

**Then add properties to `UnifiedCrossSection.cs`:**

#### Step 5.5.2: Create `PriorityAwareJunctionBankingCalculator` Service

**New File:** `BeamNgTerrainPoc/Terrain/Algorithms/Junction/PriorityAwareJunctionBankingCalculator.cs`

> ?? **~200 lines** - focused on determining junction behavior based on priority.

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates junction banking behavior based on road priority.
/// 
/// Priority Rules:
/// 1. Higher-priority road MAINTAINS its banking (highway stays banked)
/// 2. Lower-priority road ADAPTS to match the higher-priority road's banked surface
/// 3. Equal-priority roads both REDUCE banking to flat at junction
/// 4. Endpoints (dead ends) fade banking to flat
/// 
/// This ensures highways remain safe at high speeds while secondary roads
/// smoothly transition to meet them.
/// </summary>
public class PriorityAwareJunctionBankingCalculator
{
    /// <summary>
    /// Analyzes all junctions and sets banking behavior for each cross-section.
    /// Must be called AFTER:
    /// - Junction detection
    /// - Elevation harmonization  
    /// - Curvature calculation
    /// But BEFORE:
    /// - Bank angle calculation
    /// </summary>
    /// <param name="network">The road network with detected junctions</param>
    /// <param name="transitionDistanceMeters">Distance over which banking transitions occur</param>
    public void CalculateJunctionBankingBehavior(
        UnifiedRoadNetwork network,
        float transitionDistanceMeters = 30.0f)
    {
        // Initialize all cross-sections to normal behavior
        foreach (var cs in network.CrossSections)
        {
            cs.JunctionBankingBehavior = JunctionBankingBehavior.Normal;
            cs.JunctionBankingFactor = 1.0f;
            cs.DistanceToNearestJunction = float.MaxValue;
        }

        if (network.Junctions.Count == 0)
            return;

        // Build priority lookup for splines
        var splinePriorities = network.Splines.ToDictionary(s => s.SplineId, s => s.Priority);

        // Process each junction
        foreach (var junction in network.Junctions.Where(j => !j.IsExcluded))
        {
            ProcessJunction(network, junction, splinePriorities, transitionDistanceMeters);
        }

        // Log statistics
        var stats = network.CrossSections
            .GroupBy(cs => cs.JunctionBankingBehavior)
            .ToDictionary(g => g.Key, g => g.Count());
        
        TerrainCreationLogger.Current?.Detail(
            $"Junction banking behavior: " +
            $"Normal={stats.GetValueOrDefault(JunctionBankingBehavior.Normal)}, " +
            $"Maintain={stats.GetValueOrDefault(JunctionBankingBehavior.MaintainBanking)}, " +
            $"Adapt={stats.GetValueOrDefault(JunctionBankingBehavior.AdaptToHigherPriority)}, " +
            $"Suppress={stats.GetValueOrDefault(JunctionBankingBehavior.SuppressBanking)}");
    }

    private void ProcessJunction(
        UnifiedRoadNetwork network,
        Junction junction,
        Dictionary<int, int> splinePriorities,
        float transitionDistanceMeters)
    {
        var participatingSplines = junction.ParticipatingSplineIds.ToList();
        
        if (participatingSplines.Count == 0)
            return;

        // Handle endpoints (single spline at junction = dead end)
        if (junction.Type == JunctionType.Endpoint || participatingSplines.Count == 1)
        {
            ProcessEndpoint(network, junction, participatingSplines[0], transitionDistanceMeters);
            return;
        }

        // Find the highest priority among participating splines
        var priorityGroups = participatingSplines
            .GroupBy(id => splinePriorities.GetValueOrDefault(id, 0))
            .OrderByDescending(g => g.Key)
            .ToList();

        var highestPriority = priorityGroups[0].Key;
        var highestPrioritySplines = priorityGroups[0].ToList();

        // Check if there are multiple splines with highest priority (equal priority case)
        bool hasEqualPriorityConflict = highestPrioritySplines.Count > 1;

        foreach (var splineId in participatingSplines)
        {
            var splinePriority = splinePriorities.GetValueOrDefault(splineId, 0);
            var crossSections = network.GetCrossSectionsForSpline(splineId).ToList();

            JunctionBankingBehavior behavior;
            int? higherPrioritySplineId = null;

            if (splinePriority == highestPriority)
            {
                if (hasEqualPriorityConflict)
                {
                    // Equal priority - all reduce banking
                    behavior = JunctionBankingBehavior.SuppressBanking;
                }
                else
                {
                    // This is THE highest priority - maintain banking
                    behavior = JunctionBankingBehavior.MaintainBanking;
                }
            }
            else
            {
                // Lower priority - adapt to highest priority road
                behavior = JunctionBankingBehavior.AdaptToHigherPriority;
                higherPrioritySplineId = highestPrioritySplines[0]; // Use first if multiple
            }

            // Apply behavior to cross-sections near this junction
            ApplyBehaviorToNearbyCS(
                crossSections,
                junction.Position,
                behavior,
                higherPrioritySplineId,
                transitionDistanceMeters);
        }
    }

    private void ProcessEndpoint(
        UnifiedRoadNetwork network,
        Junction junction,
        int splineId,
        float transitionDistanceMeters)
    {
        var crossSections = network.GetCrossSectionsForSpline(splineId).ToList();
        
        // Endpoints always suppress banking (fade to flat at dead end)
        ApplyBehaviorToNearbyCS(
            crossSections,
            junction.Position,
            JunctionBankingBehavior.SuppressBanking,
            null,
            transitionDistanceMeters);
    }

    private void ApplyBehaviorToNearbyCS(
        List<UnifiedCrossSection> crossSections,
        Vector2 junctionPosition,
        JunctionBankingBehavior behavior,
        int? higherPrioritySplineId,
        float transitionDistanceMeters)
    {
        foreach (var cs in crossSections)
        {
            var distToJunction = Vector2.Distance(cs.CenterPoint, junctionPosition);
            
            // Only affect cross-sections within transition distance
            if (distToJunction > transitionDistanceMeters)
                continue;

            // Track closest junction
            if (distToJunction < cs.DistanceToNearestJunction)
            {
                cs.DistanceToNearestJunction = distToJunction;
            }

            // Calculate transition factor (1 = at junction, 0 = at transition boundary)
            var transitionFactor = 1.0f - (distToJunction / transitionDistanceMeters);
            transitionFactor = MathF.Max(0, transitionFactor);
            
            // Apply smooth cosine interpolation for gradual transition
            transitionFactor = 0.5f + 0.5f * MathF.Cos((1.0f - transitionFactor) * MathF.PI);

            // Only override if this junction has stronger influence
            // (closer junction or higher transition factor)
            if (transitionFactor > (1.0f - cs.JunctionBankingFactor) || 
                cs.JunctionBankingBehavior == JunctionBankingBehavior.Normal)
            {
                cs.JunctionBankingBehavior = behavior;
                cs.JunctionBankingFactor = 1.0f - transitionFactor; // Invert: 0 at junction, 1 far away
                cs.HigherPrioritySplineId = higherPrioritySplineId;
            }
        }
    }
}
```

#### Step 5.5.3: Modify `BankingCalculator` to Use Priority-Aware Behavior

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/BankingCalculator.cs`

Update the `CalculateBanking` method:

```csharp
/// <summary>
/// Calculates bank angles for all cross-sections of a spline.
/// IMPORTANT: Junction banking behavior must be calculated before calling this!
/// </summary>
public void CalculateBanking(
    List<UnifiedCrossSection> crossSections,
    SplineRoadParameters parameters,
    UnifiedRoadNetwork? network = null) // Network needed for AdaptToHigherPriority
{
    if (!parameters.EnableAutoBanking || crossSections.Count < 2)
    {
        foreach (var cs in crossSections)
        {
            cs.BankAngleRadians = 0;
            cs.BankedNormal3D = new Vector3(0, 0, 1);
        }
        return;
    }

    // Step 1: Calculate curvature at each point
    _curvatureCalculator.CalculateCurvature(crossSections);

    // Step 2: Calculate bank angles based on junction behavior
    var maxBankRad = parameters.MaxBankAngleDegrees * MathF.PI / 180f;

    for (int i = 0; i < crossSections.Count; i++)
    {
        var cs = crossSections[i];
        float bankAngle;

        switch (cs.JunctionBankingBehavior)
        {
            case JunctionBankingBehavior.MaintainBanking:
            case JunctionBankingBehavior.Normal:
                // Full banking based on curvature
                bankAngle = CalculateBankAngleFromCurvature(
                    cs.Curvature, parameters.CurvatureToBankScale, 
                    parameters.BankStrength, maxBankRad);
                break;

            case JunctionBankingBehavior.SuppressBanking:
                // Blend from calculated banking to zero
                var normalBankAngle = CalculateBankAngleFromCurvature(
                    cs.Curvature, parameters.CurvatureToBankScale,
                    parameters.BankStrength, maxBankRad);
                bankAngle = normalBankAngle * cs.JunctionBankingFactor;
                break;

            case JunctionBankingBehavior.AdaptToHigherPriority:
                // Blend from our banking to the higher-priority road's banking
                var ourBankAngle = CalculateBankAngleFromCurvature(
                    cs.Curvature, parameters.CurvatureToBankScale,
                    parameters.BankStrength, maxBankRad);
                
                var targetAngle = GetHigherPriorityBankAngle(cs, network);
                
                // Interpolate: far from junction = our banking, at junction = target banking
                bankAngle = Lerp(targetAngle, ourBankAngle, cs.JunctionBankingFactor);
                break;

            default:
                bankAngle = 0;
                break;
        }

        cs.BankAngleRadians = bankAngle;
    }

    // Step 3: Apply falloff blending for smooth transitions (curve entry/exit)
    ApplyFalloffBlending(crossSections, parameters);

    // Step 4: Calculate 3D banked normals
    CalculateBankedNormals(crossSections);
}

private float CalculateBankAngleFromCurvature(
    float curvature, float curvatureScale, float strength, float maxBankRad)
{
    var bankFactor = MathF.Min(MathF.Abs(curvature) * curvatureScale, 1f);
    bankFactor *= strength;
    var sign = MathF.Sign(curvature);
    return sign * bankFactor * maxBankRad;
}

private float GetHigherPriorityBankAngle(UnifiedCrossSection cs, UnifiedRoadNetwork? network)
{
    if (network == null || !cs.HigherPrioritySplineId.HasValue)
        return 0;

    // Find the nearest cross-section on the higher-priority road
    var higherPriorityCS = network.CrossSections
        .Where(other => other.OwnerSplineId == cs.HigherPrioritySplineId.Value)
        .OrderBy(other => Vector2.Distance(other.CenterPoint, cs.CenterPoint))
        .FirstOrDefault();

    return higherPriorityCS?.BankAngleRadians ?? 0;
}

private static float Lerp(float a, float b, float t) => a + (b - a) * t;
```

#### Step 5.5.4: Handle Edge Elevation Adaptation

When a lower-priority road meets a banked higher-priority road, it's not just the bank angle that matters - the **edge elevations** must also transition smoothly.

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/BankedElevationCalculator.cs`

Add method for adapting edge elevations:

```csharp
/// <summary>
/// For cross-sections that adapt to a higher-priority road, calculate edge elevations
/// that smoothly transition to match the banked surface of the higher-priority road.
/// </summary>
public void CalculateAdaptiveEdgeElevations(
    List<UnifiedCrossSection> crossSections,
    UnifiedRoadNetwork network,
    float roadHalfWidth)
{
    foreach (var cs in crossSections)
    {
        if (cs.JunctionBankingBehavior != JunctionBankingBehavior.AdaptToHigherPriority ||
            !cs.HigherPrioritySplineId.HasValue)
        {
            // Normal edge elevation calculation
            CalculateEdgeElevationsForCS(cs, roadHalfWidth);
            continue;
        }

        // Find where this cross-section meets the higher-priority road
        var higherPriorityCS = FindNearestCrossSection(
            network, cs.HigherPrioritySplineId.Value, cs.CenterPoint);

        if (higherPriorityCS == null)
        {
            CalculateEdgeElevationsForCS(cs, roadHalfWidth);
            continue;
        }

        // Calculate what elevation the higher-priority road has at our edge positions
        var leftEdgePos = cs.CenterPoint - cs.NormalDirection * roadHalfWidth;
        var rightEdgePos = cs.CenterPoint + cs.NormalDirection * roadHalfWidth;

        // Project our edge positions onto the higher-priority road's cross-section
        var targetLeftElev = GetElevationAtPoint(higherPriorityCS, leftEdgePos);
        var targetRightElev = GetElevationAtPoint(higherPriorityCS, rightEdgePos);

        // Blend between our calculated edges and the target edges
        var normalLeft = cs.TargetElevation - roadHalfWidth * MathF.Sin(cs.BankAngleRadians);
        var normalRight = cs.TargetElevation + roadHalfWidth * MathF.Sin(cs.BankAngleRadians);

        var t = cs.JunctionBankingFactor; // 0 at junction, 1 far away
        cs.LeftEdgeElevation = Lerp(targetLeftElev, normalLeft, t);
        cs.RightEdgeElevation = Lerp(targetRightElev, normalRight, t);
    }
}

private static float Lerp(float a, float b, float t) => a + (b - a) * t;
```

---

### Phase 6: Pipeline Integration

#### Step 6.1: Create `BankingOrchestrator` Service

**New File:** `BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs`

> ?? **Why an orchestrator?** Instead of adding banking logic to the already-large `UnifiedRoadSmoother.cs`,
> we create a dedicated orchestrator that coordinates all banking calculations. The smoother just calls one method.

```csharp
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Algorithms.Junction;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Orchestrates all banking calculations in the correct order.
/// Keeps banking logic out of UnifiedRoadSmoother.
/// </summary>
public class BankingOrchestrator
{
    private readonly PriorityAwareJunctionBankingCalculator _junctionBankingCalc = new();
    private readonly BankingCalculator _bankingCalc = new();
    private readonly BankedElevationCalculator _edgeCalc = new();

    /// <summary>
    /// Applies banking to the road network.
    /// Must be called AFTER junction detection and elevation harmonization.
    /// </summary>
    /// <param name="network">The road network</param>
    /// <param name="materials">Materials with banking settings</param>
    /// <param name="junctionBlendDistance">Distance for junction transitions</param>
    /// <returns>True if any banking was applied</returns>
    public bool ApplyBanking(
        UnifiedRoadNetwork network,
        List<MaterialDefinition> materials,
        float junctionBlendDistance = 30.0f)
    {
        if (!HasAnyBankingEnabled(materials))
        {
            TerrainLogger.Info("Banking: No materials have banking enabled, skipping.");
            return false;
        }

        TerrainLogger.Info("Banking: Phase 1 - Calculating junction banking behavior...");
        _junctionBankingCalc.CalculateJunctionBankingBehavior(network, junctionBlendDistance);

        TerrainLogger.Info("Banking: Phase 2 - Calculating bank angles...");
        foreach (var spline in network.Splines)
        {
            var bankingParams = spline.Parameters.GetSplineParameters()?.Banking;
            if (bankingParams?.EnableAutoBanking != true)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            
            _bankingCalc.CalculateBanking(crossSections, bankingParams, network);
            
            var halfWidth = spline.Parameters.RoadWidthMeters / 2f;
            _edgeCalc.CalculateEdgeElevations(crossSections, halfWidth);
        }

        LogBankingStatistics(network);
        return true;
    }

    private static bool HasAnyBankingEnabled(List<MaterialDefinition> materials)
    {
        return materials.Any(m => 
            m.RoadParameters?.GetSplineParameters()?.Banking?.EnableAutoBanking == true);
    }

    private static void LogBankingStatistics(UnifiedRoadNetwork network)
    {
        var bankedCount = network.CrossSections.Count(cs => MathF.Abs(cs.BankAngleRadians) > 0.001f);
        var totalCount = network.CrossSections.Count;
        var maxAngle = network.CrossSections.Max(cs => MathF.Abs(cs.BankAngleRadians)) * 180f / MathF.PI;
        
        TerrainLogger.Info($"Banking: Applied to {bankedCount}/{totalCount} cross-sections, max angle: {maxAngle:F1}°");
    }
}
```

**Minimal change to `UnifiedRoadSmoother.cs`:**

```csharp
// Add field:
private readonly BankingOrchestrator _bankingOrchestrator = new();

// In SmoothAllRoads(), after junction harmonization:
// Phase 3.5 + 4: Apply banking (all logic in orchestrator)
_bankingOrchestrator.ApplyBanking(
    network, 
    roadMaterials, 
    junctionParams.JunctionBlendDistanceMeters);
```

This keeps the change to `UnifiedRoadSmoother.cs` to just 3 lines!

---

### Phase 7: Master Spline Export

#### Step 7.1: Update `MasterSplineExporter` for Banking

**File:** `BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs`

Modify the `MasterSpline` creation to include banking parameters:

```csharp
// In ExportFromUnifiedNetwork, when creating MasterSpline:
var splineParams = paramSpline.Parameters.GetSplineParameters();

var masterSpline = new MasterSpline
{
    Id = Guid.NewGuid().ToString(),
    Name = splineName,
    Nodes = nodes,
    
    // Banking parameters from spline
    IsAutoBanking = splineParams.EnableAutoBanking,
    BankStrength = splineParams.BankStrength,
    AutoBankFalloff = splineParams.AutoBankFalloff,
    
    // Export calculated normals with banking applied
    Nmls = ExportBankedNormals(paramSpline, network, nodes.Count),
    
    Widths = nodes.Select(_ => roadWidth).ToList()
};

// Add helper method:
private static List<SplineNormal> ExportBankedNormals(
    ParameterizedRoadSpline paramSpline,
    UnifiedRoadNetwork network,
    int nodeCount)
{
    var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();
    
    if (crossSections.Count == 0 || !paramSpline.Parameters.GetSplineParameters().EnableAutoBanking)
    {
        // No banking - return vertical normals
        return Enumerable.Range(0, nodeCount)
            .Select(_ => new SplineNormal { X = 0, Y = 0, Z = 1 })
            .ToList();
    }
    
    // Sample normals at node intervals
    var normals = new List<SplineNormal>();
    var step = Math.Max(1, crossSections.Count / nodeCount);
    
    for (int i = 0; i < nodeCount; i++)
    {
        var csIndex = Math.Min(i * step, crossSections.Count - 1);
        var cs = crossSections[csIndex];
        
        // Transform banked normal to BeamNG coordinate system
        var worldNormal = BeamNgCoordinateTransformer.TransformNormal(cs.BankedNormal3D);
        
        normals.Add(new SplineNormal
        {
            X = worldNormal.X,
            Y = worldNormal.Y,
            Z = worldNormal.Z
        });
    }
    
    return normals;
}
```

---

### Phase 8: UI Integration

#### Step 8.1: Create `BankingSettingsPanel` Component

**New File:** `BeamNG_LevelCleanUp/BlazorUI/Components/BankingSettingsPanel.razor`

> ?? **Separate component** keeps `TerrainMaterialSettings.razor` from growing larger.

```razor
@using BeamNgTerrainPoc.Terrain.Models

<MudExpansionPanel Text="@GetPanelTitle()" Dense="true">
    <MudStack Spacing="2" Class="pa-2">
        <MudCheckBox @bind-Value="_enableBanking"
                     Label="Enable Auto-Banking"
                     Color="Color.Primary"
                     T="bool" />
        
        @if (_enableBanking)
        {
            <MudAlert Severity="Severity.Info" Dense="true" Class="mb-2">
                Banking tilts the road surface on curves for realistic driving.
                Higher-priority roads maintain banking at junctions.
            </MudAlert>

            <MudGrid Spacing="2">
                <MudItem xs="12" sm="6">
                    <MudSlider @bind-Value="_bankStrength"
                               Min="0" Max="1" Step="0.05f"
                               Color="Color.Primary">
                        <MudText Typo="Typo.caption">
                            Banking Strength: @(_bankStrength.ToString("P0"))
                        </MudText>
                    </MudSlider>
                </MudItem>

                <MudItem xs="12" sm="6">
                    <MudNumericField @bind-Value="_maxBankAngle"
                                     Label="Max Bank Angle (°)"
                                     Variant="Variant.Outlined"
                                     Min="0f" Max="20f" Step="0.5f"
                                     HelperText="Highways: 4-8°, Race tracks: up to 15°" />
                </MudItem>

                <MudItem xs="12" sm="6">
                    <MudSlider @bind-Value="_bankFalloff"
                               Min="0.3f" Max="2.0f" Step="0.1f"
                               Color="Color.Secondary">
                        <MudText Typo="Typo.caption">
                            Transition Falloff: @_bankFalloff.ToString("F1")
                        </MudText>
                    </MudSlider>
                </MudItem>

                <MudItem xs="12" sm="6">
                    <MudNumericField @bind-Value="_transitionLength"
                                     Label="Transition Length (m)"
                                     Variant="Variant.Outlined"
                                     Min="5f" Max="100f" Step="5f"
                                     HelperText="Distance for banking fade in/out" />
                </MudItem>
            </MudGrid>

            <MudDivider Class="my-2" />
            
            <MudText Typo="Typo.caption" Color="Color.Secondary">
                <MudIcon Icon="@Icons.Material.Filled.Info" Size="Size.Small" />
                Presets:
            </MudText>
            <MudButtonGroup OverrideStyles="false" Size="Size.Small">
                <MudButton OnClick="ApplyHighwayPreset">Highway</MudButton>
                <MudButton OnClick="ApplyRuralPreset">Rural Road</MudButton>
                <MudButton OnClick="ApplyRaceTrackPreset">Race Track</MudButton>
            </MudButtonGroup>
        }
    </MudStack>
</MudExpansionPanel>

@code {
    [Parameter] public BankingParameters? Parameters { get; set; }
    [Parameter] public EventCallback<BankingParameters> ParametersChanged { get; set; }

    private bool _enableBanking;
    private float _bankStrength = 0.5f;
    private float _maxBankAngle = 8.0f;
    private float _bankFalloff = 0.6f;
    private float _transitionLength = 30.0f;

    protected override void OnParametersSet()
    {
        if (Parameters != null)
        {
            _enableBanking = Parameters.EnableAutoBanking;
            _bankStrength = Parameters.BankStrength;
            _maxBankAngle = Parameters.MaxBankAngleDegrees;
            _bankFalloff = Parameters.AutoBankFalloff;
            _transitionLength = Parameters.BankTransitionLengthMeters;
        }
    }

    private string GetPanelTitle() => _enableBanking 
        ? $"Road Banking (Max {_maxBankAngle:F0}°)" 
        : "Road Banking (Disabled)";

    private async Task NotifyChanged()
    {
        var newParams = new BankingParameters
        {
            EnableAutoBanking = _enableBanking,
            BankStrength = _bankStrength,
            MaxBankAngleDegrees = _maxBankAngle,
            AutoBankFalloff = _bankFalloff,
            BankTransitionLengthMeters = _transitionLength
        };
        await ParametersChanged.InvokeAsync(newParams);
    }

    private async Task ApplyHighwayPreset()
    {
        var preset = BankingParameters.Highway;
        _enableBanking = preset.EnableAutoBanking;
        _bankStrength = preset.BankStrength;
        _maxBankAngle = preset.MaxBankAngleDegrees;
        _bankFalloff = preset.AutoBankFalloff;
        await NotifyChanged();
    }

    private async Task ApplyRuralPreset()
    {
        var preset = BankingParameters.RuralRoad;
        // ... same pattern
        await NotifyChanged();
    }

    private async Task ApplyRaceTrackPreset()
    {
        var preset = BankingParameters.RaceTrack;
        // ... same pattern
        await NotifyChanged();
    }
}
```

**Minimal change to `TerrainMaterialSettings.razor`:**

```razor
@* Add inside the road smoothing section: *@
@if (Material.IsRoadMaterial)
{
    <BankingSettingsPanel 
        Parameters="@_bankingParams"
        ParametersChanged="OnBankingParametersChanged" />
}
```

This keeps the change to `TerrainMaterialSettings.razor` to just 4 lines!

---

### Phase 9: Per-Spline Override System (Future)

#### Step 9.1: Add `SplineBankingOverride` Class

**New File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/SplineBankingOverride.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Per-spline or per-region banking override.
/// Allows disabling or modifying banking for specific road segments.
/// </summary>
public class SplineBankingOverride
{
    /// <summary>
    /// Spline ID this override applies to.
    /// </summary>
    public int SplineId { get; set; }
    
    /// <summary>
    /// Start distance along spline (meters). Null = from start.
    /// </summary>
    public float? StartDistance { get; set; }
    
    /// <summary>
    /// End distance along spline (meters). Null = to end.
    /// </summary>
    public float? EndDistance { get; set; }
    
    /// <summary>
    /// Override banking strength for this region (0-1).
    /// Null = use material default.
    /// Use 0 to disable banking in towns/villages.
    /// </summary>
    public float? BankStrengthOverride { get; set; }
    
    /// <summary>
    /// Override max bank angle for this region (degrees).
    /// Null = use material default.
    /// </summary>
    public float? MaxBankAngleOverride { get; set; }
    
    /// <summary>
    /// Reason for override (for UI display).
    /// </summary>
    public string? Reason { get; set; }
}
```

#### Step 9.2: Add Override Support to `ParameterizedRoadSpline`

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs`

Add:

```csharp
/// <summary>
/// Per-spline banking overrides. Regions within the spline where
/// banking should be modified (e.g., reduced in urban areas).
/// </summary>
public List<SplineBankingOverride> BankingOverrides { get; set; } = new();

/// <summary>
/// Gets the effective bank strength at a given distance along this spline.
/// Checks overrides first, falls back to material defaults.
/// </summary>
public float GetEffectiveBankStrength(float distanceAlongSpline)
{
    var override_ = BankingOverrides.FirstOrDefault(o =>
        (o.StartDistance ?? 0) <= distanceAlongSpline &&
        distanceAlongSpline <= (o.EndDistance ?? float.MaxValue));
    
    if (override_?.BankStrengthOverride.HasValue == true)
        return override_.BankStrengthOverride.Value;
    
    return Parameters.GetSplineParameters().BankStrength;
}
```

---

## Testing Strategy

### Unit Tests

1. **CurvatureCalculator Tests**
   - Straight line ? curvature = 0
   - Perfect circle ? curvature = 1/radius
   - S-curve ? sign changes
   - Minimum points handling

2. **BankingCalculator Tests**
   - No banking when disabled
   - Maximum banking on tight curves
   - Correct sign for left vs right curves
   - Falloff transition smoothness

3. **Edge Elevation Tests**
   - Flat road ? equal edges
   - Banked road ? correct height difference
   - Correct direction (higher outside of curve)

4. **Priority-Aware Junction Banking Tests** (CRITICAL)
   - Higher-priority road at junction ? `MaintainBanking` behavior, full bank angle
   - Lower-priority road at junction ? `AdaptToHigherPriority` behavior
   - Same-priority roads at junction ? `SuppressBanking` behavior, both fade to flat
   - Endpoint ? `SuppressBanking` behavior, fade to flat
   - Edge elevations of lower-priority road match higher-priority road's banked surface
   - Transition is smooth (cosine interpolation)

### Integration Tests

1. **Full Pipeline Test**
   - Process a known road network with curves
   - Verify curvature detection
   - Verify banking angles within limits
   - Verify edge elevations

2. **Priority-Based Junction Tests** (CRITICAL)
   - **Highway × Dirt Road Test**:
     - Create banked highway curve
     - Add crossing dirt road
     - Verify highway maintains full banking through junction
     - Verify dirt road edge elevations adapt to match highway's banked surface
     - Verify no bumps when driving on highway at high speed
   
   - **Same-Priority Junction Test**:
     - Two residential roads meeting
     - Both should reduce banking to flat at junction
     - Smooth transition for both roads

3. **Cross-Material Junction Test**
   - Highway material (high priority, banked) meets residential (low priority)
   - Verify highway banking unchanged
   - Verify residential road adapts

4. **Export Verification**
   - Export to BeamNG JSON
   - Verify `isAutoBanking`, `bankStrength`, `autoBankFalloff` set
   - Verify `nmls` array contains rotated normals
   - Verify normals transition smoothly at priority-based junctions

### Manual Testing in BeamNG

1. Import generated terrain with banked roads
2. Drive vehicles through curves
3. Verify visual banking matches parameters
4. Verify smooth transitions at curve entry/exit
5. **Drive highway through junction with dirt road crossing - verify NO banking loss**
6. **Drive dirt road onto highway - verify smooth ramp to banked surface**
7. **Drive between same-priority roads at junction - verify smooth flat transition**

---

## Implementation Order

### Milestone 1: Core Infrastructure (Week 1)
- [ ] Step 1.1: Add parameters to `SplineRoadParameters`
- [ ] Step 1.2: Extend `SplineSample`
- [ ] Step 1.3: Extend `UnifiedCrossSection` (including junction suppression fields)

### Milestone 2: Calculation Services (Week 1-2)
- [ ] Step 2.1: `CurvatureCalculator`
- [ ] Step 3.1: `BankingCalculator`
- [ ] Step 4.1: `BankedElevationCalculator`

### Milestone 3: Priority-Aware Junction Banking (Week 2) - CRITICAL
- [x] Step 5.5.1: Add junction banking context fields to `UnifiedCrossSection` ? IMPLEMENTED
- [x] Step 5.5.2: Create `PriorityAwareJunctionBankingCalculator` service ? IMPLEMENTED
- [x] Step 5.5.3: Integrate priority-aware behavior into `BankingCalculator` ? IMPLEMENTED
- [x] Step 5.5.4: Handle edge elevation adaptation for lower-priority roads ? IMPLEMENTED
- [x] Step 5.5.5: Create `JunctionBankingAdapter` for smooth elevation ramps ? IMPLEMENTED (NEW)

> **FIX IMPLEMENTED**: Added `JunctionBankingAdapter.cs` to handle smooth elevation transitions
> at junctions with banked roads. This fixes:
> 1. "Speed bump" artifacts when secondary roads meet banked primary roads
> 2. Elevation discontinuities at equal-priority junctions
> 
> The adapter:
> - Calculates the surface elevation at the intersection point (accounting for banking)
> - Applies a smooth quintic smoothstep ramp to transition elevations
> - Handles both AdaptToHigherPriority and SuppressBanking scenarios
> - Recalculates edge elevations after center elevation adaptation

> **CRITICAL FIX (Step 5.5.6)**: Reordered pipeline to make junction harmonization banking-aware!
> 
> **The Problem**: Junction harmonization was running BEFORE banking, so it only knew about
> centerline elevations (`TargetElevation`). When a secondary road met a banked primary road,
> the harmonizer set the secondary road's elevation to match the primary's centerline, but
> the actual connection point on the banked surface was at a different elevation. This caused
> "cliff" artifacts.
> 
> **The Solution**: Split banking into two phases:
> 1. **Phase 2.5 (Pre-calculation)**: Calculate curvature, bank angles, and edge elevations
>    BEFORE junction harmonization. This gives the harmonizer the banking data it needs.
> 2. **Phase 3 (Junction Harmonization)**: Now banking-aware! Uses `BankedTerrainHelper.GetBankedElevation()`
>    to calculate the surface elevation at the actual connection point.
> 3. **Phase 3.5 (Finalization)**: Apply junction-aware banking adjustments and adapt secondary
>    road elevations to smoothly meet banked primary road surfaces.
> 
> **Files Modified**:
> - `UnifiedRoadSmoother.cs`: Reordered pipeline with new phases 2.5 and 3.5
> - `BankingOrchestrator.cs`: Added `ApplyBankingPreCalculation()` and `FinalizeBankingAfterHarmonization()`
> - `BankingCalculator.cs`: Added `CalculateBankingBasic()` and `ApplyJunctionAwareBankingAdjustments()`
> - `NetworkJunctionHarmonizer.cs`: Made `ComputeTJunctionElevation()` banking-aware

> **CRITICAL FIX (Step 5.5.7)**: Fixed adaptive bank angle calculation for secondary roads!
> 
> **The Problem (from the image)**: 
> - Secondary road (going north) was banking in the WRONG direction (left side lower)
> - The cliff artifact at the junction showed the elevation transition wasn't working
> - The exported spline showed completely wrong banking direction
> 
> **Root Cause**:
> The original code in `GetHigherPriorityBankAngle()` simply returned the primary road's bank angle.
> But this is WRONG! The secondary road should NOT adopt the primary road's bank angle. Instead, it
> needs to calculate a **RAMP angle** that makes the secondary road smoothly transition from its
> natural elevation to the primary road's banked surface.
> 
> **The Fix (in `BankingOrchestrator.CalculateAdaptiveBankAngle()`)**:
> 1. Find where the secondary road connects to the primary road
> 2. Calculate the primary road's **surface elevation** at that connection point (using banking)
> 3. Calculate the elevation difference between the secondary road's center and the primary's surface
> 4. Determine which side of the secondary road faces the primary (left or right)
> 5. Calculate a **ramp angle** that tilts the secondary road to meet the primary road's surface
> 
> **Example**: If the primary road is on the right and its surface is 0.5m higher than the secondary
> road's center, and the secondary road is 10m wide (5m half-width):
> - The right edge needs to be 0.5m higher
> - Ramp angle = arcsin(0.5 / 5.0) ? 5.7°
> - This tilts the secondary road UP toward the primary road
> 
> **Files Modified**:
> - `BankingOrchestrator.cs`: Added `CalculateAdaptiveBankAngle()` method
> - `BankingCalculator.cs`: Updated `ApplyJunctionAwareBankingAdjustments()` documentation
> - `JunctionBankingAdapter.cs`: Improved logging and uses junction CS center for surface elevation

### Milestone 4: Pipeline Integration (Week 2)
- [x] Step 5.1: Create `BankedTerrainHelper.cs` for banking-aware terrain blending ? IMPLEMENTED
- [x] Step 6.1: Integrate banking into road smoother orchestration (Phase 3.5) ? IMPLEMENTED
- [x] Modify `RoadMaskBuilder.cs` for banking-aware elevation in protection mask ? IMPLEMENTED
- [x] Modify `ElevationMapBuilder.cs` for banking-aware elevation interpolation ? IMPLEMENTED

### Milestone 5: Export & UI (Week 2-3)
- [x] Step 7.1: Update master spline export ? IMPLEMENTED
- [x] Step 8.1: Add UI controls ? IMPLEMENTED

### Milestone 6: Override System (Week 3+, Future)
- [ ] Step 9.1: Override data structure
- [ ] Step 9.2: Override support in splines

---

## Performance Considerations

1. **Curvature Calculation**: O(n) per spline - minimal impact
2. **Banking Falloff**: Currently O(n²) due to all-pairs blending - optimize with spatial windowing
3. **Edge Elevations**: O(n) - minimal impact
4. **Terrain Blending**: Already optimized with spatial indexing - banking adds minimal overhead

**Optimization Opportunities:**
- Pre-compute curvature during spline sampling (before cross-section generation)
- Use fixed-window falloff instead of all-pairs blending
- Cache lateral offset calculations during terrain blending

---

## References

- BeamNG Master Spline Format: `geom.lua` banking implementation
- AASHTO Green Book: Highway superelevation design standards
- Rodrigues' Rotation Formula: 3D vector rotation around arbitrary axis
