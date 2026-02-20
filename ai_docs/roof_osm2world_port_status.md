# Roof OSM2World Port — Status & Next Steps

## Date: 2026-02-18

## What Was Done

### Session 1 (2026-02-17): Initial Port
Rewrote the roof generation to use OSM2World's proven approach instead of our custom polygon-splitting code. Ported `FaceDecompositionUtil` and rewrote `RoofGenerator` to use face decomposition for both gabled and hipped roofs. Code compiled but didn't produce visible improvement.

### Session 2 (2026-02-18): Modular Class Hierarchy + Bug Fixes
Refactored from monolithic `RoofGenerator` into a proper class hierarchy matching OSM2World's Java architecture 1:1. Fixed critical bugs in `FaceDecompositionUtil` and added vertex snapping for graph connectivity.

---

## Current Architecture

### New Files (modular roof class hierarchy — 1:1 port of OSM2World Java)

1. **NEW: `BeamNG.Procedural3D/Building/Roof/HeightfieldRoof.cs`**
   - Abstract base class (port of `HeightfieldRoof.java`)
   - `GenerateMesh()` — face decomposition + triangulation + height assignment
   - `GetRoofHeightAt()` — with interpolation fallback from nearest segment
   - Abstract members: `GetPolygon()`, `GetInnerPoints()`, `GetInnerSegments()`, `GetRoofHeightAtNoInterpolation()`

2. **NEW: `BeamNG.Procedural3D/Building/Roof/RoofWithRidge.cs`**
   - Abstract ridge-based roof (port of `RoofWithRidge.java`)
   - Constructor calculates ridge, caps, maxDistanceToRidge (1:1 port of Java constructor)
   - `GetRoofHeightAtNoInterpolation()` — linear falloff from ridge
   - Ridge direction detection, polygon simplification, utility methods

3. **NEW: `BeamNG.Procedural3D/Building/Roof/GabledRoof.cs`**
   - Concrete class (port of `GabledRoof.java`)
   - `relativeRoofOffset = 0`, inserts ridge endpoints into polygon
   - `GetInnerSegments()` = just the ridge (with vertex snapping)
   - Generates gable walls via `GenerateAdditionalGeometry()`

4. **NEW: `BeamNG.Procedural3D/Building/Roof/HippedRoof.cs`**
   - Concrete class (port of `HippedRoof.java`)
   - `relativeRoofOffset = 1/3`, polygon unchanged
   - `GetInnerSegments()` = ridge + 4 hip lines (with cap vertex snapping)

### Modified Files

5. **MODIFIED: `BeamNG.Procedural3D/Building/BuildingMeshGenerator.cs`**
   - Uses new modular classes via `GenerateRoof()` method

6. **MODIFIED: `BeamNG.Procedural3D/Building/RoofGenerator.cs`**
   - Simplified to thin facade delegating to new classes

7. **MODIFIED: `BeamNG.Procedural3D/Building/FaceDecompositionUtil.cs`**
   - Rewritten in previous session (proper polygon centroid, indexOf fallback)

### Key Bug Fixes in the Rewrite

- **`GetPointInside`**: Uses proper polygon centroid (shoelace formula) instead of centroid of first 3 vertices — this was the most likely cause of the off-by-1 face count in session 1
- **Face enumeration `indexOf` fallback**: Matches Java's silent `(-1+1)%size=0` behavior
- **Vertex snapping**: Inner segment endpoints are snapped to actual polygon vertices via `FindNearestVertex()` to ensure PointPool connectivity

---

## Architecture (How OSM2World Does It)

The key insight is that OSM2World does NOT manually split polygons into left/right faces. Instead:

1. **Define inner segments** — boundary lines that divide the roof into faces:
   - Gabled: 1 segment (the ridge line)
   - Hipped: 5 segments (ridge + 4 lines from ridge endpoints to cap segment endpoints)

2. **FaceDecompositionUtil.splitPolygonIntoFaces()** — General-purpose algorithm:
   - Collects ALL segments (polygon edges + inner segments)
   - Finds all intersection points between segments
   - Snaps intersections to nearby endpoints (SNAP_DISTANCE = 1e-5)
   - Splits segments at intersection points -> fully-noded planar graph
   - Creates directed edges (both directions per undirected edge)
   - At each node, sorts outgoing edges clockwise by angle
   - Face enumeration: picks unused edge, follows "next clockwise" rule -> traces minimal faces
   - Classifies faces as CCW (outer) or CW (inner/hole), nests holes in outers
   - Filters: only returns faces whose interior is inside the original polygon

