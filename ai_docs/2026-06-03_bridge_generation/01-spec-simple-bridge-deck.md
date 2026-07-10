# Spec — Simple Bridge Deck (v1: flat pane)

**Date:** 2026-06-03
**Depends on:** `00-findings-and-decisions.md`
**Scope:** v1 only. One flat ribbon surface per bridge, exported as `.dae`, placed as `TSStatic`.
Out of scope for v1: railings, piers/supports, abutments, deck thickness/sides, tunnels, custom
materials, multi-span.

---

## 1. What we produce

For every spline where `IsBridge == true` **and** `parameters.ExcludeBridgesFromTerrain == true`
("generate bridge" mode):

1. A deck **mesh**: a ribbon of quads following the spline centerline at the solved deck elevation,
   spanning the road width, normals up. (This is the road-deck ribbon `RoadMeshBuilder` already makes.)
2. A **`.dae`** at `art/shapes/MT_bridges/bridge_{splineId}.dae` using BeamNG's scene-tree hierarchy:
  `base00/start01`, visible deck LOD node, `Colmesh-1` collision mesh, and `collision-1` marker.
  One file per bridge — **no chunking** (D5).
3. A **`TSStatic`** entry in `main/MissionGroup/MT_bridges/items.level.json` (NDJSON), at world position
   `(0,0,0)` (the mesh carries world coordinates, matching the road/building exporters), with
   `shapeName = /levels/{level}/art/shapes/MT_bridges/bridge_{splineId}.dae`.
4. The parent **`SimGroup` "MT_bridges"** ensured in `main/MissionGroup/items.level.json`.
5. A **placeholder material** registered in the level so the deck renders (D3).

Terrain under the bridge stays unmodified and unpainted (already the case — §2 of findings).

---

## 2. Data model & geometry

### Source: virtual cross-sections (D2)
Each bridge spline's `UnifiedCrossSection`s already carry, post-solve:
- `CenterPoint : Vector2` — centerline XY (terrain space)
- `TargetElevation : float` — solved deck centerline Z (continuous with approach roads)
- `TangentDirection`, `NormalDirection : Vector2`
- `EffectiveRoadWidth : float`
- `BankAngleRadians`, `LeftEdgeElevation`, `RightEdgeElevation` — **populated in production** (banking
  runs on bridges; only roundabouts are exempt). The spike's flat/NaN values were a test-harness artifact.
  The deck reads them when set and falls back to flat (`center ± width/2`) only for straight bridges.

### Mapping to `RoadCrossSection` (the mesh builder input)
| `RoadCrossSection` field | Source |
|---|---|
| `CenterPoint` | `cs.CenterPoint` |
| `CenterElevation` | `cs.TargetElevation` |
| `TangentDirection` | `cs.TangentDirection` |
| `NormalDirection` | `cs.NormalDirection` |
| `WidthMeters` | `cs.EffectiveRoadWidth` |
| `BankAngleRadians` | `cs.BankAngleRadians` (0 ⇒ flat pane) |
| `DistanceAlongRoad` | cumulative distance (for UV tiling) |
| `LeftEdgeElevation` / `RightEdgeElevation` | from `cs` if set, else null (builder derives from bank) |

World-coordinate conversion (terrain-space → BeamNG world, applying `metersPerPixel` /
`terrainBaseHeight`) reuses the **same transform** as `RoadNetworkDaeExporter` so the deck aligns with
the terrain and the approach roads.

### The one required change to existing code
`CrossSectionConverter` drops excluded cross-sections (`if (cs.IsExcluded) continue;` at
`CrossSectionConverter.cs:102,140`). The bridge path must **include** them. Approach (decided in plan):
add an opt-in (e.g. `includeExcludedForSplineIds` set, or a dedicated
`ConvertSplineToWorldCoordinates(spline, includeExcluded:true)`) rather than globally flipping the skip
— the DecalRoad/road-mesh callers must keep dropping excluded sections.

---

## 3. Elevation continuity (the "must not regress" invariant)

- The deck's first/last cross-section Z **must equal** the adjacent approach-road Z at the shared node.
  This already holds via chain smoothing (tested). v1 inherits it for free by consuming `TargetElevation`.
- **Fallback (defensive):** if a bridge spline's cross-sections have `NaN`/unset `TargetElevation`
  (unchained bridge — chain fragmentation is a known fragility, see memory `continuation_seam_ditch`),
  fall back to `spline.ElevationProfile` (entry/exit + curve) and, failing that, sampled terrain +
  fixed clearance. **Emit a diagnostic** when the fallback fires — silent fallback hides chain bugs.

