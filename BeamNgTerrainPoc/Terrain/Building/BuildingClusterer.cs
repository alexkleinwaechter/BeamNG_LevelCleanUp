using System.Numerics;
using BeamNG.Procedural3D.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Groups buildings into spatial clusters using a uniform grid.
/// Each building's world-space centroid determines which grid cell it belongs to.
/// This guarantees every building ends up in exactly one cluster (no duplicates, no omissions).
/// </summary>
public class BuildingClusterer
{
    /// <summary>
    /// Clusters buildings into grid cells of the specified size.
    /// </summary>
    /// <param name="buildings">All buildings with WorldPosition already assigned (post-coordinate-transform).</param>
    /// <param name="cellSizeMeters">Grid cell size in meters. Must be > 0.</param>
    /// <returns>List of clusters, each containing one or more buildings.</returns>
    /// <exception cref="ArgumentException">If cellSizeMeters is not positive.</exception>
    /// <exception cref="InvalidOperationException">If building count invariant is violated.</exception>
    public List<BuildingCluster> ClusterBuildings(IReadOnlyList<BuildingData> buildings, float cellSizeMeters)
    {
        if (cellSizeMeters <= 0)
            throw new ArgumentException("Cell size must be positive.", nameof(cellSizeMeters));

        if (buildings.Count == 0)
            return new List<BuildingCluster>();

        // Group buildings by grid cell
        var cellGroups = new Dictionary<(int CellX, int CellY), List<BuildingData>>();

        foreach (var building in buildings)
        {
            int cellX = (int)MathF.Floor(building.WorldPosition.X / cellSizeMeters);
            int cellY = (int)MathF.Floor(building.WorldPosition.Y / cellSizeMeters);
            var key = (cellX, cellY);

            if (!cellGroups.TryGetValue(key, out var group))
            {
                group = new List<BuildingData>();
                cellGroups[key] = group;
            }

            group.Add(building);
        }

        // Build cluster objects
        var clusters = new List<BuildingCluster>(cellGroups.Count);

        foreach (var ((cellX, cellY), group) in cellGroups)
        {
            // Anchor position = centroid of all buildings in this cell
            var anchor = ComputeCentroid(group);

            clusters.Add(new BuildingCluster
            {
                CellX = cellX,
                CellY = cellY,
                AnchorPosition = anchor,
                Buildings = group
            });
        }

        // Invariant: every building accounted for exactly once
        int totalClustered = clusters.Sum(c => c.Buildings.Count);
        if (totalClustered != buildings.Count)
        {
            throw new InvalidOperationException(
                $"Building clustering invariant violated: {totalClustered} buildings in clusters " +
                $"but {buildings.Count} buildings total. This is a bug.");
        }

        Console.WriteLine($"BuildingClusterer: {buildings.Count} buildings → {clusters.Count} clusters " +
                          $"(cell size: {cellSizeMeters}m, avg {buildings.Count / (float)clusters.Count:F1} buildings/cluster)");

        return clusters;
    }

    private static Vector3 ComputeCentroid(List<BuildingData> buildings)
    {
        var sum = Vector3.Zero;
        foreach (var b in buildings)
            sum += b.WorldPosition;
        return sum / buildings.Count;
    }
}

/// <summary>
/// A spatial cluster of buildings that will be merged into a single DAE file.
/// </summary>
public class BuildingCluster
{
    /// <summary>Grid cell X index.</summary>
    public int CellX { get; init; }

    /// <summary>Grid cell Y index.</summary>
    public int CellY { get; init; }

    /// <summary>
    /// World-space anchor position (centroid of all buildings in this cluster).
    /// Used as the TSStatic position; building geometry is offset relative to this.
    /// </summary>
    public Vector3 AnchorPosition { get; init; }

    /// <summary>Buildings in this cluster.</summary>
    public List<BuildingData> Buildings { get; init; } = new();

    /// <summary>
    /// File name for the cluster DAE (without directory).
    /// </summary>
    public string FileName => $"cluster_{CellX}_{CellY}.dae";

    /// <summary>
    /// Scene object name for the TSStatic entry.
    /// </summary>
    public string SceneName => $"cluster_{CellX}_{CellY}";
}
