# Bridge Deck Cross-Section Research Corpus

**Date:** 2026-06-07
**Purpose:** Reference data on how real road bridge decks are shaped — in cross-section (deck slab, soffit, fascia, parapet/barrier) and longitudinally (span-to-depth, piers, abutments) — to inform sensible *default geometry* for a procedurally generated box-deck mesh in the BeamNG terrain pipeline. All figures are converted to metres. Each number is flagged as a **code minimum**, a **standard DOT detail**, or a **rule of thumb**, and cross-checked across sources where possible. This is engineering-reference data for *visual believability at driving speed*, not a structural design document.

---

## TL;DR — Recommended Default Numbers (for a v1 generated mesh)

| Parameter | Recommended default | Plausible range | Confidence / note |
|---|---|---|---|
| **Deck total thickness** (riding surface top → soffit underside) | **0.85 m** for short overpass; or scale as **~0.05 × span** | 0.45 m (slab) – 2.0 m (long girder span) | Medium. Span-driven; 0.85 m suits a typical ~17 m clear-span overpass. Riding slab alone is ~0.2 m; the rest is girder/structure depth. |
| **Riding slab thickness alone** (if modelling slab separately) | 0.22 m | 0.20 – 0.30 m | High. 8–10 in across US/UK DOTs. |
| **Fascia / edge-beam face height** (exposed vertical edge) | 0.45 m | 0.30 – 0.90 m | Low-medium. Equals deck edge thickness; visually it is "deck thickness you see from the side." |
| **Parapet / barrier height** (above deck) | **0.90 m** | 0.81 – 1.07 m (traffic); up to 1.4 m combo | High. Jersey 0.81 m; F-shape ~0.81 m; tall-wall / single-slope 1.07 m. |
| **Barrier base width** | 0.45 m | 0.40 – 0.61 m | High. ~0.61 m (24 in) for full Jersey; ~0.45 m for slimmer single-slope. |
| **Barrier top width** | 0.20 m | 0.15 – 0.25 m | Medium. |
| **Pedestrian rail height** | 1.07 m | 1.07 – 1.37 m | High. 42 in min ped; 54 in (1.37 m) if bike/equestrian. |
| **Abutment end wall height** (deck soffit → ground) | match local terrain gap; expose ~0.6 m, bury rest | seat exposure 0.6 m, full-height = whatever the gap is | Medium. Seat-type is the common overpass case. |
| **Pier column diameter** | 1.0 m (round) | 0.6 – 1.5 m | High. 24–48 in standard; 36 in (0.9 m) most common. |
| **Pier needed when span exceeds** | ~**35–40 m** | 25 m (slab) – 60 m (deep girder) | Medium. Single simple span rarely exceeds ~45 m without prestress; multi-span overpasses pier every 20–40 m. |
| **Deck cross slope (crown)** | 2.0% | 1.5 – 2.5% (NC); up to ~8% superelevated | High. Already handled by banking in pipeline. |

> **One-line v1 recommendation:** A box deck **0.85 m thick** with a vertical **fascia face = deck thickness**, a simple **0.9 m parapet** of base 0.45 m / top 0.2 m on each edge, **seat-type abutment end walls** closing the soffit-to-ground gap, and **optional 1.0 m round piers** only when a span exceeds ~35 m — gives a believable overpass at driving distance/speed.

---

## 1. Deck slab thickness / structural depth vs span

Two distinct numbers matter and are often conflated:

- **Riding slab thickness** — the concrete deck slab itself (the bit you drive on). ~0.2–0.3 m.
- **Total structural depth** — top of riding surface to the *underside* (soffit) of the superstructure, i.e. slab + girders. This is what determines visible "deck thickness" from the side and underneath. Driven by span via span-to-depth ratios.

### 1a. Riding slab thickness (the slab alone)

| Source / standard | Slab thickness | Metric |
|---|---|---|
| TxDOT standard deck slab on I-girders | 8.5 in (thinner not permitted) | **0.216 m** |
| IDOT standard slab | 8 in | 0.203 m |
| UK / SteelConstruction.info composite deck | 250 mm | 0.250 m |
| General US practice range | 8–12 in | 0.20–0.30 m |

Cross-check: all sources cluster **0.20–0.25 m** for the structural riding slab. Use **0.22 m** if modelling the slab as a separate layer.

