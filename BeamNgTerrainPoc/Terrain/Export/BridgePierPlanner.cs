using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>Support archetype of a bridge span, decided from its OSM tags (doc 19 §2).</summary>
public enum BridgePierArchetype
{
    /// <summary>Round/octagonal column(s) + pier cap at ~<c>PierSpacingMeters</c> — the ≥95 % default.</summary>
    ColumnPier,

    /// <summary>Same columns, strict regular rhythm across the whole span (viaduct/aqueduct optic).</summary>
    ViaductPier,

    /// <summary>Closely-spaced slender twin bents (trestle/boardwalk) — v1 alias of ColumnPier.</summary>
    TrestleBent,

    /// <summary>No intermediate supports (suspension-class until pylons exist, floating, movable).</summary>
    NoSupports,
}

/// <summary>One planned column of a pier (terrain-local meters).</summary>
public sealed class PlannedPierColumn
{
    /// <summary>Column center, plan view.</summary>
    public required Vector2 Center { get; init; }

    /// <summary>Post-carve ground: MINIMUM heightmap sample over the 4 column-footprint corners (§3d).</summary>
    public required float GroundZ { get; init; }

    /// <summary>Column bottom = <see cref="GroundZ"/> − <c>PierGroundEmbedMeters</c>.</summary>
    public required float BottomZ { get; init; }

    /// <summary>Column diameter (m) — archetype-resolved (trestle bents are slimmer).</summary>
    public required float Diameter { get; init; }
}

/// <summary>
///     One placed pier of a span (doc 19 §3): the resolved station plus everything the mesh builder
///     needs, interpolated from the span snapshot at the FINAL (post slide-or-skip) station. All
///     coordinates terrain-local meters; Z pre-base-height (the exporter adds <c>terrainBaseHeight</c>).
/// </summary>
public sealed class PierPlan
{
    public required int SpanId { get; init; }

    /// <summary>Nominal bay index this pier was planned for (diagnostics key).</summary>
    public required int BayIndex { get; init; }

    /// <summary>Final arc-length station along the owning spline (<see cref="BridgeStation.DistanceAlongSpline"/> frame).</summary>
    public required float Station { get; init; }

    /// <summary>Nominal (pre-slide) station. Equal to <see cref="Station"/> when not slid.</summary>
    public required float NominalStation { get; init; }

    public required BridgePierArchetype Archetype { get; init; }

    public required Vector2 Center { get; init; }
    public required Vector2 Normal { get; init; }
    public required Vector2 Tangent { get; init; }
    public required float DeckWidth { get; init; }

    /// <summary>Deck-top elevations at the station (banked edges carried explicitly, like the snapshot).</summary>
    public required float CenterZ { get; init; }

    public required float LeftEdgeZ { get; init; }
    public required float RightEdgeZ { get; init; }

    /// <summary>Deck soffit at the centerline (= <see cref="CenterZ"/> − deck thickness).</summary>
    public required float SoffitZ { get; init; }

    /// <summary>Cap plan width (deck width − 2·inset), already clamped positive.</summary>
    public required float CapWidth { get; init; }

    public required List<PlannedPierColumn> Columns { get; init; }

    public bool WasSlid => MathF.Abs(Station - NominalStation) > 0.01f;
}

/// <summary>
///     Forbidden arc-intervals along a span axis (doc 19 §3b) with slide-or-skip resolution (§3c).
///     Pure interval algebra, unit-testable without any geometry.
/// </summary>
public sealed class PierForbiddenIntervals
{
    private readonly List<(float Start, float End, string Reason)> _intervals = [];

    public IReadOnlyList<(float Start, float End, string Reason)> Intervals => _intervals;

    public void Add(float start, float end, string reason)
    {
        if (end < start) (start, end) = (end, start);
        _intervals.Add((start, end, reason));
    }

    /// <summary>The blocking reason at <paramref name="s"/>, or null when the station is allowed.</summary>
    public string? BlockedBy(float s)
    {
        foreach (var (start, end, reason) in _intervals)
        {
            if (s >= start && s <= end)
                return reason;
        }

        return null;
    }

    public bool IsForbidden(float s) => BlockedBy(s) != null;

    /// <summary>
    ///     Nearest allowed station to <paramref name="s"/> within ±<paramref name="maxSlide"/>, clamped to
    ///     [<paramref name="usableStart"/>, <paramref name="usableEnd"/>], searched at 0.25 m steps on both
    ///     sides (nearer side wins; exact tie → the lower station, deterministic). Null when no allowed
    ///     position exists in range — the caller skips the pier (§3c.3: the constraint is absolute).
    /// </summary>
    public float? NearestAllowed(float s, float maxSlide, float usableStart, float usableEnd,
        Func<float, bool>? extraPredicate = null)
    {
        const float step = 0.25f;
        bool Allowed(float x) =>
            x >= usableStart && x <= usableEnd && !IsForbidden(x) && (extraPredicate?.Invoke(x) ?? true);

        if (Allowed(s)) return s;

        for (var d = step; d <= maxSlide + 1e-3f; d += step)
        {
            if (Allowed(s - d)) return s - d;
            if (Allowed(s + d)) return s + d;
        }

        return null;
    }
}

