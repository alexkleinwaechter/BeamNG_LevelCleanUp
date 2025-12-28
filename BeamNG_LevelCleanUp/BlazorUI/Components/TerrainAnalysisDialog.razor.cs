using System.Globalization;
using System.Numerics;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;
using WheelEventArgs = Microsoft.AspNetCore.Components.Web.WheelEventArgs;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
/// Fullscreen dialog for terrain analysis results.
/// Displays detected splines and junctions with interactive SVG,
/// allowing users to exclude problematic junctions from harmonization.
/// </summary>
public partial class TerrainAnalysisDialog : ComponentBase, IDisposable
{
    [CascadingParameter]
    private IMudDialogInstance MudDialog { get; set; } = null!;

    /// <summary>
    /// The analysis state containing the road network and junction data.
    /// </summary>
    [Parameter]
    public TerrainAnalysisState? AnalysisState { get; set; }

    /// <summary>
    /// Meters per pixel for coordinate conversion.
    /// </summary>
    [Parameter]
    public float MetersPerPixel { get; set; } = 1.0f;

    // Element reference for the SVG container
    private ElementReference _svgContainerRef;

    // View state
    private float _zoom = 1.0f;
    private float _panX = 0f;
    private float _panY = 0f;
    private float _networkExtent = 1024f;

    // Interaction state
    private bool _isPanning;
    private double _lastMouseX;
    private double _lastMouseY;

    // Selection state
    private int? _selectedJunctionId;
    private int? _hoveredJunctionId;
    private NetworkJunction? _selectedJunction;

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

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        // Setup debounce timer
        _debounceTimer = new System.Timers.Timer(DebounceDelayMs);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // Calculate network extent from analysis data
        if (AnalysisState?.Network != null)
        {
            CalculateNetworkExtent();
            CacheSplineAndJunctionData();
        }

        // Update selected junction reference
        if (_selectedJunctionId.HasValue && AnalysisState != null)
        {
            _selectedJunction = AnalysisState.GetJunction(_selectedJunctionId.Value);
        }
        
