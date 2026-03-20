# DecalRoad Standalone Regeneration Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable DecalRoad regeneration from a previously generated terrain without re-running the full terrain generation pipeline, by persisting a lightweight network snapshot to disk during generation and loading it back on demand.

**Architecture:** During terrain generation, serialize a `DecalRoadNetworkSnapshot` (flat DTOs of spline metadata, cross-sections, and junctions) to `MT_TerrainGeneration/decalroad_data/network.bin`. For the heightmap, read it back from the existing `.ter` file using `TerrainSerializer` (ushort→float, acceptable precision for the fallback-only elevation path). On "Re-generate DecalRoads", load the snapshot + `.ter` heightmap and pass them to `DecalRoadGenerator.Generate()` exactly as the in-memory path does today.

**Tech Stack:** .NET 9, C#, System.Numerics (Vector2), System.IO.BinaryWriter/BinaryReader, Grille.BeamNG.Lib (TerrainSerializer), xUnit

**Spec:** `docs/superpowers/specs/2026-03-12-decalroad-generation-design.md`

**Skills:** @beamng-decalroad-generation

---

## Design Decisions

### Why a custom binary format instead of JSON?

A typical 4096 terrain has ~500K cross-sections. Each cross-section has 10+ float/int fields. In JSON this would be ~40-80MB with parsing overhead. A binary format gives ~15MB with instant deserialization via BinaryReader. The snapshot is an internal cache, not a user-editable config, so human readability is not needed.

### Why not serialize the full UnifiedRoadNetwork?

`ParameterizedRoadSpline.Spline` contains MathNet.Numerics `IInterpolation` objects (private fields, no public serialization support). The DecalRoad generator never calls `Spline.SampleByDistance()` — it only reads pre-computed cross-section data. So we serialize only what the generator actually reads: spline metadata + cross-sections + junction structure.

### Why read heightmap from .ter instead of saving separately?

The .ter file is always written during terrain generation and contains the heightmap as ushort values (quantized 0→maxHeight). The DecalRoad generator only uses the heightmap as a **fallback** when `TargetElevation` is NaN (rare — only PNG-sourced splines). The ushort→float precision loss is negligible for this fallback path. This avoids saving an additional 64MB file.

### Snapshot data budget

For a typical 4096 terrain with 1000 splines, 500K cross-sections, 300 junctions:
- Spline metadata: ~1000 × 80 bytes ≈ 80KB
- Cross-sections: ~500K × 44 bytes ≈ 22MB
- Junctions: ~300 × 200 bytes ≈ 60KB
- **Total: ~22MB** (vs ~80MB for JSON, vs ~64MB raw heightmap)

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs` | DTO record types + binary serialize/deserialize for the snapshot |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs` | Extracts snapshot from a live `UnifiedRoadNetwork` |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs` | Loads snapshot from disk and reconstructs a `UnifiedRoadNetwork` + reads heightmap from `.ter` |
| `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadNetworkSnapshotTests.cs` | Round-trip serialization tests |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` | After DecalRoad generation, save snapshot via `DecalRoadNetworkSnapshotBuilder` |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | `RegenerateDecalRoads()`: load snapshot from disk when in-memory cache is null |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Update re-generate button disabled state (enable when snapshot file exists on disk) |

---

## Chunk 1: Snapshot DTO & Serialization

