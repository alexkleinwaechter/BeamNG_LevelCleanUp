using System.Drawing;
using System.Numerics;
using BeamNG.Procedural3D.Building;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Parses OSM building features into <see cref="BuildingData"/> and <see cref="BeamNG.Procedural3D.Building.Building"/> objects.
///
/// Responsibilities:
/// - Extracts building polygon features from <see cref="OsmQueryResult"/>
/// - Parses OSM tags (height, levels, material, roof:*, building:colour, etc.)
/// - Converts WGS84 polygon coordinates to local meter-space footprints (origin at centroid)
/// - Computes world position (centroid) and ground elevation via terrain heightmap sampling
/// - Groups building:part features into parent buildings via centroid point-in-polygon test
///
/// Coordinate pipeline:
///   WGS84 (lon/lat) → terrain pixels (via OsmGeometryProcessor) → meters (× metersPerPixel)
///   → local footprint (subtract centroid) + WorldPosition (centroid in BeamNG world space)
/// </summary>
public class OsmBuildingParser
{
    private readonly OsmGeometryProcessor _geometryProcessor;

    public OsmBuildingParser(OsmGeometryProcessor geometryProcessor)
    {
        _geometryProcessor = geometryProcessor;
    }

    // ==========================================
    // Building-with-Parts API (Phase 2)
    // ==========================================

    /// <summary>
    /// Parses all building and building:part polygon features from an OSM query result,
    /// grouping parts into their parent buildings.
    ///
    /// Port of OSM2World's Building.java part discovery:
    /// 1. Separate features into main buildings (building=*) and parts (building:part=*)
    /// 2. For each main building, find contained parts via centroid point-in-polygon test
    /// 3. Coverage check: if parts cover >= 90% of building area, use only parts; otherwise
    ///    treat the building outline itself as a single part (fallback)
    /// 4. All parts share the parent building's centroid as coordinate origin
    /// </summary>
    public List<BeamNG.Procedural3D.Building.Building> ParseBuildingsWithParts(
        OsmQueryResult queryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler = null)
    {
        // 1. Collect main building features (building=*) and part features (building:part=*)
        var mainBuildingFeatures = queryResult.Features
            .Where(f => f.GeometryType == OsmGeometryType.Polygon
                        && f.Tags.ContainsKey("building")
                        && !IsBuildingPartOnly(f))
            .ToList();

        var partFeatures = queryResult.Features
            .Where(f => f.GeometryType == OsmGeometryType.Polygon && IsBuildingPart(f))
            .ToList();

        Console.WriteLine($"OsmBuildingParser: Found {mainBuildingFeatures.Count} main buildings, " +
                          $"{partFeatures.Count} building:part features");

        // 2. Transform all part features to world-space meter coordinates (for point-in-polygon matching)
        var partCandidates = new List<PartCandidate>();
        foreach (var pf in partFeatures)
        {
            var meterCoords = TransformRingToMeters(pf.Coordinates, bbox, terrainSize, metersPerPixel);
            if (meterCoords.Count < 3) continue;
            var centroid = ComputeCentroid(meterCoords);
            partCandidates.Add(new PartCandidate(pf, meterCoords, centroid));
        }

        // 3. Build each main building, discovering which parts belong to it
        var buildings = new List<BeamNG.Procedural3D.Building.Building>();
        var usedPartIds = new HashSet<long>();
        int skippedTooSmall = 0;
        int skippedDegenerate = 0;
        int buildingsWithParts = 0;

        foreach (var feature in mainBuildingFeatures)
        {
            var building = ParseMainBuilding(feature, partCandidates, usedPartIds,
                bbox, terrainSize, metersPerPixel, heightSampler);
            if (building == null)
            {
                skippedDegenerate++;
                continue;
            }

            // Skip very small buildings (< 4 m² footprint area)
            if (building.OutlinePolygon != null && ComputePolygonArea(building.OutlinePolygon) < 4.0f)
            {
                skippedTooSmall++;
                continue;
            }

            if (building.Parts.Count > 1) buildingsWithParts++;
            buildings.Add(building);
        }

        // 4. Any remaining unmatched parts become standalone single-part buildings
        int orphanParts = 0;
        foreach (var pc in partCandidates)
        {
            if (usedPartIds.Contains(pc.Feature.Id)) continue;

            var orphanBuilding = CreateStandalonePartBuilding(pc, bbox, terrainSize, metersPerPixel, heightSampler);
            if (orphanBuilding == null) continue;
            // Use lower threshold for building:part orphans (pillars etc. can be small)
            // OSM2World has no area filter at all
            if (orphanBuilding.OutlinePolygon != null && ComputePolygonArea(orphanBuilding.OutlinePolygon) < 0.5f) continue;

            buildings.Add(orphanBuilding);
            orphanParts++;
        }

        Console.WriteLine($"OsmBuildingParser: Parsed {buildings.Count} buildings " +
                          $"({buildingsWithParts} with multiple parts, {orphanParts} orphan parts, " +
                          $"skipped {skippedDegenerate} degenerate, {skippedTooSmall} too small)");

        // 4b. Deduplicate by OsmId — keep the first occurrence (main building over orphan part).
        // Duplicates can arise when a feature has both building=* and building:part=* tags,
        // or from multipolygon relations where member ways also carry building tags.
        int beforeDedup = buildings.Count;
        var seenOsmIds = new HashSet<long>();
        buildings = buildings.Where(b => seenOsmIds.Add(b.OsmId)).ToList();
        if (buildings.Count < beforeDedup)
        {
            Console.WriteLine($"OsmBuildingParser: Deduplicated {beforeDedup - buildings.Count} buildings with duplicate OsmId");
        }

        // 5. Detect building passages (tunnel=building_passage)
        // Port of OSM2World BuildingPart.java lines 152-267:
        // Find roads tagged tunnel=building_passage that share OSM nodes with building outlines.
        var passageFeatures = queryResult.Features
            .Where(f => f.GeometryType == OsmGeometryType.LineString
                        && f.GetStructureType() == StructureType.BuildingPassage)
            .ToList();

        if (passageFeatures.Count > 0)
        {
            DetectBuildingPassages(buildings, mainBuildingFeatures, passageFeatures,
                bbox, terrainSize, metersPerPixel);
        }

        // Free outline polygons (no longer needed after parsing)
        foreach (var b in buildings)
            b.OutlinePolygon = null;

        return buildings;
    }

