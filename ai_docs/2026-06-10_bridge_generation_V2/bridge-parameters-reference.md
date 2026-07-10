# Bridge Parameters — Plain-English Reference

*What every bridge knob does, whether it is actually wired into the generator, and the hidden
default numbers baked into the code — all in everyday terms with small real-life examples.*

**Last verified:** 2026-07-08, by tracing every parameter to its read-site in code (not just its
definition). Each entry carries a **wired?** verdict:

- ✅ **Wired** — the value is read by pipeline code and changes the generated bridge/terrain.
- 🔶 **Detection-only** — it only writes a diagnostic log line; no geometry changes.
- ⚪ **Dormant** — the property exists (and may even be passed all the way through), but **no code
  reads it today**, so changing it does nothing. See the dedicated Part C.

## Changelog

**2026-07-08 — doc-17 collapse #2: obstacle typing unconditional, `Min Bridge Clearance` removed**
(branch `feature/bridge_embankment_containment`). The `EnableObstacleTyping` dev flag and the generic
`MinBridgeClearanceMeters` (5.0) are gone: every crossing now gets its typed budget
`ClearanceFor(kind) + structural depth` (road 4.7, rail 5/6, water 2/5.25, terrain 0). The resolver's
road-vs-road fallback moved 5.0 → 4.7; the legacy terrain-max-under-span raise path was deleted
(terrain is not an obstacle — the doc-20 floating-deck rule). Removed the two UI checkboxes/boxes
(Obstacle Typing + Min Bridge Clearance; **14 V2 checkboxes** now) and their state/param/preset
plumbing. **NOT byte-identical** — needs a render check (Manhattan + a steep map). Old presets still
import (unknown keys ignored). Tests: the decision-logic suites use a clean 5 m/no-depth clearance
helper so their arithmetic stays readable; typed values are covered by `BridgeObstacleTypingPlannerTests`.

**2026-07-08 — doc-17 collapse #1: removed the `Dip As Pin` flag** (branch
`feature/bridge_embankment_containment`). The standalone `EnableDipAsPin` toggle was strictly subsumed
by Sparse Floor Constraints — every consumption site OR'd the two (`EnableDipAsPin || EnableSparseDeckConstraints`),
so with the shipped sparse-on preset it was a no-op. Deleted the property, collapsed the three call
sites (resolver / junction blender / smoother) to `EnableSparseDeckConstraints` alone, removed the UI
checkbox (now **15** checkboxes), and re-pointed its tests to sparse (dropping the two legacy-off
contrast tests). Sparse now unconditionally owns the pre-smooth lower-road dip wells. `Graded Deck` was
**kept** — contrary to doc 17 §2's blanket claim, it is live under sparse (`sparse && graded` selects
`BuildUniformSoftPins`) and is part of the validated sparse+graded combo, so it can only go as part of
the sparse collapse itself.

**2026-07-08 — audit + cleanup sweep** (branch `feature/bridge_embankment_containment`). This doc was
rewritten from a read-site wiring audit, then the dead/duplicated surface it exposed was cleaned up:

- **Surfaced** the 5 missing Bridge Rule System V2 dev checkboxes — Natural Profile Anchor, Span
  Consolidation, Abutment Suppression, Deck-to-Deck Continuity, Seamless Deck Overlap (now **16**
  checkboxes total). *(commit `107396d`)*
- **Removed — dead UI param:** *Under-Deck Clearance (m)* (plumbed everywhere but read nowhere; the
  under-deck carve was rolled back). See Part C2. *(commit `107396d`)*
- **Removed — 3 never-built Phase-B flags:** `EnableEmbankmentStamping`, `EnableCuts`,
  `EnableAbutmentPlacement` (+ their `AnyEnabled` entries; this also fixed `AnyEnabled` spuriously
  tripping on a dead flag). See Part C1. *(commit `d961cd9`)*
- **Removed — 6 dead option numbers:** `SideSlopeRunPerRise`, `AbutmentMinDeckHeightMeters`,
  `AbutmentFillLengthMeters`, `AbutmentFillLateralFalloffMeters`, `RampDetectionLengthMeters`,
  `RampDetectionMinGradePct`; relabelled the emptied "Phase B stamping knobs" grouping. See Part C3.
