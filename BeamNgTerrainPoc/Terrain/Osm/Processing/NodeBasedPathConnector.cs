using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Connects OSM road paths using node-based connectivity (shared OSM node IDs)
/// instead of purely geometric endpoint proximity matching.
/// 
/// Uses a three-tier strategy:
///   Tier 1 — Shared Node ID: two paths share an OSM node at their endpoints (highest confidence).
///            No distance check needed — OSM topology guarantees connectivity.
///   Tier 2 — Same Name/Ref + Proximity: paths share a road name or ref tag and their
///            endpoints are within the distance tolerance.
///   Tier 3 — Proximity Only: fallback for cropped paths that lost node IDs at terrain
///            boundaries. Only applies when at least one matched endpoint has a null node ID.
/// 
/// Anti-merge rules prevent inappropriate merges:
///   - A shared node used by 3+ path endpoints is a junction, not a continuation —
///     UNLESS both paths form a logical through-road (compatible type, no conflicting names,
///     not roundabout, deflection angle &lt; 90°). This through-junction exception creates
///     longer splines that improve elevation smoothing quality. Name/ref matching is NOT
///     required — unnamed roads merge freely through junctions based on angle alone.
///   - Different highway types should not merge (e.g., motorway + residential).
///   - Conflicting non-empty name tags prevent merging.
/// </summary>
internal static class NodeBasedPathConnector
{
    /// <summary>
    /// Highway type compatibility groups. A main road type and its _link variant
    /// are considered compatible and may merge. All other cross-type combinations
    /// are blocked by the anti-merge rule.
    /// </summary>
    private static readonly Dictionary<string, string> HighwayTypeGroups = new()
    {
        ["motorway"] = "motorway",
        ["motorway_link"] = "motorway",
        ["trunk"] = "trunk",
        ["trunk_link"] = "trunk",
        ["primary"] = "primary",
        ["primary_link"] = "primary",
        ["secondary"] = "secondary",
        ["secondary_link"] = "secondary",
        ["tertiary"] = "tertiary",
        ["tertiary_link"] = "tertiary",
        ["residential"] = "residential",
        ["unclassified"] = "unclassified",
        ["living_street"] = "living_street",
        ["service"] = "service",
        ["track"] = "track",
        ["path"] = "track",
        ["footway"] = "footway",
        ["cycleway"] = "cycleway",
        ["bridleway"] = "bridleway",
        ["steps"] = "steps",
        ["pedestrian"] = "pedestrian",
    };

