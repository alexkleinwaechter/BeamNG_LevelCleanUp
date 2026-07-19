using System.Globalization;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
///     V2 plan 0.4 — "what's under the bridge". Classifies in-memory OSM features (railways/waterways are
///     NOT road splines but ARE in <see cref="OsmQueryResult.Features"/>) into bridge obstacle kinds, projects
///     them to terrain-local meters ONCE (review P0-3: the planner must never touch WGS84), and finds where
///     they pass under a bridge span footprint. Built once per generation whenever OSM features exist
///     (obstacle typing is unconditional, doc 17 §4a); when no OSM features exist (PNG-skeleton sources)
///     the set is empty and every span degrades to Road/Terrain classification.
/// </summary>
public static class BridgeObstacleClassifier
{
    /// <summary>Railway values that are a physical track at grade (clearance applies).</summary>
    private static readonly HashSet<string> RailValues =
    [
        "rail", "light_rail", "narrow_gauge", "tram", "monorail", "funicular", "miniature", "preserved",
        "subway", // kept only when not underground — the layer/tunnel guard below drops buried subways
    ];

    /// <summary>Linear waterway values with an actual water surface.</summary>
    private static readonly HashSet<string> WaterwayValues =
    [
        "river", "riverbank", "canal", "stream", "tidal_channel", "drain", "ditch",
    ];

    // ========================================
    // PER-FEATURE CLASSIFICATION
    // ========================================

    /// <summary>
    ///     Obstacle kind of one OSM feature, or null when it is not a clearance-relevant obstacle
    ///     (buildings, landuse, abandoned rail, underground features, …). Terrain is never returned —
    ///     it is the ABSENCE of any crossing feature (spec R1).
    /// </summary>
    public static BridgeObstacleKind? ClassifyFeature(OsmFeature feature)
    {
        var tags = feature.Tags;

        // Underground / covered features never constrain a deck above them (R6 multi-level is deferred,
        // but a buried subway or culverted stream must not force 6 m of clearance today).
        if (feature.Layer < 0) return null;
        if (tags.TryGetValue("tunnel", out var tunnel) && tunnel != "no") return null;
        if (tags.TryGetValue("covered", out var covered) && covered == "yes") return null;

        if (tags.TryGetValue("railway", out var railway))
            return RailValues.Contains(railway) ? BridgeObstacleKind.Rail : null;

        if (tags.TryGetValue("waterway", out var waterway))
            return WaterwayValues.Contains(waterway) ? BridgeObstacleKind.Water : null;

        if (tags.TryGetValue("natural", out var natural) && natural == "water")
            return BridgeObstacleKind.Water;

        if (tags.ContainsKey("highway"))
            return BridgeObstacleKind.Road;

        return null;
    }

    /// <summary>
    ///     Doc 19: pier-keep-out-only obstacle kind of one OSM feature — <c>building</c>/<c>building:part</c>
    ///     polygons (value != "no"). Kept OUT of <see cref="ClassifyFeature"/> on purpose: buildings never
    ///     constrain deck CLEARANCE, they only repel pier footprints. Only consulted when
    ///     <see cref="BridgeRuleSystemOptions.EnableBridgePiers"/> is on (flag off ⇒ obstacle set unchanged).
    /// </summary>
    public static BridgeObstacleKind? ClassifyPierObstacle(OsmFeature feature)
    {
        if (feature.GeometryType != OsmGeometryType.Polygon) return null;

        if ((feature.Tags.TryGetValue("building", out var building) && building != "no") ||
            (feature.Tags.TryGetValue("building:part", out var part) && part != "no"))
            return BridgeObstacleKind.Building;

        return null;
    }

    /// <summary>Spec §3.1: electrified unless OSM explicitly says `electrified=no` (conservative default).</summary>
    public static bool IsElectrified(IReadOnlyDictionary<string, string> tags)
    {
        return !tags.TryGetValue("electrified", out var v) || v != "no";
    }

