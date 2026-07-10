# Bridge-to-Bridge Abutment Suppression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** No terrain terraforming (exclusion-shrink overlap zone, abutment tongue, excavator tongue ceiling) at bridge span ends where the road continues onto another bridge deck (doc 13 spec).

**Architecture:** A detection pre-pass inside `UnifiedRoadSmoother.MarkStructureExclusions` marks each bridge `StructureSegment` end with `StartContinuesOntoDeck`/`EndContinuesOntoDeck` (foreign-deck footprint hit or same-spline neighbour segment). Three consumers read the flags: the exclusion shrink becomes per-end, `BridgeAbutmentOverlapStamper` skips the suppressed run, `BridgeDeckExcavator` drops the overlap exemption at that end. All gated on new `BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression` (default false ⇒ byte-identical).

**Tech Stack:** .NET 9, xUnit (`BeamNgTerrainPoc.Tests`), existing test helpers `RoadNetworkTestHelpers`.

## Global Constraints

- Flag default **false**; flag off ⇒ byte-identical output (existing 744 tests must stay green untouched).
- Flag is **NOT** added to `BridgeRuleSystemOptions.AnyEnabled` (it must not activate the planner pipeline alone; it is consumed directly by exclusion marking / stamper / excavator).
- Foreign-deck hit rule: end cross-section center within `deckSection.EffectiveRoadWidth/2 + 1.0 m` of a deck section of ANOTHER spline (strictly ON the deck — parallel twin decks must not suppress each other).
- Same-spline rule: neighbouring `IsBridge` segment boundary within `2 × AbutmentOverlapMeters` of this end.
- Per suppressed end log: `[BRIDGE-B2B] span=<id> spline=<id> start|end lands on spline=<other>|continues into span=<other> — abutment suppressed` (file-only Detail).
- Test command: `dotnet test BeamNgTerrainPoc.Tests\BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~<TestClass>" -v minimal`

---

### Task 1: Flag + StructureSegment end flags

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs` (after `EnableContiguousSpanConsolidation`, line ~111)
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/StructureSegment.cs` (properties + `Clone`)
- Test: `BeamNgTerrainPoc.Tests/RoadGeometry/StructureSegmentOpsTests.cs`

**Interfaces:**
- Produces: `BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression : bool` (default false, NOT in `AnyEnabled`); `StructureSegment.StartContinuesOntoDeck : bool`, `StructureSegment.EndContinuesOntoDeck : bool` (both default false, copied by `Clone()`).

- [ ] **Step 1: Write the failing test** (append to `StructureSegmentOpsTests`):

```csharp
[Fact]
public void Clone_CopiesBridgeToBridgeContinuationFlags()
{
    var seg = new StructureSegment
    {
        Type = StructureType.Bridge,
        StartContinuesOntoDeck = true,
        EndContinuesOntoDeck = true,
    };

    var clone = seg.Clone();

    Assert.True(clone.StartContinuesOntoDeck);
    Assert.True(clone.EndContinuesOntoDeck);
}
```

- [ ] **Step 2: Run — expect FAIL** (compile error: property does not exist).
- [ ] **Step 3: Implement.** In `StructureSegment` after `LayerRanges`:

```csharp
    /// <summary>
    ///     Doc 13: this span end is a bridge-to-bridge continuation — the road continues onto another
    ///     deck (a foreign spline's span footprint, or a same-spline neighbour segment) instead of
    ///     meeting the ground. Abutment terraforming (exclusion-shrink overlap zone, overlap tongue,
    ///     excavator tongue ceiling) is suppressed at this end. Set by
    ///     <c>BridgeToBridgeContinuity.MarkContinuationEnds</c> only when
    ///     <c>BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression</c> is on.
    /// </summary>
    public bool StartContinuesOntoDeck { get; set; }

    /// <summary>See <see cref="StartContinuesOntoDeck"/> — the <see cref="EndDistance"/> side.</summary>
    public bool EndContinuesOntoDeck { get; set; }
```

Add to `Clone()` initializer: `StartContinuesOntoDeck = StartContinuesOntoDeck, EndContinuesOntoDeck = EndContinuesOntoDeck,`.

In `BridgeRuleSystemOptions` after `EnableContiguousSpanConsolidation`:

```csharp
    /// <summary>
    ///     Doc 13: suppress abutment terraforming at span ends whose road continues onto ANOTHER bridge
    ///     deck (ramp landing mid-span on a trunk, end-to-end spline handoffs of one physical structure).
    ///     Those ends kept getting the full ground-abutment package — 3 m non-excluded overlap zone
    ///     (Phase-4 embankment pillar up to the deck corner), overlap tongue, excavator tongue ceiling.
    ///     Deliberately NOT part of <see cref="AnyEnabled"/>: consumed directly by the exclusion marking /
    ///     stamper / excavator, must not activate the planner pipeline alone.
    /// </summary>
    public bool EnableBridgeToBridgeAbutmentSuppression { get; set; }
```

- [ ] **Step 4: Run — expect PASS.** `--filter "FullyQualifiedName~StructureSegmentOpsTests"`
- [ ] **Step 5: Commit** `feat(bridge): doc 13 - flag + StructureSegment continuation-end flags`

---

### Task 2: Detection helper + per-end exclusion shrink

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/BridgeToBridgeContinuity.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` — `MarkStructureExclusions` (line ~2062): call helper at top; per-end `overlapStart`/`overlapEnd`
- Test: `BeamNgTerrainPoc.Tests/Elevation/StructureExclusionMarkingTests.cs`

**Interfaces:**
- Consumes: Task 1 flags/properties.
- Produces: `internal static class BridgeToBridgeContinuity { internal static void MarkContinuationEnds(IEnumerable<ParameterizedRoadSpline> splines, Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline) }` — sets the segment flags; `MarkStructureExclusions` keeps its signature.

- [ ] **Step 1: Write the failing tests** (append to `StructureExclusionMarkingTests`):

```csharp
    // ── Doc 13: bridge-to-bridge abutment suppression ──────────────────────────────────────────

    private static BridgeRuleSystemOptions SuppressionRules() => new()
    {
        EnableSparseDeckConstraints = true, // the overlap shrink is sparse-mode only
        EnableBridgeToBridgeAbutmentSuppression = true,
    };

    /// <summary>Trunk (0,0)→(60,0) span [10,50]; ramp (30,30)→(30,1) whose span [5,29] ends ON the
    /// trunk deck (distance 1 m &lt; halfWidth 4 + 1). The ramp's landing end must stay fully excluded
    /// (no 3 m overlap zone); its ground start and both trunk ends keep today's shrink.</summary>
    private static (ParameterizedRoadSpline trunk, StructureSegment trunkSeg,
        ParameterizedRoadSpline ramp, StructureSegment rampSeg,
        Dictionary<int, List<UnifiedCrossSection>> bySpline)
        BuildRampLandingOnTrunk(bool suppressionOn = true)
    {
        var trunkSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 11L }
        };
        var trunk = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [trunkSeg]);

        var rampSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 5f, EndDistance = 29f, OsmWayIds = { 22L }
        };
        var ramp = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(30, 30), new Vector2(30, 1),
            mergeStructuresIntoCorridor: true, structureSegments: [rampSeg]);

        trunk.Parameters.BridgeRules = SuppressionRules();
        ramp.Parameters.BridgeRules = SuppressionRules();
        if (!suppressionOn)
        {
            trunk.Parameters.BridgeRules.EnableBridgeToBridgeAbutmentSuppression = false;
            ramp.Parameters.BridgeRules.EnableBridgeToBridgeAbutmentSuppression = false;
        }

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, trunk);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, ramp);
        return (trunk, trunkSeg, ramp, rampSeg, GroupBySpline(network));
    }

    private static UnifiedCrossSection At(Dictionary<int, List<UnifiedCrossSection>> bySpline,
        int splineId, float station) =>
        bySpline[splineId].OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    [Fact]
    public void RampLandingOnTrunkDeck_EndSuppressed_StaysFullyExcluded()
    {
        var (trunk, trunkSeg, ramp, rampSeg, bySpline) = BuildRampLandingOnTrunk();

        UnifiedRoadSmoother.MarkStructureExclusions([trunk, ramp], bySpline);

        Assert.True(rampSeg.EndContinuesOntoDeck);    // lands on the trunk deck
        Assert.False(rampSeg.StartContinuesOntoDeck); // ground abutment
        Assert.False(trunkSeg.StartContinuesOntoDeck);
        Assert.False(trunkSeg.EndContinuesOntoDeck);

        // Suppressed ramp end: the last 3 m stay EXCLUDED (no overlap zone → no embankment pillar).
        Assert.True(At(bySpline, 2, 28f).IsExcluded);
        // Ramp ground start keeps today's shrink: first 3 m of the span stay stampable road.
        Assert.False(At(bySpline, 2, 6f).IsExcluded);
        Assert.True(At(bySpline, 2, 10f).IsExcluded);
        // Trunk ends keep today's shrink.
        Assert.False(At(bySpline, 1, 11f).IsExcluded);
        Assert.False(At(bySpline, 1, 49f).IsExcluded);
        Assert.True(At(bySpline, 1, 15f).IsExcluded);
    }

    [Fact]
    public void ParallelTwinDecks_BesideNotOn_NoSuppression()
    {
        // Two parallel decks 10 m apart, width 8 (halfWidth 4 + 1 margin = 5 < 10): true shore
        // abutments must keep their overlap shrink — "beside a deck" is not "on a deck".
        var segA = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 31L }
        };
        var a = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [segA]);
        var segB = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 50f, OsmWayIds = { 32L }
        };
        var b = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(0, 10), new Vector2(60, 10),
            mergeStructuresIntoCorridor: true, structureSegments: [segB]);
        a.Parameters.BridgeRules = SuppressionRules();
        b.Parameters.BridgeRules = SuppressionRules();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, a);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, b);
        var bySpline = GroupBySpline(network);

        UnifiedRoadSmoother.MarkStructureExclusions([a, b], bySpline);

        Assert.False(segA.StartContinuesOntoDeck);
        Assert.False(segA.EndContinuesOntoDeck);
        Assert.False(segB.StartContinuesOntoDeck);
        Assert.False(segB.EndContinuesOntoDeck);
        Assert.False(At(bySpline, 1, 11f).IsExcluded); // shrink still applies
        Assert.False(At(bySpline, 2, 49f).IsExcluded);
    }

    [Fact]
    public void SameSplineNeighbourSegments_FacingEndsSuppressed()
    {
        // Two bridge segments on ONE spline with a 4 m gap (≤ 2 × AbutmentOverlapMeters = 6):
        // the facing ends are a continuation (un-consolidated same-spline joint), the outer ends
        // are ground abutments.
        var seg1 = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 10f, EndDistance = 20f, OsmWayIds = { 41L }
        };
        var seg2 = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 24f, EndDistance = 40f, OsmWayIds = { 42L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(60, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg1, seg2]);
        spline.Parameters.BridgeRules = SuppressionRules();

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        var bySpline = GroupBySpline(network);

        UnifiedRoadSmoother.MarkStructureExclusions([spline], bySpline);

        Assert.False(seg1.StartContinuesOntoDeck);
        Assert.True(seg1.EndContinuesOntoDeck);
        Assert.True(seg2.StartContinuesOntoDeck);
        Assert.False(seg2.EndContinuesOntoDeck);

        Assert.True(At(bySpline, 1, 19f).IsExcluded);  // facing end of seg1: no shrink
        Assert.True(At(bySpline, 1, 25f).IsExcluded);  // facing end of seg2: no shrink
        Assert.False(At(bySpline, 1, 11f).IsExcluded); // outer ends: today's shrink
        Assert.False(At(bySpline, 1, 39f).IsExcluded);
    }

    [Fact]
    public void SuppressionFlagOff_ByteIdenticalShrink()
    {
        var (trunk, trunkSeg, ramp, rampSeg, bySpline) = BuildRampLandingOnTrunk(suppressionOn: false);

        UnifiedRoadSmoother.MarkStructureExclusions([trunk, ramp], bySpline);

        Assert.False(rampSeg.EndContinuesOntoDeck);
        Assert.False(At(bySpline, 2, 28f).IsExcluded); // legacy: shrunk at the landing end too
    }