- **Consolidated 2 duplicated constants** to a single source each (alias, value unchanged): the
  junction margin (`JunctionMarginMeters` canonical → `JunctionClearanceMarginMeters` alias) and
  `DefaultMinClearanceMeters` (resolver canonical → profile-solver alias). See B11. *(commit `08b16fd`)*
- **Fixed** the stale "(default 2.0)" caption + preset fallback on *Deck Thickness Max* (real default
  **1.2**); left the genuinely-2.0 Water-Freeboard and Structural-Depth-Max captions alone. See A4.
  *(commit `ba82c5f`)*

What remains in Part C is intentional: `EnableBridgeBridge` (detection-only, kept for the doc-16
bridge-over-bridge work) and the documentation-ghost note. The next planned step — collapsing the
flag/legacy A/B harness to the one validated code path — is specced in
[doc 17](17-de-legacy-hardcode-winning-combination.md) and gated on render validation of docs 14/15.

Sources of truth: [GenerateTerrain.razor](../../BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor)
(the UI), [GenerateTerrain.razor.cs](../../BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs)
and [TerrainGenerationState.cs](../../BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs)
(UI fields + defaults), [BridgeRuleSystemOptions.cs](../../BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs)
(V2 rule numbers), [BridgeDeckProfile.cs](../../BeamNG.Procedural3D/RoadMesh/BridgeDeckProfile.cs)
+ [BridgeDeckMeshBuilder.cs](../../BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs) (the 3D deck),
and the planner/solver/excavator classes under
[BeamNgTerrainPoc/Terrain](../../BeamNgTerrainPoc/Terrain).

---

## How to picture a generated bridge first

Before the parameters make sense, here is the anatomy of one of our bridges, in real-world words:

- **Deck** — the flat slab you drive on.
- **Soffit** — the underside of that slab (look up at a flyover; the soffit is the ceiling).
- **Deck thickness / structural depth** — how thick the slab + its hidden beams are, top to
  soffit. A long bridge needs deeper beams, like a longer shelf needs a thicker plank not to bow.
- **Clearance / headroom** — the gap between whatever passes *under* the bridge (a road, a
  railway, a river) and the soffit. This is the "max height 4.7 m" sign on a motorway underpass.
- **Span** — the distance the bridge bridges, end to end.
- **Abutment** — the solid block at each end of the bridge where it lands and meets the ground.
- **Embankment** — the earth ramp that climbs up to the abutment so the road can reach deck level.
- **Parapet** — the side wall / railing that stops a car going over the edge.
- **Approach ramp & grade** — the climbing road on each side; "grade" is its steepness in
  percent (6% = a 6 m climb over 100 m, which is steep for a motorway).

The generator's whole job is to decide, at every place a road crosses something: *do we lift the
bridge up, or push the lower thing down, and by how much, so there's enough headroom — without
making the road climb so steeply it feels wrong?* Most parameters below are levers on that decision
or on what the finished bridge looks like.

**The one big mental split:** parameters come in two families.

1. The **legacy deck-shape** knobs (Part A4) that shape the visible slab and the single generic
   clearance number — these work with everything off.
2. The **Bridge Rule System V2** rules (Part A2/A3) — newer, smarter, type-aware planning that you
   opt into one checkbox at a time. All V2 checkboxes default **OFF** = "behave exactly like before."

---

# Part A — Parameters visible in the UI

All live in the **"Bridge/Tunnel Structure Handling"** section of the *Generate Terrain* page.

## A1. Top-level switches — *all ✅ wired*

These flow from the page into `RoadSmoothingParameters` per road and gate the whole bridge path.

### Exclude Bridges — *checkbox, default OFF* · ✅
When ON, any road tagged as a bridge in OpenStreetMap is **left alone by the terrain** — the ground
is not raised or painted to match it, and the road instead becomes a lifted 3D deck mesh floating
above the landscape. When OFF, bridge roads are pressed into the terrain like ordinary roads.
*Real-world:* "don't bulldoze the valley to meet the viaduct — let the viaduct fly over it."

### Exclude Tunnels — *checkbox, default OFF* · ✅
Mirror of the above: when ON, tunnel roads don't carve or paint the **surface** above them, because
a tunnel passes *through* the hill, it doesn't reshape the hilltop.

