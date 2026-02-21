using System.Numerics;
using BeamNG.Procedural3D.Building;
using Bldg = BeamNG.Procedural3D.Building.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
///     Common interface for items that can be spatially clustered.
///     Must provide a world position for grid cell assignment.
/// </summary>
public interface IClusterable
{
    Vector3 WorldPosition { get; }
}

/// <summary>
///     Common interface for cluster metadata needed by the export pipeline.
/// </summary>
public interface IClusterInfo
{
    string FileName { get; }
    string SceneName { get; }
    Vector3 AnchorPosition { get; }
    int BuildingCount { get; }
}

/// <summary>
///     Groups buildings into spatial clusters using a uniform grid.
///     Each building's world-space centroid determines which grid cell it belongs to.
///     This guarantees every building ends up in exactly one cluster (no duplicates, no omissions).
/// </summary>
public class BuildingClusterer
{
    /// <summary>
    ///     Clusters BuildingData objects into grid cells.
    /// </summary>
    public List<BuildingCluster<BuildingData>> ClusterBuildings(
        IReadOnlyList<BuildingData> buildings, float cellSizeMeters)
    {
        return ClusterItems(buildings, cellSizeMeters, b => b.WorldPosition);
    }

    /// <summary>
    ///     Clusters multi-part Building objects into grid cells.
    /// </summary>
    public List<BuildingCluster<Bldg>> ClusterMultiPartBuildings(
        IReadOnlyList<Bldg> buildings, float cellSizeMeters)
    {
        return ClusterItems(buildings, cellSizeMeters, b => b.WorldPosition);
    }

    /// <summary>
    ///     Generic clustering implementation for any item type with a world position.
    /// </summary>
    private static List<BuildingCluster<T>> ClusterItems<T>(
        IReadOnlyList<T> items,
        float cellSizeMeters,
        Func<T, Vector3> getPosition)
    {
        if (cellSizeMeters <= 0)
            throw new ArgumentException("Cell size must be positive.", nameof(cellSizeMeters));

        if (items.Count == 0)
            return new List<BuildingCluster<T>>();

        // Group by grid cell
        var cellGroups = new Dictionary<(int CellX, int CellY), List<T>>();

        foreach (var item in items)
        {
            var pos = getPosition(item);
            var cellX = (int)MathF.Floor(pos.X / cellSizeMeters);
            var cellY = (int)MathF.Floor(pos.Y / cellSizeMeters);
            var key = (cellX, cellY);

            if (!cellGroups.TryGetValue(key, out var group))
            {
                group = new List<T>();
                cellGroups[key] = group;
            }

            group.Add(item);
        }

        // Build cluster objects
        var clusters = new List<BuildingCluster<T>>(cellGroups.Count);

        foreach (var ((cellX, cellY), group) in cellGroups)
        {
            var anchor = ComputeCentroid(group, getPosition);

            clusters.Add(new BuildingCluster<T>
            {
                CellX = cellX,
                CellY = cellY,
                AnchorPosition = anchor,
                Buildings = group
            });
        }

        // Invariant: every item accounted for exactly once
        var totalClustered = clusters.Sum(c => c.Buildings.Count);
        if (totalClustered != items.Count)
            throw new InvalidOperationException(
                $"Building clustering invariant violated: {totalClustered} items in clusters " +
                $"but {items.Count} items total. This is a bug.");

        Console.WriteLine($"BuildingClusterer: {items.Count} buildings → {clusters.Count} clusters " +
                          $"(cell size: {cellSizeMeters}m, avg {items.Count / (float)clusters.Count:F1} buildings/cluster)");

        return clusters;
    }

    private static Vector3 ComputeCentroid<T>(List<T> items, Func<T, Vector3> getPosition)
    {
        var sum = Vector3.Zero;
        foreach (var item in items)
            sum += getPosition(item);
        return sum / items.Count;
    }
}

/// <summary>
///     A spatial cluster of buildings that will be merged into a single DAE file.
///     Generic over the building type (BuildingData for legacy, Building for multi-part).
/// </summary>
public class BuildingCluster<T> : IClusterInfo
{
    /// <summary>Grid cell X index.</summary>
    public int CellX { get; init; }

    /// <summary>Grid cell Y index.</summary>
    public int CellY { get; init; }

    /// <summary>Buildings in this cluster.</summary>
    public List<T> Buildings { get; init; } = new();

    /// <summary>
    ///     World-space anchor position (centroid of all buildings in this cluster).
    ///     Used as the TSStatic position; building geometry is offset relative to this.
    /// </summary>
    public Vector3 AnchorPosition { get; init; }

    /// <summary>
    ///     File name for the cluster DAE (without directory).
    /// </summary>
    public string FileName => $"cluster_{CellX}_{CellY}.dae";

    /// <summary>
    ///     Scene object name for the TSStatic entry.
    /// </summary>
    public string SceneName => $"cluster_{CellX}_{CellY}";

    /// <summary>
    ///     Number of buildings in this cluster.
    /// </summary>
    public int BuildingCount => Buildings.Count;
}