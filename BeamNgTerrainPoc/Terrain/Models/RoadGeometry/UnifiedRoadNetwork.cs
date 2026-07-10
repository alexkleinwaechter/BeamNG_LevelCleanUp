namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Unified road network containing all materials' roads.
/// This is the central data structure for the material-agnostic processing pipeline.
/// All roads from all materials are merged here for unified junction detection,
/// elevation harmonization, and terrain blending.
/// </summary>
public class UnifiedRoadNetwork
{
    /// <summary>
    /// All parameterized road splines from all materials.
    /// </summary>
    public List<ParameterizedRoadSpline> Splines { get; } = [];

    /// <summary>
    /// All cross-sections generated from all splines.
    /// Each cross-section references its owning spline via OwnerSplineId.
    /// </summary>
    public List<UnifiedCrossSection> CrossSections { get; } = [];

    /// <summary>
    /// Detected junctions in the unified network.
    /// Populated by NetworkJunctionDetector after splines and cross-sections are added.
    /// </summary>
    public List<NetworkJunction> Junctions { get; } = [];

    /// <summary>
    /// Grade-separated crossings (a road passing under a bridge, or two roads on different vertical
    /// layers). Populated by NetworkJunctionDetector INSTEAD of an at-grade MidSplineCrossing junction
    /// when the two splines are vertically separated. Consumed by GradeSeparationResolver (feature E-A).
    /// </summary>
    public List<GradeSeparatedCrossing> GradeSeparatedCrossings { get; } = [];

    /// <summary>
    /// Captured bridge spans (merged-corridor bridges, plan doc 11 §3, option B). Populated by
    /// <c>BridgeProfileSolver.RefineSpans</c> after the span elevation is finalised and BEFORE any
    /// heightmap carve. Empty in legacy (flag-off) mode. Consumed by the deck exporter / excavator / bridge
    /// DecalRoads — one deck per span, keyed by the OSM way-id set.
    /// </summary>
    public List<BridgeSpanSnapshot> BridgeSpans { get; } = [];

    /// <summary>
    /// The merged-corridor bridge-elevation plan (plan doc 14 §4, Phase C/D). Computed by
    /// <c>BridgeElevationPlanner.Plan</c> and stashed by <c>UnifiedRoadSmoother.ApplyBridgeDeckPins</c>
    /// (Phase 1.85) once the span decks have been pinned. Null in legacy (flag-off) mode — there are no spans
    /// to plan, so the post-smoothing <c>GradeSeparationResolver.ApplyLowerRoadDips</c> falls back to its
    /// priority-veto logic. When present, it tells that pass which crossings the rule engine assigned a
    /// dip/split (the only ones to lower against the final stamped deck Z), leaving raise/veto/already-clear
    /// crossings untouched.
    /// </summary>
    public Algorithms.BridgeElevationPlan? BridgeElevationPlan { get; set; }

    /// <summary>
    /// Junctions the bridge machinery holds HIGH (doc 08 §7 C3): M1 approach-ramp raises
    /// (<c>RaiseJunctionsAlongApproachRamps</c>) + on-deck junction pins (<c>PinOnDeckJunctions</c>),
    /// stashed by <c>UnifiedRoadSmoother.ApplyBridgeDeckPins</c> (sparse mode; empty otherwise). The
    /// affine junction leveling reads this to DECAY its endpoint correction on terminating side roads
    /// over a class-slope distance instead of tilting the whole road — the in-solver containment of the
    /// "Damm" propagation. Membership-only: the junction's Z keeps being re-derived per pass as usual.
    /// </summary>
    public HashSet<NetworkJunction> BridgeRaisedJunctions { get; } = [];

    /// <summary>
    /// The A0 early road-elevation estimate (doc 08 §5 D2), keyed by <see cref="UnifiedCrossSection.Index"/>.
    /// Built ONCE per run by <c>UnifiedRoadSmoother.SmoothAllRoads</c> (bridges on or off) against the
    /// pre-smoothing DEM, reused by the bridge planner (flag-gated) and read by the post-solve
    /// <c>RoadElevationDeviationReport</c> ("dam report") to quantify how far each road's final profile
    /// drifted from its natural elevation. Null when smoothing was skipped (all paint-only).
    /// </summary>
    public Dictionary<int, float>? EarlyElevationEstimate { get; set; }

    /// <summary>
    /// Maps SplineId -> MaterialName for the painting phase.
    /// Used to apply the correct terrain material texture to each road.
    /// </summary>
    public Dictionary<int, string> SplineMaterialMap { get; } = new();

