// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Lightweight snapshot of a UnifiedRoadNetwork containing only the fields
/// that DecalRoadGenerator.Generate() actually reads. Designed for fast
/// binary serialization to MT_TerrainGeneration/decalroad_data/network.bin.
/// </summary>
public class DecalRoadNetworkSnapshot
{
    public const int FormatVersion = 1;
    public const string FileName = "network.bin";
    public const string SubFolder = "decalroad_data";

    public List<SplineSnapshot> Splines { get; set; } = [];
    public List<CrossSectionSnapshot> CrossSections { get; set; } = [];
    public List<JunctionSnapshot> Junctions { get; set; } = [];

    public void WriteTo(BinaryWriter w)
    {
        w.Write(FormatVersion);
        w.Write(Splines.Count);
        foreach (var s in Splines)
            s.WriteTo(w);
        w.Write(CrossSections.Count);
        foreach (var cs in CrossSections)
            cs.WriteTo(w);
        w.Write(Junctions.Count);
        foreach (var j in Junctions)
            j.WriteTo(w);
    }

    public static DecalRoadNetworkSnapshot ReadFrom(BinaryReader r)
    {
        var version = r.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"DecalRoadNetworkSnapshot version mismatch: expected {FormatVersion}, got {version}");

        var snapshot = new DecalRoadNetworkSnapshot();

        var splineCount = r.ReadInt32();
        for (int i = 0; i < splineCount; i++)
            snapshot.Splines.Add(SplineSnapshot.ReadFrom(r));

        var csCount = r.ReadInt32();
        for (int i = 0; i < csCount; i++)
            snapshot.CrossSections.Add(CrossSectionSnapshot.ReadFrom(r));

        var junctionCount = r.ReadInt32();
        for (int i = 0; i < junctionCount; i++)
            snapshot.Junctions.Add(JunctionSnapshot.ReadFrom(r));

        return snapshot;
    }

    public static string GetSnapshotPath(string levelPath)
    {
        return Path.Combine(levelPath, "MT_TerrainGeneration", SubFolder, FileName);
    }

    public static bool Exists(string levelPath)
    {
        return File.Exists(GetSnapshotPath(levelPath));
    }
}

public class SplineSnapshot
{
    public int SplineId { get; set; }
    public string OsmRoadType { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public bool IsBridge { get; set; }
    public bool IsTunnel { get; set; }
    public int Priority { get; set; }
    public float RoadWidthMeters { get; set; }
    public float? RoadSurfaceWidthMeters { get; set; }
    public float? MasterSplineWidthMeters { get; set; }
    public float TerrainAffectedRangeMeters { get; set; }
    public Vector2 StartPoint { get; set; }
    public Vector2 EndPoint { get; set; }
    public float TotalLengthMeters { get; set; }
    public List<LaneSegmentSnapshot>? LaneSegments { get; set; }

    public void WriteTo(BinaryWriter w)
    {
        w.Write(SplineId);
        w.Write(OsmRoadType);
        w.Write(MaterialName);
        w.Write(IsBridge);
        w.Write(IsTunnel);
        w.Write(Priority);
        w.Write(RoadWidthMeters);
        w.Write(RoadSurfaceWidthMeters.HasValue);
        if (RoadSurfaceWidthMeters.HasValue) w.Write(RoadSurfaceWidthMeters.Value);
        w.Write(MasterSplineWidthMeters.HasValue);
        if (MasterSplineWidthMeters.HasValue) w.Write(MasterSplineWidthMeters.Value);
        w.Write(TerrainAffectedRangeMeters);
        w.Write(StartPoint.X); w.Write(StartPoint.Y);
        w.Write(EndPoint.X); w.Write(EndPoint.Y);
        w.Write(TotalLengthMeters);

        var hasLaneSegments = LaneSegments != null;
        w.Write(hasLaneSegments);
        if (hasLaneSegments)
        {
            w.Write(LaneSegments!.Count);
            foreach (var ls in LaneSegments)
                ls.WriteTo(w);
        }
    }

    public static SplineSnapshot ReadFrom(BinaryReader r)
    {
        var s = new SplineSnapshot
        {
            SplineId = r.ReadInt32(),
            OsmRoadType = r.ReadString(),
            MaterialName = r.ReadString(),
            IsBridge = r.ReadBoolean(),
            IsTunnel = r.ReadBoolean(),
            Priority = r.ReadInt32(),
            RoadWidthMeters = r.ReadSingle(),
        };
        if (r.ReadBoolean()) s.RoadSurfaceWidthMeters = r.ReadSingle();
        if (r.ReadBoolean()) s.MasterSplineWidthMeters = r.ReadSingle();
        s.TerrainAffectedRangeMeters = r.ReadSingle();
        s.StartPoint = new Vector2(r.ReadSingle(), r.ReadSingle());
        s.EndPoint = new Vector2(r.ReadSingle(), r.ReadSingle());
        s.TotalLengthMeters = r.ReadSingle();

        if (r.ReadBoolean())
        {
            var count = r.ReadInt32();
            s.LaneSegments = new List<LaneSegmentSnapshot>(count);
            for (int i = 0; i < count; i++)
                s.LaneSegments.Add(LaneSegmentSnapshot.ReadFrom(r));
        }

        return s;
    }
}

public class LaneSegmentSnapshot
{
    public int StartPointIndex { get; set; }
    public float StartDistance { get; set; }
    public int TotalLanes { get; set; }
    public int LanesForward { get; set; }
    public int LanesBackward { get; set; }
    public int LanesBothWays { get; set; }
    public bool IsOneWay { get; set; }

