namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase B.1 — AASHTO K-value lookup for parabolic vertical curve length.
///     Source: roadmap §B; values derived from the 2018 AASHTO Green Book
///     stopping-sight-distance tables. The primary API is speed-keyed
///     (<see cref="GetKFromSpeed" />) with linear interpolation between table
///     rows; OSM road type and material <c>DesignSpeedKmh</c> are mapped to a
///     design speed by <see cref="ResolveDesignSpeed" /> with OSM-first
///     precedence. Used as a CEILING in
///     <see cref="UnifiedJunctionProfileBlender" />.<c>CalculateAdaptiveBlendDistance</c>;
///     never extends a shorter adaptive distance.
/// </summary>
public static class AashtoKValueTable
{
    private record struct KRow(int SpeedKmh, float KSag, float KCrest);

    // Ordered by ascending speed for interpolation.
    private static readonly KRow[] Rows = new[]
    {
        new KRow(30, 4f, 3f),
        new KRow(50, 15f, 10f),
        new KRow(80, 32f, 30f),
        new KRow(100, 45f, 50f),
        new KRow(120, 57f, 95f),
    };

    /// <summary>
    ///     Speed-keyed K lookup with linear interpolation. Speeds below 30 clamp
    ///     to residential; above 120 clamp to motorway.
    /// </summary>
    public static float GetKFromSpeed(int speedKmh, bool isSag)
    {
        if (speedKmh <= Rows[0].SpeedKmh)
            return isSag ? Rows[0].KSag : Rows[0].KCrest;
        if (speedKmh >= Rows[^1].SpeedKmh)
            return isSag ? Rows[^1].KSag : Rows[^1].KCrest;

        for (var i = 0; i < Rows.Length - 1; i++)
        {
            var lo = Rows[i];
            var hi = Rows[i + 1];
            if (speedKmh >= lo.SpeedKmh && speedKmh <= hi.SpeedKmh)
            {
                var t = (float)(speedKmh - lo.SpeedKmh) / (hi.SpeedKmh - lo.SpeedKmh);
                var kLo = isSag ? lo.KSag : lo.KCrest;
                var kHi = isSag ? hi.KSag : hi.KCrest;
                return kLo + t * (kHi - kLo);
            }
        }
        return isSag ? Rows[0].KSag : Rows[0].KCrest;
    }

    /// <summary>
    ///     OSM-type wrapper. Returns the K value for the design speed implied by
    ///     the OSM road class. Null / empty / unknown types fall back to residential.
    /// </summary>
    public static float GetKFromOsmRoadType(string? osmRoadType, bool isSag)
    {
        var speed = OsmRoadTypeToSpeed(osmRoadType) ?? 30;
        return GetKFromSpeed(speed, isSag);
    }

    /// <summary>
    ///     Encodes the OSM-first precedence rule:
    ///     1. OSM road type if present;
    ///     2. material <c>DesignSpeedKmh</c> override if no OSM data;
    ///     3. residential default (30 km/h).
    /// </summary>
    public static int ResolveDesignSpeed(string? osmRoadType, int? materialOverrideKmh)
    {
        var osmSpeed = OsmRoadTypeToSpeed(osmRoadType);
        if (osmSpeed.HasValue) return osmSpeed.Value;
        if (materialOverrideKmh.HasValue) return materialOverrideKmh.Value;
        return 30;
    }

    /// <summary>
    ///     Computes the K-derived L_cap for a single blend end. Returns
    ///     <see cref="float.PositiveInfinity" /> when no vertical curve is
    ///     geometrically required, so callers can safely take
    ///     <c>MathF.Min(adaptive, cap)</c>.
    /// </summary>
    public static float ComputeCap(
        int speedKmh,
        float zJunction, float mJunction,
        float zNaturalAtL, float blendLength)
    {
        if (blendLength <= 0.01f) return float.PositiveInfinity;

        var chordGrade = (zNaturalAtL - zJunction) / blendLength;
        var algebraicDiff = chordGrade - mJunction;
        if (MathF.Abs(algebraicDiff) < 0.0001f) return float.PositiveInfinity;

        var isSag = algebraicDiff > 0f;
        var k = GetKFromSpeed(speedKmh, isSag);
        var aPercent = MathF.Abs(algebraicDiff) * 100f;
        return k * aPercent;
    }

    private static int? OsmRoadTypeToSpeed(string? osmRoadType)
    {
        if (string.IsNullOrWhiteSpace(osmRoadType)) return null;
        return osmRoadType.ToLowerInvariant() switch
        {
            "motorway" or "motorway_link" => 120,
            "trunk" or "trunk_link" => 100,
            "primary" or "primary_link" => 80,
            "secondary" or "secondary_link" or "tertiary" or "tertiary_link" => 50,
            "residential" or "unclassified" or "service" or "living_street"
                or "track" or "path" or "footway" or "cycleway" or "pedestrian" or "steps"
                or "busway" or "raceway" => 30,
            _ => null
        };
    }
}