    /// <summary>
    /// Connects paths using the three-tier node-based strategy.
    /// </summary>
    /// <param name="paths">Paths with OSM metadata to connect.</param>
    /// <param name="tolerance">Maximum distance for geometric proximity fallback (in meters).</param>
    /// <returns>Connected paths (bare point lists — metadata consumed during merging).</returns>
    public static List<List<Vector2>> Connect(List<PathWithMetadata> paths, float tolerance)
    {
        if (paths.Count <= 1)
            return paths.Select(p => p.Points).ToList();

        // Step 1: Build node valence map — count how many path endpoints share each node ID.
        // Nodes with valence >= 3 are junctions where paths should NOT merge.
        var nodeValence = BuildNodeValenceMap(paths);

        // Step 2: Clone paths into working list (originals are not modified)
        var working = paths.Select(ClonePath).ToList();
        var toleranceSquared = tolerance * tolerance;

        int tier1Merges = 0;
        int tier1ThroughJunctionMerges = 0;
        int tier2Merges = 0;
        int tier3Merges = 0;
        int iterations = 0;
        bool didMerge;

        do
        {
            didMerge = false;
            iterations++;

            for (int i = 0; i < working.Count && !didMerge; i++)
            {
                var path1 = working[i];
                if (path1.Points.Count < 2) continue;

                for (int j = i + 1; j < working.Count; j++)
                {
                    var path2 = working[j];
                    if (path2.Points.Count < 2) continue;

                    // Name conflict applies to all tiers
                    if (HaveConflictingNames(path1, path2))
                        continue;

                    // Try Tier 1: Shared Node ID (highest confidence)
                    // At valence-2 nodes (simple continuation), OSM topology is definitive —
                    // any highway type transition is valid (e.g., track → path, residential → service).
                    // Type compatibility is only checked at junction nodes (valence >= 3) inside TryMergeByNodeId.
                    var tier1Result = TryMergeByNodeId(path1, path2, nodeValence, ref tier1ThroughJunctionMerges);
                    if (tier1Result != null)
                    {
                        working[i] = tier1Result;
                        working.RemoveAt(j);
                        didMerge = true;
                        tier1Merges++;
                        break;
                    }

                    // Type compatibility — only needed for proximity-based tiers where we
                    // don't have node-based certainty about the connection
                    if (!AreTypesCompatible(path1, path2))
                        continue;

                    // Try Tier 2: Same Name/Ref + Proximity
                    var tier2Result = TryMergeBySameNameAndProximity(path1, path2, toleranceSquared);
                    if (tier2Result != null)
                    {
                        working[i] = tier2Result;
                        working.RemoveAt(j);
                        didMerge = true;
                        tier2Merges++;
                        break;
                    }

                    // Try Tier 3: Proximity Only (for cropped paths with null node IDs)
                    var tier3Result = TryMergeByProximityWithCropped(path1, path2, toleranceSquared);
                    if (tier3Result != null)
                    {
                        working[i] = tier3Result;
                        working.RemoveAt(j);
                        didMerge = true;
                        tier3Merges++;
                        break;
                    }
                }
            }
        } while (didMerge && iterations < 1000); // Safety limit

        int totalMerges = tier1Merges + tier2Merges + tier3Merges;
        Console.WriteLine($"  Node-based path joining: {totalMerges} merges in {iterations} iterations " +
            $"(Tier1/NodeID: {tier1Merges} [{tier1ThroughJunctionMerges} through-junction], " +
            $"Tier2/Name+Prox: {tier2Merges}, Tier3/CroppedProx: {tier3Merges}), " +
            $"tolerance={tolerance:F2}m");

        return working.Select(p => p.Points).ToList();
    }

    // ========================================================================================
    //  Valence Map
    // ========================================================================================

    /// <summary>
    /// Builds a map of nodeId → number of path endpoints that reference this node.
    /// Nodes with valence >= 3 are junctions (T-junction, crossroads, etc.) where
    /// merging is blocked unless through-road conditions are met.
    /// </summary>
    private static Dictionary<long, int> BuildNodeValenceMap(List<PathWithMetadata> paths)
    {
        var valence = new Dictionary<long, int>();
        foreach (var path in paths)
        {
            if (path.StartNodeId.HasValue)
            {
                valence.TryGetValue(path.StartNodeId.Value, out int count);
                valence[path.StartNodeId.Value] = count + 1;
            }
            if (path.EndNodeId.HasValue)
            {
                valence.TryGetValue(path.EndNodeId.Value, out int count);
                valence[path.EndNodeId.Value] = count + 1;
            }
        }
        return valence;
    }

    private static bool IsJunctionNode(long nodeId, Dictionary<long, int> nodeValence)
    {
        return nodeValence.TryGetValue(nodeId, out int valence) && valence >= 3;
    }

    // ========================================================================================
    //  Tier 1 — Shared Node ID
    // ========================================================================================

