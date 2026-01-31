# BeamNG.drive Materials Documentation

## Overview

BeamNG.drive uses a JSON-based material system (`*.materials.json`) to define surface properties for 3D objects (TSStatic, DAE files). This document provides comprehensive documentation for implementing material rendering in external 3D viewers.

## File Structure

Materials are stored in `*.materials.json` files, typically named `main.materials.json`. These files are found throughout the game's asset structure:

```
/art/shapes/*/main.materials.json
/levels/*/art/shapes/*/main.materials.json
/vehicles/*/main.materials.json
```

---

## Material Versions

BeamNG supports two material system versions:

| Version | Description |
|---------|-------------|
| **1.0** | Legacy material system (sRGB colorspace for all textures) |
| **1.5** | PBR (Physically Based Rendering) system with proper linear/sRGB colorspace handling |

### Version Detection
```json
{
  "MaterialName": {
    "version": 1.5,
    ...
  }
}
```

---

## Core Material Properties

### Basic Identification

| Property | Type | Description |
|----------|------|-------------|
| `mapTo` | string | Links material to mesh surfaces by name. Maps the material to specific geometry in the DAE file |
| `name` | string | Internal material name/identifier |
| `materialTag0-2` | string | Categorization tags (e.g., "beamng", "RoadAndPath") |
| `groundType` | string | Physics ground type (e.g., "ASPHALT", "DIRT", "GRASS") |

---

## Texture Maps

### Version 1.0 (Legacy) Texture Maps

| Property | Format | Description |
|----------|--------|-------------|
| `colorMap` | sRGB | Base color/diffuse texture (alias: `diffuseMap`) |
| `normalMap` | Linear | Normal map for surface detail |
| `specularMap` | sRGB | Specular/gloss information |
| `detailMap` | sRGB | Tiled detail texture |
| `detailNormalMap` | Linear | Normal map for detail layer |
| `overlayMap` | sRGB | Overlay texture (uses UV2) |
| `reflectivityMap` | Linear | Environment reflection intensity |
| `lightMap` | Linear | Pre-baked lighting information |
| `opacityMap` | Linear | Alpha/transparency mask |
| `envMap` | sRGB | Environment map override |
| `colorPaletteMap` | sRGB | Color palette for paintable regions |

### Version 1.5 (PBR) Texture Maps

| Property | Format | Recommended Compression | Description |
|----------|--------|------------------------|-------------|
| `baseColorMap` / `diffuseMap` | sRGB | BC7_SRGB | Albedo/base color |
| `normalMap` | Linear | BC5/3Dc | Surface normals (RG channels) |
| `metallicMap` | Linear | BC4 | Metallic factor (grayscale) |
| `roughnessMap` | Linear | BC4 | Surface roughness (grayscale) |
| `ambientOcclusionMap` | Linear | BC4 | Ambient occlusion (grayscale) |
| `opacityMap` | Linear | BC4 | Transparency mask (grayscale) |
| `emissiveMap` | Linear | BC7 | Self-illumination color |
| `clearCoatMap` | Linear | BC4 | Clear coat intensity |
| `clearCoatBottomNormalMap` | Linear | BC5 | Normal map under clear coat |
| `detailMap` | Linear | BC7 | Detail albedo texture |
| `detailNormalMap` | Linear | BC5 | Detail normal map |
| `colorPaletteMap` | sRGB | BC7 | Paintable regions mask |

### Texture Path Resolution

Textures can be specified as:
- **Absolute paths**: `/art/shapes/buildings/texture.dds`
- **Relative paths**: `texture.dds` (relative to material file location)
- **Tagged textures**: `@tagname` or `^levelrelative` (special runtime resolution)

### Texture Cooker Suffixes

BeamNG uses a texture cooker system with specific suffixes:
- `.color.png` → Compiled to BC7 sRGB
- `.normal.png` → Compiled to BC5 (normal maps)
- `.data.png` → Compiled to BC4/BC7 linear

---

## Material Factors & Colors

