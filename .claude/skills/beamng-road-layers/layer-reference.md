# Detailed Layer Type Reference

This document contains the exact configurations for all auto-generated layer types in BeamNG's road spline system. Values are sourced from `layerMgr.lua`.

## Default Slider Values

```
defaultWidth = 10.0
defaultLateralPosition = 0.0
defaultRenderPriority = 10
defaultTexLength = 10.0
defaultFadeIn = 1.0
defaultFadeOut = 1.0
```

---

## Road Markings

### Center Line
- **Material**: `m_line_white_discontinue` (dashed white)
- **Width**: 0.2m
- **Position**: 0.0 (center)
- **Texture Length**: 5.0m
- **isTrackWidth**: false (fixed width)
- **Count**: 1 layer

### Edge Lines
- **Material**: `m_line_white` (solid white)
- **Width**: 0.25m
- **Position**: -1.0 (left), +1.0 (right)
- **Texture Length**: default (10.0m)
- **isTrackWidth**: false (fixed width)
- **Count**: 2 layers (left + right)

### Lane Lines
- **Material**: `m_line_white_discontinue` (dashed white)
- **Width**: 0.2m
- **Texture Length**: 5.0m
- **isTrackWidth**: false (fixed width)
- **Count**: Variable, based on road width

Lane position calculation:
```
halfWidth = roadWidth / 2
numLanes = floor(halfWidth / 5.0) - 1  // per side

Left lanes:  position = ((-i * 5.0) + 2.5) / halfWidth  (for i = 1..numLanes)
Right lanes: position = ((i * 5.0) - 2.5) / halfWidth   (for i = 1..numLanes)
```

---

## Edge Blends (Shoulder Transitions)

### Edge Blend 1 (Close - Asphalt Edge)
- **Material**: `m_road_asphalt_edge`
- **Width**: 1.0m
- **Position**: -1.1 (left), +1.1 (right)
- **Texture Length**: 10.0m
- **Render Priority**: `defaultPriority - 1` (renders below road surface)
- **isTrackWidth**: false
- **isFlip**: false (left), true (right)
- **Count**: 2 layers

### Edge Blend 2 (Mid - Dirt Transition)
- **Material**: `m_road_edge_dirt`
- **Width**: 2.0m
- **Position**: -1.25 (left), +1.25 (right)
- **Texture Length**: 10.0m
- **Render Priority**: `defaultPriority - 1`
- **isTrackWidth**: false
- **isFlip**: false (left), true (right)
- **Count**: 2 layers

### Edge Blend 3 (Far - Grass Transition)
- **Material**: `m_road_asphalt_edge_grass`
- **Width**: 3.0m
- **Position**: -1.35 (left), +1.35 (right)
- **Texture Length**: 10.0m
- **Render Priority**: `defaultPriority - 1`
- **isTrackWidth**: false
- **isFlip**: false (left), true (right)
- **Count**: 2 layers

---

## Wear Patterns (Per-Lane)

All wear pattern layers use `isTrackWidth = true` (follow road width) and are created per-lane.

### Light Tread Marks
- **Material**: `m_tread_marks_clean`
- **Texture Length**: 5.0m
- **Position**: Calculated per-lane (see formula below)

### Heavy Tread Marks
- **Material**: `road_rubber_double`
- **Texture Length**: 5.0m
- **Position**: Calculated per-lane

### Per-Lane Position Calculation

Full-length layers (tread marks, damage, etc.) span each lane:
```
halfWidth = roadWidth / 2
For each lane i (1-based):
  Left lane:  position = ((-i * 5.0) + 2.5) / halfWidth
  Right lane: position = ((i * 5.0) - 2.5) / halfWidth
```

This distributes layers evenly across the road width with 5m lane spacing.

---

## Damage & Repair Overlays (Per-Lane)

### Road Cracks
- **Material**: `m_asphalt_cracks_02`
- **Texture Length**: 15.0m
- **isTrackWidth**: true

### Repair 1
- **Material**: `repair1`
- **Texture Length**: 25.0m
- **isTrackWidth**: true

### Repair 2
- **Material**: `repair2`
- **Texture Length**: 25.0m
- **isTrackWidth**: true

### Road Patches
- **Material**: `road_patches1`
- **Texture Length**: 45.0m
- **isTrackWidth**: true

### Damage Asphalt 1
- **Material**: `m_asphalt_damaged_01`
- **Texture Length**: 10.0m
- **isTrackWidth**: true

### Damage Asphalt 2
- **Material**: `m_asphalt_damaged_02`
- **Texture Length**: 25.0m
- **isTrackWidth**: true

---

## Layer Creation Pattern

All auto-generated layers follow this pattern in `layerMgr.lua`:

```lua
-- Create layer with standard properties
layer = {
    name = categoryName .. " " .. sideName .. " " .. laneIndex,
    id = UUID,
    isDirty = true,
    isHidden = true,        -- Auto-generated layers are hidden from UI
    isEnabled = true,
    material = materialName,
    isFlip = isRightSide,   -- Only for edge blends
    isTrackWidth = true,    -- false for markings and edge blends
    width = layerWidth,
    position = calculatedPosition,
    texLen = textureLength,
    fadeIn = 1.0,
    fadeOut = 1.0,
    renderPriority = 10,    -- 9 for edge blends
    isOverObjects = false,
}
```

Key insight: Auto-generated layers have `isHidden = true` so they don't appear in the UI layer list but still generate DecalRoad objects.
