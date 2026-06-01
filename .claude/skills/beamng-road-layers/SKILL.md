---
name: beamng-road-layers
description: Reference for BeamNG road spline layer system - how visual layers (markings, edge blends, wear patterns) map to DecalRoad objects with lateral positioning, materials, and widths. Use when generating or configuring road visual layers.
user-invocable: false
---

# BeamNG Road Spline Layer System

## Overview

A road spline in BeamNG consists of a centerline with widths, plus multiple **layers** that each generate one or more DecalRoad objects. Each layer represents a visual element (marking, blend, wear pattern) positioned relative to the road centerline.

## Layer Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `material` | string | `"road_invisible"` | Material name for the DecalRoad |
| `width` | float | `10.0` | Fixed width in meters (used when `isTrackWidth = false`) |
| `position` | float | `0.0` | Lateral position: -1.0 = left edge, 0.0 = center, +1.0 = right edge |
| `isTrackWidth` | bool | `true` | If true, width follows road width; if false, uses fixed `width` |
| `isFlip` | bool | `false` | Reverse the spline direction (used for bilateral asymmetric layers) |
| `texLen` | float | `10.0` | Texture repetition length along spline in meters |
| `fadeIn` | float | `1.0` | Fade-in distance at road start (meters) |
| `fadeOut` | float | `1.0` | Fade-out distance at road end (meters) |
| `renderPriority` | int | `10` | Draw order (higher renders on top) |
| `isOverObjects` | bool | `false` | Render above other scene objects |

## Lateral Offset Calculation

The `position` value determines where a layer sits relative to the road centerline:

```
offsetPoint = centerPoint + binormal * position * 0.5 * roadWidth
```

Where:
- `centerPoint` = interpolated position on the road centerline
- `binormal` = perpendicular vector to the road direction (pointing right)
- `position` = lateral position value (-1.0 to +1.0)
- `roadWidth` = road width at that point

**Examples:**
- `position = 0.0` → centered on road
- `position = -1.0` → left edge of road
- `position = +1.0` → right edge of road
- `position = ±1.1` → slightly beyond road edge (for edge blends)

## Layer-to-DecalRoad Mapping

Each layer creates one DecalRoad (or multiple if >100 nodes). The DecalRoad receives:
- Nodes with laterally-offset positions and appropriate widths
- Material, textureLength, renderPriority from the layer
- `improvedSpline = true`, `drivability = -1.0` (always)
- `startEndFade` from fadeIn/fadeOut values

## Auto-Generated Layer Types

BeamNG's road spline editor supports auto-generated layers organized into categories. See [layer-reference.md](layer-reference.md) for complete details on all layer types with exact materials, widths, and positions.

### Summary of Layer Categories

| Category | Material | Width | Position | Description |
|----------|----------|-------|----------|-------------|
| **Edge Lines** | `m_line_white` | 0.25m | ±1.0 | Solid white edge markings |
| **Center Line** | `m_line_white_discontinue` | 0.2m | 0.0 | Dashed center marking |
| **Lane Lines** | `m_line_white_discontinue` | 0.2m | per-lane | Dashed lane dividers |
| **Edge Blend 1** | `m_road_asphalt_edge` | 1.0m | ±1.1 | Close asphalt-edge transition |
| **Edge Blend 2** | `m_road_edge_dirt` | 2.0m | ±1.25 | Mid-range dirt transition |
| **Edge Blend 3** | `m_road_asphalt_edge_grass` | 3.0m | ±1.35 | Far grass transition |
| **Light Tread** | `m_tread_marks_clean` | track | per-lane | Light tire marks |
| **Heavy Tread** | `road_rubber_double` | track | per-lane | Heavy tire marks |
| **Road Cracks** | `m_asphalt_cracks_02` | track | per-lane | Crack overlay |
| **Repair 1** | `repair1` | track | per-lane | Repair patches |
| **Repair 2** | `repair2` | track | per-lane | Repair patches |
| **Patches** | `road_patches1` | track | per-lane | Road patches |
| **Damage 1** | `m_asphalt_damaged_01` | track | per-lane | Damage texture |
| **Damage 2** | `m_asphalt_damaged_02` | track | per-lane | Damage texture |

"track" width means `isTrackWidth = true` (follows road width).

## Render Priority Stacking

Layers render bottom-to-top by `renderPriority`:
1. Edge blends (priority 9) - below everything
2. Road surface/wear layers (priority 10) - standard
3. Road markings (priority 10+) - on top

## Bilateral Layers

Many layer types come in left/right pairs:
- **Edge Lines**: Left (position -1.0) + Right (position +1.0)
- **Edge Blends**: Left + Right (right side uses `isFlip = true`)
- **Lane layers**: Created per-lane with calculated positions

The `isFlip` flag on right-side edge blends ensures the texture direction matches the road direction from that side's perspective.
