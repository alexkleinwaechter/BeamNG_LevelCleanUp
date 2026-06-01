using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Junction;

public class PropagationOverlapTaperTests
{
    /// <summary>
    ///     Mimics franco_same_prio spline 64 / junction 126 topology:
    ///       - Spline length 60 m (test compresses from real 311 m for speed)
    ///       - Descending natural profile: 159 m at CS 0 → 156 m at CS 60 (slope ≈ -0.05)
    ///       - Direct end constraint at j126 (CS 60): elevation 158.95, blend distance 30 m
    ///       - Propagated mid-spline influence from j102: elevation 166.54, attached at
    ///         CS 60 with blend distance 25 m (so the influence reaches back to CS 35).
    ///       - CS-by-CS influence weights follow the existing quintic smoothstep falloff,
    ///         but for test simplicity we set a single CS's influence and inspect it.
    /// </summary>
    private static (List<UnifiedCrossSection> sections,
                    Dictionary<int, List<(float elevation, float weight, int junctionId)>> influences,
                    Dictionary<int, SplineClaimedZone> claimedZones)
        BuildJunction126Scenario(bool taperEnabled)
    {
        var sections = new List<UnifiedCrossSection>();
        for (var i = 0; i <= 60; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                Index = 64_000 + i,
                LocalIndex = i,
                OwnerSplineId = 64,
                CenterPoint = new Vector2(i, 0f),
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, 1f),
                TargetElevation = i >= 55 ? 158.95f : (159f - 0.05f * (60 - i)),
                BankAngleRadians = 0f,
                EffectiveRoadWidth = 6f
            });
        }

        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 58, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };

        Dictionary<int, SplineClaimedZone> claimedZones = new();
        if (taperEnabled)
        {
            var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
            {
                {
                    (64, false), new JunctionEndpointConstraint
                    {
                        Elevation = 158.95f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = false,
                        BlendDistanceMeters = 30f,
                        Junction = new NetworkJunction { JunctionId = 126 },
                        PrimaryTangentDirection = new Vector2(1f, 0f)
                    }
                }
            };
            var crossSectionsBySpline = new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } };
            claimedZones = SplineClaimedZones.Build(constraints, crossSectionsBySpline);
        }

        return (sections, influences, claimedZones);
    }

    [Fact]
    public void TaperOff_LegacyBehaviour_PropagatedInfluenceDragsCsToward166()
    {
        var (sections, influences, _) = BuildJunction126Scenario(taperEnabled: false);
        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, splineClaimedZones: null);

        var after = sections[58].TargetElevation;

        Assert.True(after - beforeElev > 5f,
            $"Without taper, CS 58 should jump upward toward 166m; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_CsInsideContestedEndZone_InfluenceAttenuatedToNearZero()
    {
        var (sections, influences, claimedZones) = BuildJunction126Scenario(taperEnabled: true);
        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[58].TargetElevation;

        Assert.True(after - beforeElev < 0.2f,
            $"With taper, CS 58 (2m from j126 anchor) should barely move; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_CsAtFarEndOfBlendZone_InfluenceMostlyPreserved()
    {
        var (sections, _, claimedZones) = BuildJunction126Scenario(taperEnabled: true);
        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 30, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };
        var beforeElev = sections[30].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[30].TargetElevation;

        var (sectionsBaseline, _, _) = BuildJunction126Scenario(taperEnabled: false);
        var baselineInfluences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 30, new List<(float, float, int)> { (166.54f, 0.85f, 102) } }
        };
        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sectionsBaseline, baselineInfluences, splineClaimedZones: null);
        var baselineAfter = sectionsBaseline[30].TargetElevation;

        Assert.Equal(baselineAfter, after, 2);
        Assert.True(after - beforeElev > 5f,
            $"At blend boundary, taper=1 → full influence; before={beforeElev}, after={after}");
    }

    [Fact]
    public void TaperOn_SameJunctionAsInfluenceSource_NoTaper()
    {
        var (sections, _, _) = BuildJunction126Scenario(taperEnabled: true);
        var influences = new Dictionary<int, List<(float elevation, float weight, int junctionId)>>
        {
            { 64_000 + 58, new List<(float, float, int)> { (166.54f, 0.85f, 126) } }
        };
        var constraints = new Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint>
        {
            {
                (64, false), new JunctionEndpointConstraint
                {
                    Elevation = 158.95f, Slope = 0f, BankAngleRadians = 0f, IsSplineStart = false,
                    BlendDistanceMeters = 30f,
                    Junction = new NetworkJunction { JunctionId = 126 },
                    PrimaryTangentDirection = new Vector2(1f, 0f)
                }
            }
        };
        var claimedZones = SplineClaimedZones.Build(
            constraints, new Dictionary<int, List<UnifiedCrossSection>> { { 64, sections } });

        var beforeElev = sections[58].TargetElevation;

        UnifiedJunctionProfileBlender.ApplyPropagatedMidSplineInfluences(
            sections, influences, claimedZones);

        var after = sections[58].TargetElevation;

        Assert.True(after - beforeElev > 5f,
            $"Same-junction case: taper should be 1, full influence applies; before={beforeElev}, after={after}");
    }
}
