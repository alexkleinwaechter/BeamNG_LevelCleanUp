# E-B — 3D Bridge Deck Mesh: Default Cross-Section + Mesh Spec

**Date:** 2026-06-07
**Branch:** `feature/bridges`
**Reads with:** `07-bridge-height-spec.md` §5 (E-B framing) + §6 D-5, `08-grade-separation-followups.md`,
`09a-bridge-cross-section-research-corpus.md` (the dimensioned research this spec is grounded in), and the
shipped Phase-0/E-A code (`BridgeDeckDaeExporter`, `BridgeProfileSolver`, `BridgeDeckExcavator`,
`RoadMeshBuilder`).

This is the **build contract** for E-B: turn the flat-ribbon bridge deck into a real 3D **box deck** with
**fascia side faces + soffit + edge parapets + abutment end walls**, and **reopen the under-deck gap** (D-5).
Piers are **deferred** (decision below). Every default number traces to the research corpus; the four profile
decisions were ratified with the user 2026-06-07.

---

## 0. Ratified decisions (2026-06-07)

| # | Decision | Value |
|---|---|---|
| **B-1** | **Deck thickness** (riding-surface top → soffit underside) | **span-proportional**, `thickness = clamp(DeckThicknessSpanRatio · spanLength, DeckThicknessMinMeters, DeckThicknessMaxMeters)` = `clamp(0.05·span, 0.45, 2.0)` m. UI-tunable (ratio + min + max). |
| **B-2** | **Parapet/barrier** per edge | **solid trapezoidal**: height **0.9 m**, base width **0.45 m**, top width **0.20 m**, outer face flush with the deck edge, inner face sloped inward. UI-tunable (height; widths are constants for v1). |
| **B-3** | **Abutment end walls** | **yes** — a vertical end wall (full deck width) dropping from the deck soffit to the terrain at each bridge end (seat-type behaviour). |
| **B-4** | **Piers** | **deferred** to a later iteration. v1 = deck box + fascia + parapets + abutment walls only. |

Soffit shape is a **flat box underside** (corpus §2 — correct for slab/box bridges, indistinguishable from
girder ribbing at driving distance). No separate riding-slab layer in v1 (single solid box).

---

## 1. What exists today (ground truth, verified)

- `BridgeDeckDaeExporter.Export` converts each bridge spline's cross-sections to world coords
  (`CrossSectionConverter.ConvertSplineToWorldCoordinates`, keeps `IsExcluded` sections) and feeds them to
  `new RoadMeshBuilder().AddCrossSections(...).Build()` → a **single flat ribbon**: 2 verts/section, 2
  tris/segment. One `.dae` per bridge via `WriteBeamNgBridgeDae` (base00/start01/Colmesh-1 hierarchy).
- Per converted `RoadCrossSection` (world coords, Z incl. `TerrainBaseHeight`):
  `CenterPoint` (X,Y), `CenterElevation` (deck-top centre Z), `NormalDirection` (unit, **points right**),
  `TangentDirection` (unit, along road), `WidthMeters` (= `EffectiveRoadWidth`), `BankAngleRadians`,
  `DistanceAlongRoad` (= source `DistanceAlongSpline`), `LeftEdgeElevation`/`RightEdgeElevation` (banked deck-top
  edges that `BridgeProfileSolver` wrote = `centre ∓ halfWidth·sin(bank)`).
  - **Top-edge world positions** (the current ribbon verts):
    `topLeft = CenterPoint − Normal·halfWidth` at `LeftEdgeElevation`;
    `topRight = CenterPoint + Normal·halfWidth` at `RightEdgeElevation`.
- `BridgeDeckExcavator.Excavate` lowers any terrain cell under the deck footprint that pokes **above** the deck
  to `deckZ + offset·sin(bank) − undercut` (0.05 m). Min-only (never fills). This is the **shave-to-deck** rule.
- `BridgeProfileSolver.ApplyStructuralProfiles` has already set each bridge section's final `TargetElevation`
  + banked edges before either the excavator or this exporter runs (one source of truth).
- `BridgeSceneWriter` writes one `TSStatic` per deck at world (0,0,0) + a concrete-gray placeholder material.

The mesh ignores `TangentDirection` for vertex placement (verts are `Center ± Normal·halfWidth`) — the E-B box
extends that: all new geometry is built from `Center`, `Normal`, world-`Z`, and `Tangent` is used **only** for
the end-cap outward facing. (Matches the doc-04 "normal-only seam" note.)

---

## 2. The default cross-section (looking along the road, +Normal = right)

