# Road Network Smoothing: Comprehensive Survey & Analysis

**Date:** 2026-02-25
**Scope:** Full analysis of the terrain road smoothing pipeline, focusing on junction bumpiness, connecting road slope issues, and overlapping road problems.

---

## Table of Contents

1. [Pipeline Overview](#1-pipeline-overview)
2. [Phase-by-Phase Inventory & Evaluation](#2-phase-by-phase-inventory--evaluation)
3. [Identified Root Causes of Junction Bumpiness](#3-identified-root-causes-of-junction-bumpiness)
4. [The Overlapping Roads Problem (Highways / 4-Lane Roads)](#4-the-overlapping-roads-problem)
5. [Edge-to-Edge Road Surface Flattening Analysis](#5-edge-to-edge-road-surface-flattening-analysis)
6. [Multi-Material Interaction Issues](#6-multi-material-interaction-issues)
7. [Evaluation Summary Table](#7-evaluation-summary-table)
8. [Improvement Proposals](#8-improvement-proposals)

---

## 1. Pipeline Overview

The road smoothing pipeline is orchestrated by `UnifiedRoadSmoother.SmoothAllRoads()` in `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`. It operates in 10 sequential phases on a unified road network built from all materials:

```
Phase 1:   Build Unified Road Network        (UnifiedRoadNetworkBuilder)
Phase 1.5: Identify Roundabout Splines       (closed-loop detection)
Phase 2:   Calculate Target Elevations        (OptimizedElevationSmoother, per-spline)
Phase 2.3: Structure Elevation Profiles       (StructureElevationIntegrator, bridges/tunnels)
Phase 2.5: Banking Pre-calculation            (BankingOrchestrator, curvature-based)
Phase 2.6: Roundabout Elevation Harmonization (RoundaboutElevationHarmonizer)
Phase 3:   Junction Detection & Harmonization (NetworkJunctionDetector + NetworkJunctionHarmonizer)
Phase 4:   Terrain Blending                   (UnifiedTerrainBlender, single-pass protected)
Phase 5:   Material Painting                  (MaterialPainter, per-spline surface width)
Phase 6:   Post-Processing Smoothing          (PostProcessingSmoother, Gaussian blur)
```

Each road material contributes splines to a `UnifiedRoadNetwork` containing:
- `List<ParameterizedRoadSpline>` — Splines with per-material `RoadSmoothingParameters`
- `List<UnifiedCrossSection>` — Cross-sections sampled at 0.5m intervals (default)
- `List<NetworkJunction>` — Detected junctions with contributors and type classification
- `Dictionary<int, string>` — SplineId-to-MaterialName mapping

---

## 2. Phase-by-Phase Inventory & Evaluation

### Phase 1: Network Building (`UnifiedRoadNetworkBuilder`)

**What it does:**
- Extracts splines from each material (pre-built OSM splines or PNG skeleton extraction)
- Wraps each in `ParameterizedRoadSpline` with priority calculated from OSM road type + width + material order
- Generates cross-sections at `CrossSectionIntervalMeters` (default 0.5m) along each spline
- Filters out splines shorter than one cross-section interval

**File:** `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

**Evaluation:**
- **Strength:** Unified network is the right architecture — enables cross-material junction detection
- **Strength:** Priority system (OSM type > width > material order) provides deterministic conflict resolution
- **Weakness:** Cross-sections are generated per-spline with no awareness of network topology. Two splines meeting at a junction have independent cross-section sequences that may not align spatially at the junction point. This misalignment propagates through all subsequent phases.
- **Weakness:** For parallel carriageways (highways in OSM), two splines at the same priority with similar widths are created without any notion that they form a single road corridor. No "road corridor grouping" exists.

---

### Phase 2: Elevation Calculation (`OptimizedElevationSmoother`)

**What it does:**
1. Samples terrain heightmap at each cross-section center point
2. Applies longitudinal smoothing (Box filter with prefix sums or Butterworth low-pass filter)
3. Optionally applies GlobalLevelingStrength (blends toward network average elevation)
4. Optionally enforces RoadMaxSlopeDegrees (iterative forward-backward constraint)

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs`

**Evaluation:**
- **Strength:** Butterworth filter is a sound choice — maximally flat passband, zero-phase forward-backward filtering eliminates phase shift
- **Strength:** Box filter with prefix sums is O(N), good for flat terrain
- **Strength:** Max slope constraint with iterative convergence handles steep terrain well
- **CRITICAL WEAKNESS: Per-spline isolation.** Each spline is smoothed entirely independently. The smoother has no knowledge of:
  - Where the spline connects to other splines (junctions)
  - What elevation other splines have at shared junction points
  - Whether the smoothed endpoint elevation will be compatible with the junction harmonizer's later calculation

  **Impact:** On hilly terrain, a spline's smoothed endpoint can be meters away from the junction's eventual harmonized elevation. The junction harmonizer (Phase 3) then has to force a large correction over the blend distance, creating a visible "ramp" or "kink" in the first 10-30m of the connecting road.

- **WEAKNESS: No endpoint anchoring.** The smoothing filter treats endpoints the same as mid-spline points. Ideally, if a spline endpoint will connect to a junction, its elevation should be anchored to reduce the correction needed later.

---

### Phase 2.5: Banking Pre-calculation (`BankingOrchestrator`)

**What it does:**
- Calculates curvature at each cross-section point
- Converts curvature to bank angle via `bankAngle = min(curvature * CurvatureToBankScale, 1) * MaxBankAngleDegrees * BankStrength`
- Computes left/right edge elevations from bank angle and road width
- Applies falloff blending for smooth banking transitions

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingOrchestrator.cs`

**Evaluation:**
- **Strength:** Banking must happen before junction harmonization so the harmonizer can use surface elevations — correct ordering
- **Strength:** Roundabout splines excluded from banking (flat ring elevation)
- **Weakness:** Banking at junction approaches is not suppressed. When a road curves into a T-junction, the bank angle at the endpoint cross-section creates a tilted surface that the terminating road must match. The `JunctionSurfaceCalculator.ApplyEdgeConstraints()` handles this, but the transition from "banked approach" to "flat junction plateau" can still be abrupt if the banking changes rapidly near the junction.

---

### Phase 2.6: Roundabout Elevation Harmonization (`RoundaboutElevationHarmonizer`)

**What it does:**
- Detects connection points where roads meet roundabout rings
- Calculates uniform ring elevation (weighted average of terrain + connection elevations)
- Blends connecting roads toward ring elevation over configurable distance (default 50m)
- Marks roundabout junctions as excluded from general harmonization

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs`

**Evaluation:**
- **Strength:** Separate handling for roundabouts is correct — they have fundamentally different geometry (closed loops, uniform elevation)
- **Strength:** Connection road blending uses up to 75% of road length to prevent affecting the far end
- **Weakness:** The ring elevation is a single value. On sloped terrain, a perfectly flat ring may look unnatural and create steep embankments on the downhill side. A slight tilt following the terrain gradient would be more realistic.
- **Weakness:** Marking roundabout junctions as excluded prevents the general harmonizer from processing them, but connecting roads that also meet OTHER roads at their far ends can still have issues.

---

### Phase 3: Junction Detection & Harmonization

#### 3a. Detection (`NetworkJunctionDetector`)

**What it does:**
- Collects first/last cross-sections of each spline as endpoints
- Builds spatial grid for efficient proximity queries
- Clusters nearby endpoints using Union-Find with configurable radius (default 10m)
- Detects T-junctions (endpoint touching middle of another road)
- Detects mid-spline crossings (two roads crossing without terminating)
- Classifies junctions: Endpoint, TJunction, YJunction, CrossRoads, Complex, MidSplineCrossing, Roundabout

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs`

**Evaluation:**
- **Strength:** Union-Find is the right choice for transitive clustering — if A is near B and B is near C, all three form one junction
- **Strength:** T-junction detection via mid-spline proximity check catches roads that meet another road's side
- **WEAKNESS: Detection radius is too coarse for dense junctions.** With default 10m radius, two separate junctions that are 15m apart are detected as separate, but the blend zones (30m each) overlap significantly. The harmonizer treats them independently.
- **WEAKNESS: No awareness of road width.** The detection radius is purely geometric. A 20m-wide highway should have a larger detection radius than a 4m path. The per-material override exists (`JunctionHarmonizationParameters.JunctionDetectionRadiusMeters`) but the default is 5m, which may not be enough for wide roads.

#### 3b. Crossroad-to-T-Junction Conversion (`CrossroadToTJunctionConverter`)

**What it does:**
- Finds mid-spline crossings and splits the secondary road at the crossing point
- Creates two T-junctions from one crossing (primary road continues, secondary terminates on both sides)
- Existing T-junction logic then handles harmonization

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/CrossroadToTJunctionConverter.cs`

**Evaluation:**
- **Strength:** Elegant reduction — converts a hard problem (2 continuous roads crossing) into 2 instances of the well-solved T-junction problem
- **Weakness:** The "primary" road selection uses priority, then length as tiebreaker. For equal-priority same-material roads, the longer road always wins, which may not be correct if the shorter road is straighter or more important locally.

#### 3c. Harmonization (`NetworkJunctionHarmonizer`)

**What it does:**
1. Computes harmonized elevation per junction type:
   - **T-Junction:** Continuous road wins; surface-aware calculation accounts for banking AND longitudinal slope at the connection point
   - **Y/X/Complex:** Priority-weighted average; equal-priority uses geometric heuristics (road length, alignment angle)
   - **Endpoints:** Blend to terrain elevation
   - **Mid-Spline Crossing:** Priority²-weighted average (squaring emphasizes higher priority)
2. Propagates constraints along affected splines using blend function (Cosine/Cubic/Quintic)
3. Handles overlapping blend zones by accumulating weighted influences from all nearby junctions
4. Applies endpoint tapering for isolated road ends
5. Applies multi-way junction plateau smoothing

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Evaluation:**
- **Strength:** T-junction surface-aware calculation is sophisticated — accounts for both banking (lateral tilt) and longitudinal slope (grade). This is the correct approach.
- **Strength:** Edge constraints (`ConstrainedLeftEdgeElevation`, `ConstrainedRightEdgeElevation`) provide per-edge control at junction cross-sections
- **Strength:** Overlapping blend zone handling via accumulated weighted influences prevents independent junction zones from creating "steps" at boundaries
- **Strength:** The `SmallElevationDifferenceMeters` (0.5m) threshold avoids unnecessary ramps when roads are nearly at the same height

- **WEAKNESS: Propagation operates on cross-section center elevations only, then edge constraints separately.** The edge constraint propagation (`PropagateEdgeConstraintsForTJunctions`) happens in a third pass after center elevation propagation. If the center elevation propagation and edge constraint propagation don't agree on the same surface geometry, the road can have "twisted" cross-sections where the center is at one elevation but the edges follow a different slope.

- **WEAKNESS: Junction plateau smoothing is an afterthought.** `ApplyMultiWayJunctionPlateauSmoothing` runs after all other harmonization. It samples ORIGINAL (pre-harmonization) elevations and blends them in. On steep terrain, this can partially undo the harmonization work by pulling elevations back toward the original terrain surface.

- **WEAKNESS: The propagation distance is the same for all junction types.** A complex highway interchange needs much longer blend distances (100m+) than a residential T-junction (15m). The global `JunctionBlendDistanceMeters` doesn't adapt.

- **CRITICAL WEAKNESS: No iterative refinement.** The pipeline is single-pass: smooth → harmonize → blend. There's no feedback loop where the harmonizer's output feeds back into the smoother for re-smoothing. The result is that Phase 2's smoothing creates an elevation profile that Phase 3 then "patches" with junction corrections, but the patches themselves are not smoothed.

---

### Phase 4: Terrain Blending (`UnifiedTerrainBlender`)

**What it does:**
1. Builds combined road core mask (`RoadMaskBuilder.BuildCombinedRoadCoreMask`)
2. Builds protection mask with ownership tracking (`BuildRoadCoreProtectionMaskWithOwnership`)
3. Computes Euclidean Distance Transform (EDT) from combined mask
4. Builds elevation map with ownership (`ElevationMapBuilder.BuildElevationMapWithOwnership`)
5. Applies protected blending (`ProtectedBlendingProcessor.ApplyProtectedBlending`)

**Files:**
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/UnifiedTerrainBlender.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs`

**Evaluation:**

**RoadMaskBuilder:**
- **Strength:** Scanline rasterization for polygon fill is efficient and correct
- **Strength:** Priority-based ownership resolution (higher priority overwrites lower)
- **WEAKNESS: Non-banked road segments use average elevation of two adjacent cross-sections.** This means if `cs1.TargetElevation = 100.0` and `cs2.TargetElevation = 100.5`, the ENTIRE quad between them gets 100.25. There's no gradient along the road direction within the segment. With 0.5m cross-section spacing, these 0.25m jumps between segments create the "staircase" effect visible as bumpiness on the road surface.
- **WEAKNESS: Equal-priority overlapping roads (parallel carriageways) have arbitrary ownership.** The first spline processed claims pixels; the second gets them only if it has higher priority. For same-material same-priority roads, this creates a "first come first served" pattern that doesn't reflect actual road geometry.

**ElevationMapBuilder:**
- **Strength:** Dual strategy (OSM: all-neighbor IDW, PNG: single-spline IDW) addresses different data characteristics
- **Strength:** Pre-computed protection mask ownership prevents nearest-cross-section ambiguity in road cores
- **CRITICAL WEAKNESS: IDW for OSM roads at junctions.** The `InterpolateNearbyCrossSectionsBuffered` method finds ALL cross-sections within the search radius and uses 1/d² weighting. At a junction, cross-sections from Road A (at elevation 100m) and Road B (at elevation 102m) both contribute. A pixel between the two roads gets a blended elevation ~101m that belongs to neither road. This creates a "dome" or "valley" artifact at the junction center.
- **WEAKNESS: The dominant owner is determined by priority, but elevation is from IDW of ALL cross-sections.** The ownership and elevation can be inconsistent — a pixel may be "owned" by the highway but its elevation is influenced by the side road's cross-sections.

**ProtectedBlendingProcessor:**
- **Strength:** Road core pixels are absolutely protected — never modified by blend zones
- **Strength:** Higher-priority roads' protection zones override lower-priority blend calculations
- **Strength:** `EnforceSideMaxSlope` prevents impossible embankment angles
- **Strength:** Per-spline blend range calculation (`CalculateDistanceToOwningSpline` using lateral offset)
- **WEAKNESS: The distance-to-owner calculation uses lateral offset from the nearest cross-section's normal.** At road curves and junctions, the normal direction can change rapidly, causing the "distance to road" to jump between cross-sections. This creates irregular blend zone boundaries.
- **WEAKNESS: The blend function operates in 1D (distance from road edge).** It has no awareness of the 2D junction geometry. A pixel 5m from Road A's edge but 20m from the junction center gets the same blend factor as a pixel 5m from Road A's edge at the junction center. The junction area needs a 2D blend kernel, not a 1D radial function.

---

### Phase 6: Post-Processing Smoothing (`PostProcessingSmoother`)

**What it does:**
- Applies Gaussian/Box/Bilateral blur to road surface pixels + shoulder extension
- Operates per-material parameter group
- Handles junction overlap by expanding masks where different parameter groups meet

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/Blending/PostProcessingSmoother.cs`

**Evaluation:**
- **Strength:** Bilateral filter option preserves road edges better than Gaussian
- **Weakness:** Fixed kernel size regardless of road width or elevation difference. A 7px kernel at 4m/px = 28m smoothing window, which may not be enough for highway junctions but is too much for narrow paths.
- **Weakness:** Smoothing is applied AFTER all elevation work is done. It can soften visible artifacts but cannot fix structural problems (e.g., a 1m elevation mismatch at a junction becomes a 1m bump spread over 28m instead of a sharp step — still wrong, just softer).
- **Weakness:** The mask extension (`SmoothingMaskExtensionMeters`, default 6m) means smoothing bleeds into the shoulder area. On narrow roads close together, the shoulder areas overlap and the smoothing can create unexpected terrain modifications between roads.

---

## 3. Identified Root Causes of Junction Bumpiness

### Root Cause 1: Per-Spline Smoothing Creates Junction Elevation Mismatches

**The Problem:**
Phase 2 smooths each spline independently. On terrain with, say, 5% grade, a road running uphill will have its endpoint smoothed to an elevation that reflects the smoothing window's average — which is offset from the actual terrain at that point. A road approaching the same junction from a different direction will be smoothed with a completely different context.

**Example:** Consider a T-junction on a 5% grade hillside:
- Road A runs east-west (along the contour), smooth elevation ~200m
- Road B runs north-south (uphill), smoothed endpoint ~203m (because the Butterworth filter pulls the endpoint toward the road's average, which is lower)
- Junction harmonizer sets harmonized elevation to ~200m (Road A wins as continuous)
- Road B must drop 3m over the blend distance (30m) = visible downward ramp

**Severity:** HIGH on hilly terrain, LOW on flat terrain

### Root Cause 2: Cross-Section-to-Pixel Discretization Creates Staircasing

**The Problem:**
Cross-sections are sampled every 0.5m along the road. The road core is rasterized as quad polygons between consecutive cross-sections. For non-banked roads, each quad gets the AVERAGE elevation of its two bounding cross-sections. Since elevation changes along the road, consecutive quads have slightly different flat elevations, creating a staircase pattern.

**Impact:** At 4m/px terrain resolution with 0.5m cross-section spacing, a single terrain pixel spans ~8 cross-section intervals. The pixel-level elevation is determined by whichever cross-section pair's quad center hits the pixel center, which may not be the optimal interpolation.

**Severity:** MEDIUM — partially mitigated by post-processing Gaussian blur, but still visible on steep roads

### Root Cause 3: IDW Elevation Mixing at Junctions

**The Problem:**
`ElevationMapBuilder` uses inverse-distance-weighted interpolation from ALL nearby cross-sections for OSM roads. At a junction, cross-sections from multiple roads with different target elevations coexist in the same spatial region. The IDW creates a smooth blend between them, but this blend doesn't follow any road's actual surface — it's an arbitrary average that creates a "bump" or "dip" at the junction center.

**Example:** At a T-junction:
- Main road cross-sections at elevation 100.0m (flat, running east-west)
- Side road cross-sections at elevation 101.5m (descending from north)
- Junction center pixel is equidistant from both: IDW gives ~100.75m
- But the junction should be flat at 100.0m (main road wins)
- Result: 0.75m bump at junction center that shouldn't exist

**Severity:** HIGH — this is the single biggest contributor to junction bumpiness

### Root Cause 4: Protection Mask Gaps at Junction Boundaries

**The Problem:**
The protection mask is built from road core polygons (quad segments between cross-sections). At junctions where roads meet at angles, there are GAPS between the quads of different roads — triangular areas where no road core polygon covers. These gaps fall through to blend zone processing, which uses IDW, creating the artifacts described in Root Cause 3.

**Severity:** MEDIUM — the gaps are typically small (a few pixels) but they're exactly at the junction center where accuracy matters most

### Root Cause 5: Blend Zone Boundary Discontinuities for Overlapping Roads

**The Problem:**
When two roads with the same priority run parallel (highway carriageways), their blend zones overlap. The blend calculation for each pixel uses the distance to the "owning" spline, but ownership at the boundary between two equal-priority splines is determined by which was processed first. A pixel can be "owned" by Road A (distance 6m) even though Road B is only 4m away but was processed second. The resulting blend factor is wrong for that pixel.

**Severity:** HIGH for highways/multi-lane roads, LOW for isolated roads

### Root Cause 6: Junction Harmonization Blend is Not Re-Smoothed

**The Problem:**
After Phase 3 modifies cross-section elevations to enforce junction constraints, the modified elevations go directly into Phase 4 (terrain blending). There's no re-smoothing step to ensure the "patched" elevation profile is actually smooth. The blend function (Cosine, Cubic) creates a mathematically smooth transition in the 1D distance-from-junction domain, but when rasterized to 2D terrain pixels, the C0/C1 continuity is lost at:
- The boundary where the junction blend zone ends and the original smoothed profile begins
- The overlap region where two junction blend zones meet
- The corners of the junction where blend zone shape is irregular

**Severity:** HIGH — this is why the "first meters" of connecting roads are visibly bumpy

### Root Cause 7: No Junction Plateau Geometry

**The Problem:**
Real junctions have a flat or gently curved plateau at the intersection center. The current pipeline has no concept of a junction plateau — the junction is just the point where spline endpoints meet, and the harmonized elevation is a single value. There's no 2D area defined as "the junction surface" where elevation should be held flat.

**Severity:** HIGH — the junction center is determined entirely by IDW interpolation of nearby cross-sections, with no explicit flat area

---

## 4. The Overlapping Roads Problem

### How OSM Represents Multi-Lane Roads

OSM represents divided highways as two separate `way` elements — one for each direction of travel. These are typically 5-15m apart depending on the median width. Both ways share the same `highway=*` tag and thus get the same priority in the current system.

### Current Handling

1. **Spline creation:** Each way becomes an independent spline. No grouping or corridor detection.
2. **Cross-sections:** Each spline gets its own cross-sections. The two sets of cross-sections overlap in the median area.
3. **Elevation smoothing:** Each spline is smoothed independently. On terrain with cross-slope, the two carriageways may end up at slightly different elevations.
4. **Protection mask:** Both splines' road cores are rasterized. In the overlap zone, the first-processed spline claims pixels. This is arbitrary.
5. **IDW elevation:** In the median area, both splines' cross-sections contribute. The IDW blend creates an elevation that's the average of both, which may not match either road's surface.
6. **Blend zone:** The area BETWEEN the two carriageways is technically in the blend zone of both roads. Each pixel gets the nearer road's blend calculation, creating a V-shaped valley or ridge between the carriageways.

### Why This Fails

The fundamental issue is that the system treats each carriageway as an independent road with its own elevation profile. In reality, both carriageways should share a single "road corridor" elevation profile, and the median between them should be at a natural grade between the two surfaces.

**Visible artifacts:**
- Median ridge/valley between carriageways
- Inconsistent banking (one carriageway may bank left while the other banks right, creating a discontinuity)
- Junction areas where one carriageway's blend zone interferes with the other carriageway's road core

---

## 5. Edge-to-Edge Road Surface Flattening Analysis

### What "Edge-to-Edge Flattening" Means

For a road cross-section without banking, the terrain should be flat from the left road edge to the right road edge at the `TargetElevation`. For banked roads, the surface should tilt linearly from left edge elevation to right edge elevation.

### Current Implementation

**Road core rasterization** (`RoadMaskBuilder`):
- For each pair of consecutive cross-sections, creates a quad polygon (left1, right1, right2, left2)
- If banking: Calls `BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos)` per-pixel — this correctly interpolates
- If no banking: Uses `GetSegmentAverageElevation(cs1, cs2)` = `(cs1.TargetElevation + cs2.TargetElevation) / 2` for the ENTIRE quad

**The problem with the non-banked case:**
The average elevation means ALL pixels in the quad get the same elevation. Between one quad and the next, elevation jumps by `(cs_i+1.TargetElevation - cs_i.TargetElevation) / 2`. With 0.5m spacing and typical grades, these jumps are small but accumulate visually as a ribbed texture on the road surface.

**The problem with the banked case at junctions:**
`GetBankedElevationForPixel` calls `GetBankedElevationInSegment` which:
1. Calculates interpolation parameter `t` along the segment
2. Interpolates banking angle, center elevation, normal direction
3. Calculates per-pixel elevation from lateral offset and interpolated bank angle

This is correct in principle, but at junction cross-sections with `HasJunctionConstraint`, the edge elevations come from `ConstrainedLeftEdgeElevation` and `ConstrainedRightEdgeElevation`. These constrained values are computed by projecting onto the primary road's surface. If the primary road is banked AND sloped, the projection involves both lateral and longitudinal components, and the interpolation between constrained and non-constrained cross-sections can be non-linear.

### Where It Breaks Down

1. **Junction approach:** The last few cross-sections before a junction have constrained edges, but the cross-sections further back do not. The transition from "normal banking" to "constrained edges" happens over 0-2 cross-section intervals (0-1m), which is too abrupt on steep terrain.

2. **Road width changes at junctions:** If two roads of different widths meet, the terminating road's cross-section width doesn't change, but the constraint forces its edges to match the wider road's surface. The resulting geometry can have overlapping quad polygons.

3. **Cross-section normal discontinuities:** At sharp turns near junctions, the normal direction can flip between consecutive cross-sections. The quad polygon becomes self-intersecting (bowtie shape), causing incorrect rasterization.

---

## 6. Multi-Material Interaction Issues

### Different Materials, Different Parameters

The system supports multiple road materials with independent parameters:
- `AsphaltRoad`: width 8m, blend range 12m, Butterworth smoothing (order 4), priority 80
- `DirtRoad`: width 4m, blend range 6m, Box filter, priority 40
- `HighwayRoad`: width 12m, blend range 20m, Butterworth (order 6), priority 100

### Cross-Material Junction Handling

When a dirt road meets an asphalt road:
1. **Detection:** Works correctly — endpoints from both materials clustered into one junction
2. **Harmonization:** With `EnableCrossMaterialHarmonization = true`, the asphalt road (higher priority) wins
3. **Blend distance:** The asphalt road's 30m blend distance is used for the asphalt side, but the dirt road's 15m distance may be used for the dirt side. This asymmetry can create a lopsided junction.

### Cross-Material Blend Zone Conflicts

The critical issue: When the asphalt road's blend zone (extending 12m from the road edge) overlaps with the dirt road's road core (which is only 2m from the centerline), the `ProtectedBlendingProcessor` correctly uses the dirt road's protection mask to prevent the asphalt blend from modifying dirt road core pixels. But the dirt road's blend zone (extending only 6m) may not reach the asphalt road's core, leaving a gap of unmodified terrain between the two roads' influence zones.

---

## 7. Evaluation Summary Table

| Effort / Feature | What It Addresses | Effectiveness | Key Weakness |
|---|---|---|---|
| **Butterworth longitudinal smoothing** | Road-following elevation | Good for isolated roads | No junction awareness; endpoints diverge |
| **Junction detection (Union-Find)** | Finding where roads meet | Good for simple junctions | Radius not width-aware; no corridor grouping |
| **T-Junction surface-aware harmonization** | Banking + slope at T-junctions | Very good for 2-road junctions | Surface projection accuracy depends on cross-section alignment |
| **Crossroad-to-T-Junction conversion** | Handling mid-spline crossings | Elegant architectural solution | Only handles 2 roads crossing; priority tiebreaker for equal roads |
| **Multi-way junction geometric heuristics** | Equal-priority junctions | Reasonable for simple cases | Length/angle heuristics fail for complex interchanges |
| **Junction constraint propagation** | Blending junction elevation back along roads | Good concept | Single-pass; no re-smoothing; C1 discontinuity at blend boundary |
| **Overlapping blend zone accumulation** | Dense networks with close junctions | Addresses the step artifact | Weighted average doesn't guarantee smooth surface |
| **Edge constraint propagation** | T-junction road surface matching | Correct approach | Too-abrupt transition from constrained to unconstrained edges |
| **Protection mask with ownership** | Preventing cross-road elevation pollution | Essential for multi-road areas | Equal-priority conflict resolution is first-come-first-served |
| **IDW elevation interpolation** | Smooth per-pixel elevation in blend zones | Good for isolated roads | Creates artifacts at junctions (cross-road elevation mixing) |
| **Priority-based ownership** | Deterministic conflict resolution | Works for mixed-priority roads | Equal-priority roads (highways) not handled |
| **EDT distance field** | Fast road proximity computation | Excellent performance (single pass) | Global distance doesn't distinguish between roads |
| **Side max slope enforcement** | Preventing impossible embankments | Good constraint | Doesn't adapt slope to local terrain curvature |
| **Post-processing Gaussian blur** | Smoothing staircase artifacts | Mild improvement | Doesn't fix structural problems; fixed kernel size |
| **Bilateral filter option** | Edge-preserving smoothing | Better than Gaussian for road edges | Still can't fix junction geometry issues |
| **Roundabout elevation harmonization** | Flat roundabout rings | Good for simple roundabouts | Flat ring on sloped terrain creates steep embankments |
| **Banking pre-calculation** | Road superelevation | Correct physics model | Banking at junction approaches not suppressed |
| **Road corridor (lacking)** | Grouped parallel roads | NOT IMPLEMENTED | Fundamental gap for highways |

---

## 8. Improvement Proposals

### Proposal 1: Junction-Aware Elevation Smoothing (Network-Constrained Smoothing)

**Problem addressed:** Root Cause 1 (per-spline smoothing ignores junctions)

**Concept:** After junction detection (move detection before Phase 2), add junction endpoint elevations as ANCHORING CONSTRAINTS for the longitudinal smoother. Each spline's endpoint that connects to a junction gets a "target elevation hint" — the terrain elevation at the junction point. The smoother then treats these as soft constraints, pulling the endpoint toward the hint value.

**Algorithm sketch:**
```
1. Run junction detection EARLY (before Phase 2)
2. For each junction, sample terrain elevation at junction center
3. For each spline endpoint at a junction:
   a. Set anchor elevation = terrain height at junction point
   b. Set anchor weight = 0.5 (blends with smoothing filter)
4. Modify OptimizedElevationSmoother:
   a. After Box/Butterworth filtering
   b. Apply exponential decay from endpoints:
      elevation[i] = lerp(smoothed[i], anchor, exp(-dist_from_endpoint / anchor_decay_meters))
   c. anchor_decay_meters = JunctionBlendDistanceMeters
```

**Where it fits:** Between current Phase 1 and Phase 2 (reorder junction detection to run first for detection only, then elevation calculation, then full harmonization).

**Expected impact:** Reduces the correction needed in Phase 3 by 50-80% on hilly terrain, directly reducing the "first meters" bumpiness.

**Complexity:** Medium — requires restructuring phase ordering and adding endpoint anchoring to the smoother.

---

### Proposal 2: Junction Plateau Area with 2D Elevation Kernel

**Problem addressed:** Root Causes 3, 4, and 7 (IDW mixing, protection mask gaps, no plateau geometry)

**Concept:** Define an explicit junction AREA (not just a point) for each detected junction. Within this area, use a dedicated elevation calculation that produces a smooth, flat or gently sloped surface — not the IDW of surrounding cross-sections.

**Algorithm sketch:**
```
1. For each junction, calculate a junction polygon:
   - Convex hull of all contributing cross-section endpoints
   - Expanded by max(roadWidth/2) of all contributing roads
   - This defines the "junction plateau"

2. Within the junction polygon:
   - Set elevation to harmonizedElevation (or interpolated surface for banked/sloped primaries)
   - Mark these pixels as "junction core" in the protection mask (highest priority)
   - These pixels are NEVER processed by IDW or blend zone logic

3. For pixels outside the junction polygon but within blend range:
   - Blend from junction surface elevation to terrain elevation
   - Use 2D distance from junction polygon boundary (not from individual cross-sections)
```

**Where it fits:** New sub-step in Phase 4, before ElevationMapBuilder runs.

**Expected impact:** Eliminates junction center bumps entirely. Creates visually flat intersection areas.

**Complexity:** High — requires junction polygon computation, new rasterization step, integration with existing protection mask.

---

### Proposal 3: Road Corridor Grouping for Parallel Carriageways

**Problem addressed:** Root Cause 5 and Section 4 (overlapping roads)

**Concept:** Detect parallel splines that represent the same road corridor (dual carriageways) and treat them as a unified super-spline for elevation purposes.

**Algorithm sketch:**
```
1. After network building, detect corridor groups:
   a. For each pair of same-material splines:
      - Calculate average centerline distance
      - Calculate angular alignment (parallel test)
      - If distance < 2 * maxRoadWidth AND alignment > 170°: CORRIDOR PAIR
   b. Group transitive pairs into corridors

2. For each corridor group:
   a. Calculate a single elevation profile for the corridor centerline
   b. Apply banking across the FULL corridor width (including median)
   c. Each carriageway gets its elevation from the corridor profile + lateral offset
   d. The median area gets smooth interpolation between carriageways

3. In the protection mask:
   - Corridor groups share ownership for the median area
   - Blend zone extends from the OUTER edges, not from each carriageway independently
```

**Where it fits:** New Phase 1.7 between network building and elevation calculation.

**Expected impact:** Eliminates median ridge/valley artifacts on highways. Consistent banking across corridors.

**Complexity:** High — requires corridor detection heuristic, new elevation profile abstraction, modifications to several existing phases.

---

### Proposal 4: Iterative Junction Refinement (Smooth-Harmonize-Resmooth)

**Problem addressed:** Root Cause 6 (harmonized elevations not re-smoothed)

**Concept:** Run the smoothing and harmonization in 2-3 iterations. Each iteration reduces the residual mismatch between smoothed profiles and junction constraints.

**Algorithm sketch:**
```
Iteration 1 (current pipeline):
  Phase 2: Smooth elevations
  Phase 3: Harmonize junctions → modifies N cross-sections

Iteration 2 (NEW):
  Re-run Phase 2 smoothing on modified elevations
  Re-run Phase 3 harmonization (corrections will be smaller)

(Optional) Iteration 3:
  If max correction in iteration 2 > threshold: repeat
```

**Where it fits:** Wrap Phases 2-3 in a convergence loop.

**Expected impact:** The ramp artifacts at junction approaches become progressively smoother. After 2 iterations, the correction is typically <10% of the original, making it visually imperceptible.

**Complexity:** Low — minimal code changes. The smoother and harmonizer are already pure functions that can be re-invoked. Main concern is performance (2-3x slower for these phases).

---

### Proposal 5: Gradient-Continuous Junction Blending (C1 Continuity)

**Problem addressed:** Root Cause 6 (C0 but not C1 continuity at blend boundaries)

**Concept:** Replace the current blend function (which only guarantees elevation continuity at the boundary) with a Hermite-spline-based blend that also matches the SLOPE at both ends.

**Algorithm sketch:**
```
Current approach (C0):
  At boundary: elevation matches ✓, slope may jump ✗

Proposed approach (C1 Hermite):
  At junction end (t=0):
    elevation = junction.HarmonizedElevation
    slope = primary road's longitudinal slope (from CalculatePrimaryRoadSlope)
  At blend boundary (t=1):
    elevation = original smoothed elevation
    slope = original smoothed slope (finite difference of adjacent cross-sections)

  For t ∈ [0, 1]:
    Use cubic Hermite interpolation: h(t) = H00*e0 + H10*s0 + H01*e1 + H11*s1
    where H00, H10, H01, H11 are Hermite basis functions
```

**Where it fits:** Replace `ApplyBlendFunction` in `PropagateJunctionConstraints`.

**Expected impact:** Eliminates the visible "kink" at the boundary where junction blend meets original smoothed profile.

**Complexity:** Low — the Hermite interpolation is a simple function replacement. Main challenge is calculating the slope at the blend boundary from the existing cross-section data.

---

### Proposal 6: Per-Pixel Bilinear Road Core Elevation

**Problem addressed:** Root Cause 2 (staircase from per-segment averaging)

**Concept:** Instead of using the average elevation for non-banked road segments, interpolate elevation along the road direction within each quad segment.

**Algorithm sketch:**
```
Current (RoadMaskBuilder, non-banked):
  elevation = (cs1.TargetElevation + cs2.TargetElevation) / 2  // flat per-quad

Proposed:
  For each pixel (x, y) within the quad:
    1. Project pixel position onto segment direction
    2. Calculate t = fraction along segment [0, 1]
    3. elevation = lerp(cs1.TargetElevation, cs2.TargetElevation, t)
  This creates a smooth gradient along the road instead of flat steps.
```

**Where it fits:** Replace `GetSegmentAverageElevation` with per-pixel interpolation in `FillConvexPolygonWithOwnershipAndBanking`.

**Expected impact:** Eliminates staircase effect on road surfaces. The road becomes a continuous ramp instead of discrete steps.

**Complexity:** Low — the banked code path already does per-pixel calculation. Simply apply the same approach to non-banked roads.

---

### Proposal 7: Junction-Aware IDW with Road-Context Filtering

**Problem addressed:** Root Cause 3 (IDW elevation mixing at junctions)

**Concept:** When computing IDW elevation for a pixel in the blend zone near a junction, filter out cross-sections that belong to a DIFFERENT road than the pixel's owner, unless they're within the junction plateau area (where shared elevation is desired).

**Algorithm sketch:**
```
Current (InterpolateNearbyCrossSectionsBuffered):
  All nearby cross-sections contribute with 1/d² weighting

Proposed:
  For each nearby cross-section:
    if cs.OwnerSplineId == pixel.OwnerSplineId:
      weight = 1/d²  // Full weight for same-road cross-sections
    else if pixel is within junction plateau area:
      weight = 1/d²  // Full weight — junction should blend
    else:
      weight = 0      // Reject cross-road contamination
```

**Where it fits:** Modify `InterpolateNearbyCrossSectionsBuffered` in `ElevationMapBuilder.cs`.

**Expected impact:** Eliminates elevation pollution from neighboring roads in blend zones. Junction plateaus still get proper blending.

**Complexity:** Medium — requires junction polygon lookup during per-pixel processing (needs spatial index for junction areas).

---

### Proposal 8: Adaptive Blend Distance Based on Elevation Difference

**Problem addressed:** The fixed 30m blend distance being too short for large elevation differences on steep terrain

**Concept:** Scale the blend distance based on the elevation difference that needs to be bridged. Larger elevation corrections need longer blend distances to maintain acceptable slopes.

**Algorithm sketch:**
```
elevationDiff = abs(junctionElevation - splineEndpointElevation)
minBlendDistance = JunctionBlendDistanceMeters  // configured minimum
slopeBasedDistance = elevationDiff / tan(desiredMaxSlopeDegrees)
effectiveBlendDistance = max(minBlendDistance, slopeBasedDistance)
```

**Where it fits:** In `PropagateJunctionConstraints`, compute per-contributor blend distance instead of using the global value.

**Expected impact:** Steep terrain corrections get spread over longer distances, producing gentle slopes instead of visible ramps.

**Complexity:** Very Low — a few lines in the propagation loop.

---

### Priority Ranking of Proposals

| # | Proposal | Impact | Complexity | Recommended Priority |
|---|----------|--------|------------|---------------------|
| 6 | Per-pixel bilinear road core elevation | Medium | Low | 1st — Easy win, fixes staircase |
| 8 | Adaptive blend distance | Medium-High | Very Low | 2nd — Easy win, fixes steep terrain |
| 5 | C1 Hermite junction blending | High | Low | 3rd — Fixes kink at blend boundary |
| 4 | Iterative junction refinement | High | Low | 4th — Reduces all junction artifacts |
| 1 | Junction-aware elevation smoothing | High | Medium | 5th — Addresses root cause |
| 7 | Junction-aware IDW filtering | High | Medium | 6th — Fixes elevation pollution |
| 2 | Junction plateau area | Very High | High | 7th — Most complete fix but complex |
| 3 | Road corridor grouping | High | High | 8th — Needed for highways but complex |