### Merge Bridges Into Corridor — *checkbox, default ON* · ✅
When ON, a bridge isn't a separate little road segment; it's stitched into the road it belongs to so
the deck and the roads on either side share **one continuous centreline**. Removes the "stamped
rectangle" kink you'd otherwise see where the bridge meets the road. When OFF, the legacy
whole-spline exclusion path is used.
*Real-world:* the bridge is just part of the road, not a Lego piece dropped on top.

## A2. Bridge Rule System V2 checkboxes — *14 boxes, every one defaults OFF*

Each box opts into one improvement. Grouped below by what they touch. **13 of the 14 are fully
wired**; one (Bridge-over-Bridge Marker) only logs. Turn them on individually to trial each upgrade.

> **Obstacle typing is always on** (doc 17 §4a — no longer a checkbox): every bridge gets the right
> headroom for *what* it crosses (road ~4.7 m, electrified railway ~6 m, navigable river ~5.25 m,
> stream only ~2 m) plus the structural deck depth. Rail and water are never pushed down to "cheat" the
> gap. The type-aware clearance *values* live in A3.

**Planning / decision rules**

| Checkbox (UI label) | Wired? | In plain words |
|---|---|---|
| **Priority Distribution** | ✅ | Decides *who yields* when headroom is short. A motorway over a small street keeps its smooth line (bridge barely lifts) and the street dips instead — by importance, not 50/50. Also switches on the interchange "cluster one dip" logic. |
| **Ramp Feasibility** | ✅ | Reality-checks the plan: if the gap would force an impossibly steep ramp or too deep a trench, it backs off to the steepest *allowed* slope, then as a last resort accepts slightly reduced headroom and logs a warning. |
| **Span Solve Order** | ✅ | Plans the most important bridges first and remembers their finished height, so a flyover built *over* an already-raised bridge clears the **raised** deck, not the old ground. |
| **Early Elevation Estimate** | ✅ | Makes the planner read heights from a *smoothed* road line instead of raw ground samples — raw samples can accidentally read the steep embankment banks and misjudge a crossing. |

**Deck-shape / smoothing rules**

| Checkbox (UI label) | Wired? | In plain words |
|---|---|---|
| **Graded Deck** | ✅ | Lets a raised deck **follow the road's own slope** (a tilted straight line between the two ends) instead of one dead-flat level, so it touches down cleanly at both ends. *(Superseded by Sparse Floor Constraints when that is also on.)* |
| **Sparse Floor Constraints** | ✅ | The current best approach (doc 03 v3): the bridge is handed to the road smoother as *real road cross-sections* with **nothing hard-pinned**, so the joins at each end are seamless by construction. Headroom is enforced as gentle "humps" where a crossing needs one and as minimum-height "floors" mid-span. Also owns the pre-smooth lower-road dip wells (one gentle eased trough carved *before* smoothing, no late kinky re-cut — formerly the standalone "Dip As Pin" toggle). **Overrides Graded Deck** pinning while on. |
| **Pinned Deck Profile** | ✅ | Tells the later smoothing pass to **keep** the deck height the planner chose instead of re-curving it and letting a flyover sag back toward a straight line. |
| **Station Re-Projection** | ✅ | Puts each bridge back exactly where OpenStreetMap drew it. Without it, earlier math can let a bridge drift a few metres along the road from its true spot. |

**Interchange / dam-prevention rules (doc 09–13)**