    /// <summary>
    /// Attempts to merge two paths that share an OSM node ID at their endpoints.
    /// At valence-2 nodes (simple continuation), merges unconditionally — OSM topology
    /// is definitive, regardless of highway type differences (e.g., track → path).
    /// At junction nodes (valence >= 3), merging requires compatible highway types AND
    /// through-road conditions (not roundabout, deflection angle &lt; 90°).
    /// </summary>
    private static PathWithMetadata? TryMergeByNodeId(
        PathWithMetadata path1, PathWithMetadata path2,
        Dictionary<long, int> nodeValence,
        ref int throughJunctionMerges)
    {
        // path1.End → path2.Start
        if (path1.EndNodeId.HasValue && path2.StartNodeId.HasValue &&
            path1.EndNodeId.Value == path2.StartNodeId.Value)
        {
            if (IsJunctionNode(path1.EndNodeId.Value, nodeValence))
            {
                if (!AreTypesCompatible(path1, path2))
                    return null;
                if (path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                    IsThroughRoad(path1, path2, path1.Points[^2], path1.Points[^1], path2.Points[1]))
                {
                    throughJunctionMerges++;
                    return MergeEndToStart(path1, path2);
                }
                return null;
            }
            return MergeEndToStart(path1, path2);
        }

        // path1.End → path2.End (reverse path2)
        if (path1.EndNodeId.HasValue && path2.EndNodeId.HasValue &&
            path1.EndNodeId.Value == path2.EndNodeId.Value)
        {
            if (IsJunctionNode(path1.EndNodeId.Value, nodeValence))
            {
                if (!AreTypesCompatible(path1, path2))
                    return null;
                if (path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                    IsThroughRoad(path1, path2, path1.Points[^2], path1.Points[^1], path2.Points[^2]))
                {
                    throughJunctionMerges++;
                    return MergeEndToEnd(path1, path2);
                }
                return null;
            }
            return MergeEndToEnd(path1, path2);
        }

        // path1.Start → path2.End
        if (path1.StartNodeId.HasValue && path2.EndNodeId.HasValue &&
            path1.StartNodeId.Value == path2.EndNodeId.Value)
        {
            if (IsJunctionNode(path1.StartNodeId.Value, nodeValence))
            {
                if (!AreTypesCompatible(path1, path2))
                    return null;
                if (path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                    IsThroughRoad(path1, path2, path2.Points[^2], path1.Points[0], path1.Points[1]))
                {
                    throughJunctionMerges++;
                    return MergeStartToEnd(path1, path2);
                }
                return null;
            }
            return MergeStartToEnd(path1, path2);
        }

        // path1.Start → path2.Start (reverse path2)
        if (path1.StartNodeId.HasValue && path2.StartNodeId.HasValue &&
            path1.StartNodeId.Value == path2.StartNodeId.Value)
        {
            if (IsJunctionNode(path1.StartNodeId.Value, nodeValence))
            {
                if (!AreTypesCompatible(path1, path2))
                    return null;
                if (path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                    IsThroughRoad(path1, path2, path2.Points[1], path1.Points[0], path1.Points[1]))
                {
                    throughJunctionMerges++;
                    return MergeStartToStart(path1, path2);
                }
                return null;
            }
            return MergeStartToStart(path1, path2);
        }

        return null;
    }

    // ========================================================================================
    //  Tier 2 — Same Name/Ref + Proximity
    // ========================================================================================

    /// <summary>
    /// Attempts to merge paths that share the same non-empty name or ref tag
    /// AND have endpoints within the proximity tolerance.
    /// </summary>
    private static PathWithMetadata? TryMergeBySameNameAndProximity(
        PathWithMetadata path1, PathWithMetadata path2,
        float toleranceSquared)
    {
        if (!ShareNameOrRef(path1, path2))
            return null;

        return TryMergeByProximity(path1, path2, toleranceSquared);
    }

    // ========================================================================================
    //  Tier 3 — Proximity Only (Cropped Paths)
    // ========================================================================================

    /// <summary>
    /// Attempts to merge paths by proximity, but ONLY when at least one of the matched
    /// endpoints has a null node ID (cropped at terrain boundary).
    /// 
    /// This prevents proximity-based merges from overriding node-based topology for paths
    /// that have full node ID information. If both endpoints have node IDs but they don't
    /// match, those are topologically different endpoints that happen to be geometrically
    /// close — they should NOT merge.
    /// </summary>
    private static PathWithMetadata? TryMergeByProximityWithCropped(
        PathWithMetadata path1, PathWithMetadata path2,
        float toleranceSquared)
    {
        var p1End = path1.Points[^1];
        var p2Start = path2.Points[0];
        var p2End = path2.Points[^1];
        var p1Start = path1.Points[0];

        // End → Start
        if (Vector2.DistanceSquared(p1End, p2Start) <= toleranceSquared &&
            (!path1.EndNodeId.HasValue || !path2.StartNodeId.HasValue))
        {
            return MergeEndToStart(path1, path2);
        }

        // End → End
        if (Vector2.DistanceSquared(p1End, p2End) <= toleranceSquared &&
            (!path1.EndNodeId.HasValue || !path2.EndNodeId.HasValue))
        {
            return MergeEndToEnd(path1, path2);
        }

        // Start → End
        if (Vector2.DistanceSquared(p1Start, p2End) <= toleranceSquared &&
            (!path1.StartNodeId.HasValue || !path2.EndNodeId.HasValue))
        {
            return MergeStartToEnd(path1, path2);
        }

        // Start → Start
        if (Vector2.DistanceSquared(p1Start, p2Start) <= toleranceSquared &&
            (!path1.StartNodeId.HasValue || !path2.StartNodeId.HasValue))
        {
            return MergeStartToStart(path1, path2);
        }

        return null;
    }