### Task 1: Create DecalRoadNetworkSnapshot DTOs and binary serialization

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs`

This file contains the flat DTO records and the binary read/write logic. The DTOs capture exactly the fields that `DecalRoadGenerator` reads from `UnifiedRoadNetwork`, nothing more.

- [ ] **Step 1: Create the snapshot DTO file with record types and binary serialization**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

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

    /// <summary>
    /// Writes the snapshot to a binary stream.
    /// Format: version(int) → splineCount → splines → csCount → crossSections → junctionCount → junctions
    /// </summary>
    public void WriteTo(BinaryWriter w)
    {
        w.Write(FormatVersion);

        // Splines
        w.Write(Splines.Count);
        foreach (var s in Splines)
            s.WriteTo(w);

        // Cross-sections
        w.Write(CrossSections.Count);
        foreach (var cs in CrossSections)
            cs.WriteTo(w);

        // Junctions
        w.Write(Junctions.Count);
        foreach (var j in Junctions)
            j.WriteTo(w);
    }

    /// <summary>
    /// Reads a snapshot from a binary stream.
    /// </summary>
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

    /// <summary>
    /// Gets the default file path for the snapshot within a level directory.
    /// </summary>
    public static string GetSnapshotPath(string levelPath)
    {
        return Path.Combine(levelPath, "MT_TerrainGeneration", SubFolder, FileName);
    }

    /// <summary>
    /// Checks whether a snapshot file exists for the given level.
    /// </summary>
    public static bool Exists(string levelPath)
    {
        return File.Exists(GetSnapshotPath(levelPath));
    }
}

/// <summary>
/// Flat snapshot of a ParameterizedRoadSpline — only fields used by DecalRoadGenerator.
/// </summary>
public class SplineSnapshot
{
    public int SplineId { get; set; }
    public string OsmRoadType { get; set; } = string.Empty; // empty = null
    public string MaterialName { get; set; } = string.Empty;
    public bool IsBridge { get; set; }
    public bool IsTunnel { get; set; }
    public int Priority { get; set; }

    // From Parameters (RoadSmoothingParameters)
    public float RoadWidthMeters { get; set; }
    public float? RoadSurfaceWidthMeters { get; set; }
    public float? MasterSplineWidthMeters { get; set; }
    public float TerrainAffectedRangeMeters { get; set; }

    // Spline geometry (only StartPoint/EndPoint needed for naming)
    public Vector2 StartPoint { get; set; }
    public Vector2 EndPoint { get; set; }
    public float TotalLengthMeters { get; set; }

    // Lane segments (nullable)
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

/// <summary>
/// Snapshot of a LaneSegment + its OsmLaneInfo.
/// </summary>
public class LaneSegmentSnapshot
{
    public int StartPointIndex { get; set; }
    public float StartDistance { get; set; }

    // OsmLaneInfo fields
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

/// <summary>
/// Flat snapshot of a UnifiedCrossSection — only fields used by DecalRoadGenerator.
/// </summary>
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

/// <summary>
/// Snapshot of a NetworkJunction — position, type, and contributor references.
/// </summary>
public class JunctionSnapshot
{
    public Vector2 Position { get; set; }
    public int Type { get; set; } // Cast to/from JunctionType enum
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

/// <summary>
/// Snapshot of a JunctionContributor — references spline by ID + cross-section by owner+localIndex.
/// </summary>
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
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshot.cs
git commit -m "feat: add DecalRoadNetworkSnapshot DTOs with binary serialization"
```

---

### Task 2: Create snapshot builder (live network → snapshot)

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs`

This class extracts a `DecalRoadNetworkSnapshot` from a live `UnifiedRoadNetwork` and writes it to disk.

- [ ] **Step 1: Create the snapshot builder**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Builds a DecalRoadNetworkSnapshot from a live UnifiedRoadNetwork,
/// capturing only the fields that DecalRoadGenerator needs.
/// </summary>
public static class DecalRoadNetworkSnapshotBuilder
{
    /// <summary>
    /// Creates a snapshot from a live network.
    /// </summary>
    public static DecalRoadNetworkSnapshot Build(UnifiedRoadNetwork network)
    {
        var snapshot = new DecalRoadNetworkSnapshot();

        // Splines
        foreach (var spline in network.Splines)
        {
            var ss = new SplineSnapshot
            {
                SplineId = spline.SplineId,
                OsmRoadType = spline.OsmRoadType ?? string.Empty,
                MaterialName = spline.MaterialName,
                IsBridge = spline.IsBridge,
                IsTunnel = spline.IsTunnel,
                Priority = spline.Priority,
                RoadWidthMeters = spline.Parameters.RoadWidthMeters,
                RoadSurfaceWidthMeters = spline.Parameters.RoadSurfaceWidthMeters,
                MasterSplineWidthMeters = spline.Parameters.MasterSplineWidthMeters,
                TerrainAffectedRangeMeters = spline.Parameters.TerrainAffectedRangeMeters,
                StartPoint = spline.StartPoint,
                EndPoint = spline.EndPoint,
                TotalLengthMeters = spline.TotalLengthMeters
            };

            if (spline.LaneSegments != null)
            {
                ss.LaneSegments = spline.LaneSegments.Select(ls => new LaneSegmentSnapshot
                {
                    StartPointIndex = ls.StartPointIndex,
                    StartDistance = ls.StartDistance,
                    TotalLanes = ls.LaneInfo.TotalLanes,
                    LanesForward = ls.LaneInfo.LanesForward,
                    LanesBackward = ls.LaneInfo.LanesBackward,
                    LanesBothWays = ls.LaneInfo.LanesBothWays,
                    IsOneWay = ls.LaneInfo.IsOneWay
                }).ToList();
            }

            snapshot.Splines.Add(ss);
        }

        // Cross-sections
        foreach (var cs in network.CrossSections)
        {
            snapshot.CrossSections.Add(new CrossSectionSnapshot
            {
                CenterPoint = cs.CenterPoint,
                NormalDirection = cs.NormalDirection,
                TargetElevation = cs.TargetElevation,
                OwnerSplineId = cs.OwnerSplineId,
                LocalIndex = cs.LocalIndex,
                DistanceAlongSpline = cs.DistanceAlongSpline,
                EffectiveRoadWidth = cs.EffectiveRoadWidth,
                Curvature = cs.Curvature,
                IsExcluded = cs.IsExcluded,
                IsSplineStart = cs.IsSplineStart,
                IsSplineEnd = cs.IsSplineEnd
            });
        }

        // Junctions
        foreach (var junction in network.Junctions)
        {
            var js = new JunctionSnapshot
            {
                Position = junction.Position,
                Type = (int)junction.Type,
                IsExcluded = junction.IsExcluded
            };

            foreach (var contributor in junction.Contributors)
            {
                js.Contributors.Add(new JunctionContributorSnapshot
                {
                    SplineId = contributor.Spline.SplineId,
                    CrossSectionOwnerSplineId = contributor.CrossSection.OwnerSplineId,
                    CrossSectionLocalIndex = contributor.CrossSection.LocalIndex,
                    IsSplineStart = contributor.IsSplineStart,
                    IsSplineEnd = contributor.IsSplineEnd
                });
            }

            snapshot.Junctions.Add(js);
        }

        return snapshot;
    }

    /// <summary>
    /// Builds a snapshot from a live network and writes it to disk.
    /// Creates the directory structure if it doesn't exist.
    /// </summary>
    public static void SaveToLevel(UnifiedRoadNetwork network, string levelPath)
    {
        var snapshot = Build(network);
        var path = DecalRoadNetworkSnapshot.GetSnapshotPath(levelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        snapshot.WriteTo(writer);
    }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotBuilder.cs
git commit -m "feat: add DecalRoadNetworkSnapshotBuilder to extract snapshot from live network"
```

