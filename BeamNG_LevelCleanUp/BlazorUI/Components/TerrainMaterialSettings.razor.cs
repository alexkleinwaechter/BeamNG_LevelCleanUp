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

    private async Task ImportRoadSettings()
    {
        string? selectedPath = null;
        var staThread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog();
            dialog.Filter = "Road Smoothing JSON (*_roadSmoothing*.json)|*_roadSmoothing*.json|All JSON Files (*.json)|*.json";
            dialog.Title = $"Import Road Smoothing Settings for {Material.InternalName}";
            if (dialog.ShowDialog() == DialogResult.OK)
                selectedPath = dialog.FileName;
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (!string.IsNullOrEmpty(selectedPath))
        {
            await ImportRoadSettingsFromFile(selectedPath);
        }
    }

    private async Task ImportRoadSettingsFromFile(string filePath)
    {
        try
        {
            var jsonContent = await File.ReadAllTextAsync(filePath);
            var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(jsonContent);
            
            if (jsonNode == null)
            {
                return;
            }

            // Import primary parameters
            if (jsonNode["roadWidthMeters"] != null)
                Material.RoadWidthMeters = jsonNode["roadWidthMeters"]!.GetValue<float>();
            if (jsonNode["terrainAffectedRangeMeters"] != null)
                Material.TerrainAffectedRangeMeters = jsonNode["terrainAffectedRangeMeters"]!.GetValue<float>();
            if (jsonNode["enableMaxSlopeConstraint"] != null)
                Material.EnableMaxSlopeConstraint = jsonNode["enableMaxSlopeConstraint"]!.GetValue<bool>();
            if (jsonNode["roadMaxSlopeDegrees"] != null)
                Material.RoadMaxSlopeDegrees = jsonNode["roadMaxSlopeDegrees"]!.GetValue<float>();
            if (jsonNode["sideMaxSlopeDegrees"] != null)
                Material.SideMaxSlopeDegrees = jsonNode["sideMaxSlopeDegrees"]!.GetValue<float>();

            // Import algorithm settings
            if (jsonNode["approach"] != null && Enum.TryParse<RoadSmoothingApproach>(jsonNode["approach"]!.GetValue<string>(), out var approach))
                Material.Approach = approach;
            if (jsonNode["blendFunctionType"] != null && Enum.TryParse<BlendFunctionType>(jsonNode["blendFunctionType"]!.GetValue<string>(), out var blendType))
                Material.BlendFunctionType = blendType;
            if (jsonNode["crossSectionIntervalMeters"] != null)
                Material.CrossSectionIntervalMeters = jsonNode["crossSectionIntervalMeters"]!.GetValue<float>();
            if (jsonNode["enableTerrainBlending"] != null)
                Material.EnableTerrainBlending = jsonNode["enableTerrainBlending"]!.GetValue<bool>();

            // Import spline parameters
            var splineParams = jsonNode["splineParameters"];
            if (splineParams != null)
            {
                if (splineParams["tension"] != null)
                    Material.SplineTension = splineParams["tension"]!.GetValue<float>();
                if (splineParams["continuity"] != null)
                    Material.SplineContinuity = splineParams["continuity"]!.GetValue<float>();
                if (splineParams["bias"] != null)
                    Material.SplineBias = splineParams["bias"]!.GetValue<float>();
                if (splineParams["useGraphOrdering"] != null)
                    Material.UseGraphOrdering = splineParams["useGraphOrdering"]!.GetValue<bool>();
                if (splineParams["preferStraightThroughJunctions"] != null)
                    Material.PreferStraightThroughJunctions = splineParams["preferStraightThroughJunctions"]!.GetValue<bool>();
                if (splineParams["densifyMaxSpacingPixels"] != null)
                    Material.DensifyMaxSpacingPixels = splineParams["densifyMaxSpacingPixels"]!.GetValue<float>();
                if (splineParams["simplifyTolerancePixels"] != null)
                    Material.SimplifyTolerancePixels = splineParams["simplifyTolerancePixels"]!.GetValue<float>();
                if (splineParams["bridgeEndpointMaxDistancePixels"] != null)
                    Material.BridgeEndpointMaxDistancePixels = splineParams["bridgeEndpointMaxDistancePixels"]!.GetValue<float>();
                if (splineParams["minPathLengthPixels"] != null)
                    Material.MinPathLengthPixels = splineParams["minPathLengthPixels"]!.GetValue<float>();
                if (splineParams["junctionAngleThreshold"] != null)
                    Material.JunctionAngleThreshold = splineParams["junctionAngleThreshold"]!.GetValue<float>();
                if (splineParams["orderingNeighborRadiusPixels"] != null)
                    Material.OrderingNeighborRadiusPixels = splineParams["orderingNeighborRadiusPixels"]!.GetValue<float>();
                if (splineParams["skeletonDilationRadius"] != null)
                    Material.SkeletonDilationRadius = splineParams["skeletonDilationRadius"]!.GetValue<int>();
                if (splineParams["smoothingWindowSize"] != null)
                    Material.SplineSmoothingWindowSize = splineParams["smoothingWindowSize"]!.GetValue<int>();
                if (splineParams["useButterworthFilter"] != null)
                    Material.SplineUseButterworthFilter = splineParams["useButterworthFilter"]!.GetValue<bool>();
                if (splineParams["butterworthFilterOrder"] != null)
                    Material.SplineButterworthFilterOrder = splineParams["butterworthFilterOrder"]!.GetValue<int>();
                if (splineParams["globalLevelingStrength"] != null)
                    Material.GlobalLevelingStrength = splineParams["globalLevelingStrength"]!.GetValue<float>();
            }

            // Import DirectMask parameters
            var directMaskParams = jsonNode["directMaskParameters"];
            if (directMaskParams != null)
            {
                if (directMaskParams["smoothingWindowSize"] != null)
                    Material.DirectMaskSmoothingWindowSize = directMaskParams["smoothingWindowSize"]!.GetValue<int>();
                if (directMaskParams["roadPixelSearchRadius"] != null)
                    Material.RoadPixelSearchRadius = directMaskParams["roadPixelSearchRadius"]!.GetValue<int>();
                if (directMaskParams["useButterworthFilter"] != null)
                    Material.DirectMaskUseButterworthFilter = directMaskParams["useButterworthFilter"]!.GetValue<bool>();
                if (directMaskParams["butterworthFilterOrder"] != null)
                    Material.DirectMaskButterworthFilterOrder = directMaskParams["butterworthFilterOrder"]!.GetValue<int>();
            }

            // Import post-processing settings
            var postProcessing = jsonNode["postProcessing"];
            if (postProcessing != null)
            {
                if (postProcessing["enabled"] != null)
                    Material.EnablePostProcessingSmoothing = postProcessing["enabled"]!.GetValue<bool>();
                if (postProcessing["smoothingType"] != null && Enum.TryParse<PostProcessingSmoothingType>(postProcessing["smoothingType"]!.GetValue<string>(), out var smoothingType))
                    Material.SmoothingType = smoothingType;
                if (postProcessing["kernelSize"] != null)
                    Material.SmoothingKernelSize = postProcessing["kernelSize"]!.GetValue<int>();
                if (postProcessing["sigma"] != null)
                    Material.SmoothingSigma = postProcessing["sigma"]!.GetValue<float>();
                if (postProcessing["iterations"] != null)
                    Material.SmoothingIterations = postProcessing["iterations"]!.GetValue<int>();
                if (postProcessing["maskExtensionMeters"] != null)
                    Material.SmoothingMaskExtensionMeters = postProcessing["maskExtensionMeters"]!.GetValue<float>();
            }

            // Import debug settings
            var debug = jsonNode["debug"];
            if (debug != null)
            {
                if (debug["exportSmoothedHeightmapWithOutlines"] != null)
                    Material.ExportSmoothedHeightmapWithOutlines = debug["exportSmoothedHeightmapWithOutlines"]!.GetValue<bool>();
                if (debug["exportSplineDebugImage"] != null)
                    Material.ExportSplineDebugImage = debug["exportSplineDebugImage"]!.GetValue<bool>();
                if (debug["exportSkeletonDebugImage"] != null)
                    Material.ExportSkeletonDebugImage = debug["exportSkeletonDebugImage"]!.GetValue<bool>();
                if (debug["exportSmoothedElevationDebugImage"] != null)
                    Material.ExportSmoothedElevationDebugImage = debug["exportSmoothedElevationDebugImage"]!.GetValue<bool>();
            }

            // Import junction harmonization settings
            var junctionParams = jsonNode["junctionHarmonization"];
            if (junctionParams != null)
            {
                if (junctionParams["enableJunctionHarmonization"] != null)
                    Material.EnableJunctionHarmonization = junctionParams["enableJunctionHarmonization"]!.GetValue<bool>();
                if (junctionParams["junctionDetectionRadiusMeters"] != null)
                    Material.JunctionDetectionRadiusMeters = junctionParams["junctionDetectionRadiusMeters"]!.GetValue<float>();
                if (junctionParams["junctionBlendDistanceMeters"] != null)
                    Material.JunctionBlendDistanceMeters = junctionParams["junctionBlendDistanceMeters"]!.GetValue<float>();
                if (junctionParams["blendFunctionType"] != null && Enum.TryParse<JunctionBlendFunctionType>(junctionParams["blendFunctionType"]!.GetValue<string>(), out var junctionBlendType))
                    Material.JunctionBlendFunction = junctionBlendType;
                if (junctionParams["enableEndpointTaper"] != null)
                    Material.EnableEndpointTaper = junctionParams["enableEndpointTaper"]!.GetValue<bool>();
                if (junctionParams["endpointTaperDistanceMeters"] != null)
                    Material.EndpointTaperDistanceMeters = junctionParams["endpointTaperDistanceMeters"]!.GetValue<float>();
                if (junctionParams["endpointTerrainBlendStrength"] != null)
                    Material.EndpointTerrainBlendStrength = junctionParams["endpointTerrainBlendStrength"]!.GetValue<float>();
            }

            // Set preset to Custom since we imported custom values
            Material.SelectedPreset = RoadPresetType.Custom;

            await OnMaterialChanged.InvokeAsync(Material);
        }
        catch (Exception)
        {
            // Silently fail - the file might be malformed
        }
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
        public bool EnableMaxSlopeConstraint { get; set; } = false;
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

        // ========================================
        // JUNCTION HARMONIZATION
        // ========================================
        public bool EnableJunctionHarmonization { get; set; } = true;
        public float JunctionDetectionRadiusMeters { get; set; } = 20.0f;
        public float JunctionBlendDistanceMeters { get; set; } = 40.0f;
        public JunctionBlendFunctionType JunctionBlendFunction { get; set; } = JunctionBlendFunctionType.Cosine;
        public bool EnableEndpointTaper { get; set; } = true;
        public float EndpointTaperDistanceMeters { get; set; } = 30.0f;
        public float EndpointTerrainBlendStrength { get; set; } = 0.3f;

        /// <summary>
        ///     Applies all values from a preset to this material's settings.
        /// </summary>
        public void ApplyPreset(RoadSmoothingParameters preset)
        {
            // Primary parameters
            RoadWidthMeters = preset.RoadWidthMeters;
            TerrainAffectedRangeMeters = preset.TerrainAffectedRangeMeters;
            EnableMaxSlopeConstraint = preset.EnableMaxSlopeConstraint;
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

            // Junction harmonization parameters
            if (preset.JunctionHarmonizationParameters != null)
            {
                EnableJunctionHarmonization = preset.JunctionHarmonizationParameters.EnableJunctionHarmonization;
                JunctionDetectionRadiusMeters = preset.JunctionHarmonizationParameters.JunctionDetectionRadiusMeters;
                JunctionBlendDistanceMeters = preset.JunctionHarmonizationParameters.JunctionBlendDistanceMeters;
                JunctionBlendFunction = preset.JunctionHarmonizationParameters.BlendFunctionType;
                EnableEndpointTaper = preset.JunctionHarmonizationParameters.EnableEndpointTaper;
                EndpointTaperDistanceMeters = preset.JunctionHarmonizationParameters.EndpointTaperDistanceMeters;
                EndpointTerrainBlendStrength = preset.JunctionHarmonizationParameters.EndpointTerrainBlendStrength;
            }
        }

        /// <summary>
        ///     Builds the full RoadSmoothingParameters from all stored values.
        ///     Creates a subfolder per material name to avoid overwriting debug images.
        /// </summary>
        public RoadSmoothingParameters BuildRoadSmoothingParameters(string? debugOutputDirectory = null)
        {
            // Create a subfolder for this material's debug output to avoid overwriting other materials' images
            string? materialDebugDirectory = null;
            if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
            {
                // Sanitize material name for use as folder name
                var safeMaterialName = SanitizeFolderName(InternalName);
                materialDebugDirectory = Path.Combine(debugOutputDirectory, safeMaterialName);
            }

            var result = new RoadSmoothingParameters
            {
                // Primary parameters
                RoadWidthMeters = RoadWidthMeters,
                TerrainAffectedRangeMeters = TerrainAffectedRangeMeters,
                EnableMaxSlopeConstraint = EnableMaxSlopeConstraint,
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

                // Debug - use material-specific subfolder
                DebugOutputDirectory = materialDebugDirectory,
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

            // Junction harmonization parameters
            result.JunctionHarmonizationParameters = new JunctionHarmonizationParameters
            {
                EnableJunctionHarmonization = EnableJunctionHarmonization,
                JunctionDetectionRadiusMeters = JunctionDetectionRadiusMeters,
                JunctionBlendDistanceMeters = JunctionBlendDistanceMeters,
                BlendFunctionType = JunctionBlendFunction,
                EnableEndpointTaper = EnableEndpointTaper,
                EndpointTaperDistanceMeters = EndpointTaperDistanceMeters,
                EndpointTerrainBlendStrength = EndpointTerrainBlendStrength
            };

            return result;
        }

        /// <summary>
        ///     Sanitizes a material name for use as a folder name by removing invalid characters.
        /// </summary>
        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "unknown_material";

            // Get invalid path characters
            var invalidChars = Path.GetInvalidFileNameChars();
            
            // Replace invalid characters with underscores
            var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            
            // Remove any leading/trailing whitespace and dots (which can cause issues)
            sanitized = sanitized.Trim().Trim('.');
            
            // If result is empty, use a default name
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown_material" : sanitized;
        }
    }
}