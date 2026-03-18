# Road Width Calculation in OSM2World

## Overview

The core logic lives in `core/src/main/java/org/osm2world/world/modules/RoadModule.java`, with width parsing helpers in `core/src/main/java/org/osm2world/world/modules/common/WorldModuleParseUtil.java` and `core/src/main/java/org/osm2world/util/ValueParseUtil.java`.

---

## Width Calculation Priority (4 levels)

The `calculateWidth()` method (~line 1190) resolves total road width in this order:

1. **Sum of explicit per-lane widths** — If every lane has a known width (from tags like `width:lanes:forward=3|3|2.5`), sum them.
2. **Explicit road-level `width` or `est_width` tag** — Parsed via `parseWidth()`, supports meters, km, miles, feet/inches, or bare numbers (treated as meters).
3. **Lane count + default lane width** — If `lanes`, `lanes:forward`, `lanes:backward`, or `divider` tags exist, multiply lane count by `DEFAULT_LANE_WIDTH = 3.5m`.
4. **Estimation from highway type** — Last resort, using type-specific multipliers (see table below).

---

## Lane Count Determination

Determined in `buildBasicLaneLayout()` (~line 823):

| Source | Example |
|--------|---------|
| Per-lane tags with `\|` delimiters | `width:lanes:forward=3\|3\|2.5` → 3 lanes |
| Explicit `lanes` / `lanes:forward` / `lanes:backward` | `lanes=4` |
| Default by highway type (see below) | `highway=motorway` → 2 per direction |

### Default Lane Counts (`getDefaultLanes`, ~line 164)

| Highway Type | Default Lanes |
|---|---|
| `motorway` | 2 per direction |
| `primary`, `secondary` | 2 total |
| `residential`, `living_street`, `service`, `track` | 1 |
| Any `*_link` | 1 |
| Oneway roads | 1 |
| All others (`tertiary`, `unclassified`, etc.) | 2 if bidirectional, 1 if oneway |

---

## Lane Layout Structure

Lanes are built left-to-right with these types and default widths:

| Lane Type | Default Width | Added When |
|---|---|---|
| `VEHICLE_LANE` | 3.5m (`DEFAULT_LANE_WIDTH`) | Always (core lanes) |
| `CYCLEWAY` | 1.5m | `cycleway:left=lane` / `cycleway:right=lane` |
| `SIDEWALK` | 1.0m | `sidewalk=both` / `sidewalk:left=yes` / etc. |
| `KERB` | 0.15m | Sidewalk present and `kerb != no` |
| `BUS_BAY` | (explicit only) | `bus_bay:left=yes` / `bus_bay:right=yes` |
| `DASHED_LINE` | 0.1m | Between same-direction lanes, center divider default |
| `SOLID_LINE` | 0.1m | When `divider=solid_line` or overtaking prohibited |

### Layout Order Per Side

vehicle lanes → (dashed line) → cycleway → kerb → sidewalk

### Center Divider (bidirectional roads)

Determined by `divider` tag or `overtaking:*` tags. Default is `DASHED_LINE`.

---

## Estimation Fallback (no tags at all)

`estimateVehicleLanesWidth()` (~line 1261) when no `lanes`/`width`/`divider` tags exist:

| Highway Type | Estimated Total Vehicle Width |
|---|---|
| `path` | 1.0m |
| `track` | 2.5m |
| `service` + `parking_aisle` | 2.8m (3.5 * 0.8) |
| `service` (other) | 3.5m |
| `primary`, `secondary` | 7.0m (2 * 3.5) |
| `motorway` | 8.75m (2.5 * 3.5) |
| Oneway roads | 3.5m |
| Everything else | 4.0m |

---

## Width Distribution Among Lanes

`LaneLayout.setCalculatedValues()` (~line 1677) distributes the total width:

1. Lanes with explicit widths keep their width.
2. Remaining width is divided equally among implicit-width lanes.
3. If explicit widths exceed total, all lanes are **scaled down proportionally**.
4. Each lane gets a relative width (0-1 fraction) and absolute position for rendering.

---

## Width Parsing (`parseMeasure`)

The `ValueParseUtil.parseMeasure()` method supports multiple units:

- Meter values: `5 m`, `5.5m`
- Kilometer values: `0.5 km`
- Mile values: `0.003 mi`
- Feet/inches: `16'4"`
- Unitless numbers: `5` (interpreted as meters by default)

---

## Practical Examples

### Simple road
`highway=secondary` → 2 default lanes * 3.5m = **7.0m**

### Explicit width wins
`highway=secondary, width=8m` → **8.0m** (tag overrides calculation)

### Per-lane widths
`highway=motorway, width:lanes:forward=3.5|3.5|3.25` → forward direction alone = **10.25m**

### Road with sidewalks and cycleway
`highway=tertiary, lanes=2, sidewalk=both, cycleway:left=lane`

| Component | Width |
|---|---|
| Left sidewalk | 1.0m |
| Left kerb | 0.15m |
| Left cycleway | 1.5m |
| Dashed line | 0.1m |
| Vehicle lane | 3.5m |
| Center dashed line | 0.1m |
| Vehicle lane | 3.5m |
| Right kerb | 0.15m |
| Right sidewalk | 1.0m |
| **Total** | **~11.5m** |

### Parking aisle
`highway=service, service=parking_aisle` → 3.5 * 0.8 = **2.8m**

---

## Key Takeaways for Reimplementation

1. **Priority chain**: explicit per-lane widths > road `width` tag > lane-count-based calculation > highway-type estimation.
2. **`DEFAULT_LANE_WIDTH = 3.5m`** is the single most important constant.
3. Lane types beyond vehicle lanes (sidewalk, cycleway, kerb, line markings) each add their own width.
4. Width parsing supports multiple units via `parseMeasure()` — meters, km, miles, feet+inches.
5. The `divider` and `overtaking:*` tags control center line type (dashed vs solid), which also has a width (0.1m).
6. When explicit lane widths don't match the total road width, proportional scaling is applied rather than failing.
7. Oneway roads: `lanes` tag refers to total lanes (not per-direction); `lanes:forward`/`lanes:backward` are directional.
8. Kerb height is configurable: default 0.12m, lowered/rolled 0.03m, flush 0m.
