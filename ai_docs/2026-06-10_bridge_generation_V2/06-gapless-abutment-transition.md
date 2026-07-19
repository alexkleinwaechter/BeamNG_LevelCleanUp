# 06 — Gap-free terrain ↔ bridge transition (abutment overlap)

**Date:** 2026-06-12, after render #12 ("Way better!" — elevations now correct; this doc is about the
JOINT). **Status:** analysis + ratified design direction (user: "the terrain must be forced to overlap
the bridge a bit and then excavated"), not yet implemented.

## 1. Symptom (render #12 screenshot, bridge 355-class short span)

At both abutments the deck mesh and the terrain do not meet cleanly:
- a visible GAP / hole right where the deck end cap meets the stamped approach road;
- a striped, near-vertical terrain face under each approach (1 m texels stretched over a ~3–5 m wall);
- the grass/terrain edge staircases along the deck sides (raster cells).

**Requirement (user, hard):** absolutely gap-free driving transition — racing/simulation use. A wheel
must never see a hole or lip at an abutment.

## 2. Why the gap exists (verified in code — it is NOT one bug)

1. **The stamped road simply ends at the exclusion boundary.** `RoadMaskBuilder` skips every
   `IsExcluded` cross-section in BOTH mask passes (`RoadMaskBuilder.cs:46`, `:110`) — span sections
   contribute nothing to the heightmap. The last terrain cell carrying road Z is the cell whose CENTER
   is nearest the last approach section; the deck mesh begins exactly at the first span station. Between
   them lies up to ~1 cell of no-man's land that gets only blended/IDW surroundings — typically LOWER
   than both road and deck → the hole at the cap.
2. **Raster quantization (the user's 1 m hypothesis — correct, but only half the story).** Cell centers
   are on a 1 m grid; the road surface is stamped per-cell. On a 5–8 % approach grade adjacent cells step
   5–8 cm; at the abutment the joint partner is a SMOOTH mesh, so the step shows as a lip/gap. No raster
   resolution fixes a butt-joint exactly — finite resolution always leaves a residual. **Two systems can
   only meet gap-free if they OVERLAP, never if they merely abut.**
3. **The excavator is (mostly) innocent — but it sharpens the edge.** `BridgeDeckExcavator` lowers every
   cell under the deck footprint above `deckZ − undercut` (default 0.05). Its footprint is the span
   stations; a cell straddling the abutment line can be cut even though it is the one cell that should
   carry the approach up to the cap. So: not the cause of the wall, but it can deepen the cap hole by
   `undercut` and must respect the overlap zone below.
4. **The striped wall is the missing embankment.** The stamp's lateral falloff ends at
   road-half-width + `TerrainAffectedRangeMeters`; beyond that the terrain falls to natural ground in
   ~1–2 cells → near-vertical face with stretched texels. That is Phase B-1's job (constant-slope
   embankment stamping, `SideSlopeRunPerRise` 1:1.5) — this doc's overlap zone is its abutment-end piece.
   (Doc-24 learning, kept from the reverted branch: terrain FILL removes the cliff — never a mesh apron;
   an apron just buries in the embankment.)

## 3. Design — "overlap, then excavate" (the user's proposal, made precise)

### 3.1 Abutment overlap stamp (the core)

New behaviour in the road-mask/stamping path: span cross-sections within `AbutmentOverlapMeters`
(default **3 m**, ≥ 3 cells) of each span END are stamped into the heightmap like road sections — at the
DECK profile Z minus a small `OverlapDropMeters` (default **0.03 m**) so the deck mesh stays the visible
and physical driving surface (no z-fighting), with the normal full width + lateral falloff. Result: the
terrain runs CONTINUOUSLY from the approach, under the deck end, dying out a few meters in. Any residual
raster step now lies UNDER the deck surface where no wheel ever touches it — that is the whole point of
overlap vs butt-joint.

### 3.2 Excavator exemption

`BridgeDeckExcavator` must not cut the overlap zone back down: per span, cells whose station is within
the overlap of either end keep a ceiling of `deckZ − OverlapDropMeters` (not `− undercut`); daylight
shaving starts only beyond the overlap. (Alternative: set `undercut = OverlapDropMeters` globally —
rejected, the mid-span daylight gap should stay independent.)

### 3.3 Longitudinal continuity of the stamp

The overlap stamp uses the SAME solved deck elevations the mesh uses (`network.BridgeSpans` snapshot —
post approach-raise, doc 04/05), so terrain and mesh agree by construction. The approach side needs no
change: its sections are already stamped; the overlap simply extends the stamped run across the
exclusion boundary so the per-cell interpolation between consecutive sections never sees a hole.

### 3.4 What this does NOT try to fix

- The striped embankment side walls away from the abutment = Phase B-1 (embankment stamping) proper.
- Mid-span under-deck daylight = excavator, unchanged.
- The deck mesh geometry (caps/parapets) is untouched — terrain comes UP to the deck, not mesh down.

## 4. Implementation sketch

1. `RoadMaskBuilder` (both mask passes): treat a span section as stampable when
   `DistanceToNearestSpanEnd(cs) <= AbutmentOverlapMeters`, with stamp Z = `cs.TargetElevation −
   OverlapDropMeters` (TargetElevation IS the deck Z after RefineSpans + ramps). Gate: sparse mode (or
   `MergeStructuresIntoCorridor`) — flag-off byte-identical. NOTE Phase-4 stamping runs BEFORE
   RefineSpans/ramps, so the overlap must be stamped (or re-stamped) post-solve — candidate slot: a
   small dedicated overlap-stamp pass next to the dip carve / ramp fill in TerrainCreator, which already
   mutate the heightmap post-solve with max-combine semantics.
2. `BridgeDeckExcavator.Excavate`: per cell, compute station via nearest span snapshot station; ceiling
   = `deckZ − (inOverlap ? OverlapDropMeters : undercutMeters)`.
3. Knobs on `BridgeRuleSystemOptions` (+ V2 UI block + preset round-trip is automatic):
   `AbutmentOverlapMeters` = 3, `AbutmentOverlapDropMeters` = 0.03.
4. Log: `[BRIDGE-OVERLAP] span=… end=start|end cells=… maxLift=…` + excavator exemption count.

## 5. Verification (render #13)

1. In-game: drive both abutments of 355/394/395 — no lip, no hole, no visible terrain gap at the caps.
2. Log: `[BRIDGE-OVERLAP]` lines per span end; excavator `cellsLowered` similar to #12 (exemption only
   removes a handful of cells).
3. The cap hole from §2.1 gone in a top-down terrain inspection (no below-road cells at the boundary).
4. Striped walls REDUCED at the abutment ends (full fix lands with Phase B-1).
