# Handoff / Pre-Spec — Bridge Height (item E: 3D deck + grade-separated crossings)

**Date:** 2026-06-07
**Branch:** `feature/bridges`
**Reads with:** `06-next-steps-handoff.md` §4 (the item-E sketch), `05-bridge-elevation-and-continuity-plan.md`
(shipped vertical solver + excavator), `00-findings-and-decisions.md` (D1–D9).

This document is the **research + decision** hand-off for the next headline feature: giving bridges real
**height** — a 3D deck (thickness/fascia/piers) and, more importantly, making the bridge deck **clear the
road(s) passing underneath it**. Phase 0 (the flat-ribbon deck + vertical profile + banked excavation) is
closed; this is the "right version".

It answers the four questions raised when scoping the feature, **each verified against the code** (file:line),
then turns the findings into concrete decisions and a phased plan. Nothing here is implemented yet.

---

## 0. TL;DR

- **OSM bridge tags:** In bridge mode (`ExcludeBridgesFromTerrain=true`) every bridge OSM way becomes its own
  spline 1:1 — `IsBridge`, `IsTunnel`, `Layer`, `StructureType`, `BridgeStructureType` (from `bridge:structure`)
  are preserved per-way. **But** the full OSM tag dictionary does **not** reach the spline (only `highway`),
  the `bridge=` *value* (viaduct/cantilever/…) is collapsed to a bool, and a multi-way bridge becomes
  **several** splines. → small, safe plumbing job to expose a tag bag; multi-way-bridge chaining is the real
  risk.
- **Roads under bridges:** They **are** already found — as `MidSplineCrossing` junctions. **But** junction
  detection is completely **layer/bridge-blind**: a road passing under a bridge is currently treated as a real
  at-grade junction (latent bug), and there is **no clearance concept anywhere**. → this is the core new work.
- **Priority rules:** A solid priority cascade exists (motorway>…>path, OSM-class or width based) and is
  already the arbiter for who-holds-elevation at junctions. It is directly **reusable** to decide which road
  gives way at a crossing — but bridges currently sit *outside* junction harmonization, so the rule must be
  applied in a new grade-separation step, not the existing one.
- **DEM quality:** Elevation is **DEM-only** (no OSM elevation), sampled **nearest-neighbour** (no bilinear),
  then low-pass smoothed with a **~150 m window**. Good to ~±0.5–1 m on short/flat spans; **unreliable for
  long/deep spans** (the same blur that caused the deck-sag bug). → drive clearance off **road-vs-road solved
  elevations**, not terrain-under-span; treat DEM-under-deck as a diagnostic only.

**Recommended first slice:** make grade-separated crossings *first-class* (detect + classify by layer, compute
road-vs-road clearance, decide under/over by priority) **before** any 3D mesh work. The mesh is the visible
payoff; the crossing logic is what makes "bridge height" mean something.

> **Decisions D-1…D-6 are RATIFIED (2026-06-07) — see §6.** Build order: **E-A** (grade-separated crossing
> logic) first, then **E-B** (3D deck mesh + reopen under-deck gap), then **E-C** (`OsmTags` bag, with E-B).

---

## 1. OSM bridge tags on our splines  (your Q1)

**Question:** are all bridge tags from OSM available on our bridge splines (for multi-layered bridges / typing)?

### What's captured and where (verified)

| Stage | What it holds | Ref |
|---|---|---|
| Overpass fetch | explicitly queries `way["bridge"]`, `way["tunnel"]`, `way["man_made"="bridge"]` | `OverpassApiService.cs` (~440) |
| Parse | **full** tag dict per feature (`OsmFeature.Tags`) — nothing filtered at parse | `OsmGeoJsonParser.cs` (~178) |
| Feature props | `IsBridge` (`bridge!=no`), `IsTunnel` (`tunnel`/`covered=yes`), `Layer` (int, def 0), `BridgeStructureType` (`bridge:structure`), `StructureType` enum | `OsmFeature.cs` |
| Path | `PathWithMetadata` carries `Tags` + all the derived flags | `PathWithMetadata.cs` |
| **Spline** | `IsBridge`, `IsTunnel`, `Layer`, `StructureType`, `BridgeStructureType`, `OsmRoadType` (=`highway` only), `OsmWayIds` | **verified** `ParameterizedRoadSpline.cs:43,57,101,123,148` |

