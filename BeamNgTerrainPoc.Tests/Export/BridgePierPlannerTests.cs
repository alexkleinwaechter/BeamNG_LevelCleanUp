using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Doc 19 §3 — pier placement: archetype from OSM tags, nominal stations at the spacing knob,
///     forbidden intervals from road crossings / OSM obstacles / buildings / lower decks, and the
///     slide-or-skip resolution whose keep-out is ABSOLUTE (a pier never stands on an obstacle; the
///     bay just grows). Fixture: straight flat 200 m deck along X at y=100, 8 m wide, deck Z 10,
///     ground 0 ⇒ usable [6, 194], 6 nominal piers at s ≈ 32.9/59.7/86.6/113.4/140.3/167.1,
///     pier half-extent ≈ 3.58 m (7 m cap × 1.5 m).
/// </summary>
public class BridgePierPlannerTests
{
    private const int SplineId = 1;
    private const float DeckZ = 10f;

    // 200 m span, 0.05·span ratio clamps to max 1.2 ⇒ soffit at 8.8.
    private const float SoffitZ = 8.8f;

    private static BridgeSpanSnapshot MakeSpan(
        float length = 200f,
        float width = 8f,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        var stations = new List<BridgeStation>();
        for (var d = 0f; d <= length + 0.01f; d += 5f)
        {
            stations.Add(new BridgeStation
            {
                Center = new Vector2(d, 100f),
                Normal = new Vector2(0f, -1f),
                Tangent = new Vector2(1f, 0f),
                Width = width,
                CenterZ = DeckZ, LeftEdgeZ = DeckZ, RightEdgeZ = DeckZ,
                DistanceAlongSpline = d,
            });
        }

        return new BridgeSpanSnapshot
        {
            SplineId = SplineId, SpanId = 42, OsmWayIds = { 111L }, OsmTags = tags, Stations = stations,
        };
    }

    private static List<PierPlan> Plan(
        BridgeSpanSnapshot span,
        float groundZ = 0f,
        BridgeObstacleSet? obstacles = null,
        IReadOnlyList<GradeSeparatedCrossing>? crossings = null,
        IReadOnlyList<BridgeSpanSnapshot>? allSpans = null,
        List<string>? log = null,
        StructureSegment? segment = null,
        BridgeRuleSystemOptions? options = null)
    {
        return BridgePierPlanner.PlanSpan(new BridgePierPlanner.PlanInput
        {
            Span = span,
            Segment = segment,
            Options = options ?? new BridgeRuleSystemOptions { EnableBridgePiers = true },
            DeckProfile = new BridgeDeckProfile(),
            Obstacles = obstacles,
            Crossings = crossings,
            LowerRoadHalfWidth = _ => 4f,
            AllSpans = allSpans,
            GroundZ = _ => groundZ,
            Log = log != null ? log.Add : null,
        });
    }

    private static GradeSeparatedCrossing RoadCrossingAt(float x, int lowerSplineId = 7) => new()
    {
        UpperSplineId = SplineId, LowerSplineId = lowerSplineId,
        CrossingXY = new Vector2(x, 100f),
        UpperLayer = 1, LowerLayer = 0, UpperPriority = 100, LowerPriority = 50,
        UpperIsBridge = true, LowerIsBridge = false,
    };