### Diffuse/Base Color

```json
{
  "diffuseColor": "1 1 1 1"
}
```

Format: `"R G B A"` - Space-separated float values (0.0-1.0+, HDR supported)

### PBR Factors (Version 1.5)

| Property | Type | Range | Default | Description |
|----------|------|-------|---------|-------------|
| `metallicFactor` | float | 0-1 | 0 | Surface metalness |
| `roughnessFactor` | float | 0-1 | 0.5 | Surface roughness |
| `normalMapStrength` | float | 0+ | 1 | Normal map intensity |
| `opacityFactor` | float | 0-1 | 1 | Base opacity multiplier |
| `emissiveFactor` | color4 | 0+ | "0 0 0 1" | Emissive color/intensity (HDR) |
| `clearCoatFactor` | float | 0-1 | 0 | Clear coat intensity |
| `clearCoatRoughnessFactor` | float | 0-1 | 0 | Clear coat roughness |
| `detailScale` | vec2 | any | "1 1" | Detail texture UV scale |
| `detailBaseColorMapStrength` | float | 0-1 | 1 | Detail albedo blend |
| `detailNormalMapStrength` | float | 0-1 | 1 | Detail normal blend |
| `reflectivityMapFactor` | float | 0-1 | 1 | Reflection intensity multiplier |

### Legacy Factors (Version 1.0)

| Property | Type | Description |
|----------|------|-------------|
| `specular` | color4 | Specular color `"R G B A"` |
| `specularPower` | float | Specular exponent/shininess |
| `minnaertConstant` | float | Minnaert shading constant |
| `glowFactor` | color4 | Glow/bloom color |

---

## Instance Color System

### Overview

Instance color allows per-object color tinting without creating new materials. This is critical for vehicle paint systems.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `instanceDiffuse` | bool | Enable instance color multiplication on base color |
| `instanceEmissive` | bool | Enable instance color on emissive channel |
| `instanceOpacity` | bool | Enable instance alpha on opacity |

### Instance Color Format

Instance colors are set per-object (TSStatic, Vehicle) as RGBA values:

```lua
obj:setField('instanceColor', 0, 'R G B A')
```

Example: `'1 0 0 1'` = Red with full opacity

### Usage in Materials

When `instanceDiffuse = true`:
```
finalColor = textureColor * instanceColor
```

### Color Palette System

For vehicle paints with multiple color zones:

| Property | Description |
|----------|-------------|
| `colorPaletteMap` | Texture defining paint regions (R/G/B channels = different zones) |
| `paletteBaseColor` | Apply palette to base color |
| `paletteMetallic` | Apply palette to metallic |
| `paletteRoughness` | Apply palette to roughness |
| `paletteClearCoat` | Apply palette to clear coat |
| `paletteClearCoatRoughness` | Apply palette to clear coat roughness |

The engine provides multiple instance colors for palette zones:
- `instanceColor` - Primary color
- `instanceColorPalette0` - Secondary color zone
- `instanceColorPalette1` - Tertiary color zone

---

## Transparency & Alpha Settings

### Translucency Mode

| Property | Type | Values | Description |
|----------|------|--------|-------------|
| `translucent` | bool/string | "0"/"1" | Enable transparency |
| `translucentBlendOp` | string | See below | Blend operation mode |

### Blend Operations

| Mode | Description | Use Case |
|------|-------------|----------|
| `None` | No blending (opaque) | Default solid surfaces |
| `PreMulAlpha` | Pre-multiplied alpha (v1.5+) | Proper alpha compositing |
| `LerpAlpha` | Linear interpolation alpha | Standard transparency |
| `Add` | Additive blending | Lights, glows |
| `AddAlpha` | Additive with alpha | Particles, light rays |
| `Mul` | Multiplicative | Shadows, darkening |
| `Sub` | Subtractive | Special effects |

### Alpha Testing (Cutout)

For binary transparency (foliage, fences):

