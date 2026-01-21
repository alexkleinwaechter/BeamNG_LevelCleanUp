using SixLabors.ImageSharp.PixelFormats;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Provides consistent color mappings for junction type visualization in debug images and UI.
/// 
/// Color scheme rationale:
/// - Network junction types (geometric detection): Use warm/cool spectrum for road topology
/// - OSM junction types (semantic tags): Use distinct colors for traffic control features
/// </summary>
public static class JunctionTypeColors
{
    #region Network Junction Types (Geometric Detection)

    /// <summary>
    /// Gets the color for a network junction type (geometric detection).
    /// </summary>
    /// <param name="type">The junction type.</param>
    /// <returns>RGBA color for visualization.</returns>
    public static Rgba32 GetNetworkJunctionColor(JunctionType type)
    {
        return type switch
        {
            JunctionType.Endpoint => new Rgba32(255, 255, 0, 255),       // Yellow - dead ends
            JunctionType.TJunction => new Rgba32(0, 255, 255, 255),      // Cyan - T intersections
            JunctionType.YJunction => new Rgba32(0, 255, 0, 255),        // Green - Y merges
            JunctionType.CrossRoads => new Rgba32(255, 128, 0, 255),     // Orange - 4-way
            JunctionType.Complex => new Rgba32(255, 0, 255, 255),        // Magenta - 5+ way
            JunctionType.MidSplineCrossing => new Rgba32(255, 64, 128, 255), // Pink/Coral - grade crossings
            JunctionType.Roundabout => new Rgba32(128, 255, 128, 255),   // Light green - roundabout connections
            _ => new Rgba32(255, 255, 255, 255)                          // White - unknown
        };
    }

    /// <summary>
    /// Gets the hex color string for a network junction type (for SVG/CSS).
    /// </summary>
    public static string GetNetworkJunctionColorHex(JunctionType type)
    {
        return type switch
        {
            JunctionType.Endpoint => "#FFFF00",        // Yellow
            JunctionType.TJunction => "#00FFFF",       // Cyan
            JunctionType.YJunction => "#00FF00",       // Green
            JunctionType.CrossRoads => "#FF8000",      // Orange
            JunctionType.Complex => "#FF00FF",         // Magenta
            JunctionType.MidSplineCrossing => "#FF4080", // Pink/Coral
            JunctionType.Roundabout => "#80FF80",      // Light green
            _ => "#FFFFFF"                             // White
        };
    }

    #endregion

    #region OSM Junction Types (Semantic Tags)

    /// <summary>
    /// Gets the color for an OSM junction type (semantic detection from tags).
    /// 
    /// Color scheme:
    /// - Traffic control (red family): Signals, stops, give way
    /// - Highway interchanges (blue/purple): Motorway junctions
    /// - Geometric (yellow/orange): T-junction, crossroads, complex
    /// - Special features (green/cyan): Mini roundabout, turning circle, crossing
    /// </summary>
    /// <param name="type">The OSM junction type.</param>
    /// <returns>RGBA color for visualization.</returns>
    public static Rgba32 GetOsmJunctionColor(OsmJunctionType type)
    {
        return type switch
        {
            // === Traffic Control Features (Red family) ===
            OsmJunctionType.TrafficSignals => new Rgba32(255, 50, 50, 255),    // Bright red - stop for lights
            OsmJunctionType.Stop => new Rgba32(220, 20, 60, 255),              // Crimson - stop sign
            OsmJunctionType.GiveWay => new Rgba32(255, 140, 0, 255),           // Dark orange - yield

            // === Highway Interchanges (Blue/Purple family) ===
            OsmJunctionType.MotorwayJunction => new Rgba32(65, 105, 225, 255), // Royal blue - highway exits

            // === Special Features (Green/Cyan family) ===
            OsmJunctionType.MiniRoundabout => new Rgba32(50, 205, 50, 255),    // Lime green - small roundabout
            OsmJunctionType.TurningCircle => new Rgba32(0, 206, 209, 255),     // Dark turquoise - cul-de-sac
            OsmJunctionType.Crossing => new Rgba32(144, 238, 144, 255),        // Light green - pedestrian crossing

            // === Geometric Detection (Yellow/Orange family) ===
            OsmJunctionType.TJunction => new Rgba32(255, 215, 0, 255),         // Gold - 3-way
            OsmJunctionType.CrossRoads => new Rgba32(255, 165, 0, 255),        // Orange - 4-way
            OsmJunctionType.ComplexJunction => new Rgba32(255, 69, 0, 255),    // Orange-red - 5+ way

            // === Unknown ===
            OsmJunctionType.Unknown => new Rgba32(169, 169, 169, 255),         // Dark gray - unclassified
            _ => new Rgba32(128, 128, 128, 255)                                // Gray - default
        };
    }

    /// <summary>
    /// Gets the hex color string for an OSM junction type (for SVG/CSS).
    /// </summary>
    public static string GetOsmJunctionColorHex(OsmJunctionType type)
    {
        return type switch
        {
            // Traffic Control Features
            OsmJunctionType.TrafficSignals => "#FF3232",    // Bright red
            OsmJunctionType.Stop => "#DC143C",              // Crimson
            OsmJunctionType.GiveWay => "#FF8C00",           // Dark orange

            // Highway Interchanges
            OsmJunctionType.MotorwayJunction => "#4169E1",  // Royal blue

            // Special Features
            OsmJunctionType.MiniRoundabout => "#32CD32",    // Lime green
            OsmJunctionType.TurningCircle => "#00CED1",     // Dark turquoise
            OsmJunctionType.Crossing => "#90EE90",          // Light green

            // Geometric Detection
            OsmJunctionType.TJunction => "#FFD700",         // Gold
            OsmJunctionType.CrossRoads => "#FFA500",        // Orange
            OsmJunctionType.ComplexJunction => "#FF4500",   // Orange-red

            // Unknown
            OsmJunctionType.Unknown => "#A9A9A9",           // Dark gray
            _ => "#808080"                                  // Gray
        };
    }

