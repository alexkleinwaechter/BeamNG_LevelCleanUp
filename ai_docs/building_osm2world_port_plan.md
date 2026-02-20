# Plan: Port OSM2World Building Architecture 1:1 to C#

## Date: 2026-02-18

## Context

Complex building footprints (L-shapes, T-shapes) produce roof artifacts: gaps between walls and roof, wrong faces, floating roof edges. Two root causes found by comparing Java and C# code:

**Root Cause 1 — Flat walls vs. per-vertex roof-following walls:**
- Java `ExteriorBuildingWall` line 189: `topZ = baseEle + heightWithoutRoof + roof.getRoofHeightAt(p)` — each wall vertex extends UP to meet the roof
- C# `BuildingMeshGenerator.GenerateWalls()` line 85: `float top = building.RoofBaseHeight` — all wall vertices at constant height
- Result: gaps between flat wall tops and sloped roof surfaces

**Root Cause 2 — No building:part support:**
- Java `Building` contains multiple `BuildingPart` objects, each with own simple polygon + independent roof
- C# has single `BuildingData` per building, one polygon, one roof
- Result: complex footprints get a single ridge that can't properly cover the shape

---

## Phase 1: Fix Walls to Follow Roof (3 new files, 2 modified)

### Step 1.1 — New: `BeamNG.Procedural3D/Building/Roof/FlatRoof.cs`
Port of `FlatRoof.java`. Needed so ALL roofs (including flat) go through `HeightfieldRoof` interface.
- `GetPolygon()` → returns originalPolygon unchanged
- `GetInnerSegments()` → empty list
- `GetRoofHeightAtNoInterpolation()` → always returns 0f

### Step 1.2 — New: `BeamNG.Procedural3D/Building/WallSurface.cs`
Port of `WallSurface.java`. Triangulates a wall between lower boundary (flat) and upper boundary (variable height following roof).
- Constructor takes `List<Vector3> lowerBoundary, List<Vector3> upperBoundary`
- Upper boundary may have MORE vertices than lower (e.g., ridge endpoint inserted by GabledRoof)
- Triangulation: form closed polygon from lower + reversed upper, earcut triangulate
- UV: U = distance along wall / scaleU, V = height / scaleV

### Step 1.3 — New: `BeamNG.Procedural3D/Building/ExteriorBuildingWall.cs`
Port of `ExteriorBuildingWall.java`. THE critical fix.

**Key algorithm** (port of Java lines 134-191):
1. Bottom boundary: wall footprint points at constant Z = baseEle (flat)
2. Top boundary: extract vertices from ROOF POLYGON between wall start/end:
   - Get `roofPolygon = roof.GetPolygon()` (may have extra vertices like ridge endpoints)
   - Find startIdx/endIdx of wall endpoints in roofPolygon
   - Walk roofPolygon from startIdx to endIdx, collecting ALL vertices (including inserted ridge points)
   - For each top vertex: `Z = baseEle + heightWithoutRoof + roof.GetRoofHeightAt(vertex)`
3. Create WallSurface(bottomBoundary, topBoundary)

**Static factory**: `SplitIntoWalls(roof, footprint, baseEle, heightWithoutRoof, material, textureScale)`
- Walks footprint polygon, identifies corners
- For each wall segment: extracts top boundary from roof polygon, creates ExteriorBuildingWall

### Step 1.4 — Modify: `BeamNG.Procedural3D/Building/BuildingMeshGenerator.cs`
- Add `CreateRoof(building)` factory → shared HeightfieldRoof instance (FlatRoof/GabledRoof/HippedRoof)
- Replace `GenerateWalls()` to use `ExteriorBuildingWall.SplitIntoWalls(roof, ...)`
- Both walls and roof use the SAME roof instance (critical for coordinate consistency)
- Remove `GenerateFlatRoof()` — replaced by `FlatRoof.GenerateMesh()`

New flow:
```
1. var roof = CreateRoof(building)       // shared instance
2. walls = ExteriorBuildingWall.SplitIntoWalls(roof, ...)
3. foreach wall: wall.Render(meshBuilder)  // per-vertex roof height
4. roofMesh = roof.GenerateMesh(textureScale)
5. floorMesh = GenerateFloor(building)
```

### Step 1.5 — Modify: `BeamNG.Procedural3D/Building/Roof/GabledRoof.cs`
- Remove `GenerateAdditionalGeometry()` override
- After Phase 1, gable walls are created by ExteriorBuildingWall because the wall top boundary includes the ridge endpoint (from `GetPolygon()` → `InsertIntoPolygon`)
- Keeping it would produce duplicate gable triangles