/// <summary>
///     Doc 19 §3 — plans intermediate pier supports for one bridge span: archetype from OSM tags (§2),
///     nominal stations at <c>PierSpacingMeters</c> (§3a), forbidden intervals from everything the deck
///     crosses — network road crossings, OSM rail/water/building features, other decks BELOW (§3b) —
///     then slide-or-skip resolution (§3c) and the ground standing surface (§3d). A pier footprint
///     NEVER stands on an obstacle: positions move or drop, the deck (a rigid solved body) tolerates a
///     longer bay. Pure function of the snapshot + obstacle set — same run, same piers.
/// </summary>
public static class BridgePierPlanner
{
    /// <summary>§3a.2: extra margin (m) past the end-stamp zone before the first usable pier station.</summary>
    public const float PierEndMarginMeters = 3f;

    /// <summary>§3b.2: assumed half-width (m) of an OSM road feature crossing under the deck (the
    /// robustness net for roads the crossing detector missed; detector crossings use the exact width).</summary>
    public const float DefaultRoadHalfWidthMeters = 4f;

    /// <summary>§3b.2: rail corridor half-width (6 m raster default → 3 m).</summary>
    public const float RailCorridorHalfWidthMeters = 3f;

    /// <summary>Trestle alias (§4): bay spacing / column diameter overrides.</summary>
    public const float TrestleSpacingMeters = 12f;

    public const float TrestleColumnDiameterMeters = 0.5f;

    // ========================================
    // ARCHETYPE (doc 19 §2)
    // ========================================

    /// <summary>
    ///     Decision order: <c>bridge:structure</c>-derived <see cref="StructureSegment.BridgeStructureType"/>
    ///     → raw <c>bridge=*</c> / <c>bridge:movable=*</c> tags → ColumnPier default. Suspension-class tags
    ///     yield <see cref="BridgePierArchetype.NoSupports"/> — a wrong forest of columns under the Brooklyn
    ///     Bridge is worse than none (pylons are a later doc).
    /// </summary>
    public static BridgePierArchetype ResolveArchetype(BridgeSpanSnapshot span, StructureSegment? segment)
    {
        var tags = span.OsmTags;
        var bridgeValue = tags != null && tags.TryGetValue("bridge", out var bv)
            ? bv.ToLowerInvariant()
            : null;

        if (tags != null && tags.ContainsKey("bridge:movable"))
            return BridgePierArchetype.NoSupports;

        var structure = segment?.BridgeStructureType?.ToLowerInvariant();
        switch (structure)
        {
            case "suspension" or "cable-stayed" or "simple-suspension" or "floating" or "movable":
                return BridgePierArchetype.NoSupports;
            case "viaduct" or "aqueduct":
                return BridgePierArchetype.ViaductPier;
            case "beam" or "truss" or "cantilever" or "arch" or "humpback" or "clapper":
                return BridgePierArchetype.ColumnPier;
        }

        return bridgeValue switch
        {
            "suspension" or "cable-stayed" or "simple-suspension" or "floating" or "movable"
                => BridgePierArchetype.NoSupports,
            "viaduct" or "aqueduct" => BridgePierArchetype.ViaductPier,
            "trestle" or "boardwalk" => BridgePierArchetype.TrestleBent,
            _ => BridgePierArchetype.ColumnPier,
        };
    }

    // ========================================
    // PER-SPAN PLANNING
    // ========================================

    /// <summary>Everything one span's plan needs. Optional members degrade gracefully (no obstacle set ⇒
    /// no keep-out from that source), EXCEPT <see cref="GroundZ"/>, which is required — piers must stand
    /// on the FINAL carved ground.</summary>
    public sealed class PlanInput
    {
        public required BridgeSpanSnapshot Span { get; init; }

        /// <summary>The span's structure segment (merge-end flags). Null tolerated (test fixtures).</summary>
        public StructureSegment? Segment { get; init; }

        public required BridgeRuleSystemOptions Options { get; init; }

        /// <summary>Deck profile — thickness rule (soffit) + end-stamp length (§3a usable interval).</summary>
        public required BridgeDeckProfile DeckProfile { get; init; }

        /// <summary>Rail/water/road/building keep-out source (§3b.2/3). Null ⇒ source skipped.</summary>
        public BridgeObstacleSet? Obstacles { get; init; }

        /// <summary>Network grade-separated crossings (§3b.1). The planner filters to this span's spline.</summary>
        public IReadOnlyList<GradeSeparatedCrossing>? Crossings { get; init; }

        /// <summary>Exact half-width (m) of a crossing's lower road at the crossing. Null ⇒ class default.</summary>
        public Func<GradeSeparatedCrossing, float>? LowerRoadHalfWidth { get; init; }

        /// <summary>All span snapshots of the network — the lower-deck exclusion partners (§3b.4).</summary>
        public IReadOnlyList<BridgeSpanSnapshot>? AllSpans { get; init; }

        /// <summary>Run-level geometry cache. Every span consults every other span (§3b.4 + footprint
        /// validation), so without a shared cache the same partner geometry is rebuilt O(spans²) times
        /// per export. Null ⇒ a per-call cache is used (correct, just slower across spans).</summary>
        public SpanGeometryCache? GeometryCache { get; init; }