---

### Task 3: Create snapshot loader (disk → UnifiedRoadNetwork + heightmap)

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs`

This class reads the snapshot from disk and reconstructs a `UnifiedRoadNetwork` that is compatible with `DecalRoadGenerator.Generate()`. It also reads the heightmap from the `.ter` file.

**Critical design**: The reconstructed `ParameterizedRoadSpline` objects will have a **stub `RoadSpline`** constructed from just `StartPoint` and `EndPoint` (2-point linear spline). The DecalRoad generator never calls `Spline.SampleByDistance()` — it uses cross-section data exclusively. The stub spline exists only to satisfy the `required RoadSpline Spline` property and provide `StartPoint`/`EndPoint` for naming.

- [ ] **Step 1: Create the snapshot loader**

```csharp
// BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Grille.BeamNG.IO.Binary;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Loads a DecalRoadNetworkSnapshot from disk and reconstructs a UnifiedRoadNetwork
/// suitable for DecalRoadGenerator.Generate(). Also provides heightmap loading from .ter files.
/// </summary>
public static class DecalRoadNetworkSnapshotLoader
{
    /// <summary>
    /// Loads the snapshot from a level directory and reconstructs a UnifiedRoadNetwork.
    /// </summary>
    /// <param name="levelPath">Level root directory (contains MT_TerrainGeneration/).</param>
    /// <returns>The reconstructed network, or null if no snapshot exists.</returns>
    public static UnifiedRoadNetwork? LoadNetwork(string levelPath)
    {
        var snapshotPath = DecalRoadNetworkSnapshot.GetSnapshotPath(levelPath);
        if (!File.Exists(snapshotPath))
            return null;

        DecalRoadNetworkSnapshot snapshot;
        using (var stream = File.OpenRead(snapshotPath))
        using (var reader = new BinaryReader(stream))
        {
            snapshot = DecalRoadNetworkSnapshot.ReadFrom(reader);
        }

        return ReconstructNetwork(snapshot);
    }

    /// <summary>
    /// Loads the heightmap from the .ter file using Grille.BeamNG.Lib's TerrainSerializer.
    /// Returns a float[y, x] row-major array matching what DecalRoadGenerator expects.
    /// </summary>
    /// <param name="terFilePath">Full path to the .ter file.</param>
    /// <param name="maxHeight">MaxHeight parameter used when the terrain was generated.
    /// Required for ushort→float height conversion.</param>
    /// <returns>The heightmap as float[y, x], or null if the file doesn't exist.</returns>
    public static float[,]? LoadHeightmap(string terFilePath, float maxHeight)
    {
        if (!File.Exists(terFilePath))
            return null;

        using var stream = File.OpenRead(terFilePath);
        var terrain = TerrainSerializer.Deserialize(stream, maxHeight);

        var size = terrain.Size;
        var heightMap = new float[size, size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                heightMap[y, x] = terrain.Data[x, y].Height;
            }
        }