```

Add `using BeamNgTerrainPoc.Terrain.Models;` to the test file usings if missing.

- [ ] **Step 2: Run — expect FAIL** (`RampLandingOnTrunkDeck…` and `SameSplineNeighbourSegments…` fail on flag/exclusion asserts; `SuppressionFlagOff…`/`ParallelTwinDecks…` may already pass).
- [ ] **Step 3: Implement.** New file `BeamNgTerrainPoc/Terrain/Algorithms/BridgeToBridgeContinuity.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Doc 13 — bridge-to-bridge continuity detection. A physical structure is split across splines
///     (trunk + ramps) and, rarely, across un-consolidated same-spline segments; a span end where the
///     road continues onto ANOTHER deck is NOT a ground abutment, yet it kept getting the full abutment
///     package (3 m non-excluded overlap zone → Phase-4 ground-to-deck embankment pillar, overlap
///     tongue, excavator tongue ceiling) — terrain walls at mid-air deck corners (bridge_904452323,
///     bridge_1546435469). This pass marks such ends on the <see cref="StructureSegment"/> so the three
///     consumers suppress the treatment. Gated per spline on
///     <c>BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression</c>; off ⇒ no flags set ⇒
///     byte-identical.
/// </summary>
internal static class BridgeToBridgeContinuity
{
    /// <summary>Beyond the deck half-width, how much lateral slack still counts as ON the deck.
    /// Small on purpose: parallel twin decks (~10 m apart) must not suppress each other's true
    /// shore abutments.</summary>
    internal const float OnDeckMarginMeters = 1.0f;