```
            parapet (0.9 high)                         parapet
          top 0.20                                    top 0.20
            ┌──┐                                        ┌──┐
            │  │  inner face sloped                     │  │
            │   \___                              ___/  │  │
   ─────────┴───────┬──────── deck top (riding) ───────┬──┴─────────  ← topLeft … topRight (banked)
            ◄ base ►│                                   │◄ base ►
            0.45    │                                   │  0.45
   ◄──────────────── EffectiveRoadWidth ───────────────►│
                    │         box deck (solid)          │
   ─────────────────┴───────── soffit ─────────────────┴──────────   ← botLeft … botRight  (top − thickness)
                              flat underside
```

- **Deck box:** top ribbon (existing) + a parallel **soffit** ribbon at `topZ − thickness` (thickness from B-1).
  The two **side faces** between top and soffit edges **are the fascia** (corpus §3 — no separate fascia band).
- **Parapets:** one solid trapezoid sitting **on** each deck top edge, outer face flush with the deck edge,
  rising vertically in world-Z by `ParapetHeightMeters`.
- **Abutment end walls:** at the first and last section, a vertical wall (deck width) from soffit-edge Z down to
  `min(terrain under the four end corners, soffitZ)` — closes the soffit-to-ground gap (B-3).
- **Cross slope / bank:** inherited — top edges already carry banked Z; soffit/parapet/wall are built **relative
  to those banked edges**, so the whole box tilts with the deck (no separate crown).

### 2.1 Thickness — single source of truth

```
static float ComputeDeckThicknessMeters(float spanLengthMeters, BridgeDeckProfile p)
    => Math.Clamp(p.DeckThicknessSpanRatio * spanLengthMeters, p.DeckThicknessMinMeters, p.DeckThicknessMaxMeters);
```

`spanLengthMeters = lastSection.DistanceAlongRoad − firstSection.DistanceAlongRoad`. **Both** the mesh builder
and the excavator must call this same helper so the soffit Z they assume is identical. Live it on
`BridgeDeckProfile` (or a static `BridgeDeckGeometry`) in `BeamNG.Procedural3D` or
`BeamNgTerrainPoc.Terrain.Export` — must be reachable by both the exporter and the excavator.

---

## 3. Mesh construction (exact vertex layout + winding)

A new **`BridgeDeckMeshBuilder`** (in `BeamNG.Procedural3D/RoadMesh/`, alongside `RoadMeshBuilder`) owns this.
It takes the ordered `IReadOnlyList<RoadCrossSection>` + a `BridgeDeckProfile` + `thickness` and returns one
`Mesh` (single material for v1 — the existing placeholder; structure should make a 2nd parapet material a cheap
future add). Build order and winding (BeamNG is Z-up after `ConvertToZUp`; the existing ribbon uses
**counter-clockwise = upward** normals — match it; `SmoothNormals` then averages):

For section `i`, let `hwL/hwR` be the left/right half-width offsets, and:
- `topL_i = Center_i − N_i·hw` @ `LeftEdgeElevation_i`, `topR_i = Center_i + N_i·hw` @ `RightEdgeElevation_i`
- `botL_i = topL_i − (0,0,thickness)`, `botR_i = topR_i − (0,0,thickness)`

**(a) Deck top** — unchanged ribbon: `topL_i, topR_i`; tris `(topL_i, topR_i, topL_{i+1})`,
`(topR_i, topR_{i+1}, topL_{i+1})` (CCW-up).
**(b) Soffit** — `botL_i, botR_i`; tris wound the **opposite** way so the normal faces **down**:
`(botL_i, botL_{i+1}, botR_i)`, `(botR_i, botL_{i+1}, botR_{i+1})`.
**(c) Left fascia** (outer normal `−N`): quads `topL_i→botL_i→botL_{i+1}→topL_{i+1}`, wound so the face points
away from the deck centre (`−Normal`).
**(d) Right fascia** (outer normal `+N`): quads `topR_i→botR_i→botR_{i+1}→topR_{i+1}`, wound to point `+Normal`.
**(e) Start cap** (section 0, facing `−Tangent_0`): quad `topL_0, topR_0, botR_0, botL_0`.
**(f) End cap** (section ^1, facing `+Tangent_^1`): quad `topL_^1, topR_^1, botR_^1, botL_^1` (reverse winding).