### 1b. AASHTO LRFD Table 2.5.2.6.3-1 — Traditional Minimum Depths for Constant-Depth Superstructures

These are **code minimums** ("may be used in the absence of other criteria"), with *L* = span length (centre-to-centre of supports). Real bridges are usually built 5–15% deeper than these minimums for economy. Confirmed/cross-checked across the steel-girder source (Mead & Hunt) and the AASHTO-derived concrete values (Midas + DOT guides):

| Superstructure type | Simple span | Continuous span |
|---|---|---|
| **Slab** (main reinf. ∥ traffic), min total depth | (S + 3000)/30 mm ≈ **(S + 10 ft)/30** [≥ 0.165 m] | (S + 3000)/30, ×0.9 for cont. |
| Reinforced-concrete **T-beam** | 0.070 L | 0.065 L |
| Reinforced-concrete **box beam** | 0.060 L | 0.055 L |
| Prestressed **precast I-beam** | 0.045 L | 0.040 L |
| Prestressed **CIP box beam** | 0.045 L | 0.040 L |
| Prestressed **adjacent box beam** | 0.030 L | 0.025 L |
| **Steel** composite I-beam — *overall* depth | 0.040 L | 0.032 L |
| **Steel** composite I-beam — *steel portion* only | 0.033 L | 0.027 L |
| **Trusses** | 0.100 L | 0.100 L |

**Worked examples (total structural depth at the minimum):**

| Clear span | Prestressed I-beam (0.045 L) | Steel composite (0.040 L) | RC T-beam (0.070 L) |
|---|---|---|---|
| 12 m | 0.54 m | 0.48 m | 0.84 m |
| 20 m | 0.90 m | 0.80 m | 1.40 m |
| 30 m | 1.35 m | 1.20 m | 2.10 m |
| 40 m | 1.80 m | 1.60 m | — (impractical) |

**Box girder (CIP post-tensioned), Caltrans rule of thumb:** depth ≈ **0.045 × span (simple)** / **0.04 × span (continuous)**; range 0.04–0.05. This is the workhorse for California overpasses. A 30 m continuous box span → ~1.2 m deep.

**Takeaway for the generator:** a single coefficient **structural depth ≈ 0.05 × span** captures most short/medium overpasses well (slightly conservative/deep, which reads as believable). Clamp to a sensible minimum (~0.45 m) for very short spans so the deck never looks like paper.

---

## 2. Soffit (underside) shape & a defensible default deck thickness

- **Flat solid slab:** soffit is flat and parallel to the deck — the whole deck is one slab. Cleanest to model. Underside thickness = structural depth (~0.5 m for a short slab-bridge span).
- **Slab-on-girder:** soffit is the bottom flange line of the girders; the real underside is ribbed, but at driving distance a **flat box soffit at the girder-bottom elevation** is indistinguishable.
- **Box girder:** soffit is a flat closed underside (the box bottom slab) — visually a flat box, ideal for a simple mesh.

For a **simplified game mesh**, treat the deck as a single solid box whose **thickness = total structural depth**. A flat soffit is correct-looking for slab and box bridges and "close enough" for girder bridges.

**Defensible default deck thickness for a short-to-medium overpass:**

| Case | Thickness | Basis |
|---|---|---|
| Very short slab overpass (≤12 m) | 0.45–0.55 m | slab min depth + parapet curb |
| Typical highway overpass (15–25 m, box/girder) | **0.85 m** | 0.045 × ~19 m span |
| Medium span (25–40 m) | 1.2–1.8 m | 0.045 × span |

**Default = 0.85 m**, range **0.45–2.0 m**, scaled by span where span is known.

---

## 3. Fascia / edge beam

The **fascia** is the exposed vertical (or near-vertical) outer face of the deck edge — the band you see from the side. Its height equals the deck-edge thickness (edge beam / overhang depth), typically a bit less than mid-span structural depth because the overhang is thinner than the girder zone.

| Item | Typical | Metric |
|---|---|---|
| Deck overhang / edge thickness | 8–12 in at tip, thickening to ~12–18 in at girder | 0.20–0.45 m |
| Visible fascia band (edge of slab + edge beam) | 1–3 ft | **0.3–0.9 m** |