    /// <summary>Spec §3.1: navigable when `boat=yes`, any `CEMT=*`, a canal, or width ≥ the threshold.</summary>
    public static bool IsNavigable(IReadOnlyDictionary<string, string> tags, float navigableWidthMeters)
    {
        if (tags.TryGetValue("boat", out var boat) && boat == "yes") return true;
        if (tags.ContainsKey("CEMT")) return true;
        if (tags.TryGetValue("waterway", out var ww) && ww == "canal") return true;
        var width = ParseWidthMeters(tags);
        return width.HasValue && width.Value >= navigableWidthMeters;
    }

    /// <summary>First parsable number from `width` / `est_width` (tolerates a trailing "m" / value lists).</summary>
    public static float? ParseWidthMeters(IReadOnlyDictionary<string, string> tags)
    {
        foreach (var key in (string[]) ["width", "est_width"])
        {
            if (!tags.TryGetValue(key, out var raw)) continue;
            var token = raw.Split(';', ' ', 'm')[0].Replace(',', '.');
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                return v;
        }

        return null;
    }

    // ========================================
    // SET CONSTRUCTION (once per generation)
    // ========================================

    /// <summary>
    ///     Projects every clearance-relevant OSM feature into terrain-local meters and builds the spatial
    ///     bucket. <paramref name="includeRoads"/> default-on: road obstacles are normally detected via the
    ///     road-spline grade-separation crossings, but un-generated highways (e.g. paths not selected as a
    ///     material) only exist here.
    /// </summary>
    public static BridgeObstacleSet BuildObstacleSet(
        OsmQueryResult queryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        BridgeRuleSystemOptions options,
        bool includeRoads = true)
    {
        var processor = new OsmGeometryProcessor();
        var features = new List<BridgeObstacleFeature>();
        var terrainExtent = terrainSize * metersPerPixel;

        foreach (var feature in queryResult.Features)
        {
            if (feature.Coordinates.Count < 2) continue;

            // Doc 19: building polygons join the set ONLY when piers are enabled (pier keep-out); every
            // clearance consumer filters by kind, so they are inert to the elevation rules even then.
            var kind = ClassifyFeature(feature) ??
                       (options.EnableBridgePiers ? ClassifyPierObstacle(feature) : null);
            if (kind is null) continue;
            if (kind == BridgeObstacleKind.Road && !includeRoads) continue;

            // Same WGS84 → pixel → meter path the road splines take, so coordinates line up exactly.
            var pixels = processor.TransformToTerrainCoordinates(feature.Coordinates, bbox, terrainSize);
            var points = pixels.Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel)).ToList();

            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in points)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            // Drop features entirely outside the terrain (with a small margin).
            const float margin = 32f;
            if (max.X < -margin || max.Y < -margin || min.X > terrainExtent + margin ||
                min.Y > terrainExtent + margin)
                continue;

            features.Add(new BridgeObstacleFeature
            {
                OsmId = feature.Id,
                Kind = kind.Value,
                Electrified = kind == BridgeObstacleKind.Rail && IsElectrified(feature.Tags),
                Navigable = kind == BridgeObstacleKind.Water &&
                            IsNavigable(feature.Tags, options.NavigableWaterWidthMeters),
                Points = points,
                IsPolygon = feature.GeometryType == OsmGeometryType.Polygon,
                Min = min,
                Max = max,
            });
        }

        TerrainCreationLogger.Current?.Detail(
            $"[BRIDGE-OBSTACLES] built obstacle set: {features.Count} features " +
            $"(rail={features.Count(f => f.Kind == BridgeObstacleKind.Rail)}, " +
            $"water={features.Count(f => f.Kind == BridgeObstacleKind.Water)}, " +
            $"road={features.Count(f => f.Kind == BridgeObstacleKind.Road)}, " +
            $"building={features.Count(f => f.Kind == BridgeObstacleKind.Building)}) " +
            $"from {queryResult.Features.Count} OSM features");

        return new BridgeObstacleSet(features);
    }

    // ========================================
    // PER-SPAN QUERY
    // ========================================

    /// <summary>
    ///     Crossings of obstacle features under a bridge span footprint (one per feature, v1).
    ///     <paramref name="ignoreOsmWayIds"/> must carry the span's own way-id set
    ///     (<c>StructureSegment.OsmWayIds</c>): a bridge way is itself <c>highway=*</c>, so it sits in the
    ///     obstacle set as a Road feature, and a span footprint always contains its own centerline — without
    ///     the guard every bridge would report ITSELF as an obstacle and the planner would raise the deck to
    ///     clear its own deck.
    /// </summary>
    public static List<BridgeObstacleCrossing> FindCrossings(
        BridgeObstacleSet set, BridgeSpanFootprint footprint, float sampleStepMeters = 2f,
        IReadOnlySet<long>? ignoreOsmWayIds = null)
    {
        return FindCrossings(set, footprint.Min, footprint.Max, footprint.Contains, sampleStepMeters,
            ignoreOsmWayIds);
    }

    /// <summary>
    ///     Geometry-only core (testable without a road network): walks each candidate's segments at
    ///     <paramref name="sampleStepMeters"/> and reports the midpoint of the first sample run inside the
    ///     footprint. A polygon whose EDGES never enter the footprint (deck fully over a lake interior)
    ///     is caught by the footprint-center containment test. Features whose <c>OsmId</c> is in
    ///     <paramref name="ignoreOsmWayIds"/> are skipped (the span's own ways — see the footprint overload).
    /// </summary>
    public static List<BridgeObstacleCrossing> FindCrossings(
        BridgeObstacleSet set, Vector2 min, Vector2 max, Func<Vector2, bool> contains,
        float sampleStepMeters = 2f, IReadOnlySet<long>? ignoreOsmWayIds = null)
    {
        var step = Math.Max(0.25f, sampleStepMeters);
        var result = new List<BridgeObstacleCrossing>();

        foreach (var feature in set.QueryAabb(min, max))
        {
            if (ignoreOsmWayIds != null && ignoreOsmWayIds.Contains(feature.OsmId))
                continue; // the span's own way(s) — a bridge is not an obstacle under itself

            Vector2? runStart = null;
            Vector2? runEnd = null;

            foreach (var sample in SamplePolyline(feature, step))
            {
                if (contains(sample))
                {
                    runStart ??= sample;
                    runEnd = sample;
                }
                else if (runStart.HasValue)
                {
                    break; // first inside-run complete
                }
            }

            if (runStart.HasValue)
            {
                result.Add(new BridgeObstacleCrossing(feature, (runStart.Value + runEnd!.Value) * 0.5f));
            }
            else if (feature.IsPolygon)
            {
                var center = (min + max) * 0.5f;
                if (contains(center) && feature.ContainsPoint(center))
                    result.Add(new BridgeObstacleCrossing(feature, center));
            }
        }

        return result;
    }

    /// <summary>Dense samples along the feature polyline / polygon ring (also used by the planner's
    /// water-surface-Z estimate, A1 — review P0-3 v1: min centerline DEM within the span footprint).</summary>
    internal static IEnumerable<Vector2> SamplePolyline(BridgeObstacleFeature feature, float step)
    {
        var pts = feature.Points;
        var count = feature.IsPolygon ? pts.Count : pts.Count - 1; // polygon: close the ring
        for (var i = 0; i < count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var len = Vector2.Distance(a, b);
            var n = Math.Max(1, (int)(len / step));
            for (var s = 0; s < n; s++)
                yield return Vector2.Lerp(a, b, (float)s / n);
        }

        if (pts.Count > 0 && !feature.IsPolygon)
            yield return pts[^1];
    }
}