        return heightMap;
    }

    /// <summary>
    /// Reconstructs a UnifiedRoadNetwork from a snapshot.
    /// Splines get stub RoadSpline objects (2-point linear) since the generator
    /// only uses pre-computed cross-section data.
    /// </summary>
    internal static UnifiedRoadNetwork ReconstructNetwork(DecalRoadNetworkSnapshot snapshot)
    {
        var network = new UnifiedRoadNetwork();

        // Build splines with stub geometry
        var splineById = new Dictionary<int, ParameterizedRoadSpline>();
        foreach (var ss in snapshot.Splines)
        {
            // Create stub RoadSpline from start/end points only.
            // DecalRoadGenerator never calls Spline.SampleByDistance() — it uses
            // cross-section CenterPoint/NormalDirection exclusively.
            var controlPoints = new List<Vector2> { ss.StartPoint, ss.EndPoint };
            var stubSpline = new RoadSpline(controlPoints, SplineInterpolationType.LinearControlPoints);

            List<LaneSegment>? laneSegments = null;
            if (ss.LaneSegments != null)
            {
                laneSegments = ss.LaneSegments.Select(ls => new LaneSegment
                {
                    StartPointIndex = ls.StartPointIndex,
                    StartDistance = ls.StartDistance,
                    LaneInfo = new OsmLaneInfo
                    {
                        TotalLanes = ls.TotalLanes,
                        LanesForward = ls.LanesForward,
                        LanesBackward = ls.LanesBackward,
                        LanesBothWays = ls.LanesBothWays,
                        IsOneWay = ls.IsOneWay
                    }
                }).ToList();
            }

            var spline = new ParameterizedRoadSpline
            {
                Spline = stubSpline,
                Parameters = new RoadSmoothingParameters
                {
                    RoadWidthMeters = ss.RoadWidthMeters,
                    RoadSurfaceWidthMeters = ss.RoadSurfaceWidthMeters,
                    MasterSplineWidthMeters = ss.MasterSplineWidthMeters,
                    TerrainAffectedRangeMeters = ss.TerrainAffectedRangeMeters
                },
                MaterialName = ss.MaterialName,
                SplineId = ss.SplineId,
                OsmRoadType = ss.OsmRoadType == string.Empty ? null : ss.OsmRoadType,
                LaneSegments = laneSegments,
                IsBridge = ss.IsBridge,
                IsTunnel = ss.IsTunnel
            };
            spline.Priority = ss.Priority;

            network.AddSpline(spline);
            splineById[ss.SplineId] = spline;
        }

        // Add cross-sections
        foreach (var css in snapshot.CrossSections)
        {
            network.AddCrossSection(new UnifiedCrossSection
            {
                CenterPoint = css.CenterPoint,
                NormalDirection = css.NormalDirection,
                TargetElevation = css.TargetElevation,
                OwnerSplineId = css.OwnerSplineId,
                LocalIndex = css.LocalIndex,
                DistanceAlongSpline = css.DistanceAlongSpline,
                EffectiveRoadWidth = css.EffectiveRoadWidth,
                Curvature = css.Curvature,
                IsExcluded = css.IsExcluded,
                IsSplineStart = css.IsSplineStart,
                IsSplineEnd = css.IsSplineEnd
            });
        }

        // Reconstruct junctions
        // We need to find the actual cross-section and spline objects by reference
        // Build a lookup: (OwnerSplineId, LocalIndex) → UnifiedCrossSection
        var csLookup = new Dictionary<(int, int), UnifiedCrossSection>();
        foreach (var cs in network.CrossSections)
        {
            csLookup[(cs.OwnerSplineId, cs.LocalIndex)] = cs;
        }

        foreach (var js in snapshot.Junctions)
        {
            var junction = new NetworkJunction
            {
                Position = js.Position,
                Type = (JunctionType)js.Type,
                IsExcluded = js.IsExcluded
            };

            foreach (var cs in js.Contributors)
            {
                if (!splineById.TryGetValue(cs.SplineId, out var spline))
                    continue;
                if (!csLookup.TryGetValue((cs.CrossSectionOwnerSplineId, cs.CrossSectionLocalIndex), out var crossSection))
                    continue;

                junction.Contributors.Add(new JunctionContributor
                {
                    Spline = spline,
                    CrossSection = crossSection,
                    IsSplineStart = cs.IsSplineStart,
                    IsSplineEnd = cs.IsSplineEnd
                });
            }

            network.Junctions.Add(junction);
        }

        return network;
    }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs
git commit -m "feat: add DecalRoadNetworkSnapshotLoader for disk-based network reconstruction"
```

---

## Chunk 2: Tests

### Task 4: Write round-trip serialization tests

**Files:**
- Create: `BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadNetworkSnapshotTests.cs`

- [ ] **Step 1: Write tests for snapshot round-trip serialization**

```csharp
// BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadNetworkSnapshotTests.cs
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNgTerrainPoc.Tests.DecalRoad;