---

## 4. Output details

### DAE
- Writer: `ColladaExporter.Export(new BeamNgDaeScene { ... }, path)`.
- Visible deck mesh is written as a LOD node under `start01`.
- A separate material-less `Colmesh-1` is written under `start01`; v1 uses the whole generated deck mesh as
  the colmesh so DecalRoads with `overObjects=true` can project onto the deck.
- `collision-1` is written as the BeamNG marker node under `base00`.
- Visible mesh material name = placeholder material (so the `<material>` reference resolves).
- Filename: `bridge_{splineId}.dae`. (Stable, unique per spline; revisit if splineIds aren't stable
  across runs — see open Q.)

### TSStatic (mirror `BuildingSceneWriter.CreateTSStaticEntry`)
```
class           = "TSStatic"
name            = "bridge_{splineId}"
__parent        = "MT_bridges"
position        = [0,0,0]
rotationMatrix  = [1,0,0, 0,1,0, 0,0,1]
shapeName       = "/levels/{level}/art/shapes/MT_bridges/bridge_{splineId}.dae"
useInstanceRenderData = true
persistentId    = <new GUID>
```
`isRenderEnabled` — buildings set `false`; for a visible bridge deck we want it **rendered** (verify
the building default and override as needed).

### SimGroup
`{"class":"SimGroup","name":"MT_bridges","persistentId":"<guid>","__parent":"MissionGroup"}` written to
`main/MissionGroup/items.level.json` if absent (reuse `EnsureSimGroupInParent`).

### Material (placeholder, D3)
Register one simple material (e.g. `bridge_deck_placeholder`) via the building material-writing path
(`BuildingSceneWriter.WriteMaterials` equivalent), with a flat base color or a reused road texture.
Fine-tuning deferred.

---

## 5. Pipeline integration

Hook after the terrain is written, mirroring the `ExportRoadMeshDae` block (`TerrainCreator.cs:299-303`):

```
if (anyBridgeSplines && generateBridgesEnabled && unifiedResult?.Network != null)
    await ExportBridgeDecksAsync(unifiedResult.Network, outputPath, parameters, ...);
```

`generateBridgesEnabled` = `parameters.ExcludeBridgesFromTerrain` for now (D1; rename later). Objects in
scope at that call site: `network`, level output path, `metersPerPixel`, terrain size, base height.

---

## 6. Acceptance criteria (v1)

1. A map with at least one `bridge=yes` way, generated with bridge generation on, produces:
   - `art/shapes/MT_bridges/bridge_*.dae` (one per bridge), and
   - `MT_bridges` SimGroup + one `TSStatic` per bridge in `main/MissionGroup/MT_bridges/`.
2. Loaded in BeamNG, each deck is visible, follows the road line, and its ends **meet the approach
   roads** with no visible vertical step (inherits the < 1m chain continuity).
3. Terrain under the bridge is **not** terraformed and **not** painted (unchanged behavior).
4. No regression: with bridge generation **off**, output is byte-identical to today.
5. Unchained/edge-case bridges either render via fallback or are skipped **with a logged warning** —
   never a crash, never a silent NaN deck.

---

## 6b. Scope additions (2026-06-03 — re-thinking the bridge exclusion)

After confirming the exclusion only really suppresses terrain stamping + material painting (see findings
§4 D8–D10), v1 scope grows by two items, tracked as plan Steps 7–8:

- **Lane markings on the deck (D8):** generate DecalRoads for bridge spans, draped at deck elevation, each
  with `OverObjects = true` so they render on the deck `.dae` and not the terrain beneath. This also requires
  the bridge `.dae` to provide a BeamNG `Colmesh-1`; otherwise `overObjects` has no collision surface to
  project onto. (Re-enables the `DecalRoadGenerator.cs:41` bridge skip.)
- **Junction harmonization at bridge ends (D9):** let the bridge↔approach endpoint junctions harmonize,
  guarded so the deck end is not pinned toward terrain.

The flat-deck core (Steps 1–5) is unchanged; banking superelevation now comes for free (D6 corrected).

## 7. Naming note (deferred, not v1)

`IsExcluded` and `ExcludeBridgesFromTerrain` are misnomers (they mean "don't terraform/paint, but still
solve"). Rename to a "generate bridges" / "don't stamp terrain" vocabulary in a later pass once the
feature is proven, to avoid churn now.
