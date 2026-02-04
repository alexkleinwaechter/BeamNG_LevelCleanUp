using System.Globalization;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class OsmMapTileBackground
{
    private const int OsmTileSize = 256;
    private const float ZoomStep = 0.15f; // Zoom change per scroll wheel tick
    private bool _isPanning;
    private float _panStartViewCenterX;
    private float _panStartViewCenterY;
    private double _panStartX;
    private double _panStartY;

    /// <summary>
    ///     The geographic bounding box to display tiles for.
    /// </summary>
    [Parameter]
    public GeoBoundingBox? BoundingBox { get; set; }

    /// <summary>
    ///     Display width in pixels.
    /// </summary>
    [Parameter]
    public int DisplayWidth { get; set; }

    /// <summary>
    ///     Display height in pixels.
    /// </summary>
    [Parameter]
    public int DisplayHeight { get; set; }

    /// <summary>
    ///     Whether the map tiles are visible.
    /// </summary>
    [Parameter]
    public bool IsVisible { get; set; } = true;

    /// <summary>
    ///     Opacity of the tiles (0.0 to 1.0).
    /// </summary>
    [Parameter]
    public float Opacity { get; set; } = 0.85f;

    /// <summary>
    ///     Whether to show the OpenStreetMap attribution.
    /// </summary>
    [Parameter]
    public bool ShowAttribution { get; set; } = true;

    /// <summary>
    ///     Position of the attribution text: "top-left", "top-right", "bottom-left", "bottom-right".
    /// </summary>
    [Parameter]
    public string AttributionPosition { get; set; } = "bottom-right";

    /// <summary>
    ///     Maximum number of tiles to load (to prevent performance issues).
    /// </summary>
    [Parameter]
    public int MaxTiles { get; set; } = 25;

    /// <summary>
    ///     Z-index for the tile container.
    /// </summary>
    [Parameter]
    public int ZIndex { get; set; }

    /// <summary>
    ///     Zoom multiplier. 1.0 = fit entire bounding box, 2.0 = 2x zoom (half area visible), etc.
    /// </summary>
    [Parameter]
    public float ZoomLevel { get; set; } = 1.0f;

    /// <summary>
    ///     Callback when zoom level changes (e.g., via scroll wheel).
    /// </summary>
    [Parameter]
    public EventCallback<float> ZoomLevelChanged { get; set; }

    /// <summary>
    ///     Center of the view within the bounding box (normalized 0-1 coordinates).
    ///     (0.5, 0.5) = center of bounding box.
    /// </summary>
    [Parameter]
    public (float X, float Y) ViewCenter { get; set; } = (0.5f, 0.5f);

    /// <summary>
    ///     Callback when view center changes (e.g., via panning).
    /// </summary>
    [Parameter]
    public EventCallback<(float X, float Y)> ViewCenterChanged { get; set; }

    /// <summary>
    ///     Whether scroll wheel zoom is enabled.
    /// </summary>
    [Parameter]
    public bool EnableScrollZoom { get; set; }

    /// <summary>
    ///     Minimum zoom level (1.0 = fit all).
    /// </summary>
    [Parameter]
    public float MinZoom { get; set; } = 1.0f;

    /// <summary>
    ///     Maximum zoom level.
    /// </summary>
    [Parameter]
    public float MaxZoom { get; set; } = 10.0f;

    /// <summary>
    ///     Whether click-and-drag panning is enabled (only applies when zoomed in).
    ///     Set to false to disable panning while keeping scroll zoom.
    /// </summary>
    [Parameter]
    public bool EnablePanning { get; set; } = true;

    /// <summary>
    ///     Callback when the effective bounding box changes (zoom/pan).
    /// </summary>
    [Parameter]
    public EventCallback<GeoBoundingBox?> EffectiveBoundingBoxChanged { get; set; }

    /// <summary>
    ///     The effective bounding box being displayed (subset of original based on zoom/pan).
    ///     This is computed from BoundingBox, ZoomLevel, and ViewCenter.
    /// </summary>
    public GeoBoundingBox? EffectiveBoundingBox => CalculateEffectiveBoundingBox();

    private string GetContainerStyle()
    {
        return $"width: {DisplayWidth}px; height: {DisplayHeight}px; z-index: {ZIndex};";
    }

    private string GetAttributionStyle()
    {
        return AttributionPosition switch
        {
            "top-left" => "top: 2px; left: 2px;",
            "top-right" => "top: 2px; right: 2px;",
            "bottom-left" => "bottom: 2px; left: 2px;",
            "bottom-right" => "bottom: 2px; right: 2px;",
            _ => "bottom: 2px; right: 2px;"
        };
    }

    private async Task HandleWheel(WheelEventArgs e)
    {
        if (!EnableScrollZoom || BoundingBox == null)
            return;

        // Calculate new zoom level based on scroll direction
        var zoomDelta = e.DeltaY > 0 ? -ZoomStep : ZoomStep;
        var newZoom = Math.Clamp(ZoomLevel + zoomDelta * ZoomLevel, MinZoom, MaxZoom);

        if (Math.Abs(newZoom - ZoomLevel) > 0.001f)
        {
            // Calculate mouse position in normalized coordinates (0-1)
            var mouseNormX = (float)(e.OffsetX / DisplayWidth);
            var mouseNormY = (float)(e.OffsetY / DisplayHeight);

            // Zoom toward mouse position for more intuitive zooming
            if (newZoom > ZoomLevel)
            {
                // Zooming in: shift view center toward mouse position
                var shiftFactor = 0.1f; // How much to shift toward mouse
                var newCenterX = ViewCenter.X + (mouseNormX - 0.5f) * shiftFactor * (1.0f - 1.0f / newZoom);
                var newCenterY = ViewCenter.Y + (mouseNormY - 0.5f) * shiftFactor * (1.0f - 1.0f / newZoom);

                var clampedCenter = ClampViewCenter(newCenterX, newCenterY, newZoom);
                await UpdateViewCenter(clampedCenter);
            }
            else if (newZoom <= MinZoom + 0.01f)
            {
                // Zooming out to minimum: reset to center
                await UpdateViewCenter((0.5f, 0.5f));
            }

            await UpdateZoomLevel(newZoom);
        }
    }

    private void HandleMouseDown(MouseEventArgs e)
    {
        if (!EnableScrollZoom || !EnablePanning || ZoomLevel <= MinZoom + 0.01f)
            return;

        _isPanning = true;
        _panStartX = e.ClientX;
        _panStartY = e.ClientY;
        _panStartViewCenterX = ViewCenter.X;
        _panStartViewCenterY = ViewCenter.Y;
    }

    private async Task HandleMouseMove(MouseEventArgs e)
    {
        if (!_isPanning || !EnableScrollZoom || !EnablePanning)
            return;

        var deltaX = e.ClientX - _panStartX;
        var deltaY = e.ClientY - _panStartY;

        // Convert pixel delta to normalized coordinate delta
        // The visible area is 1/ZoomLevel of the total, so pan speed scales with zoom
        // X: dragging right (positive deltaX) should decrease ViewCenter.X (show content to the left)
        // Y: dragging down (positive deltaY) should INCREASE ViewCenter.Y (show content above/north)
        //    because ViewCenter.Y=0 is south (MinLatitude) and ViewCenter.Y=1 is north (MaxLatitude)
        var normalizedDeltaX = (float)(-deltaX / DisplayWidth / ZoomLevel);
        var normalizedDeltaY = (float)(deltaY / DisplayHeight / ZoomLevel);

        var newCenterX = _panStartViewCenterX + normalizedDeltaX;
        var newCenterY = _panStartViewCenterY + normalizedDeltaY;

        var clampedCenter = ClampViewCenter(newCenterX, newCenterY, ZoomLevel);
        await UpdateViewCenter(clampedCenter);
    }

    private void HandleMouseUp(MouseEventArgs e)
    {
        _isPanning = false;
    }

    private void HandleMouseLeave(MouseEventArgs e)
    {
        _isPanning = false;
    }

    private async Task UpdateZoomLevel(float newZoom)
    {
        ZoomLevel = newZoom;
        await ZoomLevelChanged.InvokeAsync(newZoom);
        await NotifyEffectiveBoundingBoxChanged();
    }

    private async Task UpdateViewCenter((float X, float Y) newCenter)
    {
        ViewCenter = newCenter;
        await ViewCenterChanged.InvokeAsync(newCenter);
        await NotifyEffectiveBoundingBoxChanged();
    }

    private async Task NotifyEffectiveBoundingBoxChanged()
    {
        if (EffectiveBoundingBoxChanged.HasDelegate)
            await EffectiveBoundingBoxChanged.InvokeAsync(EffectiveBoundingBox);
    }

    /// <summary>
    ///     Clamps the view center so the effective bounding box stays within the original bounds.
    /// </summary>
    private (float X, float Y) ClampViewCenter(float centerX, float centerY, float zoom)
    {
        if (zoom <= MinZoom + 0.01f)
            return (0.5f, 0.5f);

        // The visible portion is 1/zoom of the total
        var halfVisibleWidth = 0.5f / zoom;
        var halfVisibleHeight = 0.5f / zoom;

        // Clamp so the visible rectangle stays within 0-1 range
        var clampedX = Math.Clamp(centerX, halfVisibleWidth, 1.0f - halfVisibleWidth);
        var clampedY = Math.Clamp(centerY, halfVisibleHeight, 1.0f - halfVisibleHeight);

        return (clampedX, clampedY);
    }

    /// <summary>
    ///     Calculates the effective bounding box based on zoom level and view center.
    /// </summary>
    private GeoBoundingBox? CalculateEffectiveBoundingBox()
    {
        if (BoundingBox == null || ZoomLevel <= MinZoom + 0.01f)
            return BoundingBox;

        var originalWidth = BoundingBox.MaxLongitude - BoundingBox.MinLongitude;
        var originalHeight = BoundingBox.MaxLatitude - BoundingBox.MinLatitude;

        // Calculate visible size based on zoom
        var visibleWidth = originalWidth / ZoomLevel;
        var visibleHeight = originalHeight / ZoomLevel;

        // Calculate center in geographic coordinates
        var centerLon = BoundingBox.MinLongitude + originalWidth * ViewCenter.X;
        var centerLat = BoundingBox.MinLatitude + originalHeight * ViewCenter.Y;

        // Calculate new bounds centered on ViewCenter
        var newMinLon = centerLon - visibleWidth / 2;
        var newMaxLon = centerLon + visibleWidth / 2;
        var newMinLat = centerLat - visibleHeight / 2;
        var newMaxLat = centerLat + visibleHeight / 2;

        // Clamp to original bounds
        if (newMinLon < BoundingBox.MinLongitude)
        {
            newMaxLon += BoundingBox.MinLongitude - newMinLon;
            newMinLon = BoundingBox.MinLongitude;
        }

        if (newMaxLon > BoundingBox.MaxLongitude)
        {
            newMinLon -= newMaxLon - BoundingBox.MaxLongitude;
            newMaxLon = BoundingBox.MaxLongitude;
        }

        if (newMinLat < BoundingBox.MinLatitude)
        {
            newMaxLat += BoundingBox.MinLatitude - newMinLat;
            newMinLat = BoundingBox.MinLatitude;
        }

        if (newMaxLat > BoundingBox.MaxLatitude)
        {
            newMinLat -= newMaxLat - BoundingBox.MaxLatitude;
            newMaxLat = BoundingBox.MaxLatitude;
        }

        // Final clamp to ensure we don't exceed original bounds
        newMinLon = Math.Max(newMinLon, BoundingBox.MinLongitude);
        newMaxLon = Math.Min(newMaxLon, BoundingBox.MaxLongitude);
        newMinLat = Math.Max(newMinLat, BoundingBox.MinLatitude);
        newMaxLat = Math.Min(newMaxLat, BoundingBox.MaxLatitude);

        return new GeoBoundingBox(newMinLon, newMinLat, newMaxLon, newMaxLat);
    }

    /// <summary>
    ///     Resets zoom to fit the entire bounding box.
    /// </summary>
    public async Task ResetZoom()
    {
        await UpdateZoomLevel(MinZoom);
        await UpdateViewCenter((0.5f, 0.5f));
    }

    /// <summary>
    ///     Gets the list of OSM tiles needed to cover the bounding box.
    ///     Uses EffectiveBoundingBox when zoomed.
    /// </summary>
    private List<OsmTileInfo> GetVisibleTiles()
    {
        var tiles = new List<OsmTileInfo>();

        if (BoundingBox == null || DisplayWidth <= 0 || DisplayHeight <= 0)
            return tiles;

        // Use effective bounding box (respects zoom/pan) for tile calculation
        var bbox = EffectiveBoundingBox ?? BoundingBox;

        // Calculate optimal zoom level based on the bounding box size and display size
        var zoom = CalculateOptimalZoom(bbox, DisplayWidth, DisplayHeight);

        // Clamp zoom to reasonable levels
        zoom = Math.Clamp(zoom, 1, 18);

        // Get tile coordinates for the bounding box corners
        var (minTileX, minTileY) = LatLonToTile(bbox.MaxLatitude, bbox.MinLongitude, zoom);
        var (maxTileX, maxTileY) = LatLonToTile(bbox.MinLatitude, bbox.MaxLongitude, zoom);

        // Limit the number of tiles to prevent performance issues
        var tileCountX = maxTileX - minTileX + 1;
        var tileCountY = maxTileY - minTileY + 1;

        if (tileCountX * tileCountY > MaxTiles)
        {
            // Too many tiles, reduce zoom level
            zoom = Math.Max(1, zoom - 2);
            (minTileX, minTileY) = LatLonToTile(bbox.MaxLatitude, bbox.MinLongitude, zoom);
            (maxTileX, maxTileY) = LatLonToTile(bbox.MinLatitude, bbox.MaxLongitude, zoom);
        }

        // Calculate how the tile grid maps to display pixels
        var bboxLonRange = bbox.MaxLongitude - bbox.MinLongitude;
        var bboxLatRange = bbox.MaxLatitude - bbox.MinLatitude;

        for (var tileY = minTileY; tileY <= maxTileY; tileY++)
        for (var tileX = minTileX; tileX <= maxTileX; tileX++)
        {
            // Get the geographic bounds of this tile
            var (tileSouthLat, tileWestLon) = TileToLatLon(tileX, tileY + 1, zoom);
            var (tileNorthLat, tileEastLon) = TileToLatLon(tileX + 1, tileY, zoom);

            // Calculate position in display pixels relative to the container
            var leftPx = (tileWestLon - bbox.MinLongitude) / bboxLonRange * DisplayWidth;
            var topPx = (bbox.MaxLatitude - tileNorthLat) / bboxLatRange * DisplayHeight;
            var tileLonSpan = tileEastLon - tileWestLon;
            var tileLatSpan = tileNorthLat - tileSouthLat;
            var widthPx = tileLonSpan / bboxLonRange * DisplayWidth;
            var heightPx = tileLatSpan / bboxLatRange * DisplayHeight;

            // Build tile URL (using OpenStreetMap tile server)
            var url = $"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png";

            // Build CSS style for positioning
            var style = string.Format(CultureInfo.InvariantCulture,
                "left: {0:F1}px; top: {1:F1}px; width: {2:F1}px; height: {3:F1}px; opacity: {4:F2};",
                leftPx, topPx, widthPx, heightPx, Opacity);

            tiles.Add(new OsmTileInfo(url, style));
        }

        return tiles;
    }

    /// <summary>
    ///     Calculates the optimal zoom level to fit the bounding box in the display area.
    /// </summary>
    private static int CalculateOptimalZoom(GeoBoundingBox bbox, int displayWidth, int displayHeight)
    {
        var lonSpan = bbox.MaxLongitude - bbox.MinLongitude;
        var latSpan = bbox.MaxLatitude - bbox.MinLatitude;

        // Calculate zoom for longitude
        var zoomLon = Math.Log2(360.0 / lonSpan * displayWidth / OsmTileSize);

        // Calculate zoom for latitude (using Mercator projection approximation)
        var centerLat = (bbox.MinLatitude + bbox.MaxLatitude) / 2.0;
        var latRadians = centerLat * Math.PI / 180.0;
        var mercatorLatSpan = Math.Log(Math.Tan(Math.PI / 4 + latRadians / 2)) -
                              Math.Log(Math.Tan(Math.PI / 4 + (centerLat - latSpan / 2) * Math.PI / 180.0 / 2));
        var worldMercatorHeight = 2 * Math.PI;
        var zoomLat = Math.Log2(worldMercatorHeight / Math.Abs(mercatorLatSpan) * displayHeight / OsmTileSize);

        return (int)Math.Floor(Math.Min(zoomLon, zoomLat));
    }

    /// <summary>
    ///     Converts latitude/longitude to OSM tile coordinates.
    /// </summary>
    private static (int tileX, int tileY) LatLonToTile(double lat, double lon, int zoom)
    {
        var n = Math.Pow(2, zoom);
        var tileX = (int)Math.Floor((lon + 180.0) / 360.0 * n);
        var latRad = lat * Math.PI / 180.0;
        var tileY = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);

        tileX = Math.Clamp(tileX, 0, (int)n - 1);
        tileY = Math.Clamp(tileY, 0, (int)n - 1);

        return (tileX, tileY);
    }

    /// <summary>
    ///     Converts OSM tile coordinates to latitude/longitude (northwest corner of tile).
    /// </summary>
    private static (double lat, double lon) TileToLatLon(int tileX, int tileY, int zoom)
    {
        var n = Math.Pow(2, zoom);
        var lon = tileX / n * 360.0 - 180.0;
        var latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
        var lat = latRad * 180.0 / Math.PI;
        return (lat, lon);
    }

    /// <summary>
    ///     Represents a single OSM tile to render.
    /// </summary>
    private record OsmTileInfo(string Url, string Style);
}