public class DecalRoadNetworkSnapshotTests
{
    [Fact]
    public void RoundTrip_EmptySnapshot_Succeeds()
    {
        var snapshot = new DecalRoadNetworkSnapshot();

        var deserialized = RoundTrip(snapshot);

        Assert.Empty(deserialized.Splines);
        Assert.Empty(deserialized.CrossSections);
        Assert.Empty(deserialized.Junctions);
    }

    [Fact]
    public void RoundTrip_SplineData_PreservesAllFields()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 42,
            OsmRoadType = "primary",
            MaterialName = "Asphalt",
            IsBridge = true,
            IsTunnel = false,
            Priority = 8000,
            RoadWidthMeters = 7.5f,
            RoadSurfaceWidthMeters = 6.0f,
            MasterSplineWidthMeters = 5.5f,
            TerrainAffectedRangeMeters = 12.0f,
            StartPoint = new Vector2(100, 200),
            EndPoint = new Vector2(300, 400),
            TotalLengthMeters = 283.0f
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.Splines);
        var s = result.Splines[0];
        Assert.Equal(42, s.SplineId);
        Assert.Equal("primary", s.OsmRoadType);
        Assert.Equal("Asphalt", s.MaterialName);
        Assert.True(s.IsBridge);
        Assert.False(s.IsTunnel);
        Assert.Equal(8000, s.Priority);
        Assert.Equal(7.5f, s.RoadWidthMeters);
        Assert.Equal(6.0f, s.RoadSurfaceWidthMeters);
        Assert.Equal(5.5f, s.MasterSplineWidthMeters);
        Assert.Equal(12.0f, s.TerrainAffectedRangeMeters);
        Assert.Equal(100f, s.StartPoint.X);
        Assert.Equal(200f, s.StartPoint.Y);
        Assert.Equal(300f, s.EndPoint.X);
        Assert.Equal(400f, s.EndPoint.Y);
        Assert.Equal(283.0f, s.TotalLengthMeters);
        Assert.Null(s.LaneSegments);
    }

    [Fact]
    public void RoundTrip_SplineWithLaneSegments_PreservesLaneData()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 1,
            MaterialName = "Asphalt",
            StartPoint = new Vector2(0, 0),
            EndPoint = new Vector2(100, 0),
            TotalLengthMeters = 100,
            LaneSegments =
            [
                new LaneSegmentSnapshot
                {
                    StartPointIndex = 0, StartDistance = 0,
                    TotalLanes = 4, LanesForward = 2, LanesBackward = 2,
                    LanesBothWays = 0, IsOneWay = false
                },
                new LaneSegmentSnapshot
                {
                    StartPointIndex = 5, StartDistance = 50,
                    TotalLanes = 3, LanesForward = 2, LanesBackward = 1,
                    LanesBothWays = 0, IsOneWay = false
                }
            ]
        });

        var result = RoundTrip(snapshot);

        Assert.NotNull(result.Splines[0].LaneSegments);
        Assert.Equal(2, result.Splines[0].LaneSegments!.Count);
        Assert.Equal(4, result.Splines[0].LaneSegments[0].TotalLanes);
        Assert.Equal(3, result.Splines[0].LaneSegments[1].TotalLanes);
        Assert.Equal(50f, result.Splines[0].LaneSegments[1].StartDistance);
    }

    [Fact]
    public void RoundTrip_NullOsmType_SerializesAsEmpty()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Splines.Add(new SplineSnapshot
        {
            SplineId = 1,
            OsmRoadType = string.Empty, // represents null OsmRoadType
            MaterialName = "DirtRoad",
            StartPoint = Vector2.Zero,
            EndPoint = Vector2.One,
            TotalLengthMeters = 1
        });

        var result = RoundTrip(snapshot);
        Assert.Equal(string.Empty, result.Splines[0].OsmRoadType);
    }

    [Fact]
    public void RoundTrip_CrossSections_PreservesAllFields()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.CrossSections.Add(new CrossSectionSnapshot
        {
            CenterPoint = new Vector2(50.5f, 100.3f),
            NormalDirection = new Vector2(0.0f, 1.0f),
            TargetElevation = 125.7f,
            OwnerSplineId = 7,
            LocalIndex = 42,
            DistanceAlongSpline = 84.2f,
            EffectiveRoadWidth = 8.0f,
            Curvature = 0.015f,
            IsExcluded = true,
            IsSplineStart = false,
            IsSplineEnd = true
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.CrossSections);
        var cs = result.CrossSections[0];
        Assert.Equal(50.5f, cs.CenterPoint.X);
        Assert.Equal(100.3f, cs.CenterPoint.Y);
        Assert.Equal(0.0f, cs.NormalDirection.X);
        Assert.Equal(1.0f, cs.NormalDirection.Y);
        Assert.Equal(125.7f, cs.TargetElevation);
        Assert.Equal(7, cs.OwnerSplineId);
        Assert.Equal(42, cs.LocalIndex);
        Assert.Equal(84.2f, cs.DistanceAlongSpline);
        Assert.Equal(8.0f, cs.EffectiveRoadWidth);
        Assert.Equal(0.015f, cs.Curvature);
        Assert.True(cs.IsExcluded);
        Assert.False(cs.IsSplineStart);
        Assert.True(cs.IsSplineEnd);
    }

    [Fact]
    public void RoundTrip_NaNTargetElevation_Preserved()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.CrossSections.Add(new CrossSectionSnapshot
        {
            CenterPoint = Vector2.Zero,
            NormalDirection = Vector2.UnitX,
            TargetElevation = float.NaN, // PNG-sourced spline, no elevation data
            OwnerSplineId = 1,
            LocalIndex = 0,
        });

        var result = RoundTrip(snapshot);
        Assert.True(float.IsNaN(result.CrossSections[0].TargetElevation));
    }

    [Fact]
    public void RoundTrip_Junctions_PreservesContributorReferences()
    {
        var snapshot = new DecalRoadNetworkSnapshot();
        snapshot.Junctions.Add(new JunctionSnapshot
        {
            Position = new Vector2(500, 600),
            Type = (int)JunctionType.TJunction,
            IsExcluded = false,
            Contributors =
            [
                new JunctionContributorSnapshot
                {
                    SplineId = 1,
                    CrossSectionOwnerSplineId = 1,
                    CrossSectionLocalIndex = 10,
                    IsSplineStart = false,
                    IsSplineEnd = true
                },
                new JunctionContributorSnapshot
                {
                    SplineId = 2,
                    CrossSectionOwnerSplineId = 2,
                    CrossSectionLocalIndex = 5,
                    IsSplineStart = false,
                    IsSplineEnd = false
                }
            ]
        });

        var result = RoundTrip(snapshot);

        Assert.Single(result.Junctions);
        var j = result.Junctions[0];
        Assert.Equal(500f, j.Position.X);
        Assert.Equal((int)JunctionType.TJunction, j.Type);
        Assert.Equal(2, j.Contributors.Count);
        Assert.Equal(1, j.Contributors[0].SplineId);
        Assert.True(j.Contributors[0].IsSplineEnd);
        Assert.Equal(2, j.Contributors[1].SplineId);
        Assert.False(j.Contributors[1].IsSplineEnd);
    }

    [Fact]
    public void RoundTrip_FullNetwork_BuildAndReconstruct()
    {
        // Build a small live network
        var network = new UnifiedRoadNetwork();

        var spline1Points = new List<Vector2> { new(0, 0), new(100, 0), new(200, 0) };
        var spline1 = new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(spline1Points, SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 8.0f,
                MasterSplineWidthMeters = 6.0f
            },
            MaterialName = "Asphalt",
            SplineId = 1,
            OsmRoadType = "primary"
        };
        spline1.Priority = 80;
        network.AddSpline(spline1);

        // Add a few cross-sections
        for (int i = 0; i < 5; i++)
        {
            network.AddCrossSection(new UnifiedCrossSection
            {
                CenterPoint = new Vector2(i * 50, 0),
                NormalDirection = new Vector2(0, 1),
                TargetElevation = 100 + i,
                OwnerSplineId = 1,
                LocalIndex = i,
                DistanceAlongSpline = i * 50,
                EffectiveRoadWidth = 8.0f,
                Curvature = 0.01f * i
            });
        }

        // Build snapshot → serialize → deserialize → reconstruct
        var snapshot = DecalRoadNetworkSnapshotBuilder.Build(network);
        var deserialized = RoundTrip(snapshot);
        var reconstructed = DecalRoadNetworkSnapshotLoader.ReconstructNetwork(deserialized);

        Assert.Single(reconstructed.Splines);
        Assert.Equal(5, reconstructed.CrossSections.Count);
        Assert.Equal("primary", reconstructed.Splines[0].OsmRoadType);
        Assert.Equal(6.0f, reconstructed.Splines[0].Parameters.EffectiveMasterSplineWidthMeters);

        // Verify cross-section query works
        var csForSpline = reconstructed.GetCrossSectionsForSpline(1).ToList();
        Assert.Equal(5, csForSpline.Count);
        Assert.Equal(102f, csForSpline[2].TargetElevation);
    }

    [Fact]
    public void InvalidVersion_ThrowsInvalidDataException()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(999); // Bad version
        }

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<InvalidDataException>(() => DecalRoadNetworkSnapshot.ReadFrom(r));
    }

    private static DecalRoadNetworkSnapshot RoundTrip(DecalRoadNetworkSnapshot snapshot)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            snapshot.WriteTo(w);
        }

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        return DecalRoadNetworkSnapshot.ReadFrom(r);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~DecalRoadNetworkSnapshot" -v n`
Expected: All 8 tests PASS

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/DecalRoad/DecalRoadNetworkSnapshotTests.cs
git commit -m "test: add round-trip serialization tests for DecalRoadNetworkSnapshot"
```

---

## Chunk 3: Pipeline Integration

### Task 5: Save snapshot during terrain generation

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

After DecalRoad generation (or even when DecalRoads are disabled but road smoothing produced a network), save the snapshot so standalone regeneration is available later.

- [ ] **Step 1: Add snapshot save to TerrainCreator after DecalRoad generation**

In `TerrainCreator.cs`, find the block at approximately line 354-358 where `OutputNetwork` and `OutputHeightMap` are populated:

```csharp
// Populate output properties for downstream use (re-generation)
parameters.OutputNetwork = unifiedResult?.Network;
parameters.OutputHeightMap = heightMap2D;
```

**Add immediately after** that block (before "4. Process material layers"):

```csharp
// Save DecalRoad network snapshot for standalone re-generation across sessions
if (unifiedResult?.Network != null)
{
    try
    {
        var levelDir = Path.GetDirectoryName(outputPath)!;
        Services.DecalRoad.DecalRoadNetworkSnapshotBuilder.SaveToLevel(
            unifiedResult.Network, levelDir);
        perfLog.Info($"Saved DecalRoad network snapshot to {levelDir}/MT_TerrainGeneration/decalroad_data/");
    }
    catch (Exception ex)
    {
        perfLog.Warning($"Failed to save DecalRoad network snapshot: {ex.Message}");
        // Non-fatal — in-memory re-generation still works for current session
    }
}
```

Also add the required using at the top of the file (if not already present — check first):

```csharp
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/TerrainCreator.cs
git commit -m "feat: save DecalRoad network snapshot during terrain generation"
```

---

### Task 6: Load snapshot in RegenerateDecalRoads when in-memory cache is null

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

Update `RegenerateDecalRoads()` to fall back to loading the snapshot from disk when `_state.CachedNetwork` or `_state.CachedHeightMap` is null. This enables regeneration in a fresh session after the user loads a preset and selects a level folder.

- [ ] **Step 1: Update RegenerateDecalRoads to load from disk**

In `GenerateTerrain.razor.cs`, find the `RegenerateDecalRoads()` method (around line 2494). Replace the entire method with:

```csharp
private async Task RegenerateDecalRoads()
{
    _state.DecalRoadSettings ??= new DecalRoadSettings { Enabled = true };

    _isGenerating = true;
    StateHasChanged();

    try
    {
        await Task.Run(() =>
        {
            var network = _state.CachedNetwork;
            var heightMap = _state.CachedHeightMap;

            // Fall back to loading from disk if in-memory cache is empty
            if (network == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "Loading road network from saved snapshot...");
                network = DecalRoadNetworkSnapshotLoader.LoadNetwork(_state.WorkingDirectory);
                if (network == null)
                    throw new InvalidOperationException(
                        "No road network available. Generate terrain first to create the network snapshot.");

                // Cache for subsequent re-generations in this session
                _state.CachedNetwork = network;
            }

            if (heightMap == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "Loading heightmap from .ter file...");
                var terPath = _state.GetOutputPath();
                heightMap = DecalRoadNetworkSnapshotLoader.LoadHeightmap(terPath, _state.MaxHeight);
                if (heightMap == null)
                    throw new InvalidOperationException(
                        $"Terrain file not found at {terPath}. Generate terrain first.");

                // Cache for subsequent re-generations in this session
                _state.CachedHeightMap = heightMap;
            }

            var appDataDefaults = DecalRoadDefaultsManager.Load();

            DecalRoadSceneWriter.CleanPrevious(_state.WorkingDirectory);

            var decalRoads = DecalRoadGenerator.Generate(
                network,
                heightMap,
                _state.MetersPerPixel,
                _state.TerrainSize,
                _state.TerrainBaseHeight,
                _state.DecalRoadSettings,
                appDataDefaults);

            if (decalRoads.Count > 0)
            {
                var writer = new DecalRoadSceneWriter();
                writer.WriteAll(decalRoads, _state.WorkingDirectory);
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Re-generated {decalRoads.Count} DecalRoad objects");
        });

        await InvokeAsync(() =>
        {
            Snackbar.Add("DecalRoads re-generated successfully", Severity.Success);
            StateHasChanged();
        });
    }
    catch (Exception ex)
    {
        ShowException(ex);
        await InvokeAsync(() =>
        {
            Snackbar.Add($"DecalRoad generation failed: {ex.Message}", Severity.Error);
        });
    }
    finally
    {
        _isGenerating = false;
        await InvokeAsync(StateHasChanged);
    }
}
```

Add the required using at the top of the file (check if already present):

```csharp
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
```

Note: `DecalRoadGenerator`, `DecalRoadSceneWriter`, `DecalRoadDefaultsManager`, and `DecalRoadSettings` should already be imported from the existing implementation. Only `DecalRoadNetworkSnapshotLoader` is new.

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat: load DecalRoad snapshot from disk when in-memory cache is empty"
```

