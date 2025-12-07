using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

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
        
        // Step 1: Find path endpoints
        var endpoints = FindPathEndpoints(geometry);
        TerrainLogger.Info($"  Found {endpoints.Count} path endpoints");
        
        // Step 2: Detect junction clusters
        var junctions = DetectJunctions(endpoints, junctionParams.JunctionDetectionRadiusMeters);
        TerrainLogger.Info($"  Detected {junctions.Count(j => !j.IsIsolatedEndpoint)} multi-path junctions");
        TerrainLogger.Info($"  Detected {junctions.Count(j => j.IsIsolatedEndpoint)} isolated endpoints");
        
        // Step 3: Compute harmonized elevations for each junction
        ComputeJunctionElevations(junctions, heightMap, metersPerPixel, junctionParams);
        
        // Step 4: Propagate junction constraints back along paths
        PropagateJunctionConstraints(geometry, junctions, junctionParams);
        
        // Step 5: Apply endpoint tapering for isolated endpoints
        if (junctionParams.EnableEndpointTaper)
        {
            ApplyEndpointTapering(geometry, junctions, heightMap, metersPerPixel, junctionParams);
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
    /// Uses single-linkage clustering: endpoints within radius are merged into same junction.
    /// </summary>
    private List<Junction> DetectJunctions(List<CrossSection> endpoints, float radiusMeters)
    {
        var junctions = new List<Junction>();
        var assigned = new HashSet<int>(); // Track which endpoints are already assigned
        
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
        
        return junctions;
    }
    
    /// <summary>
    /// Computes the harmonized elevation for each junction.
    /// For multi-path junctions: weighted average of contributing path elevations.
    /// For isolated endpoints: terrain elevation at that point.
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
            else
            {
                // For multi-path junctions: weighted average by inverse distance from center
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
    /// </summary>
    private void PropagateJunctionConstraints(
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
            foreach (var endpointCs in junction.CrossSections)
            {
                if (!pathGroups.TryGetValue(endpointCs.PathId, out var pathSections))
                    continue;
                
                // Determine if this is start or end of path
                bool isStartOfPath = endpointCs.LocalIndex == pathSections[0].LocalIndex;
                
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
    }
    
    /// <summary>
    /// Applies tapering at isolated endpoints to smoothly transition back to terrain.
    /// </summary>
    private void ApplyEndpointTapering(
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
    }
}
