using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Converts cross-sections from the terrain pipeline format to the 3D mesh generation format.
/// Supports conversion to both terrain coordinates and BeamNG world coordinates.
/// </summary>
public static class CrossSectionConverter
{
    /// <summary>
    /// Converts a single UnifiedCrossSection to a RoadCrossSection for mesh generation.
    /// Coordinates remain in terrain space (origin at bottom-left corner).
    /// </summary>
    /// <param name="source">The source cross-section from the smoothing pipeline.</param>
    /// <returns>A RoadCrossSection suitable for mesh generation in terrain coordinates.</returns>
    public static RoadCrossSection Convert(UnifiedCrossSection source)
    {
        return new RoadCrossSection
        {
            CenterPoint = source.CenterPoint,
            CenterElevation = source.TargetElevation,
            TangentDirection = source.TangentDirection,
            NormalDirection = source.NormalDirection,
            WidthMeters = source.EffectiveRoadWidth,
            BankAngleRadians = source.BankAngleRadians,
            DistanceAlongRoad = source.DistanceAlongSpline,
            // Use constrained elevations if available, otherwise use calculated values
            LeftEdgeElevation = source.ConstrainedLeftEdgeElevation ?? 
                               (float.IsNaN(source.LeftEdgeElevation) ? null : source.LeftEdgeElevation),
            RightEdgeElevation = source.ConstrainedRightEdgeElevation ?? 
                                (float.IsNaN(source.RightEdgeElevation) ? null : source.RightEdgeElevation)
        };
    }


    /// <summary>
    /// Converts a single UnifiedCrossSection to a RoadCrossSection with BeamNG world coordinates.
    /// The center point is transformed from terrain space (origin at bottom-left) to world space (origin at center).
    /// The terrain base height is added to all elevation values so the mesh aligns with the terrain's absolute height.
    /// This allows the exported DAE mesh to be placed at BeamNG world position (0,0,0) and align with the terrain.
    /// </summary>
    /// <param name="source">The source cross-section from the smoothing pipeline.</param>
    /// <param name="terrainSizePixels">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel).</param>
    /// <param name="terrainBaseHeight">Base height offset for the terrain (added to all Z coordinates).</param>
    /// <returns>A RoadCrossSection with world coordinates suitable for DAE export.</returns>
    public static RoadCrossSection ConvertToWorldCoordinates(
        UnifiedCrossSection source,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight)
    {
        // Transform center point from terrain space to world space
        var worldCenter = BeamNgCoordinateTransformer.TerrainToWorld2D(
            source.CenterPoint, terrainSizePixels, metersPerPixel);

        // Add terrain base height to all elevation values
        // This ensures the DAE mesh sits at the correct absolute height when placed at (0,0,0)
        var centerElevation = source.TargetElevation + terrainBaseHeight;
        
        // Calculate edge elevations with base height offset
        float? leftEdgeElevation = null;
        if (source.ConstrainedLeftEdgeElevation.HasValue)
            leftEdgeElevation = source.ConstrainedLeftEdgeElevation.Value + terrainBaseHeight;
        else if (!float.IsNaN(source.LeftEdgeElevation))
            leftEdgeElevation = source.LeftEdgeElevation + terrainBaseHeight;
        
        float? rightEdgeElevation = null;
        if (source.ConstrainedRightEdgeElevation.HasValue)
            rightEdgeElevation = source.ConstrainedRightEdgeElevation.Value + terrainBaseHeight;
        else if (!float.IsNaN(source.RightEdgeElevation))
            rightEdgeElevation = source.RightEdgeElevation + terrainBaseHeight;

        return new RoadCrossSection
        {
            CenterPoint = worldCenter,
            CenterElevation = centerElevation,
            // Tangent and normal directions are unit vectors - they don't need transformation
            TangentDirection = source.TangentDirection,
            NormalDirection = source.NormalDirection,
            WidthMeters = source.EffectiveRoadWidth,
            BankAngleRadians = source.BankAngleRadians,
            DistanceAlongRoad = source.DistanceAlongSpline,
            LeftEdgeElevation = leftEdgeElevation,
            RightEdgeElevation = rightEdgeElevation
        };
    }