For the mesh, the **fascia is simply the side face of the box deck** — no separate parameter strictly needed. If a distinct fascia band is wanted, **0.45 m** is a good default exposed edge.

---

## 4. Parapet / barrier / railing

This is the most visually important feature at driving speed — the thing the driver sees beside the lane. Numbers are well-standardised.

### 4a. Concrete traffic barriers (cross-section)

| Barrier | Height | Base width | Top width | Notes |
|---|---|---|---|---|
| **New Jersey** shape | 32 in = **0.81 m** | 24 in = 0.61 m | ~6 in = 0.15 m | Slope-break ("toe") at 13 in (0.33 m); lower face 55° (1:1.4). |
| **F-shape** | ~32 in = **0.81 m** | 24 in = 0.61 m | ~6 in | Same family; slope-break lower, at 10 in (0.25 m) — less vehicle climb. |
| **Single-slope / constant-slope** | tapers to **42 in = 1.07 m** | 24 in = 0.61 m | ~8–9 in | Constant face angle ~9–11° from vertical (TX/CA styles); good for resurfacing. |
| **Ontario tall wall** | 42 in = **1.07 m** | — | — | Jersey profile, taller. |
| **Vertical (straight) parapet** | varies 0.81–1.07 m | ~0.30–0.45 m | similar to base | Simplest profile; flat vertical face. |

Cross-check: Jersey **0.81 m ± 0.01 m** height, **0.61 m base** confirmed (jjhooks, Jackwin, Wikipedia, NYSDOT). F-shape shares the Jersey footprint, differing only in the lower slope-break height.

### 4b. Vehicle restraint / containment-level heights (Eurocode/Austroads framing)

| Containment level | Typical barrier height |
|---|---|
| Low / normal containment | 0.8–0.9 m |
| Medium containment | 0.9–1.0 m |
| High containment (heavy vehicle) | 1.0–1.4 m |

### 4c. Pedestrian / bicycle railings

| Use | Min height | Metric |
|---|---|---|
| Pedestrian-only rail | 42 in | **1.07 m** |
| Combination traffic + pedestrian | 42 in ped side | 1.07 m |
| Bicycle rail (added) | 48 in (preferred) / 54 in | 1.22 / **1.37 m** |
| Equestrian | 54 in | 1.37 m |
| Max opening between rails | 4–6 in | 0.10–0.15 m |

**Mesh recommendation:** model a single **0.9 m solid parapet** (base 0.45 m, top 0.2 m) per edge as a trapezoidal extrusion. This reads as a concrete Jersey/F-shape barrier. Bump to **1.1 m** when a pedestrian way is present.

---

## 5. Abutment walls

The abutment is the end support; for the generator we only need a wall that closes the gap between the deck soffit and the embankment/ground.

**Two archetypes:**

- **Seat-type (stub) abutment** — short wall sitting on top of the embankment fill; the embankment slopes up underneath the deck end. Most common for grade-separation **overpasses**. Exposed stem is short. *Examples:* MnDOT prefers stem exposure ~0.6 m (2 ft) on the low side; CDOT requires ≥0.6 m (2 ft) from top of embankment to girder bottom, bearing embedded ≥0.45 m (1.5 ft).
- **Full-height abutment** — tall retaining wall holding back the whole embankment, deck bears at the top. Used where there's no room for a slope (urban, over water). Wall height = full soffit-to-ground gap.

**Wingwalls** flank the abutment to retain the side fill — modelled as angled or perpendicular wall flaps; not critical for v1.

**Mesh recommendation:** at each bridge end, drop a **vertical end wall** from the deck soffit down to the terrain heightmap, filling the gap. Default to **seat-type behaviour**: let the terrain ramp up under the deck (the existing dip/ramp logic already shapes the embankment), and only expose ~0.6 m of wall above grade. Add short **wingwall flaps** at ±45° later if desired.

---

## 6. Piers / columns (intermediate supports)

| Item | Typical value | Metric |
|---|---|---|
| Round column diameter (standard menu) | 30 / 36 / 42 / 48 in | 0.76 / **0.91** / 1.07 / 1.22 m |
| Common default diameter | 36 in | **0.9 m** |
| Larger (deep girder / Tx62–70) | 42 in | 1.07 m |
| Min column spacing, multi-column bent | 16 ft | 4.9 m |
| Column height limit | ≤12 × dia | e.g. 0.9 m dia → ≤11 m tall |
| Pier cap width | column dia + 24 in (CA) / +3 in min (IA) | dia + 0.6 m |
| Single-column vs multi-column | single for narrow decks/stream crossings; multi-column bent (2–4 columns) for wide decks | — |