    // ========================================================================================
    //  Proximity Merge (used by Tier 2 after name/ref matching)
    // ========================================================================================

    /// <summary>
    /// Checks all 4 endpoint combinations for geometric proximity.
    /// Used by Tier 2 after confirming name/ref compatibility.
    /// Rejects U-turns on one-way divided roads (e.g. roundabout exit + entry
    /// sharing a divergence node with the same name/ref).
    /// </summary>
    private static PathWithMetadata? TryMergeByProximity(
        PathWithMetadata path1, PathWithMetadata path2,
        float toleranceSquared)
    {
        var p1End = path1.Points[^1];
        var p2Start = path2.Points[0];
        var p2End = path2.Points[^1];
        var p1Start = path1.Points[0];
        bool bothOneway = AreBothOneway(path1, path2);

        // End → Start
        if (Vector2.DistanceSquared(p1End, p2Start) <= toleranceSquared)
        {
            if (bothOneway && path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                IsUturnAtConnection(path1.Points[^2], p1End, path2.Points[1]))
                return null;
            return MergeEndToStart(path1, path2);
        }

        // End → End
        if (Vector2.DistanceSquared(p1End, p2End) <= toleranceSquared)
        {
            if (bothOneway && path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                IsUturnAtConnection(path1.Points[^2], p1End, path2.Points[^2]))
                return null;
            return MergeEndToEnd(path1, path2);
        }

        // Start → End
        if (Vector2.DistanceSquared(p1Start, p2End) <= toleranceSquared)
        {
            if (bothOneway && path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                IsUturnAtConnection(path2.Points[^2], p2End, path1.Points[1]))
                return null;
            return MergeStartToEnd(path1, path2);
        }

        // Start → Start
        if (Vector2.DistanceSquared(p1Start, p2Start) <= toleranceSquared)
        {
            if (bothOneway && path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                IsUturnAtConnection(path2.Points[1], p2Start, path1.Points[1]))
                return null;
            return MergeStartToStart(path1, path2);
        }

        return null;
    }

    // ========================================================================================
    //  Merge Operations
    // ========================================================================================
    //
    // Each merge returns a NEW PathWithMetadata combining the two input paths' geometry.
    // The merged path inherits:
    //   - Tags from path1 (the merge base)
    //   - OsmWayId from path1
    //   - Structure metadata from path1
    //   - StartNodeId/EndNodeId from the appropriate outer endpoints