        /// <summary>Post-carve ground elevation at a terrain-local point (§1 ordering — FINAL ground).</summary>
        public required Func<Vector2, float> GroundZ { get; init; }

        /// <summary>Diagnostic sink for the <c>[PIER]</c> lines. Null ⇒ silent.</summary>
        public Action<string>? Log { get; init; }
    }

    /// <summary>Plans all piers of one span. Empty list when the span is short, unsupported by archetype,
    /// or fully blocked. Never throws on degenerate geometry.</summary>
    public static List<PierPlan> PlanSpan(PlanInput input)
    {
        var span = input.Span;
        var options = input.Options;
        var log = input.Log;
        var stations = span.Stations;
        if (stations.Count < 2) return [];

        var firstS = stations[0].DistanceAlongSpline;
        var lastS = stations[^1].DistanceAlongSpline;
        var spanLength = lastS - firstS;
        if (spanLength < options.MinSpanLengthForPiersMeters) return [];

        var archetype = ResolveArchetype(span, input.Segment);
        if (archetype == BridgePierArchetype.NoSupports)
        {
            log?.Invoke($"[PIER] span {span.SpanId} ({spanLength:F0}m): NoSupports archetype " +
                        $"(tags: {DescribeTags(span)}) — no piers");
            return [];
        }

        var endMargin = input.DeckProfile.EndStampLengthMeters + PierEndMarginMeters;
        var usableStart = firstS + endMargin;
        var usableEnd = lastS - endMargin;
        if (usableEnd - usableStart < 1f)
        {
            log?.Invoke($"[PIER] span {span.SpanId} ({spanLength:F0}m): usable interval collapsed " +
                        "(end zones overlap) — no piers");
            return [];
        }

        var spacing = archetype == BridgePierArchetype.TrestleBent
            ? TrestleSpacingMeters
            : Math.Max(5f, options.PierSpacingMeters);
        var usable = usableEnd - usableStart;
        var bayCount = (int)MathF.Round(usable / spacing);
        if (bayCount <= 0) return [];

        var thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(spanLength, input.DeckProfile);
        var maxWidth = 0f;
        foreach (var st in stations) maxWidth = MathF.Max(maxWidth, st.Width);

        var columnDiameter = archetype == BridgePierArchetype.TrestleBent
            ? TrestleColumnDiameterMeters
            : options.PierColumnDiameterMeters;
        var capWidthMax = MathF.Max(columnDiameter, maxWidth - 2f * options.PierCapSideInsetMeters);
        // §3b: the FOOTPRINT (half the pier's plan diagonal, cap overhang included) must clear the
        // obstacle — not the centerline.
        var pierHalfExtent = 0.5f * MathF.Sqrt(
            capWidthMax * capWidthMax + options.PierCapLengthMeters * options.PierCapLengthMeters);

        var cache = input.GeometryCache ?? new SpanGeometryCache();
        var geometry = cache.Get(span);
        var forbidden = BuildForbiddenIntervals(input, geometry, cache, pierHalfExtent, firstS, lastS);

        // §3a.4 + slide predicate: soffit-to-ground must stay ≥ MinPierHeightMeters at the FINAL station.
        bool TallEnough(float s)
        {
            var sample = geometry.Interpolate(s);
            var ground = input.GroundZ(sample.Center);
            if (float.IsNaN(ground)) return true; // outside heightmap — don't block on missing data
            return sample.CenterZ - thickness - ground >= options.MinPierHeightMeters;
        }

        // Nominal stations: k piers spread over the usable interval in equal bays (§3a.3).
        var nominal = new float[bayCount];
        for (var j = 0; j < bayCount; j++)
            nominal[j] = usableStart + usable * (j + 1) / (bayCount + 1);

        // ViaductPier: keep the rhythm — try a uniform shift of the whole ladder first (§3c.2).
        var rhythmShift = 0f;
        if (archetype == BridgePierArchetype.ViaductPier)
        {
            rhythmShift = FindRhythmShift(nominal, forbidden, options.MaxPierSlideMeters,
                usableStart, usableEnd, TallEnough) ?? 0f;
            if (rhythmShift != 0f)
                log?.Invoke($"[PIER] span {span.SpanId}: viaduct rhythm shifted {rhythmShift:+0.0;-0.0}m " +
                            "to clear obstacles");
        }

        var placedStations = new List<(int Bay, float Nominal, float Station)>();
        for (var j = 0; j < bayCount; j++)
        {
            var s = nominal[j] + rhythmShift;

            if (!TallEnough(s) && !forbidden.IsForbidden(s))
            {
                log?.Invoke($"[PIER] span {span.SpanId} bay {j}: deck too low " +
                            $"(< {options.MinPierHeightMeters:F1}m above ground) @ s={s:F1}m — dropped " +
                            "(embankment zone)");
                continue;
            }

            var resolved = forbidden.NearestAllowed(
                s, options.MaxPierSlideMeters, usableStart, usableEnd, TallEnough);
            if (resolved == null)
            {
                var reason = forbidden.BlockedBy(s) ?? "no allowed position in slide range";
                var bayGrows = usable / (bayCount + 1) * 2f;
                log?.Invoke($"[PIER] span {span.SpanId} bay {j}: blocked by {reason} @ s={s:F1}m — " +
                            $"skipped (bay grows to {bayGrows:F0}m)");
                continue;
            }

            placedStations.Add((j, s, resolved.Value));
        }

        // §3c.4: piers slid toward each other closer than MinPierGapMeters merge (or the second drops).
        placedStations.Sort((a, b) => a.Station.CompareTo(b.Station));
        for (var i = placedStations.Count - 2; i >= 0; i--)
        {
            var cur = placedStations[i];
            var next = placedStations[i + 1];
            if (next.Station - cur.Station >= options.MinPierGapMeters) continue;

            var mid = (cur.Station + next.Station) / 2f;
            if (!forbidden.IsForbidden(mid) && TallEnough(mid))
            {
                placedStations[i] = (cur.Bay, cur.Nominal, mid);
                log?.Invoke($"[PIER] span {span.SpanId} bays {cur.Bay}+{next.Bay}: merged to one pier " +
                            $"@ s={mid:F1}m (gap < {options.MinPierGapMeters:F0}m)");
            }
            else
            {
                log?.Invoke($"[PIER] span {span.SpanId} bay {next.Bay}: dropped " +
                            $"(within {options.MinPierGapMeters:F0}m of bay {cur.Bay})");
            }

            placedStations.RemoveAt(i + 1);
        }

        // Materialize plans + final absolute-constraint validation (§3b hard constraint: the footprint
        // RING must clear building polygons and lower decks even where the s-interval projection leaked).
        var result = new List<PierPlan>(placedStations.Count);
        foreach (var (bay, nominalS, station) in placedStations)
        {
            var plan = BuildPlan(span, input, geometry, archetype, bay, nominalS, station,
                thickness, columnDiameter, pierHalfExtent);
            if (plan == null) continue;

            var violation = ValidateFootprint(plan, input, cache, pierHalfExtent);
            if (violation != null)
            {
                log?.Invoke($"[PIER] span {span.SpanId} bay {bay}: footprint validation hit {violation} " +
                            $"@ s={station:F1}m — skipped");
                continue;
            }

            var slidNote = plan.WasSlid ? $" (slid {station - nominalS:+0.0;-0.0}m)" : "";
            log?.Invoke($"[PIER] span {span.SpanId} bay {bay}: placed @ s={station:F1}m " +
                        $"xy=({plan.Center.X:F0},{plan.Center.Y:F0}) height={plan.SoffitZ - plan.Columns.Min(c => c.GroundZ):F1}m " +
                        $"{plan.Columns.Count} column(s){slidNote}");
            result.Add(plan);
        }

        if (result.Count > 0 || bayCount > 0)
        {
            log?.Invoke($"[PIER] span {span.SpanId} ({archetype}, {spanLength:F0}m): " +
                        $"{result.Count}/{bayCount} pier(s) placed");
        }

        return result;
    }

