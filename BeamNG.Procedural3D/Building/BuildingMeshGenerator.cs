namespace BeamNG.Procedural3D.Building;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Generates 3D meshes from BuildingData.
/// Produces separate meshes per material (wall material, roof material).
/// All coordinates are in local space relative to the building's ground center (centroid at floor level).
/// In local space: X/Y are horizontal, Z is up (BeamNG convention).
/// </summary>
public class BuildingMeshGenerator
{
    /// <summary>
    /// Generates all meshes for a single building.
    /// Returns meshes grouped by material name.
    /// </summary>
    public Dictionary<string, Mesh> GenerateMeshes(BuildingData building)
    {
        var meshes = new Dictionary<string, Mesh>();

        // Generate walls
        if (building.HasWalls && building.FootprintOuter.Count >= 3)
        {
            var wallMesh = GenerateWalls(building);
            if (wallMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.WallMaterial, wallMesh);
            }
        }

        // Generate flat roof (Phase 1 — only flat roofs)
        if (building.FootprintOuter.Count >= 3)
        {
            var roofMesh = GenerateFlatRoof(building);
            if (roofMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.RoofMaterial, roofMesh);
            }
        }

        // Generate floor (only for elevated building parts)
        if (building.MinHeight > 0 && building.FootprintOuter.Count >= 3)
        {
            var floorMesh = GenerateFloor(building);
            if (floorMesh != null && floorMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.WallMaterial, floorMesh);
            }
        }

        return meshes;
    }

    /// <summary>
    /// Generates wall quads for the building footprint.
    /// Each wall segment is a quad from MinHeight to WallHeight + MinHeight.
    /// UV: U = cumulative distance along wall (for texture tiling), V = height.
    /// Flat normals (each wall face has its own outward-facing normal).
    /// </summary>
    private Mesh GenerateWalls(BuildingData building)
    {
        var builder = new MeshBuilder()
            .WithName($"walls_{building.OsmId}")
            .WithMaterial(building.WallMaterial);

        var footprint = building.FootprintOuter;
        float bottom = building.MinHeight;
        float top = building.RoofBaseHeight;

        // Walk footprint edges — create a wall quad for each edge
        float cumulativeDistance = 0;

        for (int i = 0; i < footprint.Count; i++)
        {
            int next = (i + 1) % footprint.Count;

            Vector2 p0 = footprint[i];
            Vector2 p1 = footprint[next];

            float segmentLength = Vector2.Distance(p0, p1);
            if (segmentLength < 0.01f) continue; // Skip degenerate edges

            // Calculate outward-facing normal for this wall segment
            // For CCW polygon: outward normal is to the right of the edge direction
            Vector2 edge = p1 - p0;
            Vector3 wallNormal = Vector3.Normalize(new Vector3(edge.Y, -edge.X, 0));

            // Wall UV: U = distance along wall, V = height
            // This tiles the texture naturally along walls
            float u0 = cumulativeDistance;
            float u1 = cumulativeDistance + segmentLength;

            // Four corners of the wall quad (in local space, Z-up)
            // Bottom-left, bottom-right, top-right, top-left
            var bl = builder.AddVertex(
                new Vector3(p0.X, p0.Y, bottom),
                wallNormal,
                new Vector2(u0, 0));
            var br = builder.AddVertex(
                new Vector3(p1.X, p1.Y, bottom),
                wallNormal,
                new Vector2(u1, 0));
            var tr = builder.AddVertex(
                new Vector3(p1.X, p1.Y, top),
                wallNormal,
                new Vector2(u1, top - bottom));
            var tl = builder.AddVertex(
                new Vector3(p0.X, p0.Y, top),
                wallNormal,
                new Vector2(u0, top - bottom));

            // Add quad (CCW winding when viewed from outside)
            builder.AddQuad(bl, br, tr, tl);

            cumulativeDistance += segmentLength;
        }

        // Also generate walls for holes (inner rings face inward)
        if (building.FootprintHoles != null)
        {
            foreach (var hole in building.FootprintHoles)
            {
                cumulativeDistance = 0;
                for (int i = 0; i < hole.Count; i++)
                {
                    int next = (i + 1) % hole.Count;
                    Vector2 p0 = hole[i];
                    Vector2 p1 = hole[next];

                    float segmentLength = Vector2.Distance(p0, p1);
                    if (segmentLength < 0.01f) continue;

                    // For holes (CW winding), the inward-facing normal is the "outside"
                    Vector2 edge = p1 - p0;
                    Vector3 wallNormal = Vector3.Normalize(new Vector3(-edge.Y, edge.X, 0));

                    float u0 = cumulativeDistance;
                    float u1 = cumulativeDistance + segmentLength;

                    var bl = builder.AddVertex(new Vector3(p0.X, p0.Y, bottom), wallNormal, new Vector2(u0, 0));
                    var br = builder.AddVertex(new Vector3(p1.X, p1.Y, bottom), wallNormal, new Vector2(u1, 0));
                    var tr = builder.AddVertex(new Vector3(p1.X, p1.Y, top), wallNormal, new Vector2(u1, top - bottom));
                    var tl = builder.AddVertex(new Vector3(p0.X, p0.Y, top), wallNormal, new Vector2(u0, top - bottom));

                    builder.AddQuad(bl, br, tr, tl);

                    cumulativeDistance += segmentLength;
                }
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Generates the flat roof surface using polygon triangulation.
    /// Placed at the top of the building (Height above ground).
    /// </summary>
    private Mesh GenerateFlatRoof(BuildingData building)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        float roofZ = building.Height;
        Vector3 upNormal = Vector3.UnitZ;

        return GenerateHorizontalSurface(builder, building, roofZ, upNormal, false);
    }

    /// <summary>
    /// Generates the floor/bottom polygon for elevated building parts.
    /// Placed at MinHeight, facing downward.
    /// </summary>
    private Mesh? GenerateFloor(BuildingData building)
    {
        if (building.MinHeight <= 0)
            return null;

        var builder = new MeshBuilder()
            .WithName($"floor_{building.OsmId}")
            .WithMaterial(building.WallMaterial);

        float floorZ = building.MinHeight;
        Vector3 downNormal = -Vector3.UnitZ;

        return GenerateHorizontalSurface(builder, building, floorZ, downNormal, true);
    }

    /// <summary>
    /// Generates a horizontal surface (roof or floor) by triangulating the building footprint.
    /// </summary>
    private Mesh GenerateHorizontalSurface(
        MeshBuilder builder,
        BuildingData building,
        float z,
        Vector3 normal,
        bool reverseWinding)
    {
        var outerRing = building.FootprintOuter;

        // Prepare holes for triangulation
        IReadOnlyList<IReadOnlyList<Vector2>>? holes = null;
        if (building.FootprintHoles is { Count: > 0 })
        {
            holes = building.FootprintHoles
                .Select(h => (IReadOnlyList<Vector2>)h)
                .ToList();
        }

        // Triangulate the polygon
        var triangleIndices = PolygonTriangulator.Triangulate(outerRing, holes);

        if (triangleIndices.Count < 3)
            return builder.Build();

        // Build flattened vertex list (outer + holes)
        var allVertices = new List<Vector2>(outerRing);
        if (building.FootprintHoles != null)
        {
            foreach (var hole in building.FootprintHoles)
            {
                allVertices.AddRange(hole);
            }
        }

        // Add vertices to mesh with planar UV mapping
        int baseIndex = builder.VertexCount;
        foreach (var v2d in allVertices)
        {
            var position = new Vector3(v2d.X, v2d.Y, z);
            // Planar XY UV mapping for top-down texturing (in local space)
            var uv = new Vector2(v2d.X, v2d.Y);
            builder.AddVertex(position, normal, uv);
        }

        // Add triangles from earcut indices
        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            int i0 = baseIndex + triangleIndices[i];
            int i1 = baseIndex + triangleIndices[i + 1];
            int i2 = baseIndex + triangleIndices[i + 2];

            if (reverseWinding)
                builder.AddTriangle(i0, i2, i1);
            else
                builder.AddTriangle(i0, i1, i2);
        }

        return builder.Build();
    }

    /// <summary>
    /// Merges a mesh into the dictionary, combining meshes that share the same material.
    /// </summary>
    private static void AddOrMergeMesh(Dictionary<string, Mesh> meshes, string materialKey, Mesh mesh)
    {
        if (!meshes.TryGetValue(materialKey, out var existing))
        {
            meshes[materialKey] = mesh;
            return;
        }

        // Merge into existing mesh
        int baseIndex = existing.Vertices.Count;
        existing.Vertices.AddRange(mesh.Vertices);
        foreach (var tri in mesh.Triangles)
        {
            existing.Triangles.Add(new Triangle(
                tri.V0 + baseIndex,
                tri.V1 + baseIndex,
                tri.V2 + baseIndex));
        }
    }
}
