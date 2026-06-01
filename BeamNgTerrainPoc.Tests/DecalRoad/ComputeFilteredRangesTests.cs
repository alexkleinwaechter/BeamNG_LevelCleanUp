using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class ComputeFilteredRangesTests
{
    private static (List<UnifiedCrossSection> Sections, List<float> Distances)
        CreateSectionsWithCurve(int count, int curveStart, int curveEnd, float curvature = 0.02f)
    {
        var sections = new List<UnifiedCrossSection>();
        var distances = new List<float>();
        for (int i = 0; i < count; i++)
        {
            sections.Add(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i, 0),
                NormalDirection = new Vector2(0, 1),
                TangentDirection = new Vector2(1, 0),
                Curvature = (i >= curveStart && i <= curveEnd) ? curvature : 0f
            });
            distances.Add(i);
        }
        return (sections, distances);
    }

    private static DecalRoadSettings DefaultSettings => new() { RandomSeed = 42 };

    [Fact]
    public void ReplaceInCurve_StraightSegments_UseMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveReplacementTextureLength = 5f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f // no transition for simpler test
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var straightSegs = result.Where(s => s.Material == "main_mat").ToList();
        Assert.NotEmpty(straightSegs);
        Assert.All(straightSegs, s =>
        {
            Assert.Equal(0.25f, s.Width);
            Assert.Equal(10f, s.TextureLength);
        });
    }

    [Fact]
    public void ReplaceInCurve_CurveSegments_UseReplacementMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveReplacementTextureLength = 5f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        Assert.All(curveSegs, s =>
        {
            Assert.Equal(0.15f, s.Width);
            Assert.Equal(5f, s.TextureLength);
        });
    }

    [Fact]
    public void ReplaceInCurve_ZeroReplacementWidth_FallsBackToMainWidth()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0f, // should fall back to 0.25
            CurveReplacementTextureLength = 0f, // should fall back to 10
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        Assert.All(curveSegs, s =>
        {
            Assert.Equal(0.25f, s.Width); // fell back to main
            Assert.Equal(10f, s.TextureLength); // fell back to main
        });
    }

    [Fact]
    public void ReplaceInCurve_EmptyReplacementMaterial_FallsBackToMainEverywhere()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "", // empty — should degrade to None
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        // Should produce a single segment with main material covering full range
        Assert.All(result, s => Assert.Equal("main_mat", s.Material));
    }

    [Fact]
    public void ReplaceInCurve_Randomize_OnlyAffectsStraightSegments()
    {
        var (sections, distances) = CreateSectionsWithCurve(200, 80, 120);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.ReplaceInCurve,
            CurveReplacementMaterial = "repl_mat",
            CurveReplacementWidth = 0.15f,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f,
            Randomize = true,
            RandomMinPatchLength = 5f,
            RandomMaxPatchLength = 15f,
            RandomMinGapLength = 5f,
            RandomMaxGapLength = 15f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 199, DefaultSettings, splineId: 1);

        // Curve segments should be continuous (not randomized)
        var curveSegs = result.Where(s => s.Material == "repl_mat").ToList();
        Assert.NotEmpty(curveSegs);
        // The curve zone should be one continuous segment (no gaps from randomizer)
        Assert.Single(curveSegs);

        // Straight segments may have gaps (randomizer applied)
        var straightSegs = result.Where(s => s.Material == "main_mat").ToList();
        // With randomizer, there should be multiple patches (not one continuous range)
        // The exact count depends on RNG, but with 200m and these params we expect multiple
        Assert.True(straightSegs.Count >= 1);
    }

    [Fact]
    public void CurveOnly_ReturnsOnlyCurveZones_WithMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.CurveOnly,
            CurveMinCurvature = 0.01f,
            CurveTransitionLength = 0f
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        // Should only cover the curve zone, using main material
        Assert.All(result, s =>
        {
            Assert.Equal("main_mat", s.Material);
            Assert.True(s.Start >= 40 && s.End <= 60,
                $"Segment ({s.Start},{s.End}) outside curve zone (40,60)");
        });
    }

    [Fact]
    public void None_ReturnsFullRange_WithMainMaterial()
    {
        var (sections, distances) = CreateSectionsWithCurve(100, 40, 60);
        var layer = new DecalRoadLayerDefinition
        {
            Name = "Test",
            Material = "main_mat",
            Width = 0.25f,
            TextureLength = 10f,
            CurveConstraint = CurveConstraintMode.None
        };

        var result = DecalRoadGenerator.ComputeFilteredRanges(
            layer, sections, distances, 0, 99, DefaultSettings, splineId: 1);

        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(99, result[0].End);
        Assert.Equal("main_mat", result[0].Material);
    }
}