| Property | Type | Range | Description |
|----------|------|-------|-------------|
| `alphaTest` | bool | - | Enable alpha testing |
| `alphaRef` | int | 0-255 | Alpha threshold (pixels below are discarded) |

### Z-Buffer Control

| Property | Type | Description |
|----------|------|-------------|
| `translucentZWrite` | bool | Write to depth buffer when translucent |
| `translucentRecvShadows` | bool | Receive shadows when translucent |

---

## Geometry Settings

### Double-Sided Rendering

```json
{
  "doubleSided": "1",
  "invertBackFaceNormals": "0"
}
```

| Property | Description |
|----------|-------------|
| `doubleSided` | Render both sides of polygons |
| `invertBackFaceNormals` | Flip normals for backfaces |

### Shadow Casting

```json
{
  "castShadows": "1"
}
```

---

## Multi-Layer Materials

Version 1.5 supports up to 4 layers per material:

```json
{
  "MaterialName": {
    "version": 1.5,
    "activeLayers": 2,
    "diffuseMap[0]": "base_color.dds",
    "diffuseMap[1]": "decal_color.dds",
    "normalMap[0]": "base_normal.dds",
    "normalMap[1]": "decal_normal.dds"
  }
}
```

### Layer Properties

Each texture/property can have layer indices `[0]` through `[3]`:
- Layer 0: Base layer
- Layers 1-3: Detail/decal layers

### UV Channel Selection

| Property | Values | Description |
|----------|--------|-------------|
| `diffuseMapUseUV` | "0"/"1" | UV channel for diffuse |
| `normalMapUseUV` | "0"/"1" | UV channel for normal |
| `metallicMapUseUV` | "0"/"1" | UV channel for metallic |
| `roughnessMapUseUV` | "0"/"1" | UV channel for roughness |
| `ambientOcclusionMapUseUV` | "0"/"1" | UV channel for AO |
| `opacityMapUseUV` | "0"/"1" | UV channel for opacity |
| `emissiveMapUseUV` | "0"/"1" | UV channel for emissive |
| `clearCoatMapUseUV` | "0"/"1" | UV channel for clear coat |
| `colorPaletteMapUseUV` | "0"/"1" | UV channel for color palette |
| `detailMapUseUV` | "0"/"1" | UV channel for detail |

---

## Animation Properties

Materials support animated textures:

### Animation Flags

```json
{
  "animFlags": "0x00000001"
}
```

| Flag | Hex Value | Description |
|------|-----------|-------------|
| Scroll | 0x00000001 | UV scrolling animation |
| Rotate | 0x00000002 | UV rotation animation |
| Wave | 0x00000004 | Wave distortion |
| Scale | 0x00000008 | UV scaling animation |
| Sequence | 0x00000010 | Sprite sheet animation |

### Animation Parameters

| Property | Description |
|----------|-------------|
| `scrollDir` | Scroll direction `"U V"` |
| `scrollSpeed` | Scroll speed multiplier |
| `rotSpeed` | Rotation speed |
| `rotPivotOffset` | Rotation pivot `"U V"` |
| `waveType` | "Sin", "Square", "Triangle" |
| `waveFreq` | Wave frequency |
| `waveAmp` | Wave amplitude |
| `sequenceFramePerSec` | Sprite animation FPS |
| `sequenceSegmentSize` | Frames in sprite sheet |

---

## Reflection System

### Reflection Modes

| Mode | Properties | Description |
|------|------------|-------------|
| None | - | No reflections |
| Level | `dynamicCubemap = "1"` | Use level's environment map |
| Custom | `cubemap = "CubemapName"` | Use specific cubemap |

### Cubemap Reference

```json
{
  "cubemap": "MyCubemap",
  "dynamicCubemap": "0"
}
```

---

## Complete Material Example

### Version 1.5 PBR Material