    /// <summary>
    /// path1.End → path2.Start: append path2 to path1.
    /// Result: [path1 points...] + [path2 points, skipping shared first point]
    /// </summary>
    private static PathWithMetadata MergeEndToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path1.Points);
        merged.AddRange(path2.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            startNodeId: path1.StartNodeId,
            endNodeId: path2.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    /// <summary>
    /// path1.End → path2.End: append reversed path2 to path1.
    /// Result: [path1 points...] + [path2 reversed, skipping shared first (=original last)]
    /// </summary>
    private static PathWithMetadata MergeEndToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path1.Points);
        for (int k = path2.Points.Count - 2; k >= 0; k--)
            merged.Add(path2.Points[k]);
        return new PathWithMetadata(
            merged,
            startNodeId: path1.StartNodeId,
            endNodeId: path2.StartNodeId, // path2 reversed → its original start is now the end
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    /// <summary>
    /// path1.Start → path2.End: prepend path2 to path1.
    /// Result: [path2 points...] + [path1 points, skipping shared first]
    /// </summary>
    private static PathWithMetadata MergeStartToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path2.Points);
        merged.AddRange(path1.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            startNodeId: path2.StartNodeId,
            endNodeId: path1.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    /// <summary>
    /// path1.Start → path2.Start: prepend reversed path2 to path1.
    /// Result: [path2 reversed...] + [path1 points, skipping shared first]
    /// </summary>
    private static PathWithMetadata MergeStartToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        for (int k = path2.Points.Count - 1; k >= 0; k--)
            merged.Add(path2.Points[k]);
        merged.AddRange(path1.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            startNodeId: path2.EndNodeId, // path2 reversed → its original end is now the start
            endNodeId: path1.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    // ========================================================================================
    //  Anti-Merge Rules
    // ========================================================================================

    /// <summary>
    /// Checks if two paths have compatible highway types for merging.
    /// Incompatible types (e.g., motorway + residential) should not merge even if they
    /// share an endpoint — they meet at a junction, not a continuation.
    /// 
    /// If either path has no highway tag, merging is allowed (don't block on missing data).
    /// </summary>
    private static bool AreTypesCompatible(PathWithMetadata path1, PathWithMetadata path2)
    {
        var group1 = GetHighwayGroup(path1);
        var group2 = GetHighwayGroup(path2);

        // Missing highway tag → allow merging (don't block on incomplete data)
        if (group1 == null || group2 == null)
            return true;

        return string.Equals(group1, group2, StringComparison.Ordinal);
    }

    private static string? GetHighwayGroup(PathWithMetadata path)
    {
        if (!path.Tags.TryGetValue("highway", out var highway) || string.IsNullOrEmpty(highway))
            return null;

        return HighwayTypeGroups.TryGetValue(highway, out var group) ? group : highway;
    }

    /// <summary>
    /// Returns true if both paths have non-empty name tags that differ AND do not share
    /// the same ref tag. Empty/missing names don't trigger a conflict — unnamed roads can
    /// merge with named roads. A shared ref (route number like "D 914", "B51") overrides
    /// name conflicts because road names commonly change at bridges, intersections, and
    /// administrative boundaries while the route reference stays the same.
    /// </summary>
    private static bool HaveConflictingNames(PathWithMetadata path1, PathWithMetadata path2)
    {
        path1.Tags.TryGetValue("name", out var name1);
        path2.Tags.TryGetValue("name", out var name2);

        if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
            return false;

        if (string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase))
            return false; // same name — no conflict

        // Names differ — but a shared ref overrides the conflict
        path1.Tags.TryGetValue("ref", out var ref1);
        path2.Tags.TryGetValue("ref", out var ref2);
        if (!string.IsNullOrEmpty(ref1) && !string.IsNullOrEmpty(ref2) &&
            string.Equals(ref1, ref2, StringComparison.OrdinalIgnoreCase))
        {
            return false; // same route reference — name change is expected (e.g., at bridge)
        }

        return true; // different names, no shared ref — genuine conflict
    }

    /// <summary>
    /// Returns true if both paths share a non-empty name or ref tag value.
    /// </summary>
    private static bool ShareNameOrRef(PathWithMetadata path1, PathWithMetadata path2)
    {
        // Check name tag
        path1.Tags.TryGetValue("name", out var name1);
        path2.Tags.TryGetValue("name", out var name2);
        if (!string.IsNullOrEmpty(name1) && !string.IsNullOrEmpty(name2) &&
            string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check ref tag
        path1.Tags.TryGetValue("ref", out var ref1);
        path2.Tags.TryGetValue("ref", out var ref2);
        if (!string.IsNullOrEmpty(ref1) && !string.IsNullOrEmpty(ref2) &&
            string.Equals(ref1, ref2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // ========================================================================================
    //  Through-Junction Merging
    // ========================================================================================

    /// <summary>
    /// Checks whether two paths form a logical through-road at a junction node.
    /// Through-junction merging is allowed at junction nodes (valence >= 3) when ALL of:
    ///   1. Compatible highway types (already checked before Tier 1 call)
    ///   2. No conflicting names (already checked before Tier 1 call)
    ///   3. Neither path is tagged junction=roundabout (roundabout rings stay separate)
    ///   4. The deflection angle at the junction is below 90° (prevents sharp-turn merges)
    ///
    /// Name/ref matching is NOT required — unnamed roads (common for unclassified, service,
    /// residential) should merge through junctions to create longer splines for smooth
    /// elevation profiles. The angle + type + conflicting-name guards are sufficient.
    /// </summary>
    private static bool IsThroughRoad(
        PathWithMetadata path1, PathWithMetadata path2,
        Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext)
    {
        // Condition 3: Neither path is a roundabout
        if (IsRoundabout(path1) || IsRoundabout(path2))
            return false;

        // Condition 4: Deflection angle < 90° at junction
        if (!IsDeflectionBelow90(incomingPrev, connectionPoint, outgoingNext))
            return false;

        return true;
    }

    private static bool IsRoundabout(PathWithMetadata path)
    {
        return path.Tags.TryGetValue("junction", out var junction) &&
               string.Equals(junction, "roundabout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the deflection angle at the connection point is below 90°,
    /// meaning the road continues roughly in the same direction through the junction.
    /// dot > 0 corresponds to deflection < 90° (cos(90°) = 0).
    /// </summary>
    private static bool IsDeflectionBelow90(Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext)
    {
        var dirIn = connectionPoint - incomingPrev;
        var dirOut = outgoingNext - connectionPoint;

        float lenInSq = dirIn.LengthSquared();
        float lenOutSq = dirOut.LengthSquared();
        if (lenInSq < 1e-8f || lenOutSq < 1e-8f)
            return false; // degenerate segments — don't merge

        dirIn /= MathF.Sqrt(lenInSq);
        dirOut /= MathF.Sqrt(lenOutSq);
        float dot = Vector2.Dot(dirIn, dirOut);

        return dot > 0f;
    }

    // ========================================================================================
    //  U-turn Detection
    // ========================================================================================

    /// <summary>
    /// Checks whether merging at a connection point would create a U-turn (sharp reversal).
    /// Only called when both ways are one-way, so a sharp reversal indicates separate
    /// carriageways of a divided road rather than a hairpin curve on a mountain pass.
    /// </summary>
    private static bool IsUturnAtConnection(Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext)
    {
        var dirIn = connectionPoint - incomingPrev;
        var dirOut = outgoingNext - connectionPoint;

        float lenInSq = dirIn.LengthSquared();
        float lenOutSq = dirOut.LengthSquared();
        if (lenInSq < 1e-8f || lenOutSq < 1e-8f)
            return false;

        dirIn /= MathF.Sqrt(lenInSq);
        dirOut /= MathF.Sqrt(lenOutSq);
        float dot = Vector2.Dot(dirIn, dirOut);

        // dot < -0.7 corresponds to angle > ~135° — a clear U-turn
        return dot < -0.7f;
    }

    /// <summary>
    /// Returns true if both paths have oneway=yes (or equivalent). One-way roads that reverse
    /// direction at a shared node are separate carriageways of a divided road, not a hairpin.
    /// </summary>
    private static bool AreBothOneway(PathWithMetadata path1, PathWithMetadata path2)
    {
        return IsOneway(path1.Tags) && IsOneway(path2.Tags);
    }

    private static bool IsOneway(Dictionary<string, string> tags)
    {
        return tags.TryGetValue("oneway", out var value) &&
               (value == "yes" || value == "true" || value == "1" || value == "-1");
    }

    // ========================================================================================
    //  Helpers
    // ========================================================================================

    private static PathWithMetadata ClonePath(PathWithMetadata source)
    {
        return new PathWithMetadata(
            new List<Vector2>(source.Points),
            source.StartNodeId,
            source.EndNodeId,
            source.OsmWayId,
            source.Tags,  // Tags are not mutated during merging — shared reference is safe
            source.IsBridge,
            source.IsTunnel,
            source.StructureType,
            source.Layer,
            source.BridgeStructureType);
    }
}