| Checkbox (UI label) | Wired? | In plain words |
|---|---|---|
| **Natural Profile Anchor** | ✅ | Side roads hold their own natural height; a bridge's extra elevation can't "diffuse" outward and pile the terrain into a dam beside the ramp. Validated on Manhattan (over-height terrain pixels dropped ~95%). |
| **Span Consolidation** | ✅ | Joins a viaduct that OSM split into many pieces (alternating map "layers") back into **one deck with two real abutments**, instead of a fake abutment pair at every piece boundary. |
| **Abutment Suppression** | ✅ | Where one bridge's road runs straight onto *another* bridge's deck, don't stamp a ground abutment there — otherwise terrain gets shoved across the deck it merges into. *(Terrain-layer fix only.)* |
| **Deck-to-Deck Continuity** | ✅ | At a bridge→bridge merge, treat it like a T-junction: the through (trunk) deck wins, and the ramp's end is snapped onto the trunk deck's surface so there's no step where they meet. Best paired with Span Consolidation. |
| **Seamless Deck Overlap** | ✅ (needs Deck-to-Deck Continuity) | The follow-up that fixes the whole overlapping *area*, not just the join point: the ramp's overlapping part is laid flat exactly on the trunk deck (no z-fighting), parapets are opened where the two roadways fuse, and the mesh end-block is skipped at the merge. **Does nothing unless Deck-to-Deck Continuity is also on.** |

**Diagnostics**

| Checkbox (UI label) | Wired? | In plain words |
|---|---|---|
| **Bridge-over-Bridge Marker** | 🔶 detection-only | Just logs `[BRIDGE-BRIDGE]` where one bridge crosses another. It doesn't resolve anything yet — multi-level clearance is still an open task. |

**Dependencies to remember:** Seamless Deck Overlap needs Deck-to-Deck Continuity · Sparse Floor
Constraints overrides Graded Deck pinning and owns the lower-road dip wells · the coherent-underpass numbers (B9) only apply
when Priority Distribution is on. The last three interchange rules (Abutment Suppression,
Deck-to-Deck, Seamless Overlap) are deliberately *not* counted in the "any V2 rule on" gate — they
attach directly to the mesh/terrain step and are meaningless without merged spans.

## A3. Type-aware clearance & abutment number boxes — *V2, all ✅ wired*

These numeric fields (right below the checkboxes) feed the **Obstacle Typing** and abutment logic.
They were formerly code-only; they are now real UI fields. They only bite when the matching V2
checkbox is on (clearances need Obstacle Typing).

| UI field | Default | Wired? | Plain-English meaning + example |
|---|---|---|---|
| **Road Clearance (m)** | 4.70 | ✅ | Headroom kept over a road passing under the deck. The "max height" of a normal underpass. |
| **Rail Clearance electr. (m)** | 6.00 | ✅ | Headroom over an electrified line — extra room for the overhead power wires. |
| **Rail Clearance (m)** | 5.00 | ✅ | Headroom over a plain (non-electrified) railway. |
| **Navigable Water Clearance (m)** | 5.25 | ✅ | Gap above a boat-navigable river/canal so vessels fit under. |
| **Water Freeboard (m)** | 2.00 | ✅ | Gap above a small, non-navigable stream — enough to clear high water. |
| **Structural Depth Max (m)** | 2.0 | ✅ | Ceiling on how thick we *pretend* the beams are when measuring headroom (the clearance budget — **not** the visible slab, which has its own Max in A4). Kept low so we don't float the deck metres above where it visually sits. |
| **Abutment Overlap (m)** | 3.0 | ✅ | How far the terrain road stamp runs *onto* the deck end to seal the seam between dirt and deck mesh. |
| **Overlap Drop (m)** | 0.01 | ✅ | How far that overlap tongue sits *below* the deck (1 cm), so the deck stays the visible surface — no shimmer. |
| **Under-Deck Material** | *(auto)* | ✅ | Terrain material painted under each deck so grass/billboards can't grow through it. Defaults to a "dirt"-ish material; empty = no repaint. |

## A4. Legacy deck-shape number boxes — *work with V2 off*

Eight fields (page fields wrapping `TerrainGenerationState`) that shape the **look and geometry** of
the finished bridge. All are wired.

### Bridge Max Sag (m) — *default 1.0* · ✅
How far the middle of the deck may **droop below** a straight line between its two ends before we
flatten the curve back. Real bridges have a slight belly; this trades *flatter span but a sharper
kink at the ends* (low) against *more belly but a smoother join* (high). Grades are never clamped.

### Deck Undercut (m) — *default 0.05 (5 cm)* · ✅
When terrain pokes up above the deck, how far we shave it **below** the deck surface. Tiny on
purpose — just enough that the slab stays the visible surface and doesn't shimmer against the dirt.
*Real-world:* trim the grass so it sits a hair under the kerb, not over it.

