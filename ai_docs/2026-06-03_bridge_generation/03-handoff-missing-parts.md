# Handoff — Missing Parts for Bridge Deck Plan

**Date:** 2026-06-06  
**Branch:** `feature/bridges` (verify locally before continuing)  
**Source plan:** `ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md`

## 1. What is complete vs missing

### Complete

- Step 0: verification spike
- Step 1: excluded cross-section world conversion path
- Step 2: one bridge deck `.dae` per bridge spline
- Step 3: `MT_bridges` SimGroup + one `TSStatic` per deck
- Step 4: placeholder material write
- Step 5: pipeline hook in `TerrainCreator`
- Step 7: bridge endpoint inclusion guard in `NetworkJunctionHarmonizer`
- Step 8: bridge DecalRoad overlays generated on deck elevation with `OverObjects = true` (code/tests)

### Still missing (this handoff)

- **Step 6 finalization**: manual quality pass + documented outcome updates
- **Step 8 manual validation**: confirm bridge lane/edge markings render on deck in BeamNG

## 2. Current evidence relevant to missing parts

1. Bridge decks are generated and present in-game.
2. Endpoint Z reconciliation is active during bridge export and logged (endpoint correction count/max).
3. `DecalRoadGenerator` no longer skips generated bridge splines when `ExcludeBridgesFromTerrain` is true;
   generated tunnels remain skipped.
4. Generated bridge DecalRoads force `OverObjects = true` and use cross-section/deck elevation rather than
   terrain heightmap elevation.
5. Bridge deck `.dae` files now use the BeamNG `base00/start01/Colmesh-1/collision-1` hierarchy. The current
   v1 colmesh is the generated deck ribbon, material-less, so `overObjects` has a collision surface to use.
6. `GeneratedDecalRoad.OverObjects` already exists and is serialized by `DecalRoadSceneWriter` as
   `overObjects`, so Step 8 did **not** need a new scene-file property. The generator now forces the existing
   flag for generated bridge decals.
7. `RoadCorridorBuilder` also skips generated bridges. Keep that behavior unless intentionally redesigning
   terrain/corridor overlap handling; Step 8 is about visual DecalRoad overlays on bridge decks, not terrain
   stamping under bridges.

## 3. Step 6 — finalize validation and close plan status

### Work to do

1. Run manual BeamNG validation on at least one map with curved and straight bridges.
2. Verify and record:
   - deck aligned with road centerline,
   - no vertical seam step at both bridge ends,
   - correct width match at seams,
   - banking visual continuity on curved bridges,
   - terrain remains untouched beneath deck,
   - no major top-down seam kink (document if present).
3. Update `02-implementation-plan.md`:
   - move Step 6 from `PARTIAL` to `DONE` only if all checks pass,
   - otherwise keep partial and link blocker notes.

### Suggested log/screenshot artifacts

- capture screenshot per bridge seam (entry/exit),
- copy `Bridge deck export: ... endpoint correction(s), max correction ...` log line.

## 4. Step 8 — bridge DecalRoads on top of the deck

### Goal

Generate lane markings/edge lines for bridge spans and force rendering over deck meshes (not terrain below).

### Intended behavior

Generated bridges are the special case where the road surface is a mesh instead of stamped terrain:

- `spline.IsBridge && spline.Parameters.ExcludeBridgesFromTerrain == true`
   - generate bridge deck mesh,
   - keep terrain painting/corridor terrain footprint excluded,
   - still generate DecalRoad visual layers,
   - force every generated bridge DecalRoad to `OverObjects = true`.
- `spline.IsBridge && ExcludeBridgesFromTerrain == false`
   - existing behavior: bridge behaves like a regular terrain road,
   - do not force `OverObjects` unless the layer itself requests it.
- `spline.IsTunnel && ExcludeTunnelsFromTerrain == true`
   - keep current skip behavior for now.
- non-bridge roads
   - preserve existing layer-driven `OverObjects` behavior.

### Implemented code changes

1. **Re-enabled bridge spline DecalRoad generation** in:
   - `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`
   - adjusted the structure skip so generated bridge splines are not skipped, while generated tunnels still are.
   - kept `RoadCorridorBuilder` and `MaterialPainter` exclusions intact; they protect terrain under the deck.
2. **Bridge DecalRoads use deck elevation source**:
    - consume the same unified cross-section/elevation data used for deck export,
    - generated bridge decals must include excluded cross-sections instead of dropping them,
    - do not fall back to terrain-projected node heights for bridge spans unless the bridge has no solved
       elevation and would be skipped anyway.
3. **Forced `OverObjects = true` on bridge-generated roads**:
   - set `OverObjects = layer.OverObjects || isGeneratedBridge` when creating `GeneratedDecalRoad`,
   - post-processing and chunking continue to copy `OverObjects` from the original road,
   - existing layer behavior for non-bridge roads is preserved.
4. **Added deck collision support for `OverObjects`**:
   - bridge DAEs use the same BeamNG DAE scene-tree convention as buildings,
   - visible deck is a separate LOD node,
   - `Colmesh-1` is a material-less copy of the generated deck ribbon,
   - `collision-1` marker is present under `base00`.
5. **Tunnel behavior remains unchanged** for now.

### Concrete code map

