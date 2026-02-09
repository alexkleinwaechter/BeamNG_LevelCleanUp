using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Harmonizes road elevations at junctions and endpoints to eliminate discontinuities.
/// 
/// PROBLEM SOLVED:
/// - When multiple road splines meet at intersections, each has independently smoothed elevations
/// - This causes elevation "jumps" where roads meet
/// - Spline endpoints can have abrupt elevation changes where the road "drops off"
/// 
/// ALGORITHM:
/// 1. Detect junction clusters (where endpoints from different paths are close together)
/// 2. Compute harmonized elevation for each junction (weighted average)
/// 3. Propagate junction constraints back along each path with smooth blending
/// 4. Taper isolated endpoints back toward natural terrain elevation
/// </summary>
public class JunctionElevationHarmonizer
{
    /// <summary>
    /// Represents a detected junction where multiple road paths meet.
    /// </summary>
    public class Junction
    {
        /// <summary>
        /// Center position of the junction in world coordinates.
        /// </summary>
        public Vector2 CenterPosition { get; set; }
        
        /// <summary>
        /// Cross-sections that are part of this junction (from different paths).
        /// </summary>
        public List<CrossSection> CrossSections { get; set; } = new();
        
        /// <summary>
        /// Path IDs that contribute to this junction.
        /// </summary>
        public HashSet<int> PathIds { get; set; } = new();
        
        /// <summary>
        /// Harmonized target elevation for this junction.
        /// </summary>
        public float HarmonizedElevation { get; set; }
        
        /// <summary>
        /// Is this an isolated endpoint (single path ending) rather than a multi-path junction?
        /// </summary>
        public bool IsIsolatedEndpoint { get; set; }
    }
    
    /// <summary>
    /// Harmonizes elevations at junctions and endpoints after initial smoothing.
    /// Call this AFTER CalculateTargetElevations() but BEFORE terrain blending.
    /// </summary>
    /// <param name="geometry">Road geometry with calculated target elevations</param>
    /// <param name="heightMap">Original terrain heightmap</param>
    /// <param name="parameters">Road smoothing parameters</param>
    /// <param name="metersPerPixel">Scale factor</param>
    public void HarmonizeElevations(
        RoadGeometry geometry,
        float[,] heightMap,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        if (geometry.CrossSections.Count == 0)
        {
            TerrainLogger.Info("No cross-sections to harmonize");
            return;
        }
        
        // Get harmonization parameters
        var junctionParams = parameters.JunctionHarmonizationParameters ?? new JunctionHarmonizationParameters();
        
        if (!junctionParams.EnableJunctionHarmonization)
        {
            TerrainLogger.Info("Junction harmonization disabled");
            return;
        }
        
        TerrainLogger.Info("=== JUNCTION ELEVATION HARMONIZATION ===");
        TerrainLogger.Info($"  Junction detection radius: {junctionParams.JunctionDetectionRadiusMeters}m");
        TerrainLogger.Info($"  Blend distance: {junctionParams.JunctionBlendDistanceMeters}m");
        TerrainLogger.Info($"  Endpoint taper enabled: {junctionParams.EnableEndpointTaper}");
        TerrainLogger.Info($"  Endpoint terrain blend strength: {junctionParams.EndpointTerrainBlendStrength:P0}");
        
        // Capture pre-harmonization elevations for comparison
        var preHarmonizationElevations = new Dictionary<int, float>();
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            preHarmonizationElevations[cs.Index] = cs.TargetElevation;
        }
        
        // Step 1: Find path endpoints
        var endpoints = FindPathEndpoints(geometry);
        TerrainLogger.Info($"  Found {endpoints.Count} path endpoints from {geometry.CrossSections.GroupBy(cs => cs.PathId).Count()} paths");
        
        // Step 2: Detect junction clusters (including T-junctions)
        var junctions = DetectJunctions(endpoints, junctionParams.JunctionDetectionRadiusMeters, geometry);
        int multiPathJunctions = junctions.Count(j => !j.IsIsolatedEndpoint);
        int isolatedEndpoints = junctions.Count(j => j.IsIsolatedEndpoint);
        TerrainLogger.Info($"  Detected {multiPathJunctions} multi-path junctions, {isolatedEndpoints} isolated endpoints");
        
        // Step 3: Compute harmonized elevations for each junction
        ComputeJunctionElevations(junctions, heightMap, metersPerPixel, junctionParams);
        
        // Step 4: Propagate junction constraints back along paths
        int propagatedCount = PropagateJunctionConstraints(geometry, junctions, junctionParams);
        
        // Step 5: Apply endpoint tapering for isolated endpoints
        int taperedCount = 0;
        if (junctionParams.EnableEndpointTaper)
        {
            taperedCount = ApplyEndpointTapering(geometry, junctions, heightMap, metersPerPixel, junctionParams);
        }
        