### ~~Min Bridge Clearance (m)~~ — *removed 2026-07-08 (doc 17 §4a)*
The generic road-under-bridge headroom is gone. Obstacle typing is now unconditional, so the typed
**Road Clearance (A3, default 4.7)** is what a road under a bridge gets — plus the structural deck
depth. The resolver's road-vs-road fallback now reads that same 4.7 (was 5.0). Old presets still
import (the `minBridgeClearanceMeters` key is ignored).

### Bridge Deck Thickness Ratio — *default 0.05* · ✅
Visible slab thickness as a **fraction of span**: thickness = ratio × span, then clamped by Min/Max
below. Drives both the 3D mesh and the under-deck excavation.
*Real-world:* longer shelves need thicker planks — this is the rule of thumb for "how thick."

### Bridge Deck Thickness Min (m) — *default 0.45* · ✅
The **thinnest** the visible slab may be, so even short bridges look like they have real structure.

### Bridge Deck Thickness Max (m) — *default 1.2* · ✅
The **thickest** the visible slab may be, so long bridges don't grow a cartoonish slab. (The caption
and the preset-exporter fallback both read 1.2 as of 2026-07-08 — earlier builds showed a stale 2.0.)

### Bridge Parapet Height (m) — *default 0.9* · ✅
Height of the **side barrier / railing** along the deck edges. Set to **0** to build no parapets.
0.9 m is a typical crash-barrier wall height.

### Bridge Abutment Depth (m) — *default 1.0* · ✅
How far the solid **end block** drops below the soffit at each bridge end — the chunk the deck lands
on; it also stops terrain poking through at the ends. *(Only has effect while abutment blocks are
generated, which is the built-in default — see B6 `GenerateAbutments`.)*

---

# Part B — Hidden constants that DO shape bridges (wired, but no UI knob)

These are read by pipeline code every run and genuinely affect output; they just aren't user-tunable
without editing source. Grouped by what they affect. (Dormant/unused numbers are in Part C instead.)

## B1. Extra clearance values (V2 "Obstacle Typing") — ✅
*From `BridgeRuleSystemOptions.cs`. Used when Obstacle Typing is on; not surfaced in the UI.*

| Constant | Value | Plain-English meaning |
|---|---|---|
| `NavigableWaterWidthMeters` | **20 m** | A waterway this wide or wider counts as "navigable" (boats) → gets the bigger clearance. |
| `ReducedRoadClearanceMeters` | **4.20 m** | Last-resort smaller road headroom, used only when the proper gap is impossible, and logged. |

## B2. Structural-depth budget (V2) — ✅
*The **clearance-budget** depth — how thick we pretend the structure is when measuring the gap — not
the visible slab (A4/B6). Only the Max is surfaced (A3).*

| Constant | Value | Plain-English meaning |
|---|---|---|
| `StructuralDepthSpanDivisor` | **20** | Beam depth ≈ span ÷ 20. A 40 m span → ~2 m of beams (then clamped). Longer bridges, deeper beams. |
| `StructuralDepthMinMeters` | **0.45 m** | Never count less than this, even on tiny spans. |
| *(Max is the A3 "Structural Depth Max" box)* | 2.0 m | Never count more than this. |

## B3. How far a road underneath may be pushed down (V2) — ✅

| Constant | Value | Plain-English meaning |
|---|---|---|
| `MaxCutDepthMeters` | **4.0 m** | Normal limit on trenching the lower road for headroom — beyond this, drainage stops being believable. |
| `MaxCutDepthHardMeters` | **6.0 m** | Absolute trench limit, only under last-resort escalation. |

## B4. Approach-ramp steepness, by road class (V2 "Ramp Feasibility") — ✅
*Applies to **built approach ramps only**, never the natural through-road. "Normal" is the target;
"Absolute" is the steepest allowed before giving up and reducing headroom.*

| Road class | Normal max grade | Absolute max grade |
|---|---|---|
| Motorway / trunk | **4%** | **5%** |
| Primary | **5%** | **6%** |
| Secondary / tertiary | **6%** | **8%** |
| Residential / unclassified | **8%** | **10%** |
| Service / track | **10%** | **14%** |