- `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`
   - top-level skip now only keeps generated tunnels out; generated bridges continue through layer resolution.
   - `GenerateForLayerRange(...)` creates `GeneratedDecalRoad` and currently sets `OverObjects = layer.OverObjects`.
      Generated bridges now pass an `isGeneratedBridge` signal from `Generate(...)` through `GenerateForSpline(...)`.
   - chunking already preserves `OverObjects = road.OverObjects`; keep that.
- `BeamNgTerrainPoc/Terrain/Models/DecalRoad/GeneratedDecalRoad.cs`
   - already has `public bool OverObjects { get; init; }`.
- `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadSceneWriter.cs`
   - already writes `dict["overObjects"] = dr.OverObjects`.
- `BeamNgTerrainPoc/Terrain/Services/DecalRoad/RoadCorridorBuilder.cs`
   - still skips generated bridge splines. That is acceptable for this step because terrain corridors/painting
      are intentionally excluded beneath bridge decks.
- `BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs`
   - now writes `BeamNgDaeScene` instead of a flat `ExportZUp` DAE,
   - includes `base00/start01/Colmesh-1/collision-1` so BeamNG can project decals onto the deck object.

### Implemented shape

1. Added a helper near the generator skip logic:

    ```csharp
    private static bool IsGeneratedBridge(ParameterizedRoadSpline spline) =>
          spline.IsBridge && spline.Parameters.ExcludeBridgesFromTerrain;
    ```

2. Replaced the existing skip with bridge/tunnel-specific intent:

    ```csharp
    var isGeneratedBridge = IsGeneratedBridge(spline);
    if (spline.IsTunnel && spline.Parameters.ExcludeTunnelsFromTerrain)
          continue;
    ```

   In other words, remove the bridge half of the existing generated-structure skip from
   `DecalRoadGenerator`. Generated bridges should continue through layer resolution and node generation.

3. Passed `isGeneratedBridge` down into the road creation path and set:

    ```csharp
    OverObjects = layer.OverObjects || isGeneratedBridge,
    ```

4. Bridge cross-section sampling keeps excluded sections via `network.GetCrossSectionsForSpline(...)`, which returns
   the stored sections directly.

5. Focused tests showed generated bridge decals survive overlap post-processing without additional changes.

### Test update notes

Existing corridor tests in `BeamNgTerrainPoc.Tests/DecalRoad/BridgeDecalRoadFilterTests.cs` remain as-is because
bridge terrain corridors should still be excluded. New generator tests cover the Step 8 overlay behavior.

### Required tests

Added tests under `BeamNgTerrainPoc.Tests/DecalRoad/`:

1. bridge spline in generate-bridge mode yields DecalRoad output (not skipped),
2. generated bridge DecalRoad has `OverObjects = true`,
3. bridge DecalRoad node elevations follow deck/source bridge elevations (not terrain),
4. non-bridge roads retain existing `OverObjects` behavior.

Added tests under `BeamNgTerrainPoc.Tests/Export/`:

1. bridge deck DAE contains `base00`, `start01`, `Colmesh-1`, `collision-1`, and visible LOD node,
2. `Colmesh-1` triangles are material-less so the generated deck visual mesh is not reused as a materialized
   collision/visual node.

Suggested assertions:

- Create a bridge spline with `isBridge: true`, `excludeBridges: true`, and cross-sections whose
   `TargetElevation` is clearly different from the heightmap. For example, cross-sections at `125f` over a
   heightmap at `10f`.
- Call `DecalRoadGenerator.Generate(...)` with a minimal enabled layer set.
- Assert output count is greater than zero.
- Assert all roads for that spline have `OverObjects == true`.
- Assert every node Z matches the transformed cross-section elevation plus terrain base height, not the
   terrain heightmap elevation.
- Add a regular non-bridge road with a layer where `OverObjects = false`; assert it remains false.
- Add a regular non-bridge road with a layer where `OverObjects = true`; assert it remains true.

### Manual validation notes

Tests passed: focused DecalRoad suite, 49 tests, 2026-06-06.

Next manual validation should use a generated map with at least one straight bridge and one curved bridge:

1. Inspect `main/MissionGroup/DecalRoads/items.level.json` and verify bridge road entries have
    `"overObjects": true`.
2. In BeamNG, confirm the lane/edge markings render on the deck surface and not on the valley terrain.
3. Confirm the terrain beneath the deck is still untouched by road material painting.
4. Record any remaining seam kink under the follow-up doc, not as a Step 8 blocker.

## 5. Known follow-up after missing parts

A separate continuity issue remains in top-down seam angle alignment (plan-view tangent mismatch). See:

- `ai_docs/2026-06-06_bridge_road_continuity_followup.md`

Do **not** block Step 8 on this. Complete Step 8 first, then implement that follow-up as a new phase.

## 6. Definition of done for this handoff

All items below must be true:

1. Step 8 is implemented in production code and covered by tests.
2. Step 6 status in `02-implementation-plan.md` reflects real validation state.
3. Bridge lanes/markings visibly render on deck surfaces in BeamNG (not on terrain below).
4. No regression in existing road/tunnel DecalRoad behavior.

## 7. Suggested quick command checklist

```powershell
# run targeted tests
 dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter FullyQualifiedName~DecalRoad

# run all tests
 dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj

# inspect bridge export + decal logs in latest run
 Select-String -Path "C:\Users\aklei\AppData\Local\BeamNG\BeamNG.drive\current\levels\*\MT_TerrainGeneration\logs\*.txt" -Pattern "Bridge deck export|DecalRoad|OverObjects"
```
