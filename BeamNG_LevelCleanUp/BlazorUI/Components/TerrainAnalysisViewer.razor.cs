using System.Globalization;
using System.Numerics;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Microsoft.AspNetCore.Components;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;
using WheelEventArgs = Microsoft.AspNetCore.Components.Web.WheelEventArgs;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
/// Interactive viewer component for terrain analysis results.
/// Displays detected splines and junctions, allowing users to
/// click on junctions to exclude them from harmonization.
/// </summary>
public partial class TerrainAnalysisViewer : ComponentBase, IDisposable
{
    /// <summary>
    /// The analysis state containing the road network and junction data.
    /// </summary>
    [Parameter]
    public TerrainAnalysisState? AnalysisState { get; set; }

    /// <summary>
    /// Callback when junction exclusions change.
    /// </summary>
    [Parameter]
    public EventCallback<HashSet<int>> OnExclusionsChanged { get; set; }

    /// <summary>
    /// Terrain size in pixels (used for coordinate scaling).
    /// </summary>
    [Parameter]
    public int TerrainSize { get; set; } = 1024;

    /// <summary>
    /// Meters per pixel for coordinate conversion.
    /// </summary>
    [Parameter]
    public float MetersPerPixel { get; set; } = 1.0f;

    // Canvas and view state
    private ElementReference _canvasContainer;
    private const int CanvasSize = 500;
    private float _zoom = 1.0f;
    private float _panX = 0f;
    private float _panY = 0f;
    private float _networkExtent = 1024f;

    // Interaction state
    private bool _isPanning;
    private double _lastMouseX;
    private double _lastMouseY;

    // Selection state - now supports clusters of overlapping junctions
    private int? _selectedJunctionId;
    private int? _hoveredJunctionId;
    private NetworkJunction? _selectedJunction;
    private List<NetworkJunction> _selectedCluster = [];
    
    // Cluster detection threshold in world meters
    private const float ClusterRadiusMeters = 15f;

    // Debouncing for smooth zoom/pan
    private System.Timers.Timer? _debounceTimer;
    private bool _renderPending;
    private const int DebounceDelayMs = 16; // ~60fps max

    // Cached computed values for performance
    private string? _cachedViewBox;
    private float _lastZoomForViewBox;
    private float _lastPanXForViewBox;
    private float _lastPanYForViewBox;
    private Dictionary<int, string>? _cachedSplinePaths;
    private Dictionary<int, (Vector2 Pos, float Radius, string Color)>? _cachedJunctionData;

    // Material colors for spline rendering
    private readonly Dictionary<string, string> _materialColors = new();
    private int _colorIndex;
    private static readonly string[] PaletteColors =
    [
        "#FFD700", // Gold
        "#00CED1", // Dark Cyan
        "#FF6B6B", // Light Red
        "#98FB98", // Pale Green
        "#DDA0DD", // Plum
        "#87CEEB", // Sky Blue
        "#FFA07A", // Light Salmon
        "#90EE90"  // Light Green
    ];

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Calculate network extent from analysis data
        if (AnalysisState?.Network != null)
        {
            CalculateNetworkExtent();
            // Pre-compute spline paths and junction positions (expensive operations)
            CacheSplineAndJunctionData();
        }

        // Update selected junction reference if it changed
        if (_selectedJunctionId.HasValue && AnalysisState != null)
        {
            _selectedJunction = AnalysisState.GetJunction(_selectedJunctionId.Value);
        }
        
        // Invalidate viewbox cache when parameters change
        _cachedViewBox = null;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        // Setup debounce timer
        _debounceTimer = new System.Timers.Timer(DebounceDelayMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;
    }