    /// <summary>
    /// Parses a main building outline and discovers which building:part features belong to it.
    /// </summary>
    private BeamNG.Procedural3D.Building.Building? ParseMainBuilding(
        OsmFeature feature,
        List<PartCandidate> allPartCandidates,
        HashSet<long> usedPartIds,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler)
    {
        // Transform outline to meter-space
        var outerMeterCoords = TransformRingToMeters(feature.Coordinates, bbox, terrainSize, metersPerPixel);
        if (outerMeterCoords.Count < 3) return null;

        var buildingCentroid = ComputeCentroid(outerMeterCoords);

        // Convert outline to local coordinates (origin at building centroid)
        var localOutline = outerMeterCoords
            .Select(p => new Vector2(p.X - buildingCentroid.X, p.Y - buildingCentroid.Y))
            .ToList();
        if (!IsCounterClockwise(localOutline)) localOutline.Reverse();
        localOutline = EnsureClosed(localOutline);

        // Sample ground elevation
        float groundElevation = 0f;
        if (heightSampler != null)
            groundElevation = outerMeterCoords.Min(p => heightSampler(p.X, p.Y));

        string buildingType = GetTagValue(feature.Tags, "building") ?? "yes";

        var building = new BeamNG.Procedural3D.Building.Building
        {
            OsmId = feature.Id,
            BuildingType = buildingType,
            GroundElevation = groundElevation,
            WorldPosition = new Vector3(buildingCentroid.X, buildingCentroid.Y, groundElevation),
            OutlinePolygon = localOutline
        };

        // Find building:part features whose centroid is inside this building's outline
        float outlineArea = ComputePolygonArea(localOutline);
        float partAreaSum = 0f;

        var containedParts = new List<(PartCandidate Candidate, BuildingData Data)>();
        foreach (var pc in allPartCandidates)
        {
            if (usedPartIds.Contains(pc.Feature.Id)) continue;

            // Test if part centroid is inside building outline (in world meter-space)
            var partCentroidLocal = new Vector2(
                pc.WorldCentroid.X - buildingCentroid.X,
                pc.WorldCentroid.Y - buildingCentroid.Y);

            if (!PointInPolygon(partCentroidLocal, localOutline)) continue;

            // Parse this part using the BUILDING's centroid as coordinate origin
            var partData = ParseBuildingPart(pc.Feature, buildingCentroid,
                bbox, terrainSize, metersPerPixel, feature.Tags);
            if (partData == null) continue;

            float partArea = ComputePolygonArea(partData.FootprintOuter);
            if (partArea < 0.05f) continue; // skip degenerate slivers only (OSM2World has no area filter)

            containedParts.Add((pc, partData));
            partAreaSum += partArea;
        }

        // Port of OSM2World Building.java lines 95-116:
        // 1. ALWAYS keep contained parts (never discard them)
        // 2. If parts cover < 90% of outline, ALSO add the building outline as an additional part
        foreach (var (candidate, data) in containedParts)
        {
            building.Parts.Add(data);
            usedPartIds.Add(candidate.Feature.Id);
        }

        bool partsFullyCover = containedParts.Count > 0
                               && partAreaSum >= 0.9f * outlineArea;

        if (!partsFullyCover)
        {
            // Parts don't exist or don't cover enough — add building outline as a part too
            var outlinePart = ParseFeatureAsPart(feature, buildingCentroid,
                bbox, terrainSize, metersPerPixel, parentTags: null);
            if (outlinePart != null)
                building.Parts.Add(outlinePart);
        }

        if (building.Parts.Count == 0) return null;

        return building;
    }