---

### Task 7: Update re-generate button enabled state

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

The re-generate button should be enabled when EITHER the in-memory cache is available OR the snapshot file exists on disk.

- [ ] **Step 1: Add helper method to check if regeneration is possible**

In `GenerateTerrain.razor.cs`, add a helper method near the other DecalRoad methods:

```csharp
private bool CanRegenerateDecalRoads()
{
    // Available if in-memory cache exists OR snapshot file exists on disk
    return _state.CachedNetwork != null ||
           (!string.IsNullOrEmpty(_state.WorkingDirectory) &&
            DecalRoadNetworkSnapshot.Exists(_state.WorkingDirectory));
}
```

Add the using if not already present:

```csharp
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
```

- [ ] **Step 2: Update the Disabled condition on the re-generate button**

In `GenerateTerrain.razor`, find the re-generate button (around line 803-808):

```razor
<MudButton Variant="Variant.Outlined"
           Color="Color.Secondary"
           StartIcon="@Icons.Material.Filled.Refresh"
           Disabled="@(_state.CachedNetwork == null || _isGenerating)"
           OnClick="RegenerateDecalRoads">
    Re-generate DecalRoads
</MudButton>
```

Replace the `Disabled` condition:

```razor
<MudButton Variant="Variant.Outlined"
           Color="Color.Secondary"
           StartIcon="@Icons.Material.Filled.Refresh"
           Disabled="@(!CanRegenerateDecalRoads() || _isGenerating)"
           OnClick="RegenerateDecalRoads">
    Re-generate DecalRoads
</MudButton>
```

