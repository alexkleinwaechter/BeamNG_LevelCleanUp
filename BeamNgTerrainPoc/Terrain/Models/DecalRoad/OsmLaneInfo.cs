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
        Access = Access
    };

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

        // Priority 1: both forward + backward explicit
        if (hasFwd && hasBwd)
        {
            lanesForward = fwd;
            lanesBackward = bwd;
            if (totalLanes <= 0) totalLanes = fwd + bwd;
            isOneWay = false;
        }
        // Priority 2: oneway=yes + lanes
        else if (isOneWayYes && totalLanes > 0)
        {
            lanesForward = totalLanes;
            lanesBackward = 0;
            isOneWay = true;
        }
        // Priority 3: oneway=-1 + lanes
        else if (isOneWayReverse && totalLanes > 0)
        {
            lanesForward = 0;
            lanesBackward = totalLanes;
            isOneWay = true;
        }
        // Priority 4: lanes:forward + lanes
        else if (hasFwd && totalLanes > 0)
        {
            lanesForward = fwd;
            lanesBackward = totalLanes - fwd;
            isOneWay = false;
        }
        // Priority 5: lanes:backward + lanes
        else if (hasBwd && totalLanes > 0)
        {
            lanesForward = totalLanes - bwd;
            lanesBackward = bwd;
            isOneWay = false;
        }
        // Priority 6: lanes only (two-way)
        else if (totalLanes > 0)
        {
            lanesBackward = totalLanes / 2;
            lanesForward = totalLanes - lanesBackward; // odd extra to forward
            isOneWay = false;
        }
        // Priority 7: no lane tags
        else
        {
            return null;
        }

        var info = new OsmLaneInfo
        {
            TotalLanes = totalLanes,
            LanesForward = lanesForward,
            LanesBackward = lanesBackward,
            IsOneWay = isOneWay
        };

        // Parse future-use fields
        if (tags.TryGetValue("turn:lanes:forward", out var tlf))
            info.TurnLanesForward = tlf;
        else if (tags.TryGetValue("turn:lanes", out var tl) && !isOneWayReverse)
            info.TurnLanesForward = tl;

        if (tags.TryGetValue("turn:lanes:backward", out var tlb))
            info.TurnLanesBackward = tlb;

        if (tags.TryGetValue("maxspeed", out var ms)) info.MaxSpeed = ms;
        if (tags.TryGetValue("surface", out var sf)) info.Surface = sf;
        if (tags.TryGetValue("bus:lanes", out var bl)) info.BusLanes = bl;
        if (tags.TryGetValue("hgv:lanes", out var hl)) info.HgvLanes = hl;
        if (tags.TryGetValue("access", out var ac)) info.Access = ac;

        return info;
    }
}
