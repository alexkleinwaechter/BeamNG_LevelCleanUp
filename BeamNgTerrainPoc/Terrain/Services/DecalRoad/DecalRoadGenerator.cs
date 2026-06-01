using System.Diagnostics;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
///     Generates DecalRoad objects from a UnifiedRoadNetwork.
///     Uses cross-section data (same as MasterSplineExporter) for accurate centerline alignment,
///     then applies lateral offsets and post-process overlap detection.
///     All roads are generated uninterrupted first, then a post-processor detects and resolves
///     overlaps on the actual generated geometry, followed by chunking.
/// </summary>
public class DecalRoadGenerator
{
    /// <summary>
    ///     Generate all DecalRoad objects for the given network.
    /// </summary>
    public static List<GeneratedDecalRoad> Generate(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        DecalRoadSettings settings,
        IReadOnlyDictionary<string, DecalRoadLayerSet> appDataDefaults)
    {
        var results = new List<GeneratedDecalRoad>();
        var splineSurfaces = new List<SplineSurfaceData>();

        // Build continuity lookup for post-processor overlap exemptions
        var continuityLookup = BuildContinuityLookup(network.Junctions);

        // Generate all DecalRoads uninterrupted (no corridor checking during generation)
        foreach (var spline in network.Splines)
        {
            // Skip structures only when exclusion is enabled — when disabled, bridges/tunnels
            // get DecalRoad surfaces like regular roads (flat terrain representation)
            if ((spline.IsBridge && spline.Parameters.ExcludeBridgesFromTerrain) ||
                (spline.IsTunnel && spline.Parameters.ExcludeTunnelsFromTerrain))
                continue;

            // Resolve layer set — roundabout splines use "roundabout" key
            DecalRoadLayerSet? layerSet;
            if (spline.IsRoundabout)
            {
                // Try "roundabout" key first, then fall back to regular cascade
                layerSet = DecalRoadLayerSetResolver.Resolve(
                    "roundabout", spline.MaterialName, settings, appDataDefaults);
                // If no roundabout-specific set found, try the road's own OSM type
                layerSet ??= DecalRoadLayerSetResolver.Resolve(
                    spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            }
            else
            {
                layerSet = DecalRoadLayerSetResolver.Resolve(
                    spline.OsmRoadType, spline.MaterialName, settings, appDataDefaults);
            }

            if (layerSet == null || !layerSet.IsEnabled)
                continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count < 2)
                continue;

            // Collect spline surface data for overlap detection footprint
            var surfaceSections = SubSampleCrossSections(crossSections, settings.NodeSpacingMeters);
            if (surfaceSections.Count >= 2)
            {
                var defaultWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
                // Use max width across all segments for overlap detection footprint
                // (conservative: ensures the widest section is fully covered)
                var maxMasterSplineWidth = spline.WidthProfile?.Segments.Max(s => s.MasterSplineWidth)
                                           ?? defaultWidth;
                splineSurfaces.Add(new SplineSurfaceData
                {
                    SplineId = spline.SplineId,
                    SurfaceHalfWidth = maxMasterSplineWidth / 2f,
                    CenterlinePoints = surfaceSections
                        .Select(cs => BeamNgCoordinateTransformer.TerrainToWorld2D(
                            cs.CenterPoint, terrainSizePixels, metersPerPixel))
                        .ToList()
                });
            }

            var splineResults = GenerateForSpline(
                spline, layerSet, crossSections,
                heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                settings.NodeSpacingMeters, settings);
            results.AddRange(splineResults);
        }

        // Post-process: detect and resolve overlaps on actual generated geometry
        results = DecalRoadOverlapPostProcessor.Process(results, splineSurfaces, continuityLookup);

        // Chunk long roads into ≤100 nodes with shared boundary nodes
        var chunkedResults = new List<GeneratedDecalRoad>(results.Count);
        foreach (var road in results)
        {
            var chunks = ChunkNodes(road.Nodes);
            for (var ci = 0; ci < chunks.Count; ci++)
            {
                var isFirst = ci == 0;
                var isLast = ci == chunks.Count - 1;
                chunkedResults.Add(new GeneratedDecalRoad
                {
                    Name = chunks.Count > 1 ? $"{road.Name}_{ci:D3}" : road.Name,
                    ParentGroupName = road.ParentGroupName,
                    Material = road.Material,
                    TextureLength = road.TextureLength,
                    RenderPriority = road.RenderPriority,
                    StartEndFade =
                    [
                        isFirst ? road.StartEndFade[0] : 0f,
                        isLast ? road.StartEndFade[1] : 0f
                    ],
                    DistanceFade = road.DistanceFade,
                    Drivability = road.Drivability,
                    Nodes = chunks[ci],
                    IsAIRoad = road.IsAIRoad,
                    AutoLanes = road.AutoLanes,
                    LanesLeft = road.LanesLeft,
                    LanesRight = road.LanesRight,
                    OneWay = road.OneWay,
                    FlipDirection = road.FlipDirection,
                    GatedRoad = road.GatedRoad,
                    SplineId = road.SplineId,
                    JunctionConstraint = road.JunctionConstraint,
                    JunctionReplacementMaterial = road.JunctionReplacementMaterial,
                    JunctionReplacementWidth = road.JunctionReplacementWidth,
                    JunctionReplacementTextureLength = road.JunctionReplacementTextureLength,
                    IsRoundaboutRoad = road.IsRoundaboutRoad,
                    PreserveContinuity = road.PreserveContinuity,
                    OverObjects = road.OverObjects,
                    ImprovedSpline = road.ImprovedSpline,
                    Smoothness = road.Smoothness,
                    Detail = road.Detail
                });
            }
        }

        return chunkedResults;
    }

