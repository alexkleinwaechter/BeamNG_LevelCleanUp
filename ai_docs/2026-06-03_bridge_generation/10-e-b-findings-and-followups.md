# E-B (3D bridge deck) — Findings & Follow-up Tasks

**Date:** 2026-06-07
**Branch:** `feature/bridges`
**Reads with:** `07-bridge-height-spec.md` (E-A/E-B/E-C spec, D-1..D-6), `08-grade-separation-followups.md`,
`09-bridge-deck-mesh-spec.md` (deck mesh contract), `09a-...-research-corpus.md` (dimensioned reference data).

This is the running findings + handoff doc after the E-B 3D-deck work and the two visual-iteration rounds
that followed. It records **what is live now**, the **one technical finding that must not be rediscovered**
(the inverted-normal root cause), and the **next tasks** — including two new features the user requested:
a DecalRoad **"on bridge"** mode and an **AI waypoint generator** that replaces AI DecalRoads on bridges.
Brainstorming of those two is explicitly deferred to the next session.

---

## 1. Where we are now (live on `feature/bridges`)

The bridge deck is a **3D solid box** exported as one `.dae` + one `TSStatic` per "generate bridge" spline
(`IsBridge && ExcludeBridgesFromTerrain`). Current geometry (all in `BridgeDeckMeshBuilder`):

- **Solid box shell** — deck top + soffit (`top − thickness`) + two fascia sides + start/end caps.
- **Edge parapets** — solid trapezoid per edge (0.9 m, base 0.45 / top 0.20), gated `ParapetHeightMeters > 0`.
- **Solid end stamps** — a solid abutment block under each end (soffit→down `AbutmentDepthMeters`, `EndStampLengthMeters` long), gated `GenerateAbutments`.
- **NO approach apron** (removed — it was a visual "disaster").
- Deck thickness = `clamp(0.05·span, 0.45, 2.0)` via `BridgeDeckProfile.ComputeDeckThicknessMeters`.
- Mesh counts: **`40(N-1)+48` verts / `20(N-1)+24` tris** (N = cross-sections; every face is a dedicated quad).

Terrain under the deck: **shave-to-deck only** — the excavator lowers only terrain poking *above* the deck
driving surface to `deckZ − undercut` (0.05 m). The **D-5 under-deck daylight carve was rolled back** (it dug a
deep ragged channel that wrecked the road↔bridge transition at abutments and side-road crossings). The deck box
just sits on natural terrain for now.

Knobs (UI, preset round-trip): `BridgeDeckThicknessSpanRatio/Min/Max`, `BridgeParapetHeightMeters`,
`BridgeUnderDeckClearanceMeters` (currently unused — reserved), plus the older
`BridgeMaxSagBelowChordMeters`, `BridgeDeckUndercutMeters`, `MinBridgeClearanceMeters`. `EndStampLengthMeters`
is a profile default (no UI knob yet). E-C `OsmTags` bag is on splines but **not yet consumed**.

**Status: NOT yet validated in-game after iteration 3.** First action next session = re-render and confirm the
solid + shading + transitions.

### Commit trail (this feature, newest first)
- `f87780d` rebuild deck as solid with outward normals; drop approach apron (iteration 3 mesh fix)
- `ea5ef93` roll back the under-deck carve to pre-stage-5 shave-to-deck (iteration 3 carve rollback)
- `b059b22` / `05faf60` v2 excavator protected-zone / solid stamps + aprons (superseded by iteration 3)
- `c5be290` E-C OsmTags bag (D-6)
- `b639adf` deck-mesh UI knobs + preset round-trip + profile unification (stage 6)
- `6047fd9` D-5 under-deck carve (stage 5) — **rolled back**; `7b43985` abutments (stage 4) = the pre-stage-5
  checkout point for A/B testing
- `9fe9d48`/`c8a47c0`/`23f36c2`/`c36c9ac` E-A grade-separated crossings; `21ffffd`/`f6562d3` box/parapets;
  `7751930` research corpus + spec; `0e9d8d0` Phase 0

---

## 2. THE finding that must not be rediscovered — inverted deck normals

**Symptom:** the deck looked like flat "panes," not a solid; lighting was wrong (deck top lit from below);
some faces (fascia) looked fine, others (top/soffit/caps) wrong → "half-broken."

**Root cause (verified in `ColladaExporter.cs`):** there are **two different export paths**, and they handle
coordinates/winding **differently**:

| Path | Used by | Coordinate transform | Winding |
|---|---|---|---|
| **Direct BeamNG-DAE XML** (`Export(BeamNgDaeScene)` → `WriteBeamNgDae` → `BuildGeometryElement`, uses `TransformPositionZUp`) | **bridge decks**, buildings | **NONE** — positions/normals written **as-is** (`ConvertToZUp` is *ignored* here); normals written raw (`vertex.Normal`, line ~444) | as-is (`FlipWindingOrder=false`) |
| **Assimp** (`Export(IEnumerable<Mesh>)` → `ConvertToAssimpMesh`, uses `TransformPosition`) | **road mesh** (`RoadNetworkDaeExporter`) | **`(-X, Z, Y)` mirror** when `ConvertToZUp=true` — a reflection (det −1) that **flips winding handedness** | flipped by the mirror |

The bridge builder had **copied the road's winding** into the *non-mirroring* path. So the deck top, soffit and
caps ended up with **inverted geometric normals** (deck top computed to −Z = lit from below); the fascia
happened to be wound the other way → looked right. Hence the half-broken appearance.

**Fix (live):** `BridgeDeckMeshBuilder` now authors **every face outward in plain Z-up** via one helper
`AddFace(a, b, c, d, approxOutward)` that (1) orients the quad CCW-from-outside so `cross(b−a, c−a)` agrees with
`approxOutward`, and (2) writes that **exact outward flat normal** on all four vertices — **no smooth/flat
recompute pass, no per-face guessing**. Regression test `Build_AllFaceNormals_PointOutward_NotInverted` locks
it (deck top +Z, soffit −Z, sides outward, stored normal == geometric normal).

**Rule for any future bridge/TSStatic mesh:** it goes through the no-conversion XML path, so **author geometry
outward in plain Z-up** (don't borrow the road mesh's winding, which assumes the Assimp mirror).

**Known tradeoff:** normals are **flat per quad** (no along-length smoothing) — fine for matte concrete; a
smooth-along-length option is a polish item (§3 task I).

---

## 2b. ⚠ REGRESSION (HIGH PRIORITY) — bridge terraforming bleeds onto neighbouring roads

**Reported from an iteration-3 render (2026-06-07).** Where a road passes under a bridge, the lower-road
**dip** (and the deck **shave**) terraform the heightmap — and that terraforming **bleeds laterally onto the
adjacent roads on the left and right, destroying their flat road surfaces**. The dip of the under-bridge road
itself also looks poor ("a bit horrible"). The user's framing — and it's correct — is that **the bridge
process is not integrated into the road-smoothing system**: it bolts a raw heightmap carve onto the *end* of
the pipeline and bypasses the road-surface protection the smoother already has.

### Root cause (verified)
- `GradeSeparationResolver.ApplyLowerRoadDips` → `DipLowerRoad` carves the dip straight into `heightMap2D`
  **after Phase-4 blending**, over a lateral footprint `reach = halfWidth + TerrainAffectedRangeMeters` with a
  smoothstep `LateralFalloff` (`GradeSeparationResolver.cs:282, 313–330`). That fade skirt has **no idea other
  roads exist** — wherever it overlaps a neighbouring road's footprint it lowers that terrain too, so the
  adjacent road loses its flat shape. `BridgeDeckExcavator.Excavate` has the same blind spot (raw post-blend
  shave over the deck footprint + `edgeMargin`).
- These two passes run in `TerrainCreator`'s 3b-bridge block **after** the blender, so they never see the
  blender's road mask / surface protection.

### The protection machinery that ALREADY exists (reuse it)
The main smoothing/blending pipeline already protects road surfaces from each other — this is what the user
means by "we solved this already":
- `Terrain/Algorithms/Blending/RoadMaskBuilder.cs` — builds the per-cell road-surface mask.
- `Terrain/Algorithms/Blending/ContestedPixelResolver.cs` — priority arbitration where surfaces compete.
- `Terrain/Services/DecalRoad/SurfaceFootprintIndex.cs` — spatial index of road-surface footprints.
- `SinglePassBlender` + the **surface-protection margin** (`Tests/Blending/SurfaceProtectionMarginTests.cs`,
  `Tests/Junction/SurfacePriorityOverrideTests.cs`) — keeps blending from eating into another road's surface.

### Fix directions (decide next session — this is partly architectural)
1. **Minimum / tactical:** before the dip & excavator write a cell, **skip cells that belong to another road's
   protected surface** (query a road mask / `SurfaceFootprintIndex` excluding the lower road itself). Stops the
   bleed without restructuring. Also make the dip's lateral fade respect neighbouring footprints.
2. **Proper / strategic (what the user is really asking for):** **integrate grade separation into the
   road-smoothing system** instead of a post-pipeline heightmap carve. The bridge-clearance need should enter
   as a *constraint* the smoother/blender solves (like junction harmonization already does), so the lower
   road's regrade is produced **by** the smoother — automatically inheriting road-surface protection, junction
   harmonization, banking, and a clean (non-"horrible") longitudinal profile — and the neighbouring roads are
   protected by the same machinery that already protects them everywhere else.