---

## Phase 2: Building/BuildingPart Architecture (4 new files, 3 modified)

### Step 2.1 — New: `BeamNG.Procedural3D/Building/BuildingPartData.cs`
Lightweight DTO for a single building part's properties (parsed from OSM).
- Same fields as BuildingData but without world-position: OsmId, Polygon, Holes, Height, MinHeight, Levels, RoofShape, RoofHeight, RoofDirection, RoofAngle, WallMaterial, RoofMaterial

### Step 2.2 — New: `BeamNG.Procedural3D/Building/BuildingPart.cs`
Port of `BuildingPart.java`. One section of a building with its own polygon + roof + walls.
- Constructor: creates roof via factory, creates walls via `ExteriorBuildingWall.SplitIntoWalls()`
- `GenerateMeshes()` → renders walls, roof, floor; groups by material key
- Contains `CreateRoofForShape()` factory method (port of `Roof.createRoofForShape()`)

### Step 2.3 — New: `BeamNG.Procedural3D/Building/Building.cs`
Port of `Building.java`. Container for multiple BuildingPart objects.
- Part discovery: find `building:part=*` features contained within building outline
- Coverage check: if parts cover >= 90% of building area, use parts; otherwise fallback to single part
- `GenerateMeshes()` → iterates parts, merges meshes by material

### Step 2.4 — New: `BeamNG.Procedural3D/Building/LevelAndHeightData.cs`
Port of `LevelAndHeightData.java`. Height/level calculation from OSM tags.
- Computes: Height, HeightWithoutRoof, Levels, MinHeight
- Priority: explicit height > levels * heightPerLevel > defaults
- Replaces scattered height logic in OsmBuildingParser + BuildingMeshGenerator.EnsureRoofHeight()

### Step 2.5 — Modify: `BeamNgTerrainPoc/Terrain/Building/OsmBuildingParser.cs`
- New method `ParseBuildingsWithParts()` → returns `List<Building>`
- Separates features into mainBuildings (`building=*`) and parts (`building:part=*`)
- For each mainBuilding: finds contained parts using centroid point-in-polygon test
- All parts share the main building's centroid as coordinate origin

### Step 2.6 — Modify: `BeamNgTerrainPoc/Terrain/Building/BuildingDaeExporter.cs`
- Add `ExportBuilding(Building)` overload that iterates parts

### Step 2.7 — Modify: `BeamNgTerrainPoc/Terrain/Building/BuildingGenerationOrchestrator.cs`
- Update pipeline: `ParseBuildingsWithParts()` → `List<Building>` → `ExportAll()`

---

## Java → C# File Mapping

| Java File | C# File | Status |
|-----------|---------|--------|
| `HeightfieldRoof.java` | `Roof/HeightfieldRoof.cs` | Already ported |
| `RoofWithRidge.java` | `Roof/RoofWithRidge.cs` | Already ported |
| `GabledRoof.java` | `Roof/GabledRoof.cs` | Already ported (remove gable wall workaround) |
| `HippedRoof.java` | `Roof/HippedRoof.cs` | Already ported |
| `FlatRoof.java` | `Roof/FlatRoof.cs` | **Phase 1 — NEW** |
| `FaceDecompositionUtil.java` | `FaceDecompositionUtil.cs` | Already ported |
| `ExteriorBuildingWall.java` | `ExteriorBuildingWall.cs` | **Phase 1 — NEW** |
| `WallSurface.java` | `WallSurface.cs` | **Phase 1 — NEW** |
| `BuildingPart.java` | `BuildingPart.cs` | **Phase 2 — NEW** |
| `Building.java` | `Building.cs` | **Phase 2 — NEW** |
| `LevelAndHeightData.java` | `LevelAndHeightData.cs` | **Phase 2 — NEW** |
| `Roof.java` (factory) | Integrated into `BuildingPart.CreateRoofForShape()` | **Phase 2** |

## OSM2World Java Source Reference

All Java source at:
```
D:\Source\beamng_mapping_pro\examples_for_ai\OSM2World\core\src\main\java\org\osm2world\
```