    /// <summary>
    /// Creates a standalone Building from an orphan building:part that didn't match any main building.
    /// </summary>
    private BeamNG.Procedural3D.Building.Building? CreateStandalonePartBuilding(
        PartCandidate pc,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler)
    {
        var centroid = pc.WorldCentroid;

        float groundElevation = 0f;
        if (heightSampler != null)
            groundElevation = pc.WorldMeterCoords.Min(p => heightSampler(p.X, p.Y));

        var partData = ParseFeatureAsPart(pc.Feature, centroid,
            bbox, terrainSize, metersPerPixel, parentTags: null);
        if (partData == null) return null;

        string buildingType = GetTagValue(pc.Feature.Tags, "building:part") ?? "yes";

        var localOutline = partData.FootprintOuter;

        return new BeamNG.Procedural3D.Building.Building
        {
            OsmId = pc.Feature.Id,
            BuildingType = buildingType,
            GroundElevation = groundElevation,
            WorldPosition = new Vector3(centroid.X, centroid.Y, groundElevation),
            OutlinePolygon = localOutline,
            Parts = new List<BuildingData> { partData }
        };
    }

    /// <summary>
    /// Parses a building:part feature using the parent building's centroid as coordinate origin.
    /// Tags from the parent building are inherited for properties not set on the part.
    /// </summary>
    private BuildingData? ParseBuildingPart(
        OsmFeature partFeature,
        Vector2 buildingCentroid,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Dictionary<string, string> parentTags)
    {
        return ParseFeatureAsPart(partFeature, buildingCentroid,
            bbox, terrainSize, metersPerPixel, parentTags);
    }

    /// <summary>
    /// Parses any feature (main building or building:part) as a BuildingData part,
    /// using the given centroid as coordinate origin. Optionally inherits tags from parent.
    /// </summary>
    private BuildingData? ParseFeatureAsPart(
        OsmFeature feature,
        Vector2 centroid,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Dictionary<string, string>? parentTags)
    {
        var outerMeterCoords = TransformRingToMeters(feature.Coordinates, bbox, terrainSize, metersPerPixel);
        if (outerMeterCoords.Count < 3) return null;

        var localOuter = outerMeterCoords
            .Select(p => new Vector2(p.X - centroid.X, p.Y - centroid.Y))
            .ToList();
        if (!IsCounterClockwise(localOuter)) localOuter.Reverse();
        localOuter = EnsureClosed(localOuter);

        // Process inner rings
        var localHoles = TransformInnerRings(feature, centroid, bbox, terrainSize, metersPerPixel);

        // Build tag set with inheritance from parent building
        var tags = feature.Tags;

        // Determine building type for defaults lookup
        string buildingType = GetTagValue(tags, "building:part")
                              ?? GetTagValue(tags, "building")
                              ?? GetInheritedTag(parentTags, "building")
                              ?? "yes";

        return ParseTagsIntoBuildingData(feature.Id, buildingType, localOuter, localHoles, tags, parentTags);
    }