    public void WriteTo(BinaryWriter w)
    {
        w.Write(StartPointIndex);
        w.Write(StartDistance);
        w.Write(TotalLanes);
        w.Write(LanesForward);
        w.Write(LanesBackward);
        w.Write(LanesBothWays);
        w.Write(IsOneWay);
    }

    public static LaneSegmentSnapshot ReadFrom(BinaryReader r)
    {
        return new LaneSegmentSnapshot
        {
            StartPointIndex = r.ReadInt32(),
            StartDistance = r.ReadSingle(),
            TotalLanes = r.ReadInt32(),
            LanesForward = r.ReadInt32(),
            LanesBackward = r.ReadInt32(),
            LanesBothWays = r.ReadInt32(),
            IsOneWay = r.ReadBoolean()
        };
    }
}

public class CrossSectionSnapshot
{
    public Vector2 CenterPoint { get; set; }
    public Vector2 NormalDirection { get; set; }
    public float TargetElevation { get; set; }
    public int OwnerSplineId { get; set; }
    public int LocalIndex { get; set; }
    public float DistanceAlongSpline { get; set; }
    public float EffectiveRoadWidth { get; set; }
    public float Curvature { get; set; }
    public bool IsExcluded { get; set; }
    public bool IsSplineStart { get; set; }
    public bool IsSplineEnd { get; set; }

    public void WriteTo(BinaryWriter w)
    {
        w.Write(CenterPoint.X); w.Write(CenterPoint.Y);
        w.Write(NormalDirection.X); w.Write(NormalDirection.Y);
        w.Write(TargetElevation);
        w.Write(OwnerSplineId);
        w.Write(LocalIndex);
        w.Write(DistanceAlongSpline);
        w.Write(EffectiveRoadWidth);
        w.Write(Curvature);
        w.Write(IsExcluded);
        w.Write(IsSplineStart);
        w.Write(IsSplineEnd);
    }

    public static CrossSectionSnapshot ReadFrom(BinaryReader r)
    {
        return new CrossSectionSnapshot
        {
            CenterPoint = new Vector2(r.ReadSingle(), r.ReadSingle()),
            NormalDirection = new Vector2(r.ReadSingle(), r.ReadSingle()),
            TargetElevation = r.ReadSingle(),
            OwnerSplineId = r.ReadInt32(),
            LocalIndex = r.ReadInt32(),
            DistanceAlongSpline = r.ReadSingle(),
            EffectiveRoadWidth = r.ReadSingle(),
            Curvature = r.ReadSingle(),
            IsExcluded = r.ReadBoolean(),
            IsSplineStart = r.ReadBoolean(),
            IsSplineEnd = r.ReadBoolean()
        };
    }
}

public class JunctionSnapshot
{
    public Vector2 Position { get; set; }
    public int Type { get; set; }
    public bool IsExcluded { get; set; }
    public List<JunctionContributorSnapshot> Contributors { get; set; } = [];

    public void WriteTo(BinaryWriter w)
    {
        w.Write(Position.X); w.Write(Position.Y);
        w.Write(Type);
        w.Write(IsExcluded);
        w.Write(Contributors.Count);
        foreach (var c in Contributors)
            c.WriteTo(w);
    }

    public static JunctionSnapshot ReadFrom(BinaryReader r)
    {
        var j = new JunctionSnapshot
        {
            Position = new Vector2(r.ReadSingle(), r.ReadSingle()),
            Type = r.ReadInt32(),
            IsExcluded = r.ReadBoolean()
        };
        var count = r.ReadInt32();
        for (int i = 0; i < count; i++)
            j.Contributors.Add(JunctionContributorSnapshot.ReadFrom(r));
        return j;
    }
}

public class JunctionContributorSnapshot
{
    public int SplineId { get; set; }
    public int CrossSectionOwnerSplineId { get; set; }
    public int CrossSectionLocalIndex { get; set; }
    public bool IsSplineStart { get; set; }
    public bool IsSplineEnd { get; set; }

    public void WriteTo(BinaryWriter w)
    {
        w.Write(SplineId);
        w.Write(CrossSectionOwnerSplineId);
        w.Write(CrossSectionLocalIndex);
        w.Write(IsSplineStart);
        w.Write(IsSplineEnd);
    }

    public static JunctionContributorSnapshot ReadFrom(BinaryReader r)
    {
        return new JunctionContributorSnapshot
        {
            SplineId = r.ReadInt32(),
            CrossSectionOwnerSplineId = r.ReadInt32(),
            CrossSectionLocalIndex = r.ReadInt32(),
            IsSplineStart = r.ReadBoolean(),
            IsSplineEnd = r.ReadBoolean()
        };
    }
}