Key files for this port:
- `world/modules/building/Building.java` — building container, part discovery
- `world/modules/building/BuildingPart.java` — part with own polygon/roof/walls
- `world/modules/building/ExteriorBuildingWall.java` — per-vertex roof height walls (lines 134-191 critical)
- `world/modules/building/WallSurface.java` — wall surface triangulation
- `world/modules/building/LevelAndHeightData.java` — height/level calculation
- `world/modules/building/roof/Roof.java` — abstract base + factory
- `world/modules/building/roof/FlatRoof.java` — trivial flat roof

## What We Skip (not relevant for BeamNG)
- Window/door rendering (WindowImplementation, Door, GeometryWindow)
- LOD system
- Indoor rendering (IndoorRoom, BuildingPartInterior)
- Building passages (tunnel=building_passage)
- RoofBuildingPart (building:part=roof)
- MapArea/MapNode/MapWay integration (we use our own data model)
- ComplexRoof (requires explicitly mapped roof:ridge ways in OSM)

---

## Phase 3: Fix Height Calculation + DomeRoof (1 new file, 3 modified) — DONE 2026-02-18

### Problem

Building part heights were wrong compared to OSM2World. Side-by-side comparison showed parts too short/tall.
Five bugs found by line-by-line comparison of `LevelAndHeightData.java` (435 lines) vs `LevelAndHeightData.cs` (118 lines):

### Bug 1 — Total height default didn't include roofHeight (CRITICAL)

**Java line 186**: `height = parseHeight(tags, buildingLevels * defaults.heightPerLevel + roofHeight)`
**C# line 44**: `height = taggedHeight ?? (levels * heightPerLevel)` — roofHeight NOT included

Impact: 3-level gabled building without explicit height tag:
- Java: total=12.5m (7.5+5), wall=7.5m, roof=5m
- Old C#: total=7.5m, wall=2.5m (clamped), roof=2.5m (clamped) → **walls 3x too short**

**Fix**: Compute roofHeight FIRST, then `height = taggedHeight ?? (levels * hpl + roofHeight)`

### Bug 2 — `building:min_level` not handled

**Java lines 195-196**: `minHeight = (heightWithoutRoof / buildingLevels) * buildingMinLevel`
**Old C#**: Only checks explicit `min_height` tag, ignores `building:min_level` entirely.

Impact: Parts using `building:min_level` instead of `min_height` all start at ground level.

**Fix**: Parse `building:min_level` tag in OsmBuildingParser, pass to `Compute()`.

### Bug 3 — `hasWalls=false` min_height adjustment missing

**Java lines 197-198**: `minHeight = heightWithoutRoof - 0.3` for carports/roof-only.
**Old C#**: Not implemented.

**Fix**: Added `hasWalls` parameter, delegates to `defaults.HasWalls`.

### Bug 4 — Level count not derived from height tag

**Java lines 136-138**: When `building:levels` absent but `height=X` present: `levels = max(1, height / hpl)`
**Old C#**: Falls back to defaults.Levels immediately.

**Fix**: Added derivation: `levels = max(1, (int)(taggedHeight / hpl))`.

### Bug 5 — DomeRoof height = diameter/2

**Java lines 165-166**: `if (roof instanceof DomeRoof) roofHeight = outline.getDiameter() / 2`
**Old C#**: No dome support at all.

**Fix**: Added `polygonDiameter` parameter, computed via new `ComputePolygonDiameter()` helper.

### Step 3.1 — Modify: `BeamNG.Procedural3D/Building/LevelAndHeightData.cs`

Rewrote `Compute()` to match Java `LevelAndHeightData.java` constructor (lines 107-206):
- **Reordered**: roofHeight computed BEFORE totalHeight (was after)
- **Default total**: `height = levels * hpl + roofHeight` (was `levels * hpl`)
- **Rounding**: Added Java's `Math.round(heightWithoutRoof * 1e4) / 1e4`
- 3 new optional params (backward-compatible): `taggedMinLevel`, `hasWalls`, `polygonDiameter`
- `ComputeRoofHeight()`: removed `totalHeight` param (no longer needed since computed first), added dome case

### Step 3.2 — Modify: `BeamNgTerrainPoc/Terrain/Building/OsmBuildingParser.cs`

In `ParseTagsIntoBuildingData()`:
- Parse `building:min_level` tag
- Compute polygon diameter for dome shapes via new `ComputePolygonDiameter()` (max vertex-to-vertex distance)
- Pass `taggedMinLevel`, `defaults.HasWalls`, `polygonDiameter` to `LevelAndHeightData.Compute()`