    internal static List<GeneratedDecalRoad> GenerateForSpline(
        ParameterizedRoadSpline spline,
        DecalRoadLayerSet layerSet,
        IReadOnlyList<UnifiedCrossSection> crossSections,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeSpacingMeters,
        DecalRoadSettings settings)
    {
        var results = new List<GeneratedDecalRoad>();
        // Use master spline width for lateral offsets (cascade: MasterSplineWidth → RoadSurfaceWidth → RoadWidth)
        // MasterSplineWidth is intentionally narrower than RoadSurfaceWidth to account for material dither;
        // DecalRoad edge blends are designed to visually improve the dither area at road edges.
        // When a WidthProfile is available, query at distance 0 as the representative width;
        // per-section width variation is handled by the downstream per-node export paths.
        var roadWidth = spline.WidthProfile?.GetWidthsAtDistance(0f).masterSpline
                        ?? spline.Parameters.EffectiveMasterSplineWidthMeters;
        var laneCount = GetLaneCount(spline, layerSet);
        var splineName = GetSplineName(spline);

        // Sub-sample cross-sections at desired node spacing
        var sampledSections = SubSampleCrossSections(crossSections, nodeSpacingMeters);
        if (sampledSections.Count < 2) return results;

        // Compute cumulative distances along sampled cross-sections for lane boundary detection
        var csDistances = new List<float>(sampledSections.Count);
        if (sampledSections.Count > 0)
        {
            csDistances.Add(0f);
            for (var i = 1; i < sampledSections.Count; i++)
                csDistances.Add(csDistances[i - 1] +
                                Vector2.Distance(sampledSections[i - 1].CenterPoint, sampledSections[i].CenterPoint));
        }

        var laneChangeBoundaries = FindLaneChangeBoundaryIndices(spline.LaneSegments, csDistances);
        var hasLaneChanges = laneChangeBoundaries.Count > 0 && spline.LaneSegments != null;

        // Pre-compute range boundaries and segment indices for lane-dependent splitting.
        // Each range maps directly to a LaneSegment index, avoiding distance-based resolution
        // which suffers from polyline-vs-spline distance space mismatches.
        List<int>? rangeStarts = null;
        List<int>? rangeEnds = null;
        List<int>? rangeSegmentIndices = null;
        if (hasLaneChanges)
        {
            rangeStarts = [0, .. laneChangeBoundaries.Select(b => b.CsIndex)];
            rangeEnds = [.. laneChangeBoundaries.Select(b => b.CsIndex), sampledSections.Count];
            // Range 0 uses segment 0; range R (R>=1) uses boundary[R-1].SegmentIndex
            rangeSegmentIndices = [0, .. laneChangeBoundaries.Select(b => b.SegmentIndex)];
        }

        // Resolve base lane info for the initial expansion (first segment or null)
        var baseLaneInfo = spline.LaneSegments is { Count: > 0 }
            ? spline.LaneSegments[0].LaneInfo
            : null;

        // Expand layers with lane-info awareness (DirectionDivider positioning, IsPerLane filtering)
        var expandedLayers = ExpandLayersWithLaneInfo(layerSet.Layers, laneCount, baseLaneInfo);
        var chunkIndex = 0;

        // Phase A: All layers except IsPerLane+DirectionDivider when lane changes exist (those are handled in Phase B)
        foreach (var (layer, position, side, laneIndex, isFlipped) in expandedLayers)
        {
            if (!layer.IsEnabled) continue;

            // Skip IsPerLane and DirectionDivider in this phase when lane changes exist — handled in Phase B
            if (hasLaneChanges && (layer.IsPerLane || layer.LayerType == DecalRoadLayerType.DirectionDivider)) continue;

            if (IsLaneDependent(layer) && hasLaneChanges)
            {
                // Split at boundaries (AI road only in this phase)
                for (var r = 0; r < rangeStarts!.Count; r++)
                {
                    var rangeStart = rangeStarts[r];
                    var rangeEnd = rangeEnds![r];
                    if (rangeEnd - rangeStart < 2) continue;

                    // Use direct segment index instead of distance-based resolution
                    var segIdx = rangeSegmentIndices![r];
                    var segInfo = spline.LaneSegments![segIdx].LaneInfo;
                    // TotalLanes can be 0 when OSM has width= but no lanes= tag; use method-level default
                    var segLaneCount = segInfo.TotalLanes > 0 ? segInfo.TotalLanes : laneCount;

                    // rangeEnd is exclusive (from rangeEnds), convert to inclusive for filter
                    var filteredRanges = ComputeFilteredRanges(
                        layer, sampledSections, csDistances,
                        rangeStart, rangeEnd - 1, settings, spline.SplineId);

                    foreach (var seg in filteredRanges)
                    {
                        var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);

                        GenerateForLayerRange(
                            layer, position, side, laneIndex, isFlipped,
                            subSections, segInfo, segLaneCount,
                            spline, roadWidth, splineName,
                            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                            ref chunkIndex, results,
                            seg.Material, seg.Width, seg.TextureLength);
                    }
                }
            }
            else
            {
                // Lane-independent or no lane changes: apply constraint filters then generate
                var filteredRanges = ComputeFilteredRanges(
                    layer, sampledSections, csDistances,
                    0, sampledSections.Count - 1, settings, spline.SplineId);

                foreach (var seg in filteredRanges)
                {
                    var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);
                    GenerateForLayerRange(
                        layer, position, side, laneIndex, isFlipped,
                        subSections, baseLaneInfo, laneCount,
                        spline, roadWidth, splineName,
                        heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                        ref chunkIndex, results,
                        seg.Material, seg.Width, seg.TextureLength);
                }
            }
        }

        // Phase B: IsPerLane + DirectionDivider layers — re-expand per range with segment-specific lane info
        if (hasLaneChanges)
        {
            var laneAwareLayers = layerSet.Layers
                .Where(l => (l.IsPerLane || l.LayerType == DecalRoadLayerType.DirectionDivider) && l.IsEnabled)
                .ToList();
            for (var r = 0; r < rangeStarts!.Count; r++)
            {
                var rangeStart = rangeStarts[r];
                var rangeEnd = rangeEnds![r];
                if (rangeEnd - rangeStart < 2) continue;

                // Use direct segment index instead of distance-based resolution
                // to avoid polyline-vs-spline distance space mismatches
                var segIdx = rangeSegmentIndices![r];
                var segInfo = spline.LaneSegments![segIdx].LaneInfo;
                // TotalLanes can be 0 when OSM has width= but no lanes= tag; use method-level default
                var segLaneCount = segInfo.TotalLanes > 0 ? segInfo.TotalLanes : laneCount;

                // Re-expand with segment-specific lane count and lane info
                var segExpanded = ExpandLayersWithLaneInfo(laneAwareLayers, segLaneCount, segInfo);
                foreach (var (layer, position, side, laneIndex, isFlipped) in segExpanded)
                {
                    // rangeEnd is exclusive (from rangeEnds), convert to inclusive for filter
                    var filteredRanges = ComputeFilteredRanges(
                        layer, sampledSections, csDistances,
                        rangeStart, rangeEnd - 1, settings, spline.SplineId);

                    foreach (var seg in filteredRanges)
                    {
                        var subSections = sampledSections.GetRange(seg.Start, seg.End - seg.Start + 1);

                        GenerateForLayerRange(
                            layer, position, side, laneIndex, isFlipped,
                            subSections, segInfo, segLaneCount,
                            spline, roadWidth, splineName,
                            heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
                            ref chunkIndex, results,
                            seg.Material, seg.Width, seg.TextureLength);
                    }
                }
            }
        }

        return results;
    }

    private static bool IsLaneDependent(DecalRoadLayerDefinition layer)
    {
        return layer.IsPerLane
               || layer.LayerType == DecalRoadLayerType.AIRoad
               || layer.LayerType == DecalRoadLayerType.DirectionDivider;
    }

    private static void GenerateForLayerRange(
        DecalRoadLayerDefinition layer, float position, string side,
        int laneIndex, bool isFlipped,
        IReadOnlyList<UnifiedCrossSection> sections,
        OsmLaneInfo? segInfo, int segLaneCount,
        ParameterizedRoadSpline spline, float roadWidth, string splineName,
        float[,] heightMap, float metersPerPixel, int terrainSizePixels,
        float terrainBaseHeight,
        ref int chunkIndex, List<GeneratedDecalRoad> results,
        string? overrideMaterial = null,
        float? overrideWidth = null,
        float? overrideTextureLength = null)
    {
        // Note: IsTrackWidth and IsLaneWidth take precedence over overrideWidth.
        // These modes mean "fill the road/lane width" which is geometry-driven,
        // not material-driven. The override only applies to fixed-width layers.
        var baseWidth = overrideWidth ?? layer.Width;

        // Calculate laterally offset nodes and per-node widths using cross-section normals.
        // When a WidthProfile is available, query per-section distance for varying road width;
        // this enables DecalRoad overlays to widen/narrow with lane count changes.
        var offsetNodes2D = new List<Vector2>(sections.Count);
        var nodeWidths = new List<float>(sections.Count);
        foreach (var cs in sections)
        {
            var sectionRoadWidth = spline.WidthProfile?.GetWidthsAtDistance(cs.DistanceAlongSpline).masterSpline
                                   ?? roadWidth;

            var offset = position * 0.5f * sectionRoadWidth;
            var offsetPos = cs.CenterPoint + cs.NormalDirection * offset;
            offsetNodes2D.Add(offsetPos);

            float nodeWidth;
            if (layer.IsTrackWidth)
                nodeWidth = sectionRoadWidth;
            else if (layer.IsLaneWidth)
                nodeWidth = sectionRoadWidth / Math.Max(1, segLaneCount);
            else
                nodeWidth = baseWidth;
            nodeWidths.Add(nodeWidth);
        }

        // Convert all nodes to world coordinates with elevation from cross-sections
        // (no corridor-based interruption — post-processor handles overlap detection)
        var worldNodes = new List<float[]>(offsetNodes2D.Count);
        for (var i = 0; i < offsetNodes2D.Count; i++)
        {
            var pos = offsetNodes2D[i];
            var cs = sections[i];

            // Use TargetElevation from unified pipeline (smoothed/harmonized),
            // matching MasterSplineExporter behavior exactly
            float elevation;
            if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                elevation = cs.TargetElevation;
            else
                elevation = GetHeightMapElevation(heightMap, pos.X, pos.Y, metersPerPixel);

            var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                pos.X, pos.Y, elevation + terrainBaseHeight,
                terrainSizePixels, metersPerPixel);
            worldNodes.Add([worldPos.X, worldPos.Y, worldPos.Z, nodeWidths[i]]);
        }

        // Reverse node order for flipped layers (right-side mirrored).
        if (isFlipped)
            worldNodes.Reverse();

        chunkIndex++;
        var name = $"{splineName}_{layer.Name}_{side}_{chunkIndex:D3}";

        var startFade = isFlipped ? layer.FadeOut : layer.FadeIn;
        var endFade = isFlipped ? layer.FadeIn : layer.FadeOut;

        var road = new GeneratedDecalRoad
        {
            Name = name,
            ParentGroupName = splineName,
            Material = overrideMaterial ?? layer.Material,
            TextureLength = overrideTextureLength ?? layer.TextureLength,
            RenderPriority = layer.RenderPriority,
            StartEndFade = [startFade, endFade],
            DistanceFade = layer.DistanceFade,
            Drivability = layer.Drivability,
            IsAIRoad = layer.LayerType == DecalRoadLayerType.AIRoad,
            LanesLeft = layer.LanesLeft,
            LanesRight = layer.LanesRight,
            OneWay = layer.OneWay,
            FlipDirection = layer.FlipDirection,
            GatedRoad = layer.GatedRoad,
            OverObjects = layer.OverObjects,
            ImprovedSpline = layer.ImprovedSpline,
            Smoothness = layer.Smoothness,
            Detail = layer.Detail,
            Nodes = worldNodes,
            SplineId = spline.SplineId,
            JunctionConstraint = layer.JunctionConstraint,
            JunctionReplacementMaterial = layer.JunctionReplacementMaterial,
            JunctionReplacementWidth = layer.JunctionReplacementWidth > 0
                ? layer.JunctionReplacementWidth : (overrideWidth ?? layer.Width),
            JunctionReplacementTextureLength = layer.JunctionReplacementTextureLength > 0
                ? layer.JunctionReplacementTextureLength : (overrideTextureLength ?? layer.TextureLength),
            IsRoundaboutRoad = spline.IsRoundabout,
            PreserveContinuity = layer.LayerType == DecalRoadLayerType.DirectionDivider
        };

        // Override AI road properties from lane segment data.
        // Only override when TotalLanes > 0 (explicit OSM lane tags exist).
        // When TotalLanes is 0 (width-only OSM data, no lanes= tag), keep layer defaults
        // so that LanesLeft/LanesRight come from the layer set rather than being zeroed out.
        if (layer.LayerType == DecalRoadLayerType.AIRoad && segInfo is { TotalLanes: > 0 })
        {
            var (lanesRight, lanesLeft, oneWay, flipDirection) = DeriveAIRoadProperties(segInfo);
            road.LanesRight = lanesRight;
            road.LanesLeft = lanesLeft;
            road.OneWay = oneWay;
            road.FlipDirection = flipDirection;
            road.AutoLanes = false; // Disable auto-computation when we set lanes explicitly
        }

        // Roundabout AI road: always one-way, use OSM lanes or default to 1
        // Must run AFTER the segInfo override above (it overwrites those values)
        if (layer.LayerType == DecalRoadLayerType.AIRoad && spline.IsRoundabout)
        {
            road.OneWay = true;
            road.LanesLeft = 0;
            // If we have lane info from OSM with explicit lanes, use total lanes as right lanes.
            // TotalLanes can be 0 when OSM has width= but no lanes= tag — keep layer default.
            if (segInfo != null && segInfo.TotalLanes > 0)
                road.LanesRight = segInfo.TotalLanes;
            // Always disable auto-lane computation — we set lanes explicitly.
            // Without this, BeamNG's auto-lane logic overrides OneWay and LanesLeft at runtime.
            road.AutoLanes = false;
        }

        results.Add(road);
    }

    /// <summary>
    ///     Computes constraint-filtered sub-ranges for a layer within [rangeStart, rangeEnd].
    ///     Returns GenerationSegments tagged with the appropriate material/width/textureLength.
    ///     For ReplaceInCurve: returns interleaved straight (main) + curve (replacement) segments.
    ///     Randomizer applies only to straight segments when in ReplaceInCurve mode.
    /// </summary>
    internal static List<GenerationSegment> ComputeFilteredRanges(
        DecalRoadLayerDefinition layer,
        IReadOnlyList<UnifiedCrossSection> sampledSections,
        IReadOnlyList<float> csDistances,
        int rangeStart,
        int rangeEnd,
        DecalRoadSettings settings,
        int splineId)
    {
        // Helper to wrap (int,int) ranges into GenerationSegments with given overrides
        static List<GenerationSegment> Wrap(
            List<(int Start, int End)> ranges, string material, float width, float textureLength)
        {
            return ranges.Select(r => new GenerationSegment(r.Start, r.End, material, width, textureLength)).ToList();
        }

        var mainMaterial = layer.Material;
        var mainWidth = layer.Width;
        var mainTextureLength = layer.TextureLength;

        if (layer.CurveConstraint == CurveConstraintMode.None)
        {
            var eligibleRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };
            if (layer.Randomize && eligibleRanges.Count > 0)
            {
                var splineSeed = settings.RandomSeed ^ splineId;
                eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    eligibleRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }

            return Wrap(eligibleRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        // Both CurveOnly and ReplaceInCurve need curve ranges
        var curveRanges = DecalRoadLayerFilter.ApplyCurveFilter(
            sampledSections, csDistances, layer.CurveMinCurvature,
            layer.CurveTransitionLength, rangeStart, rangeEnd);

        if (layer.CurveConstraint == CurveConstraintMode.CurveOnly)
        {
            var eligibleRanges = curveRanges;
            if (layer.Randomize && eligibleRanges.Count > 0)
            {
                var splineSeed = settings.RandomSeed ^ splineId;
                eligibleRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    eligibleRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }

            return Wrap(eligibleRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        // ReplaceInCurve mode
        // Validate replacement material — fall back to None behavior if empty
        if (string.IsNullOrEmpty(layer.CurveReplacementMaterial))
        {
            Debug.WriteLine(
                $"[DecalRoad] ReplaceInCurve layer '{layer.Name}' has empty CurveReplacementMaterial — falling back to main material");

            var fallbackRanges = new List<(int Start, int End)> { (rangeStart, rangeEnd) };
            if (layer.Randomize)
            {
                var splineSeed = settings.RandomSeed ^ splineId;
                fallbackRanges = DecalRoadLayerFilter.ApplyRandomizer(
                    fallbackRanges, csDistances,
                    layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                    layer.RandomMinGapLength, layer.RandomMaxGapLength,
                    splineSeed);
            }

            return Wrap(fallbackRanges, mainMaterial, mainWidth, mainTextureLength);
        }

        var replacementMaterial = layer.CurveReplacementMaterial;
        var replacementWidth = layer.CurveReplacementWidth > 0 ? layer.CurveReplacementWidth : mainWidth;
        var replacementTextureLength = layer.CurveReplacementTextureLength > 0
            ? layer.CurveReplacementTextureLength
            : mainTextureLength;

        // Curve segments: replacement values, never randomized
        var curveSegments = Wrap(curveRanges, replacementMaterial, replacementWidth, replacementTextureLength);

        // Straight segments: main values, randomizer applies here
        var straightRanges = DecalRoadLayerFilter.InvertRanges(curveRanges, rangeStart, rangeEnd);
        if (layer.Randomize && straightRanges.Count > 0)
        {
            var splineSeed = settings.RandomSeed ^ splineId;
            straightRanges = DecalRoadLayerFilter.ApplyRandomizer(
                straightRanges, csDistances,
                layer.RandomMinPatchLength, layer.RandomMaxPatchLength,
                layer.RandomMinGapLength, layer.RandomMaxGapLength,
                splineSeed);
        }

        var straightSegments = Wrap(straightRanges, mainMaterial, mainWidth, mainTextureLength);

        // Merge and sort by Start
        var allSegments = new List<GenerationSegment>(curveSegments.Count + straightSegments.Count);
        allSegments.AddRange(curveSegments);
        allSegments.AddRange(straightSegments);
        allSegments.Sort((a, b) => a.Start.CompareTo(b.Start));

        return allSegments;
    }

    /// <summary>
    ///     Sub-samples cross-sections at the desired node spacing interval.
    ///     Matches MasterSplineExporter.SampleNodesFromUnifiedCrossSections step logic.
    ///     Always includes first and last cross-sections.
    /// </summary>
    internal static List<UnifiedCrossSection> SubSampleCrossSections(
        IReadOnlyList<UnifiedCrossSection> crossSections,
        float nodeSpacingMeters)
    {
        if (crossSections.Count <= 2)
            return crossSections.ToList();

        // Estimate total path length from cross-section positions
        float totalLength = 0;
        for (var i = 1; i < crossSections.Count; i++)
            totalLength += Vector2.Distance(crossSections[i - 1].CenterPoint, crossSections[i].CenterPoint);

        var nodeCount = Math.Max(2, (int)Math.Ceiling(totalLength / nodeSpacingMeters) + 1);
        var step = Math.Max(1, crossSections.Count / nodeCount);

        var sampled = new List<UnifiedCrossSection>();
        for (var i = 0; i < crossSections.Count; i += step)
            sampled.Add(crossSections[i]);

        // Always include the last cross-section
        if (sampled.Count == 0 || sampled[^1] != crossSections[^1])
            sampled.Add(crossSections[^1]);

        return sampled;
    }

    /// <summary>
    ///     Calculates lane boundary positions as normalized values in [-1, +1].
    ///     For N lanes, returns N-1 boundary positions.
    /// </summary>
    public static float[] CalculateLaneBoundaryPositions(int laneCount)
    {
        if (laneCount <= 1) return [];

        var positions = new float[laneCount - 1];
        for (var i = 1; i < laneCount; i++)
            positions[i - 1] = -1.0f + 2.0f * i / laneCount;

        return positions;
    }

    /// <summary>
    ///     Calculates the normalized position [-1, +1] of the direction boundary
    ///     (where opposing traffic meets) from OsmLaneInfo.
    ///     The boundary sits after LanesBackward lanes from the left edge.
    /// </summary>
    public static float CalculateDirectionBoundaryPosition(OsmLaneInfo info)
    {
        if (info.TotalLanes <= 0) return 0f;
        return -1.0f + 2.0f * info.LanesBackward / info.TotalLanes;
    }

    /// <summary>
    ///     Calculates lane boundary positions excluding the direction boundary.
    ///     Used for IsPerLane layers when TotalLanes >= 3 to avoid overlapping
    ///     with the DirectionDivider layer at the direction boundary.
    /// </summary>
    public static float[] CalculateLaneBoundaryPositionsExcludingDirectionBoundary(
        int laneCount, OsmLaneInfo? laneInfo)
    {
        var allBoundaries = CalculateLaneBoundaryPositions(laneCount);
        if (laneInfo == null || laneCount <= 2)
            return allBoundaries;

        var dirBoundary = CalculateDirectionBoundaryPosition(laneInfo);
        const float tolerance = 0.01f;
        return allBoundaries.Where(b => MathF.Abs(b - dirBoundary) > tolerance).ToArray();
    }

    /// <summary>
    ///     Calculates lane center positions as normalized values in [-1, +1].
    ///     For N lanes, returns N positions (one per lane).
    ///     Matches BeamNG layerMgr.lua tread mark positioning:
    ///     Left lane i:  ((-i * laneWidth) + laneWidth/2) / halfWidth
    ///     Right lane i: ((i * laneWidth) - laneWidth/2) / halfWidth
    ///     Simplified: lane center[k] = -1.0 + (2*k + 1) / N  for k in 0..N-1
    /// </summary>
    public static float[] CalculateLaneCenterPositions(int laneCount)
    {
        if (laneCount <= 0) return [];

        var positions = new float[laneCount];
        for (var k = 0; k < laneCount; k++)
            positions[k] = -1.0f + (2 * k + 1.0f) / laneCount;

        return positions;
    }

    /// <summary>
    ///     Splits a node list into chunks of at most maxNodesPerChunk.
    ///     Adjacent chunks share a boundary node (overlap by 1) to ensure
    ///     seamless spline continuity, matching BeamNG's chunking behavior.
    ///     Avoids tiny last chunks (less than minLastChunkNodes) by redistributing
    ///     nodes evenly — prevents fade values from making short chunks invisible.
    /// </summary>
    public static List<List<float[]>> ChunkNodes(List<float[]> nodes, int maxNodesPerChunk = 80,
        int minLastChunkNodes = 20)
    {
        if (nodes.Count <= maxNodesPerChunk)
            return [nodes];

        // Calculate how many chunks we need, accounting for overlap of 1
        var stride = maxNodesPerChunk - 1;
        var naiveChunkCount = (int)Math.Ceiling((double)(nodes.Count - 1) / stride);

        // Check if the last chunk would be too small
        var lastChunkSize = nodes.Count - (naiveChunkCount - 1) * stride;
        if (lastChunkSize < minLastChunkNodes && naiveChunkCount > 1)
        {
            // Redistribute: use equal-sized chunks that all fit within maxNodesPerChunk
            // Each chunk overlaps by 1, so total coverage = sum(chunkSize - 1) + 1 = nodes.Count
            // With N chunks: N * (chunkSize - 1) + 1 = nodes.Count
            // chunkSize = ceil((nodes.Count - 1) / N) + 1
            var chunkSize = (int)Math.Ceiling((double)(nodes.Count - 1) / naiveChunkCount) + 1;
            chunkSize = Math.Min(chunkSize, maxNodesPerChunk);

            var chunks = new List<List<float[]>>();
            var i = 0;
            while (i < nodes.Count)
            {
                var count = Math.Min(chunkSize, nodes.Count - i);
                chunks.Add(nodes.GetRange(i, count));
                i += count - 1; // overlap by 1
                if (i >= nodes.Count - 1) break;
            }

            return chunks;
        }

        // Standard chunking — last chunk is large enough
        {
            var chunks = new List<List<float[]>>();
            var i = 0;
            while (i < nodes.Count)
            {
                var count = Math.Min(maxNodesPerChunk, nodes.Count - i);
                chunks.Add(nodes.GetRange(i, count));
                i += maxNodesPerChunk - 1;
            }

            return chunks;
        }
    }

    /// <summary>
    ///     Expands layers by mirroring and per-lane replication.
    ///     Returns tuples of (layer, normalizedPosition, sideLabel, laneIndex, isFlipped).
    ///     When IsFlipped is true, nodes must be reversed to flip texture UV along the spline,
    ///     matching BeamNG's layerMgr.lua isFlip behavior for right-side mirrored layers.
    /// </summary>
    internal static List<(DecalRoadLayerDefinition Layer, float Position, string Side, int LaneIndex, bool IsFlipped)>
        ExpandLayers(IReadOnlyList<DecalRoadLayerDefinition> layers, int laneCount)
    {
        var expanded = new List<(DecalRoadLayerDefinition, float, string, int, bool)>();

        foreach (var layer in layers)
            if (layer.LayerType == DecalRoadLayerType.DirectionDivider)
            {
                // DirectionDivider only for multi-lane roads (>= 3 lanes) with lane markings enabled.
                // For <= 2 lanes the IsPerLane dashed marking (or nothing) serves as center line.
                var hasEnabledLaneMarking = layers.Any(l =>
                    l.LayerType == DecalRoadLayerType.LaneMarking && l.IsEnabled);
                if (laneCount < 3 || !hasEnabledLaneMarking) continue;

                var side = layer.Position < -0.01f ? "L" : layer.Position > 0.01f ? "R" : "C";
                expanded.Add((layer, layer.Position, side, 0, false));
            }
            else if (layer.LayerType == DecalRoadLayerType.TreadMarks && layer.IsLaneWidth)
            {
                // Per-lane tread marks: one DecalRoad per lane, centered in each lane.
                // Covers all lanes including center lane for odd counts (e.g. 3 lanes).
                // isFlip = false for all (tire tracks are symmetric).
                var centers = CalculateLaneCenterPositions(laneCount);
                int leftNum = 0, rightNum = 0;
                for (var i = 0; i < centers.Length; i++)
                {
                    string side;
                    if (centers[i] < -0.01f)
                        side = $"L{++leftNum}";
                    else if (centers[i] > 0.01f)
                        side = $"R{++rightNum}";
                    else
                        side = "C";
                    expanded.Add((layer, centers[i], side, i, false));
                }
            }
            else if (layer.IsPerLane)
            {
                // Replicate at each lane boundary
                var boundaries = CalculateLaneBoundaryPositions(laneCount);
                for (var i = 0; i < boundaries.Length; i++) expanded.Add((layer, boundaries[i], $"C{i + 1}", i, false));
            }
            else if (layer.IsMirrored)
            {
                // Left: original node order, Right: reversed nodes (flips texture UV)
                expanded.Add((layer, -MathF.Abs(layer.Position), "L", 0, false));
                expanded.Add((layer, MathF.Abs(layer.Position), "R", 0, true));
            }
            else
            {
                // Single placement at declared position
                var side = layer.Position < -0.01f ? "L" : layer.Position > 0.01f ? "R" : "C";
                expanded.Add((layer, layer.Position, side, 0, false));
            }

        return expanded;
    }

    /// <summary>
    ///     Expands layers with lane-info awareness:
    ///     - DirectionDivider: positioned at the direction boundary, suppressed when TotalLanes &lt;= 2
    ///     - IsPerLane: skips the direction boundary position when TotalLanes &gt;= 3
    ///     Falls back to standard ExpandLayers when laneInfo is null.
    /// </summary>
    internal static List<(DecalRoadLayerDefinition Layer, float Position, string Side, int LaneIndex, bool IsFlipped)>
        ExpandLayersWithLaneInfo(IReadOnlyList<DecalRoadLayerDefinition> layers, int laneCount, OsmLaneInfo? laneInfo)
    {
        if (laneInfo == null)
            return ExpandLayers(layers, laneCount);

        var expanded = new List<(DecalRoadLayerDefinition, float, string, int, bool)>();

        foreach (var layer in layers)
            if (layer.LayerType == DecalRoadLayerType.DirectionDivider)
            {
                // DirectionDivider only renders when TotalLanes >= 3 and lane markings are present and enabled.
                // For 2-lane roads, the IsPerLane dashed marking serves as the center line.
                // When no LaneMarking layer exists or is enabled, suppress DirectionDivider
                // (small roads with no lane markings should have no divider either).
                var hasEnabledLaneMarking = layers.Any(l =>
                    l.LayerType == DecalRoadLayerType.LaneMarking && l.IsEnabled);
                if (laneInfo.TotalLanes >= 3 && !laneInfo.IsOneWay && hasEnabledLaneMarking)
                {
                    var dirPos = CalculateDirectionBoundaryPosition(laneInfo);
                    var side = dirPos < -0.01f ? "L" : dirPos > 0.01f ? "R" : "C";
                    expanded.Add((layer, dirPos, side, 0, false));
                }
                // else: suppressed for <= 2 lanes or one-way roads
            }
            else if (layer.LayerType == DecalRoadLayerType.TreadMarks && layer.IsLaneWidth)
            {
                // Per-lane tread marks: one DecalRoad per lane, centered in each lane.
                var centers = CalculateLaneCenterPositions(laneCount);
                int leftNum = 0, rightNum = 0;
                for (var i = 0; i < centers.Length; i++)
                {
                    string side;
                    if (centers[i] < -0.01f)
                        side = $"L{++leftNum}";
                    else if (centers[i] > 0.01f)
                        side = $"R{++rightNum}";
                    else
                        side = "C";
                    expanded.Add((layer, centers[i], side, i, false));
                }
            }
            else if (layer.IsPerLane)
            {
                // When TotalLanes >= 3, skip the direction boundary (DirectionDivider handles it)
                var boundaries = laneInfo.TotalLanes >= 3 && !laneInfo.IsOneWay
                    ? CalculateLaneBoundaryPositionsExcludingDirectionBoundary(laneCount, laneInfo)
                    : CalculateLaneBoundaryPositions(laneCount);
                for (var i = 0; i < boundaries.Length; i++) expanded.Add((layer, boundaries[i], $"C{i + 1}", i, false));
            }
            else if (layer.IsMirrored)
            {
                expanded.Add((layer, -MathF.Abs(layer.Position), "L", 0, false));
                expanded.Add((layer, MathF.Abs(layer.Position), "R", 0, true));
            }
            else
            {
                var side = layer.Position < -0.01f ? "L" : layer.Position > 0.01f ? "R" : "C";
                expanded.Add((layer, layer.Position, side, 0, false));
            }

        return expanded;
    }

    /// <summary>
    ///     For Phase 2: lookup of which splines are continuous at which junctions.
    ///     Key = splineId, Value = set of splineIds that terminate at junctions where key is continuous.
    ///     If spline A is continuous at a junction where spline B terminates,
    ///     then ContinuityLookup[A] contains B.
    /// </summary>
    private static Dictionary<int, HashSet<int>> BuildContinuityLookup(
        IReadOnlyList<NetworkJunction> junctions)
    {
        var lookup = new Dictionary<int, HashSet<int>>();

        foreach (var junction in junctions)
        {
            if (junction.IsExcluded) continue;
            if (junction.Type == JunctionType.Endpoint) continue;

            // Roundabout rings should NOT get continuity exemptions.
            // Their markings should be suppressed where connecting roads' corridors overlap.
            if (junction.Type == JunctionType.Roundabout) continue;

            var continuousIds = junction.GetContinuousRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();
            var terminatingIds = junction.GetTerminatingRoads()
                .Select(c => c.Spline.SplineId).ToHashSet();

            // For each continuous road, record which terminating roads it can ignore
            foreach (var contId in continuousIds)
            {
                if (!lookup.TryGetValue(contId, out var set))
                {
                    set = [];
                    lookup[contId] = set;
                }

                foreach (var termId in terminatingIds)
                    set.Add(termId);
            }
        }

        return lookup;
    }


    private static int GetLaneCount(ParameterizedRoadSpline spline, DecalRoadLayerSet layerSet)
    {
        // Use first lane segment's TotalLanes if available and non-zero.
        // TotalLanes can be 0 when OSM has a width= tag but no lanes= tag —
        // in that case fall through to the layer set default.
        if (spline.LaneSegments is { Count: > 0 } segs && segs[0].LaneInfo.TotalLanes > 0)
            return segs[0].LaneInfo.TotalLanes;

        return layerSet.DefaultLaneCount;
    }

    /// <summary>
    ///     Returns the OsmLaneInfo active at the given distance along the spline.
    ///     Segments are assumed sorted ascending by StartDistance.
    /// </summary>
    public static OsmLaneInfo ResolveLaneInfo(
        IReadOnlyList<LaneSegment> segments, float distance)
    {
        // Walk backwards from end to find the last segment with StartDistance <= distance
        for (var i = segments.Count - 1; i >= 0; i--)
            if (segments[i].StartDistance <= distance)
                return segments[i].LaneInfo;
        return segments[0].LaneInfo;
    }

    /// <summary>
    ///     Derives BeamNG AI road properties from OsmLaneInfo.
    ///     lanesRight = forward direction, lanesLeft = backward direction.
    ///     LanesBothWays added to forward for AI pathfinding purposes.
    /// </summary>
    public static (int LanesRight, int LanesLeft, bool OneWay, bool FlipDirection)
        DeriveAIRoadProperties(OsmLaneInfo info)
    {
        var lanesRight = info.LanesForward + info.LanesBothWays;
        var lanesLeft = info.LanesBackward;
        var oneWay = info.IsOneWay;
        var flipDirection = info.IsOneWay && info.LanesForward == 0 && info.LanesBackward > 0;

        return (lanesRight, lanesLeft, oneWay, flipDirection);
    }

    /// <summary>
    ///     Returns cross-section indices where lane configuration changes.
    ///     Used to split lane-dependent layers at lane-change boundaries.
    /// </summary>
    /// <summary>
    ///     Finds cross-section indices where lane configuration changes, along with
    ///     the LaneSegment index that starts at each boundary.
    ///     Returns (CsIndex, SegmentIndex) pairs so callers can look up lane info
    ///     directly by segment index instead of error-prone distance-based resolution.
    /// </summary>
    public static List<(int CsIndex, int SegmentIndex)> FindLaneChangeBoundaryIndices(
        IReadOnlyList<LaneSegment>? segments,
        IReadOnlyList<float> crossSectionDistances)
    {
        if (segments == null || segments.Count <= 1)
            return [];

        var boundaries = new List<(int CsIndex, int SegmentIndex)>();
        // For each segment boundary (skip first), find the nearest cross-section
        for (var s = 1; s < segments.Count; s++)
        {
            var boundaryDist = segments[s].StartDistance;
            // Linear scan for nearest cross-section
            var bestIdx = 0;
            var bestDelta = float.MaxValue;
            for (var i = 0; i < crossSectionDistances.Count; i++)
            {
                var delta = MathF.Abs(crossSectionDistances[i] - boundaryDist);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = i;
                }
            }

            // Avoid duplicate boundaries and out-of-range
            if (bestIdx > 0 && bestIdx < crossSectionDistances.Count - 1)
                if (boundaries.Count == 0 || boundaries[^1].CsIndex != bestIdx)
                    boundaries.Add((bestIdx, s));
        }

        return boundaries;
    }

    private static string GetSplineName(ParameterizedRoadSpline spline)
    {
        // Use material name + ID for unique naming (matches MasterSplineExporter pattern)
        return $"{spline.MaterialName}_{spline.SplineId:D3}";
    }

    private static float GetHeightMapElevation(
        float[,] heightMap, float terrainX, float terrainY, float metersPerPixel)
    {
        var pixelX = (int)(terrainX / metersPerPixel);
        var pixelY = (int)(terrainY / metersPerPixel);
        var size = heightMap.GetLength(0);
        pixelX = Math.Clamp(pixelX, 0, size - 1);
        pixelY = Math.Clamp(pixelY, 0, size - 1);
        return heightMap[pixelY, pixelX]; // [y, x] row-major
    }

    /// <summary>
    ///     A filtered sub-range tagged with material/width/textureLength overrides.
    ///     For None/CurveOnly: uses the layer's own values.
    ///     For ReplaceInCurve: straight segments use main values, curve segments use replacement values.
    /// </summary>
    internal record struct GenerationSegment(
        int Start,
        int End,
        string Material,
        float Width,
        float TextureLength
    );
}