    // ========================================
    // FORBIDDEN INTERVALS (§3b)
    // ========================================

    private static PierForbiddenIntervals BuildForbiddenIntervals(
        PlanInput input, SpanStationGeometry geometry, SpanGeometryCache cache,
        float pierHalfExtent, float firstS, float lastS)
    {
        var span = input.Span;
        var options = input.Options;
        var forbidden = new PierForbiddenIntervals();
        var clearance = options.PierClearanceMarginMeters;

        // §3b.1 — network road crossings under this span (exact lower-road width when resolvable).
        if (input.Crossings != null)
        {
            foreach (var crossing in input.Crossings)
            {
                if (crossing.UpperSplineId != span.SplineId) continue;

                var (s, dist) = geometry.Project(crossing.CrossingXY);
                if (s < firstS - 1f || s > lastS + 1f) continue;
                // A crossing far off the deck axis belongs to another span of the same spline.
                if (dist > geometry.MaxHalfWidth + 2f) continue;

                var lowerHalf = input.LowerRoadHalfWidth?.Invoke(crossing) ?? DefaultRoadHalfWidthMeters;
                var margin = lowerHalf + pierHalfExtent + clearance;
                var label = crossing.HasLowerSpline
                    ? $"road (spline {crossing.LowerSplineId})"
                    : $"{crossing.LowerKind.ToString().ToLowerInvariant()} crossing";
                forbidden.Add(s - margin, s + margin, label);
            }
        }

        // §3b.2/3 — OSM rail / navigable water / road / building features from the obstacle set.
        if (input.Obstacles is { Count: > 0 } obstacles)
        {
            var margin = pierHalfExtent + clearance;
            var queryMin = new Vector2(geometry.Min.X - margin, geometry.Min.Y - margin);
            var queryMax = new Vector2(geometry.Max.X + margin, geometry.Max.Y + margin);

            foreach (var feature in obstacles.QueryAabb(queryMin, queryMax))
            {
                if (span.OsmWayIds.Contains(feature.OsmId))
                    continue; // merged-corridor self-exclusion (doc 19 §7)

                // The grid buckets are coarse — drop candidates whose exact AABB misses the query
                // rect (they can't contribute an interval) before sampling their polyline.
                if (feature.Max.X < queryMin.X || feature.Min.X > queryMax.X ||
                    feature.Max.Y < queryMin.Y || feature.Min.Y > queryMax.Y)
                    continue;

                if (feature.Kind == BridgeObstacleKind.Water && !feature.Navigable)
                {
                    // Real piers stand in rivers — allowed v1, but visible in the log for render review.
                    if (FirstRunInterval(feature, geometry, out _, out _))
                        input.Log?.Invoke($"[PIER] span {span.SpanId}: non-navigable water {feature.OsmId} " +
                                          "under deck — piers allowed (logged)");
                    continue;
                }

                var kindMargin = feature.Kind switch
                {
                    BridgeObstacleKind.Rail => RailCorridorHalfWidthMeters,
                    BridgeObstacleKind.Road => DefaultRoadHalfWidthMeters,
                    _ => 0f,
                };

                if (feature.Kind == BridgeObstacleKind.Water)
                {
                    // Navigable: forbid the WHOLE wet interval (no columns in a shipping channel).
                    if (FullEnvelopeInterval(feature, geometry, out var min, out var max))
                        forbidden.Add(min - margin, max + margin, $"navigable water {feature.OsmId}");
                    continue;
                }

                foreach (var (runMin, runMax) in RunIntervals(feature, geometry))
                {
                    forbidden.Add(runMin - kindMargin - margin, runMax + kindMargin + margin,
                        $"{feature.Kind.ToString().ToLowerInvariant()} {feature.OsmId}");
                }

                // A polygon whose ring never enters the footprint (deck fully over a large building):
                // forbid every station whose center is inside the polygon.
                if (feature is { IsPolygon: true, Kind: BridgeObstacleKind.Building })
                {
                    foreach (var (runMin, runMax) in StationRunsInsidePolygon(feature, geometry))
                        forbidden.Add(runMin - margin, runMax + margin, $"building {feature.OsmId}");
                }
            }
        }

        // §3b.4 — other decks BELOW: a pier must never land on (or drop through) a lower deck.
        if (input.AllSpans != null)
        {
            var thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(
                lastS - firstS, input.DeckProfile);
            foreach (var partner in input.AllSpans)
            {
                if (partner.SpanId == span.SpanId || partner.Stations.Count < 2) continue;

                var partnerGeometry = cache.Get(partner);
                if (!BoundsOverlap(geometry, partnerGeometry)) continue;

                float? runStart = null;
                float runEnd = 0f;
                foreach (var st in span.Stations)
                {
                    var blocked = partnerGeometry.TryGetSurfaceZ(st.Center, out var partnerZ) &&
                                  partnerZ < st.CenterZ - thickness;
                    if (blocked)
                    {
                        runStart ??= st.DistanceAlongSpline;
                        runEnd = st.DistanceAlongSpline;
                    }
                    else if (runStart.HasValue)
                    {
                        forbidden.Add(runStart.Value - pierHalfExtent - clearance,
                            runEnd + pierHalfExtent + clearance, $"lower deck (span {partner.SpanId})");
                        runStart = null;
                    }
                }

                if (runStart.HasValue)
                {
                    forbidden.Add(runStart.Value - pierHalfExtent - clearance,
                        runEnd + pierHalfExtent + clearance, $"lower deck (span {partner.SpanId})");
                }
            }
        }

        return forbidden;
    }