    /// <summary>
    /// Parses OSM tags (with optional inheritance from parent) into a BuildingData object.
    /// Uses LevelAndHeightData for centralized height computation.
    /// Shared between main buildings and building:part features.
    /// </summary>
    private static BuildingData ParseTagsIntoBuildingData(
        long osmId,
        string buildingType,
        List<Vector2> localOuter,
        List<List<Vector2>>? localHoles,
        Dictionary<string, string> tags,
        Dictionary<string, string>? parentTags)
    {
        // Resolve roof shape: part tags > parent tags > defaults
        string? roofShapeTag = GetTagValue(tags, "roof:shape")
                               ?? GetInheritedTag(parentTags, "roof:shape");
        var defaults = BuildingDefaults.GetDefaultsFor(buildingType, roofShapeTag);

        // Tag-based default overrides (Java BuildingDefaults.getDefaultsFor(TagSet) handles these
        // but our GetDefaultsFor only receives the building type string)
        string? parkingTag = GetTagValueWithInheritance(tags, parentTags, "parking");
        if (parkingTag?.Equals("multi-storey", StringComparison.OrdinalIgnoreCase) == true)
            defaults = defaults with { Levels = 5, HasWindows = false };

        string? manMadeTag = GetTagValueWithInheritance(tags, parentTags, "man_made");
        if (manMadeTag?.Equals("chimney", StringComparison.OrdinalIgnoreCase) == true)
            defaults = defaults with
            {
                Levels = 1, HeightPerLevel = 10.0f, WallMaterial = "BRICK",
                RoofMaterial = "BRICK", HasWindows = false, RoofShape = "flat"
            };

        // Parse explicit tag values for LevelAndHeightData
        TryParseMeters(GetTagValueWithInheritance(tags, parentTags, "height"), out float parsedHeight);
        TryParseMeters(GetTagValueWithInheritance(tags, parentTags, "building:height"), out float parsedBuildingHeight);
        float? taggedHeight = parsedHeight > 0 ? parsedHeight
            : parsedBuildingHeight > 0 ? parsedBuildingHeight : null;

        TryParseMeters(GetTagValue(tags, "min_height"), out float parsedMinHeight);
        TryParseMeters(GetTagValue(tags, "building:min_height"), out float parsedBuildingMinHeight);
        float? taggedMinHeight = parsedMinHeight > 0 ? parsedMinHeight
            : parsedBuildingMinHeight > 0 ? parsedBuildingMinHeight : null;

        TryParseMeters(GetTagValue(tags, "facade_height"), out float parsedFacadeHeight);
        float? taggedFacadeHeight = parsedFacadeHeight > 0 ? parsedFacadeHeight : null;

        int.TryParse(GetTagValueWithInheritance(tags, parentTags, "building:levels"), out int parsedLevels);
        int? taggedLevels = parsedLevels > 0 ? parsedLevels : null;

        TryParseMeters(GetTagValue(tags, "roof:height"), out float parsedRoofHeight);
        float? taggedRoofHeight = parsedRoofHeight > 0 ? parsedRoofHeight : null;

        int.TryParse(GetTagValue(tags, "roof:levels"), out int parsedRoofLevels);
        int? taggedRoofLevels = parsedRoofLevels > 0 ? parsedRoofLevels : null;

        // building:min_level (Java line 119) — for floating building parts
        int.TryParse(GetTagValue(tags, "building:min_level"), out int parsedMinLevel);
        int? taggedMinLevel = parsedMinLevel > 0 ? parsedMinLevel : null;

        // Compute polygon diameter for dome roof height (Java line 166: outline.getDiameter() / 2)
        float? polygonDiameter = defaults.RoofShape == "dome"
            ? ComputePolygonDiameter(localOuter)
            : null;

        // Centralized height computation
        var heightData = LevelAndHeightData.Compute(
            taggedHeight, taggedMinHeight, taggedFacadeHeight,
            taggedLevels, taggedRoofHeight, taggedRoofLevels,
            defaults.RoofShape, defaults,
            taggedMinLevel, defaults.HasWalls, polygonDiameter);

        // Parse roof direction
        float? roofDirection = null;
        if (float.TryParse(GetTagValue(tags, "roof:direction"),
                System.Globalization.CultureInfo.InvariantCulture, out float parsedDirection))
            roofDirection = parsedDirection;

        // Parse roof angle
        float? roofAngle = null;
        if (float.TryParse(GetTagValue(tags, "roof:angle"),
                System.Globalization.CultureInfo.InvariantCulture, out float parsedAngle))
            roofAngle = parsedAngle;

        string roofOrientation = GetTagValue(tags, "roof:orientation") ?? "along";

        // Parse materials
        string wallMaterial = BuildingMaterialLibrary.MapOsmWallMaterial(
            GetTagValue(tags, "building:material") ?? GetTagValue(tags, "building:facade:material"));
        if (wallMaterial == "BUILDING_DEFAULT" && defaults.WallMaterial != "BUILDING_DEFAULT")
            wallMaterial = defaults.WallMaterial;

        string roofMaterial = BuildingMaterialLibrary.MapOsmRoofMaterial(GetTagValue(tags, "roof:material"));
        if (roofMaterial == "ROOF_DEFAULT")
            roofMaterial = defaults.RoofMaterial;

        // Parse colours
        Color? wallColor = ParseOsmColor(GetTagValue(tags, "building:colour"));
        Color? roofColor = ParseOsmColor(GetTagValue(tags, "roof:colour"));

        // === OSM2World window heuristics (ExteriorBuildingWall.java lines 101-128) ===
        bool hasWindows = defaults.HasWindows;

        string? windowTag = GetTagValue(tags, "window");
        if (windowTag != null)
        {
            // Explicit window=* tag takes priority over all heuristics
            hasWindows = !windowTag.Equals("no", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Heuristic: no building:levels tag (or levels=0) → no windows
            // taggedLevels is null when tag is absent or parsed value <= 0
            if (taggedLevels == null)
                hasWindows = false;

            // Heuristic: glass material → no windows (entire facade is glass)
            if (wallMaterial == "GLASS_WALL")
                hasWindows = false;
        }

        return new BuildingData
        {
            OsmId = osmId,
            BuildingType = buildingType,
            FootprintOuter = localOuter,
            FootprintHoles = localHoles,
            Height = heightData.Height,
            MinHeight = heightData.MinHeight,
            Levels = heightData.Levels,
            HeightPerLevel = heightData.HeightPerLevel,
            RoofShape = defaults.RoofShape,
            RoofHeight = heightData.RoofHeight,
            RoofDirection = roofDirection,
            RoofAngle = roofAngle,
            RoofOrientation = roofOrientation,
            WallMaterial = wallMaterial,
            RoofMaterial = roofMaterial,
            WallColor = wallColor,
            RoofColor = roofColor,
            HasWindows = hasWindows,
            HasWalls = defaults.HasWalls,
        };
    }

    // ==========================================
    // Legacy API (flat list, no parts grouping)
    // ==========================================

    /// <summary>
    /// Parses all building polygon features from an OSM query result.
    /// Returns a flat list of BuildingData (no building:part grouping).
    /// </summary>
    public List<BuildingData> ParseBuildings(
        OsmQueryResult queryResult,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler = null)
    {
        var buildingFeatures = queryResult.GetFeaturesByCategory("building")
            .Where(f => f.GeometryType == OsmGeometryType.Polygon)
            .ToList();

        return ParseBuildingFeatures(buildingFeatures, bbox, terrainSize, metersPerPixel, heightSampler);
    }

    /// <summary>
    /// Parses a pre-filtered list of building polygon features.
    /// Use this overload when the user has selected specific building features via the UI.
    /// </summary>
    public List<BuildingData> ParseBuildingFeatures(
        List<OsmFeature> buildingFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler = null)
    {
        Console.WriteLine($"OsmBuildingParser: Found {buildingFeatures.Count} building polygon features");

        var buildings = new List<BuildingData>();
        int skippedTooSmall = 0;
        int skippedDegenerate = 0;

        foreach (var feature in buildingFeatures)
        {
            var building = ParseSingleBuilding(feature, bbox, terrainSize, metersPerPixel, heightSampler);
            if (building == null)
            {
                skippedDegenerate++;
                continue;
            }

            // Skip very small buildings (< 4 m² footprint area)
            float area = ComputePolygonArea(building.FootprintOuter);
            if (area < 4.0f)
            {
                skippedTooSmall++;
                continue;
            }

            buildings.Add(building);
        }

        Console.WriteLine($"OsmBuildingParser: Parsed {buildings.Count} buildings " +
            $"(skipped {skippedDegenerate} degenerate, {skippedTooSmall} too small)");

        return buildings;
    }

    /// <summary>
    /// Parses a single OSM building feature into a <see cref="BuildingData"/>.
    /// Returns null if the feature has degenerate geometry (< 3 unique vertices).
    /// </summary>
    private BuildingData? ParseSingleBuilding(
        OsmFeature feature,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        Func<float, float, float>? heightSampler)
    {
        // Transform outer ring: WGS84 → terrain pixels → meters
        var outerMeterCoords = TransformRingToMeters(feature.Coordinates, bbox, terrainSize, metersPerPixel);
        if (outerMeterCoords.Count < 3)
            return null;

        // Compute centroid in world meter-space
        var centroid = ComputeCentroid(outerMeterCoords);

        // Convert to local coordinates (origin at centroid)
        var localOuter = outerMeterCoords.Select(p => new Vector2(p.X - centroid.X, p.Y - centroid.Y)).ToList();

        // Ensure CCW winding for outer ring
        if (!IsCounterClockwise(localOuter))
            localOuter.Reverse();

        // Close the ring if not already closed
        localOuter = EnsureClosed(localOuter);

        // Process inner rings (holes/courtyards)
        var localHoles = TransformInnerRings(feature, centroid, bbox, terrainSize, metersPerPixel);

        // Sample ground elevation at the lowest footprint vertex.
        float groundElevation = 0f;
        if (heightSampler != null)
        {
            groundElevation = outerMeterCoords.Min(p => heightSampler(p.X, p.Y));
        }

        // Parse tags using shared method
        var tags = feature.Tags;
        string buildingType = GetTagValue(tags, "building") ?? "yes";
        var buildingData = ParseTagsIntoBuildingData(feature.Id, buildingType, localOuter, localHoles, tags, parentTags: null);

        // Set world position (not set by ParseTagsIntoBuildingData since it's part-agnostic)
        buildingData.GroundElevation = groundElevation;
        buildingData.WorldPosition = new Vector3(centroid.X, centroid.Y, groundElevation);

        return buildingData;
    }

    // ==========================================
    // Building passage detection
    // ==========================================

    /// <summary>
    /// Detects building passages by finding shared OSM node IDs between passage roads
    /// and building outlines. Creates PassageInfo entries on affected BuildingData parts.
    ///
    /// Port of OSM2World BuildingPart.java lines 152-233:
    /// - Finds roads tagged tunnel=building_passage that share nodes with building polygon
    /// - Each shared node → one wall opening (entry or exit point)
    /// - Passage width derived from road width tag or highway type defaults
    /// </summary>
    private void DetectBuildingPassages(
        List<BeamNG.Procedural3D.Building.Building> buildings,
        List<OsmFeature> buildingFeatures,
        List<OsmFeature> passageFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        // Build lookup: OSM node ID → list of (building feature ID, coordinate of that node)
        var nodeToBuilding = new Dictionary<long, List<(long FeatureId, GeoCoordinate Coord)>>();
        foreach (var feature in buildingFeatures)
        {
            if (feature.NodeIds.Count == 0) continue;
            int count = Math.Min(feature.NodeIds.Count, feature.Coordinates.Count);
            for (int i = 0; i < count; i++)
            {
                long nodeId = feature.NodeIds[i];
                if (!nodeToBuilding.TryGetValue(nodeId, out var list))
                {
                    list = new();
                    nodeToBuilding[nodeId] = list;
                }
                list.Add((feature.Id, feature.Coordinates[i]));
            }
        }

        // Build lookup: building OsmId → Building object
        // Use first-wins to handle any remaining duplicate OsmIds gracefully
        var buildingById = new Dictionary<long, BeamNG.Procedural3D.Building.Building>();
        foreach (var b in buildings)
            buildingById.TryAdd(b.OsmId, b);

        int passageOpenings = 0;

        foreach (var passage in passageFeatures)
        {
            if (passage.NodeIds.Count < 2) continue;

            float passageWidth = GetPassageWidth(passage.Tags);

            // Check all passage nodes for shared nodes with building outlines
            // (typically start and end nodes, which are the entry/exit points)
            foreach (long nodeId in passage.NodeIds)
            {
                if (!nodeToBuilding.TryGetValue(nodeId, out var matches)) continue;

                foreach (var (featureId, nodeCoord) in matches)
                {
                    if (!buildingById.TryGetValue(featureId, out var building)) continue;

                    // Transform shared node coordinate to building local space
                    var meterCoords = TransformRingToMeters(
                        new List<GeoCoordinate> { nodeCoord }, bbox, terrainSize, metersPerPixel);
                    if (meterCoords.Count == 0) continue;

                    var localPos = new Vector2(
                        meterCoords[0].X - building.WorldPosition.X,
                        meterCoords[0].Y - building.WorldPosition.Y);

                    var passageInfo = new PassageInfo(localPos, passageWidth);

                    // Add to all parts — wall generation filters by position automatically
                    foreach (var part in building.Parts)
                    {
                        part.Passages.Add(passageInfo);
                    }

                    passageOpenings++;
                }
            }
        }

        if (passageOpenings > 0)
        {
            Console.WriteLine($"OsmBuildingParser: Detected {passageOpenings} building passage openings " +
                              $"from {passageFeatures.Count} passage roads");
        }
    }

    /// <summary>
    /// Determines passage width from road tags.
    /// Checks explicit width=* tag first, then uses defaults based on highway type.
    /// </summary>
    private static float GetPassageWidth(Dictionary<string, string> tags)
    {
        // Explicit width tag takes priority
        if (tags.TryGetValue("width", out var widthStr) &&
            float.TryParse(widthStr, System.Globalization.CultureInfo.InvariantCulture, out float width) &&
            width > 0)
        {
            return width;
        }

        // Default widths based on highway type
        string? highway = tags.TryGetValue("highway", out var hw) ? hw : null;
        return highway?.ToLowerInvariant() switch
        {
            "motorway" or "trunk" => 8.0f,
            "primary" => 7.0f,
            "secondary" => 6.5f,
            "tertiary" => 6.0f,
            "residential" or "living_street" or "unclassified" => 5.5f,
            "service" => 3.5f,
            "footway" or "path" or "pedestrian" or "cycleway" or "steps" => 2.5f,
            _ => 4.0f
        };
    }

    // ==========================================
    // Helpers: building:part detection
    // ==========================================

    /// <summary>
    /// Returns true if the feature has a building:part=* tag (excluding "no").
    /// </summary>
    private static bool IsBuildingPart(OsmFeature feature)
    {
        return feature.Tags.TryGetValue("building:part", out var val)
               && !val.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the feature has building:part=* but NOT building=* (i.e., a pure part, not an outline).
    /// </summary>
    private static bool IsBuildingPartOnly(OsmFeature feature)
    {
        return IsBuildingPart(feature) && !feature.Tags.ContainsKey("building");
    }

    /// <summary>
    /// Pre-parsed building:part candidate for efficient matching.
    /// </summary>
    private record PartCandidate(OsmFeature Feature, List<Vector2> WorldMeterCoords, Vector2 WorldCentroid);

    // ==========================================
    // Helpers: tag inheritance
    // ==========================================

    /// <summary>
    /// Gets a tag value, first trying the feature's own tags, then inherited parent tags.
    /// </summary>
    private static string? GetTagValueWithInheritance(
        Dictionary<string, string> tags,
        Dictionary<string, string>? parentTags,
        string key)
    {
        return GetTagValue(tags, key) ?? GetInheritedTag(parentTags, key);
    }

    /// <summary>
    /// Gets a tag value from inherited parent tags, returning null if not present.
    /// </summary>
    private static string? GetInheritedTag(Dictionary<string, string>? parentTags, string key)
    {
        if (parentTags == null) return null;
        return GetTagValue(parentTags, key);
    }

    // ==========================================
    // Helpers: geometry (polygon metrics)
    // ==========================================

    /// <summary>
    /// Computes the diameter of a polygon (max distance between any two vertices).
    /// Port of OSM2World's PolygonShapeXZ.getDiameter().
    /// Used for dome roof height calculation: roofHeight = diameter / 2.
    /// </summary>
    private static float ComputePolygonDiameter(List<Vector2> polygon)
    {
        float maxDistSq = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            for (int j = i + 1; j < polygon.Count; j++)
            {
                float distSq = Vector2.DistanceSquared(polygon[i], polygon[j]);
                if (distSq > maxDistSq)
                    maxDistSq = distSq;
            }
        }
        return MathF.Sqrt(maxDistSq);
    }

    // ==========================================
    // Helpers: geometry
    // ==========================================

    /// <summary>
    /// Transforms inner rings of a feature to local coordinates relative to a centroid.
    /// </summary>
    private List<List<Vector2>>? TransformInnerRings(
        OsmFeature feature,
        Vector2 centroid,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        if (feature.InnerRings == null || feature.InnerRings.Count == 0)
            return null;

        var localHoles = new List<List<Vector2>>();
        foreach (var innerRing in feature.InnerRings)
        {
            var innerMeterCoords = TransformRingToMeters(innerRing, bbox, terrainSize, metersPerPixel);
            if (innerMeterCoords.Count < 3) continue;

            var localInner = innerMeterCoords
                .Select(p => new Vector2(p.X - centroid.X, p.Y - centroid.Y))
                .ToList();

            if (IsCounterClockwise(localInner))
                localInner.Reverse();

            localInner = EnsureClosed(localInner);
            localHoles.Add(localInner);
        }

        return localHoles.Count > 0 ? localHoles : null;
    }

    /// <summary>
    /// Point-in-polygon test using ray casting algorithm.
    /// Works with closed polygon rings (first == last vertex).
    /// </summary>
    private static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int n = polygon.Count;
        // Skip closing vertex if ring is closed
        int vertexCount = n;
        if (n > 1 && Vector2.DistanceSquared(polygon[0], polygon[^1]) < 0.0001f)
            vertexCount = n - 1;

        for (int i = 0, j = vertexCount - 1; i < vertexCount; j = i++)
        {
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y) &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y)
                          / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Transforms a ring of geographic coordinates to meter coordinates in terrain space.
    /// Removes duplicate consecutive points.
    /// </summary>
    private List<Vector2> TransformRingToMeters(
        List<GeoCoordinate> geoCoords,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel)
    {
        // Transform to terrain-space pixels (bottom-left origin, Y increases northward)
        var pixelCoords = _geometryProcessor.TransformToTerrainCoordinates(geoCoords, bbox, terrainSize);

        // Convert pixels to meters
        var meterCoords = pixelCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();

        // Remove duplicate consecutive points (tolerance 0.01m)
        return RemoveDuplicateConsecutive(meterCoords, 0.01f);
    }

