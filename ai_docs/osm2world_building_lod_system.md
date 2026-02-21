# Building LOD (Level of Detail) System

This document explains how the Level of Detail system works for buildings in OSM2World.

---

## Overview

The LOD system controls the trade-off between visual quality and performance when generating building geometry. It operates in **two layers**:

1. **Generation time** — building components tag their meshes with `LODRange`s so that multiple geometry variants coexist (e.g. simple walls at low LOD, detailed windows at high LOD).
2. **Output time** — the output pipeline filters meshes by the target LOD and adjusts tessellation quality accordingly.

---

## LOD Levels

Defined in `scene/mesh/LevelOfDetail.java` as an enum:

| Level | Description |
|-------|-------------|
| **LOD0** | Lowest quality, fastest rendering. Minimal geometry, no windows, no interiors. |
| **LOD1** | Low quality. Flat window textures for explicitly-tagged windows only. |
| **LOD2** | Medium quality. Flat window textures for all buildings. |
| **LOD3** | High quality. Full 3D window geometry, door insets, building interiors rendered. |
| **LOD4** | Highest quality. Full 3D windows everywhere, finest tessellation, transparent window panes. |

The active LOD is set via the `lod` configuration property (integer 0–4, default **4**) in `O2WConfig.lod()`.

---

## LODRange

Defined in `scene/mesh/LODRange.java` as a Java `record`:

```java
public record LODRange(LevelOfDetail min, LevelOfDetail max)
```

Each `Mesh` carries a `lodRange` field that declares the range of LOD levels at which it should be visible. Key operations:

- `contains(lod)` — returns true if a given LOD level falls within the range.
- `intersection(ranges...)` — computes the overlap of multiple ranges; returns `null` if empty.

Meshes created without an explicit LOD range default to `LOD0..LOD4` (visible at all levels).

---

## How Buildings Tag Meshes with LOD Ranges

### Entry Point: Building → BuildingPart

`Building` extends `CachingProceduralWorldObject` and delegates rendering to its `BuildingPart` instances. Each `BuildingPart.buildMeshesAndModels(target)` renders:

| Component | LOD Behavior |
|-----------|-------------|
| **Exterior walls** | Multiple geometry variants generated at different LOD ranges (see below) |
| **Roof** | No explicit LOD range — rendered at **all LODs** (LOD0–LOD4) |
| **Building bottom (floor slab)** | No explicit LOD range — rendered at **all LODs** |
| **Interior** (rooms, indoor walls) | Wrapped in `target.setCurrentLodRange(LOD3, LOD4)` — **only at LOD3+** |

The constant `BuildingPart.INDOOR_MIN_LOD = LOD3` gates all interior content.

### The Target Mechanism

`ProceduralWorldObject.Target` (in `world/data/ProceduralWorldObject.java`) is the key mechanism for LOD-tagging meshes:

```java
target.setCurrentLodRange(LOD3, LOD4);   // all following drawMesh calls get LOD3-LOD4
target.drawTriangles(...);                // this mesh is tagged LOD3-LOD4
target.setCurrentLodRange(null);          // reset: meshes return to their intrinsic LOD range
```

When `setCurrentLodRange` is active, every `drawMesh` call overrides the mesh's LOD range with the target's current range.

---

## Exterior Walls and Window LOD Tiers

The most sophisticated LOD usage is in `ExteriorBuildingWall.java`. The method `chooseWindowImplementations()` returns a map of `WindowImplementation → LODRange`, producing **multiple wall geometry variants** at different detail levels.

### WindowImplementation Enum

Defined in `world/modules/building/WindowImplementation.java`:

| Value | Description |
|-------|-------------|
| `NONE` | Plain wall surface with no windows |
| `FLAT_TEXTURES` | A repeating texture image applied to a flat wall |
| `INSET_TEXTURES` | Textured windows with slight geometric inset (config-only) |
| `FULL_GEOMETRY` | Full 3D window geometry with frames, sills, and glass panes |

### Default LOD Mapping

