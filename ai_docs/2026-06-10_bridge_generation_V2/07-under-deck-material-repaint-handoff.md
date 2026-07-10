# 07 — HANDOFF: repaint terrain material under bridge decks (billboard vegetation poke-through)

**Date:** 2026-06-12. **Branch:** `feature/bridge_merged_corridor` @ `6ea6cf1` (644 tests).
**Status:** handoff prompt — NOT designed in detail, NOT implemented. Next session starts here.

## 1. Context (where the previous topic ended)

Doc 06 v2 made the terrain↔deck transition gap-free and smooth: the abutment overlap zone
([Start+overlap, End−overlap] exclusion shrink in `MarkStructureExclusions`) is ordinary smoothed +
painted road tucked `AbutmentOverlapDropMeters` (user runs 0.01–0.03) under the deck; the excavator
shaves anything poking above `deckZ − undercut` under the rest of the span. **Consequence: terrain now
sits deliberately TIGHT under the deck** (overlap tongue ≈ 1–3 cm below the deck top; excavated cells
just below the soffit line).

## 2. The new problem (user report)

Where the terrain material under the deck carries **billboard / groundcover vegetation** (grass layers),
the billboards grow THROUGH the deck mesh wherever terrain and deck are close — exactly the overlap
tongues and shaved cells doc 06 created. Vegetation placement keys off the terrain MATERIAL per cell, so
the fix is material-level, not geometry-level.

## 3. The user's design (ratified direction)

**Repaint the terrain material under each bridge deck footprint with a vegetation-free material.**

1. **Where to paint:** the cells the bridge machinery already touches — the span footprint
   (`StructureSpanId` sections incl. the overlap zone, road width + a small margin, same lateral march
   as `BridgeDeckExcavator` / `BridgeAbutmentOverlapStamper`). Suggest: paint wherever
   `terrainZ ≥ deckZ − (clearance threshold ~0.5–1 m)` so deep ravines/water under tall spans keep their
   natural material — only the "tight" cells change. (Confirm threshold with user; maybe paint the whole
   footprint — simpler and invisible under the deck.)
2. **UI: a dropdown** in the Bridge Rule System V2 block to select WHICH terrain material to use.
   Options = the level's terrain material list (the same set the generation pipeline paints roads with —
   `parameters.Materials` / the terrain materials going into `theTerrain.terrain.json`).
3. **Default selection rule (user-specified, exact):** prefer "dirt", else "asphalt", whatever is
   available — matched with a case-insensitive **CONTAINS** query against the material names, and when
   several names match, take the **SHORTEST matching name** (e.g. "dirt" beats "dirt_loose_rocky").
   If neither matches: first material / leave unpainted + warn (ask user which fallback).
4. Persist the selection in the preset (the `bridgeRules` node round-trips whole-object — adding a
   string property `UnderDeckMaterialName` to `BridgeRuleSystemOptions` gets persistence for free) and
   default the dropdown via the contains-query when the preset carries nothing.

## 4. Implementation pointers (to verify first — NOT yet code-checked)

- **Find the material map:** the binary terrain holds height + material byte per cell
  (`Grille.BeamNG.Lib.IO.Binary.TerrainSerializer`, `terrain.Data[x,y].Material` = index into the
  terrain.json material list). Locate where TerrainCreator assembles the material indices (road material
  painting pass — CLAUDE.md "Material painting along roads"; grep for the material-layer/index array
  built alongside `heightMap2D`) and hook AFTER all road painting, near the doc-06 slot
  (overlap stamp → excavate → **repaint**), so the bridge repaint wins under the deck.
- **Footprint source:** reuse the excavator's deck-group walk (sections with `StructureSpanId >= 0`,
  lateral march over halfWidth + margin). Gate: `ExcludeBridgesFromTerrain` + merged spans present
  (repaint is harmless but pointless without decks); NOT sparse-gated — vegetation poke-through affects
  any generated deck.
- **Material name → index:** resolve the selected name against the terrain material list order used in
  `theTerrain.terrain.json` (PathResolver/material list in the terrain writer). The contains-query
  default belongs in ONE shared helper used by both the UI default and a headless fallback.
- **UI:** `GenerateTerrain.razor` V2 block — `MudSelect<string>` bound to
  `_state.BridgeRules.UnderDeckMaterialName`, options from the loaded material set; show the resolved
  default ("dirt"/"asphalt" contains-match, shortest name) when unset.
- **Log:** `[BRIDGE-MATERIAL] spans=N cellsPainted=M material=<name>` + warn when no match found.

## 5. Verification (render checklist)

1. Log line shows the resolved material + painted cell count.
2. In-game: no grass/billboards poking through any deck (355/394/395), especially over the overlap
   tongues; ravine/water under tall spans keeps natural material (if threshold variant chosen).
3. Dropdown round-trips through preset export/import; default resolves to dirt-ish material on kattenes.

## 6. Open queue after this topic (unchanged)

199 isolated-span junction-raise anchor fallback → D honesty (planClear post-ramp/dip, dip target =
typed budget) → Phase B embankment stamping (`822b045` cherry-pick; pairs with ramp fill + doc 06).
