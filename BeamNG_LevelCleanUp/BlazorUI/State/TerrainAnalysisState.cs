using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>
/// Holds the state of terrain analysis, including detected junctions
/// and user-modified exclusions.
/// This state persists between analysis and generation phases,
/// allowing users to review and modify the network before committing.
/// </summary>
public class TerrainAnalysisState
{
    /// <summary>
    /// The analyzed road network (null if not yet analyzed).
    /// Contains all splines, cross-sections, and detected junctions.
    /// </summary>
    public UnifiedRoadNetwork? Network { get; set; }

    /// <summary>
    /// The full analysis result from the TerrainAnalyzer.
    /// Contains additional metadata like statistics and debug image.
    /// </summary>
    public TerrainAnalyzer.AnalysisResult? AnalysisResult { get; set; }

    /// <summary>
    /// Junction IDs that user has marked for exclusion.
    /// These junctions will be skipped during harmonization.
    /// </summary>
    public HashSet<int> ExcludedJunctionIds { get; } = [];

    /// <summary>
    /// Pre-harmonization elevations for comparison.
    /// Key = cross-section index, Value = elevation in meters.
    /// </summary>
    public Dictionary<int, float> PreHarmonizationElevations { get; set; } = new();

    /// <summary>
    /// Debug image data as PNG bytes (null if not generated).
    /// Used for interactive visualization in the UI.
    /// </summary>
    public byte[]? DebugImageData { get; set; }

    /// <summary>
    /// Width of the debug image in pixels.
    /// </summary>
    public int DebugImageWidth { get; set; }

    /// <summary>
    /// Height of the debug image in pixels.
    /// </summary>
    public int DebugImageHeight { get; set; }

    /// <summary>
    /// Timestamp when the analysis was performed.
    /// </summary>
    public DateTime? AnalysisTimestamp { get; set; }

    /// <summary>
    /// Whether analysis has been performed and is valid.
    /// </summary>
    public bool HasAnalysis => Network != null && AnalysisResult is { Success: true };

    /// <summary>
    /// Number of excluded junctions.
    /// </summary>
    public int ExcludedCount => ExcludedJunctionIds.Count;

    /// <summary>
    /// Total number of junctions in the network.
    /// </summary>
    public int TotalJunctionCount => Network?.Junctions.Count ?? 0;

    /// <summary>
    /// Number of active (non-excluded) junctions.
    /// </summary>
    public int ActiveJunctionCount => TotalJunctionCount - ExcludedCount;

    /// <summary>
    /// Number of splines in the network.
    /// </summary>
    public int SplineCount => Network?.Splines.Count ?? 0;

    /// <summary>
    /// Total road length in kilometers.
    /// </summary>
    public float TotalRoadLengthKm => (Network?.Splines.Sum(s => s.TotalLengthMeters) ?? 0) / 1000f;

    /// <summary>
    /// Gets statistics breakdown by junction type.
    /// </summary>
    public Dictionary<JunctionType, int> JunctionsByType => Network?.Junctions
        .Where(j => !j.IsExcluded)
        .GroupBy(j => j.Type)
        .ToDictionary(g => g.Key, g => g.Count()) ?? new();

    /// <summary>
    /// Gets statistics breakdown of excluded junctions by type.
    /// </summary>
    public Dictionary<JunctionType, int> ExcludedJunctionsByType => Network?.Junctions
        .Where(j => j.IsExcluded)
        .GroupBy(j => j.Type)
        .ToDictionary(g => g.Key, g => g.Count()) ?? new();

    /// <summary>
    /// Toggles the exclusion state of a junction.
    /// </summary>
    /// <param name="junctionId">The junction ID to toggle.</param>
    /// <returns>True if the junction is now excluded, false if included.</returns>
    public bool ToggleJunctionExclusion(int junctionId)
    {
        if (ExcludedJunctionIds.Contains(junctionId))
        {
            ExcludedJunctionIds.Remove(junctionId);
            UpdateNetworkExclusion(junctionId, false);
            return false;
        }
        else
        {
            ExcludedJunctionIds.Add(junctionId);
            UpdateNetworkExclusion(junctionId, true);
            return true;
        }
    }

    /// <summary>
    /// Excludes a junction by ID.
    /// </summary>
    /// <param name="junctionId">The junction ID to exclude.</param>
    /// <param name="reason">Optional reason for exclusion.</param>
    public void ExcludeJunction(int junctionId, string? reason = null)
    {
        ExcludedJunctionIds.Add(junctionId);
        UpdateNetworkExclusion(junctionId, true, reason);
    }

    /// <summary>
    /// Includes (un-excludes) a junction by ID.
    /// </summary>
    /// <param name="junctionId">The junction ID to include.</param>
    public void IncludeJunction(int junctionId)
    {
        ExcludedJunctionIds.Remove(junctionId);
        UpdateNetworkExclusion(junctionId, false);
    }

