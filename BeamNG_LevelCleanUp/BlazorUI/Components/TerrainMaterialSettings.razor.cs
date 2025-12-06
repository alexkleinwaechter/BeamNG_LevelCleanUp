using BeamNgTerrainPoc.Examples;
using BeamNgTerrainPoc.Terrain.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using DialogResult = System.Windows.Forms.DialogResult;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class TerrainMaterialSettings
{
    /// <summary>
    ///     Available road smoothing presets from RoadSmoothingPresets class
    /// </summary>
    public enum RoadPresetType
    {
        Custom,
        Highway,
        MountainRoad,
        RacingCircuit,
        DirtRoad,
        TerrainFollowingSmooth,
        MountainousUltraSmooth,
        HillyAggressive,
        FlatModerate,
        FastTesting,
        ExtremeNuclear
    }

    [Parameter] public TerrainMaterialItemExtended Material { get; set; } = null!;
    [Parameter] public EventCallback<TerrainMaterialItemExtended> OnMaterialChanged { get; set; }

    /// <summary>
    ///     Gets all current validation warnings based on parameter combinations
    /// </summary>
    private List<ValidationWarning> GetValidationWarnings()
    {
        var warnings = new List<ValidationWarning>();

        if (!Material.IsRoadMaterial)
            return warnings;

        // ========================================
        // CRITICAL: GlobalLevelingStrength + TerrainAffectedRangeMeters
        // ========================================
        if (Material.GlobalLevelingStrength > 0.5f && Material.TerrainAffectedRangeMeters < 15.0f)
            warnings.Add(new ValidationWarning(
                Severity.Error,
                "Disconnected Road Risk",
                $"GlobalLevelingStrength ({Material.GlobalLevelingStrength:F2}) > 0.5 with TerrainAffectedRangeMeters ({Material.TerrainAffectedRangeMeters:F1}m) < 15m will likely create disconnected 'dotted' road segments.",
                "Either reduce GlobalLevelingStrength to ≤ 0.5 OR increase TerrainAffectedRangeMeters to ≥ 15m (recommended: 20m+)",
                Icons.Material.Filled.LinkOff));
        else if (Material.GlobalLevelingStrength > 0.3f && Material.TerrainAffectedRangeMeters < 12.0f)
            warnings.Add(new ValidationWarning(
                Severity.Warning,
                "Blend Zone May Be Too Narrow",
                $"GlobalLevelingStrength ({Material.GlobalLevelingStrength:F2}) with TerrainAffectedRangeMeters ({Material.TerrainAffectedRangeMeters:F1}m) may cause visible transitions.",
                "Consider increasing TerrainAffectedRangeMeters to ≥ 12m for smoother blending"));

        // ========================================
        // CrossSectionIntervalMeters vs Road Impact Radius
        // ========================================
        var totalImpactRadius = Material.RoadWidthMeters / 2.0f + Material.TerrainAffectedRangeMeters;
        var recommendedMaxInterval = totalImpactRadius / 3.0f;

        if (Material.CrossSectionIntervalMeters > recommendedMaxInterval)
            warnings.Add(new ValidationWarning(
                Severity.Warning,
                "Cross-Section Spacing May Cause Gaps",
                $"CrossSectionIntervalMeters ({Material.CrossSectionIntervalMeters:F2}m) is large relative to the road impact radius ({totalImpactRadius:F1}m).",
                $"Reduce CrossSectionIntervalMeters to ≤ {recommendedMaxInterval:F2}m for continuous coverage",
                Icons.Material.Filled.SpaceBar));

        // ========================================
        // SmoothingWindowSize (odd number check for Spline)
        // ========================================
        if (Material.Approach == RoadSmoothingApproach.Spline && Material.SplineSmoothingWindowSize % 2 == 0)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "Window Size Should Be Odd",
                $"SmoothingWindowSize ({Material.SplineSmoothingWindowSize}) should be an odd number for symmetric smoothing.",
                $"Change to {Material.SplineSmoothingWindowSize + 1} for optimal results",
                Icons.Material.Filled.Info));

        // ========================================
        // SmoothingKernelSize (odd number check for Post-Processing)
        // ========================================
        if (Material.EnablePostProcessingSmoothing && Material.SmoothingKernelSize % 2 == 0)
            warnings.Add(new ValidationWarning(
                Severity.Warning,
                "Kernel Size Must Be Odd",
                $"SmoothingKernelSize ({Material.SmoothingKernelSize}) must be an odd number.",
                $"Change to {Material.SmoothingKernelSize + 1}",
                Icons.Material.Filled.GridOn));

        // ========================================
        // SmoothingMaskExtensionMeters vs CrossSectionIntervalMeters
        // ========================================
        if (Material.EnablePostProcessingSmoothing &&
            Material.SmoothingMaskExtensionMeters < Material.CrossSectionIntervalMeters * 2)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "Mask Extension May Be Insufficient",
                $"SmoothingMaskExtensionMeters ({Material.SmoothingMaskExtensionMeters:F1}m) should be ≥ 2× CrossSectionIntervalMeters ({Material.CrossSectionIntervalMeters:F2}m) to fully eliminate staircase artifacts.",
                $"Increase to ≥ {Material.CrossSectionIntervalMeters * 2:F1}m",
                Icons.Material.Filled.Expand));

        // ========================================
        // Butterworth not enabled for hilly terrain (high window size suggests hilly)
        // ========================================
        if (Material.Approach == RoadSmoothingApproach.Spline &&
            !Material.SplineUseButterworthFilter &&
            Material.SplineSmoothingWindowSize > 150)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "Consider Butterworth Filter",
                $"Large smoothing window ({Material.SplineSmoothingWindowSize}) suggests hilly terrain. Butterworth filter provides better results than box filter for elevation changes.",
                "Enable Butterworth Filter for 'maximally flat' passband without ringing artifacts",
                Icons.Material.Filled.FilterAlt));

        // ========================================
        // High Butterworth order warning
        // ========================================
        var butterworthOrder = Material.Approach == RoadSmoothingApproach.Spline
            ? Material.SplineButterworthFilterOrder
            : Material.DirectMaskButterworthFilterOrder;
        var butterworthEnabled = Material.Approach == RoadSmoothingApproach.Spline
            ? Material.SplineUseButterworthFilter
            : Material.DirectMaskUseButterworthFilter;

        if (butterworthEnabled && butterworthOrder > 6)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "High Butterworth Order",
                $"Filter order {butterworthOrder} is very aggressive. While it provides maximum flatness, it may introduce subtle ringing at sharp transitions.",
                "Order 3-4 is usually optimal for most terrain types",
                Icons.Material.Filled.TrendingFlat));

        // ========================================
        // Very small road width
        // ========================================
        if (Material.RoadWidthMeters < 3.0f)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "Narrow Road",
                $"Road width ({Material.RoadWidthMeters:F1}m) is very narrow. Standard single-lane roads are typically 3-4m, two-lane roads 6-8m.",
                "Ensure this is intentional (e.g., footpath, trail)",
                Icons.Material.Filled.Straighten));

        // ========================================
        // Steep road slope
        // ========================================
        if (Material.RoadMaxSlopeDegrees > 12.0f)
            warnings.Add(new ValidationWarning(
                Severity.Info,
                "Steep Road Grade",
                $"Max road slope ({Material.RoadMaxSlopeDegrees:F1}°) exceeds typical limits. Standard highways: 4-6°, mountain roads: 8-10°.",
                "Very steep grades (>12°) may feel unrealistic for paved roads",
                Icons.Material.Filled.Landscape));

        return warnings;
    }

    /// <summary>
    ///     Gets count of validation errors for title badge
    /// </summary>
    private int GetValidationErrorCount()
    {
        return GetValidationWarnings().Count(w => w.Severity == Severity.Error);
    }

    /// <summary>
    ///     Gets count of validation warnings for title badge
    /// </summary>
    private int GetValidationWarningCount()
    {
        return GetValidationWarnings().Count(w => w.Severity == Severity.Warning);
    }

    private string GetPanelTitle()
    {
        return $"[{Material.Order}] {Material.InternalName}";
    }

    private async Task SelectLayerMap()
    {
        string? selectedPath = null;
        var staThread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog();
            dialog.Filter = "PNG Images (*.png)|*.png|All Files (*.*)|*.*";
            dialog.Title = $"Select Layer Map for {Material.InternalName}";
            if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.FileName;
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (!string.IsNullOrEmpty(selectedPath))
        {
            Material.LayerMapPath = selectedPath;
            await OnMaterialChanged.InvokeAsync(Material);
        }
    }

    private async Task ClearLayerMap()
    {
        Material.LayerMapPath = null;
        await OnMaterialChanged.InvokeAsync(Material);
    }

    private async Task OnPresetChanged(RoadPresetType preset)
    {
        Material.SelectedPreset = preset;

        // Apply ALL preset values
        var presetParams = GetPresetParameters(preset);
        if (presetParams != null) Material.ApplyPreset(presetParams);

        await OnMaterialChanged.InvokeAsync(Material);
    }

    /// <summary>
    ///     Gets the RoadSmoothingParameters for a given preset type.
    ///     Returns null for Custom preset.
    /// </summary>
    public static RoadSmoothingParameters? GetPresetParameters(RoadPresetType preset)
    {
        return preset switch
        {
            RoadPresetType.Highway => RoadSmoothingPresets.Highway,
            RoadPresetType.MountainRoad => RoadSmoothingPresets.MountainRoad,
            RoadPresetType.RacingCircuit => RoadSmoothingPresets.RacingCircuit,
            RoadPresetType.DirtRoad => RoadSmoothingPresets.DirtRoad,
            RoadPresetType.TerrainFollowingSmooth => RoadSmoothingPresets.TerrainFollowingSmooth,
            RoadPresetType.MountainousUltraSmooth => RoadSmoothingPresets.MountainousUltraSmooth,
            RoadPresetType.HillyAggressive => RoadSmoothingPresets.HillyAggressive,
            RoadPresetType.FlatModerate => RoadSmoothingPresets.FlatModerate,
            RoadPresetType.FastTesting => RoadSmoothingPresets.FastTesting,
            RoadPresetType.ExtremeNuclear => RoadSmoothingPresets.ExtremeNuclear,
            _ => null
        };
    }

    /// <summary>
    ///     Represents a validation warning with severity and recommendation
    /// </summary>
    private record ValidationWarning(
        Severity Severity,
        string Title,
        string Message,
        string? Recommendation = null,
        string Icon = Icons.Material.Filled.Warning);

    /// <summary>
    ///     Extended terrain material item with ALL road smoothing properties for terrain generation
    /// </summary>
    public class TerrainMaterialItemExtended
    {
        public int Order { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
        public string JsonKey { get; set; } = string.Empty;
        public string Selector { get; set; } = "materials";

        // Layer map
        public string? LayerMapPath { get; set; }
        public bool HasLayerMap => !string.IsNullOrEmpty(LayerMapPath);

        // Road smoothing enabled
        public bool IsRoadMaterial { get; set; }
        public RoadPresetType SelectedPreset { get; set; } = RoadPresetType.Highway;

        // ========================================
        // PRIMARY PARAMETERS (always visible)
        // ========================================
        public float RoadWidthMeters { get; set; } = 8.0f;
        public float TerrainAffectedRangeMeters { get; set; } = 6.0f;
        public float RoadMaxSlopeDegrees { get; set; } = 6.0f;
        public float SideMaxSlopeDegrees { get; set; } = 45.0f;

        // ========================================
        // ALGORITHM SETTINGS
        // ========================================
        public RoadSmoothingApproach Approach { get; set; } = RoadSmoothingApproach.Spline;
        public BlendFunctionType BlendFunctionType { get; set; } = BlendFunctionType.Cosine;
        public float CrossSectionIntervalMeters { get; set; } = 0.5f;
        public bool EnableTerrainBlending { get; set; } = true;

        // ========================================
        // SPLINE PARAMETERS
        // ========================================
        // Curve fitting
        public float SplineTension { get; set; } = 0.2f;
        public float SplineContinuity { get; set; } = 0.7f;
        public float SplineBias { get; set; }

        // Path extraction
        public bool UseGraphOrdering { get; set; } = true;
        public bool PreferStraightThroughJunctions { get; set; }
        public float DensifyMaxSpacingPixels { get; set; } = 1.5f;
        public float SimplifyTolerancePixels { get; set; } = 0.5f;
        public float BridgeEndpointMaxDistancePixels { get; set; } = 40.0f;
        public float MinPathLengthPixels { get; set; } = 100.0f;
        public float JunctionAngleThreshold { get; set; } = 90.0f;
        public float OrderingNeighborRadiusPixels { get; set; } = 2.5f;
        public int SkeletonDilationRadius { get; set; } = 0;

        // Elevation smoothing (Spline)
        public int SplineSmoothingWindowSize { get; set; } = 301;
        public bool SplineUseButterworthFilter { get; set; } = true;
        public int SplineButterworthFilterOrder { get; set; } = 4;
        public float GlobalLevelingStrength { get; set; }

        // ========================================
        // DIRECTMASK PARAMETERS
        // ========================================
        public int DirectMaskSmoothingWindowSize { get; set; } = 10;
        public int RoadPixelSearchRadius { get; set; } = 3;
        public bool DirectMaskUseButterworthFilter { get; set; } = true;
        public int DirectMaskButterworthFilterOrder { get; set; } = 3;

        // ========================================
        // POST-PROCESSING SMOOTHING
        // ========================================
        public bool EnablePostProcessingSmoothing { get; set; } = true;
        public PostProcessingSmoothingType SmoothingType { get; set; } = PostProcessingSmoothingType.Gaussian;
        public int SmoothingKernelSize { get; set; } = 7;
        public float SmoothingSigma { get; set; } = 1.5f;
        public int SmoothingIterations { get; set; } = 1;
        public float SmoothingMaskExtensionMeters { get; set; } = 6.0f;

        // ========================================
        // DEBUG OUTPUT
        // ========================================
        public bool ExportSmoothedHeightmapWithOutlines { get; set; }
        public bool ExportSplineDebugImage { get; set; }
        public bool ExportSkeletonDebugImage { get; set; }
        public bool ExportSmoothedElevationDebugImage { get; set; }

        /// <summary>
        ///     Applies all values from a preset to this material's settings.
        /// </summary>
        public void ApplyPreset(RoadSmoothingParameters preset)
        {
            // Primary parameters
            RoadWidthMeters = preset.RoadWidthMeters;
            TerrainAffectedRangeMeters = preset.TerrainAffectedRangeMeters;
            RoadMaxSlopeDegrees = preset.RoadMaxSlopeDegrees;
            SideMaxSlopeDegrees = preset.SideMaxSlopeDegrees;

            // Algorithm settings
            Approach = preset.Approach;
            BlendFunctionType = preset.BlendFunctionType;
            CrossSectionIntervalMeters = preset.CrossSectionIntervalMeters;
            EnableTerrainBlending = preset.EnableTerrainBlending;

            // Post-processing
            EnablePostProcessingSmoothing = preset.EnablePostProcessingSmoothing;
            SmoothingType = preset.SmoothingType;
            SmoothingKernelSize = preset.SmoothingKernelSize;
            SmoothingSigma = preset.SmoothingSigma;
            SmoothingIterations = preset.SmoothingIterations;
            SmoothingMaskExtensionMeters = preset.SmoothingMaskExtensionMeters;

            // Debug
            ExportSmoothedHeightmapWithOutlines = preset.ExportSmoothedHeightmapWithOutlines;

            // Spline parameters
            if (preset.SplineParameters != null)
            {
                SplineTension = preset.SplineParameters.SplineTension;
                SplineContinuity = preset.SplineParameters.SplineContinuity;
                SplineBias = preset.SplineParameters.SplineBias;
                UseGraphOrdering = preset.SplineParameters.UseGraphOrdering;
                PreferStraightThroughJunctions = preset.SplineParameters.PreferStraightThroughJunctions;
                DensifyMaxSpacingPixels = preset.SplineParameters.DensifyMaxSpacingPixels;
                SimplifyTolerancePixels = preset.SplineParameters.SimplifyTolerancePixels;
                BridgeEndpointMaxDistancePixels = preset.SplineParameters.BridgeEndpointMaxDistancePixels;
                MinPathLengthPixels = preset.SplineParameters.MinPathLengthPixels;
                JunctionAngleThreshold = preset.SplineParameters.JunctionAngleThreshold;
                OrderingNeighborRadiusPixels = preset.SplineParameters.OrderingNeighborRadiusPixels;
                SkeletonDilationRadius = preset.SplineParameters.SkeletonDilationRadius;
                SplineSmoothingWindowSize = preset.SplineParameters.SmoothingWindowSize;
                SplineUseButterworthFilter = preset.SplineParameters.UseButterworthFilter;
                SplineButterworthFilterOrder = preset.SplineParameters.ButterworthFilterOrder;
                GlobalLevelingStrength = preset.SplineParameters.GlobalLevelingStrength;
                ExportSplineDebugImage = preset.SplineParameters.ExportSplineDebugImage;
                ExportSkeletonDebugImage = preset.SplineParameters.ExportSkeletonDebugImage;
                ExportSmoothedElevationDebugImage = preset.SplineParameters.ExportSmoothedElevationDebugImage;
            }

            // DirectMask parameters
            if (preset.DirectMaskParameters != null)
            {
                DirectMaskSmoothingWindowSize = preset.DirectMaskParameters.SmoothingWindowSize;
                RoadPixelSearchRadius = preset.DirectMaskParameters.RoadPixelSearchRadius;
                DirectMaskUseButterworthFilter = preset.DirectMaskParameters.UseButterworthFilter;
                DirectMaskButterworthFilterOrder = preset.DirectMaskParameters.ButterworthFilterOrder;
            }
        }

        /// <summary>
        ///     Builds the full RoadSmoothingParameters from all stored values.
        /// </summary>
        public RoadSmoothingParameters BuildRoadSmoothingParameters(string? debugOutputDirectory = null)
        {
            var result = new RoadSmoothingParameters
            {
                // Primary parameters
                RoadWidthMeters = RoadWidthMeters,
                TerrainAffectedRangeMeters = TerrainAffectedRangeMeters,
                RoadMaxSlopeDegrees = RoadMaxSlopeDegrees,
                SideMaxSlopeDegrees = SideMaxSlopeDegrees,

                // Algorithm settings
                Approach = Approach,
                BlendFunctionType = BlendFunctionType,
                CrossSectionIntervalMeters = CrossSectionIntervalMeters,
                EnableTerrainBlending = EnableTerrainBlending,

                // Post-processing
                EnablePostProcessingSmoothing = EnablePostProcessingSmoothing,
                SmoothingType = SmoothingType,
                SmoothingKernelSize = SmoothingKernelSize,
                SmoothingSigma = SmoothingSigma,
                SmoothingIterations = SmoothingIterations,
                SmoothingMaskExtensionMeters = SmoothingMaskExtensionMeters,

                // Debug
                DebugOutputDirectory = debugOutputDirectory,
                ExportSmoothedHeightmapWithOutlines = ExportSmoothedHeightmapWithOutlines
            };

            // Spline parameters
            result.SplineParameters = new SplineRoadParameters
            {
                SplineTension = SplineTension,
                SplineContinuity = SplineContinuity,
                SplineBias = SplineBias,
                UseGraphOrdering = UseGraphOrdering,
                PreferStraightThroughJunctions = PreferStraightThroughJunctions,
                DensifyMaxSpacingPixels = DensifyMaxSpacingPixels,
                SimplifyTolerancePixels = SimplifyTolerancePixels,
                BridgeEndpointMaxDistancePixels = BridgeEndpointMaxDistancePixels,
                MinPathLengthPixels = MinPathLengthPixels,
                JunctionAngleThreshold = JunctionAngleThreshold,
                OrderingNeighborRadiusPixels = OrderingNeighborRadiusPixels,
                SkeletonDilationRadius = SkeletonDilationRadius,
                SmoothingWindowSize = SplineSmoothingWindowSize,
                UseButterworthFilter = SplineUseButterworthFilter,
                ButterworthFilterOrder = SplineButterworthFilterOrder,
                GlobalLevelingStrength = GlobalLevelingStrength,
                ExportSplineDebugImage = ExportSplineDebugImage,
                ExportSkeletonDebugImage = ExportSkeletonDebugImage,
                ExportSmoothedElevationDebugImage = ExportSmoothedElevationDebugImage
            };

            // DirectMask parameters
            result.DirectMaskParameters = new DirectMaskRoadParameters
            {
                SmoothingWindowSize = DirectMaskSmoothingWindowSize,
                RoadPixelSearchRadius = RoadPixelSearchRadius,
                UseButterworthFilter = DirectMaskUseButterworthFilter,
                ButterworthFilterOrder = DirectMaskButterworthFilterOrder
            };

            return result;
        }
    }
}