        _cachedViewBox = null;
    }

    private void OnDebounceTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_renderPending)
        {
            _renderPending = false;
            InvokeAsync(StateHasChanged);
        }
    }

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

        if (AnalysisState.DebugImageWidth > 0)
        {
            _networkExtent = Math.Max(AnalysisState.DebugImageWidth, AnalysisState.DebugImageHeight);
        }
        else
        {
            float maxX = 0, maxY = 0;
            foreach (var cs in AnalysisState.Network.CrossSections)
            {
                var x = cs.CenterPoint.X / MetersPerPixel;
                var y = cs.CenterPoint.Y / MetersPerPixel;
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            _networkExtent = Math.Max(maxX, maxY) * 1.1f;
        }

        _networkExtent = Math.Max(_networkExtent, 100);
    }

    private void CacheSplineAndJunctionData()
    {
        if (AnalysisState?.Network == null) return;

        _cachedSplinePaths = new Dictionary<int, string>();
        foreach (var spline in AnalysisState.Network.Splines)
        {
            var crossSections = AnalysisState.Network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count >= 2)
            {
                _cachedSplinePaths[spline.SplineId] = GetSplinePathData(crossSections);
            }
        }

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
        if (_cachedViewBox != null && 
            Math.Abs(_lastZoomForViewBox - _zoom) < 0.001f &&
            Math.Abs(_lastPanXForViewBox - _panX) < 0.001f &&
            Math.Abs(_lastPanYForViewBox - _panY) < 0.001f)
        {
            return _cachedViewBox;
        }

        var size = _networkExtent / _zoom;
        _cachedViewBox = $"{_panX.ToString("F2", CultureInfo.InvariantCulture)} {_panY.ToString("F2", CultureInfo.InvariantCulture)} {size.ToString("F2", CultureInfo.InvariantCulture)} {size.ToString("F2", CultureInfo.InvariantCulture)}";
        _lastZoomForViewBox = _zoom;
        _lastPanXForViewBox = _panX;
        _lastPanYForViewBox = _panY;
        return _cachedViewBox;
    }

    private (Vector2 Pos, float Radius, string Color) GetCachedJunctionData(NetworkJunction junction)
    {
        if (_cachedJunctionData != null && _cachedJunctionData.TryGetValue(junction.JunctionId, out var data))
        {
            return data;
        }
        return (TransformToCanvas(junction.Position), GetJunctionRadius(junction.Type), GetJunctionColor(junction.Type));
    }

    private string? GetCachedSplinePath(int splineId)
    {
        return _cachedSplinePaths?.GetValueOrDefault(splineId);
    }

    private Vector2 TransformToCanvas(Vector2 worldPosition)
    {
        var x = worldPosition.X / MetersPerPixel;
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
            JunctionType.Endpoint => "#FFFF00",
            JunctionType.TJunction => "#00FFFF",
            JunctionType.YJunction => "#00FF00",
            JunctionType.CrossRoads => "#FF8000",
            JunctionType.Complex => "#FF00FF",
            JunctionType.MidSplineCrossing => "#FF4080",
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
        if (e.Button == 0)
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

            // For responsive SVG, we estimate canvas size based on typical fullscreen dimensions
            // The SVG uses width/height 100%, so we use a reasonable estimate
            var estimatedCanvasSize = 800f; // Approximate size for scaling
            var scaleFactor = _networkExtent / (_zoom * estimatedCanvasSize);
            _panX -= (float)(deltaX * scaleFactor);
            _panY -= (float)(deltaY * scaleFactor);

            // Calculate pan bounds
            var viewableSize = _networkExtent / _zoom;
            var maxPan = _networkExtent - viewableSize;
            
            _panX = Math.Clamp(_panX, 0, Math.Max(0, maxPan));
            _panY = Math.Clamp(_panY, 0, Math.Max(0, maxPan));

            _lastMouseX = e.ClientX;
            _lastMouseY = e.ClientY;

            _cachedViewBox = null;
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
        _zoom = Math.Clamp(_zoom * zoomDelta, 0.5f, 10f);
        
        _cachedViewBox = null;
        RequestRender();
    }

    private void ZoomIn()
    {
        _zoom = Math.Min(_zoom * 1.25f, 10f);
        _cachedViewBox = null;
        StateHasChanged();
    }

    private void ZoomOut()
    {
        _zoom = Math.Max(_zoom * 0.8f, 0.5f);
        _cachedViewBox = null;
        StateHasChanged();
    }

    private void ResetView()
    {
        _zoom = 1.0f;
        _panX = 0;
        _panY = 0;
        _cachedViewBox = null;
        StateHasChanged();
    }

    #endregion

    #region Junction Interaction

    private void OnJunctionClick(NetworkJunction junction)
    {
        if (_selectedJunctionId == junction.JunctionId)
        {
            ToggleJunctionExclusion(junction.JunctionId);
        }
        else
        {
            _selectedJunctionId = junction.JunctionId;
            _selectedJunction = junction;
        }
        StateHasChanged();
    }

    private void OnJunctionHover(int junctionId)
    {
        if (_hoveredJunctionId != junctionId)
        {
            _hoveredJunctionId = junctionId;
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
        AnalysisState?.ToggleJunctionExclusion(junctionId);
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

        StateHasChanged();
    }

    private void ClearAllExclusions()
    {
        AnalysisState?.ClearAllExclusions();
        StateHasChanged();
    }

    #endregion

    #region Dialog Actions

    private void Cancel()
    {
        MudDialog.Cancel();
    }

    private void ClearAndClose()
    {
        MudDialog.Close(MudBlazor.DialogResult.Cancel());
    }

    private void ApplyAndGenerate()
    {
        MudDialog.Close(MudBlazor.DialogResult.Ok(true));
    }

    #endregion
}