    private void OnDebounceTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_renderPending)
        {
            _renderPending = false;
            InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Request a debounced re-render. Multiple rapid calls will coalesce into one render.
    /// </summary>
    private void RequestRender()
    {
        _renderPending = true;
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    public void Dispose()
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
    }

    private void CalculateNetworkExtent()
    {
        if (AnalysisState?.Network == null) return;

        // Use the debug image dimensions if available, otherwise calculate from cross-sections
        if (AnalysisState.DebugImageWidth > 0)
        {
            _networkExtent = Math.Max(AnalysisState.DebugImageWidth, AnalysisState.DebugImageHeight);
        }
        else
        {
            // Calculate extent from cross-section positions
            float maxX = 0, maxY = 0;
            foreach (var cs in AnalysisState.Network.CrossSections)
            {
                var x = cs.CenterPoint.X / MetersPerPixel;
                var y = cs.CenterPoint.Y / MetersPerPixel;
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            _networkExtent = Math.Max(maxX, maxY) * 1.1f; // Add 10% margin
        }

        // Ensure minimum extent
        _networkExtent = Math.Max(_networkExtent, 100);
    }

    /// <summary>
    /// Pre-computes spline paths and junction positions to avoid recalculating during render.
    /// This is called once when analysis data changes, not on every zoom/pan.
    /// </summary>
    private void CacheSplineAndJunctionData()
    {
        if (AnalysisState?.Network == null) return;

        // Cache spline paths (expensive string building)
        _cachedSplinePaths = new Dictionary<int, string>();
        foreach (var spline in AnalysisState.Network.Splines)
        {
            var crossSections = AnalysisState.Network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count >= 2)
            {
                _cachedSplinePaths[spline.SplineId] = GetSplinePathData(crossSections);
            }
        }

        // Cache junction canvas positions and colors (coordinate transforms)
        _cachedJunctionData = new Dictionary<int, (Vector2, float, string)>();
        foreach (var junction in AnalysisState.Network.Junctions)
        {
            var pos = TransformToCanvas(junction.Position);
            var radius = GetJunctionRadius(junction.Type);
            var color = GetJunctionColor(junction.Type);
            _cachedJunctionData[junction.JunctionId] = (pos, radius, color);
        }
    }

    private string GetViewBox()
    {
        // Use cached viewbox if zoom/pan hasn't changed
        if (_cachedViewBox != null && 
            Math.Abs(_lastZoomForViewBox - _zoom) < 0.001f &&
            Math.Abs(_lastPanXForViewBox - _panX) < 0.001f &&
            Math.Abs(_lastPanYForViewBox - _panY) < 0.001f)
        {
            return _cachedViewBox;
        }

        var size = _networkExtent / _zoom;
        var offsetX = _panX;
        var offsetY = _panY;
        _cachedViewBox = $"{offsetX.ToString("F2", CultureInfo.InvariantCulture)} {offsetY.ToString("F2", CultureInfo.InvariantCulture)} {size.ToString("F2", CultureInfo.InvariantCulture)} {size.ToString("F2", CultureInfo.InvariantCulture)}";
        _lastZoomForViewBox = _zoom;
        _lastPanXForViewBox = _panX;
        _lastPanYForViewBox = _panY;
        return _cachedViewBox;
    }

    /// <summary>
    /// Gets cached junction data for rendering. Falls back to computing if not cached.
    /// </summary>
    private (Vector2 Pos, float Radius, string Color) GetCachedJunctionData(NetworkJunction junction)
    {
        if (_cachedJunctionData != null && _cachedJunctionData.TryGetValue(junction.JunctionId, out var data))
        {
            return data;
        }
        // Fallback if not cached
        return (TransformToCanvas(junction.Position), GetJunctionRadius(junction.Type), GetJunctionColor(junction.Type));
    }

    /// <summary>
    /// Gets cached spline path data for rendering.
    /// </summary>
    private string? GetCachedSplinePath(int splineId)
    {
        return _cachedSplinePaths?.GetValueOrDefault(splineId);
    }

    private Vector2 TransformToCanvas(Vector2 worldPosition)
    {
        // Convert from world meters to pixel coordinates
        var x = worldPosition.X / MetersPerPixel;
        // Flip Y axis (world Y increases up, canvas Y increases down)
        var y = _networkExtent - (worldPosition.Y / MetersPerPixel);
        return new Vector2(x, y);
    }

    private string GetSplinePathData(List<UnifiedCrossSection> crossSections)
    {
        if (crossSections.Count < 2) return string.Empty;

        var sb = new System.Text.StringBuilder();
        var first = TransformToCanvas(crossSections[0].CenterPoint);
        sb.Append($"M {first.X.ToString("F1", CultureInfo.InvariantCulture)},{first.Y.ToString("F1", CultureInfo.InvariantCulture)}");

        for (int i = 1; i < crossSections.Count; i++)
        {
            var pt = TransformToCanvas(crossSections[i].CenterPoint);
            sb.Append($" L {pt.X.ToString("F1", CultureInfo.InvariantCulture)},{pt.Y.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        return sb.ToString();
    }

    private string GetMaterialColor(string materialName)
    {
        if (!_materialColors.TryGetValue(materialName, out var color))
        {
            color = PaletteColors[_colorIndex % PaletteColors.Length];
            _materialColors[materialName] = color;
            _colorIndex++;
        }
        return color;
    }

    private static string GetJunctionColor(JunctionType type)
    {
        return type switch
        {
            JunctionType.Endpoint => "#FFFF00",        // Yellow
            JunctionType.TJunction => "#00FFFF",       // Cyan
            JunctionType.YJunction => "#00FF00",       // Green
            JunctionType.CrossRoads => "#FF8000",      // Orange
            JunctionType.Complex => "#FF00FF",         // Magenta
            JunctionType.MidSplineCrossing => "#FF4080", // Pink/Coral
            _ => "#FFFFFF"
        };
    }

    private static float GetJunctionRadius(JunctionType type)
    {
        return type == JunctionType.Endpoint ? 5f : 8f;
    }

    private string GetJunctionTooltip(NetworkJunction junction)
    {
        var excluded = AnalysisState?.IsJunctionExcluded(junction.JunctionId) ?? false;
        var status = excluded ? " [EXCLUDED]" : "";
        return $"Junction #{junction.JunctionId}: {junction.Type}{status}\n" +
               $"Position: ({junction.Position.X:F1}, {junction.Position.Y:F1})\n" +
               $"Click to select/toggle exclusion";
    }

    #region Mouse Interaction

    private void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == 0) // Left button
        {
            _isPanning = true;
            _lastMouseX = e.ClientX;
            _lastMouseY = e.ClientY;
        }
    }

    private void OnMouseMove(MouseEventArgs e)
    {
        if (_isPanning)
        {
            var deltaX = e.ClientX - _lastMouseX;
            var deltaY = e.ClientY - _lastMouseY;

            // Scale delta by zoom level and canvas/viewbox ratio
            var scaleFactor = _networkExtent / (_zoom * CanvasSize);
            _panX -= (float)(deltaX * scaleFactor);
            _panY -= (float)(deltaY * scaleFactor);

            // Calculate pan bounds based on zoom level
            // The viewable area is (_networkExtent / _zoom), so we can pan from 0 to the area beyond the viewport
            var viewableSize = _networkExtent / _zoom;
            var maxPan = _networkExtent - viewableSize;
            
            // Clamp pan: at zoom=1, maxPan=0 (no panning needed); at higher zoom, allow full range
            _panX = Math.Clamp(_panX, 0, Math.Max(0, maxPan));
            _panY = Math.Clamp(_panY, 0, Math.Max(0, maxPan));

            _lastMouseX = e.ClientX;
            _lastMouseY = e.ClientY;

            // Use debounced render for smooth panning
            _cachedViewBox = null; // Invalidate viewbox cache
            RequestRender();
        }
    }

    private void OnMouseUp(MouseEventArgs e)
    {
        _isPanning = false;
    }

    private void OnMouseLeave(MouseEventArgs e)
    {
        _isPanning = false;
    }

    private void OnWheel(WheelEventArgs e)
    {
        var zoomDelta = e.DeltaY > 0 ? 0.9f : 1.1f;
        var newZoom = Math.Clamp(_zoom * zoomDelta, 0.5f, 10f);

        // Zoom toward mouse position (approximate)
        // For simplicity, we zoom toward center
        _zoom = newZoom;
        
        // Use debounced render for smooth zooming
        _cachedViewBox = null; // Invalidate viewbox cache
        RequestRender();
    }

    private void ZoomIn()
    {
        _zoom = Math.Min(_zoom * 1.25f, 10f);
        StateHasChanged();
    }

    private void ZoomOut()
    {
        _zoom = Math.Max(_zoom * 0.8f, 0.5f);
        StateHasChanged();
    }

    private void ResetView()
    {
        _zoom = 1.0f;
        _panX = 0;
        _panY = 0;
        StateHasChanged();
    }

    #endregion

    #region Junction Interaction

    private void OnJunctionClick(NetworkJunction junction)
    {
        if (_selectedJunctionId == junction.JunctionId)
        {
            // Double-click behavior: toggle exclusion
            ToggleJunctionExclusion(junction.JunctionId);
        }
        else
        {
            // Select the junction and find its cluster
            _selectedJunctionId = junction.JunctionId;
            _selectedJunction = junction;
            _selectedCluster = FindJunctionCluster(junction);
        }
        StateHasChanged();
    }

    /// <summary>
    /// Finds all junctions that are clustered (overlapping) with the given junction.
    /// Junctions within ClusterRadiusMeters of each other are considered a cluster.
    /// </summary>
    private List<NetworkJunction> FindJunctionCluster(NetworkJunction centerJunction)
    {
        if (AnalysisState?.Network == null) return [centerJunction];

        var cluster = new List<NetworkJunction>();
        var processed = new HashSet<int>();
        var queue = new Queue<NetworkJunction>();
        
        queue.Enqueue(centerJunction);
        processed.Add(centerJunction.JunctionId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            cluster.Add(current);

            // Find all junctions within cluster radius
            foreach (var other in AnalysisState.Network.Junctions)
            {
                if (processed.Contains(other.JunctionId)) continue;

                var distance = Vector2.Distance(current.Position, other.Position);
                if (distance <= ClusterRadiusMeters)
                {
                    processed.Add(other.JunctionId);
                    queue.Enqueue(other);
                }
            }
        }

        // Sort by junction ID for consistent display order
        return cluster.OrderBy(j => j.JunctionId).ToList();
    }

    private void OnJunctionHover(int junctionId)
    {
        if (_hoveredJunctionId != junctionId)
        {
            _hoveredJunctionId = junctionId;
            // Hover doesn't need debouncing but we avoid unnecessary re-renders
            StateHasChanged();
        }
    }

    private void OnJunctionHoverEnd()
    {
        if (_hoveredJunctionId != null)
        {
            _hoveredJunctionId = null;
            StateHasChanged();
        }
    }

    private void ToggleJunctionExclusion(int junctionId)
    {
        if (AnalysisState == null) return;

        AnalysisState.ToggleJunctionExclusion(junctionId);
        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private void ToggleSelectedJunctionExclusion(bool exclude)
    {
        if (AnalysisState == null || _selectedJunctionId == null) return;

        if (exclude)
        {
            AnalysisState.ExcludeJunction(_selectedJunctionId.Value, "User excluded");
        }
        else
        {
            AnalysisState.IncludeJunction(_selectedJunctionId.Value);
        }

        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private void ToggleClusterJunctionExclusion(int junctionId, bool exclude)
    {
        if (AnalysisState == null) return;

        if (exclude)
        {
            AnalysisState.ExcludeJunction(junctionId, "User excluded");
        }
        else
        {
            AnalysisState.IncludeJunction(junctionId);
        }

        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private void ExcludeAllInCluster()
    {
        if (AnalysisState == null || _selectedCluster.Count == 0) return;

        foreach (var junction in _selectedCluster)
        {
            AnalysisState.ExcludeJunction(junction.JunctionId, "User excluded (cluster)");
        }

        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private void IncludeAllInCluster()
    {
        if (AnalysisState == null || _selectedCluster.Count == 0) return;

        foreach (var junction in _selectedCluster)
        {
            AnalysisState.IncludeJunction(junction.JunctionId);
        }

        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private void ClearAllExclusions()
    {
        if (AnalysisState == null) return;

        AnalysisState.ClearAllExclusions();
        NotifyExclusionsChanged();
        StateHasChanged();
    }

    private async void NotifyExclusionsChanged()
    {
        if (AnalysisState != null && OnExclusionsChanged.HasDelegate)
        {
            await OnExclusionsChanged.InvokeAsync(AnalysisState.ExcludedJunctionIds);
        }
    }

    #endregion
}