    /// <summary>Contiguous inside-the-footprint runs of a feature polyline, as s-intervals.</summary>
    private static List<(float Min, float Max)> RunIntervals(
        BridgeObstacleFeature feature, SpanStationGeometry geometry)
    {
        var runs = new List<(float, float)>();
        float? runMin = null, runMax = null;

        foreach (var sample in BridgeObstacleClassifier.SamplePolyline(feature, 2f))
        {
            if (geometry.ContainsPlan(sample, out var s))
            {
                runMin = runMin.HasValue ? MathF.Min(runMin.Value, s) : s;
                runMax = runMax.HasValue ? MathF.Max(runMax.Value, s) : s;
            }
            else if (runMin.HasValue)
            {
                runs.Add((runMin.Value, runMax!.Value));
                runMin = runMax = null;
            }
        }

        if (runMin.HasValue)
            runs.Add((runMin.Value, runMax!.Value));

        return runs;
    }

    /// <summary>Global min–max s-envelope of all inside samples (the navigable-water wet interval).</summary>
    private static bool FullEnvelopeInterval(
        BridgeObstacleFeature feature, SpanStationGeometry geometry, out float min, out float max)
    {
        min = float.MaxValue;
        max = float.MinValue;
        foreach (var sample in BridgeObstacleClassifier.SamplePolyline(feature, 2f))
        {
            if (!geometry.ContainsPlan(sample, out var s)) continue;
            min = MathF.Min(min, s);
            max = MathF.Max(max, s);
        }

        return min <= max;
    }

    private static bool FirstRunInterval(
        BridgeObstacleFeature feature, SpanStationGeometry geometry, out float min, out float max)
        => FullEnvelopeInterval(feature, geometry, out min, out max);