### The merge question (verified — this is the important nuance)

Path merging (`NodeBasedPathConnector`) is **first-path-wins**: all four merge functions copy
`path1.IsBridge/IsTunnel/StructureType/Layer/BridgeStructureType` and `path1.Tags`, only unioning way IDs
(`NodeBasedPathConnector.cs:500–560`). Partitioning before merge is by **highway type only**
(`GetHighwayGroup`), *not* by structure/layer — `AreTypesCompatible` is only a comment, not a real guard.

**However**, in **bridge mode** (`ExcludeBridgesFromTerrain=true`, which is exactly when we generate decks)
structure ways are **split out and never merged** — each becomes one spline 1:1 with metadata preserved
(`OsmGeometryProcessor.cs:805–871`, "protected structure paths (kept separate)"). First-path-wins tag loss
therefore only bites in legacy non-exclusion mode. **So bridge identity/tags are safe in the mode we ship.**

### GAPS for the multi-layer / typed-bridge goal

1. **No full tag bag on the spline.** Only `highway` survives as `OsmRoadType`; everything else
   (`bridge=` value like *viaduct/aqueduct/cantilever*, `bridge:movable`, `maxheight`, `maxweight`, `man_made`)
   is dropped after the feature stage. We keep `BridgeStructureType` (good) but **not the `bridge=`
   classification value** (collapsed to bool). To "model bridge types differently" we'd want that value.
   → **Fix (small, low-risk):** add `IReadOnlyDictionary<string,string>? OsmTags` to `RoadSpline` /
   `ParameterizedRoadSpline`, populate it from `PathWithMetadata.Tags` at spline creation
   (`OsmGeometryProcessor.cs` ~897/949). Optionally promote `bridge=` value to a `BridgeKind` field.
2. **Multi-way bridge = multiple splines.** A long bridge split across OSM ways yields several un-merged
   bridge splines. This is the same fragmentation behind the known "unchained bridge" fallback in the solver.
   For *typed/multi-layer* bridges we likely want to **group** structure splines that share endpoints +
   `layer` + `bridge`/`bridge:structure` into one logical bridge. → decision needed (see §6 D-2).
3. **`layer` is captured but only used (today) as a number, never to separate geometry or crossings.** It's
   the key we need in §2.

---

## 2. Roads under the bridge — are they identified?  (your Q2)

**Question:** a road crossing below a bridge — we should already have it as a mid-spline crossing. Verify.

### Verified findings

- Junction types (`NetworkJunction.cs`): `Endpoint, TJunction, YJunction, CrossRoads, Complex,
  **MidSplineCrossing**, Roundabout, Continuation`.
- `MidSplineCrossing` = two splines cross in XY where **neither terminates** and they **don't share an OSM
  node** — detected geometrically by sampling mid-spline cross-sections into a spatial index
  (`NetworkJunctionDetector.cs:644` `DetectMidSplineCrossings`). **So yes — a road under a bridge is found
  here.** Your intuition is correct.