**When does a span realistically need a pier?**
- Single simple spans rarely exceed ~**40–45 m** without going to deep prestressed/steel girders.
- Typical multi-span highway overpasses place piers every **20–40 m**.
- **Heuristic for generator:** if total bridge length > ~**40 m**, insert intermediate pier(s) so no span exceeds ~35–40 m. Below that, a single clear span needs no pier. Slab-only bridges top out much shorter (~12–18 m per span).

**Mesh recommendation:** piers are **optional for v1**. When enabled, a single **0.9–1.0 m round column** (or a 2-column bent for wide decks) with a simple **pier cap box** (deck-width × ~1.0 m deep × ~1.5 m wide) dropping to terrain is sufficient. Only spawn when a span exceeds the ~35–40 m threshold.

---

## 7. Cross slope / crown

| Condition | Cross slope |
|---|---|
| Normal crown (NC), no superelevation | **2.0%** each side of crown |
| Acceptable range (drainage) | 1.5–2.5% |
| Superelevated (curves) | up to ~6–8% one-way |

Confirmed (Iowa DOT, WSDOT, TxDOT, MDOT): **2% normal crown** is the universal default; constant cross slope preferred across a bridge's length. **Already handled by the pipeline's banking logic** — noted here only for completeness; the deck mesh should inherit the road's existing banking/crown rather than impose its own.

---

## 8. OSM `bridge=` classification → structural form

For future mesh-style switching. From the OSM `Key:bridge` wiki (definitions verbatim where quoted), plus implied structural form:

| `bridge=` value | OSM meaning | Implied structural form (mesh hint) |
|---|---|---|
| `yes` | Generic bridge, unspecified | Default box deck |
| `viaduct` | Series of spans, each short relative to total length | **Multi-span box deck + regular piers** (the prime "needs piers" case) |
| `aqueduct` | Carries a canal / fresh water | Trough/channel deck (rare for roads) |
| `trestle` | Series of short spans on rigid frames | Many short spans, **frequent slender piers/bents** |
| `cantilever` | Span supported at one end only | Cantilever arm — specialised; box deck OK as approximation |
| `cable-stayed` | (discouraged value; use `bridge:structure=cable-stayed`) | Deck + pylon + cables — special mesh |
| `suspension` | (via `bridge:structure=suspension`) | Deck + towers + main cables — special mesh |
| `movable` | A span can move (bascule/swing/lift) | Box deck; ignore the mechanism for terrain |
| `covered` | Has a roof / enclosed sides | Box deck + roof (rare) |
| `boardwalk` | Plank walkway over wet/difficult terrain, low to ground | Thin low deck, many small posts |
| `low_water_crossing` | Low bridge carrying vehicles above low-flow water | Thin slab near grade |

`bridge:structure=*` carries the engineering form (**arch, beam, truss, cable-stayed, suspension, floating, simple-suspension**) and `bridge:support=*` carries the support type. For v1, treat everything as a box deck; the only value worth branching on early is **`viaduct`/`trestle` → force intermediate piers**.

---

## Implications for a simplified game mesh

**What matters visually at driving distance/speed (model these):**
1. **Parapet/barrier** — the dominant beside-lane feature. A ~0.9 m trapezoidal solid barrier per edge is essential; without it the deck reads as a bare slab and looks wrong.
2. **Deck side face / fascia** — the visible edge band and the apparent thickness. Getting the **thickness roughly span-proportional (~0.05 × span)** sells the structure. A 0.1 m wafer over a 30 m span looks fake; a 0.85–1.5 m box looks right.
3. **Abutment end walls** — closing the soffit-to-ground gap stops the "floating bridge" / see-through-the-end artifact.
4. **Soffit** — only seen when driving *under* the bridge (grade separation). A **flat box underside** is enough.

**What can be coarse / deferred:**
- Girder ribbing under the soffit (flat box is fine).
- Wingwalls (nice-to-have; abutment end wall alone is acceptable).
- Pier caps / bent details (a plain box cap is fine; piers themselves optional).
- Barrier profile subtleties (Jersey vs F vs single-slope) — a single trapezoid covers all.
- Cross slope — inherited from existing road banking.