**Explicit windows** (OSM data has `window=*` tags):

| LOD | WindowImplementation |
|-----|---------------------|
| LOD0 | `NONE` |
| LOD1–LOD2 | `FLAT_TEXTURES` |
| LOD3–LOD4 | `FULL_GEOMETRY` |

**Implicit windows** (no explicit OSM tags, auto-detected):

| LOD | WindowImplementation |
|-----|---------------------|
| LOD0–LOD1 | `NONE` |
| LOD2–LOD3 | `FLAT_TEXTURES` |
| LOD4 | `FULL_GEOMETRY` |

### Config Overrides

The properties `explicitWindowImplementation` and `implicitWindowImplementation` can force a single implementation across all LOD levels (e.g. always use `FLAT_TEXTURES`).

### Rendering Loop

The wall's `renderTo()` method iterates over all window implementations, setting the LOD range per variant:

```java
for (WindowImplementation windowImplementation : windowImplementations.keySet()) {
    LODRange lodRange = windowImplementations.get(windowImplementation);
    if (config.containsKey("lod") && !lodRange.contains(config.lod())) continue; // skip unneeded
    target.setCurrentLodRange(lodRange);
    // ... render wall surfaces with this window implementation ...
}
target.setCurrentLodRange(null);
```

When the config specifies a target LOD, variants outside that LOD are skipped entirely — an early optimization that avoids generating unused geometry.

---

## Door Insets

Door geometry is simplified at lower LOD levels. In `ExteriorBuildingWall.java`:

```java
if (lodRange.max().ordinal() < 3 || config.lod().ordinal() < 3) {
    params = params.withInset(0.0); // flat door, no recessed geometry
}
```

At LOD3+, doors have inset (recessed) geometry. Below LOD3, doors are rendered flush with the wall.

---

## Transparent vs. Opaque Window Panes

`GeometryWindow.java` creates **two versions** of each window pane at different LOD ranges:

| LOD Range | Material | Purpose |
|-----------|----------|---------|
| LOD3–LOD4 | Transparent glass | Allows seeing through to building interior |
| LOD0–LOD2 | Opaque glass | Performance optimization (no interior visibility needed) |

This is accomplished using `LODRange.intersection()` to safely narrow the range:

```java
// Transparent version (only where interior is visible)
var lodRange = LODRange.intersection(previousLodRange, new LODRange(INDOOR_MIN_LOD, LOD4));
t.setCurrentLodRange(lodRange);
target.drawTriangles(params.transparentWindowMaterial(), ...);

// Opaque fallback (lower LODs)
t.setCurrentLodRange(LOD0, INDOOR_MIN_LOD);
target.drawTriangles(params.opaqueWindowMaterial(), ...);
```

---

## Caching and LOD Invalidation

`CachingProceduralWorldObject` (in `world/data/CachingProceduralWorldObject.java`) caches the generated meshes for each building. The cache is **invalidated when the configured LOD changes**:

```java
private void fillTargetIfNecessary() {
    if (target == null || (lod != null && getConfiguredLod() != null && lod != getConfiguredLod())) {
        lod = getConfiguredLod();
        target = new ProceduralWorldObject.Target();
        buildMeshesAndModels(target);
    }
}
```

`Building` overrides `getConfiguredLod()` to return `config.lod()`, tying the cache lifetime to the LOD setting. When the LOD changes, all building meshes — walls, roof, floor, interior — are regenerated from scratch.

---

## Output Pipeline LOD Filtering

After meshes are generated (potentially containing variants for multiple LOD levels), the output pipeline selects only the meshes relevant to the target LOD.

### FilterLod (MeshStore.java)

The `FilterLod` processing step removes all meshes whose `lodRange` does not contain the target LOD:

```java
public static class FilterLod implements MeshProcessingStep {
    @Override
    public MeshStore apply(MeshStore meshStore) {
        return new MeshStore(meshStore.meshesWithMetadata().stream()
                .filter(m -> m.mesh().lodRange.contains(targetLod))
                .toList());
    }
}
```

