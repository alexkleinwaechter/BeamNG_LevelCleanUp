---
name: beamng-decalroad-format
description: Reference for BeamNG DecalRoad JSON format, properties, node structure, and NDJSON serialization. Use when generating, parsing, or working with DecalRoad scene objects.
user-invocable: false
---

# BeamNG DecalRoad Format Reference

## What is a DecalRoad?

A DecalRoad is a BeamNG scene object that renders a textured strip (decal) along a spline path on the terrain surface. Unlike mesh-based roads, DecalRoads are projected onto terrain and other objects. They are used for:
- Road surface textures
- Road markings (center lines, edge lines, lane dividers)
- Edge blends (asphalt-to-dirt transitions)
- Tread marks, damage overlays, patches
- AI navigation paths (invisible roads with `material: "road_invisible"`)

## NDJSON Serialization

DecalRoads are stored in `items.level.json` files using **NDJSON format** (Newline Delimited JSON). Each line is a standalone JSON object - no array brackets, no commas between lines.

```
{"class":"SimGroup","name":"DecalRoads","persistentId":"<guid>","__parent":"MissionGroup"}
{"class":"DecalRoad","persistentId":"<guid>","__parent":"DecalRoads","position":[0,0,0],"material":"m_line_white","nodes":[[0,0,0,0.25],[10,0,0,0.25]],...}
{"class":"DecalRoad","persistentId":"<guid>","__parent":"DecalRoads","position":[0,0,0],"material":"m_road_asphalt_edge","nodes":[[0,0,0,1.0],[10,0,0,1.0]],...}
```

## DecalRoad JSON Properties

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `class` | string | Yes | - | Always `"DecalRoad"` |
| `persistentId` | string | Yes | - | Unique identifier (GUID or custom ID) |
| `__parent` | string | Yes | - | Name of parent SimGroup |
| `position` | `[X, Y, Z]` | Yes | - | World position of first node |
| `nodes` | `[[X,Y,Z,W],...]` | Yes | - | Array of `[X, Y, Z, width]` tuples |
| `material` | string | Yes | `"road_invisible"` | Material name for rendering |
| `improvedSpline` | bool | No | `true` | Use improved spline interpolation |
| `renderPriority` | int | No | `10` | Draw order (higher = on top) |
| `drivability` | float | No | `-1.0` | `-1.0` = non-drivable, `1.0` = drivable |
| `distanceFade` | `[start, end]` | No | `[10000, 10000]` | LOD fade distances in meters |
| `startEndFade` | `[fadeIn, fadeOut]` | No | `[1.0, 1.0]` | Fade at road start/end in meters |
| `overObjects` | bool | No | `false` | Render above other objects |
| `detail` | float | No | `1.0` | Binormal subdivision detail level |
| `textureLength` | float | No | `10.0` | Texture repeat length in meters |

## Node Format

Each node is a 4-element array: `[X, Y, Z, width]`

- **X, Y**: Horizontal world coordinates (BeamNG world space, origin at terrain center)
- **Z**: Elevation/height
- **width**: Road width at this node in meters

**Coordinate system**: BeamNG world coordinates have origin (0,0) at terrain CENTER. X increases right, Y increases forward, Z is up.

**Important**: The `position` property MUST match the first node's `[X, Y, Z]` values.

## Maximum Nodes Per DecalRoad

BeamNG limits DecalRoad objects to **100 nodes maximum**. For longer roads, split into multiple DecalRoad objects (chunks) that share boundary points for seamless joins.

### Chunking Algorithm

```
maxNodes = 100
numChunks = ceil(totalNodes / maxNodes)

for chunk i (1-based):
  startIdx = (i-1) * maxNodes
  endIdx = min(i * maxNodes, totalNodes) - 1

  // Share boundary point with previous chunk
  if i > 1: startIdx = startIdx - 1

  // Fade only on first/last chunks
  fadeIn  = (i == 1)         ? desiredFadeIn  : 0.0
  fadeOut = (i == numChunks)  ? desiredFadeOut : 0.0
```

## SimGroup Hierarchy

DecalRoads must be parented to a SimGroup in the scene tree:

```
MissionGroup/
  DecalRoads/                    # SimGroup container
    items.level.json             # Contains SimGroup + DecalRoad NDJSON lines
```

A SimGroup line must precede its child DecalRoads:
```json
{"class":"SimGroup","name":"GeneratedRoadMarkings","persistentId":"<guid>","__parent":"MissionGroup"}
```

## Real Example (from BeamNG level)

```json
{"class":"DecalRoad","persistentId":"TRONROUT0000000356415327_Chemin","__parent":"AI_decalroads","position":[-864.079346,-3417.87036,80.5446167],"distanceFade":[10000,10000],"drivability":1,"improvedSpline":true,"material":"road_invisible","nodes":[[-864.079346,-3417.87036,80.5446167,3.5],[-866.771423,-3412.2915,80.652832,3.5],[-879.131165,-3392.85083,80.9914551,3.5],[-891.528687,-3373.66504,81.5001221,3.5],[-905.079346,-3352.87036,81.6122437,3.5]],"renderPriority":12}
```

## Common Materials

| Material Name | Usage |
|---------------|-------|
| `road_invisible` | AI navigation (invisible) |
| `m_line_white` | Solid white road marking |
| `m_line_white_discontinue` | Dashed white road marking |
| `m_road_asphalt_edge` | Asphalt edge blend |
| `m_road_edge_dirt` | Dirt edge blend |
| `m_road_asphalt_edge_grass` | Grass edge blend |
| `m_tread_marks_clean` | Light tire marks |
| `road_rubber_double` | Heavy tire marks |
| `m_asphalt_cracks_02` | Road cracks |
| `repair1`, `repair2` | Road repairs |
| `road_patches1` | Road patches |
| `m_asphalt_damaged_01`, `m_asphalt_damaged_02` | Road damage |

**Note**: Materials must exist in the level's material files. For generated levels, materials should be defined in `art/road/` or referenced from the game's built-in materials.
