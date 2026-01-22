# Terrain Holes Editor Plugin - Implementation Plan

## Overview

This document outlines the implementation plan for creating a BeamNG.drive editor plugin that processes hole maps (PNG files) or JSON files to create holes in **existing terrain** without reimporting.

## Understanding Terrain Holes in BeamNG

### How Terrain Holes Actually Work

**KEY DISCOVERY**: Terrain holes are created by setting the **material index to 255** at specific terrain grid positions!

```lua
-- To CREATE a hole:
tb:setMaterialIdxWs(worldPosition, 255)

-- To RESTORE terrain (fill hole):
tb:setMaterialIdxWs(worldPosition, originalMaterialIndex)
```

This is used in [tunnelMesh.lua](ge/extensions/editor/tech/roadArchitect/tunnelMesh.lua#L109) for creating tunnel holes.

### Core Mechanism (from tunnelMesh.lua)

```lua
-- Creating holes:
local tb = extensions.editor_terrainEditor.getTerrainBlock()
holes[hCtr] = { p = vec3(x, y, 0), i = tb:getMaterialIdxWs(pos) }  -- Save original material
tb:setMaterialIdxWs(pos, 255)  -- Set to 255 = HOLE

-- After modifying, update the terrain:
tb:updateGridMaterials(minPos, maxPos)
tb:updateGrid(minPos, maxPos)
```

### Key Methods for Direct Hole Manipulation

| Method | Description |
|--------|-------------|
| `tb:setMaterialIdxWs(vec3, 255)` | **Creates a hole** at world position |
| `tb:setMaterialIdxWs(vec3, idx)` | **Restores terrain** with material index |
| `tb:getMaterialIdxWs(vec3)` | Gets current material index at position |
| `tb:updateGridMaterials(min, max)` | Updates material grid after changes |
| `tb:updateGrid(min, max)` | Updates terrain grid after changes |

### Hole Map PNG Format

- **File Format**: PNG image
- **Naming Convention**: `*_holeMap.png`
- **Pixel Interpretation**: 
  - **Black pixels (0,0,0)** = Hole (material index 255)
  - **White pixels (255,255,255)** = Solid terrain (keep existing material)
- **Resolution**: Should match terrain grid resolution

---

## Implementation Plan

### Phase 1: Core Plugin Structure

#### 1.1 Create Extension File

Create a new editor extension at:
```
lua/ge/extensions/editor/terrainHoleProcessor.lua
```

#### 1.2 Basic Module Structure

```lua
-- terrainHoleProcessor.lua
local M = {}
local im = ui_imgui

local windowName = "terrainHoleProcessor"
local logTag = "terrainHoleProcessor"

-- Store original materials for undo
local originalMaterials = {}

-- Configuration
local config = {
  holeMapPath = im.ArrayChar(256),
  jsonConfigPath = im.ArrayChar(256),
  invertHoles = im.BoolPtr(false),
  previewMode = im.BoolPtr(true)
}

local function onEditorInitialized()
  editor.registerWindow(windowName, im.ImVec2(400, 300))
end

local function onEditorGui()
  -- Window implementation
end

M.onEditorInitialized = onEditorInitialized
M.onEditorGui = onEditorGui

return M
```

---

### Phase 2: PNG Hole Map Processing

#### 2.1 Load and Parse PNG Hole Map

```lua
local function loadHoleMapFromPng(pngPath)
  local bitmap = GBitmap()
  if not bitmap:loadFile(pngPath) then
    log('E', logTag, 'Failed to load hole map: ' .. pngPath)
    return nil
  end
  
  local width = bitmap:getWidth()
  local height = bitmap:getHeight()
  local holes = {}
  
  for y = 0, height - 1 do
    for x = 0, width - 1 do
      local color = bitmap:getColor(x, y)
      -- Black pixels indicate holes
      if color.red < 128 and color.green < 128 and color.blue < 128 then
        table.insert(holes, {x = x, y = y})
      end
    end
  end
  
  return {
    width = width,
    height = height,
    holes = holes
  }
end
```

#### 2.2 Apply Holes to Existing Terrain (Direct Method)

```lua
local function applyHolesToExistingTerrain(holePositions)
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then
    log('E', logTag, 'No terrain block found')
    return false
  end
  
  local terrainPos = tb:getPosition()
  local gridSize = tb:getSquareSize()
  
  local xMin, xMax, yMin, yMax = 1e99, -1e99, 1e99, -1e99
  local savedMaterials = {}
  
  for _, hole in ipairs(holePositions) do
    -- Convert image coordinates to world coordinates
    local worldX = terrainPos.x + hole.x * gridSize
    local worldY = terrainPos.y + hole.y * gridSize
    local worldPos = vec3(worldX, worldY, 0)
    
    -- Track bounds for update
    xMin, xMax = math.min(xMin, worldX), math.max(xMax, worldX)
    yMin, yMax = math.min(yMin, worldY), math.max(yMax, worldY)
    
    -- Save original material for undo
    local originalMat = tb:getMaterialIdxWs(worldPos)
    table.insert(savedMaterials, { pos = worldPos, mat = originalMat })
    
    -- CREATE THE HOLE by setting material index to 255
    tb:setMaterialIdxWs(worldPos, 255)
  end
  
  -- Store for undo functionality
  originalMaterials = savedMaterials
  
  -- Update terrain grid (REQUIRED after changes)
  local te = extensions.editor_terrainEditor.getTerrainEditor()
  local gMin, gMax = Point2I(0, 0), Point2I(0, 0)
  te:worldToGridByPoint2I(vec3(xMin, yMin), gMin, tb)
  te:worldToGridByPoint2I(vec3(xMax, yMax), gMax, tb)
  
  tb:updateGridMaterials(vec3(gMin.x, gMin.y), vec3(gMax.x, gMax.y))
  tb:updateGrid(vec3(gMin.x, gMin.y), vec3(gMax.x, gMax.y))
  
  -- Mark terrain as dirty for saving
  editor_terrainEditor.setTerrainDirty()
  
  log('I', logTag, 'Applied ' .. #holePositions .. ' holes to terrain')
  return true
end
```

#### 2.3 Undo/Restore Holes

```lua
local function restoreHoles()
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb or not originalMaterials or #originalMaterials == 0 then
    return false
  end
  
  local xMin, xMax, yMin, yMax = 1e99, -1e99, 1e99, -1e99
  
  for _, saved in ipairs(originalMaterials) do
    local p = saved.pos
    xMin, xMax = math.min(xMin, p.x), math.max(xMax, p.x)
    yMin, yMax = math.min(yMin, p.y), math.max(yMax, p.y)
    
    -- Restore original material
    tb:setMaterialIdxWs(p, saved.mat)
  end
  
  -- Update terrain grid
  local te = extensions.editor_terrainEditor.getTerrainEditor()
  local gMin, gMax = Point2I(0, 0), Point2I(0, 0)
  te:worldToGridByPoint2I(vec3(xMin, yMin), gMin, tb)
  te:worldToGridByPoint2I(vec3(xMax, yMax), gMax, tb)
  
  tb:updateGridMaterials(vec3(gMin.x, gMin.y), vec3(gMax.x, gMax.y))
  tb:updateGrid(vec3(gMin.x, gMin.y), vec3(gMax.x, gMax.y))
  
  originalMaterials = {}
  editor_terrainEditor.setTerrainDirty()
  
  return true
end
```

---

### Phase 3: JSON Configuration Support

#### 3.1 JSON Schema Definition

```json
{
  "version": "1.0",
  "description": "Terrain Hole Configuration",
  "coordinateSystem": "world",
  "holes": [
    {
      "type": "point",
      "position": {"x": 100.5, "y": 200.3},
      "comment": "Single grid cell hole"
    },
    {
      "type": "rectangle",
      "min": {"x": 100, "y": 200},
      "max": {"x": 150, "y": 230},
      "comment": "Rectangular area of holes"
    },
    {
      "type": "circle",
      "center": {"x": 300, "y": 400},
      "radius": 25,
      "comment": "Circular area of holes"
    },
    {
      "type": "polygon",
      "points": [
        {"x": 500, "y": 100},
        {"x": 550, "y": 150},
        {"x": 520, "y": 200},
        {"x": 480, "y": 180}
      ],
      "comment": "Polygon-shaped hole area"
    },
    {
      "type": "image",
      "path": "/levels/mylevel/art/terrain/custom_holes.png",
      "offset": {"x": 0, "y": 0},
      "scale": 1.0,
      "comment": "Holes from PNG mask"
    }
  ],
  "options": {
    "invertHoles": false,
    "mergeWithExisting": true
  }
}
```

#### 3.2 JSON Processor - Generate World Positions

```lua
local function processJsonHoleConfig(jsonPath)
  local config = jsonReadFile(jsonPath)
  if not config then
    log('E', logTag, 'Failed to load JSON config: ' .. jsonPath)
    return nil
  end
  
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then return nil end
  
  local gridSize = tb:getSquareSize()
  local worldHoles = {}
  
  for _, hole in ipairs(config.holes or {}) do
    if hole.type == "point" then
      table.insert(worldHoles, vec3(hole.position.x, hole.position.y, 0))
      
    elseif hole.type == "rectangle" then
      for x = hole.min.x, hole.max.x, gridSize do
        for y = hole.min.y, hole.max.y, gridSize do
          table.insert(worldHoles, vec3(x, y, 0))
        end
      end
      
    elseif hole.type == "circle" then
      local cx, cy, r = hole.center.x, hole.center.y, hole.radius
      for x = cx - r, cx + r, gridSize do
        for y = cy - r, cy + r, gridSize do
          if (x - cx)^2 + (y - cy)^2 <= r * r then
            table.insert(worldHoles, vec3(x, y, 0))
          end
        end
      end
      
    elseif hole.type == "polygon" then
      local positions = processPolygonHole(hole.points, gridSize)
      for _, pos in ipairs(positions) do
        table.insert(worldHoles, pos)
      end
      
    elseif hole.type == "image" then
      local imgHoles = loadHoleMapFromPng(hole.path)
      if imgHoles then
        local offsetX = hole.offset and hole.offset.x or 0
        local offsetY = hole.offset and hole.offset.y or 0
        local scale = hole.scale or 1.0
        for _, h in ipairs(imgHoles.holes) do
          table.insert(worldHoles, vec3(
            offsetX + h.x * gridSize * scale,
            offsetY + h.y * gridSize * scale, 0
          ))
        end
      end
    end
  end
  
  return worldHoles
end

-- Point-in-polygon test for polygon holes
local function isPointInPolygon(x, y, polygon)
  local inside = false
  local j = #polygon
  for i = 1, #polygon do
    local xi, yi = polygon[i].x, polygon[i].y
    local xj, yj = polygon[j].x, polygon[j].y
    if ((yi > y) ~= (yj > y)) and (x < (xj - xi) * (y - yi) / (yj - yi) + xi) then
      inside = not inside
    end
    j = i
  end
  return inside
end

local function processPolygonHole(points, gridSize)
  local positions = {}
  -- Find bounding box
  local minX, maxX, minY, maxY = 1e99, -1e99, 1e99, -1e99
  for _, p in ipairs(points) do
    minX, maxX = math.min(minX, p.x), math.max(maxX, p.x)
    minY, maxY = math.min(minY, p.y), math.max(maxY, p.y)
  end
  -- Test each grid point
  for x = minX, maxX, gridSize do
    for y = minY, maxY, gridSize do
      if isPointInPolygon(x, y, points) then
        table.insert(positions, vec3(x, y, 0))
      end
    end
  end
  return positions
end
```

---

### Phase 4: Main Apply Function

#### 4.1 Unified Hole Application

```lua
-- Main function to apply holes from either PNG or JSON
local function applyHolesFromFile(filePath)
  local worldPositions = {}
  
  if string.match(filePath, "%.png$") then
    -- PNG file - convert image coords to world coords
    local holeData = loadHoleMapFromPng(filePath)
    if not holeData then return false end
    
    local tb = extensions.editor_terrainEditor.getTerrainBlock()
    local terrainPos = tb:getPosition()
    local gridSize = tb:getSquareSize()
    
    for _, hole in ipairs(holeData.holes) do
      table.insert(worldPositions, vec3(
        terrainPos.x + hole.x * gridSize,
        terrainPos.y + hole.y * gridSize, 0
      ))
    end
    
  elseif string.match(filePath, "%.json$") then
    -- JSON file - already returns world positions
    worldPositions = processJsonHoleConfig(filePath)
    if not worldPositions then return false end
  else
    log('E', logTag, 'Unsupported file format: ' .. filePath)
    return false
  end
  
  -- Apply all holes
  return applyHolesAtWorldPositions(worldPositions)
end

-- Core function that applies holes at world positions
local function applyHolesAtWorldPositions(worldPositions)
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then
    log('E', logTag, 'No terrain block found')
    return false
  end
  
  if #worldPositions == 0 then
    log('W', logTag, 'No hole positions provided')
    return false
  end
  
  local xMin, xMax, yMin, yMax = 1e99, -1e99, 1e99, -1e99
  local savedMaterials = {}
  
  for _, worldPos in ipairs(worldPositions) do
    -- Track bounds for grid update
    xMin = math.min(xMin, worldPos.x)
    xMax = math.max(xMax, worldPos.x)
    yMin = math.min(yMin, worldPos.y)
    yMax = math.max(yMax, worldPos.y)
    
    -- Save original material for undo
    local originalMat = tb:getMaterialIdxWs(worldPos)
    if originalMat ~= 255 then  -- Don't save if already a hole
      table.insert(savedMaterials, { pos = vec3(worldPos), mat = originalMat })
    end
    
    -- CREATE THE HOLE: Material index 255 = hole
    tb:setMaterialIdxWs(worldPos, 255)
  end
  
  -- Store for undo
  originalMaterials = savedMaterials
  
  -- Update terrain grid (CRITICAL!)
  local te = extensions.editor_terrainEditor.getTerrainEditor()
  local gMin, gMax = Point2I(0, 0), Point2I(0, 0)
  te:worldToGridByPoint2I(vec3(xMin, yMin), gMin, tb)
  te:worldToGridByPoint2I(vec3(xMax, yMax), gMax, tb)
  
  local updateMin = vec3(gMin.x, gMin.y)
  local updateMax = vec3(gMax.x, gMax.y)
  tb:updateGridMaterials(updateMin, updateMax)
  tb:updateGrid(updateMin, updateMax)
  
  -- Mark as dirty for save prompt
  editor_terrainEditor.setTerrainDirty()
  
  log('I', logTag, 'Successfully applied ' .. #worldPositions .. ' holes')
  return true
end
```

---

### Phase 5: Editor UI Implementation

#### 5.1 Main Window

```lua
local function terrainHoleProcessorWindow()
  if editor.beginWindow(windowName, "Terrain Hole Processor") then
    
    -- Check for terrain
    local tb = extensions.editor_terrainEditor.getTerrainBlock()
    if not tb then
      im.TextColored(im.ImVec4(1, 0.3, 0.3, 1), "No terrain found in level!")
      editor.endWindow()
      return
    end
    
    -- Terrain info
    im.Text("Terrain: " .. tb:getName())
    im.Text("Grid Size: " .. tb:getSquareSize() .. " m")
    im.Separator()
    
    -- Input Mode Selection
    im.Text("Input Source:")
    if im.RadioButton2("PNG Hole Map", inputMode == 0) then inputMode = 0 end
    im.SameLine()
    if im.RadioButton2("JSON Configuration", inputMode == 1) then inputMode = 1 end
    
    im.Separator()
    
    if inputMode == 0 then
      -- PNG Input
      im.Text("Hole Map Image:")
      im.PushItemWidth(im.GetContentRegionAvailWidth() - 80)
      im.InputText("##holemappath", config.holeMapPath)
      im.PopItemWidth()
      im.SameLine()
      if im.Button("Browse...##holemap") then
        editor_fileDialog.openFile(
          function(data) 
            ffi.copy(config.holeMapPath, data.filepath) 
          end,
          {{"PNG Images", ".png"}, {"Any files", "*"}},
          false
        )
      end
    else
      -- JSON Input
      im.Text("JSON Configuration:")
      im.PushItemWidth(im.GetContentRegionAvailWidth() - 80)
      im.InputText("##jsonpath", config.jsonConfigPath)
      im.PopItemWidth()
      im.SameLine()
      if im.Button("Browse...##json") then
        editor_fileDialog.openFile(
          function(data) 
            ffi.copy(config.jsonConfigPath, data.filepath) 
          end,
          {{"JSON Files", ".json"}, {"Any files", "*"}},
          false
        )
      end
    end
    
    im.Separator()
    
    -- Options
    im.Checkbox("Invert Holes (white = hole)", config.invertHoles)
    im.Checkbox("Preview Mode", config.previewMode)
    
    im.Separator()
    
    -- Action Buttons
    if im.Button("Preview", im.ImVec2(100, 0)) then
      previewHoles()
    end
    im.SameLine()
    if im.Button("Apply Holes", im.ImVec2(100, 0)) then
      local path = inputMode == 0 and ffi.string(config.holeMapPath) or ffi.string(config.jsonConfigPath)
      if #path > 0 then
        applyHolesFromFile(path)
      end
    end
    im.SameLine()
    if im.Button("Undo", im.ImVec2(80, 0)) then
      restoreHoles()
    end
    
    im.Separator()
    
    -- Status
    if #originalMaterials > 0 then
      im.TextColored(im.ImVec4(0.3, 1, 0.3, 1), 
        string.format("Last operation: %d holes (can undo)", #originalMaterials))
    end
    
  end
  editor.endWindow()
end
```

---

### Phase 6: Preview and Visualization

#### 6.1 Debug Visualization

```lua
local previewPositions = {}
local showPreview = false

local function previewHoles()
  local path = inputMode == 0 and ffi.string(config.holeMapPath) or ffi.string(config.jsonConfigPath)
  if #path == 0 then return end
  
  previewPositions = {}
  
  if string.match(path, "%.png$") then
    local holeData = loadHoleMapFromPng(path)
    if holeData then
      local tb = extensions.editor_terrainEditor.getTerrainBlock()
      local terrainPos = tb:getPosition()
      local gridSize = tb:getSquareSize()
      for _, hole in ipairs(holeData.holes) do
        table.insert(previewPositions, vec3(
          terrainPos.x + hole.x * gridSize,
          terrainPos.y + hole.y * gridSize, 0
        ))
      end
    end
  elseif string.match(path, "%.json$") then
    previewPositions = processJsonHoleConfig(path) or {}
  end
  
  showPreview = true
  log('I', logTag, 'Preview: ' .. #previewPositions .. ' holes')
end

local function renderPreview()
  if not showPreview or #previewPositions == 0 then return end
  
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then return end
  
  local gridSize = tb:getSquareSize()
  
  for _, pos in ipairs(previewPositions) do
    local z = core_terrain.getTerrainHeight(pos) or 0
    local drawPos = vec3(pos.x, pos.y, z + 0.1)
    
    -- Draw red square marker for each hole position
    debugDrawer:drawSquarePrism(
      drawPos,
      drawPos + vec3(0, 0, 0.5),
      Point2F(gridSize * 0.4, gridSize * 0.4),
      Point2F(gridSize * 0.2, gridSize * 0.2),
      ColorF(1, 0, 0, 0.6)
    )
  end
end

-- Call this from onEditorGui or update loop
local function onEditorGui()
  terrainHoleProcessorWindow()
  renderPreview()
end
```

---

### Phase 7: Export Current Holes

#### 7.1 Export Terrain Holes to PNG

```lua
local function exportCurrentHoles(outputPath)
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then return false end
  
  -- Use built-in export function
  local prefix = string.gsub(outputPath, "%.png$", "")
  tb:exportHoleMaps(prefix, 'png')
  
  log('I', logTag, 'Exported hole maps to: ' .. prefix .. '_holeMap.png')
  return true
end
```

---

## Complete Plugin File Structure

```
lua/ge/extensions/editor/terrainHoleProcessor.lua
```

### Full Implementation Example

```lua
-- This Source Code Form is subject to the terms of the bCDDL, v. 1.1.
-- terrainHoleProcessor.lua - Terrain Hole Processor Editor Plugin

local M = {}
local im = ui_imgui
local ffi = require('ffi')

local windowName = "terrainHoleProcessor"
local logTag = "terrainHoleProcessor"

-- State
local inputMode = 0  -- 0 = PNG, 1 = JSON
local originalMaterials = {}
local previewPositions = {}
local showPreview = false

-- Config (ImGui pointers)
local config = {
  holeMapPath = im.ArrayChar(512),
  jsonConfigPath = im.ArrayChar(512),
  invertHoles = im.BoolPtr(false),
}

-- [Include all functions from phases 2-6 here]

local function onEditorInitialized()
  editor.registerWindow(windowName, im.ImVec2(450, 350))
  editor.addWindowMenuItem("Terrain Hole Processor", function() 
    editor.showWindow(windowName) 
  end, {groupMenuName = "Experimental"})
end

local function onEditorGui()
  if editor.beginWindow(windowName, "Terrain Hole Processor") then
    terrainHoleProcessorWindow()
  end
  editor.endWindow()
  
  if showPreview then
    renderPreview()
  end
end

-- Public interface
M.onEditorInitialized = onEditorInitialized
M.onEditorGui = onEditorGui
M.applyHolesFromFile = applyHolesFromFile
M.applyHolesAtWorldPositions = applyHolesAtWorldPositions
M.restoreHoles = restoreHoles

return M
```

---

## API Reference

### Direct Terrain Hole Methods (KEY!)

| Method | Description |
|--------|-------------|
| `tb:setMaterialIdxWs(vec3, 255)` | **Creates a hole** at world position |
| `tb:setMaterialIdxWs(vec3, idx)` | **Restores terrain** with material index |
| `tb:getMaterialIdxWs(vec3)` | Gets current material index (255 = hole) |
| `tb:updateGridMaterials(min, max)` | **Required** after material changes |
| `tb:updateGrid(min, max)` | **Required** after terrain changes |
| `tb:exportHoleMaps(prefix, format)` | Export holes to PNG |

### Helper Methods

| Method | Description |
|--------|-------------|
| `extensions.editor_terrainEditor.getTerrainBlock()` | Get active TerrainBlock |
| `extensions.editor_terrainEditor.getTerrainEditor()` | Get TerrainEditor instance |
| `te:worldToGridByPoint2I(vec3, Point2I, tb)` | Convert world to grid coords |
| `editor_terrainEditor.setTerrainDirty()` | Mark terrain for save |

---

## .TER File Binary Format (For Direct Creation in .NET)

### Overview

The `.ter` file is a **binary file** based on Torque3D's terrain format. You can create it directly in your .NET 9 application without needing a Lua plugin!

### File Structure

From the Torque3D source code ([terrFile.cpp#L267-L293](https://github.com/GarageGames/Torque3D/blob/main/Engine/source/terrain/terrFile.cpp#L267-L293)):

```
┌─────────────────────────────────────────────────────┐
│ FILE_VERSION (1 byte, U8) = 7                       │
├─────────────────────────────────────────────────────┤
│ Size (4 bytes, U32) - e.g., 256, 512, 1024, 2048    │
├─────────────────────────────────────────────────────┤
│ HeightMap[Size * Size] (U16 each, 2 bytes)          │
│ - 11.5 fixed point format                           │
│ - Total: Size * Size * 2 bytes                      │
├─────────────────────────────────────────────────────┤
│ LayerMap[Size * Size] (U8 each, 1 byte)             │
│ - Material index per grid cell                      │
│ - **255 = HOLE**                                    │
│ - Total: Size * Size * 1 byte                       │
├─────────────────────────────────────────────────────┤
│ MaterialCount (4 bytes, U32)                        │
├─────────────────────────────────────────────────────┤
│ Material Names (String for each material)           │
│ - Each string: Length (U16) + UTF-8 chars           │
└─────────────────────────────────────────────────────┘
```

### Height Map Format

Heights are stored as **11.5 fixed point** (U16):
- Range: 0 to 2048 meters
- Precision: 1/32 meter (0.03125m)

```csharp
// Convert float height to fixed point
ushort FloatToFixed(float height) => (ushort)(height * 32.0f);

// Convert fixed point to float
float FixedToFloat(ushort fixedHeight) => fixedHeight / 32.0f;
```

### Layer Map (Material Index) - WHERE HOLES ARE DEFINED!

The `LayerMap` is a flat array of bytes (U8), one per grid cell:
- **Index 0-254**: Valid material indices (0 = first material, 1 = second, etc.)
- **Index 255 = HOLE** (terrain is empty/invisible at that cell)

### Example: 4x4 Terrain with 3 Materials and 1 Hole

```
Materials:
  0 = "Grass"
  1 = "Dirt" 
  2 = "Rock"

Grid (4x4 = 16 cells):
  Position (0,0) to (3,3)

LayerMap array (16 bytes):
  [0]  [1]  [2]  [0]     ← Row 0: Grass, Dirt, Rock, Grass
  [0]  [255][1]  [0]     ← Row 1: Grass, HOLE, Dirt, Grass  
  [2]  [0]  [0]  [1]     ← Row 2: Rock, Grass, Grass, Dirt
  [0]  [0]  [2]  [0]     ← Row 3: Grass, Grass, Rock, Grass

Binary (row-major order):
  0x00, 0x01, 0x02, 0x00,  // Row 0
  0x00, 0xFF, 0x01, 0x00,  // Row 1 (0xFF = 255 = HOLE)
  0x02, 0x00, 0x00, 0x01,  // Row 2
  0x00, 0x00, 0x02, 0x00   // Row 3
```

### C# Implementation Example

```csharp
using System;
using System.IO;
using System.Text;

public class BeamNGTerrainFile
{
    public const byte FILE_VERSION = 7;
    public const byte HOLE_INDEX = 255;
    
    public uint Size { get; set; }           // Must be power of 2 (256, 512, 1024, 2048)
    public ushort[] HeightMap { get; set; }  // Size * Size elements
    public byte[] LayerMap { get; set; }     // Size * Size elements (255 = hole)
    public string[] MaterialNames { get; set; }
    
    public BeamNGTerrainFile(uint size)
    {
        Size = size;
        HeightMap = new ushort[size * size];
        LayerMap = new byte[size * size];
        MaterialNames = Array.Empty<string>();
    }
    
    /// <summary>
    /// Sets a hole at the specified grid position
    /// </summary>
    public void SetHole(int x, int y)
    {
        LayerMap[x + y * Size] = HOLE_INDEX;
    }
    
    /// <summary>
    /// Sets the material index at the specified grid position
    /// </summary>
    public void SetMaterial(int x, int y, byte materialIndex)
    {
        if (materialIndex == HOLE_INDEX)
            throw new ArgumentException("Use SetHole() to create holes");
        LayerMap[x + y * Size] = materialIndex;
    }
    
    /// <summary>
    /// Checks if position is a hole
    /// </summary>
    public bool IsHole(int x, int y)
    {
        return LayerMap[x + y * Size] == HOLE_INDEX;
    }
    
    /// <summary>
    /// Sets height at grid position (in meters)
    /// </summary>
    public void SetHeight(int x, int y, float heightMeters)
    {
        // 11.5 fixed point: multiply by 32
        HeightMap[x + y * Size] = (ushort)Math.Clamp(heightMeters * 32.0f, 0, ushort.MaxValue);
    }
    
    /// <summary>
    /// Saves the terrain to a .ter file
    /// </summary>
    public void Save(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        
        // Write version
        writer.Write(FILE_VERSION);
        
        // Write size
        writer.Write(Size);
        
        // Write height map (U16 each)
        foreach (var height in HeightMap)
        {
            writer.Write(height);
        }
        
        // Write layer map (U8 each) - THIS IS WHERE HOLES ARE!
        writer.Write(LayerMap);
        
        // Write material count
        writer.Write((uint)MaterialNames.Length);
        
        // Write material names (Torque string format)
        foreach (var name in MaterialNames)
        {
            WriteTorqueString(writer, name);
        }
    }
    
    /// <summary>
    /// Loads terrain from a .ter file
    /// </summary>
    public static BeamNGTerrainFile Load(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open);
        using var reader = new BinaryReader(stream);
        
        byte version = reader.ReadByte();
        if (version != FILE_VERSION)
            throw new InvalidDataException($"Unsupported terrain version: {version}");
        
        uint size = reader.ReadUInt32();
        var terrain = new BeamNGTerrainFile(size);
        
        // Read height map
        for (int i = 0; i < size * size; i++)
        {
            terrain.HeightMap[i] = reader.ReadUInt16();
        }
        
        // Read layer map
        terrain.LayerMap = reader.ReadBytes((int)(size * size));
        
        // Read materials
        uint matCount = reader.ReadUInt32();
        terrain.MaterialNames = new string[matCount];
        for (int i = 0; i < matCount; i++)
        {
            terrain.MaterialNames[i] = ReadTorqueString(reader);
        }
        
        return terrain;
    }
    
    private static void WriteTorqueString(BinaryWriter writer, string str)
    {
        // Torque uses length-prefixed strings
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        writer.Write((byte)bytes.Length); // String length as byte
        writer.Write(bytes);
    }
    
    private static string ReadTorqueString(BinaryReader reader)
    {
        byte length = reader.ReadByte();
        byte[] bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
}
```

### Usage Example - Creating Terrain with Holes in .NET

```csharp
// Create a 256x256 terrain
var terrain = new BeamNGTerrainFile(256);

// Set up materials
terrain.MaterialNames = new[] { "Grass", "Dirt", "Rock" };

// Fill with grass (material index 0)
Array.Fill(terrain.LayerMap, (byte)0);

// Set some heights
for (int y = 0; y < 256; y++)
{
    for (int x = 0; x < 256; x++)
    {
        float height = MathF.Sin(x * 0.1f) * 10 + 50; // Example: sine wave
        terrain.SetHeight(x, y, height);
    }
}

// Create holes in a rectangular area (for a tunnel entrance, etc.)
for (int y = 100; y < 110; y++)
{
    for (int x = 50; x < 70; x++)
    {
        terrain.SetHole(x, y);  // Sets LayerMap[index] = 255
    }
}

// Create a circular hole
int centerX = 150, centerY = 150, radius = 15;
for (int y = centerY - radius; y <= centerY + radius; y++)
{
    for (int x = centerX - radius; x <= centerX + radius; x++)
    {
        if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= radius * radius)
        {
            terrain.SetHole(x, y);
        }
    }
}

// Save to file
terrain.Save(@"C:\path\to\levels\mylevel\art\terrain\theTerrain.ter");
```

### Processing a Hole Map PNG in .NET

```csharp
using System.Drawing;

public static void ApplyHoleMapFromPng(BeamNGTerrainFile terrain, string pngPath)
{
    using var bitmap = new Bitmap(pngPath);
    
    // PNG should match terrain size
    if (bitmap.Width != terrain.Size || bitmap.Height != terrain.Size)
        throw new ArgumentException("PNG dimensions must match terrain size");
    
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            Color pixel = bitmap.GetPixel(x, y);
            
            // Black pixel = hole
            if (pixel.R < 128 && pixel.G < 128 && pixel.B < 128)
            {
                terrain.SetHole(x, y);
            }
        }
    }
}
```

### Important Notes

1. **Size must be power of 2**: 256, 512, 1024, 2048 are valid
2. **Coordinate system**: X increases right, Y increases "forward" in world space
3. **Material names** must match `TerrainMaterial` objects defined in `main.materials.json`
4. **After placing .ter file**: The level's `.mis` file needs a `TerrainBlock` object pointing to it

### File Location

Place your generated `.ter` file at:
```
/levels/<levelname>/art/terrain/theTerrain.ter
```

And reference it in the level's mission file (`.mis` or prefab):
```
new TerrainBlock(theTerrain) {
   terrainFile = "art/terrain/theTerrain.ter";
   squareSize = "1";
   position = "-512 -512 0";
};
```

---

## Quick Start Code

### Minimal Working Example

```lua
-- Quick example: Apply holes at specific world positions
local function quickApplyHoles()
  local tb = extensions.editor_terrainEditor.getTerrainBlock()
  if not tb then return end
  
  -- Define hole positions (world coordinates)
  local holePositions = {
    vec3(100, 200, 0),
    vec3(105, 200, 0),
    vec3(100, 205, 0),
    vec3(105, 205, 0),
  }
  
  -- Apply holes
  for _, pos in ipairs(holePositions) do
    tb:setMaterialIdxWs(pos, 255)  -- 255 = HOLE
  end
  
  -- Update terrain (REQUIRED!)
  tb:updateGridMaterials(vec3(95, 195), vec3(110, 210))
  tb:updateGrid(vec3(95, 195), vec3(110, 210))
  
  editor_terrainEditor.setTerrainDirty()
end
```

---

## Testing Plan

1. **Unit Tests**
   - PNG loading and pixel parsing
   - JSON configuration parsing
   - Shape generation (rect, circle, polygon)
   - Coordinate conversion (image to world)

2. **Integration Tests**
   - Apply single hole at world position
   - Apply multiple holes from PNG
   - Apply holes from JSON with shapes
   - Undo/restore functionality
   - Preview visualization accuracy

3. **Manual Testing**
   - Load various PNG hole maps
   - Test with different terrain grid sizes
   - Verify hole placement accuracy
   - Test edge cases (border holes, overlapping)
   - Verify terrain saves correctly

---

## Dependencies

- `extensions.editor_terrainEditor` - Terrain editor functionality
- `core_terrain` - Core terrain access
- `editor_fileDialog` - File browser dialogs (optional)
- `ui_imgui` - ImGui for UI
- `GBitmap` - Bitmap manipulation for PNG loading
- `debugDrawer` - Preview visualization

---

## Important Notes

1. **Material Index 255**: This is the magic number - setting material index to 255 creates a hole
2. **Grid Update Required**: Always call `updateGridMaterials()` and `updateGrid()` after modifications
3. **Save State**: Call `editor_terrainEditor.setTerrainDirty()` to enable save prompt
4. **Coordinate System**: Holes use world coordinates; PNG images need conversion based on terrain position and grid size
5. **Reference Implementation**: See [tunnelMesh.lua](ge/extensions/editor/tech/roadArchitect/tunnelMesh.lua#L109) for working example

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-01-22 | Initial implementation plan |
| 0.2 | 2026-01-22 | Updated with direct hole manipulation method (material index 255) - no terrain reimport needed |

