# Doc 13 — Bridge-to-bridge abutment suppression (spec, user-approved design)

**Date:** 2026-07-07 · **Status:** **VERIFIED** (commits `5aad25a`…`336697a`, 753 tests; regen `Log_…135439`, user: "This task seems to be solved.")

**Verification (run `135439`):** 34 span-ends suppressed — the full interchange map: 70↔14 handoff
from BOTH sides, span 2101591116 both ends (lands on 14 / on 1), 904452323 end→spline 2, ramp family
(26/33/51/52/54/56/60/462/464/465/553…). `junction fill skipped at 16 all-excluded deck junction(s)`
— the mid-span deck-Z pillars (j106 family) gone. `[BRIDGE-OVERLAP] spans` 44→35 (only ground
abutments still get tongues). Spike fixes steady at 574.
**Branch:** `feature/bridge_embankment_containment` · **Follow-up to:** doc 12 (divergence fix, verified)

**Implementation notes (delta vs plan):** §3.3's verification found the junction gap fill IS a real
fourth writer — `RoadMaskBuilder` painted a disk at `HarmonizedElevation` at every non-excluded
junction with no exclusion check on contributors; at deck-deck junctions (j106-style, mid-span)
that disk was a pure ground-to-deck pillar at deck Z — most likely the user's "mid bridge on merged
span" sighting. Fixed: fill skips junctions whose EVERY contributor section is excluded, opt-in via
the flag on any contributor spline (`[BRIDGE-B2B] junction fill skipped at N …` log; flag off =
legacy fill, test-proven). Everything else landed exactly as planned: detection helper
`BridgeToBridgeContinuity` (foreign-deck landing + same-spline neighbour), per-end exclusion shrink,
tongue skip, excavator exemption drop. Preset flag added. 9 new tests (Clone, 4 exclusion/detection,
stamper, excavator, 2 mask-fill).

## 1. Problem

User (2026-07-07, after doc-12 verification): *"If the road continuity is bridge to bridge we don't
want terrain terraforming meet the bridge corners."* Examples: `bridge_1546435469`, `bridge_904452323`.

Decoded (log `Log_TerrainGen_4096_20260707_131210`): span 904452323 is the ramp on spline 58,
≈[190, 797]. Its START sits at junction 103 ON the Brooklyn Bridge trunk deck (spline 14, span
281554390, station ≈438) and its END at junction 106 ON span 1546435469's deck (spline 2, station
≈2702). Both ends are deck-onto-deck merges, mid-air. Every span end today gets the full ground
abutment package from three writers:

1. `UnifiedRoadSmoother.MarkStructureExclusions` — shrinks the terrain exclusion by
   `AbutmentOverlapMeters` (3 m) at BOTH ends unconditionally (doc 06 v2). Those 3 m stay ordinary
   stamped road at deck Z → Phase 4 blends a ground-to-deck embankment pillar under a mid-air deck
   corner.
2. `BridgeAbutmentOverlapStamper` — stamps the raise-only tongue at both ends (capped
   `AbutmentOverlapMaxLiftMeters`).
3. `BridgeDeckExcavator` — the overlap exemption sets the excavation ceiling to `deckZ − drop` in
   those 3 m, PROTECTING the pillar from ever being cut back.

Within one spline, doc-10 consolidation removed internal joints (no same-spline gaps < 63 m on
Manhattan). The remaining "old bridge parts" are the SPLINE splits of one physical structure
(trunk/ramps): span ends landing on other decks still act as ground abutments.

## 2. Scope

IN: no terraforming at span ends that continue onto another bridge deck.
OUT (explicitly, next task): side STREETS still climbing to on-deck junctions (the +6…+11 m
FlattenSideRoadDams residual family — splines 156/148/262). Different mechanism (junction Z
transplant), separable verification.

## 3. Design (Approach A — user-picked over cross-spline consolidation (B) and post-hoc carve (C))