    /// <summary>
    /// Converts a collection of UnifiedCrossSections belonging to a single spline path.
    /// Filters out invalid cross-sections (NaN elevation, excluded, etc.).
    /// Coordinates remain in terrain space.
    /// </summary>
    /// <param name="crossSections">The cross-sections to convert (should be from a single spline).</param>
    /// <returns>A list of valid RoadCrossSections ordered by distance along the spline.</returns>
    public static List<RoadCrossSection> ConvertPath(IEnumerable<UnifiedCrossSection> crossSections)
    {
        var result = new List<RoadCrossSection>();

        foreach (var cs in crossSections.OrderBy(c => c.LocalIndex))
        {
            // Skip invalid cross-sections
            if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation < -1000)
                continue;

            if (cs.IsExcluded)
                continue;

            // Skip cross-sections with invalid positions
            if (float.IsNaN(cs.CenterPoint.X) || float.IsNaN(cs.CenterPoint.Y))
                continue;

            result.Add(Convert(cs));
        }

        return result;
    }

    /// <summary>
    /// Converts a collection of UnifiedCrossSections to world coordinates.
    /// Filters out invalid cross-sections (NaN elevation, excluded, etc.).
    /// The center points are transformed from terrain space to BeamNG world space.
    /// The terrain base height is added to all elevation values.
    /// </summary>
    /// <param name="crossSections">The cross-sections to convert (should be from a single spline).</param>
    /// <param name="terrainSizePixels">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel).</param>
    /// <param name="terrainBaseHeight">Base height offset for the terrain (added to all Z coordinates).</param>
    /// <returns>A list of valid RoadCrossSections in world coordinates, ordered by distance along the spline.</returns>
    public static List<RoadCrossSection> ConvertPathToWorldCoordinates(
        IEnumerable<UnifiedCrossSection> crossSections,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight)
    {
        var result = new List<RoadCrossSection>();

        foreach (var cs in crossSections.OrderBy(c => c.LocalIndex))
        {
            // Skip invalid cross-sections
            if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation < -1000)
                continue;

            if (cs.IsExcluded)
                continue;

            // Skip cross-sections with invalid positions
            if (float.IsNaN(cs.CenterPoint.X) || float.IsNaN(cs.CenterPoint.Y))
                continue;

            result.Add(ConvertToWorldCoordinates(cs, terrainSizePixels, metersPerPixel, terrainBaseHeight));
        }

        return result;
    }

    /// <summary>
    /// Converts all cross-sections from a unified road network, grouped by spline.
    /// Coordinates remain in terrain space.
    /// </summary>
    /// <param name="network">The unified road network containing all splines and cross-sections.</param>
    /// <returns>
    /// A dictionary mapping spline IDs to their converted cross-section lists.
    /// Each list is ordered by distance along the spline.
    /// </returns>
    public static Dictionary<int, List<RoadCrossSection>> ConvertNetwork(UnifiedRoadNetwork network)
    {
        var result = new Dictionary<int, List<RoadCrossSection>>();

        // Group cross-sections by their owning spline
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId);

        foreach (var splineGroup in crossSectionsBySpline)
        {
            var splineId = splineGroup.Key;
            var convertedPath = ConvertPath(splineGroup);

            // Only include paths with at least 2 cross-sections (needed to form triangles)
            if (convertedPath.Count >= 2)
            {
                result[splineId] = convertedPath;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts all cross-sections from a unified road network to world coordinates, grouped by spline.
    /// This is the primary method for DAE export - produces coordinates that align with BeamNG when
    /// the DAE mesh is placed at world position (0,0,0).
    /// The terrain base height is added to all elevation values.
    /// </summary>
    /// <param name="network">The unified road network containing all splines and cross-sections.</param>
    /// <param name="terrainSizePixels">Terrain size in pixels.</param>
    /// <param name="metersPerPixel">Scale factor (meters per pixel).</param>
    /// <param name="terrainBaseHeight">Base height offset for the terrain (added to all Z coordinates).</param>
    /// <returns>
    /// A dictionary mapping spline IDs to their converted cross-section lists in world coordinates.
    /// Each list is ordered by distance along the spline.
    /// </returns>
    public static Dictionary<int, List<RoadCrossSection>> ConvertNetworkToWorldCoordinates(
        UnifiedRoadNetwork network,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight)
    {
        var result = new Dictionary<int, List<RoadCrossSection>>();

        // Group cross-sections by their owning spline
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId);

        foreach (var splineGroup in crossSectionsBySpline)
        {
            var splineId = splineGroup.Key;
            var convertedPath = ConvertPathToWorldCoordinates(
                splineGroup, terrainSizePixels, metersPerPixel, terrainBaseHeight);

            // Only include paths with at least 2 cross-sections (needed to form triangles)
            if (convertedPath.Count >= 2)
            {
                result[splineId] = convertedPath;
            }
        }

        return result;
    }
}