**Suggested minimal-but-believable v1 parameter set (box deck):**

| Parameter | v1 value |
|---|---|
| `DeckThicknessMeters` | `clamp(0.05 × spanLength, 0.45, 2.0)` |
| `FasciaIsDeckEdge` | true (side face = deck thickness; no separate band) |
| `ParapetHeightMeters` | 0.9 (1.1 if pedestrian) |
| `ParapetBaseWidthMeters` | 0.45 |
| `ParapetTopWidthMeters` | 0.20 |
| `AbutmentEndWalls` | true (drop vertical wall soffit → terrain at each end) |
| `CrossSlope` | inherit from road banking |
| `GeneratePiers` | optional; only if any span > ~35 m |
| `PierColumnDiameterMeters` | 1.0 (round); 2-column bent if deck wider than ~10 m |
| `PierSpacingTargetMeters` | ≤ ~35–40 m |

This is a box deck + fascia (implicit) + simple parapet + abutment end walls, with piers as an opt-in — matching the v1 scope, all numbers grounded in DOT/AASHTO practice above.

---

## Sources

- [OpenStreetMap Wiki — Key:bridge](https://wiki.openstreetmap.org/wiki/Key:bridge) — Canonical list/definitions of `bridge=*` values (viaduct, trestle, cantilever, movable, aqueduct, boardwalk, etc.) and pointer to `bridge:structure=*`.
- [Midas Civil — Bridge Span According to AASHTO LRFD](https://resource.midasuser.com/en/blog/bridge/bridgeinsight/bridge-span-according-to-aashto-lrfd) — Discussion of AASHTO LRFD Table 2.5.2.6.3-1 traditional minimum depths and span-to-depth concepts.
- [Mead & Hunt — Designing Steel Plate Girder Bridges](https://meadhunt.com/designing-steel-plate-girder-bridges/) — Steel composite span-to-depth ratios: 0.040 L / 0.033 L (simple), 0.032 L / 0.027 L (continuous); note designs run 5–15% deeper than minimum.
- [SteelConstruction.info — Bridges: initial design](https://steelconstruction.info/Bridges_-_initial_design) — UK practice: 250 mm composite deck slab; ~4 m max slab span between girders.
- [TxDOT Bridge Design Manual — Concrete Deck Slabs on I-Girders (Ch.3 §2)](https://www.txdot.gov/manuals/brg/lrf/chapter-3--superstructure-design/section-2--concrete-deck-slabs-on-i-girders--u-bea.html) — 8.5 in standard deck slab; 10 ft max girder spacing; overhang limits (≤1.3 × girder depth, ≤6 in past flange).
- [IDOT — LRFD Slab Bridge Design Guide (bm-3.2.11)](https://idot.illinois.gov/content/dam/soi/en/web/idot/documents/doing-business/memorandums-and-letters/highways/bridges/bm-design-guides/bm-3.2.11-lrfd-slab-bridge-design.pdf) — 8 in standard slab; cites AASHTO Table 2.5.2.6.3-1 slab minimum depth.
- [Caltrans Bridge Design Practice Ch.5.4 / Box Girder docs](https://dot.ca.gov/-/media/dot-media/programs/engineering/documents/bridge-design-practices/202210bdpchapter54precastpretensionedboxgirdera11y.pdf) — Box-girder depth-to-span ≈ 0.045–0.05 (simple) / 0.04–0.045 (continuous); Caltrans prefers ≥0.045 simple, ≥0.04 continuous.
- [jjhooks (Easi-Set) — Barrier Profiles/Radius](https://jjhooks.com/technical/barrier-profiles-radius) — Jersey / F-shape / constant-slope barrier families and curve-radius tables.
- [Jackwin Safety — Jersey Barrier Dimensions](https://jackwinsafety.com/jersey-barrier-dimensions/) — Jersey barrier height 810 mm ±10, base 600 mm ±5, front slope 55° (1:1.4).
- [Wikipedia — Jersey barrier](https://en.wikipedia.org/wiki/Jersey_barrier) — 32 in (0.81 m) standard; Ontario Tall Wall 42 in (1.07 m); F-shape and constant-slope variants.
- [NYSDOT — RC Concrete Traffic Barriers detail sheets](https://www.dot.ny.gov/main/business-center/engineering/cadd-info/drawings/bridge-detail-sheets-usc/rc-concrete-traffic-barriers-usc) — Standard concrete barrier/parapet detail dimensions.
- [Arete Structures — Guide to AASHTO Pedestrian Bridge Standards](https://aretestructures.com/a-brief-guide-to-aashto-pedestrian-bridge-standards/) — Pedestrian rail 42 in min; 54 in if bicycle/equestrian; 4–6 in max opening.
- [Iowa DOT LRFD Bridge Design Manual — Railings](https://iowadot.gov/media/4645/download?inline=) — Combination ped/bike rail heights (42 in ped, 48 in preferred bike).
- [TxDOT Bridge Design Manual — Columns for Multi-Column Bents (Ch.4 §7)](https://www.txdot.gov/manuals/brg/lrf/chapter-4--substructure-design/section-7--columns-for-multi-column-bents.html) — Column sizing by superstructure type: 24 in (stream), 36 in (grade), 42 in (Tx62–70).
- [Iowa DOT LRFD Bridge Design Manual §6.6 (Piers)](https://iowadot.gov/media/4666/download?inline) — Standard round column dia 30/36/42/48 in; min column spacing 16 ft; pier cap ≥ column +3 in.
- [Caltrans Bridge Design Practice Ch.5.6 — Concrete Bent Caps](https://dot.ca.gov/-/media/dot-media/programs/engineering/documents/bridge-design-practices/202210bdpchapter56concrete-bent-capsa11y.pdf) — Min bent cap width = column width + 24 in.
- [MnDOT LRFD Bridge Design Manual §11 (Abutments)](https://www.dot.state.mn.us/bridge/pdf/lrfdmanual/section11.pdf) — Preferred low-side abutment stem 5 ft (3 ft buried, 2 ft exposed); seat-type guidance.
- [CDOT Bridge Design Manual §11](https://www.codot.gov/programs/bridge/bridge-manuals/design_manual/bdm_section-11_2025.pdf) — Seat-type abutment: bearing embedded ≥1.5 ft; ≥2 ft from embankment top to girder bottom.
- [WisDOT Bridge Manual Ch.12 (Abutments)](https://wisconsindot.gov/dtsdManuals/strct/manuals/bridge/ch12.pdf) — Seat vs full-height abutment configurations, wingwalls.
- [Iowa DOT LRFD Bridge Design Manual §5.2 / WSDOT Design Manual Ch.1250](https://wsdot.wa.gov/publications/manuals/fulltext/m22-01/1250.pdf) — Normal crown 2%; cross slope & superelevation standards.
- [NCDOT Structure Design Manual FIG067/FIG066 — Prestressed Girder Dimensions](https://connect.ncdot.gov/resources/Structures/Structure%20Design%20Manual/FIG067%20Dimensions,%20Area,%20and%20Design%20Data%20for%20Prestressed%20Concrete%20Girders%20AASHTO%20Types%20V%20and%20VI,%20Modified%20Bulb%20Tees.pdf) — AASHTO Type IV = 54 in deep; Types V & VI = 72 in; bulb-tee 54–72 in, max span ~43.8 m.

---

### Confidence & disagreements log
- **AASHTO Table 2.5.2.6.3-1 values** (0.045 L PSC I-beam, 0.040 L steel composite, 0.070 L RC T-beam, etc.): cross-checked across Mead & Hunt (steel) and AASHTO-derived DOT/Midas summaries (concrete). The full table is copyrighted, so the slab `(S+10)/30` and truss `0.100 L` rows are canonical AASHTO values not re-verified line-by-line online — **medium-high confidence**.
- **Jersey 0.81 m / 0.61 m base**: agreement across 3+ sources — **high confidence**. F-shape shares the footprint; exact slope-break heights vary slightly by state (DOTs publish modified profiles) — flagged.
- **Box-girder 0.045/0.04**: Caltrans-specific rule of thumb; other DOTs vary ±0.005 — **medium-high**.
- **Pier "needed when span > ~35–40 m"**: this is an engineering heuristic synthesised from girder span limits, **not** a single cited code line — **medium confidence**, deliberately conservative for visual plausibility.