    internal static void MarkContinuationEnds(
        IEnumerable<ParameterizedRoadSpline> splines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var all = splines as IReadOnlyList<ParameterizedRoadSpline> ?? splines.ToList();

        // Opt-in check first — building the deck index is pointless when nobody consumes it.
        if (!all.Any(s => s.Parameters.BridgeRules?.EnableBridgeToBridgeAbutmentSuppression == true))
            return;

        // Deck-section index over ALL splines' bridge segments (segment RANGES, not StructureSpanId
        // tags — the tags are not guaranteed to be set yet when the exclusion marking runs).
        var deckIndex = new List<(Vector2 Center, float HalfWidth, int SplineId)>();
        foreach (var spline in all)
        {
            if (spline.StructureSegments is not { Count: > 0 }) continue;
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var sections)) continue;

            foreach (var seg in spline.StructureSegments)
            {
                if (!seg.IsBridge) continue;
                foreach (var c in sections)
                    if (c.DistanceAlongSpline >= seg.StartDistance &&
                        c.DistanceAlongSpline <= seg.EndDistance)
                        deckIndex.Add((c.CenterPoint, c.EffectiveRoadWidth / 2f, spline.SplineId));
            }
        }

        if (deckIndex.Count == 0) return;

        foreach (var spline in all)
        {
            var rules = spline.Parameters.BridgeRules;
            if (rules?.EnableBridgeToBridgeAbutmentSuppression != true) continue;
            if (!spline.Parameters.MergeStructuresIntoCorridor) continue;
            if (spline.StructureSegments is not { Count: > 0 }) continue;
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var sections) ||
                sections.Count == 0) continue;

            var bridgeSegs = spline.StructureSegments.Where(s => s.IsBridge)
                .OrderBy(s => s.StartDistance).ToList();
            var neighbourTolerance = 2f * MathF.Max(0f, rules.AbutmentOverlapMeters);

            foreach (var seg in bridgeSegs)
            {
                seg.StartContinuesOntoDeck = EndContinuesOntoDeck(
                    spline, seg, seg.StartDistance, bridgeSegs, neighbourTolerance,
                    sections, deckIndex);
                seg.EndContinuesOntoDeck = EndContinuesOntoDeck(
                    spline, seg, seg.EndDistance, bridgeSegs, neighbourTolerance,
                    sections, deckIndex);
            }
        }
    }

    private static bool EndContinuesOntoDeck(
        ParameterizedRoadSpline spline,
        StructureSegment seg,
        float endStation,
        List<StructureSegment> sameSplineBridgeSegs,
        float neighbourTolerance,
        List<UnifiedCrossSection> sections,
        List<(Vector2 Center, float HalfWidth, int SplineId)> deckIndex)
    {
        // Same-spline neighbour segment within tolerance (un-consolidated same-spline joint).
        foreach (var other in sameSplineBridgeSegs)
        {
            if (ReferenceEquals(other, seg)) continue;
            if (MathF.Abs(other.StartDistance - endStation) <= neighbourTolerance ||
                MathF.Abs(other.EndDistance - endStation) <= neighbourTolerance)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"[BRIDGE-B2B] span={seg.SpanId} spline={spline.SplineId} " +
                    $"{(endStation <= seg.StartDistance ? "start" : "end")} continues into span={other.SpanId} " +
                    "— abutment suppressed");
                return true;
            }
        }

        // Foreign-deck landing: the end cross-section sits ON another spline's deck footprint.
        UnifiedCrossSection? endCs = null;
        var best = float.MaxValue;
        foreach (var c in sections)
        {
            var d = MathF.Abs(c.DistanceAlongSpline - endStation);
            if (d < best)
            {
                best = d;
                endCs = c;
            }
        }

        if (endCs == null) return false;

        foreach (var (center, halfWidth, splineId) in deckIndex)
        {
            if (splineId == spline.SplineId) continue;
            if (Vector2.Distance(center, endCs.CenterPoint) <= halfWidth + OnDeckMarginMeters)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"[BRIDGE-B2B] span={seg.SpanId} spline={spline.SplineId} " +
                    $"{(endStation <= seg.StartDistance ? "start" : "end")} lands on spline={splineId} " +
                    "— abutment suppressed");
                return true;
            }
        }

        return false;
    }
}
```

In `MarkStructureExclusions` (UnifiedRoadSmoother.cs:2062): materialize the enumerable once and call the helper first, then make the shrink per-end:

```csharp
    internal static void MarkStructureExclusions(
        IEnumerable<ParameterizedRoadSpline> splines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline)
    {
        var allSplines = splines as IReadOnlyList<ParameterizedRoadSpline> ?? splines.ToList();

        // Doc 13: mark span ends that continue onto another deck BEFORE shrinking exclusions —
        // such ends get NO overlap zone (and no tongue / excavator exemption downstream).
        BridgeToBridgeContinuity.MarkContinuationEnds(allSplines, crossSectionsBySpline);

        foreach (var spline in allSplines)
```

and replace the marking condition (inside the `foreach (var seg …)` loop, after `overlap` is computed):

```csharp
                    // Doc 13: a bridge-to-bridge continuation end keeps the FULL exclusion — its 3 m
                    // would otherwise be stamped as ordinary road at deck Z and Phase 4 would blend a
                    // ground-to-deck embankment pillar under a mid-air deck corner.
                    var overlapStart = seg.StartContinuesOntoDeck ? 0f : overlap;
                    var overlapEnd = seg.EndContinuesOntoDeck ? 0f : overlap;

                    var spanId = seg.SpanId;
                    var marked = 0;
                    foreach (var c in spanCs)
                    {
                        if (c.DistanceAlongSpline < seg.StartDistance ||
                            c.DistanceAlongSpline > seg.EndDistance)
                            continue;
                        c.StructureSpanId = spanId;
                        if (c.DistanceAlongSpline >= seg.StartDistance + overlapStart &&
                            c.DistanceAlongSpline <= seg.EndDistance - overlapEnd)
                        {
                            c.IsExcluded = true;
                            marked++;
                        }
                    }
```

Add `using BeamNgTerrainPoc.Terrain.Algorithms;` to UnifiedRoadSmoother.cs usings if missing (it already has it — verify).

- [ ] **Step 4: Run — expect PASS** for all four new tests AND the pre-existing `StructureExclusionMarkingTests` + `BridgeAbutmentOverlapTests.MarkStructureExclusions_*`.
- [ ] **Step 5: Commit** `feat(bridge): doc 13 - detect bridge-to-bridge span ends, keep full exclusion there`

---

### Task 3: Stamper skips suppressed ends

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Export/BridgeAbutmentOverlapStamper.cs` (`Stamp`, lines ~47–91)
- Test: `BeamNgTerrainPoc.Tests/Elevation/BridgeAbutmentOverlapTests.cs`

**Interfaces:**
- Consumes: `StructureSegment.StartContinuesOntoDeck`/`EndContinuesOntoDeck` (Task 1), looked up via `spline.StructureSegments` matching `deck[0].StructureSpanId`.

- [ ] **Step 1: Write the failing test** (append to `BridgeAbutmentOverlapTests`; `BuildCorridor` fixture already exists in the class — deck 14.7, terrain 10, span [100,200], world x = 50 + station):

```csharp
    [Fact]
    public void Stamp_SuppressedEnd_NoTongue_OtherEndStillStamped()
    {
        // Doc 13: a bridge-to-bridge continuation end gets NO tongue — terrain must not rise to a
        // mid-air deck corner. The opposite (ground) end keeps the doc-06 tongue.
        var network = BuildCorridor();
        var seg = network.GetSplineById(1)!.StructureSegments![0];
        seg.EndContinuesOntoDeck = true;
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        BridgeAbutmentOverlapStamper.Stamp(network, hm, 1f, log: false);

        Assert.Equal(14.69f, hm[150, 151], 0.01f); // station 101 — start tongue unchanged
        Assert.Equal(10f, hm[150, 249], 0.001f);   // station 199 — suppressed end: no tongue
        Assert.Equal(10f, hm[150, 251], 0.001f);   // station 201 — approach boundary also untouched
    }
```

- [ ] **Step 2: Run — expect FAIL** (`hm[150,249]` is 14.69, not 10).
- [ ] **Step 3: Implement.** In `Stamp`, after the `rules` check (line ~52), look up the segment and gate the two runs:

```csharp
            // Doc 13: a span end that continues onto another deck is not a ground abutment — no tongue.
            var seg = spline.StructureSegments?.FirstOrDefault(s => s.SpanId == deck[0].StructureSpanId);
            var suppressStart = seg?.StartContinuesOntoDeck == true;
            var suppressEnd = seg?.EndContinuesOntoDeck == true;
```

Wrap the run building + stamping:

```csharp
            var stampedCells = 0;
            if (!suppressStart)
            {
                var startRun = new List<UnifiedCrossSection>();
                if (approachBefore != null) startRun.Add(approachBefore);
                startRun.AddRange(deck.Where(c => c.DistanceAlongSpline - first <= overlap));
                stampedCells += StampRun(startRun, deck[0].StructureSpanId, deck[0].OwnerSplineId, drop,
                    affectedRange, heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                    roadSurfaceOwner, deckFootprint, maxLiftAllowed, ref maxLift);
            }

            if (!suppressEnd)
            {
                var endRun = deck.Where(c => last - c.DistanceAlongSpline <= overlap).ToList();
                if (approachAfter != null) endRun.Add(approachAfter);
                stampedCells += StampRun(endRun, deck[0].StructureSpanId, deck[0].OwnerSplineId, drop,
                    affectedRange, heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                    roadSurfaceOwner, deckFootprint, maxLiftAllowed, ref maxLift);
            }
```

(The `approachBefore`/`approachAfter` lookups can move inside their respective `if` blocks.)

- [ ] **Step 4: Run — expect PASS** for the new test AND all pre-existing `BridgeAbutmentOverlapTests`.
- [ ] **Step 5: Commit** `feat(bridge): doc 13 - no abutment tongue at bridge-to-bridge span ends`

---

### Task 4: Excavator drops the overlap exemption at suppressed ends

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Export/BridgeDeckExcavator.cs` (`Excavate`, lines ~75–92)
- Test: `BeamNgTerrainPoc.Tests/Elevation/BridgeAbutmentOverlapTests.cs`

**Interfaces:**
- Consumes: segment flags via `network.GetSplineById(deckSections[0].OwnerSplineId)` + `StructureSpanId` match. Verify `CollectDeckGroups` groups by `(OwnerSplineId, StructureSpanId)` — if a group's `StructureSpanId` is −1 (legacy whole-spline), the lookup yields null ⇒ both ends un-suppressed ⇒ legacy behaviour.

- [ ] **Step 1: Write the failing test** (append to `BridgeAbutmentOverlapTests`):

```csharp
    [Fact]
    public void Excavator_SuppressedEnd_FullUndercut_NoTongueCeiling()
    {
        // Doc 13: no tongue at a bridge-to-bridge end ⇒ no exemption either — poking terrain there is
        // shaved to the regular undercut, so nothing can survive at the mid-air deck corner.
        var network = BuildCorridor();
        var seg = network.GetSplineById(1)!.StructureSegments![0];
        seg.EndContinuesOntoDeck = true;
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 20f);

        BridgeDeckExcavator.Excavate(network, hm, 1f, undercutMeters: 0.05f,
            abutmentOverlapMeters: 3f, abutmentOverlapDropMeters: 0.03f, log: false);

        Assert.Equal(14.67f, hm[150, 151], 0.005f); // start overlap keeps the tongue ceiling
        Assert.Equal(14.65f, hm[150, 249], 0.005f); // suppressed end: full undercut
    }