    /// <summary>
    /// Computes the centroid (average position) of a polygon.
    /// </summary>
    private static Vector2 ComputeCentroid(List<Vector2> polygon)
    {
        if (polygon.Count == 0)
            return Vector2.Zero;

        float sumX = 0, sumY = 0;
        // Exclude last point if ring is closed (first == last)
        int count = polygon.Count;
        if (count > 1 && Vector2.DistanceSquared(polygon[0], polygon[^1]) < 0.0001f)
            count--;

        for (int i = 0; i < count; i++)
        {
            sumX += polygon[i].X;
            sumY += polygon[i].Y;
        }

        return new Vector2(sumX / count, sumY / count);
    }

    /// <summary>
    /// Computes the signed area of a polygon (positive = CCW, negative = CW).
    /// Uses the shoelace formula.
    /// </summary>
    private static float ComputeSignedArea(List<Vector2> polygon)
    {
        float area = 0;
        int n = polygon.Count;
        for (int i = 0; i < n; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % n];
            area += (p2.X - p1.X) * (p2.Y + p1.Y);
        }
        return area / 2f;
    }

    /// <summary>
    /// Computes the absolute area of a polygon.
    /// </summary>
    private static float ComputePolygonArea(List<Vector2> polygon)
    {
        return MathF.Abs(ComputeSignedArea(polygon));
    }

    /// <summary>
    /// Checks if a polygon has counter-clockwise winding order.
    /// </summary>
    private static bool IsCounterClockwise(List<Vector2> polygon)
    {
        // In a standard math coordinate system (Y up), negative signed area = CCW
        // The shoelace formula as implemented uses (p2.X - p1.X) * (p2.Y + p1.Y)
        // which gives negative area for CCW polygons
        return ComputeSignedArea(polygon) < 0;
    }

    /// <summary>
    /// Ensures a polygon ring is closed (first point == last point).
    /// </summary>
    private static List<Vector2> EnsureClosed(List<Vector2> ring)
    {
        if (ring.Count < 2)
            return ring;

        if (Vector2.DistanceSquared(ring[0], ring[^1]) > 0.0001f)
        {
            ring = new List<Vector2>(ring) { ring[0] };
        }

        return ring;
    }

    /// <summary>
    /// Removes consecutive duplicate points within a tolerance.
    /// </summary>
    private static List<Vector2> RemoveDuplicateConsecutive(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;

        var result = new List<Vector2> { points[0] };
        float toleranceSq = tolerance * tolerance;

        for (int i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(result[^1], points[i]) > toleranceSq)
                result.Add(points[i]);
        }

        return result;
    }

    /// <summary>
    /// Gets a tag value, returning null if not present or empty.
    /// </summary>
    private static string? GetTagValue(Dictionary<string, string> tags, string key)
    {
        if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return null;
    }

    /// <summary>
    /// Attempts to parse a meters value from an OSM tag.
    /// Supports formats: "10", "10.5", "10 m", "10m", "33 ft", "33ft", "33'".
    /// </summary>
    private static bool TryParseMeters(string? value, out float meters)
    {
        meters = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        // Check for feet suffix
        bool isFeet = false;
        if (value.EndsWith("ft", StringComparison.OrdinalIgnoreCase))
        {
            isFeet = true;
            value = value[..^2].Trim();
        }
        else if (value.EndsWith("'"))
        {
            isFeet = true;
            value = value[..^1].Trim();
        }
        else if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^1].Trim();
        }

        if (float.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
        {
            meters = isFeet ? parsed * 0.3048f : parsed;
            return meters > 0;
        }

        return false;
    }

    /// <summary>
    /// Parses an OSM color tag value to a <see cref="Color"/>.
    /// Supports named colors (red, brown, grey, etc.) and hex (#RRGGBB, #RGB).
    /// </summary>
    private static Color? ParseOsmColor(string? colorTag)
    {
        if (string.IsNullOrWhiteSpace(colorTag))
            return null;

        colorTag = colorTag.Trim().ToLowerInvariant();

        // Named colors commonly used in OSM
        var color = colorTag switch
        {
            "white" => Color.FromArgb(255, 255, 255),
            "black" => Color.FromArgb(30, 30, 30),
            "grey" or "gray" => Color.FromArgb(160, 160, 160),
            "red" => Color.FromArgb(180, 60, 50),
            "brown" => Color.FromArgb(140, 90, 50),
            "yellow" => Color.FromArgb(220, 200, 80),
            "green" => Color.FromArgb(80, 140, 60),
            "blue" => Color.FromArgb(60, 100, 180),
            "orange" => Color.FromArgb(220, 140, 40),
            "beige" => Color.FromArgb(220, 210, 180),
            "cream" => Color.FromArgb(240, 230, 200),
            "tan" => Color.FromArgb(200, 180, 140),
            "pink" => Color.FromArgb(220, 170, 170),
            "maroon" => Color.FromArgb(120, 40, 40),
            _ => (Color?)null
        };

        if (color.HasValue)
            return color.Value;

        // Try hex color
        if (colorTag.StartsWith('#'))
        {
            var hex = colorTag[1..];
            if (hex.Length == 3)
            {
                // #RGB → #RRGGBB
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            }

            if (hex.Length == 6 &&
                int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out int r) &&
                int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g) &&
                int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
            {
                return Color.FromArgb(r, g, b);
            }
        }

        return null;
    }
}