    /// <summary>Runs of consecutive span stations whose CENTER lies inside the polygon (large-area case).</summary>
    private static List<(float Min, float Max)> StationRunsInsidePolygon(
        BridgeObstacleFeature polygon, SpanStationGeometry geometry)
    {
        var runs = new List<(float, float)>();
        float? runStart = null;
        var runEnd = 0f;

        foreach (var st in geometry.Stations)
        {
            // AABB pre-check: the ray-cast is O(ring points) and most stations of a long span are
            // nowhere near the polygon.
            var c = st.Center;
            var inside = c.X >= polygon.Min.X && c.X <= polygon.Max.X &&
                         c.Y >= polygon.Min.Y && c.Y <= polygon.Max.Y &&
                         polygon.ContainsPoint(c);
            if (inside)
            {
                runStart ??= st.DistanceAlongSpline;
                runEnd = st.DistanceAlongSpline;
            }
            else if (runStart.HasValue)
            {
                runs.Add((runStart.Value, runEnd));
                runStart = null;
            }
        }

        if (runStart.HasValue)
            runs.Add((runStart.Value, runEnd));

        return runs;
    }

    // ========================================
    // RESOLUTION HELPERS
    // ========================================

    /// <summary>Smallest uniform shift |δ| ≤ maxSlide (0.25 m steps) that makes EVERY nominal station
    /// allowed — the viaduct keeps its rhythm when one obstacle shifts a bay (§3c.2). Null when none.</summary>
    private static float? FindRhythmShift(
        float[] nominal, PierForbiddenIntervals forbidden, float maxSlide,
        float usableStart, float usableEnd, Func<float, bool> tallEnough)
    {
        bool AllAllowed(float shift)
        {
            foreach (var s in nominal)
            {
                var x = s + shift;
                if (x < usableStart || x > usableEnd || forbidden.IsForbidden(x) || !tallEnough(x))
                    return false;
            }

            return true;
        }

        if (AllAllowed(0f)) return 0f;

        const float step = 0.25f;
        for (var d = step; d <= maxSlide + 1e-3f; d += step)
        {
            if (AllAllowed(-d)) return -d;
            if (AllAllowed(d)) return d;
        }

        return null;
    }

    private static PierPlan? BuildPlan(
        BridgeSpanSnapshot span, PlanInput input, SpanStationGeometry geometry,
        BridgePierArchetype archetype, int bay, float nominalS, float station,
        float thickness, float columnDiameter, float pierHalfExtent)
    {
        var options = input.Options;
        var sample = geometry.Interpolate(station);
        if (sample.Width <= 0f) return null;

        var capWidth = MathF.Max(columnDiameter, sample.Width - 2f * options.PierCapSideInsetMeters);
        var soffitZ = sample.CenterZ - thickness;

        // §4: twin columns at ±0.3·width when the deck is wide (trestle bents are always twin).
        var twin = archetype == BridgePierArchetype.TrestleBent ||
                   sample.Width >= options.PierTwinColumnThresholdMeters;
        var offsets = twin
            ? new[] { -0.3f * sample.Width, 0.3f * sample.Width }
            : new[] { 0f };

        var columns = new List<PlannedPierColumn>(offsets.Length);
        foreach (var offset in offsets)
        {
            var center = sample.Center + sample.Normal * offset;

            // §3d: min ground over the 4 footprint corners, embed below the lowest — cross-slope
            // ground swallows the column instead of daylighting it downhill.
            var half = columnDiameter / 2f;
            var ground = float.MaxValue;
            foreach (var (dx, dy) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var corner = center + sample.Normal * (dx * half) + sample.Tangent * (dy * half);
                var g = input.GroundZ(corner);
                if (!float.IsNaN(g)) ground = MathF.Min(ground, g);
            }

            if (ground == float.MaxValue)
                ground = soffitZ - options.MinPierHeightMeters; // no heightmap data — plausible stub

            columns.Add(new PlannedPierColumn
            {
                Center = center,
                GroundZ = ground,
                BottomZ = ground - options.PierGroundEmbedMeters,
                Diameter = columnDiameter,
            });
        }

        return new PierPlan
        {
            SpanId = span.SpanId,
            BayIndex = bay,
            Station = station,
            NominalStation = nominalS,
            Archetype = archetype,
            Center = sample.Center,
            Normal = sample.Normal,
            Tangent = sample.Tangent,
            DeckWidth = sample.Width,
            CenterZ = sample.CenterZ,
            LeftEdgeZ = sample.LeftEdgeZ,
            RightEdgeZ = sample.RightEdgeZ,
            SoffitZ = soffitZ,
            CapWidth = capWidth,
            Columns = columns,
        };
    }

