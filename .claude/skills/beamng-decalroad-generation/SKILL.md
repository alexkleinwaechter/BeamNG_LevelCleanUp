---
name: beamng-decalroad-generation
description: Guide for generating DecalRoad objects in the .NET terrain generation pipeline. Covers coordinate transformation, spline sampling, lateral offsets, NDJSON writing, and integration with existing code. Use when implementing or modifying DecalRoad export.
user-invocable: false
---

# DecalRoad Generation Guide (.NET Pipeline)

## Architecture Overview

The terrain generation pipeline already exports **master splines** (JSON for BeamNG's road editor). DecalRoad generation follows the same pattern but produces **scene objects** (NDJSON in `items.level.json`) instead of master spline JSON.

```
UnifiedRoadNetwork (splines + cross-sections)
    ↓
MasterSplineExporter → splines/master_splines.json (existing)
    ↓
DecalRoadExporter → items.level.json NDJSON lines (to implement)
```

## Coordinate Transformation

**Input**: Terrain meter coordinates (origin at bottom-left, Y up)
**Output**: BeamNG world coordinates (origin at center)

Use `BeamNgCoordinateTransformer.TerrainToWorld()` from `BeamNgTerrainPoc/Terrain/Utils/BeamNgCoordinateTransformer.cs`:

```csharp
var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
    terrainX, terrainY, elevation + terrainBaseHeight,
    terrainSizePixels, metersPerPixel);
// worldPos.X, worldPos.Y = horizontal (centered origin)
// worldPos.Z = elevation
```

The transformation: `worldX = terrainX - (terrainSizePixels / 2.0f * metersPerPixel)`

## Existing Code to Reuse

### Spline Sampling (from MasterSplineExporter.cs)

`MasterSplineExporter` at `BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs` already implements:
- `SampleNodesFromUnifiedCrossSections()` - samples along cross-sections with smoothed elevations
- `SampleNodesFromSpline()` - samples along RoadSpline at fixed intervals
- `SampleNodesFromCrossSections()` - samples from CrossSection objects
- Elevation lookup from heightmap or cross-section TargetElevation
- Coordinate transformation to world space

### Data Models

- `UnifiedRoadNetwork` - contains all `ParameterizedRoadSpline` objects with splines and cross-sections
- `ParameterizedRoadSpline` - spline + parameters (width, material name)
- `RoadSpline` - Catmull-Rom spline with `GetPointAtDistance()`, `SampleByDistance()`, `TotalLength`
- `UnifiedCrossSection` - center point, target elevation, width, direction
- `SplineRoadParameters` - road parameters including `EffectiveMasterSplineWidthMeters`

### Scene Writing Pattern (from BuildingSceneWriter.cs)

`BeamNgTerrainPoc/Terrain/Building/BuildingSceneWriter.cs` shows how to write scene items as NDJSON.

## DecalRoad Generation Algorithm

### Step 1: Sample Centerline Nodes

Same as MasterSplineExporter but with appropriate node spacing:

```csharp
// Sample spline at nodeDistanceMeters intervals
var nodeCount = Math.Max(2, (int)Math.Ceiling(spline.TotalLength / nodeDistanceMeters) + 1);
// For each sample point: get terrain position → elevation → TerrainToWorld()
```

### Step 2: Compute Binormals

For lateral offset, compute the perpendicular (binormal) at each node:

```csharp
// For each node i:
var direction = Vector2.Normalize(nodes[i+1].Position - nodes[i].Position);
var binormal = new Vector2(-direction.Y, direction.X); // 90-degree rotation
```

At endpoints, use adjacent segment direction. For smooth roads, average adjacent directions.

### Step 3: Generate Layer DecalRoads

For each layer type (edge lines, center line, edge blends, etc.):

```csharp
// Apply lateral offset
var offsetPos = centerPos + binormal * (layer.Position * 0.5f * roadWidth);

// Create DecalRoad node: [worldX, worldY, worldZ, layerWidth]
var node = new float[] { worldPos.X, worldPos.Y, worldPos.Z, layerWidth };
```

### Step 4: Chunk if Needed

If >100 nodes, split into chunks sharing boundary points (see beamng-decalroad-format skill).

### Step 5: Write NDJSON

Each DecalRoad is one line in the output file:

```csharp
// Write SimGroup container first
writer.WriteLine(JsonSerializer.Serialize(simGroupDict));

// Then each DecalRoad
foreach (var decalRoad in decalRoads)
    writer.WriteLine(JsonSerializer.Serialize(decalRoad));
```

## NDJSON Output Format

```json
{"class":"SimGroup","name":"GeneratedRoadMarkings","persistentId":"<guid>","__parent":"MissionGroup"}
{"class":"DecalRoad","persistentId":"<guid>","__parent":"GeneratedRoadMarkings","position":[X,Y,Z],"material":"m_line_white","improvedSpline":true,"drivability":-1,"renderPriority":10,"distanceFade":[10000,10000],"startEndFade":[1.0,1.0],"nodes":[[X,Y,Z,W],...]}
```

## Integration Point

The `TerrainGenerationOrchestrator` at `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` coordinates the pipeline. DecalRoad export should run as a post-generation task, after:
1. Terrain heightmap is finalized
2. Road splines are created (UnifiedRoadNetwork is built)
3. Master splines are exported

The output `items.level.json` should be written alongside the existing scene hierarchy in the generated level's MissionGroup structure.

## Layer Types for Generation

For terrain generation, the most impactful layers to generate are:

**High priority (visual road definition):**
1. Edge Lines (solid white, 0.25m at road edges)
2. Center Line (dashed white, 0.2m at center)
3. Edge Blend 1 (asphalt edge, 1.0m just beyond road edge)

**Medium priority (realism):**
4. Edge Blend 2 (dirt transition, 2.0m further out)
5. Edge Blend 3 (grass transition, 3.0m further out)
6. Lane Lines (for multi-lane roads)

**Lower priority (detail/wear):**
7. Light tread marks
8. Road damage/cracks

See the `beamng-road-layers` skill for exact material names, widths, and positions for each layer type.

## Key Differences from MasterSplineExporter

| Aspect | MasterSplineExporter | DecalRoadExporter |
|--------|---------------------|-------------------|
| Output format | Single JSON file | NDJSON lines in items.level.json |
| Coordinate system | Same (TerrainToWorld) | Same (TerrainToWorld) |
| Node format | `{x, y, z}` objects | `[x, y, z, width]` arrays |
| Per-spline output | 1 master spline | Multiple DecalRoads (one per layer) |
| Lateral offset | None (centerline only) | Yes (layers offset from center) |
| Width handling | Single width list | Per-layer width (fixed or tracking) |
| Max nodes | Unlimited | 100 per DecalRoad (chunking required) |