- [ ] **Step 3: Update the warning text below the button**

Find the warning text block (around line 811-816):

```razor
@if (_state.CachedNetwork == null)
{
    <MudText Typo="Typo.caption" Color="Color.Warning" Class="mt-1">
        Generate terrain first to enable re-generation.
    </MudText>
}
```

Replace with:

```razor
@if (!CanRegenerateDecalRoads())
{
    <MudText Typo="Typo.caption" Color="Color.Warning" Class="mt-1">
        Generate terrain first to enable re-generation.
    </MudText>
}
else if (_state.CachedNetwork == null)
{
    <MudText Typo="Typo.caption" Color="Color.Info" Class="mt-1">
        Will load road network from saved snapshot.
    </MudText>
}
```

- [ ] **Step 4: Verify full solution builds**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat: enable DecalRoad re-generate button when snapshot exists on disk"
```

---

## Chunk 4: Final Verification

### Task 8: Run all tests and verify build

- [ ] **Step 1: Run all tests**

```bash
dotnet test BeamNgTerrainPoc.Tests -v n
```
Expected: All tests PASS (including new snapshot tests)

- [ ] **Step 2: Build entire solution**

```bash
dotnet build
```
Expected: Build succeeded

- [ ] **Step 3: Commit any final fixes**

If any test failures or build issues were found and fixed:

```bash
git add -A
git commit -m "fix: resolve build issues from DecalRoad standalone regeneration"
```

---

## Manual Testing Checklist

After implementation, verify manually:

1. Generate terrain with OSM data that includes roads
2. Check `MT_TerrainGeneration/decalroad_data/network.bin` exists and has reasonable size (~10-30MB)
3. Click "Re-generate DecalRoads" button — should work (uses in-memory cache)
4. Close and reopen the app, load same level folder, load same preset
5. Check that re-generate button is enabled (blue info text: "Will load road network from saved snapshot")
6. Click "Re-generate DecalRoads" — should work (loads from disk)
7. Verify generated DecalRoads are identical to the original generation
8. Test with a level that has never had terrain generated — button should be disabled
9. Delete `MT_TerrainGeneration/decalroad_data/` folder — button should become disabled again

## Post-Implementation Notes

### What's NOT in this plan (deferred):

1. **Snapshot versioning migration** — If the snapshot format needs to change in the future, add a version check in `ReadFrom()` with backward-compatible reading logic. The current `FormatVersion = 1` check throws on mismatch, which is acceptable for the first version.
2. **Snapshot compression** — The binary format is already compact (~22MB for a large terrain). Gzip compression could reduce this to ~5MB but adds complexity. Can be added later if disk space becomes a concern.
3. **Partial regeneration** — Re-generating DecalRoads for only specific materials or road types. Currently the full network is re-processed.
4. **Snapshot validation** — Detecting if the `.ter` file has been modified since the snapshot was created (e.g., by manual editing). A hash of the terrain parameters could be stored in the snapshot header.