*Real-world:* a motorway wants gentle 4% ramps; a farm track can get away with a steep 14% pitch.

## B5. Who yields when headroom is short (V2 "Priority Distribution") — ✅
*The fraction of the gap solved by **raising the deck** (the rest by dipping the lower road). "Δp" =
how much more important the upper road is than the lower.*

| Priority gap (upper − lower) | Raise share | What it means |
|---|---|---|
| +2 or more | **20%** | Upper far more important → barely lifts; lower road dips 80%. |
| +1 | **35%** | Upper somewhat more important. |
| 0 (equal) | **50%** | Split evenly. |
| −1 | **65%** | Lower road more important → the bridge does most of the lifting. |
| −2 or less | **80%** | Lower road far more important → the bridge lifts almost all of it. |

Road class comes from `ClassStepFor(...)`: motorway/trunk = 4, primary = 3, secondary/tertiary = 2,
residential/unclassified = 1, service/track = 0. `*_link` ramps inherit their parent's class, so a
motorway over its own slip road compares as equal (splits evenly).

The **legacy** split, used when Priority Distribution is off, is one fixed number:
`GradeSepSplitRatio` = **0.5** (50/50) in
[BridgeElevationPlan.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlan.cs). ✅

## B6. The visible 3D deck shape — ✅
*From [BridgeDeckProfile.cs](../../BeamNG.Procedural3D/RoadMesh/BridgeDeckProfile.cs), built by
[BridgeDeckMeshBuilder.cs](../../BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs).*

| Constant | Value | UI knob? | Plain-English meaning |
|---|---|---|---|
| `DeckThicknessSpanRatio` | **0.05** | yes (A4) | Visible slab thickness = 5% of span, clamped. |
| `DeckThicknessMinMeters` | **0.45 m** | yes (A4) | Thinnest the visible slab may be. |
| `DeckThicknessMaxMeters` | **1.2 m** | yes (A4) | Thickest the visible slab may be. |
| `ParapetHeightMeters` | **0.9 m** | yes (A4) | Side-barrier height; 0 = none. |
| `MinParapetThicknessMeters` | **0.4 m** (const) | no | Enforced minimum wall thickness at every height — the builder clamps both widths up to this, so no config can produce a paper-thin wall. |
| `ParapetBaseWidthMeters` | **0.45 m** | no | Width of the parapet at its **base** (thick bottom of the wall). Clamped to ≥ the top width. |
| `ParapetTopWidthMeters` | **0.40 m** | no | Literal wall thickness at the **top** — slightly narrower than the base, so the wall leans in (a trapezoid, like a real crash wall). Clamped to ≥ 0.4 m. |
| `GenerateAbutments` | **true** | no | Whether to build the solid end blocks at all. If ever set false, the A4 *Abutment Depth* box goes dormant (its only reader is the end-stamp). |
| `AbutmentDepthMeters` | **1.0 m** | yes (A4) | How far the end block drops below the soffit. |
| `EndStampLengthMeters` | **3.0 m** | no | How **long** (along the road) the solid end block is — the footprint where the deck lands. |

## B7. Carving the space under the deck (the excavator) — ✅
*From [BridgeDeckExcavator.cs](../../BeamNgTerrainPoc/Terrain/Export/BridgeDeckExcavator.cs).*

| Constant | Value | Plain-English meaning |
|---|---|---|
| `DefaultUndercutMeters` | **0.05 m** | Code twin of the UI "Deck Undercut" — how far terrain poking above the deck is shaved below it. |
| `DefaultEdgeMarginMeters` | **0.5 m** | Extra sideways reach beyond the deck edge when shaving, so the deck never overhangs an un-shaved lip. |
| lateral step | **max(0.25 m, ½ pixel)** | How finely the shaved strip is sampled sideways, so it has no gaps even on a coarse heightmap. |

## B8. How the deck's up-and-down curve is shaped (the profile solver) — ✅
*From [BridgeProfileSolver.cs](../../BeamNgTerrainPoc/Terrain/Export/BridgeProfileSolver.cs).*

