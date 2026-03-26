namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

public class OsmLaneInfo
{
    public int TotalLanes { get; set; }
    public int LanesForward { get; set; }
    public int LanesBackward { get; set; }
    public int LanesBothWays { get; set; }
    public bool IsOneWay { get; set; }

    // Stored for future use
    public string? TurnLanesForward { get; set; }
    public string? TurnLanesBackward { get; set; }
    public string? MaxSpeed { get; set; }
    public string? Surface { get; set; }
    public string? BusLanes { get; set; }
    public string? HgvLanes { get; set; }
    public string? Access { get; set; }

    // Parsed from width=* and est_width=* tags
    public float? WidthMeters { get; set; }
    public float? EstWidthMeters { get; set; }

    public OsmLaneInfo Reversed() => new OsmLaneInfo
    {
        TotalLanes = TotalLanes,
        LanesForward = LanesBackward,
        LanesBackward = LanesForward,
        LanesBothWays = LanesBothWays,
        IsOneWay = IsOneWay,
        TurnLanesForward = TurnLanesBackward,
        TurnLanesBackward = TurnLanesForward,
        MaxSpeed = MaxSpeed,
        Surface = Surface,
        BusLanes = BusLanes,
        HgvLanes = HgvLanes,
        Access = Access,
        WidthMeters = WidthMeters,
        EstWidthMeters = EstWidthMeters,
    };

    /// <summary>
    /// Parses an OSM width value string with unit support.
    /// Supports: bare number (meters), "m", "km", "mi", "ft", feet'inches" formats.
    /// Normalizes Unicode quote characters (U+2019, U+2032, U+2033) to ASCII.
    /// </summary>
    public static bool TryParseWidth(string? value, out float meters)
    {
        meters = 0f;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Normalize Unicode quotes to ASCII
        var s = value.Trim()
            .Replace('\u2019', '\'')  // right single quotation mark
            .Replace('\u2032', '\'')  // prime
            .Replace('\u2033', '"')   // double prime
            .Replace("''", "\"");     // two single quotes → double quote

        // Try feet+inches: 25'6" or 25'
        var feetMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*'(?:\s*(\d+(?:\.\d+)?)\s*""?\s*)?$");
        if (feetMatch.Success)
        {
            var feet = float.Parse(feetMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var inches = feetMatch.Groups[2].Success
                ? float.Parse(feetMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 0f;
            meters = (feet + inches / 12f) * 0.3048f;
            return meters > 0;
        }

        // Try number + unit suffix
        var unitMatch = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(m|km|mi|ft)?$");
        if (unitMatch.Success)
        {
            var num = float.Parse(unitMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var unit = unitMatch.Groups[2].Success ? unitMatch.Groups[2].Value : "m";
            meters = unit switch
            {
                "km" => num * 1000f,
                "mi" => num * 1609.344f,
                "ft" => num * 0.3048f,
                _ => num  // "m" or bare number
            };
            return meters > 0;
        }

        return false;
    }

    public static OsmLaneInfo? TryParse(Dictionary<string, string> tags)
    {
        tags.TryGetValue("lanes", out var lanesStr);
        tags.TryGetValue("lanes:forward", out var fwdStr);
        tags.TryGetValue("lanes:backward", out var bwdStr);
        tags.TryGetValue("oneway", out var oneway);

        int.TryParse(lanesStr, out var totalLanes);
        int.TryParse(fwdStr, out var fwd);
        int.TryParse(bwdStr, out var bwd);

        bool hasFwd = fwdStr != null && fwd > 0;
        bool hasBwd = bwdStr != null && bwd > 0;
        bool isOneWayYes = oneway is "yes" or "true" or "1";
        bool isOneWayReverse = oneway == "-1";

        int lanesForward, lanesBackward;
        bool isOneWay;
        bool hasLaneTags;

        // Priority 1: both forward + backward explicit
        if (hasFwd && hasBwd)
        {
            lanesForward = fwd;
            lanesBackward = bwd;
            if (totalLanes <= 0) totalLanes = fwd + bwd;
            isOneWay = false;
            hasLaneTags = true;
        }
        // Priority 2: oneway=yes + lanes
        else if (isOneWayYes && totalLanes > 0)
        {
            lanesForward = totalLanes;
            lanesBackward = 0;
            isOneWay = true;
            hasLaneTags = true;
        }
        // Priority 3: oneway=-1 + lanes
        else if (isOneWayReverse && totalLanes > 0)
        {
            lanesForward = 0;
            lanesBackward = totalLanes;
            isOneWay = true;
            hasLaneTags = true;
        }
        // Priority 4: lanes:forward + lanes
        else if (hasFwd && totalLanes > 0)
        {
            lanesForward = fwd;
            lanesBackward = totalLanes - fwd;
            isOneWay = false;
            hasLaneTags = true;
        }
        // Priority 5: lanes:backward + lanes
        else if (hasBwd && totalLanes > 0)
        {
            lanesForward = totalLanes - bwd;
            lanesBackward = bwd;
            isOneWay = false;
            hasLaneTags = true;
        }
        // Priority 6: lanes only (two-way)
        else if (totalLanes > 0)
        {
            lanesBackward = totalLanes / 2;
            lanesForward = totalLanes - lanesBackward; // odd extra to forward
            isOneWay = false;
            hasLaneTags = true;
        }
        // Priority 7: oneway without explicit lane count — default to 1 lane
        else if (isOneWayYes || isOneWayReverse)
        {
            totalLanes = 1;
            lanesForward = isOneWayYes ? 1 : 0;
            lanesBackward = isOneWayReverse ? 1 : 0;
            isOneWay = true;
            hasLaneTags = true;
        }
        // Priority 8: no lane tags
        else
        {
            lanesForward = 0;
            lanesBackward = 0;
            isOneWay = false;
            hasLaneTags = false;
        }

        // Parse width tags once
        tags.TryGetValue("width", out var widthStr);
        tags.TryGetValue("est_width", out var estWidthStr);
        bool hasWidth = TryParseWidth(widthStr, out var widthM);
        bool hasEstWidth = TryParseWidth(estWidthStr, out var estWidthM);

        // If no lane tags AND no width tags: return null (unchanged behavior)
        if (!hasLaneTags && !hasWidth && !hasEstWidth)
            return null;

        var info = new OsmLaneInfo
        {
            TotalLanes = totalLanes,
            LanesForward = lanesForward,
            LanesBackward = lanesBackward,
            IsOneWay = isOneWay
        };

        if (hasLaneTags)
        {
            // Parse future-use fields
            if (tags.TryGetValue("turn:lanes:forward", out var tlf))
                info.TurnLanesForward = tlf;
            else if (tags.TryGetValue("turn:lanes", out var tl) && !isOneWayReverse)
                info.TurnLanesForward = tl;

            if (tags.TryGetValue("turn:lanes:backward", out var tlb))
                info.TurnLanesBackward = tlb;
        }

        if (tags.TryGetValue("maxspeed", out var ms)) info.MaxSpeed = ms;
        if (tags.TryGetValue("surface", out var sf)) info.Surface = sf;
        if (tags.TryGetValue("bus:lanes", out var bl)) info.BusLanes = bl;
        if (tags.TryGetValue("hgv:lanes", out var hl)) info.HgvLanes = hl;
        if (tags.TryGetValue("access", out var ac)) info.Access = ac;

        if (hasWidth) info.WidthMeters = widthM;
        if (hasEstWidth) info.EstWidthMeters = estWidthM;

        return info;
    }
}