```json
{
  "building_concrete": {
    "class": "Material",
    "mapTo": "concrete_wall",
    "version": 1.5,
    "activeLayers": 1,
    
    "diffuseMap[0]": "/art/shapes/buildings/concrete_d.color.png",
    "diffuseColor[0]": "1 1 1 1",
    "diffuseMapUseUV[0]": "0",
    
    "normalMap[0]": "/art/shapes/buildings/concrete_n.normal.png",
    "normalMapStrength[0]": "1",
    "normalMapUseUV[0]": "0",
    
    "metallicMap[0]": "/art/shapes/buildings/concrete_m.data.png",
    "metallicFactor[0]": "0",
    
    "roughnessMap[0]": "/art/shapes/buildings/concrete_r.data.png",
    "roughnessFactor[0]": "0.8",
    
    "ambientOcclusionMap[0]": "/art/shapes/buildings/concrete_ao.data.png",
    
    "translucent": "0",
    "translucentBlendOp": "None",
    "doubleSided": "0",
    "castShadows": "1",
    
    "groundType": "ASPHALT",
    "materialTag0": "beamng",
    "materialTag1": "building"
  }
}
```

### Transparent Material with Instance Color

```json
{
  "glass_tinted": {
    "class": "Material",
    "mapTo": "window_glass",
    "version": 1.5,
    
    "diffuseMap[0]": "/art/shapes/glass_d.color.png",
    "diffuseColor[0]": "0.2 0.2 0.2 0.6",
    "instanceDiffuse[0]": "1",
    
    "metallicFactor[0]": "0.1",
    "roughnessFactor[0]": "0.05",
    
    "translucent": "1",
    "translucentBlendOp": "LerpAlpha",
    "translucentZWrite": "1",
    
    "doubleSided": "1",
    "castShadows": "0",
    
    "dynamicCubemap": "1"
  }
}
```

### Foliage with Alpha Cutout

```json
{
  "tree_leaves": {
    "class": "Material",
    "mapTo": "leaves",
    "version": 1.5,
    
    "diffuseMap[0]": "/art/shapes/trees/leaves_d.color.png",
    "normalMap[0]": "/art/shapes/trees/leaves_n.normal.png",
    
    "alphaTest": "1",
    "alphaRef": "127",
    
    "doubleSided": "1",
    "castShadows": "1"
  }
}
```

---

## Implementation Notes for 3D Viewers

### Shader Requirements

1. **Version Detection**: Check `version` field to determine shader pipeline
2. **Color Space**: 
   - v1.0: All textures in sRGB
   - v1.5: baseColor in sRGB, others in linear
3. **Instance Colors**: Implement per-object color multiplication
4. **Alpha Handling**: Support both alpha blend and alpha test modes

### Recommended Rendering Order

1. Opaque objects (front-to-back)
2. Alpha-tested objects
3. Translucent objects (back-to-front)

### Instance Color Implementation

```glsl
// Fragment shader pseudocode
vec4 baseColor = texture(diffuseMap, uv);
if (instanceDiffuse) {
    baseColor *= instanceColor;
}

float alpha = baseColor.a;
if (instanceOpacity) {
    alpha *= instanceColor.a;
}
```

### PBR Workflow

BeamNG v1.5 uses a metallic-roughness PBR workflow compatible with glTF 2.0:

```
finalColor = lerp(dielectricF0, baseColor, metallic)
roughness = roughnessFactor * roughnessTexture
```

---

## File Discovery

To load materials for a DAE file:

1. Check for `main.materials.json` in the same directory
2. Check for `<filename>.materials.json` alongside the DAE
3. Search parent directories for material files
4. Materials are matched to geometry by `mapTo` property

---

## Texture Format Recommendations

| Format | Extension | Use Case |
|--------|-----------|----------|
| BC7 sRGB | .color.dds | Base color, emissive |
| BC7 Linear | .data.dds | Color data (linear) |
| BC5/3Dc | .normal.dds | Normal maps |
| BC4 | .data.dds | Grayscale (metallic, roughness, AO) |
| DXT1 | .dds | Legacy compressed |
| PNG | .png | Source files (texture cooker input) |

---

*Documentation generated from BeamNG.drive Lua source analysis*
*Last updated: January 2026*