    /// <summary>
    ///     Final hard-constraint check (§3b): 8 ring points at the pier's plan half-extent + the center
    ///     must clear every building polygon, and must not sit over a LOWER partner deck. Catches skewed
    ///     geometry the axis-interval projection can leak. Returns the violation label or null.
    /// </summary>
    private static string? ValidateFootprint(
        PierPlan plan, PlanInput input, SpanGeometryCache cache, float pierHalfExtent)
    {
        var probes = new List<Vector2>(9) { plan.Center };
        for (var i = 0; i < 8; i++)
        {
            var angle = MathF.PI * 2f * i / 8f;
            probes.Add(plan.Center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * pierHalfExtent);
        }

        if (input.Obstacles != null)
        {
            var min = plan.Center - new Vector2(pierHalfExtent);
            var max = plan.Center + new Vector2(pierHalfExtent);
            foreach (var feature in input.Obstacles.QueryAabb(min, max))
            {
                if (feature.Kind != BridgeObstacleKind.Building || !feature.IsPolygon) continue;
                if (input.Span.OsmWayIds.Contains(feature.OsmId)) continue;
                foreach (var p in probes)
                {
                    if (feature.ContainsPoint(p))
                        return $"building {feature.OsmId}";
                }
            }
        }

        if (input.AllSpans != null)
        {
            foreach (var partner in input.AllSpans)
            {
                if (partner.SpanId == plan.SpanId || partner.Stations.Count < 2) continue;
                var partnerGeometry = cache.Get(partner);
                // All probes lie within pierHalfExtent of the center — a partner whose (already
                // half-width-inflated) AABB can't contain any probe can't contain a violation.
                if (plan.Center.X + pierHalfExtent < partnerGeometry.Min.X ||
                    plan.Center.X - pierHalfExtent > partnerGeometry.Max.X ||
                    plan.Center.Y + pierHalfExtent < partnerGeometry.Min.Y ||
                    plan.Center.Y - pierHalfExtent > partnerGeometry.Max.Y)
                    continue;
                foreach (var p in probes)
                {
                    if (partnerGeometry.TryGetSurfaceZ(p, out var z) && z < plan.SoffitZ)
                        return $"lower deck (span {partner.SpanId})";
                }
            }
        }

        return null;
    }

