using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>Axis-aligned selection rect in source pixels — the backdrop box DTO used across UI/state.</summary>
public sealed record SelectionRect(int OffsetX, int OffsetY, int Width, int Height)
{
    public int Right => OffsetX + Width;
    public int Bottom => OffsetY + Height;
}

/// <summary>
///     Handle used to resize a <see cref="SelectionRect" /> by dragging. <see cref="Body" /> moves the whole
///     rect (both offsets, fixed size); the four edge handles move a single border; the four corner handles
///     move two borders at once.
/// </summary>
public enum BackdropHandle
{
    Body,
    N,
    S,
    E,
    W,
    NE,
    NW,
    SE,
    SW
}

/// <summary>
///     Pure selection math shared by CropAnchorSelector and CropAnchorSelectorDialog (spec §5 de-duplication).
///     No Blazor dependencies — every method is a pure function.
/// </summary>
public static class SelectionGeometry
{
    /// <summary>
    ///     Calculates how many source pixels we need to select based on:
    ///     - Target terrain size (e.g., 2048 px)
    ///     - Target meters per pixel (e.g., 1.0 m/px = 2048m terrain)
    ///     - Source native pixel size (e.g., 30 m/px)
    ///     Formula: selectionPixels = (targetSize * metersPerPixel) / nativePixelSize
    ///     Example: (2048 * 1.0) / 30 = 68 source pixels needed
    /// </summary>
    public static int CalculateSelectionSizePixels(int targetSize, float metersPerPixel,
        float nativePixelSizeMeters, int originalWidth, int originalHeight)
    {
        if (nativePixelSizeMeters <= 0)
            return targetSize; // Fallback to 1:1 if no native size

        // Calculate how many meters the target terrain represents
        var targetMeters = targetSize * metersPerPixel;

        // Calculate how many source pixels that corresponds to
        var selectionPixels = (int)Math.Ceiling(targetMeters / nativePixelSizeMeters);

        // Clamp to source dimensions
        return Math.Min(selectionPixels, Math.Min(originalWidth, originalHeight));
    }

    /// <summary>
    ///     Ensures a selection offset doesn't go out of bounds given the selection size and source dimensions.
    /// </summary>
    public static (int X, int Y) ClampOffsets(int offsetX, int offsetY, int selW, int selH,
        int originalWidth, int originalHeight)
    {
        var clampedX = Math.Max(0, Math.Min(offsetX, originalWidth - selW));
        var clampedY = Math.Max(0, Math.Min(offsetY, originalHeight - selH));
        return (clampedX, clampedY);
    }

    /// <summary>
    ///     Recalculates the geographic bounding box for a selection rect (in source pixels) against the
    ///     original image's bounding box. Returns null if there is no original bounding box or no valid
    ///     source dimensions.
    /// </summary>
    public static GeoBoundingBox? PixelRectToBoundingBox(GeoBoundingBox? original,
        int originalWidth, int originalHeight, int offsetX, int offsetY, int selW, int selH)
    {
        if (original is not { } bbox || originalWidth <= 0 || originalHeight <= 0)
            return null;

        // Calculate the fraction of the original image that we're selecting
        var leftFraction = (double)offsetX / originalWidth;
        var rightFraction = (double)(offsetX + selW) / originalWidth;
        var topFraction = (double)offsetY / originalHeight;
        var bottomFraction = (double)(offsetY + selH) / originalHeight;

        // Calculate new bounding box coordinates
        var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
        var latRange = bbox.MaxLatitude - bbox.MinLatitude;

        var newMinLon = bbox.MinLongitude + lonRange * leftFraction;
        var newMaxLon = bbox.MinLongitude + lonRange * rightFraction;
        // Latitude: top of image = max latitude, so we subtract from max
        var newMaxLat = bbox.MaxLatitude - latRange * topFraction;
        var newMinLat = bbox.MaxLatitude - latRange * bottomFraction;

        return new GeoBoundingBox(newMinLon, newMinLat, newMaxLon, newMaxLat);
    }