**(g) Parapets** (per edge, only if `ParapetHeightMeters > 0`): a trapezoid extruded along the span. In the
cross-section plane use lateral offset `o` measured along `±Normal` from the deck edge **inward**, height `h`
above the deck-edge Z. **Right** edge profile (4 corners, base→top, outer→inner):
- `pBaseOuter = topR_i` (o=0, h=0)
- `pBaseInner = topR_i − N·BaseWidth` (o=BaseWidth, h=0)
- `pTopInner  = topR_i − N·((BaseWidth+TopWidth)/2 ... )` — simpler: `topR_i − N·(BaseWidth−TopWidth)` , h=ParapetHeight
- `pTopOuter  = topR_i` + Z·ParapetHeight (o=0, h=ParapetHeight)

  i.e. outer face **vertical & flush** (o=0 top and bottom), inner face slopes from `BaseWidth` in at the base
  to `(BaseWidth−TopWidth)` in at the top, giving a top face of width `TopWidth`. Faces to emit per segment:
  outer (flush, `+N`/`−N` outward), inner (sloped, inward), top. Bottom sits on the deck → omit. **Left** edge
  mirrors with `+N` (offsets toward centre are `+N`). Parapet end caps optional for v1 (hidden by abutment wall)
  — omit. Parapet height is applied in **world-Z** (vertical posts), not along the deck normal.

**(h) Abutment end walls** (B-3, only if `GenerateAbutments`): at section 0 and ^1, a vertical quad strip from
the soffit edge line down to terrain. v1 simplification: one flat wall per end spanning `botL→botR`, its bottom
Z = `min(soffitZ_at_that_end − AbutmentMinExposureMeters(0.6), terrainZ under the end)`. Since the excavator
(D-5) carves the channel and the existing dip/ramp shapes the embankment, the end wall mainly closes the
visible gap; for v1 **drop the wall from the soffit edge straight down by `AbutmentDepthMeters` (default 2.0 m)**
(a fixed apron), wound to face outward along `∓Tangent`. (Terrain-following abutment height is a follow-up; a
fixed apron + the excavator is enough to kill the see-through-the-end artifact. Note this in the doc + a TODO.)

Collision mesh (`Colmesh-1`) = the **deck top + soffit + fascia + end caps** (the solid box) **without**
parapets (so cars don't get an invisible collision lip from the sloped parapet inner face — drivers should be
stopped by the parapet *visual* but v1 keeps colmesh = drivable box; revisit if cars clip through parapets).
Actually include parapets in colmesh so vehicles can't drive off the deck — **decision: colmesh = full mesh**
(box + parapets + walls), matching the visual. Keep it simple: clone the whole built mesh as colmesh (current
behaviour) — fine for v1.

---

## 4. Reopen the under-deck gap — D-5 (`BridgeDeckExcavator`)

Today the excavator shaves terrain down to **`deckZ − undercut`** (terrain meets the *driving surface*). With a
real box deck of thickness `T`, that leaves terrain pressed against the **top** of the deck — no daylight under
the span. D-5: lower the ceiling to the **soffit minus a clearance**:

```
ceiling(offset) = deckTopZ + offset·sin(bank) − thickness − UnderDeckClearanceMeters
```

- `thickness` = `ComputeDeckThicknessMeters(span, profile)` (the SAME helper as the mesh — §2.1).
- `UnderDeckClearanceMeters` = new knob, **default 0.5 m** (a hair of daylight below the soffit; the fascia +
  abutment walls hide the rasterised lateral edge that killed the gap before — doc 07 D-5).
- **Still min-only** (never fills): real ravines/water/dipped roads under the span are preserved; only terrain
  poking above the soffit-minus-clearance is cut. On genuinely flat ground at deck level this digs a shallow
  channel ≈ `T + 0.5 m` deep under the span — acceptable (you don't bridge flat ground; grade-separated lower
  roads are already dipped by E-A; valley/water spans are already below).
- **No-height fallback:** keep the old `deckZ − undercut` shave path for when 3D-deck thickness is unavailable
  (e.g. a future flag turns the box deck off). Implement as: `Excavate(..., deckThicknessProvider, underClearance)`
  where a null/absent thickness ⇒ old behaviour (undercut only). Practically, in bridge mode the box deck is
  always on, so the new path is the default; the fallback is a guard, not a user mode.

Wire in `TerrainCreator` 3b-block: the `Excavate` call gains the thickness (per-bridge, from the same span
calc) + `underClearanceMeters: parameters.BridgeUnderDeckClearanceMeters`.

---

## 5. Knobs (new) — params → state → UI → presets

Add to `TerrainCreationParameters` **and** `TerrainGenerationState` (with reset), wire through `TerrainCreator`,
surface in the **"Bridge/Tunnel Structure Handling"** panel of `GenerateTerrain.razor`, and round-trip in the
preset exporter/importer/result (mirror the Phase-0/E-A knobs `BridgeMaxSagBelowChordMeters`,
`BridgeDeckUndercutMeters`, `MinBridgeClearanceMeters`):

| Knob | Default | Meaning |
|---|---|---|
| `BridgeDeckThicknessSpanRatio` | 0.05 | thickness = ratio·span (B-1) |
| `BridgeDeckThicknessMinMeters` | 0.45 | clamp floor |
| `BridgeDeckThicknessMaxMeters` | 2.0 | clamp ceiling |
| `BridgeParapetHeightMeters` | 0.9 | 0 disables parapets |
| `BridgeUnderDeckClearanceMeters` | 0.5 | D-5 daylight below soffit |