    private static bool BoundsOverlap(SpanStationGeometry a, SpanStationGeometry b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;

    private static string DescribeTags(BridgeSpanSnapshot span)
    {
        if (span.OsmTags == null) return "none";
        var parts = new List<string>(2);
        foreach (var key in (string[]) ["bridge", "bridge:structure", "bridge:movable"])
        {
            if (span.OsmTags.TryGetValue(key, out var v))
                parts.Add($"{key}={v}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    /// <summary>
    ///     Caches one <see cref="SpanStationGeometry"/> per span snapshot (reference-keyed). Share one
    ///     instance across all <see cref="PlanSpan"/> calls of an export via
    ///     <see cref="PlanInput.GeometryCache"/> — but only build it AFTER the stations are final
    ///     (post <c>ExtendLandedEndsOntoPartnerDecks</c>), the geometry snapshots the station list.
    /// </summary>
    public sealed class SpanGeometryCache
    {
        private readonly Dictionary<BridgeSpanSnapshot, SpanStationGeometry> _bySpan =
            new(ReferenceEqualityComparer.Instance);

        internal SpanStationGeometry Get(BridgeSpanSnapshot span)
        {
            if (!_bySpan.TryGetValue(span, out var geometry))
                _bySpan[span] = geometry = new SpanStationGeometry(span.Stations);
            return geometry;
        }
    }

    // ========================================
    // STATION-FIELD GEOMETRY (snapshot analogue of BridgeSpanFootprint + doc-15 surface interp)
    // ========================================

    /// <summary>Interpolated pose of the deck at one arc-length station.</summary>
    internal readonly record struct StationSample(
        Vector2 Center, Vector2 Normal, Vector2 Tangent,
        float Width, float CenterZ, float LeftEdgeZ, float RightEdgeZ);

    /// <summary>
    ///     Plan/projection/surface queries over a span's station polyline (terrain-local meters). The
    ///     snapshot analogue of <see cref="Algorithms.BridgeSpanFootprint"/> plus the doc-15 surface-Z
    ///     interpolation, in one place for the pier planner.
    /// </summary>
    internal sealed class SpanStationGeometry
    {
        /// <summary>Segments per <see cref="Project"/> pruning chunk (perf; result-identical).</summary>
        private const int SegmentsPerChunk = 32;

        private readonly List<BridgeStation> _stations;
        private readonly Vector2[] _chunkMin;
        private readonly Vector2[] _chunkMax;

        public SpanStationGeometry(List<BridgeStation> stations)
        {
            _stations = stations;
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            var maxHalf = 0f;
            foreach (var st in stations)
            {
                min = Vector2.Min(min, st.Center);
                max = Vector2.Max(max, st.Center);
                maxHalf = MathF.Max(maxHalf, st.Width / 2f);
            }

            MaxHalfWidth = maxHalf;
            Min = min - new Vector2(maxHalf + 0.1f);
            Max = max + new Vector2(maxHalf + 0.1f);

            // Chunk AABBs over the segment polyline for Project() pruning. Slightly inflated so a
            // skipped chunk provably cannot beat the current best under the strict '<' update.
            var segCount = Math.Max(0, stations.Count - 1);
            var chunkCount = (segCount + SegmentsPerChunk - 1) / SegmentsPerChunk;
            _chunkMin = new Vector2[chunkCount];
            _chunkMax = new Vector2[chunkCount];
            for (var c = 0; c < chunkCount; c++)
            {
                var start = c * SegmentsPerChunk;
                var end = Math.Min(start + SegmentsPerChunk, segCount);
                var cmin = new Vector2(float.MaxValue);
                var cmax = new Vector2(float.MinValue);
                for (var i = start; i <= end; i++)
                {
                    cmin = Vector2.Min(cmin, stations[i].Center);
                    cmax = Vector2.Max(cmax, stations[i].Center);
                }

                _chunkMin[c] = cmin - new Vector2(1e-3f);
                _chunkMax[c] = cmax + new Vector2(1e-3f);
            }
        }

        public IReadOnlyList<BridgeStation> Stations => _stations;

        public Vector2 Min { get; }

        public Vector2 Max { get; }

        public float MaxHalfWidth { get; }

        /// <summary>Deck pose at arc-length <paramref name="s"/> (clamped to the span range).</summary>
        public StationSample Interpolate(float s)
        {
            var stations = _stations;
            if (s <= stations[0].DistanceAlongSpline) return ToSample(stations[0]);
            if (s >= stations[^1].DistanceAlongSpline) return ToSample(stations[^1]);

            // Binary search for the smallest hi with stations[hi].s >= s — same segment the previous
            // linear scan selected (invariant: stations[lo].s < s <= stations[hi].s).
            int lo = 0, hi = stations.Count - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (stations[mid].DistanceAlongSpline < s) lo = mid;
                else hi = mid;
            }

            var a = stations[lo];
            var b = stations[hi];
            var seg = b.DistanceAlongSpline - a.DistanceAlongSpline;
            var t = seg > 1e-6f ? (s - a.DistanceAlongSpline) / seg : 0f;
            return new StationSample(
                Vector2.Lerp(a.Center, b.Center, t),
                SafeNormalize(Vector2.Lerp(a.Normal, b.Normal, t)),
                SafeNormalize(Vector2.Lerp(a.Tangent, b.Tangent, t)),
                a.Width + (b.Width - a.Width) * t,
                a.CenterZ + (b.CenterZ - a.CenterZ) * t,
                a.LeftEdgeZ + (b.LeftEdgeZ - a.LeftEdgeZ) * t,
                a.RightEdgeZ + (b.RightEdgeZ - a.RightEdgeZ) * t);
        }

        /// <summary>Nearest point on the station polyline: arc-length station + plan distance.</summary>
        public (float S, float Distance) Project(Vector2 p)
        {
            var stations = _stations;
            var bestDistSq = float.MaxValue;
            var bestS = stations[0].DistanceAlongSpline;

            for (var c = 0; c < _chunkMin.Length; c++)
            {
                // Distance lower bound to the chunk AABB — a chunk that can't beat the current best
                // under the strict '<' update below is skipped without changing the result.
                var dx = MathF.Max(MathF.Max(_chunkMin[c].X - p.X, p.X - _chunkMax[c].X), 0f);
                var dy = MathF.Max(MathF.Max(_chunkMin[c].Y - p.Y, p.Y - _chunkMax[c].Y), 0f);
                if (dx * dx + dy * dy >= bestDistSq) continue;

                var start = c * SegmentsPerChunk;
                var end = Math.Min(start + SegmentsPerChunk, stations.Count - 1);
                for (var i = start; i < end; i++)
                {
                    var a = stations[i];
                    var b = stations[i + 1];
                    var ab = b.Center - a.Center;
                    var lenSq = ab.LengthSquared();
                    var t = lenSq > 1e-8f ? Math.Clamp(Vector2.Dot(p - a.Center, ab) / lenSq, 0f, 1f) : 0f;
                    var distSq = Vector2.DistanceSquared(p, a.Center + ab * t);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestS = a.DistanceAlongSpline + (b.DistanceAlongSpline - a.DistanceAlongSpline) * t;
                    }
                }
            }

            return (bestS, MathF.Sqrt(bestDistSq));
        }

        /// <summary>Plan containment in the swept deck footprint; outputs the projected station.</summary>
        public bool ContainsPlan(Vector2 p, out float s)
        {
            // Bounds first: Min/Max are inflated by the max half-width, so any contained point lies
            // inside them — rejecting here skips the O(stations) projection for the common far case.
            if (p.X < Min.X || p.X > Max.X || p.Y < Min.Y || p.Y > Max.Y)
            {
                s = 0f;
                return false;
            }

            (s, var dist) = Project(p);
            var half = Interpolate(s).Width / 2f;
            return dist <= half;
        }

        /// <summary>
        ///     Deck surface Z at a plan point strictly inside the footprint (station-lerped center Z +
        ///     lateral lerp toward the banked edge Z) — the doc-15 interpolation, snapshot-side.
        /// </summary>
        public bool TryGetSurfaceZ(Vector2 p, out float surfaceZ)
        {
            surfaceZ = float.NaN;
            if (!ContainsPlan(p, out var s)) return false;

            var sample = Interpolate(s);
            var half = sample.Width / 2f;
            if (half < 1e-3f) return false;

            var offset = Vector2.Dot(p - sample.Center, sample.Normal);
            var edgeZ = offset >= 0f ? sample.RightEdgeZ : sample.LeftEdgeZ;
            var frac = Math.Clamp(MathF.Abs(offset) / half, 0f, 1f);
            surfaceZ = sample.CenterZ + (edgeZ - sample.CenterZ) * frac;
            return true;
        }

        private static StationSample ToSample(BridgeStation st) => new(
            st.Center, SafeNormalize(st.Normal), SafeNormalize(st.Tangent),
            st.Width, st.CenterZ, st.LeftEdgeZ, st.RightEdgeZ);

        private static Vector2 SafeNormalize(Vector2 v)
        {
            var lenSq = v.LengthSquared();
            return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : Vector2.Zero;
        }
    }
}