    /// <summary>
    /// Maps SplineId -> ParameterizedRoadSpline for quick lookup.
    /// </summary>
    private readonly Dictionary<int, ParameterizedRoadSpline> _splineById = new();

    /// <summary>
    /// Thread lock for concurrent cross-section generation.
    /// </summary>
    private readonly object _crossSectionLock = new();

    /// <summary>
    /// Lazily built lookup: OwnerSplineId -> cross-sections ordered by LocalIndex.
    /// Rebuilt when <see cref="CrossSections"/> changes (tracked via count + explicit invalidation).
    /// Eliminates the former full-list scan + sort on every <see cref="GetCrossSectionsForSpline"/> call,
    /// which was O(splines × total cross-sections) across the junction-detection hot paths.
    /// </summary>
    private Dictionary<int, List<UnifiedCrossSection>>? _crossSectionsBySpline;

    /// <summary>
    /// Lazily built lookup: UnifiedCrossSection.Index -> cross-section (first occurrence wins,
    /// matching the FirstOrDefault semantics of the linear scans it replaces).
    /// </summary>
    private Dictionary<int, UnifiedCrossSection>? _crossSectionByIndex;

    /// <summary>
    /// CrossSections.Count at the time the lookups were built; -1 = not built.
    /// Guards against mutations that bypass <see cref="InvalidateCrossSectionCache"/>.
    /// </summary>
    private int _crossSectionCacheCount = -1;

    /// <summary>
    /// Adds a parameterized spline to the network.
    /// </summary>
    /// <param name="spline">The spline to add</param>
    public void AddSpline(ParameterizedRoadSpline spline)
    {
        Splines.Add(spline);
        SplineMaterialMap[spline.SplineId] = spline.MaterialName;
        _splineById[spline.SplineId] = spline;
    }

    /// <summary>
    /// Adds a cross-section to the network (thread-safe).
    /// </summary>
    /// <param name="crossSection">The cross-section to add</param>
    public void AddCrossSection(UnifiedCrossSection crossSection)
    {
        lock (_crossSectionLock)
        {
            CrossSections.Add(crossSection);
            _crossSectionsBySpline = null;
            _crossSectionByIndex = null;
            _crossSectionCacheCount = -1;
        }
    }

    /// <summary>
    /// Adds multiple cross-sections to the network (thread-safe batch operation).
    /// </summary>
    /// <param name="crossSections">The cross-sections to add</param>
    public void AddCrossSections(IEnumerable<UnifiedCrossSection> crossSections)
    {
        lock (_crossSectionLock)
        {
            CrossSections.AddRange(crossSections);
            _crossSectionsBySpline = null;
            _crossSectionByIndex = null;
            _crossSectionCacheCount = -1;
        }
    }

    /// <summary>
    /// Invalidates the cached cross-section lookups. MUST be called after mutating
    /// <see cref="CrossSections"/> directly (i.e. not via AddCrossSection/AddCrossSections),
    /// e.g. when removing or re-adding paint-only cross-sections.
    /// </summary>
    public void InvalidateCrossSectionCache()
    {
        lock (_crossSectionLock)
        {
            _crossSectionsBySpline = null;
            _crossSectionByIndex = null;
            _crossSectionCacheCount = -1;
        }
    }

    /// <summary>
    /// Gets a spline by its ID.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>The spline, or null if not found</returns>
    public ParameterizedRoadSpline? GetSplineById(int splineId)
    {
        return _splineById.GetValueOrDefault(splineId);
    }

    /// <summary>
    /// Gets the parameters for a spline by its ID.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>The parameters, or null if spline not found</returns>
    public RoadSmoothingParameters? GetParametersForSpline(int splineId)
    {
        return GetSplineById(splineId)?.Parameters;
    }

    /// <summary>
    /// Gets all cross-sections belonging to a specific spline.
    /// Served from a cached per-spline lookup; do NOT mutate the returned sequence.
    /// </summary>
    /// <param name="splineId">The spline ID</param>
    /// <returns>Cross-sections for the spline, ordered by local index</returns>
    public IEnumerable<UnifiedCrossSection> GetCrossSectionsForSpline(int splineId)
    {
        return GetCrossSectionsBySpline().TryGetValue(splineId, out var list)
            ? list
            : [];
    }

    /// <summary>
    /// Gets the cached lookup of all cross-sections grouped by owning spline,
    /// each list ordered by LocalIndex. Treat as read-only.
    /// </summary>
    public IReadOnlyDictionary<int, List<UnifiedCrossSection>> GetCrossSectionsBySpline()
    {
        lock (_crossSectionLock)
        {
            EnsureCrossSectionCache();
            return _crossSectionsBySpline!;
        }
    }

