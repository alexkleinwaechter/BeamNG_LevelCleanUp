using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;
using BeamNgTerrainPoc.Tests.Elevation;
using Xunit;

namespace BeamNgTerrainPoc.Tests.RoadGeometry;

/// <summary>
/// Tests for UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry — the network-level
/// repair pass that fixes non-finite cross-section geometry from degenerate source splines
/// instead of letting it crash the DecalRoad JSON scene writer (alexanderplatz, 2026-06-12).
/// </summary>
public class CrossSectionGeometryRepairTests
{
    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> sections) BuildStraightNetwork()
    {
        var network = new UnifiedRoadNetwork();
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            0, new Vector2(0, 0), new Vector2(100, 0));
        var sections = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        return (network, sections);
    }

    [Fact]
    public void NanCenters_AreReinterpolatedBetweenFiniteNeighbours()
    {
        var (network, sections) = BuildStraightNetwork();
        Assert.True(sections.Count > 20);

        // Corrupt an interior run of sections (mimics a degenerate sub-segment)
        for (var i = 10; i <= 14; i++)
            sections[i].CenterPoint = new Vector2(float.NaN, float.NaN);

        UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry(network);

        for (var i = 10; i <= 14; i++)
        {
            Assert.True(float.IsFinite(sections[i].CenterPoint.X));
            Assert.True(float.IsFinite(sections[i].CenterPoint.Y));
            // Straight road along +X at y=0: repaired centers must lie between the finite anchors
            Assert.True(sections[i].CenterPoint.X > sections[9].CenterPoint.X);
            Assert.True(sections[i].CenterPoint.X < sections[15].CenterPoint.X);
            Assert.Equal(0f, sections[i].CenterPoint.Y, 3);
        }

        // Monotonic along the run (linear interpolation between anchors)
        for (var i = 10; i < 14; i++)
            Assert.True(sections[i + 1].CenterPoint.X > sections[i].CenterPoint.X);
    }

    [Fact]
    public void NanCentersAtSplineEnd_AreCopiedFromNearestFinite()
    {
        var (network, sections) = BuildStraightNetwork();
        var lastIdx = sections.Count - 1;
        sections[lastIdx].CenterPoint = new Vector2(float.NaN, float.NaN);
        sections[lastIdx - 1].CenterPoint = new Vector2(float.PositiveInfinity, 0);

        UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry(network);

        Assert.True(float.IsFinite(sections[lastIdx].CenterPoint.X));
        Assert.True(float.IsFinite(sections[lastIdx - 1].CenterPoint.X));
        Assert.Equal(sections[lastIdx - 2].CenterPoint, sections[lastIdx].CenterPoint);
    }

    [Fact]
    public void NanTangents_AreRebuiltFromCenterline()
    {
        var (network, sections) = BuildStraightNetwork();
        sections[5].TangentDirection = new Vector2(float.NaN, float.NaN);
        sections[5].NormalDirection = new Vector2(float.NaN, float.NaN);

        UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry(network);

        // Straight +X road: tangent ≈ (1, 0), normal ≈ (0, -1)
        Assert.Equal(1f, sections[5].TangentDirection.X, 3);
        Assert.Equal(0f, sections[5].TangentDirection.Y, 3);
        Assert.Equal(0f, sections[5].NormalDirection.X, 3);
        Assert.Equal(-1f, sections[5].NormalDirection.Y, 3);
    }

    [Fact]
    public void SplineWithFewerThanTwoFiniteSections_IsExcluded()
    {
        var (network, sections) = BuildStraightNetwork();
        foreach (var cs in sections)
            cs.CenterPoint = new Vector2(float.NaN, float.NaN);

        UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry(network);

        Assert.All(sections, cs => Assert.True(cs.IsExcluded));
    }

    [Fact]
    public void AllFiniteNetwork_IsUntouched()
    {
        var (network, sections) = BuildStraightNetwork();
        var centersBefore = sections.Select(cs => cs.CenterPoint).ToList();
        var excludedBefore = sections.Select(cs => cs.IsExcluded).ToList();

        UnifiedRoadNetworkBuilder.RepairNonFiniteCrossSectionGeometry(network);

        for (var i = 0; i < sections.Count; i++)
        {
            Assert.Equal(centersBefore[i], sections[i].CenterPoint);
            Assert.Equal(excludedBefore[i], sections[i].IsExcluded);
        }
    }
}