3. **Height assignment** — For each triangulated face vertex:
   - `roofHeight * (1 - distanceToRidge / maxDistanceToRidge)`
   - Same formula for both gabled and hipped

### Key Java Classes -> C# Mapping

| OSM2World Java | C# Location |
|---|---|
| `Roof.java` (abstract base) | Not ported (not needed yet) |
| `HeightfieldRoof.java` (triangulation + rendering) | `Roof/HeightfieldRoof.cs` |
| `RoofWithRidge.java` (ridge calculation) | `Roof/RoofWithRidge.cs` |
| `GabledRoof.java` | `Roof/GabledRoof.cs` |
| `HippedRoof.java` | `Roof/HippedRoof.cs` |
| `FaceDecompositionUtil.java` | `FaceDecompositionUtil.cs` |
| `GeometryUtil.insertIntoPolygon()` | `FaceDecompositionUtil.InsertIntoPolygon()` |
| `GeometryUtil.distanceFromLineSegment()` | `FaceDecompositionUtil.DistancePointToSegment()` |
| `GeometryUtil.interpolateValue()` | `FaceDecompositionUtil.InterpolateValue()` |
| `SimplePolygonXZ.getSimplifiedPolygon()` | `RoofWithRidge.SimplifyPolygon()` |

---

## Resolved Issues (from Session 1)

### Suspect 1: FaceDecompositionUtil returns no faces / wrong faces -> FIXED
- **Root cause**: `GetPointInside` used centroid of first 3 vertices instead of proper polygon centroid (shoelace formula). This caused face filtering to reject valid faces.
- **Fix**: Replaced with proper polygon centroid calculation.
- **Additional fix**: `indexOf` fallback now matches Java's silent `(-1+1)%size=0` behavior for edge cases.

### Suspect 2: Ridge calculation produces degenerate results -> FIXED
- **Fix**: Ported the ridge calculation 1:1 from `RoofWithRidge.java` constructor into the new `RoofWithRidge.cs` class, preserving all the Java logic exactly.

### Suspect 3: Hipped roof inner segments reference simplified polygon vertices -> FIXED
- **Fix**: Inner segment endpoints (ridge endpoints, cap segment endpoints) are now snapped to the nearest actual polygon vertex via `FindNearestVertex()`. This ensures PointPool assigns the same ID to both the inner segment endpoint and the polygon vertex, maintaining graph connectivity.

---

## Remaining Work / Next Steps

### Verify Visual Output
- Build and test with actual buildings to confirm roofs render correctly
- Compare gabled (2 faces) and hipped (4 faces) face counts
- Check height profiles look correct (peak at ridge, zero at eaves)

### Potential Future Roof Types
The modular class hierarchy makes it easy to add more roof types from OSM2World:
- `FlatRoof` — height=0 everywhere (trivial)
- `PyramidalRoof` — apex at centroid, needs `GetInnerPoints()` support
- `HalfHippedRoof` — offset=1/6
- `MansardRoof` — offset=1/3, more complex segments
- `SkillionRoof` — single slope direction

### Fallback Plan (if still not working)
If FaceDecompositionUtil still proves fragile after the bug fixes:
- Add diagnostic logging in `HeightfieldRoof.GenerateMesh()` to dump face count and vertex positions
- Test with a simple rectangle building first
- Consider hybrid approach: simple left/right split for gabled, FaceDecompositionUtil only for hipped

---

## OSM2World Java Source Reference

All source files are at:
```
D:\Source\beamng_mapping_pro\examples_for_ai\OSM2World\core\src\main\java\org\osm2world\
```

Key files:
- `world/modules/building/roof/Roof.java` — Abstract base, factory method, `snapDirection()`
- `world/modules/building/roof/HeightfieldRoof.java` — Triangulation + height interpolation
- `world/modules/building/roof/RoofWithRidge.java` — Ridge calculation, cap segments, offset
- `world/modules/building/roof/GabledRoof.java` — 52 lines, very simple
- `world/modules/building/roof/HippedRoof.java` — 48 lines, very simple
- `world/modules/building/roof/FlatRoof.java` — 44 lines
- `world/modules/building/roof/PyramidalRoof.java` — Apex at centroid, lines to all vertices
- `math/algorithms/FaceDecompositionUtil.java` — Face enumeration algorithm
- `math/algorithms/GeometryUtil.java` — insertIntoPolygon, distanceFromLineSegment, interpolateValue
- `math/shapes/SimplePolygonXZ.java` — Polygon operations, getSimplifiedPolygon, getClosestSegment
- `math/VectorXZ.java` — 2D vector, `angle()`, `rightNormal()`, `fromAngle()`