    /// <summary>
    /// Gets a cross-section by its network-wide <see cref="UnifiedCrossSection.Index"/>,
    /// or null if not present. Replaces linear FirstOrDefault scans over all cross-sections.
    /// </summary>
    public UnifiedCrossSection? GetCrossSectionByIndex(int index)
    {
        lock (_crossSectionLock)
        {
            EnsureCrossSectionCache();
            return _crossSectionByIndex!.GetValueOrDefault(index);
        }
    }

    /// <summary>
    /// Builds the cross-section lookups if missing or stale. Caller must hold _crossSectionLock.
    /// </summary>
    private void EnsureCrossSectionCache()
    {
        if (_crossSectionsBySpline != null && _crossSectionCacheCount == CrossSections.Count)
            return;

        var bySpline = new Dictionary<int, List<UnifiedCrossSection>>();
        var byIndex = new Dictionary<int, UnifiedCrossSection>(CrossSections.Count);
        foreach (var cs in CrossSections)
        {
            if (!bySpline.TryGetValue(cs.OwnerSplineId, out var list))
            {
                list = [];
                bySpline[cs.OwnerSplineId] = list;
            }

            list.Add(cs);
            byIndex.TryAdd(cs.Index, cs); // first occurrence wins (FirstOrDefault semantics)
        }

        // Stable sort (OrderBy) to exactly match the previous LINQ behaviour for duplicate LocalIndex values.
        foreach (var key in bySpline.Keys.ToList())
            bySpline[key] = bySpline[key].OrderBy(static cs => cs.LocalIndex).ToList();

        _crossSectionsBySpline = bySpline;
        _crossSectionByIndex = byIndex;
        _crossSectionCacheCount = CrossSections.Count;
    }

    /// <summary>
    /// Gets all splines from a specific material.
    /// </summary>
    /// <param name="materialName">The material name</param>
    /// <returns>Splines belonging to the material</returns>
    public IEnumerable<ParameterizedRoadSpline> GetSplinesForMaterial(string materialName)
    {
        return Splines.Where(s => s.MaterialName == materialName);
    }

    /// <summary>
    /// Gets all unique material names in the network.
    /// </summary>
    public IEnumerable<string> GetMaterialNames()
    {
        return SplineMaterialMap.Values.Distinct();
    }

    /// <summary>
    /// Gets network statistics for debugging and logging.
    /// </summary>
    public NetworkStatistics GetStatistics()
    {
        return new NetworkStatistics
        {
            TotalSplines = Splines.Count,
            TotalCrossSections = CrossSections.Count,
            TotalJunctions = Junctions.Count,
            MaterialCount = GetMaterialNames().Count(),
            TotalRoadLengthMeters = Splines.Sum(s => s.TotalLengthMeters),
            SplinesByMaterial = Splines
                .GroupBy(s => s.MaterialName)
                .ToDictionary(g => g.Key, g => g.Count()),
            JunctionsByType = Junctions
                .GroupBy(j => j.Type)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    /// <summary>
    /// Clears all data from the network.
    /// </summary>
    public void Clear()
    {
        Splines.Clear();
        CrossSections.Clear();
        InvalidateCrossSectionCache();
        Junctions.Clear();
        GradeSeparatedCrossings.Clear();
        BridgeSpans.Clear();
        SplineMaterialMap.Clear();
        _splineById.Clear();
    }
}

/// <summary>
/// Statistics about the unified road network.
/// </summary>
public class NetworkStatistics
{
    public int TotalSplines { get; init; }
    public int TotalCrossSections { get; init; }
    public int TotalJunctions { get; init; }
    public int MaterialCount { get; init; }
    public float TotalRoadLengthMeters { get; init; }
    public Dictionary<string, int> SplinesByMaterial { get; init; } = new();
    public Dictionary<JunctionType, int> JunctionsByType { get; init; } = new();

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Road Network Statistics:",
            $"  Splines: {TotalSplines}",
            $"  Cross-sections: {TotalCrossSections}",
            $"  Junctions: {TotalJunctions}",
            $"  Materials: {MaterialCount}",
            $"  Total road length: {TotalRoadLengthMeters:F1}m ({TotalRoadLengthMeters / 1000:F2}km)"
        };

        if (SplinesByMaterial.Count > 0)
        {
            lines.Add("  Splines by material:");
            foreach (var (material, count) in SplinesByMaterial.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"    {material}: {count}");
            }
        }

        if (JunctionsByType.Count > 0)
        {
            lines.Add("  Junctions by type:");
            foreach (var (type, count) in JunctionsByType.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"    {type}: {count}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