| Constant | Value | Plain-English meaning |
|---|---|---|
| `DefaultMaxSagBelowChordMeters` | **1.0 m** | Code default behind the UI "Bridge Max Sag" (the UI value overrides it). |
| `DefaultGradeSampleLengthMeters` | **10 m** | How far back up the approach we look to read the slope the deck should *arrive* at, so deck and road meet without a kink. |
| `DefaultMaxProfileBulgeCapMeters` | **4 m** | Overshoot guard: if the curve bulges more than `min(¼ × span, 4 m)`, fall back to a gentler curve, then a straight chord. |
| arch-shape factor | **16·t²·(1−t)²** | The "hump" shape used to lift the deck for mid-span clearance — zero at both ends (joins stay seamless), peaks in the middle. |
| degenerate-span threshold | **0.01 m** | Spans shorter than 1 cm are left untouched rather than curved (avoids dividing by ~zero). |

## B9. Interchange "one dip for a cluster of crossings" (doc 28) — ✅
*Gated on **Priority Distribution**. Not surfaced in the UI.*

| Constant | Value | Plain-English meaning |
|---|---|---|
| `UnderpassClusterGapMeters` | **120 m** | Crossings on one road within 120 m of each other become **one** interchange dip instead of several fragmented wells. |
| `MaxUnderpassDipMeters` | **6.0 m** | Cap on that combined dip; over the cap it's cap-and-warn (residual under-clearance accepted, never turned into a bridge raise). |

## B10. Under-deck paint + overlap safety (docs 06/07) — ✅

| Constant | Value | Plain-English meaning |
|---|---|---|
| `UnderDeckPaintClearanceMeters` | **1.0 m** | Only terrain within 1 m below the deck gets repainted (B/A3 material); deep ravines/water under tall spans keep their natural look. |
| `AbutmentOverlapMaxLiftMeters` | **2.0 m** | The overlap tongue skips any cell it would have to lift more than 2 m — that's genuinely low ground under an elevated end, not a seam, and belongs to the excavator. |

## B11. Dip length + keeping clear of junctions — ✅
*From [BridgeElevationPlanner.cs](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs)
and [GradeSeparationResolver.cs](../../BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs).*

When a road dips under a bridge it eases into a long, gentle **trough** ("well") and back up:

| Constant | Value | Plain-English meaning |
|---|---|---|
| `DefaultDipRampLengthMeters` | **60 m** | **The main dip-length lever.** Half-length of the eased well: down over ~60 m, up over ~60 m (≈120 m trough). Longer = gentler. A plain code constant — the "exposable parameter" comment is aspirational; no per-run override is actually threaded. |
| `JunctionMarginMeters` (canonical) → `JunctionClearanceMarginMeters` (alias) | **2 m** (was 8) | One margin, **single source** since 2026-07-08: `JunctionMarginMeters` on the planner is the literal; the resolver's `JunctionClearanceMarginMeters` is a `const` alias of it (no more "keep in sync"). Planner sizes the dip well, resolver clamps the residual carve. The trough stops at least this far short of any junction so an intersection's levelled height isn't disturbed. If a junction sits right at the crossing, the dip is refused. |
| `FloorMinArchShape` | **0.25** | Mid-span clearance "floors" are enforced only in roughly the central 70% of the span — near the ends a floor would force a silly-tall central arch, so it's skipped. |
| `DefaultMinClearanceMeters` | **5.0 m** | Fallback headroom for a crossing the resolver is called on with no explicit clearance (its default arg). **Single source** since 2026-07-08: the literal lives on `GradeSeparationResolver`; the profile solver's same-named const is a `const` alias of it. In the real pipeline the resolver is now passed the **typed Road Clearance (4.7)**, not this default (doc 17 §4a). *(The old `BridgeElevationPlannerOptions.DefaultMinBridgeClearanceMeters` 5 m literal was removed with `MinBridgeClearanceMeters`.)* |

## B12. Internal numerical tolerances (not worth tuning)
Tiny "is this basically zero?" thresholds: `Eps` = **1e-3** (planner) / **1e-4** (solver); the
seam-kink measuring step = **min(0.5 m, 5% of span)**. Listed only for completeness.

---

# Part C — Parameters that exist but are NOT wired (do nothing today)

**This is the important "are they wired?" list.** Each of these appears in the options/UI but no
pipeline code reads it, so setting it has no effect. Don't rely on any of them.