### Class Hierarchy
```
Roof (abstract)
+-- HeightfieldRoof (abstract) -- height function + FaceDecompositionUtil triangulation
|   +-- FlatRoof -- height=0 everywhere
|   +-- PyramidalRoof -- apex at centroid
|   +-- RoofWithRidge (abstract) -- ridge calculation
|   |   +-- GabledRoof -- offset=0, insert ridge into polygon        [PORTED]
|   |   +-- HippedRoof -- offset=1/3, ridge+4 hip lines              [PORTED]
|   |   +-- HalfHippedRoof -- offset=1/6
|   |   +-- GambrelRoof -- dual slope
|   |   +-- MansardRoof -- offset=1/3, complex segments
|   |   +-- RoundRoof -- circular arc profile
|   |   +-- SkillionRoof -- single slope direction
|   +-- ComplexRoof -- user-mapped edges/ridges from OSM
+-- SpindleRoof (abstract) -- axially symmetric extrusion
|   +-- DomeRoof -- hemisphere
|   +-- OnionRoof -- bulb shape
|   +-- ConeRoof -- extends PyramidalRoof with smooth shading
+-- ChimneyRoof -- special (hole + walls)
```

### How HeightfieldRoof.renderTo() works (the key method)

```java
// Step 1: Get faces from FaceDecompositionUtil
if (getInnerPoints().isEmpty()) {
    // Gabled, Hipped, etc. — no apex points
    Collection<PolygonWithHolesXZ> faces = FaceDecompositionUtil.splitPolygonIntoFaces(
            getPolygon().getOuter(), holes, getInnerSegments());
    trianglesXZ = new ArrayList<>();
    faces.forEach(f -> trianglesXZ.addAll(f.getTriangulation()));
} else {
    // Pyramidal — has apex point, uses JTS constrained triangulation
    trianglesXZ = JTSTriangulationUtil.triangulate(
            getPolygon().getOuter(), holes, getInnerSegments(), getInnerPoints());
}

// Step 2: Assign heights to each vertex
for (TriangleXZ triangle : trianglesXZ) {
    TriangleXZ tCCW = triangle.makeCounterclockwise();
    trianglesXYZ.add(tCCW.xyz(v -> v.xyz(baseEle + getRoofHeightAt(v))));
}

// Step 3: Draw
target.drawTriangles(material, trianglesXYZ, texCoords);
```

### How getRoofHeightAt() works (HeightfieldRoof)

```java
// First try the direct formula (overridden by each roof type)
Double ele = getRoofHeightAt_noInterpolation(pos);
if (ele != null) return ele;

// Fallback: interpolate from closest segment
Collection<LineSegmentXZ> segments = getInnerSegments() + polygon.getSegments();
LineSegmentXZ closestSegment = findClosest(segments, pos);
return interpolateValue(pos, closestSegment.p1, height(p1), closestSegment.p2, height(p2));
```

For Gabled/Hipped, `getRoofHeightAt_noInterpolation()` ALWAYS returns a value (never null), so the interpolation fallback is never used. The formula is:
```java
double distRidge = distanceFromLineSegment(pos, ridge);
double relativePlacement = distRidge / maxDistanceToRidge;
return roofHeight - roofHeight * relativePlacement;
// = roofHeight * (1 - distRidge / maxDistToRidge)
```

---

## Coordinate System Notes

| OSM2World | Our C# Code | Notes |
|---|---|---|
| `VectorXZ(x, z)` | `Vector2(X, Y)` | Top-down 2D plane |
| `VectorXYZ(x, y, z)` | `Vector3(X, Y, Z)` | y=height in Java, Z=height in C# |
| `SimplePolygonXZ` — closed loop (first==last) | `List<Vector2>` — open (no closing vertex) | `RemoveClosingVertex()` handles conversion |
| `double` | `float` | Less precision, may cause snapping issues |
| `VectorXZ.angle()` — clockwise from (0,1) | `MathF.Atan2(dir.X, dir.Y)` | Adjusted for range [0, 2pi) |
| `VectorXZ.rightNormal()` — (z/len, -x/len) | `new Vector2(dir.Y, -dir.X)` | Perpendicular clockwise |

---

## Files NOT Changed

- `BuildingData.cs` — data model unchanged
- `BuildingDefaults.cs` — default roof shape mapping unchanged
- `OsmBuildingParser.cs` — OSM tag parsing unchanged
- `PolygonTriangulator.cs` — earcut triangulation unchanged