    /// <summary>
    /// Excludes multiple junctions by ID.
    /// </summary>
    /// <param name="junctionIds">The junction IDs to exclude.</param>
    /// <param name="reason">Optional reason for exclusion.</param>
    public void ExcludeJunctions(IEnumerable<int> junctionIds, string? reason = null)
    {
        foreach (var id in junctionIds)
        {
            ExcludeJunction(id, reason);
        }
    }

    /// <summary>
    /// Clears all exclusions.
    /// </summary>
    public void ClearAllExclusions()
    {
        foreach (var id in ExcludedJunctionIds.ToList())
        {
            IncludeJunction(id);
        }
        ExcludedJunctionIds.Clear();
    }

    /// <summary>
    /// Applies all exclusions to the network before generation.
    /// This ensures the network's junction IsExcluded flags match our state.
    /// </summary>
    public void ApplyExclusions()
    {
        if (Network == null) return;

        foreach (var junction in Network.Junctions)
        {
            var shouldBeExcluded = ExcludedJunctionIds.Contains(junction.JunctionId);
            junction.IsExcluded = shouldBeExcluded;
            if (shouldBeExcluded && string.IsNullOrEmpty(junction.ExclusionReason))
            {
                junction.ExclusionReason = "User excluded";
            }
            else if (!shouldBeExcluded)
            {
                junction.ExclusionReason = null;
            }
        }
    }

    /// <summary>
    /// Checks if a specific junction is excluded.
    /// </summary>
    /// <param name="junctionId">The junction ID to check.</param>
    /// <returns>True if excluded, false otherwise.</returns>
    public bool IsJunctionExcluded(int junctionId)
    {
        return ExcludedJunctionIds.Contains(junctionId);
    }

    /// <summary>
    /// Gets a junction by its ID.
    /// </summary>
    /// <param name="junctionId">The junction ID.</param>
    /// <returns>The junction, or null if not found.</returns>
    public NetworkJunction? GetJunction(int junctionId)
    {
        return Network?.Junctions.FirstOrDefault(j => j.JunctionId == junctionId);
    }

    /// <summary>
    /// Clears all analysis state.
    /// </summary>
    public void Reset()
    {
        Network = null;
        AnalysisResult = null;
        ExcludedJunctionIds.Clear();
        PreHarmonizationElevations.Clear();
        DebugImageData = null;
        DebugImageWidth = 0;
        DebugImageHeight = 0;
        AnalysisTimestamp = null;
    }

    /// <summary>
    /// Sets the analysis result and extracts relevant data.
    /// </summary>
    /// <param name="result">The analysis result from TerrainAnalyzer.</param>
    public void SetAnalysisResult(TerrainAnalyzer.AnalysisResult result)
    {
        AnalysisResult = result;
        Network = result.Network;
        PreHarmonizationElevations = result.PreHarmonizationElevations;
        DebugImageData = result.JunctionDebugImage;
        DebugImageWidth = result.ImageWidth;
        DebugImageHeight = result.ImageHeight;
        AnalysisTimestamp = DateTime.Now;

        // Clear previous exclusions since we have a new analysis
        ExcludedJunctionIds.Clear();

        // Sync any pre-existing exclusions from the network (e.g., auto-detected issues)
        if (result.Network != null)
        {
            foreach (var junction in result.Network.Junctions.Where(j => j.IsExcluded))
            {
                ExcludedJunctionIds.Add(junction.JunctionId);
            }
        }
    }

    /// <summary>
    /// Gets a summary string for display.
    /// </summary>
    public string GetSummary()
    {
        if (!HasAnalysis)
            return "No analysis performed";

        var parts = new List<string>
        {
            $"{SplineCount} splines",
            $"{TotalRoadLengthKm:F1}km total",
            $"{TotalJunctionCount} junctions"
        };

        if (ExcludedCount > 0)
        {
            parts.Add($"({ExcludedCount} excluded)");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets the debug image as a base64 data URL for display in HTML.
    /// </summary>
    public string? GetDebugImageDataUrl()
    {
        if (DebugImageData == null || DebugImageData.Length == 0)
            return null;

        return $"data:image/png;base64,{Convert.ToBase64String(DebugImageData)}";
    }

    /// <summary>
    /// Updates the network junction's exclusion state.
    /// </summary>
    private void UpdateNetworkExclusion(int junctionId, bool isExcluded, string? reason = null)
    {
        if (Network == null) return;

        var junction = Network.Junctions.FirstOrDefault(j => j.JunctionId == junctionId);
        if (junction != null)
        {
            junction.IsExcluded = isExcluded;
            junction.ExclusionReason = isExcluded ? (reason ?? "User excluded") : null;
        }
    }
}