- **BUT the detector is entirely layer/bridge-blind.** Grep of `NetworkJunctionDetector.cs` for
  `IsBridge`/`IsTunnel`/`.Layer`/`StructureType` → **zero hits** (only the `MidSplineCrossing` enum name).
  A bridge-over-road crossing is therefore created as a *real* `MidSplineCrossing` junction with both roads
  marked continuous — i.e. treated as if they meet at grade. This is a **latent bug** (documented long ago in
  `ai_docs/2026-03-25-segment-based-road-architecture-plan.md:747–763`: "false junctions at grade-separated
  crossings").
- A crossing junction does expose what we need to *fix* it: `Position` (XY), both `SplineId`s, each
  contributor's `CrossSection.TargetElevation`, and `Spline.IsBridge` / `Spline.Layer`.
- **No vertical-clearance concept exists anywhere.** `StructureElevationProfile.MinimumClearanceMeters` (5 m)
  is defined but was part of the retired write-only system; `BridgeProfileSolver.DiagnoseSeams` only looks at
  *endpoints*, never mid-span or under-crossings.

### GAPS

1. **Classify grade separation.** In crossing detection, when the two splines differ in `Layer` (or one
   `IsBridge` and sits above), do **not** emit an at-grade `MidSplineCrossing` junction — emit a new
   `GradeSeparatedCrossing` record instead (bridgeSplineId, roadSplineId, XY, each Z, layers). This both
   removes the latent false-junction bug **and** gives us the data the feature needs.
2. **Clearance = upperZ − lowerZ at the crossing point** (from the two solved cross-section elevations,
   interpolated to the exact XY). Store on the network (`List<GradeSeparatedCrossing>`).
3. **Edge cases to design for:** bridge over multiple roads; bridge over bridge (two `layer`s both >0);
   road over bridge (rare, but `layer` says who's up); crossing near a bridge endpoint (clearance there is
   ~0 by design — only enforce over the *span* interior).
4. **Bridges-without-roads-underneath** (your stated lower priority) need *nothing* here — they just keep the
   shipped behaviour. The crossing list is simply empty for them.

---

## 3. Priority rules — who goes under/over?  (your Q3)

**Question:** can our priority rules decide which road lowers/raises at the crossing?

### Verified findings

- **Priority cascade** (`ParameterizedRoadSpline.cs:191–278`): OSM class first (motorway 100, trunk 90,
  primary 80, secondary 75, tertiary 60, residential 55, service 45, track 30, path 25, …), else width-based
  (`width*5`, clamped 10–100), with material order as tiebreaker. Computed into `Priority` in
  `UnifiedRoadNetworkBuilder`.
- **Priority already arbitrates elevation at junctions:** `NetworkJunctionHarmonizer` uses "the highest-
  priority continuous road's elevation" at T/multi-way junctions; `PriorityAwareJunctionBankingCalculator`
  states the rule plainly: *higher-priority road maintains, lower-priority adapts*. So the machinery to say
  "this road yields to that one" exists and is battle-tested.
- **Caveat:** in bridge mode bridges are **excluded** from terrain smoothing and largely from harmonization
  (the deck Z comes from `BridgeProfileSolver`, not the junction harmonizer). So we **cannot** just let the
  existing junction step resolve a crossing — bridges aren't in it. The decision must be made in a **new
  grade-separation pass** that *reads* `Priority` but acts on the crossing list from §2.

### How priority maps to the under/over decision (proposed)

For a `GradeSeparatedCrossing`:
- The **bridge deck holds its solved elevation** (it already spans for grade/geometry reasons — that's the
  whole point of a bridge). It is effectively the "winner" regardless of class.
- The **road below must sit at least `clearance` under the deck** at the crossing. If the DEM-driven solved
  road elevation already clears → do nothing. If not → **push the lower road down** locally (a smooth dip),
  not the bridge up.
- **Where priority actually matters:** (a) **bridge-over-bridge / road-over-bridge** ambiguity — `layer`
  decides who's up; if `layer` is equal/missing, fall back to `Priority` (higher stays up). (b) Deciding
  whether it's acceptable to **regrade the lower road at all** — e.g. never dip a motorway to clear a service
  track; instead raise/keep the bridge. So: *layer first, priority as tiebreaker and as a veto on which road
  may be regraded.*
- This reuses the existing "lower-priority adapts" philosophy but in the **vertical-separation** direction
  (metres of drop), which the current junction code does **not** do (it only handles small same-grade
  adaptations via `Constrained*EdgeElevation`). Large drops are new.

### GAPS

1. New `GradeSeparationResolver` (reads crossing list + `Priority` + `Layer`, decides hold/dip/raise).
2. Ability to apply a **bounded vertical dip** to the lower road around the crossing (smooth, grade-limited
   but **no hard clamp** per standing feedback — ease it like the connector-grade ramp work).
3. Feed the result back: bridge deck min-Z at crossing becomes a **constraint** for `BridgeProfileSolver`
   (today it only fits endpoints; it would gain interior clearance constraints — §6 D-4).

---

## 4. How good is the DEM elevation already?  (your Q4)

**Question:** can we trust our elevation info (for clearance etc.)?

### Verified findings

- **DEM is the sole elevation source.** OSM provides no elevation; bridge endpoints come from connected-road
  (DEM-derived) Z. (`StructureElevationCalculator` has no OSM-elevation path.)
- **Sampling is nearest-neighbour**, integer pixel truncation, no bilinear, in the main road path
  (verified `OptimizedElevationSmoother.cs:119–125`). A bilinear helper exists but is **not** used by the road
  pipeline.
- **Longitudinal smoothing window ≈ 150 m** (301 samples × 0.5 m). This is the *same* low-pass that caused the
  deck-sag bug (plan 05 §1.1): over long spans the "terrain under the deck" is a blurred copy of itself.
- Vertical encoding: 16-bit, `world_Z = pixel/65535 · MaxHeight + TerrainBaseHeight` (base auto-set to GeoTIFF
  min). Quantisation step ≈ `MaxHeight/65535` (sub-cm for typical MaxHeight) — negligible vs the sampling/blur
  error. NoData → filled with min elevation (can fabricate pits).

### Assessment for bridge-height

- **Do not trust DEM-under-the-span for clearance to 0.5 m**, especially on long/deep crossings — the blur and
  nearest-neighbour noise are 1–several metres there. (This figure is an engineering estimate, not measured;
  treat as "needs a real check on a target map" rather than gospel.)
- **Do** trust the **road-vs-road solved elevations** at the crossing far more than terrain-under-span: both
  roads went through the same sampler+smoother, so their *relative* Z at a shared XY is consistent even if the
  absolute terrain is blurred. → **compute clearance road-vs-road, not deck-vs-terrain.**
- Keep a **deck-vs-terrain-under-span check as a diagnostic warning only** (reuse the parked
  `StructureElevationCalculator.SampleTerrainAlongStructure`, ideally upgraded to bilinear), to flag "deck
  likely intersects a hillside" for manual review — not to drive geometry.
- Optional future hardening: switch the road sampler to bilinear (the helper already exists) — modest accuracy
  win, but a broad change; out of scope for E unless clearance proves too noisy.

---

## 5. What this means for the bridge-height design

The original §4 of doc 06 framed E as mostly a **mesh** job (thickness, fascia, piers, abutment walls,
reopen the under-deck gap). The research says the **mesh is the easy half**; the half that makes "height"
*correct* is the **grade-separated-crossing pipeline** (§2+§3), which doesn't exist yet. Recommended framing:

- **E-A (logic): grade-separated crossings.** Detect+classify by `layer`/`IsBridge`, compute road-vs-road
  clearance, resolve under/over by layer+priority, apply a bounded dip to the lower road, feed an interior
  min-clearance constraint into `BridgeProfileSolver`. *This is the new, valuable, testable core.*
- **E-B (geometry): the 3D deck.** Deck thickness/soffit, side fascia/parapets, abutment walls, optional
  piers; reopen the real under-deck gap (the fascia hides the rasterised excavation edge that killed the gap
  before — see doc 06 §3). Extend `BridgeDeckDaeExporter` from a flat ribbon to a box+fascia mesh.
- **E-C (plumbing): tag bag.** Add `OsmTags` (+ maybe `BridgeKind` from `bridge=`) so E-B can pick mesh style
  by bridge type later. Cheap; do alongside E-B.

E-A and E-B are largely independent and can ship in either order, but **E-A first** gives the bigger
correctness win and de-risks the "reopen the gap" decision in E-B (you want clearance numbers before you cut
daylight under a deck).

---

## 6. Decisions — RATIFIED 2026-06-07

All six decided with the user (every "recommended" option chosen). This section is now the spec for E.

- **D-1 — Clearance source: ROAD-vs-ROAD solved Z.** ✅ Compute clearance from the two roads' solved
  cross-section elevations at the crossing point. DEM-under-span is a **diagnostic warning only** (the
  nearest-neighbour + ~150 m blur makes terrain-under-span untrustworthy on long/deep spans — §4).
- **D-2 — Multi-way bridges: KEEP PER-WAY for now.** ✅ Do **not** touch the merge pipeline for E. E-A crossing
  logic works per-spline. Revisit grouping (or chain-hardening) only if E-B typing actually needs one logical
  bridge. (Leaves §1 gap-2 and the unchained-bridge fallback as-is, deliberately, to unblock E-A.)
- **D-3a — Who moves: BRIDGE HOLDS, LOWER ROAD DIPS; `layer`→up, `Priority`→tiebreaker + VETO.** ✅ Bridge
  keeps its solved deck Z; the road below dips locally if it doesn't already clear. `layer` decides who is up;
  `Priority` breaks ties **and** vetoes dipping a high-class road (e.g. never dip a motorway under a minor
  bridge — instead keep/raise the bridge, see D-4).
- **D-3b — Default minimum clearance: 5.0 m**, exposed as a **UI tunable** like the Phase-0 knobs
  (`BridgeMaxSagBelowChordMeters` / `BridgeDeckUndercutMeters`). ✅
- **D-4 — Solver interior constraints: YES.** ✅ Add per-crossing minimum-Z constraints over the span to
  `BridgeProfileSolver.ApplyStructuralProfiles` (today endpoint-only); raise the deck via curve family / span
  to honour the D-3a veto case. **No grade clamps** (standing feedback) — ease the lower road's dip, don't cap
  grades.
- **D-5 — Reopen the under-deck gap in E-B.** ✅ When the 3D deck (thickness + fascia + abutment walls) lands,
  switch `BridgeDeckExcavator` from "shave-to-deck" to "clear to `deckZ − thickness − underClearance`" so
  there's real daylight under the span (fascia/walls hide the rasterised edge that killed the gap before).
  Keep shave-to-deck as the **no-height fallback**. (Not part of E-A.)
- **D-6 — Tag exposure: `OsmTags` bag only.** ✅ Add `IReadOnlyDictionary<string,string>? OsmTags` to
  `RoadSpline`/`ParameterizedRoadSpline`, populated from `PathWithMetadata.Tags` at spline creation. Downstream
  reads `spline.OsmTags["bridge"]`, `["maxheight"]`, etc. **Note:** E-A does not need it (uses `Layer` +
  `Priority`); this is E-C plumbing for E-B typing — schedule with E-B, not before E-A.

### Resulting build order (from the ratified decisions)

1. **E-A — grade-separated crossings (start here).** (a) In `DetectMidSplineCrossings`, when the two splines'
   `layer` differs (or one `IsBridge` sits above), emit a `GradeSeparatedCrossing` record instead of an
   at-grade `MidSplineCrossing` junction — fixes the latent false-junction bug *and* yields the data. (b)
   Compute road-vs-road clearance per crossing (D-1), store `List<GradeSeparatedCrossing>` on the network.
   (c) `GradeSeparationResolver`: apply D-3a (bridge holds; dip lower road with a bounded, eased dip; layer→up,
   priority veto). (d) Feed per-crossing min-Z into `BridgeProfileSolver` interior constraints (D-4). (e)
   `MinBridgeClearanceMeters` = 5.0 default, UI tunable (D-3b). Tests + a target-map render with a real
   bridge-over-road.
2. **E-B — 3D deck mesh:** thickness/soffit, fascia/parapets, abutment walls, optional piers; reopen the
   under-deck gap (D-5). Extend `BridgeDeckDaeExporter` (flat ribbon → box+fascia).
3. **E-C — `OsmTags` bag (D-6):** schedule alongside E-B (its first real consumer).

## 7. Pointers (verified)

- Tags/flags: `OsmGeometryProcessor.cs:805–871` (structure-path separation), `ParameterizedRoadSpline.cs:43,
  57,101,123,148`, `NodeBasedPathConnector.cs:500–560` (first-path-wins merge).
- Crossings: `NetworkJunctionDetector.cs:644` (`DetectMidSplineCrossings`, layer-blind), `NetworkJunction.cs`
  (types). Old bug note: `ai_docs/2026-03-25-segment-based-road-architecture-plan.md:747–763`.
- Priority: `ParameterizedRoadSpline.cs:191–278`, `NetworkJunctionHarmonizer.cs` (highest-priority-wins),
  `PriorityAwareJunctionBankingCalculator.cs` (maintain/adapt rule).
- DEM: `OptimizedElevationSmoother.cs:119–125` (nearest-neighbour), `:70` (≈150 m window),
  `GeoTiffReader.cs` (GDAL read, NoData), bilinear helper in `StructureElevationCalculator` (unused).
- Existing deck/solver/excavator: `BridgeProfileSolver.cs`, `BridgeDeckExcavator.cs`,
  `BridgeDeckDaeExporter.cs`; wiring in `TerrainCreator.cs` 3b-bridge block.