    /// <summary>
    ///     Converts a geographic bounding box back into a source-pixel rect against the original
    ///     image's bounding box — the inverse of <see cref="PixelRectToBoundingBox" />. Plain
    ///     rectangular linear interpolation (no rounding-trip clamping is applied here — callers pass
    ///     the result through <see cref="ClampBackdropRect" /> if containment must be enforced).
    ///     Mirrors the Y-axis inversion in <see cref="PixelRectToBoundingBox" /> (row 0 = north =
    ///     MaxLatitude) so the two functions round-trip. Returns null under the same preconditions as
    ///     <see cref="PixelRectToBoundingBox" /> (no original bounding box, invalid source dimensions,
    ///     or a degenerate original bbox with zero lon/lat range).
    /// </summary>
    public static SelectionRect? BoundingBoxToPixelRect(GeoBoundingBox? original,
        int originalWidth, int originalHeight, GeoBoundingBox target)
    {
        if (original is not { } bbox || originalWidth <= 0 || originalHeight <= 0)
            return null;

        var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
        var latRange = bbox.MaxLatitude - bbox.MinLatitude;

        if (lonRange <= 0 || latRange <= 0)
            return null;

        // Longitude increases left-to-right, same direction as pixel X.
        var leftFraction = (target.MinLongitude - bbox.MinLongitude) / lonRange;
        var rightFraction = (target.MaxLongitude - bbox.MinLongitude) / lonRange;

        // Latitude: top of image (pixel Y=0) = MaxLatitude — the same sign flip as
        // PixelRectToBoundingBox's "newMaxLat = MaxLatitude - latRange * topFraction".
        var topFraction = (bbox.MaxLatitude - target.MaxLatitude) / latRange;
        var bottomFraction = (bbox.MaxLatitude - target.MinLatitude) / latRange;

        var offsetX = (int)Math.Round(leftFraction * originalWidth);
        var offsetY = (int)Math.Round(topFraction * originalHeight);
        var right = (int)Math.Round(rightFraction * originalWidth);
        var bottom = (int)Math.Round(bottomFraction * originalHeight);

        return new SelectionRect(offsetX, offsetY, right - offsetX, bottom - offsetY);
    }

    /// <summary>
    ///     Calculates the on-screen rect (left/top/width/height, in display pixels) for a selection,
    ///     accounting for minimap/dialog zoom and pan state. Returns null when the selection rect
    ///     falls entirely outside the visible display area (the caller renders "display: none;" in that case).
    ///
    ///     COORDINATE SYSTEM NOTE:
    ///     - ViewCenter uses geographic convention: Y=0 is south (bottom), Y=1 is north (top)
    ///     - Pixel coordinates use screen convention: Y=0 is top, Y=OriginalHeight is bottom
    ///     - Therefore we must INVERT the Y axis when converting ViewCenter.Y to pixel coordinates
    /// </summary>
    public static (double Left, double Top, double Width, double Height)? ComputeBoxRect(
        int offsetX, int offsetY, int selW, int selH, double baseScale, float zoomLevel,
        (float X, float Y) viewCenter, int originalWidth, int originalHeight, int displayWidth, int displayHeight)
    {
        // If not zoomed, use simple calculation
        if (zoomLevel <= 1.01f)
        {
            var simpleDisplayWidth = Math.Max(10, (int)(selW * baseScale));
            var simpleDisplayHeight = Math.Max(10, (int)(selH * baseScale));
            var simpleDisplayLeft = (int)(offsetX * baseScale);
            var simpleDisplayTop = (int)(offsetY * baseScale);
            return (simpleDisplayLeft, simpleDisplayTop, simpleDisplayWidth, simpleDisplayHeight);
        }

        // When zoomed, calculate visible portion in source pixels
        var visibleSourceWidth = originalWidth / zoomLevel;
        var visibleSourceHeight = originalHeight / zoomLevel;

        // Calculate the center of visible area in source pixels
        // X: ViewCenter.X = 0 -> left (pixel 0), ViewCenter.X = 1 -> right (pixel OriginalWidth)
        var visibleCenterX = originalWidth * viewCenter.X;
        // Y: ViewCenter.Y = 0 -> south/bottom (pixel OriginalHeight), ViewCenter.Y = 1 -> north/top (pixel 0)
        // We need to INVERT the Y axis: pixelY = OriginalHeight * (1 - ViewCenter.Y)
        var visibleCenterY = originalHeight * (1.0f - viewCenter.Y);

        var visibleLeft = visibleCenterX - visibleSourceWidth / 2;
        var visibleTop = visibleCenterY - visibleSourceHeight / 2;

        // Calculate selection position relative to visible area
        var relativeLeft = offsetX - visibleLeft;
        var relativeTop = offsetY - visibleTop;

        // Scale from visible source area to display pixels
        var scaleX = displayWidth / visibleSourceWidth;
        var scaleY = displayHeight / visibleSourceHeight;

        var displayLeft = (int)(relativeLeft * scaleX);
        var displayTop = (int)(relativeTop * scaleY);
        var scaledWidth = Math.Max(10, (int)(selW * scaleX));
        var scaledHeight = Math.Max(10, (int)(selH * scaleY));

        // Check if selection is visible (intersects with display area)
        if (displayLeft + scaledWidth < 0 || displayLeft > displayWidth ||
            displayTop + scaledHeight < 0 || displayTop > displayHeight)
            return null; // Selection is outside visible area

        return (displayLeft, displayTop, scaledWidth, scaledHeight);
    }