    #endregion

    #region Display Helpers

    /// <summary>
    /// Gets the display radius for a network junction (smaller for endpoints, larger for complex).
    /// </summary>
    /// <param name="type">The junction type.</param>
    /// <returns>Radius in pixels for debug image rendering.</returns>
    public static int GetNetworkJunctionRadius(JunctionType type)
    {
        return type switch
        {
            JunctionType.Endpoint => 4,
            JunctionType.TJunction => 6,
            JunctionType.YJunction => 6,
            JunctionType.CrossRoads => 7,
            JunctionType.Complex => 8,
            JunctionType.MidSplineCrossing => 6,
            JunctionType.Roundabout => 8,
            _ => 5
        };
    }

    /// <summary>
    /// Gets the display radius for an OSM junction marker.
    /// Explicitly tagged junctions are larger than geometric ones.
    /// </summary>
    /// <param name="type">The OSM junction type.</param>
    /// <returns>Radius in pixels for debug image rendering.</returns>
    public static int GetOsmJunctionRadius(OsmJunctionType type)
    {
        return type switch
        {
            // Large markers for explicitly tagged features
            OsmJunctionType.MotorwayJunction => 10,
            OsmJunctionType.TrafficSignals => 8,
            OsmJunctionType.Stop => 7,
            OsmJunctionType.GiveWay => 7,
            OsmJunctionType.MiniRoundabout => 8,

            // Medium markers for geometric detection
            OsmJunctionType.TJunction => 6,
            OsmJunctionType.CrossRoads => 7,
            OsmJunctionType.ComplexJunction => 8,

            // Smaller markers for other features
            OsmJunctionType.TurningCircle => 5,
            OsmJunctionType.Crossing => 5,

            _ => 5
        };
    }

    /// <summary>
    /// Gets a human-readable name for an OSM junction type.
    /// </summary>
    public static string GetOsmJunctionDisplayName(OsmJunctionType type)
    {
        return type switch
        {
            OsmJunctionType.MotorwayJunction => "Motorway Exit",
            OsmJunctionType.TrafficSignals => "Traffic Lights",
            OsmJunctionType.Stop => "Stop Sign",
            OsmJunctionType.GiveWay => "Give Way",
            OsmJunctionType.MiniRoundabout => "Mini Roundabout",
            OsmJunctionType.TurningCircle => "Turning Circle",
            OsmJunctionType.Crossing => "Pedestrian Crossing",
            OsmJunctionType.TJunction => "T-Junction (OSM)",
            OsmJunctionType.CrossRoads => "Crossroads (OSM)",
            OsmJunctionType.ComplexJunction => "Complex (OSM)",
            OsmJunctionType.Unknown => "Unknown",
            _ => type.ToString()
        };
    }

    /// <summary>
    /// Gets a human-readable name for a network junction type.
    /// </summary>
    public static string GetNetworkJunctionDisplayName(JunctionType type)
    {
        return type switch
        {
            JunctionType.Endpoint => "Dead End",
            JunctionType.TJunction => "T-Junction",
            JunctionType.YJunction => "Y-Junction",
            JunctionType.CrossRoads => "Crossroads",
            JunctionType.Complex => "Complex Junction",
            JunctionType.MidSplineCrossing => "Grade Crossing",
            JunctionType.Roundabout => "Roundabout",
            _ => type.ToString()
        };
    }

    #endregion

    #region Composite Colors (Network + OSM)

    /// <summary>
    /// Gets the color for a network junction, considering its OSM hint if available.
    /// When an OSM hint exists, blends the network junction color with the OSM color
    /// to indicate that OSM data enhanced the detection.
    /// </summary>
    /// <param name="junction">The network junction with optional OSM hint.</param>
    /// <returns>RGBA color for visualization.</returns>
    public static Rgba32 GetCompositeJunctionColor(NetworkJunction junction)
    {
        var baseColor = GetNetworkJunctionColor(junction.Type);

        if (junction.OsmHint == null)
            return baseColor;

        // Blend network color with OSM color (70% network, 30% OSM)
        var osmColor = GetOsmJunctionColor(junction.OsmHint.Type);
        return BlendColors(baseColor, osmColor, 0.7f);
    }

    /// <summary>
    /// Gets the outer ring color for junctions with OSM hints.
    /// Used to draw a colored ring around junctions that have OSM data.
    /// </summary>
    /// <param name="junction">The network junction.</param>
    /// <returns>The OSM color if hint exists, null otherwise.</returns>
    public static Rgba32? GetOsmHintRingColor(NetworkJunction junction)
    {
        if (junction.OsmHint == null)
            return null;

        return GetOsmJunctionColor(junction.OsmHint.Type);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Blends two colors with the given ratio.
    /// </summary>
    /// <param name="color1">First color.</param>
    /// <param name="color2">Second color.</param>
    /// <param name="ratio">Ratio of first color (0.0 = all color2, 1.0 = all color1).</param>
    private static Rgba32 BlendColors(Rgba32 color1, Rgba32 color2, float ratio)
    {
        var invRatio = 1.0f - ratio;
        return new Rgba32(
            (byte)(color1.R * ratio + color2.R * invRatio),
            (byte)(color1.G * ratio + color2.G * invRatio),
            (byte)(color1.B * ratio + color2.B * invRatio),
            (byte)(color1.A * ratio + color2.A * invRatio)
        );
    }

    #endregion
}