**Flag:** `BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression`, default **false** ⇒
byte-identical off. Preset `bridgeRules` node round-trips automatically; NO UI field; add
`true` to the Manhattan preset (`d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json`).

### 3.1 Detection — per span end, "does the road continue onto another deck?"

New helper (internal static, `UnifiedRoadSmoother` or a small `BridgeToBridgeContinuity` class)
runs inside `MarkStructureExclusions` BEFORE the per-segment marking loop (that method receives all
splines + `crossSectionsBySpline`; note other splines' `StructureSpanId` tags are NOT yet set at
this time — the index below must be built from segment RANGES, not from tags).

Index: for every spline, every cross-section whose `DistanceAlongSpline` falls inside an `IsBridge`
structure segment → (center point, `EffectiveRoadWidth/2`, owner spline id, span id). A span end of
spline X (the cross-section nearest `seg.Start/EndDistance`) is a **bridge-to-bridge continuation**
when either:

- **Lands on a foreign deck:** some indexed section of ANOTHER spline has
  `|center − endCenter| ≤ sectionHalfWidth + 1 m`. Strictly ON the deck, not beside it — parallel
  twin decks ~10 m apart must NOT suppress each other's true shore abutments. Covers ramp-onto-trunk
  (58→14, 58→2) and end-to-end spline handoffs (70→14).
- **Same-spline neighbour segment:** the spline's next/previous `IsBridge` segment starts within
  `2 × AbutmentOverlapMeters` of this end.

Result stored on `StructureSegment`: `bool StartContinuesOntoDeck` / `bool EndContinuesOntoDeck`
(copied by `Clone()`). One decision, three consumers. Per suppressed end log (file-only):
`[BRIDGE-B2B] span=<id> spline=<id> start|end lands on spline=<other> — abutment suppressed`.

### 3.2 Consumers

- `MarkStructureExclusions`: `overlap` becomes per-end `overlapStart`/`overlapEnd`; a suppressed end
  gets 0 → the 3 m stays `IsExcluded` → no mask stamp, no material, no embankment. Deck mesh
  unchanged (`StructureSpanId` stays range-wide).
- `BridgeAbutmentOverlapStamper.Stamp`: skip the start/end `StampRun` (incl. approach-node lookup)
  for a suppressed end. Segment looked up via owner spline's `StructureSegments` matching `SpanId`.
- `BridgeDeckExcavator.Excavate`: `inOverlap` (line ~89) applies per end only when that end is NOT
  suppressed → full `undercutMeters` right to the span end; residual terrain under the corner is cut
  like anywhere else under the deck.

### 3.3 Verification item (implementation-time check, fix only if real)

Phase-4 junction gap fill (`RoadMaskBuilder`, "Filled N junction gap pixels") must not paint a disk
at deck Z at deck-deck junctions (j103/j106). If it already skips fully-excluded contributors:
nothing to do; else gate disk pixels on exclusion.

## 4. Tests (TDD, red → green)

- `StructureExclusionMarkingTests`: ramp-lands-on-trunk fixture — suppressed end keeps the 3 m
  excluded; opposite ground end keeps today's shrink. Same-spline small-gap pair — both facing ends
  suppressed. Parallel-deck-beside (lateral ≈ 10 m, halfwidth 7.5) — NOT suppressed.
- `BridgeAbutmentOverlapTests`: no tongue cells at the suppressed end; ground end still stamped.
- Excavator: ceiling at suppressed end = `deckZ − undercut` (full), not `deckZ − drop`.
- Flag off ⇒ all existing tests byte-identical (flag default false).

## 5. Manhattan verification (regen)

`[BRIDGE-B2B]` lines for span 904452323 (both ends), the 70→14 handoff and peers; render: no terrain
pillars at the deck corners of bridge_904452323 / bridge_1546435469; true ground abutments unchanged
elsewhere; 744+ tests green.