    /// <summary>
    ///     Converts a box rect (as computed by <see cref="ComputeBoxRect" />) to an inline CSS style string.
    ///     The tuple's fields are always whole numbers by construction (every branch of
    ///     <see cref="ComputeBoxRect" /> builds them from <c>(int)</c> casts), so interpolating them here is
    ///     safe from locale decimal-separator surprises (e.g. no stray "1,5px" under a comma-decimal culture).
    /// </summary>
    public static string ToCssStyle((double Left, double Top, double Width, double Height)? rect)
    {
        if (rect is not { } r)
            return "display: none;";

        return $"width: {r.Width}px; height: {r.Height}px; left: {r.Left}px; top: {r.Top}px;";
    }

    /// <summary>
    ///     Converts a screen-pixel drag delta into a source-pixel delta, accounting for the base
    ///     display scale and the current zoom level.
    /// </summary>
    public static (int X, int Y) ScreenDeltaToSourceDelta(double deltaX, double deltaY, double baseScale, float zoomLevel)
    {
        var effectiveScale = baseScale * zoomLevel;
        var sourcePixelDeltaX = (int)(deltaX / effectiveScale);
        var sourcePixelDeltaY = (int)(deltaY / effectiveScale);
        return (sourcePixelDeltaX, sourcePixelDeltaY);
    }

    /// <summary>
    ///     Clamps a backdrop rect so it always contains <paramref name="terrainRect" /> and stays inside the
    ///     mosaic bounds <c>[0, originalWidth] x [0, originalHeight]</c>. Zero margin on a side is legal.
    ///     Rules: <c>OffsetX = clamp(OffsetX, 0, terrain.OffsetX)</c>,
    ///     <c>Width >= terrain.Right - OffsetX</c> (so <c>Right >= terrain.Right</c>), <c>Right &lt;= originalWidth</c>;
    ///     symmetric for Y.
    /// </summary>
    public static SelectionRect ClampBackdropRect(SelectionRect rect, SelectionRect terrainRect,
        int originalWidth, int originalHeight)
    {
        // The left/top edge may never sit to the right/below the terrain rect's own left/top edge —
        // that would fail containment. Zero margin (offset == terrain offset) is explicitly legal.
        var offsetX = Math.Clamp(rect.OffsetX, 0, Math.Max(0, terrainRect.OffsetX));
        var offsetY = Math.Clamp(rect.OffsetY, 0, Math.Max(0, terrainRect.OffsetY));

        // Width must be large enough that Right reaches at least terrain.Right (containment),
        // and small enough that Right never exceeds the mosaic bound.
        var minWidth = terrainRect.Right - offsetX;
        var maxWidth = originalWidth - offsetX;
        var width = Math.Clamp(rect.Width, minWidth, Math.Max(minWidth, maxWidth));

        var minHeight = terrainRect.Bottom - offsetY;
        var maxHeight = originalHeight - offsetY;
        var height = Math.Clamp(rect.Height, minHeight, Math.Max(minHeight, maxHeight));

        return new SelectionRect(offsetX, offsetY, width, height);
    }

