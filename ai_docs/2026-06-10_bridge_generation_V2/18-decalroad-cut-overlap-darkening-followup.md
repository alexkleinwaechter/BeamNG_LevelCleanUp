# 18 — DecalRoad cut-overlap darkening: deferred per-layer overlap fix

Status: **DEFERRED** (accepted cosmetic, 2026-07-08). This doc records the known issue and the
agreed fix design so it can be implemented without re-deriving the analysis.

## Context: what shipped (commits `633c661`, `c86c216`, `33c73b2` on `feature/bridge_embankment_containment`)

DecalRoads are no longer generated as one spline-long road. `DecalRoadGenerator.GenerateForLayerRange`
partitions its section range via `PartitionSectionsByStructure` into contiguous **Road / Bridge / Tunnel
runs** (keyed off `UnifiedCrossSection.StructureSpanId` + `StructureSegment.IsBridge/IsTunnel`, legacy
whole-spline bridges/tunnels as a single run) and emits one DecalRoad per run:

- `OverObjects` is set only on bridge-deck runs (`SectionRun.OnDeck`), hugging the physical deck extent.
- The per-layer **render scope** (`RenderOnRoads` / `RenderOnBridges` / `RenderOnTunnels` on
  `DecalRoadLayerDefinition`) skips runs entirely — e.g. the bridges-only `BridgeTunnelSurface`
  (`RoadSurface` layer type) in the defaults.

Two different seam rules exist, deliberately:

| Cut | Overlap | Why |
|---|---|---|
| Bridge/tunnel run cuts (`PartitionSectionsByStructure`) | **One full node span** (structure runs extend 2 sections into neighbours → 2 shared nodes) | A point-contact cut straightens each piece's end segment (the rendered spline has no control point beyond its last node); in curves the straightened tails diverge from the arc and leave a **wedge-shaped coverage gap** (screenshot-verified at a deck end). The overlap nodes are the *same* cross-sections, so the neighbour's properly curved interior covers the tail. |
| Length chunking (`ChunkNodes`, ≤80 nodes) | **Single shared node** | Chunk pieces follow the same terrain — no wedge gap ever showed there. A one-span overlap was tried and **reverted**: it double-draws translucent layers (tread marks / wear), visible as a **dark band across the road** at every chunk seam (screenshot-verified). |

## The remaining issue

The same double-draw darkening now appears **at bridge-cut overlaps** for translucent all-scope layers
(tread marks / wear crossing the deck boundary render twice over the one-span overlap segment).
User decision 2026-07-08: acceptable for now — the deck transition zone is visually busy anyway.

## Agreed fix design (when it matters): per-LAYER overlap instead of per-cut

`PartitionSectionsByStructure` is called from inside `GenerateForLayerRange`, i.e. **once per layer** —
so the overlap width can be chosen per layer at zero architectural cost:

1. Add an `int structureOverlapSections` parameter to `PartitionSectionsByStructure` (currently the
   extension is hardcoded `±2` in the extension loop; `±1` ⇒ single shared node, `±2` ⇒ one-span overlap).
2. In `GenerateForLayerRange`, choose the width from the resolved layer:
   - **One-span overlap (2)**: full-width, effectively opaque surface layers — `LayerType == RoadSurface`
     is the primary case (the wedge gap is only glaring on wide surface decals).
   - **Single shared node (1)**: everything else — translucent tread/wear layers (kills the dark band),
     narrow markings (a kink at the cut reads better than the faint 2 m "fork" the overlap can produce,
     since each piece's end tangent straightens differently).
3. Optionally expose an override on `DecalRoadLayerDefinition` (e.g. `SeamOverlapSpans: 0|1`, default
   by layer type) if per-type defaults prove wrong for some custom layer — start without it.

Caveats / notes:

- Layers demoted to single-node overlap regain the point-contact cut at bridge boundaries — acceptable
  because they are narrow (kink only) or translucent (gap barely visible); the wedge was only ever
  reported for the full-width surface.
- Alternative considered and NOT chosen: crossfade via `StartEndFade` at internal cut ends (overlap +
  alpha fade = crossfade). Rejected for scope-filtered layers: when the neighbouring run is skipped
  (bridges-only surface), there is no partner to crossfade with — the fade would reveal the deck
  placeholder material at the deck edge.
- Adjacent structure runs of two different spans both extend and can overlap by up to ~3–4 nodes;
  same double-draw consideration applies there (rare: back-to-back distinct spans).

## Where the tests live

`BeamNgTerrainPoc.Tests/DecalRoad/BridgeDecalRoadFilterTests.cs`:
- `PartitionSectionsByStructure_*` pin the run extents incl. the ±2 extension and clamping.
- `Generate_MergedCorridorBridgeSpan_OverObjectsOnlyOnDeckRun` asserts the one-span overlap
  end-to-end (deck's first/last two nodes coincide with the approach runs' adjacent two nodes).

`BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadGeneratorTests.cs`:
- `ChunkNodes_SplitsWithBoundaryOverlap` pins the single-shared-node chunk seam.
- `ChunkNodes_NoDegenerateTailChunk` (81–400 node sweep) guards the fixed blind-stride tail bug —
  keep it green regardless of any overlap changes.

When implementing the per-layer overlap, the partition tests need an overlap argument; add cases for
both widths and an end-to-end test that a tread-marks layer crossing a deck boundary shares exactly
one node while a RoadSurface layer shares two.