Parapet base/top widths (0.45/0.20) and abutment depth (2.0)/exposure stay **constants** in the profile for v1
(not surfaced) to keep the panel small. `BridgeDeckProfile` (the builder's config record) is constructed in the
exporter from these params.

---

## 6. File-by-file plan

| File | Change |
|---|---|
| `BeamNG.Procedural3D/RoadMesh/BridgeDeckProfile.cs` (new) | record: thickness ratio/min/max, parapet h/base/top, abutment depth/exposure, under-clearance (clearance lives here too so excavator shares it); `ComputeDeckThicknessMeters(span)` helper. |
| `BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs` (new) | builds the box+fascia+soffit+parapets+abutments mesh per §3. |
| `BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs` | build via `BridgeDeckMeshBuilder` + `BridgeDeckProfile` (from params) instead of `RoadMeshBuilder`; compute span; pass profile. Skip-empty/skip-unchained behaviour unchanged. |
| `BeamNgTerrainPoc/Terrain/Export/BridgeDeckExcavator.cs` | D-5: ceiling = soffit − under-clearance using the shared thickness helper; keep undercut fallback. |
| `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` | pass profile/thickness knobs to exporter + excavator. |
| `TerrainCreationParameters` / `TerrainGenerationState` / `GenerateTerrain.razor` / preset import-export-result | the 5 knobs (§5). |
| (E-C, separate) `ParameterizedRoadSpline` / `RoadSpline` / `OsmGeometryProcessor` | `OsmTags` bag (D-6). |

---

## 7. Stage breakdown (TDD, commit per stage, baseline 392 green)

1. **Stage 1 — spec** (this doc + corpus). Commit `docs(E-B)`.
2. **Stage 2 — box deck (thickness + soffit + fascia + end caps).** New `BridgeDeckProfile` +
   `BridgeDeckMeshBuilder`; exporter uses it. Tests: thickness clamp formula; soffit verts = top − thickness;
   soffit faces down; fascia present; end caps; updated vert/tri counts in `BridgeDeckDaeExporterTests`
   (ribbon counts → box counts). Commit `feat(E-B): box deck mesh (thickness/soffit/fascia)`.
3. **Stage 3 — parapets.** Trapezoid per edge (§3g). Tests: parapet verts at `edge + 0.9·Z`; outer face flush;
   `ParapetHeight=0` ⇒ no parapet geometry. Commit `feat(E-B): edge parapets`.
4. **Stage 4 — abutment end walls.** §3h. Tests: end-wall quad present at both ends; bottom = soffit −
   AbutmentDepth; `GenerateAbutments=false` ⇒ none. Commit `feat(E-B): abutment end walls`.
5. **Stage 5 — D-5 reopen under-deck gap.** Excavator ceiling = soffit − clearance via shared helper; fallback
   kept. Tests: terrain above soffit cut to soffit−clearance (not deck−undercut); terrain in the gap untouched;
   banked low side still tracked; no-thickness ⇒ old undercut behaviour. Commit `feat(E-B): reopen under-deck gap (D-5)`.
6. **Stage 6 — UI knobs + presets.** 5 knobs through params/state/UI/preset round-trip. Tests: preset
   round-trip; state reset. Commit `feat(E-B): bridge-deck mesh UI knobs + preset round-trip`.
7. **Stage 7 — E-C OsmTags bag (D-6).** `IReadOnlyDictionary<string,string>? OsmTags` on the spline, populated
   from `PathWithMetadata.Tags` at spline creation. Tests: a bridge spline created from a feature with tags
   exposes `spline.OsmTags["bridge"]`. Commit `feat(E-C): OsmTags bag on splines (D-6)`.

After Stage 6: **render a bridge map in-game** to validate the box/parapet/abutment look + that the under-deck
gap is real (drive under a grade-separated crossing). E-C is plumbing — no visual change yet (future mesh-style
switching consumes it).

## 8. Acceptance

- 392 baseline tests stay green; each stage adds tests and keeps the suite green.
- A straight bridge produces a closed box (top+soffit+2 fascia+2 caps) + 2 parapets + 2 end walls; no NaN verts;
  one `.dae` + one `TSStatic` per bridge as before; unchained/empty bridges still skip with a warning.
- A curved (banked) bridge: soffit/parapets/walls tilt with the deck (built off the banked edges).
- Under a grade-separated crossing there is **real daylight** beneath the span (terrain cleared to soffit −
  clearance), with fascia + abutment walls hiding the cut edges.