```

- [ ] **Step 2: Run — expect FAIL** (`hm[150,249]` is 14.67).
- [ ] **Step 3: Implement.** In `Excavate`, at the top of the `foreach (var deckSections in deckGroups)` loop:

```csharp
            // Doc 13: no tongue at a bridge-to-bridge continuation end ⇒ no exemption either.
            var ownerSeg = network.GetSplineById(deckSections[0].OwnerSplineId)
                ?.StructureSegments?.FirstOrDefault(s => s.SpanId == deckSections[0].StructureSpanId);
            var suppressStart = ownerSeg?.StartContinuesOntoDeck == true;
            var suppressEnd = ownerSeg?.EndContinuesOntoDeck == true;
```

and change the `inOverlap` computation:

```csharp
                var inOverlap = abutmentOverlapMeters > 0f &&
                                ((cs.DistanceAlongSpline - firstDist <= abutmentOverlapMeters && !suppressStart) ||
                                 (lastDist - cs.DistanceAlongSpline <= abutmentOverlapMeters && !suppressEnd));
```

- [ ] **Step 4: Run — expect PASS** for the new test AND `Excavator_ExemptsOverlap_KeepsTongueCeiling` / `StampThenExcavate_TongueSurvives`.
- [ ] **Step 5: Commit** `feat(bridge): doc 13 - full excavator undercut at bridge-to-bridge span ends`

---

### Task 5: Junction gap fill verification (spec §3.3) + full suite

**Files:**
- Inspect: `BeamNgTerrainPoc/Terrain/Export/RoadMaskBuilder.cs` (junction gap fill, "Filled N junction gap pixels")

- [ ] **Step 1:** Read the junction gap fill implementation. Question: can it paint a disk at `HarmonizedElevation` at a junction whose contributor sections are all `IsExcluded` (deck-deck junction, j103/j106 pattern)? If the fill only bridges gaps BETWEEN already-stamped road pixels (or skips excluded contributors), nothing to do.
- [ ] **Step 2:** If it CAN paint there: add a failing test (junction between two excluded deck sections → no painted pixels), then gate the disk on contributor exclusion. If it cannot: record the finding in the Task 6 commit message — no code change.
- [ ] **Step 3:** Run the FULL suite: `dotnet test BeamNgTerrainPoc.Tests\BeamNgTerrainPoc.Tests.csproj -v minimal` — expect ≥ 750 passed, 0 failed.

---

### Task 6: Preset opt-in, docs, memory, final commit

**Files:**
- Modify: `d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json` (outside repo — add `"EnableBridgeToBridgeAbutmentSuppression": true` inside the `bridgeRules` node, next to `EnableContiguousSpanConsolidation`)
- Modify: `ai_docs/2026-06-10_bridge_generation_V2/13-bridge-to-bridge-abutment-suppression-spec.md` (status line → implemented, commits)

- [ ] **Step 1:** Add the preset flag (preserve the file's existing formatting; `bridgeRules` round-trips whole-object).
- [ ] **Step 2:** Update doc 13 status; update memory (`bridge_rule_system_v2.md` + `MEMORY.md`).
- [ ] **Step 3:** Commit `feat(bridge): doc 13 - bridge-to-bridge abutment suppression complete` (any remaining files).
- [ ] **Step 4:** Hand to user: rebuild (app closed), regen manhattan 4096, check `[BRIDGE-B2B]` lines (expect span 904452323 both ends + the 70→14 handoff family), render: no terrain pillars at deck corners of bridge_904452323 / bridge_1546435469; true ground abutments unchanged.