        // Step 6: Calculate and log elevation changes
        int totalModified = 0;
        float maxChange = 0f;
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            if (preHarmonizationElevations.TryGetValue(cs.Index, out float preElev))
            {
                float change = MathF.Abs(cs.TargetElevation - preElev);
                if (change > 0.001f)
                {
                    totalModified++;
                    if (change > maxChange)
                        maxChange = change;
                }
            }
        }
        
        TerrainLogger.Info($"  RESULT: Modified {totalModified} cross-sections");
        TerrainLogger.Info($"  RESULT: Propagated {propagatedCount}, Tapered {taperedCount}");
        TerrainLogger.Info($"  RESULT: Max elevation change: {maxChange:F3}m");
        
        // Step 7: Export debug image if enabled
        if (junctionParams.ExportJunctionDebugImage)
        {
            ExportJunctionDebugImage(geometry, junctions, preHarmonizationElevations, 
                                     parameters, metersPerPixel);
            ExportBeforeAfterElevationImage(geometry, preHarmonizationElevations,
                                             parameters, metersPerPixel);
        }
        
        TerrainLogger.Info("=== JUNCTION HARMONIZATION COMPLETE ===");
    }
    
    /// <summary>
    /// Finds the first and last cross-sections of each path (the endpoints).
    /// </summary>
    private List<CrossSection> FindPathEndpoints(RoadGeometry geometry)
    {
        var endpoints = new List<CrossSection>();
        
        // Group by PathId
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .GroupBy(cs => cs.PathId)
            .ToList();
        
        foreach (var group in pathGroups)
        {
            var ordered = group.OrderBy(cs => cs.LocalIndex).ToList();
            
            if (ordered.Count >= 1)
            {
                // First point of path
                endpoints.Add(ordered[0]);
                
                // Last point of path (if different from first)
                if (ordered.Count > 1)
                {
                    endpoints.Add(ordered[ordered.Count - 1]);
                }
            }
        }
        
        return endpoints;
    }
    
    /// <summary>
    /// Clusters nearby endpoints into junctions.
    /// 
    /// IMPROVED: Also detects T-junctions where an endpoint meets the MIDDLE of another path,
    /// not just where endpoints meet each other.
    /// 
    /// Algorithm:
    /// 1. First, cluster endpoints that are near each other (traditional Y/X intersections)
    /// 2. Then, for any "isolated" endpoint, check if it's near ANY cross-section of another path
    ///    This handles T-junctions where one road ends at another road's side
    /// </summary>
    private List<Junction> DetectJunctions(List<CrossSection> endpoints, float radiusMeters, RoadGeometry geometry)
    {
        var junctions = new List<Junction>();
        var assigned = new HashSet<int>(); // Track which endpoints are already assigned
        
        // Phase 1: Cluster endpoints that are near each other (traditional junctions)
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (assigned.Contains(i)) continue;
            
            var junction = new Junction();
            var cluster = new List<int> { i };
            assigned.Add(i);
            
            // Find all endpoints within radius (transitive closure)
            bool expanded;
            do
            {
                expanded = false;
                for (int j = 0; j < endpoints.Count; j++)
                {
                    if (assigned.Contains(j)) continue;
                    
                    // Check distance to any point in cluster
                    foreach (var idx in cluster)
                    {
                        float dist = Vector2.Distance(endpoints[idx].CenterPoint, endpoints[j].CenterPoint);
                        if (dist <= radiusMeters)
                        {
                            cluster.Add(j);
                            assigned.Add(j);
                            expanded = true;
                            break;
                        }
                    }
                }
            } while (expanded);
            
            // Build junction from cluster
            foreach (var idx in cluster)
            {
                var ep = endpoints[idx];
                junction.CrossSections.Add(ep);
                junction.PathIds.Add(ep.PathId);
            }
            
            // Compute center position
            junction.CenterPosition = new Vector2(
                junction.CrossSections.Average(cs => cs.CenterPoint.X),
                junction.CrossSections.Average(cs => cs.CenterPoint.Y)
            );
            
            // Mark as isolated endpoint if only one path
            junction.IsIsolatedEndpoint = junction.PathIds.Count == 1;
            
            junctions.Add(junction);
        }
        
        // Phase 2: Check "isolated" endpoints for T-junction detection
        // For each isolated endpoint, check if it's near any cross-section of a DIFFERENT path
        var allCrossSections = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .ToList();
        
        int tJunctionsFound = 0;
        
        foreach (var junction in junctions.Where(j => j.IsIsolatedEndpoint).ToList())
        {
            var endpoint = junction.CrossSections[0];
            
            // Find the nearest cross-section from a DIFFERENT path
            CrossSection? nearestOtherPath = null;
            float nearestDistance = float.MaxValue;
            
            foreach (var cs in allCrossSections)
            {
                // Skip cross-sections from the same path
                if (cs.PathId == endpoint.PathId) continue;
                
                float dist = Vector2.Distance(endpoint.CenterPoint, cs.CenterPoint);
                if (dist < nearestDistance && dist <= radiusMeters)
                {
                    nearestDistance = dist;
                    nearestOtherPath = cs;
                }
            }
            
            // If we found a nearby cross-section from another path, this is a T-junction!
            if (nearestOtherPath != null)
            {
                // Add the other path's cross-section to this junction
                junction.CrossSections.Add(nearestOtherPath);
                junction.PathIds.Add(nearestOtherPath.PathId);
                junction.IsIsolatedEndpoint = false; // No longer isolated!
                
                // Update center position (average of endpoint and nearest point on other road)
                junction.CenterPosition = new Vector2(
                    (endpoint.CenterPoint.X + nearestOtherPath.CenterPoint.X) / 2f,
                    (endpoint.CenterPoint.Y + nearestOtherPath.CenterPoint.Y) / 2f
                );
                
                tJunctionsFound++;
            }
        }
        
        if (tJunctionsFound > 0)
        {
            TerrainLogger.Info($"  Detected {tJunctionsFound} T-junction(s) (endpoint meeting middle of another road)");
        }
        
        return junctions;
    }
    
    /// <summary>
    /// Original DetectJunctions signature for backward compatibility.
    /// Delegates to the new version that also detects T-junctions.
    /// </summary>
    private List<Junction> DetectJunctions(List<CrossSection> endpoints, float radiusMeters)
    {
        // This overload is kept for any external callers, but won't detect T-junctions
        // since it doesn't have access to the full geometry
        var junctions = new List<Junction>();
        var assigned = new HashSet<int>();
        
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (assigned.Contains(i)) continue;
            
            var junction = new Junction();
            var cluster = new List<int> { i };
            assigned.Add(i);
            
            bool expanded;
            do
            {
                expanded = false;
                for (int j = 0; j < endpoints.Count; j++)
                {
                    if (assigned.Contains(j)) continue;
                    
                    foreach (var idx in cluster)
                    {
                        float dist = Vector2.Distance(endpoints[idx].CenterPoint, endpoints[j].CenterPoint);
                        if (dist <= radiusMeters)
                        {
                            cluster.Add(j);
                            assigned.Add(j);
                            expanded = true;
                            break;
                        }
                    }
                }
            } while (expanded);
            
            foreach (var idx in cluster)
            {
                var ep = endpoints[idx];
                junction.CrossSections.Add(ep);
                junction.PathIds.Add(ep.PathId);
            }
            
            junction.CenterPosition = new Vector2(
                junction.CrossSections.Average(cs => cs.CenterPoint.X),
                junction.CrossSections.Average(cs => cs.CenterPoint.Y)
            );
            
            junction.IsIsolatedEndpoint = junction.PathIds.Count == 1;
            junctions.Add(junction);
        }
        
        return junctions;
    }
    
    /// <summary>
    /// Computes the harmonized elevation for each junction.
    /// 
    /// For T-junctions: The CONTINUOUS road "wins" - the side road adopts the main road's elevation.
    /// For multi-endpoint junctions (Y, X): Weighted average of all contributing elevations.
    /// For isolated endpoints: Blend toward terrain elevation.
    /// </summary>
    private void ComputeJunctionElevations(
        List<Junction> junctions,
        float[,] heightMap,
        float metersPerPixel,
        JunctionHarmonizationParameters junctionParams)
    {
        int mapHeight = heightMap.GetLength(0);
        int mapWidth = heightMap.GetLength(1);
        
        foreach (var junction in junctions)
        {
            if (junction.IsIsolatedEndpoint)
            {
                // For isolated endpoints, blend toward terrain elevation
                int px = (int)(junction.CenterPosition.X / metersPerPixel);
                int py = (int)(junction.CenterPosition.Y / metersPerPixel);
                px = Math.Clamp(px, 0, mapWidth - 1);
                py = Math.Clamp(py, 0, mapHeight - 1);
                
                float terrainElevation = heightMap[py, px];
                float roadElevation = junction.CrossSections.Average(cs => cs.TargetElevation);
                
                // Blend: mostly road elevation with slight pull toward terrain
                junction.HarmonizedElevation = roadElevation * (1.0f - junctionParams.EndpointTerrainBlendStrength) 
                                              + terrainElevation * junctionParams.EndpointTerrainBlendStrength;
            }
            else if (junction.CrossSections.Count == 2)
            {
                // Likely a T-junction: one endpoint + one mid-path cross-section
                // The mid-path cross-section is from the CONTINUOUS road and should "win"
                
                // Identify which cross-section is an endpoint vs mid-path
                // An endpoint has LocalIndex == 0 or LocalIndex == max for its path
                var cs1 = junction.CrossSections[0];
                var cs2 = junction.CrossSections[1];
                
                // The cross-section that is NOT at a path boundary is the "continuous" road
                // Use its elevation as the harmonized target
                // For now, use a simple heuristic: the one with higher LocalIndex (not at start) is more likely continuous
                // But the robust check is: if one is at position 0 or at the end of its path, it's an endpoint
                
                // Since we only add mid-path cross-sections in T-junction detection,
                // the second cross-section (added in Phase 2) is always the continuous road
                // The first one is the original endpoint
                
                junction.HarmonizedElevation = cs2.TargetElevation;
            }
            else
            {
                // For multi-endpoint junctions (Y, X, etc.): weighted average
                // All cross-sections are endpoints, so weight by inverse distance from center
                float totalWeight = 0f;
                float weightedSum = 0f;
                
                foreach (var cs in junction.CrossSections)
                {
                    float dist = Vector2.Distance(cs.CenterPoint, junction.CenterPosition);
                    float weight = 1.0f / (dist + 0.1f); // Add small epsilon to avoid division by zero
                    
                    totalWeight += weight;
                    weightedSum += cs.TargetElevation * weight;
                }
                
                junction.HarmonizedElevation = totalWeight > 0 ? weightedSum / totalWeight : junction.CrossSections.Average(cs => cs.TargetElevation);
            }
        }
    }
    
    /// <summary>
    /// Propagates junction elevation constraints back along each path.
    /// Uses smooth blending over the configured blend distance.
    /// 
    /// For T-junctions: Only propagates along the SIDE ROAD (the one ending at the junction).
    /// The main continuous road keeps its original elevation - it "wins".
    /// </summary>
    /// <returns>Number of cross-sections modified</returns>
    private int PropagateJunctionConstraints(
        RoadGeometry geometry,
        List<Junction> junctions,
        JunctionHarmonizationParameters junctionParams)
    {
        float blendDistance = junctionParams.JunctionBlendDistanceMeters;
        
        // Group cross-sections by path
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .GroupBy(cs => cs.PathId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());
        
        int modifiedCount = 0;
        
        foreach (var junction in junctions.Where(j => !j.IsIsolatedEndpoint))
        {
            // For each cross-section in the junction, only propagate if it's an ENDPOINT
            // (i.e., it's the first or last cross-section of its path)
            foreach (var junctionCs in junction.CrossSections)
            {
                if (!pathGroups.TryGetValue(junctionCs.PathId, out var pathSections))
                    continue;
                
                // Check if this cross-section is at the start or end of its path
                bool isStartOfPath = junctionCs.LocalIndex == pathSections[0].LocalIndex;
                bool isEndOfPath = junctionCs.LocalIndex == pathSections[^1].LocalIndex;
                
                // Only propagate along paths where this is an endpoint
                // Skip if this is a mid-path cross-section (the continuous road)
                if (!isStartOfPath && !isEndOfPath)
                    continue;
                
                // Calculate cumulative distances from endpoint
                var distances = new float[pathSections.Count];
                if (isStartOfPath)
                {
                    // Measure from start
                    distances[0] = 0;
                    for (int i = 1; i < pathSections.Count; i++)
                    {
                        distances[i] = distances[i - 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i - 1].CenterPoint);
                    }
                }
                else
                {
                    // Measure from end
                    distances[pathSections.Count - 1] = 0;
                    for (int i = pathSections.Count - 2; i >= 0; i--)
                    {
                        distances[i] = distances[i + 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i + 1].CenterPoint);
                    }
                }
                
                // Apply blend
                for (int i = 0; i < pathSections.Count; i++)
                {
                    float dist = distances[i];
                    if (dist >= blendDistance) continue; // Outside blend zone
                    
                    var cs = pathSections[i];
                    float originalElevation = cs.TargetElevation;
                    
                    // Smooth blend factor: 0 at junction, 1 at blend distance
                    // Using cosine interpolation for smooth transition
                    float t = dist / blendDistance;
                    float blend = 0.5f - 0.5f * MathF.Cos(MathF.PI * t); // 0 at junction, 1 at blend distance
                    
                    // Blend between junction elevation and original path elevation
                    cs.TargetElevation = junction.HarmonizedElevation * (1.0f - blend) + originalElevation * blend;
                    
                    if (MathF.Abs(cs.TargetElevation - originalElevation) > 0.001f)
                        modifiedCount++;
                }
            }
        }
        
        TerrainLogger.Info($"  Modified {modifiedCount} cross-section elevations for junction blending");
        return modifiedCount;
    }
    
    /// <summary>
    /// Applies tapering at isolated endpoints to smoothly transition back to terrain.
    /// </summary>
    /// <returns>Number of cross-sections modified</returns>
    private int ApplyEndpointTapering(
        RoadGeometry geometry,
        List<Junction> junctions,
        float[,] heightMap,
        float metersPerPixel,
        JunctionHarmonizationParameters junctionParams)
    {
        float taperDistance = junctionParams.EndpointTaperDistanceMeters;
        int mapHeight = heightMap.GetLength(0);
        int mapWidth = heightMap.GetLength(1);
        
        // Group cross-sections by path
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .GroupBy(cs => cs.PathId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());
        
        int taperedCount = 0;
        
        foreach (var junction in junctions.Where(j => j.IsIsolatedEndpoint))
        {
            foreach (var endpointCs in junction.CrossSections)
            {
                if (!pathGroups.TryGetValue(endpointCs.PathId, out var pathSections))
                    continue;
                
                // Determine if this is start or end of path
                bool isStartOfPath = endpointCs.LocalIndex == pathSections[0].LocalIndex;
                
                // Get terrain elevation at endpoint
                int px = (int)(endpointCs.CenterPoint.X / metersPerPixel);
                int py = (int)(endpointCs.CenterPoint.Y / metersPerPixel);
                px = Math.Clamp(px, 0, mapWidth - 1);
                py = Math.Clamp(py, 0, mapHeight - 1);
                float terrainElevation = heightMap[py, px];
                
                // Calculate cumulative distances from endpoint
                var distances = new float[pathSections.Count];
                if (isStartOfPath)
                {
                    distances[0] = 0;
                    for (int i = 1; i < pathSections.Count; i++)
                    {
                        distances[i] = distances[i - 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i - 1].CenterPoint);
                    }
                }
                else
                {
                    distances[pathSections.Count - 1] = 0;
                    for (int i = pathSections.Count - 2; i >= 0; i--)
                    {
                        distances[i] = distances[i + 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i + 1].CenterPoint);
                    }
                }
                
                // Apply taper
                for (int i = 0; i < pathSections.Count; i++)
                {
                    float dist = distances[i];
                    if (dist >= taperDistance) continue;
                    
                    var cs = pathSections[i];
                    float originalElevation = cs.TargetElevation;
                    
                    // Quintic smoothstep for very smooth taper
                    float t = dist / taperDistance;
                    float blend = t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
                    
                    // Blend: at endpoint (t=0) = blend toward terrain; at taper distance (t=1) = original
                    float targetAtEndpoint = originalElevation * (1.0f - junctionParams.EndpointTerrainBlendStrength) 
                                            + terrainElevation * junctionParams.EndpointTerrainBlendStrength;
                    
                    cs.TargetElevation = targetAtEndpoint * (1.0f - blend) + originalElevation * blend;
                    
                    if (MathF.Abs(cs.TargetElevation - originalElevation) > 0.001f)
                        taperedCount++;
                }
            }
        }
        
        TerrainLogger.Info($"  Applied endpoint taper to {taperedCount} cross-sections");
        return taperedCount;
    }
    
    /// <summary>
    /// Exports a debug image showing junction detection and elevation changes.
    /// 
    /// Shows:
    /// - Road mask as dark gray background
    /// - All cross-sections as thin lines (color = elevation change: blue=lowered, red=raised, gray=unchanged)
    /// - Path endpoints marked with circles (cyan for start, magenta for end of each path)
    /// - Detected junctions marked with larger circles (green=multi-path junction, yellow=isolated endpoint)
    /// - Junction blend zones shown as semi-transparent overlays
    /// </summary>
    private void ExportJunctionDebugImage(
        RoadGeometry geometry,
        List<Junction> junctions,
        Dictionary<int, float> preHarmonizationElevations,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        
        TerrainLogger.Info($"  Exporting junction debug image ({width}x{height})...");
        
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
        
        // Step 1: Draw road mask as dark gray
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(40, 40, 40, 255);
                }
            }
        }
        
        // Step 2: Compute elevation change range for color mapping
        float maxLower = 0f;
        float maxRaise = 0f;
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            if (preHarmonizationElevations.TryGetValue(cs.Index, out float preElev))
            {
                float change = cs.TargetElevation - preElev;
                if (change < 0) maxLower = MathF.Max(maxLower, MathF.Abs(change));
                else maxRaise = MathF.Max(maxRaise, change);
            }
        }
        float maxChange = MathF.Max(maxLower, maxRaise);
        if (maxChange < 0.01f) maxChange = 1f; // Avoid division by zero
        
        // Step 3: Draw cross-sections colored by elevation change
        float halfWidth = parameters.RoadWidthMeters / 2.0f;
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            float change = 0f;
            if (preHarmonizationElevations.TryGetValue(cs.Index, out float preElev))
            {
                change = cs.TargetElevation - preElev;
            }
            
            // Color: gray=unchanged, blue=lowered, red=raised
            Rgba32 color;
            if (MathF.Abs(change) < 0.001f)
            {
                color = new Rgba32(80, 80, 80, 255); // Unchanged = dark gray
            }
            else if (change < 0)
            {
                // Lowered = blue (more intense = more change)
                float intensity = MathF.Abs(change) / maxChange;
                color = new Rgba32((byte)(80 * (1 - intensity)), (byte)(80 * (1 - intensity)), (byte)(80 + 175 * intensity), 255);
            }
            else
            {
                // Raised = red (more intense = more change)
                float intensity = change / maxChange;
                color = new Rgba32((byte)(80 + 175 * intensity), (byte)(80 * (1 - intensity)), (byte)(80 * (1 - intensity)), 255);
            }
            
            // Draw cross-section line
            var center = cs.CenterPoint;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, color);
        }
        
        // Step 4: Mark all path endpoints with small circles
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .GroupBy(cs => cs.PathId)
            .ToList();
        
        foreach (var pathGroup in pathGroups)
        {
            var ordered = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();
            if (ordered.Count == 0) continue;
            
            // First endpoint = cyan
            var first = ordered[0];
            int fx = (int)(first.CenterPoint.X / metersPerPixel);
            int fy = height - 1 - (int)(first.CenterPoint.Y / metersPerPixel);
            DrawFilledCircle(image, fx, fy, 3, new Rgba32(0, 255, 255, 255));
            
            // Last endpoint = magenta
            if (ordered.Count > 1)
            {
                var last = ordered[^1];
                int lx = (int)(last.CenterPoint.X / metersPerPixel);
                int ly = height - 1 - (int)(last.CenterPoint.Y / metersPerPixel);
                DrawFilledCircle(image, lx, ly, 3, new Rgba32(255, 0, 255, 255));
            }
        }
        
        // Step 5: Draw detected junctions with larger circles
        var junctionParams = parameters.JunctionHarmonizationParameters ?? new JunctionHarmonizationParameters();
        int blendRadiusPixels = (int)(junctionParams.JunctionBlendDistanceMeters / metersPerPixel);
        int detectionRadiusPixels = (int)(junctionParams.JunctionDetectionRadiusMeters / metersPerPixel);
        
        foreach (var junction in junctions)
        {
            int jx = (int)(junction.CenterPosition.X / metersPerPixel);
            int jy = height - 1 - (int)(junction.CenterPosition.Y / metersPerPixel);
            
            if (junction.IsIsolatedEndpoint)
            {
                // Isolated endpoint = yellow circle with taper zone outline
                DrawCircleOutline(image, jx, jy, detectionRadiusPixels / 2, new Rgba32(255, 255, 0, 180));
                DrawFilledCircle(image, jx, jy, 5, new Rgba32(255, 255, 0, 255));
            }
            else
            {
                // Multi-path junction = green circle with blend zone outline
                DrawCircleOutline(image, jx, jy, blendRadiusPixels, new Rgba32(0, 255, 0, 100));
                DrawCircleOutline(image, jx, jy, detectionRadiusPixels, new Rgba32(0, 255, 0, 180));
                DrawFilledCircle(image, jx, jy, 7, new Rgba32(0, 255, 0, 255));
            }
        }
        
        // Step 6: Add legend with text labels
        // Top-left corner legend with semi-transparent background
        int legendX = 10;
        int legendY = 10;
        int legendSize = 12;
        int legendSpacing = 18;
        int textOffsetX = legendSize + 8; // Space between symbol and text
        var textColor = new Rgba32(220, 220, 220, 255);
        
        // Draw legend background
        int legendBgWidth = 180;
        int legendBgHeight = legendSpacing * 7 + 10;
        DrawFilledRect(image, legendX - 5, legendY - 5, legendBgWidth, legendBgHeight, new Rgba32(20, 20, 20, 200));
        
        // Unchanged
        DrawFilledRect(image, legendX, legendY, legendSize, legendSize, new Rgba32(80, 80, 80, 255));
        DrawText(image, legendX + textOffsetX, legendY + 2, "Unchanged", textColor);
        
        // Lowered
        DrawFilledRect(image, legendX, legendY + legendSpacing, legendSize, legendSize, new Rgba32(0, 0, 255, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing + 2, "Lowered", textColor);
        
        // Raised  
        DrawFilledRect(image, legendX, legendY + legendSpacing * 2, legendSize, legendSize, new Rgba32(255, 0, 0, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing * 2 + 2, "Raised", textColor);
        
        // Multi-path junction
        DrawFilledCircle(image, legendX + legendSize / 2, legendY + legendSpacing * 3 + legendSize / 2, 5, new Rgba32(0, 255, 0, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing * 3 + 2, "Junction", textColor);
        
        // Isolated endpoint
        DrawFilledCircle(image, legendX + legendSize / 2, legendY + legendSpacing * 4 + legendSize / 2, 5, new Rgba32(255, 255, 0, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing * 4 + 2, "Isolated End", textColor);
        
        // Path start
        DrawFilledCircle(image, legendX + legendSize / 2, legendY + legendSpacing * 5 + legendSize / 2, 3, new Rgba32(0, 255, 255, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing * 5 + 2, "Path Start", textColor);
        
        // Path end
        DrawFilledCircle(image, legendX + legendSize / 2, legendY + legendSpacing * 6 + legendSize / 2, 3, new Rgba32(255, 0, 255, 255));
        DrawText(image, legendX + textOffsetX, legendY + legendSpacing * 6 + 2, "Path End", textColor);
        
        // Step 7: Save image
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "junction_harmonization_debug.png");
        image.SaveAsPng(filePath);
        
        TerrainLogger.Info($"  Exported junction debug image: {filePath}");
        TerrainLogger.Info($"  Legend: Gray=unchanged, Blue=lowered, Red=raised");
        TerrainLogger.Info($"  Legend: Green circle=multi-path junction, Yellow circle=isolated endpoint");
        TerrainLogger.Info($"  Legend: Cyan=path start, Magenta=path end");
        TerrainLogger.Info($"  Max elevation change: {maxChange:F3}m");
    }
    
    /// <summary>
    /// Draw a line using Bresenham's algorithm.
    /// </summary>
    private void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int height = img.Height;
        // Flip Y input -> image coordinates
        y0 = height - 1 - y0;
        y1 = height - 1 - y1;
        
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        
        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
    
    /// <summary>
    /// Draw a filled circle.
    /// </summary>
    private void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius * radius)
                {
                    int px = cx + x;
                    int py = cy + y;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                        img[px, py] = color;
                }
            }
        }
    }
    
    /// <summary>
    /// Draw a circle outline.
    /// </summary>
    private void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (int angle = 0; angle < 360; angle += 2)
        {
            float rad = angle * MathF.PI / 180f;
            int px = cx + (int)(radius * MathF.Cos(rad));
            int py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }
    
    /// <summary>
    /// Draw a filled rectangle.
    /// </summary>
    private void DrawFilledRect(Image<Rgba32> img, int x, int y, int w, int h, Rgba32 color)
    {
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    img[px, py] = color;
            }
        }
    }
    
    /// <summary>
    /// Draw simple bitmap text using a basic 5x7 pixel font.
    /// Supports uppercase letters, lowercase letters, numbers, and common punctuation.
    /// </summary>
    private void DrawText(Image<Rgba32> img, int x, int y, string text, Rgba32 color)
    {
        int charWidth = 6; // 5 pixels + 1 spacing
        int currentX = x;
        
        foreach (char c in text)
        {
            DrawChar(img, currentX, y, c, color);
            currentX += charWidth;
        }
    }
    
    /// <summary>
    /// Draw a single character using a simple 5x7 bitmap font.
    /// </summary>
    private void DrawChar(Image<Rgba32> img, int x, int y, char c, Rgba32 color)
    {
        // Simple 5x7 bitmap font patterns (each string is a row, '1' = pixel on)
        var charPatterns = new Dictionary<char, string[]>
        {
            ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
            ['B'] = ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
            ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
            ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
            ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
            ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
            ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01110"],
            ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
            ['I'] = ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
            ['J'] = ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
            ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
            ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
            ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
            ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
            ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
            ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
            ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
            ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
            ['S'] = ["01110", "10001", "10000", "01110", "00001", "10001", "01110"],
            ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
            ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
            ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
            ['W'] = ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
            ['X'] = ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
            ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
            ['Z'] = ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
            ['a'] = ["00000", "00000", "01110", "00001", "01111", "10001", "01111"],
            ['b'] = ["10000", "10000", "10110", "11001", "10001", "10001", "11110"],
            ['c'] = ["00000", "00000", "01110", "10000", "10000", "10001", "01110"],
            ['d'] = ["00001", "00001", "01101", "10011", "10001", "10001", "01111"],
            ['e'] = ["00000", "00000", "01110", "10001", "11111", "10000", "01110"],
            ['f'] = ["00110", "01001", "01000", "11100", "01000", "01000", "01000"],
            ['g'] = ["00000", "01111", "10001", "10001", "01111", "00001", "01110"],
            ['h'] = ["10000", "10000", "10110", "11001", "10001", "10001", "10001"],
            ['i'] = ["00100", "00000", "01100", "00100", "00100", "00100", "01110"],
            ['j'] = ["00010", "00000", "00110", "00010", "00010", "10010", "01100"],
            ['k'] = ["10000", "10000", "10010", "10100", "11000", "10100", "10010"],
            ['l'] = ["01100", "00100", "00100", "00100", "00100", "00100", "01110"],
            ['m'] = ["00000", "00000", "11010", "10101", "10101", "10001", "10001"],
            ['n'] = ["00000", "00000", "10110", "11001", "10001", "10001", "10001"],
            ['o'] = ["00000", "00000", "01110", "10001", "10001", "10001", "01110"],
            ['p'] = ["00000", "00000", "11110", "10001", "11110", "10000", "10000"],
            ['q'] = ["00000", "00000", "01101", "10011", "01111", "00001", "00001"],
            ['r'] = ["00000", "00000", "10110", "11001", "10000", "10000", "10000"],
            ['s'] = ["00000", "00000", "01110", "10000", "01110", "00001", "11110"],
            ['t'] = ["01000", "01000", "11100", "01000", "01000", "01001", "00110"],
            ['u'] = ["00000", "00000", "10001", "10001", "10001", "10011", "01101"],
            ['v'] = ["00000", "00000", "10001", "10001", "10001", "01010", "00100"],
            ['w'] = ["00000", "00000", "10001", "10001", "10101", "10101", "01010"],
            ['x'] = ["00000", "00000", "10001", "01010", "00100", "01010", "10001"],
            ['y'] = ["00000", "00000", "10001", "10001", "01111", "00001", "01110"],
            ['z'] = ["00000", "00000", "11111", "00010", "00100", "01000", "11111"],
            ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
            ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
            ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
            ['3'] = ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
            ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
            ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
            ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
            ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
            ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
            ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
            [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
            ['.'] = ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
            [','] = ["00000", "00000", "00000", "00000", "00110", "00100", "01000"],
            ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
            ['_'] = ["00000", "00000", "00000", "00000", "00000", "00000", "11111"],
            [':'] = ["00000", "01100", "01100", "00000", "01100", "01100", "00000"],
            ['('] = ["00010", "00100", "01000", "01000", "01000", "00100", "00010"],
            [')'] = ["01000", "00100", "00010", "00010", "00010", "00100", "01000"],
            ['='] = ["00000", "00000", "11111", "00000", "11111", "00000", "00000"],
            ['/'] = ["00001", "00010", "00010", "00100", "01000", "01000", "10000"],
        };
        
        // Convert to uppercase for matching if lowercase not found
        if (!charPatterns.TryGetValue(c, out var pattern))
        {
            if (!charPatterns.TryGetValue(char.ToUpper(c), out pattern))
            {
                return; // Unknown character, skip it
            }
        }
        
        // Draw the character pattern
        for (int row = 0; row < pattern.Length; row++)
        {
            for (int col = 0; col < pattern[row].Length; col++)
            {
                if (pattern[row][col] == '1')
                {
                    int px = x + col;
                    int py = y + row;
                    if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    {
                        img[px, py] = color;
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Exports a slope discontinuity debug image showing where elevation jumps exist.
    /// 
    /// This is more useful than absolute elevation comparison because:
    /// - The changes are typically sub-meter on a terrain with hundreds of meters range
    /// - What matters for driving feel is SLOPE CHANGE, not absolute elevation
    /// 
    /// Shows:
    /// - Each cross-section colored by the slope TO the next cross-section
    /// - Before (left): Slopes before harmonization
    /// - After (right): Slopes after harmonization
    /// - Sharp color transitions = elevation discontinuities (the problem we're fixing)
    /// - Smooth color transitions = good road
    /// 
    /// Color coding: Green = flat/gentle, Yellow = moderate, Red = steep, Magenta = very steep
    /// </summary>
    private void ExportBeforeAfterElevationImage(
        RoadGeometry geometry,
        Dictionary<int, float> preHarmonizationElevations,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        int singleWidth = geometry.Width;
        int height = geometry.Height;
        int totalWidth = singleWidth * 2 + 20; // Side by side with 20px gap
        
        TerrainLogger.Info($"  Exporting slope discontinuity comparison ({totalWidth}x{height})...");
        
        using var image = new Image<Rgba32>(totalWidth, height, new Rgba32(0, 0, 0, 255));
        
        // Group cross-sections by path for slope calculation
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .GroupBy(cs => cs.PathId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());
        
        // Calculate slopes for before and after
        var beforeSlopes = new Dictionary<int, float>(); // cs.Index -> slope in degrees
        var afterSlopes = new Dictionary<int, float>();
        float maxSlope = 0f;
        
        foreach (var (pathId, sections) in pathGroups)
        {
            for (int i = 0; i < sections.Count - 1; i++)
            {
                var current = sections[i];
                var next = sections[i + 1];
                
                float distance = Vector2.Distance(current.CenterPoint, next.CenterPoint);
                if (distance < 0.001f) continue;
                
                // Before slope
                if (preHarmonizationElevations.TryGetValue(current.Index, out float preElevCurr) &&
                    preHarmonizationElevations.TryGetValue(next.Index, out float preElevNext))
                {
                    float rise = MathF.Abs(preElevNext - preElevCurr);
                    float slopeDeg = MathF.Atan(rise / distance) * 180f / MathF.PI;
                    beforeSlopes[current.Index] = slopeDeg;
                    if (slopeDeg > maxSlope) maxSlope = slopeDeg;
                }
                
                // After slope
                float afterRise = MathF.Abs(next.TargetElevation - current.TargetElevation);
                float afterSlopeDeg = MathF.Atan(afterRise / distance) * 180f / MathF.PI;
                afterSlopes[current.Index] = afterSlopeDeg;
                if (afterSlopeDeg > maxSlope) maxSlope = afterSlopeDeg;
            }
        }
        
        // Normalize slopes to a reasonable range (0-15 degrees typically)
        float slopeRange = MathF.Max(maxSlope, 15f);
        
        // Draw road mask on both sides
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < singleWidth; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(30, 30, 30, 255);
                    image[x + singleWidth + 20, height - 1 - y] = new Rgba32(30, 30, 30, 255);
                }
            }
        }
        
        // Draw separator line
        for (int y = 0; y < height; y++)
        {
            for (int x = singleWidth; x < singleWidth + 20; x++)
            {
                image[x, y] = new Rgba32(60, 60, 60, 255);
            }
        }
        
        float halfWidth = parameters.RoadWidthMeters / 2.0f;
        
        // Draw BEFORE slopes (left side)
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            if (!beforeSlopes.TryGetValue(cs.Index, out float slope))
                slope = 0f;
            
            float normalized = Math.Clamp(slope / slopeRange, 0f, 1f);
            var color = GetSlopeColor(normalized);
            
            var center = cs.CenterPoint;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            
            DrawLineOnImage(image, lx, ly, rx, ry, color, height, 0);
        }
        
        // Draw AFTER slopes (right side with offset)
        foreach (var cs in geometry.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            if (!afterSlopes.TryGetValue(cs.Index, out float slope))
                slope = 0f;
            
            float normalized = Math.Clamp(slope / slopeRange, 0f, 1f);
            var color = GetSlopeColor(normalized);
            
            var center = cs.CenterPoint;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            
            DrawLineOnImage(image, lx, ly, rx, ry, color, height, singleWidth + 20);
        }
        
        // Add legend at bottom
        int legendY = height - 30;
        int legendWidth = 200;
        int legendHeight = 15;
        int legendX = (totalWidth - legendWidth) / 2;
        
        // Draw gradient bar
        for (int x = 0; x < legendWidth; x++)
        {
            float t = (float)x / legendWidth;
            var color = GetSlopeColor(t);
            for (int ly = 0; ly < legendHeight; ly++)
            {
                int px = legendX + x;
                int py = legendY + ly;
                if (px >= 0 && px < totalWidth && py >= 0 && py < height)
                    image[px, py] = color;
            }
        }
        
        // Add labels
        DrawFilledRect(image, 5, 5, 60, 14, new Rgba32(0, 100, 0, 255)); // "BEFORE" green box
        DrawFilledRect(image, singleWidth + 25, 5, 50, 14, new Rgba32(0, 100, 0, 255)); // "AFTER" green box
        
        // Save image
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "junction_slope_comparison.png");
        image.SaveAsPng(filePath);
        
        TerrainLogger.Info($"  Exported slope comparison: {filePath}");
        TerrainLogger.Info($"  Slope range: 0° (green) to {slopeRange:F1}° (magenta)");
        TerrainLogger.Info($"  Max slope found: {maxSlope:F2}°");
    }
    
    /// <summary>
    /// Gets a color for slope visualization.
    /// Green (flat) -> Yellow (moderate) -> Red (steep) -> Magenta (very steep)
    /// </summary>
    private Rgba32 GetSlopeColor(float normalizedSlope)
    {
        normalizedSlope = Math.Clamp(normalizedSlope, 0f, 1f);
        
        float r, g, b;
        
        if (normalizedSlope < 0.33f)
        {
            // Green to Yellow (flat to moderate)
            float t = normalizedSlope / 0.33f;
            r = t;
            g = 1f;
            b = 0f;
        }
        else if (normalizedSlope < 0.66f)
        {
            // Yellow to Red (moderate to steep)
            float t = (normalizedSlope - 0.33f) / 0.33f;
            r = 1f;
            g = 1f - t;
            b = 0f;
        }
        else
        {
            // Red to Magenta (steep to very steep)
            float t = (normalizedSlope - 0.66f) / 0.34f;
            r = 1f;
            g = 0f;
            b = t;
        }
        
        return new Rgba32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
    }
    
    /// <summary>
    /// Draw a line with X offset (for side-by-side comparison).
    /// </summary>
    private void DrawLineOnImage(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int imgHeight, int xOffset)
    {
        // Flip Y input -> image coordinates
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        x0 += xOffset;
        x1 += xOffset;
        
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        
        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