## C1. Removed — dead V2 flags
The Phase-B stamping flags were placeholders whose features were never implemented (no UI, read
nowhere, no tests). **Removed 2026-07-08** from `BridgeRuleSystemOptions` (properties + the
`AnyEnabled` gate): `EnableEmbankmentStamping`, `EnableCuts`, `EnableAbutmentPlacement`. Old presets
carrying these keys still import fine (unknown keys are ignored).

*(What actually seals the terrain↔deck joint today is the doc-06 **Abutment Overlap** tongue in A3,
a different mechanism.)*

## C2. Removed
- **Under-Deck Clearance (m)** — was a dead UI box (plumbed to `BridgeDeckProfile` but never read;
  the under-deck carve was rolled back). **Removed 2026-07-08** — UI field, state/params properties,
  preset export/import, and the `BridgeDeckProfile.UnderDeckClearanceMeters` property all deleted.
  The terrain-shave under the deck is the *Deck Undercut* knob (A4).

## C3. Removed — dead option numbers
The tuning knobs for the never-built Phase-B features plus two diagnostic-only thresholds — all
defined but read nowhere (no consumers, no tests, no UI, not persisted in presets). **Removed
2026-07-08:**

- `SideSlopeRunPerRise` (was 1.5) — intended 1:1.5 batter for cut walls / embankments. *(from `BridgeRuleSystemOptions`)*
- `AbutmentMinDeckHeightMeters` (was 1.5 m) — intended "below this the deck is an embankment, not a bridge" (B3). *(from `BridgeRuleSystemOptions`)*
- `AbutmentFillLengthMeters` (was 8 m) — intended abutment fill-collar length (B1). *(from `BridgeRuleSystemOptions`)*
- `AbutmentFillLateralFalloffMeters` (was 4 m) — intended sideways feather of that fill (B1). *(from `BridgeRuleSystemOptions`)*
- `RampDetectionLengthMeters` (was 30 m) / `RampDetectionMinGradePct` (was 1.5%) — intended diagnostic ramp-grade check. *(from `BridgeElevationPlannerOptions`)*

Deleting `AbutmentMinDeckHeightMeters` emptied the "R8 / Phase B stamping knobs" grouping in
`BridgeRuleSystemOptions`; the remaining members there (the doc-06 overlap tongue + doc-07 under-deck
paint, all wired) were relabelled accordingly.

## C4. Documentation ghosts (removed from this doc)
A previous version of this reference listed **`SoftHumpSlope` (0.05), `SoftHumpMinMeters` (30),
`SoftHumpMaxMeters` (150)** as living in `BridgeElevationPlanner.cs`. **No such identifiers exist
anywhere in the code** — they were never real. The gentle-hump easing that *does* exist is the
`16·t²·(1−t)²` arch and the `(1−u)²(1+2u)` run-out fade in the profile solver (B8), sized from the
span and per-crossing clearance, not from named "SoftHump" constants.

## C5. Detection-only (works, but changes no geometry)
- **Bridge-over-Bridge Marker** (`EnableBridgeBridge`) — 🔶 logs `[BRIDGE-BRIDGE]` at bridge-over-bridge
  crossings. Useful for diagnostics; resolves nothing. Real bridge-over-bridge clearance is still an
  open task (see docs 09 §9.3 / 10 / 15).

---

## Quick mental model

- **A1 switches** decide whether bridges float or press into terrain, and whether they're stitched
  into the road.
- **A2 checkboxes** decide *which smart rules run* — 15 wired, 1 diagnostic; all off = legacy.
- **A3/A4 numbers** decide *how much headroom* (type-aware vs legacy) and *how the slab looks*.
- **B1–B5** are the "traffic engineering" numbers: headroom, ramp steepness, trench depth, who yields.
- **B6–B11** are the "what you see and how it's smooth": slab, railings, end blocks, the carve, the
  curve maths, the dip well.
- **Part C** is the junk drawer: knobs that look real but do nothing — check here before tuning
  anything that "isn't working."

If you change a wired B-value, regenerate `_generated_terrain` and eyeball it in-game — these numbers
trade off against each other (more headroom can mean steeper ramps; a flatter deck can mean a sharper
kink at the ends).