    private static BridgeObstacleFeature PolygonFeature(
        long id, BridgeObstacleKind kind, float minX, float maxX, float minY, float maxY,
        bool navigable = false)
    {
        var points = new List<Vector2>
        {
            new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY),
        };
        return new BridgeObstacleFeature
        {
            OsmId = id, Kind = kind, Points = points, IsPolygon = true, Navigable = navigable,
            Min = new Vector2(minX, minY), Max = new Vector2(maxX, maxY),
        };
    }

    // ========================================
    // INTERVAL ALGEBRA (§3b/§3c primitives)
    // ========================================

    [Fact]
    public void Intervals_BlockAndSlide()
    {
        var f = new PierForbiddenIntervals();
        f.Add(40f, 60f, "road 1");
        f.Add(55f, 65f, "rail 2");

        Assert.Null(f.BlockedBy(30f));
        Assert.Equal("road 1", f.BlockedBy(50f));
        Assert.Equal("rail 2", f.BlockedBy(62f));

        // Nominal 50 blocked; nearest allowed is the LOW side (|50−39.75| < |50−65.25|).
        var slid = f.NearestAllowed(50f, maxSlide: 12f, usableStart: 0f, usableEnd: 200f);
        Assert.NotNull(slid);
        Assert.True(slid < 40f && slid >= 38f, $"expected just below 40, got {slid}");

        // Usable window excludes the low side ⇒ must go high, past BOTH intervals.
        var high = f.NearestAllowed(50f, maxSlide: 20f, usableStart: 45f, usableEnd: 200f);
        Assert.NotNull(high);
        Assert.True(high > 65f, $"expected past 65, got {high}");

        // Fully blocked window ⇒ null (skip — the constraint is absolute).
        Assert.Null(f.NearestAllowed(50f, maxSlide: 5f, usableStart: 0f, usableEnd: 200f));
    }

    [Fact]
    public void Intervals_ExtraPredicateGatesSlideTargets()
    {
        var f = new PierForbiddenIntervals();
        f.Add(40f, 60f, "road");

        // The low side is geometrically free but the predicate (deck-too-low) rejects it.
        var slid = f.NearestAllowed(50f, 15f, 0f, 200f, s => s > 55f);
        Assert.NotNull(slid);
        Assert.True(slid > 60f, $"expected the high side, got {slid}");
    }

    // ========================================
    // NOMINAL PLACEMENT (§3a)
    // ========================================

    [Fact]
    public void LongStraightSpan_PlacesEvenSingleColumnPiers()
    {
        var log = new List<string>();
        var piers = Plan(MakeSpan(), log: log);

        Assert.Equal(6, piers.Count);
        Assert.All(piers, p =>
        {
            Assert.Equal(BridgePierArchetype.ColumnPier, p.Archetype);
            Assert.False(p.WasSlid);
            var column = Assert.Single(p.Columns);
            Assert.Equal(0f, column.GroundZ);
            Assert.Equal(-1.5f, column.BottomZ, 3); // ground − PierGroundEmbedMeters
            Assert.Equal(1.2f, column.Diameter, 3);
            Assert.Equal(SoffitZ, p.SoffitZ, 2);
            Assert.Equal(7f, p.CapWidth, 2); // 8 − 2·0.5
        });

        // Equal bays over the usable interval.
        var gaps = piers.Zip(piers.Skip(1), (a, b) => b.Station - a.Station).ToList();
        Assert.All(gaps, g => Assert.Equal(gaps[0], g, 1));
        Assert.Contains(log, l => l.Contains("6/6 pier(s) placed"));
    }

    [Fact]
    public void ShortSpan_NoPiers()
    {
        Assert.Empty(Plan(MakeSpan(length: 30f)));
    }

    [Fact]
    public void WideDeck_GetsTwinColumns()
    {
        var piers = Plan(MakeSpan(width: 12f));
        Assert.NotEmpty(piers);
        Assert.All(piers, p =>
        {
            Assert.Equal(2, p.Columns.Count);
            // ±0.3·width along the normal.
            Assert.Equal(0.6f * 12f, Vector2.Distance(p.Columns[0].Center, p.Columns[1].Center), 2);
        });
    }

    [Fact]
    public void DeckTooLow_AllPiersDropped()
    {
        var log = new List<string>();
        // Soffit 8.8, ground 7 ⇒ 1.8 m < MinPierHeightMeters 2.5 — embankment zone, stub piers are clutter.
        var piers = Plan(MakeSpan(), groundZ: 7f, log: log);

        Assert.Empty(piers);
        Assert.Contains(log, l => l.Contains("deck too low") && l.Contains("dropped"));
    }

    [Fact]
    public void CrossSlopeGround_ColumnUsesLowestCorner()
    {
        var span = MakeSpan();
        var piers = BridgePierPlanner.PlanSpan(new BridgePierPlanner.PlanInput
        {
            Span = span,
            Options = new BridgeRuleSystemOptions { EnableBridgePiers = true },
            DeckProfile = new BridgeDeckProfile(),
            // Sloped ground: z falls with y — footprint corners straddle the slope.
            GroundZ = p => (100f - p.Y) * 0.5f,
        });

        Assert.NotEmpty(piers);
        var column = piers[0].Columns[0];
        // Lowest corner is at y = center + 0.6 (diameter 1.2 ⇒ half 0.6): z = −0.3.
        Assert.Equal(-0.3f, column.GroundZ, 2);
        Assert.Equal(-1.8f, column.BottomZ, 2);
    }

    // ========================================
    // ARCHETYPES (§2)
    // ========================================

    [Theory]
    [InlineData("suspension")]
    [InlineData("cable-stayed")]
    [InlineData("simple-suspension")]
    [InlineData("floating")]
    public void SuspensionClassStructure_NoSupports(string structure)
    {
        var log = new List<string>();
        var piers = Plan(MakeSpan(), log: log,
            segment: new StructureSegment { BridgeStructureType = structure });

        Assert.Empty(piers);
        Assert.Contains(log, l => l.Contains("NoSupports"));
    }

    [Fact]
    public void MovableTag_NoSupports()
    {
        var tags = new Dictionary<string, string> { ["bridge"] = "movable", ["bridge:movable"] = "bascule" };
        Assert.Empty(Plan(MakeSpan(tags: tags)));
    }

    [Fact]
    public void ViaductTag_KeepsRhythmByShiftingAllPiers()
    {
        var tags = new Dictionary<string, string> { ["bridge"] = "viaduct" };
        var log = new List<string>();
        // One road crossing dead on a nominal station: ColumnPier would slide ONE pier; the viaduct
        // shifts the whole ladder so the bays stay equal (the repetition optic).
        var piers = Plan(MakeSpan(tags: tags), crossings: [RoadCrossingAt(86.57f)], log: log);

        Assert.Equal(6, piers.Count);
        Assert.All(piers, p => Assert.Equal(BridgePierArchetype.ViaductPier, p.Archetype));
        var gaps = piers.Zip(piers.Skip(1), (a, b) => b.Station - a.Station).ToList();
        Assert.All(gaps, g => Assert.Equal(gaps[0], g, 1));
        Assert.Contains(log, l => l.Contains("rhythm shifted"));
    }

    [Fact]
    public void TrestleTag_DenseSlenderTwinBents()
    {
        var tags = new Dictionary<string, string> { ["bridge"] = "trestle" };
        var piers = Plan(MakeSpan(length: 100f), options: null, log: null,
            segment: null, obstacles: null, crossings: null, allSpans: null, groundZ: 0f);
        var trestle = Plan(MakeSpan(length: 100f, tags: tags));

        Assert.True(trestle.Count > piers.Count, "trestle spacing (12 m) must place more bents than 30 m default");
        Assert.All(trestle, p =>
        {
            Assert.Equal(BridgePierArchetype.TrestleBent, p.Archetype);
            Assert.Equal(2, p.Columns.Count);
            Assert.All(p.Columns, c => Assert.Equal(0.5f, c.Diameter, 3));
        });
    }

    // ========================================
    // KEEP-OUT (§3b/§3c) — the hard constraint
    // ========================================

    [Fact]
    public void RoadCrossingMidBay_PierSlidesPastMargin()
    {
        const float crossingX = 86.57f; // exactly nominal pier 2
        var log = new List<string>();
        var piers = Plan(MakeSpan(), crossings: [RoadCrossingAt(crossingX)], log: log);

        Assert.Equal(6, piers.Count);
        // margin = lowerHalf 4 + pierHalfExtent ≈3.58 + clearance 3 ≈ 10.58.
        Assert.All(piers, p => Assert.True(MathF.Abs(p.Station - crossingX) >= 10.5f,
            $"pier at s={p.Station:F1} inside the keep-out of the road at {crossingX}"));
        Assert.Contains(piers, p => p.WasSlid);
        Assert.Contains(log, l => l.Contains("slid"));
    }

    [Fact]
    public void BracketingObstacles_PierIsSkippedNeverPlacedOnObstacle()
    {
        var log = new List<string>();
        // Two roads bracket nominal pier 2 (86.57): their keep-outs cover the whole ±12 slide window.
        var piers = Plan(MakeSpan(), crossings: [RoadCrossingAt(76f), RoadCrossingAt(97f)], log: log);

        Assert.Equal(5, piers.Count);
        Assert.All(piers, p =>
        {
            Assert.True(MathF.Abs(p.Station - 76f) >= 10.5f);
            Assert.True(MathF.Abs(p.Station - 97f) >= 10.5f);
        });
        Assert.Contains(log, l => l.Contains("skipped") && l.Contains("blocked by road"));
    }

    [Fact]
    public void BuildingUnderHalfSpan_AllPiersOnTheOtherHalf()
    {
        // Building polygon under the first 100 m of the deck (stations walk catches the interior,
        // ring runs catch the edges).
        var building = PolygonFeature(9001, BridgeObstacleKind.Building, 0f, 100f, 90f, 110f);
        var piers = Plan(MakeSpan(), obstacles: new BridgeObstacleSet([building]));

        Assert.Equal(3, piers.Count);
        Assert.All(piers, p => Assert.True(p.Station > 106f,
            $"pier at s={p.Station:F1} stands on/next to the building"));
    }

    [Fact]
    public void NavigableWater_ForbidsWetInterval_NonNavigableAllowsButLogs()
    {
        var log = new List<string>();
        var navigable = PolygonFeature(8001, BridgeObstacleKind.Water, 80f, 120f, 60f, 140f, navigable: true);
        var piersNav = Plan(MakeSpan(), obstacles: new BridgeObstacleSet([navigable]), log: log);

        Assert.Equal(4, piersNav.Count); // both mid-river piers skipped — no columns in a shipping channel
        Assert.All(piersNav, p => Assert.True(p.Station < 73f || p.Station > 127f));

        var river = PolygonFeature(8002, BridgeObstacleKind.Water, 80f, 120f, 60f, 140f);
        var logRiver = new List<string>();
        var piersRiver = Plan(MakeSpan(), obstacles: new BridgeObstacleSet([river]), log: logRiver);

        Assert.Equal(6, piersRiver.Count); // real piers stand in rivers (v1: allowed, logged)
        Assert.Contains(logRiver, l => l.Contains("non-navigable water") && l.Contains("allowed"));
    }

    [Fact]
    public void SelfWays_NeverCountAsObstacles()
    {
        // The span's own OSM way sits in the obstacle set as a Road feature (merged corridors, doc 19 §7)
        // — without the id guard it would forbid every pier of its own deck.
        var self = new BridgeObstacleFeature
        {
            OsmId = 111L, Kind = BridgeObstacleKind.Road,
            Points = [new Vector2(0f, 100f), new Vector2(200f, 100f)],
            Min = new Vector2(0f, 100f), Max = new Vector2(200f, 100f),
        };
        var piers = Plan(MakeSpan(), obstacles: new BridgeObstacleSet([self]));

        Assert.Equal(6, piers.Count);
    }

    [Fact]
    public void LowerDeckBelow_Excluded_CoplanarMergeAllowed()
    {
        static BridgeSpanSnapshot Partner(float z)
        {
            var stations = new List<BridgeStation>();
            for (var y = 40f; y <= 160f; y += 5f)
            {
                stations.Add(new BridgeStation
                {
                    Center = new Vector2(87f, y),
                    Normal = new Vector2(1f, 0f),
                    Tangent = new Vector2(0f, 1f),
                    Width = 8f,
                    CenterZ = z, LeftEdgeZ = z, RightEdgeZ = z,
                    DistanceAlongSpline = y - 40f,
                });
            }

            return new BridgeSpanSnapshot
            {
                SplineId = 2, SpanId = 77, OsmWayIds = { 222L }, Stations = stations,
            };
        }

        // Partner deck 5 m BELOW our soffit (stacked) crossing at x=87 — nominal pier 2 (86.57) must move.
        var below = Plan(MakeSpan(), allSpans: [MakeSpan(), Partner(5f)]);
        Assert.All(below, p => Assert.True(MathF.Abs(p.Station - 87f) > 7f,
            $"pier at s={p.Station:F1} drops through the lower deck at x=87"));

        // Coplanar merge partner (same roadway surface, z 10 ≥ our soffit) does NOT repel piers.
        var coplanar = Plan(MakeSpan(), allSpans: [MakeSpan(), Partner(10f)]);
        Assert.Contains(coplanar, p => MathF.Abs(p.Station - 86.57f) < 1f);
    }

    [Fact]
    public void Determinism_SameInputsSamePiers()
    {
        var obstacles = new BridgeObstacleSet(
            [PolygonFeature(9001, BridgeObstacleKind.Building, 0f, 100f, 90f, 110f)]);
        var a = Plan(MakeSpan(), obstacles: obstacles, crossings: [RoadCrossingAt(140.29f)]);
        var b = Plan(MakeSpan(), obstacles: obstacles, crossings: [RoadCrossingAt(140.29f)]);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Station, b[i].Station);
            Assert.Equal(a[i].Center, b[i].Center);
        }
    }
}