3. The middle-road dip profile quality ("horrible") should improve naturally under (2); under (1) it still
   needs the eased well reviewed (depth/ramp vs the smoother's own profile).

**Until fixed, the dip-carve is the main risk in the bridge pipeline.** Note: shave-to-deck (current, shallow)
bleeds far less than the rolled-back D-5 soffit carve did, but the dip (which can be metres deep to make
clearance) is the real offender here. Consider gating the heightmap dip-carve off by default until (1)/(2)
lands, if renders show it doing more harm than good.

---

## 3. Next tasks

Ordered roughly by what unblocks the most. **B and C are the new user-requested features — brainstorm next
session before implementing.**

### A0. FIX THE REGRESSION (§2b) — HIGHEST PRIORITY
Stop the bridge dip/excavator from terraforming neighbouring roads, and integrate grade separation into the
road-smoothing system. See §2b for root cause, the existing protection machinery to reuse
(`RoadMaskBuilder` / `SurfaceFootprintIndex` / `ContestedPixelResolver` / surface-protection margin), and the
tactical-vs-strategic fix directions. This gates A (a clean re-render needs this fixed first).

### A. Re-render validation (iteration 3) — DO FIRST
Render a bridge map and confirm: (1) the deck reads as a **solid with correct shading** (normal fix worked),
(2) road↔bridge transitions are acceptable with shave-to-deck (no deep carve), (3) stamps look right. Decide
from there whether the simpler no-carve state is the baseline to build on.

### B. DecalRoad "on bridge" mode / parameter  ⟵ NEW (user) — brainstorm next session
**Why:** the road surface markings/material on a bridge deck need different treatment than on terrain. Today
the only special handling is `OverObjects = layer.OverObjects || isGeneratedBridge`
(`DecalRoadGenerator.cs:427`, with `IsGeneratedBridge` at `:148`) so the DecalRoad projects onto the deck
collision mesh. That's necessary but not sufficient.
**Want:** a dedicated **"on bridge"** DecalRoad mode/parameter so deck roads can differ from ground roads —
e.g. which visual layers apply on the span, deck-specific material(s)/wear, render priority/fade tuned for
projecting onto a `TSStatic`, and coordination with the AI handling in (C).
**Seeds (verified):** DecalRoad carries `OverObjects`, `ImprovedSpline`, `IsAIRoad`, `Drivability`,
`Material`, layer sets. The bridge spline already exposes `OsmTags` (E-C) for type-driven choices. Pipeline:
`DecalRoadGenerator.Generate` → `DecalRoadSceneWriter`.
**Open questions for brainstorming:** is "on bridge" a new *layer-set variant*, a per-layer flag, or a
post-pass that rewrites bridge-spline DecalRoads? How does it interact with the deck mesh's own (future)
painted material? Should parapet/edge layers be suppressed on the span?

### C. AI waypoint generator — replace AI DecalRoads on bridges  ⟵ NEW (user) — brainstorm next session
**Why:** AI navigation on BeamNG roads comes from DecalRoads flagged for AI (`IsAIRoad`, `Drivability`,
`ImprovedSpline`). On a bridge the drivable surface is a `TSStatic` deck (terrain is excluded), so the AI
DecalRoad on the span is unreliable. **Want:** a generator that emits explicit **AI waypoints / a path graph**
along the bridge deck that **replaces** the AI DecalRoad over the span, so AI traffic crosses the bridge
cleanly and connects to the approach road AI on both ends.
**Open questions for brainstorming:** target BeamNG construct (AI waypoint nodes? a dedicated AI-only
DecalRoad/MeshRoad with `Drivability` set? the `aiPath`/decal `improvedSpline`?); node spacing & width along
the deck centerline (we already have the solved bridge cross-sections — centerline + width per station are in
hand); how to stitch the waypoint path to the approach-road AI at each abutment; whether to *suppress* the
visual DecalRoad's AI flag on the span and hand AI to the waypoint path; scene-writing format + grouping.
**Seeds:** bridge cross-sections give centerline XY+Z, width, banking per station; `BridgeProfileSolver`
already produces the final deck Z; `DecalRoadSceneWriter` is the model for NDJSON scene emission.

> B and C are related — the deck DecalRoad (visual) and the AI path (navigation) likely split:
> DecalRoad-on-bridge handles *looks*, the waypoint generator handles *AI*. Decide the split when brainstorming.

### D. Side-road-meets-bridge: parapet omission + smooth transition
From the iteration-3 render: where another road (e.g. a dirt road) meets the **side** of the bridge, (1) there
must be **no parapet/railing** on that stretch of edge, and (2) the join needs a smooth transition (the old
deep carve made it worse). Requires detecting where a non-bridge spline meets a bridge **edge** (not just an
end) and locally suppressing the parapet + grading the join. User flagged this as "really complicated."

### E. Redesigned under-deck daylight (excavation)
The D-5 soffit-clearance carve was rolled back. If real daylight under the span is wanted later, redesign it so
it does **not** wreck the abutment/side-road transitions (the solid end stamps were meant to bound it; revisit
with a gentler, transition-aware carve, or drive it off the OSM-context rules engine in doc 08 §3).

### F. "Do both" grade-separation clearance split (doc 08 §2)
Split required road-under-bridge clearance between raising the deck (interior arch) and dipping the road, via a
priority-modulated ratio — the natural fix for the "trough under a flat bridge" look. Independent of the mesh.

### F2. Deck span extension to the elevated-road extent (deck follows the road off the abutment) ⟵ NEW (user, 2026-06-08)
**Why:** with merged-corridor bridges live, a deck now faithfully covers **only the OSM `bridge=yes` arc-range**
of its corridor. On a valley/fill crossing the road stays elevated on embankment well past where `bridge=yes`
is tagged, so the deck reads as **"a bit too short"** at the abutment (user report, `franco_same_prio`
`bridge_52033533` = span on corridor spline 216, arc-range `[227.2, 275.4]m` = 48.5 m deck; steep approaches
`g0=-12% / g1=+15.7%`, **no** grade-separated road under it — it's a valley bridge; the dark patch at the end is
the excavator shaving the +15.7% approach fill, `maxCut=3.37 m`). The deck is correct vs OSM, but the user
wants it to span the **physically elevated** structure, not just the tagged way.
**Want:** optionally **grow each bridge span outward along the corridor** while the road sits meaningfully above
terrain — i.e. extend `[StartDistance, EndDistance]` on each side, adding cross-sections, while
`deckZ − terrainZ > threshold` (e.g. `≥ MinBridgeClearanceMeters`, or a dedicated `DeckExtendClearanceMeters`),
stopping when the road returns to grade. The deck mesh, the per-section exclusion, and the snapshot then all
cover the extended range (they already key off the same span — one change at the marking/solve stage flows
through Phases 3–5 unchanged).
**Scope warning (decide before building):** this **changes the terrain-exclusion region** — the extended
sub-range stops stamping/painting terrain under the approach embankment too. That is the doc 08 §2 "do both" /
embankment-vs-fill decision (see also F). Confirm whether faithful-to-OSM or follow-the-fill is wanted, since it
affects the heightmap under the approaches and interacts with the §2b dip/excavator bleed.
**Seeds (verified):** the span is a `StructureSegment` arc-range; exclusion marking is
`UnifiedRoadSmoother.MarkStructureExclusions` (where the grow would live, using `cs.OriginalTerrainElevation`
vs the solved/terrain Z); the deck reads the tagged sections via the snapshot, so no exporter change needed.
Tie the threshold to the existing clearance knob or add a new UI knob (preset round-trip like the other bridge
knobs). Keep it **opt-in / off by default** until validated, since it moves terrain.

### G. Piers / intermediate supports (B-4, deferred)
Add columns + pier cap when a span exceeds ~35–40 m (corpus §6). Deferred from E-B v1.

### H. OsmTags-driven mesh styling (E-C consumer)
`spline.OsmTags["bridge"]` (viaduct/trestle/…) → switch deck style (e.g. force piers for viaduct). The plumbing
exists; nothing consumes it yet.

### I. Polish
- Smooth-along-length normals on the deck top (currently flat per quad) if faceting shows.
- Surface `EndStampLengthMeters` (and any apron-replacement transition length) as UI knobs.
- UV/texture tiling on the deck (currently simple per-quad corner UVs; fine with the flat placeholder material,
  matters once a real deck material lands).

---

## 4. Pointers (verified)
- Mesh: `BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs` (`AddFace` is the outward-normal contract),
  `BridgeDeckProfile.cs` (config + `ComputeDeckThicknessMeters`).
- Export paths: `BeamNG.Procedural3D/Exporters/ColladaExporter.cs` — `WriteBeamNgDae`/`BuildGeometryElement`
  (bridge, no transform) vs `ConvertToAssimpMesh`/`TransformPosition` (road, `(-X,Z,Y)` mirror).
- Excavator (shave-to-deck): `BeamNgTerrainPoc/Terrain/Export/BridgeDeckExcavator.cs`; wired in
  `TerrainCreator.cs` 3b-bridge block.
- DecalRoad: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` (`IsGeneratedBridge` :148,
  `OverObjects` :427; `IsAIRoad`/`Drivability`/`ImprovedSpline` on `GeneratedDecalRoad`), `DecalRoadSceneWriter.cs`.
- Solver/edges: `BridgeProfileSolver.cs` (final deck Z + banked edges); cross-section data on
  `UnifiedCrossSection` (CenterPoint, TargetElevation, Left/RightEdgeElevation, BankAngleRadians, width).