    /// <summary>
    ///     Applies a drag delta (in source pixels) on a handle — or a body move — to <paramref name="start" />.
    ///     <see cref="BackdropHandle.Body" /> moves both offsets with the SIZE UNCHANGED: each offset is
    ///     clamped into the size-preserving interval that keeps containment + mosaic bounds, so the move is
    ///     squeezed (never grows the rect). Edge handles move exactly one border, clamped into that border's
    ///     own legal range, holding the opposite (anchor) border fixed; corner handles move two borders the
    ///     same way. Width/Height are always derived from the (possibly clamped) borders afterwards, so an
    ///     anchor border can never drift — see task-16 review fix: the previous implementation mutated the
    ///     rect first and re-derived Width/Height from the ALREADY-CLAMPED offset via
    ///     <see cref="ClampBackdropRect" />, which could GROW the rect instead of squeezing the move, and let
    ///     "anchored" borders on edge/corner handles drift.
    /// </summary>
    public static SelectionRect ResizeBackdropRect(SelectionRect start, BackdropHandle handle,
        int sourceDeltaX, int sourceDeltaY, SelectionRect terrainRect, int originalWidth, int originalHeight)
    {
        SelectionRect result;

        if (handle == BackdropHandle.Body)
        {
            // Body moves both offsets with the size unchanged. Clamp each offset directly into the
            // size-preserving interval — NOT via ClampBackdropRect, which re-derives Width/Height from
            // the (already-clamped) offset and would grow the rect instead of squeezing the move.
            var minOffsetX = Math.Max(0, terrainRect.Right - start.Width);
            var maxOffsetX = Math.Min(terrainRect.OffsetX, originalWidth - start.Width);
            var minOffsetY = Math.Max(0, terrainRect.Bottom - start.Height);
            var maxOffsetY = Math.Min(terrainRect.OffsetY, originalHeight - start.Height);

            if (minOffsetX > maxOffsetX || minOffsetY > maxOffsetY)
            {
                // Defensive: the start size can't legally hold this position at all anywhere
                // (shouldn't happen for an already-legal start rect). Fall back to the general
                // clamp so we at least return a legal rect instead of an empty/inverted interval.
                var moved = new SelectionRect(start.OffsetX + sourceDeltaX, start.OffsetY + sourceDeltaY,
                    start.Width, start.Height);
                return ClampBackdropRect(moved, terrainRect, originalWidth, originalHeight);
            }

            var offsetX = Math.Clamp(start.OffsetX + sourceDeltaX, minOffsetX, maxOffsetX);
            var offsetY = Math.Clamp(start.OffsetY + sourceDeltaY, minOffsetY, maxOffsetY);
            result = new SelectionRect(offsetX, offsetY, start.Width, start.Height);
        }
        else
        {
            // Edge/corner handles: move ONLY the dragged border(s), each clamped into its own legal
            // range (west/north lower-bounded at 0 and upper-bounded at the terrain's own
            // offset; east/south lower-bounded at the terrain's own far edge and upper-bounded at
            // the mosaic edge — the same per-axis rules ClampBackdropRect enforces, just applied to
            // the border being dragged instead of re-derived after the fact). The opposite
            // (anchor) border is held at its start value, and Width/Height are derived from the
            // resulting borders — so an anchor border can never move.
            var left = start.OffsetX;
            var right = start.Right;
            var top = start.OffsetY;
            var bottom = start.Bottom;

            var movesWest = handle is BackdropHandle.W or BackdropHandle.NW or BackdropHandle.SW;
            var movesEast = handle is BackdropHandle.E or BackdropHandle.NE or BackdropHandle.SE;
            var movesNorth = handle is BackdropHandle.N or BackdropHandle.NW or BackdropHandle.NE;
            var movesSouth = handle is BackdropHandle.S or BackdropHandle.SW or BackdropHandle.SE;

            if (movesWest)
                left = Math.Clamp(left + sourceDeltaX, 0, Math.Max(0, terrainRect.OffsetX));
            if (movesEast)
                right = Math.Clamp(right + sourceDeltaX, terrainRect.Right, Math.Max(terrainRect.Right, originalWidth));
            if (movesNorth)
                top = Math.Clamp(top + sourceDeltaY, 0, Math.Max(0, terrainRect.OffsetY));
            if (movesSouth)
                bottom = Math.Clamp(bottom + sourceDeltaY, terrainRect.Bottom, Math.Max(terrainRect.Bottom, originalHeight));

            result = new SelectionRect(left, top, right - left, bottom - top);
        }

        // Defensive final clamp: a no-op for the results computed above as long as `start` was
        // itself legal — every offset/border above was already clamped into exactly the interval
        // ClampBackdropRect would enforce for it (same 0/terrain-offset and terrain-far-edge/mosaic
        // bounds), so re-clamping cannot move anything further. Kept only as a safety net in case a
        // non-legal `start` is ever passed in.
        return ClampBackdropRect(result, terrainRect, originalWidth, originalHeight);
    }

    /// <summary>
    ///     Default backdrop rect when the feature is first enabled: the terrain rect inflated 25% per side
    ///     (50% total on each axis), clamped to the mosaic bounds.
    /// </summary>
    public static SelectionRect DefaultBackdropRect(SelectionRect terrainRect, int originalWidth, int originalHeight)
    {
        // Math.Round defaults to banker's rounding (round-half-to-even); irrelevant here in practice
        // since terrainRect.Width/Height * 0.25 lands exactly on .5 only for specific even/odd
        // combinations, and either rounding direction is off by at most half a pixel of margin.
        var marginX = (int)Math.Round(terrainRect.Width * 0.25);
        var marginY = (int)Math.Round(terrainRect.Height * 0.25);

        var inflated = new SelectionRect(
            terrainRect.OffsetX - marginX,
            terrainRect.OffsetY - marginY,
            terrainRect.Width + marginX * 2,
            terrainRect.Height + marginY * 2);

        return ClampBackdropRect(inflated, terrainRect, originalWidth, originalHeight);
    }
}