### Step 3.3 — New: `BeamNG.Procedural3D/Building/Roof/DomeRoof.cs`

Port of `DomeRoof.java` + `SpindleRoof.java`. Hemisphere shape via spindle extrusion.

Architecture:
- Extends `HeightfieldRoof` (for wall compatibility via `ExteriorBuildingWall.SplitIntoWalls()`)
- `GetRoofHeightAt()` returns 0 everywhere (walls flat-topped, dome sits on top — matches Java `SpindleRoof.getRoofHeightAt()`)
- Overrides `GenerateMesh()` with spindle-based rendering (not face decomposition)

Rendering algorithm (port of `SpindleRoof.renderSpindle()` + `DomeRoof.getSpindleSteps()`):
1. 10 height rings: `relativeHeight = ring / 9`, `scaleFactor = sqrt(1 - h²)`
2. Each ring: polygon vertices scaled from center by scaleFactor, at `baseEle + h * roofHeight`
3. Quad strips between adjacent rings (rings 0-8: polyCount vertices each)
4. Top ring (scale=0): single center vertex → triangles from ring 8
5. `MeshBuilder.CalculateSmoothNormals()` for dome appearance (port of `ExtrudeOption.SMOOTH_SIDES`)

### Step 3.4 — Modify: `BeamNG.Procedural3D/Building/Roof/HeightfieldRoof.cs`

- `GenerateMesh()` changed to `virtual` so DomeRoof can override

### Step 3.5 — Modify: `BeamNG.Procedural3D/Building/BuildingMeshGenerator.cs`

- `CreateRoof()`: added `"dome" => new DomeRoof(building, polygon)`
- `GenerateRoofMesh()`: added `"dome"` to shapes routed through `roof.GenerateMesh()`

### Height calculation fix — before/after comparison

| Scenario | Old C# | Fixed C# | Java |
|----------|--------|----------|------|
| 3-level gabled, no tags | total=7.5m, wall=2.5m | total=12.5m, wall=7.5m | total=12.5m, wall=7.5m |
| 1-level hipped, no tags | total=2.5m, wall=1.5m | total=3.5m, wall=2.5m | total=3.5m, wall=2.5m |
| Dome, diameter=20m | roofHeight=0 (flat) | roofHeight=10m | roofHeight=10m |
| building:min_level=2, 5 levels | minHeight=0 | minHeight=3m | minHeight=3m |

---

## Java → C# File Mapping (Updated)

| Java File | C# File | Status |
|-----------|---------|--------|
| `HeightfieldRoof.java` | `Roof/HeightfieldRoof.cs` | Ported (Phase 1), `GenerateMesh` made virtual (Phase 3) |
| `RoofWithRidge.java` | `Roof/RoofWithRidge.cs` | Ported |
| `GabledRoof.java` | `Roof/GabledRoof.cs` | Ported |
| `HippedRoof.java` | `Roof/HippedRoof.cs` | Ported |
| `FlatRoof.java` | `Roof/FlatRoof.cs` | Ported (Phase 1) |
| `DomeRoof.java` + `SpindleRoof.java` | `Roof/DomeRoof.cs` | **Phase 3 — NEW** |
| `FaceDecompositionUtil.java` | `FaceDecompositionUtil.cs` | Ported |
| `ExteriorBuildingWall.java` | `ExteriorBuildingWall.cs` | Ported (Phase 1) |
| `WallSurface.java` | `WallSurface.cs` | Ported (Phase 1) |
| `Building.java` | `Building.cs` | Ported (Phase 2) |
| `LevelAndHeightData.java` | `LevelAndHeightData.cs` | Ported (Phase 2), **fixed (Phase 3)** |
| `BuildingDefaults.java` | `BuildingDefaults.cs` | Ported (Phase 2) |
| `Roof.java` (factory) | `BuildingMeshGenerator.CreateRoof()` | Ported (Phase 2), dome added (Phase 3) |

## Verification
1. **Phase 1 test**: Build, load map with gabled/hipped buildings → walls should follow roof slope, no gaps at any vertex, gable triangles present without `GenerateAdditionalGeometry()`
2. **Phase 2 test**: Load map area with complex buildings that have `building:part` tags → each part renders independently with correct roof shape
3. **Phase 3 test**: Compare building heights with OSM2World output for same area; dome parts should render as hemispheres; `building:min_level` parts should float at correct elevation
4. **Regression**: Simple rectangular buildings should look identical before/after
