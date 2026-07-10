# Grade-Separated Crossings (E-A) — Follow-ups & Future Design Notes

**Date:** 2026-06-07
**Branch:** `feature/bridges`
**Reads with:** `07-bridge-height-spec.md` §6 (E-A spec, ratified D-1..D-6), and the shipped E-A code
(`NetworkJunctionDetector.DetectMidSplineCrossings`, `GradeSeparationResolver`, `BridgeProfileSolver`
interior constraints, `BridgeDeckExcavator`).

E-A shipped and works in-game (road-under-bridge now gets real vertical clearance). This doc records
**known rough edges** and **two future features** the user wants, so they aren't lost.

---

## 0. Current behaviour (E-A, as shipped)

For each grade-separated crossing the resolver, per D-3a:
- **bridge HOLDS its solved deck Z**; the **lower road DIPS** locally to make `MinBridgeClearanceMeters`
  (default 5 m, UI knob) of road-vs-road clearance — an eased two-sided well, carved into both the
  cross-sections AND the heightmap (so the driven terrain actually moves);
- **priority veto:** if the lower road outranks the bridge, it is NOT dipped — instead the deck is raised
  via an interior min-Z arch in `BridgeProfileSolver` (D-4).

## 1. Known rough edge — "trough under a flat bridge looks unnatural"

When the bridge approaches sit near the crossing-road level (flat terrain, deck effectively at grade), the
*only* way to get clearance today is to dig the road down by the full clearance. On an otherwise flat road
this reads as an abrupt valley under the overpass — geometrically correct, visually unnatural.

**Mitigations already in place / cheap:** longer dip ramp (gentler trough — see §3), junction-safe clamp.

---

## 2. FUTURE FEATURE A — "do both": split clearance between raising the deck and dipping the road

Today it's all-or-nothing: dip the road (default) OR raise the bridge (veto only). A more natural result
splits the required clearance: raise the deck a bit **and** dip the road a bit, so neither extreme is hit.

**Design sketch (reuses existing machinery):**
- Introduce a split ratio `r ∈ [0,1]` (e.g. `BridgeClearanceDeckShare`, default ~0.4): of the clearance
  shortfall `required = minClearance − naturalClearance`, raise the deck by `r·required` and dip the road by
  `(1−r)·required`.
- Deck raise: emit a `BridgeInteriorConstraint(bridge, station, naturalDeckZ + r·required)` for **every**
  crossing (not just veto), feeding the existing interior-arch lever in `ApplyStructuralProfiles` (D-4, no
  grade clamp). The arch already preserves G0+G1 at the abutments.
- Road dip: dip by `(1−r)·required` via the existing eased well.
- Priority still modulates `r`: veto (lower outranks bridge) → `r=1` (raise only); lower road much
  lower-class → `r` small (mostly dip). A smooth `r(priorityΔ)` curve is the natural generalization of the
  current binary veto.
- Two-phase ordering is unchanged: `PlanConstraints` (deck-raise share, before solver) →
  `ApplyStructuralProfiles` → `ApplyLowerRoadDips` (road-dip share, after solver, carve heightmap).
- Watch: raising the deck changes the deck's own clearance over *its* terrain (excavator) and its approach
  grades (the arch is interior-only so abutments are safe, but a tall arch over a long flat deck may look
  domed — bound it like the overshoot guard). Tune `r` so the deck arch stays modest.

This is the right "natural" fix for §1 and should be the next grade-separation work after E-B/E-C unblock
deck geometry.

---

## 3. FUTURE FEATURE B — OSM-context rules engine (what lies under the span)

The user wants to know **what OSM features lie under a bridge span** — not just crossing road splines, but
**lines and polygons**: waterways/riverbanks, railways, other roads, landuse (water, forest, residential),
buildings, barriers, etc. — and run a **rules engine** on that to decide the right structural treatment.

**Why:** the correct behaviour is context-dependent and today we only see road-vs-road crossings:
- over **water / river** → never dig a trough into the water; hold/raise the deck, leave the natural gap
  (this is what a real viaduct does). Likely wants `BridgeDeckExcavator` to *reopen* the gap (ties to D-5,
  E-B) rather than shave.
- over a **railway** → standard rail clearance (different, larger min-clearance than a road).
- over a **road** → clearance by the under-road's class (motorway clearance > track clearance).
- over **buildings / landuse** → maybe raise the deck (don't carve), or flag for review.
- a bridge over **nothing meaningful** (just terrain) → current shipped behaviour is fine.

**Design sketch:**
1. **Data:** needs the OSM feature geometry retained to terrain-local coords. We fetch ways/relations in
   `OverpassApiService`; `OsmGeoJsonParser` already holds full `OsmFeature.Tags` + geometry. Today only road
   splines survive to the network. → keep a lightweight spatial index of NON-road OSM features (polygons +
   polylines + their tags) through to the bridge stage, or re-query the parsed features. Pairs naturally
   with **E-C (`OsmTags` bag, D-6)** which already plumbs tags onto splines — extend the same plumbing to a
   feature index.
2. **Spatial query:** for each generated bridge span (its footprint polygon = deck centerline ± half-width
   over the span), find intersecting OSM features (point-in-polygon / segment-intersect against the index).
3. **Rules engine:** a small, declarative rule set keyed on the under-feature's tags →
   `(clearanceMeters, who-moves: dip|raise|both|leave-gap, excavate|reopen)`. Start with a hard-coded table
   (water, railway, highway-by-class, default), make it config/preset-driven later. Output feeds the same
   `GradeSeparationResolver` decision + `BridgeInteriorConstraint` / dip + excavator mode.
4. **Diagnostics:** log per span what was found under it and which rule fired (`[GRADE-SEP] span … under=water → leave-gap`).

This is a larger feature (OSM feature retention + spatial index + rules), but it's the "cool" general
solution: the road-vs-road clearance E-A shipped is just the first rule in that engine.

---

## 4. Ramp length (done now) + junction protection

- `GradeSeparationResolver.DefaultDipRampLengthMeters` raised **30 → 60 m** so the dip trough is gentler /
  more natural where there is room (§1 mitigation).
- **Junction protection (absolute no-go to break harmonization):** the dip well is clamped so it **eases to
  zero before any junction on the lower road** (endpoints, T/cross junctions, at-grade mid-spline crossings).
  Each junction station along the spline is found from `network.Junctions` contributors; the per-side limit =
  `min(rampLength, distanceToNearestJunctionOnThatSide − margin)`. **The well is SYMMETRIC: both sides use
  the nearer-junction limit (`min(back, fwd)`)** — an earlier asymmetric per-side clamp produced an ugly
  lopsided result (long-gentle one side, short-steep notch the other), which the user rejected. Symmetric
  stays junction-safe (each side still stops short of its own junction) and looks even. The well's
  `(1−u)²(1+2u)` weight is zero in value AND slope at `u=1`, so the road rejoins the harmonized profile
  tangentially. Where a junction is close BOTH sides shorten together (symmetric, steeper); if the nearer
  junction is within the margin the crossing declines to dip (`NoOpNoBridge`) rather than corrupt
  harmonization. The §2 "do both" feature is the real fix for the residual steepness.
- Not exposed as a UI knob yet; `MinBridgeClearanceMeters` is the only grade-separation knob today. Expose
  `DipRampLength` (and later `BridgeClearanceDeckShare`) when §2 lands.