### ConvertToTriangles — LOD-Dependent Tessellation

Curved surfaces are tessellated with LOD-dependent precision:

| LOD | Max Error (meters) |
|-----|--------------------|
| LOD0 | 4.0 |
| LOD1 | 1.0 |
| LOD2 | 0.20 |
| LOD3 | 0.05 |
| LOD4 | 0.01 |

Lower LOD produces coarser triangulations, fewer triangles, and better performance.

### EmulateTextureLayers

At LOD0–LOD1, texture layers are limited to 1. This simplifies materials and reduces draw calls.

### MergeMeshes — LOD-Aware Merging

When merging meshes for draw-call optimization, meshes with **different LOD ranges are never merged** together. This preserves the ability to filter by LOD at runtime.

### GLTF Output Pipeline

`GltfOutput.java` assembles the full processing chain:

```java
List<MeshProcessingStep> processingSteps = asList(
    new FilterLod(lod),
    new ConvertToTriangles(lod),
    new EmulateTextureLayers(lod.ordinal() <= 1 ? 1 : Integer.MAX_VALUE),
    new MoveColorsToVertices(),
    new ReplaceTexturesWithAtlas(...),
    new MergeMeshes(mergeOptions)
);
```

### DrawBasedOutput (Legacy Path)

For real-time rendering targets, each mesh is checked inline:

```java
default void drawMesh(Mesh mesh) {
    if (mesh.lodRange.contains(getLod())) {
        // tessellate and draw
    }
}
```

---

## Complete LOD Summary for Buildings

```
          LOD0        LOD1        LOD2        LOD3        LOD4
          ──────────────────────────────────────────────────────
Walls:    plain wall │ flat tex*  │ flat tex   │ full 3D windows
          no windows │ (explicit) │ (all)      │ (geometry)
                     │            │            │
Doors:    flush      │ flush      │ flush      │ inset geometry
                     │            │            │
Roof:     ─────────── always rendered, same geometry ──────────
                     │            │            │
Floor:    ─────────── always rendered, same geometry ──────────
                     │            │            │
Interior: ─── not rendered ──────────────────── │ rendered
                     │            │            │
Windows:  opaque     │ opaque     │ opaque     │ transparent
(glass)   pane       │ pane       │ pane       │ pane
                     │            │            │
Tessell.: error=4.0  │ error=1.0  │ error=0.20 │ 0.05 │ 0.01
                     │            │            │
Textures: max 1 layer│ max 1 layer│ unlimited  │ unlimited
```

*Flat textures at LOD1 apply only to buildings with explicit window tags in OSM data.

---

## Key Source Files

| File | Role |
|------|------|
| `scene/mesh/LevelOfDetail.java` | LOD enum (LOD0–LOD4) |
| `scene/mesh/LODRange.java` | Inclusive LOD range record |
| `scene/mesh/Mesh.java` | Mesh class with `lodRange` field |
| `scene/mesh/MeshStore.java` | `FilterLod`, `ConvertToTriangles`, `MergeMeshes` processing steps |
| `conversion/O2WConfig.java` | `lod()` configuration property |
| `world/data/ProceduralWorldObject.java` | `Target` inner class with `setCurrentLodRange()` |
| `world/data/CachingProceduralWorldObject.java` | LOD-aware mesh caching |
| `world/modules/building/Building.java` | Building world object, overrides `getConfiguredLod()` |
| `world/modules/building/BuildingPart.java` | Delegates to walls/roof/floor/interior with LOD gating |
| `world/modules/building/ExteriorBuildingWall.java` | Window implementation LOD mapping |
| `world/modules/building/WindowImplementation.java` | Window rendering strategy enum |
| `world/modules/building/GeometryWindow.java` | Transparent/opaque window pane LOD split |
| `output/gltf/GltfOutput.java` | GLTF output pipeline with LOD filtering |
| `output/common/DrawBasedOutput.java` | Legacy real-time LOD filtering |
