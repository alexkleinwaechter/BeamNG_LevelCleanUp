using System.Globalization;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using Microsoft.AspNetCore.Components;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class OsmFeaturePreview
{
    private GeoBoundingBox? _effectiveBoundingBox;
    private float _mapOpacity = 0.7f;
    private ElementReference _previewSquare;
    private GeoBoundingBox? _previousBoundingBox;
    private (float X, float Y) _viewCenter = (0.5f, 0.5f);
    private float _zoomLevel = 1.0f;
    [Parameter] public IEnumerable<OsmFeature>? Features { get; set; }
    [Parameter] public GeoBoundingBox? BoundingBox { get; set; }
    [Parameter] public int TerrainSize { get; set; } = 512;

    /// <summary>
    ///     Whether to show the OSM map tile background. Defaults to true.
    /// </summary>
    [Parameter]
    public bool ShowMapBackground { get; set; } = true;

    /// <summary>
    ///     The pixel size for the preview. When set to 0 or less, uses default 420px.
    ///     This parameter allows the parent to control the preview size responsively.
    /// </summary>
    [Parameter]
    public int PreviewPixelSize { get; set; } = 0;

    /// <summary>
    ///     Effective pixel size - uses parameter if provided, otherwise default.
    /// </summary>
    private int EffectivePreviewPixelSize => PreviewPixelSize > 0 ? PreviewPixelSize : 420;

    protected override void OnParametersSet()
    {
        // Reset zoom when bounding box changes (new area selected)
        if (!ReferenceEquals(BoundingBox, _previousBoundingBox))
        {
            _previousBoundingBox = BoundingBox;
            ResetZoomState();
        }
    }

    #region Zoom Event Handlers

    /// <summary>
    ///     Called when zoom level changes from the OsmMapTileBackground component.
    /// </summary>
    private void OnZoomLevelChanged(float newZoom)
    {
        _zoomLevel = newZoom;
        StateHasChanged();
    }

    /// <summary>
    ///     Called when view center changes (panning) from the OsmMapTileBackground component.
    /// </summary>
    private void OnViewCenterChanged((float X, float Y) newCenter)
    {
        _viewCenter = newCenter;
        StateHasChanged();
    }

    /// <summary>
    ///     Called when the effective bounding box changes from the OsmMapTileBackground component.
    ///     This is crucial for keeping SVG features aligned with the map tiles.
    /// </summary>
    private void OnEffectiveBoundingBoxChanged(GeoBoundingBox? bounds)
    {
        _effectiveBoundingBox = bounds;
        StateHasChanged();
    }

    /// <summary>
    ///     Resets zoom to show the full bounding box.
    /// </summary>
    private void ResetZoom()
    {
        ResetZoomState();
        StateHasChanged();
    }

    /// <summary>
    ///     Internal method to reset zoom state variables.
    /// </summary>
    private void ResetZoomState()
    {
        _zoomLevel = 1.0f;
        _viewCenter = (0.5f, 0.5f);
        _effectiveBoundingBox = null;
    }

    #endregion

    #region Coordinate Transformation

    /// <summary>
    ///     Transforms geographic coordinates to pixel coordinates for SVG rendering.
    ///     When zoomed, uses the EffectiveBoundingBox so features align with the zoomed map tiles.
    ///     Uses EffectivePreviewPixelSize for the output coordinate space.
    /// </summary>
    private List<(float X, float Y)> TransformToPixelWithZoom(List<GeoCoordinate> coords)
    {
        if (BoundingBox == null || coords == null || !coords.Any())
            return new List<(float, float)>();

        // Use effective bounding box when zoomed, otherwise use original
        var bbox = _effectiveBoundingBox ?? BoundingBox;

        var result = new List<(float X, float Y)>();

        // Calculate bounding box dimensions
        var bboxWidth = bbox.MaxLongitude - bbox.MinLongitude;
        var bboxHeight = bbox.MaxLatitude - bbox.MinLatitude;

        // Avoid division by zero
        if (Math.Abs(bboxWidth) < 1e-10 || Math.Abs(bboxHeight) < 1e-10)
            return result;

        foreach (var coord in coords)
        {
            // Normalize to 0-1 range within the effective bounding box
            var normalizedX = (coord.Longitude - bbox.MinLongitude) / bboxWidth;
            var normalizedY = (coord.Latitude - bbox.MinLatitude) / bboxHeight;

            // Convert to pixel coordinates using EffectivePreviewPixelSize
            // Y is inverted: SVG has Y increasing downward, but latitude increases upward
            var pixelX = (float)(normalizedX * EffectivePreviewPixelSize);
            var pixelY = (float)((1.0 - normalizedY) * EffectivePreviewPixelSize);

            // Allow features to extend beyond bounds (they'll be clipped by the SVG viewport)
            // Use larger margin for zoomed view to handle partial visibility
            var margin = _zoomLevel > 1.01f ? EffectivePreviewPixelSize : 50;
            pixelX = Math.Clamp(pixelX, -margin, EffectivePreviewPixelSize + margin);
            pixelY = Math.Clamp(pixelY, -margin, EffectivePreviewPixelSize + margin);

            result.Add((pixelX, pixelY));
        }

        return result;
    }

    /// <summary>
    ///     Legacy transformation method for backwards compatibility.
    ///     Now delegates to TransformToPixelWithZoom.
    /// </summary>
    private List<(float X, float Y)> TransformToPixel(List<GeoCoordinate> coords)
    {
        return TransformToPixelWithZoom(coords);
    }

    #endregion

    #region SVG Styling

    private string GetPointsString(List<(float X, float Y)> coords)
    {
        if (coords == null || coords.Count < 2)
            return string.Empty;

        // Use InvariantCulture to ensure decimal point (not comma) in SVG
        return string.Join(" ", coords.Select(c =>
            string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", c.X, c.Y)));
    }

    /// <summary>
    ///     Gets stroke width for polygon features, slightly increased when zoomed.
    /// </summary>
    private string GetStrokeWidth(OsmFeature feature)
    {
        // Base stroke width is 2, increase slightly when zoomed for better visibility
        var baseWidth = 2.0f;
        if (_zoomLevel > 2.0f)
            baseWidth = 1.5f; // Thinner when zoomed to show more detail
        return baseWidth.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Gets stroke width for line features (roads, railways, waterways).
    ///     Adjusts based on zoom level and whether map background is shown.
    /// </summary>
    private string GetLineStrokeWidth(OsmFeature feature)
    {
        var baseWidth = ShowMapBackground ? 3.0f : 2.0f;

        // Adjust for zoom - thinner lines when zoomed in to show more detail
        if (_zoomLevel > 2.0f)
            baseWidth = Math.Max(1.5f, baseWidth / (_zoomLevel * 0.5f));

        return baseWidth.ToString("F1", CultureInfo.InvariantCulture);
    }

    private string GetFillColor(OsmFeature feature)
    {
        return feature.Category switch
        {
            "landuse" => feature.SubCategory switch
            {
                "forest" => "#2d5a3d",
                "grass" => "#4a7c4e",
                "meadow" => "#5a8c5e",
                "farmland" => "#8b7355",
                "residential" => "#6a6a6a",
                "industrial" => "#5a5a5a",
                "commercial" => "#6a5a5a",
                _ => "#5a5a6a"
            },
            "natural" => feature.SubCategory switch
            {
                "wood" => "#2d5a3d",
                "water" => "#3d5a8d",
                "wetland" => "#4a6a7a",
                "grassland" => "#5a8c5e",
                "scrub" => "#5a7a5a",
                "sand" => "#c2b280",
                "rock" => "#7a7a7a",
                _ => "#5a6a5a"
            },
            "building" => "#7a5a5a",
            "waterway" => "#3d5a8d",
            _ => "#5a5a5a"
        };
    }

    private string GetStrokeColor(OsmFeature feature)
    {
        return feature.Category switch
        {
            "highway" => feature.SubCategory switch
            {
                "motorway" => "#e8a030",
                "trunk" => "#e8a030",
                "primary" => "#e8a030",
                "secondary" => "#f0d040",
                "tertiary" => "#ffffff",
                "residential" => "#cccccc",
                "service" => "#aaaaaa",
                "track" => "#8b7355",
                "path" => "#8b7355",
                "footway" => "#8b8b8b",
                _ => "#cccccc"
            },
            "railway" => "#8a8a8a",
            "waterway" => "#5080c0",
            "landuse" => "#4a6a4a",
            "natural" => "#4a6a5a",
            "building" => "#8a5a5a",
            _ => "#8a8a8a"
        };
    }

    #endregion
}