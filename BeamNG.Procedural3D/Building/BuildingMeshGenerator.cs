namespace BeamNG.Procedural3D.Building;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Building.Roof;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Generates 3D meshes from BuildingData.
/// Produces separate meshes per material (wall material, roof material).
/// All coordinates are in local space relative to the building's ground center (centroid at floor level).
/// In local space: X/Y are horizontal, Z is up (BeamNG convention).
///
/// UV coordinates are divided by the material's texture scale so textures tile at the
/// correct real-world size (e.g., a 1.4m brick texture repeats every 1.4m along the wall).
///
/// Port of OSM2World's BuildingPart rendering flow:
/// 1. Create shared roof instance (all roof shapes go through HeightfieldRoof)
/// 2. Generate walls via ExteriorBuildingWall.SplitIntoWalls() — per-vertex roof height
/// 3. Generate roof mesh via HeightfieldRoof.GenerateMesh()
/// 4. Generate floor if elevated (building:part with min_height)
/// </summary>
public class BuildingMeshGenerator
{
    /// <summary>
    /// Generates all meshes for a building with multiple parts.
    /// Each part gets its own roof and walls; meshes are merged by material key across all parts.
    /// </summary>
    /// <param name="building">The building with one or more parts.</param>
    /// <param name="getTextureScale">Lookup: material key → (scaleU, scaleV) in meters per texture repeat.
    /// If null, raw meter UVs are used (1 UV unit = 1 meter).</param>
    public Dictionary<string, Mesh> GenerateMeshes(
        Building building,
        Func<string, Vector2>? getTextureScale = null)
    {
        var meshes = new Dictionary<string, Mesh>();

        foreach (var part in building.Parts)
        {
            var partMeshes = GenerateMeshes(part, getTextureScale);
            foreach (var (materialKey, mesh) in partMeshes)
            {
                AddOrMergeMesh(meshes, materialKey, mesh);
            }
        }

        return meshes;
    }

    /// <summary>
    /// Generates all meshes for a single building part (or simple building without parts).
    /// Returns meshes grouped by material key.
    /// </summary>
    /// <param name="building">The building data (single part).</param>
    /// <param name="getTextureScale">Lookup: material key → (scaleU, scaleV) in meters per texture repeat.
    /// If null, raw meter UVs are used (1 UV unit = 1 meter).</param>
    public Dictionary<string, Mesh> GenerateMeshes(
        BuildingData building,
        Func<string, Vector2>? getTextureScale = null)
    {
        var meshes = new Dictionary<string, Mesh>();

        if (building.FootprintOuter.Count < 3)
            return meshes;

        // Ensure non-flat roofs have a roof height allocated
        EnsureRoofHeight(building);

        // Prepare polygon: remove closing vertex, ensure CCW winding
        // This prepared polygon is shared between roof and wall generation (critical for coordinate consistency)
        var polygon = RoofWithRidge.PreparePolygon(building.FootprintOuter);
        if (polygon == null || polygon.Count < 3)
            return meshes;

        // Create shared roof instance — ALL roof shapes (including flat) go through HeightfieldRoof
        // Port of: roof = Roof.createRoofForShape(...) in BuildingPart.java
        var roof = CreateRoof(building, polygon);

        // Generate walls using ExteriorBuildingWall (roof-aware top boundaries)
        // Port of: walls = splitIntoWalls(area, this) + wall.renderTo() in BuildingPart.java
        // Note: walls are ALWAYS generated, even when HasWalls=false (roof, carport).
        // HasWalls=false only suppresses windows/doors — wall surfaces still provide slab thickness.
        // Port of OSM2World: BuildingPart.java always calls splitIntoWalls() regardless of hasWalls.
        {
            var wallScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;
            var wallMesh = GenerateWalls(building, roof, polygon, wallScale);
            if (wallMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.WallMaterial, wallMesh);
            }
        }

        // Generate roof mesh using the same shared roof instance
        {
            var roofScale = getTextureScale?.Invoke(building.RoofMaterial) ?? Vector2.One;
            var roofMesh = GenerateRoofMesh(building, roof, roofScale);
            if (roofMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.RoofMaterial, roofMesh);
            }
        }

        // Generate floor (only for elevated building parts)
        if (building.MinHeight > 0)
        {
            var floorScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;
            var floorMesh = GenerateFloor(building, floorScale);
            if (floorMesh != null && floorMesh.HasGeometry)
            {
                AddOrMergeMesh(meshes, building.WallMaterial, floorMesh);
            }
        }

        return meshes;
    }

    /// <summary>
    /// Generates meshes for up to 3 LOD levels of a single building part.
    /// Returns a dictionary: LOD index (0..maxLodLevel) → material key → Mesh.
    /// LOD0: solid walls + roof (no windows)
    /// LOD1: walls with textured window quads + roof
    /// LOD2: walls with 3D window geometry + doors + roof
    /// </summary>
    /// <param name="maxLodLevel">Maximum LOD level to generate (0, 1, or 2). Default 2 = all levels.</param>
    public Dictionary<int, Dictionary<string, Mesh>> GenerateMultiLodMeshes(
        BuildingData building,
        Func<string, Vector2>? getTextureScale = null,
        int maxLodLevel = 2)
    {
        var result = new Dictionary<int, Dictionary<string, Mesh>>
        {
            [0] = GenerateMeshes(building, getTextureScale)
        };
        if (maxLodLevel >= 1)
            result[1] = GenerateLod1Meshes(building, getTextureScale);
        if (maxLodLevel >= 2)
            result[2] = GenerateLod2Meshes(building, getTextureScale);
        return result;
    }

    /// <summary>
    /// Generates meshes for up to 3 LOD levels of a multi-part building.
    /// Returns a dictionary: LOD index (0..maxLodLevel) → material key → Mesh.
    /// </summary>
    /// <param name="maxLodLevel">Maximum LOD level to generate (0, 1, or 2). Default 2 = all levels.</param>
    public Dictionary<int, Dictionary<string, Mesh>> GenerateMultiLodMeshes(
        Building building,
        Func<string, Vector2>? getTextureScale = null,
        int maxLodLevel = 2)
    {
        var result = new Dictionary<int, Dictionary<string, Mesh>> { [0] = new() };
        if (maxLodLevel >= 1) result[1] = new();
        if (maxLodLevel >= 2) result[2] = new();

        foreach (var part in building.Parts)
        {
            var partLodMeshes = GenerateMultiLodMeshes(part, getTextureScale, maxLodLevel);
            foreach (var (lod, meshes) in partLodMeshes)
            {
                foreach (var (matKey, mesh) in meshes)
                {
                    AddOrMergeMesh(result[lod], matKey, mesh);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// LOD1: walls with textured window quads + roof.
    /// Windows are placed as flat textured quads (TexturedWindow) cut into the wall surface.
    /// </summary>
    private Dictionary<string, Mesh> GenerateLod1Meshes(
        BuildingData building,
        Func<string, Vector2>? getTextureScale = null)
    {
        var meshes = new Dictionary<string, Mesh>();

        if (building.FootprintOuter.Count < 3)
            return meshes;

        EnsureRoofHeight(building);

        var polygon = RoofWithRidge.PreparePolygon(building.FootprintOuter);
        if (polygon == null || polygon.Count < 3)
            return meshes;

        var roof = CreateRoof(building, polygon);

        // Generate walls with textured windows (walls always generated, HasWalls only affects windows/doors)
        {
            var wallScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;

            var wallBuilder = new MeshBuilder()
                .WithName($"walls_{building.OsmId}")
                .WithMaterial(building.WallMaterial);

            // Per-material element builders (keyed by material key)
            var elementBuilders = new Dictionary<string, MeshBuilder>();
            MeshBuilder GetElementBuilder(string key)
            {
                if (!elementBuilders.TryGetValue(key, out var b))
                {
                    b = new MeshBuilder().WithName($"elem_{key}_{building.OsmId}").WithMaterial(key);
                    elementBuilders[key] = b;
                }
                return b;
            }

            float floorEle = building.MinHeight;
            float heightWithoutRoof = building.WallHeight;

            var walls = ExteriorBuildingWall.SplitIntoWalls(roof, polygon, floorEle, heightWithoutRoof);

            // Place passages before windows (passages take priority)
            if (building.Passages.Count > 0)
            {
                float wallHeight = heightWithoutRoof - floorEle;
                foreach (var wall in walls)
                    wall.PlacePassages(building.Passages, wallHeight);
            }

            foreach (var wall in walls)
            {
                wall.RenderLod1(wallBuilder, GetElementBuilder, wallScale, building);
            }

            // Hole walls (same as LOD0 — no windows on courtyards)
            if (building.FootprintHoles != null)
            {
                GenerateHoleWalls(wallBuilder, building, wallScale);
            }

            var wallMesh = wallBuilder.Build();
            if (wallMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.WallMaterial, wallMesh);

            foreach (var (matKey, elemBuilder) in elementBuilders)
            {
                var elemMesh = elemBuilder.Build();
                if (elemMesh.HasGeometry)
                    AddOrMergeMesh(meshes, matKey, elemMesh);
            }
        }

        // Roof is the same as LOD0
        {
            var roofScale = getTextureScale?.Invoke(building.RoofMaterial) ?? Vector2.One;
            var roofMesh = GenerateRoofMesh(building, roof, roofScale);
            if (roofMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.RoofMaterial, roofMesh);
        }

        // Floor (same as LOD0)
        if (building.MinHeight > 0)
        {
            var floorScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;
            var floorMesh = GenerateFloor(building, floorScale);
            if (floorMesh != null && floorMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.WallMaterial, floorMesh);
        }

        return meshes;
    }

    /// <summary>
    /// LOD2: walls with 3D geometry windows + doors + roof.
    /// Windows are full 3D with frames, glass panes, and inner dividers (GeometryWindow).
    /// Garage buildings get multiple doors on all qualifying walls (OSM2World port).
    /// Non-garage buildings get a single door on the longest wall.
    /// </summary>
    private Dictionary<string, Mesh> GenerateLod2Meshes(
        BuildingData building,
        Func<string, Vector2>? getTextureScale = null)
    {
        var meshes = new Dictionary<string, Mesh>();

        if (building.FootprintOuter.Count < 3)
            return meshes;

        EnsureRoofHeight(building);

        var polygon = RoofWithRidge.PreparePolygon(building.FootprintOuter);
        if (polygon == null || polygon.Count < 3)
            return meshes;

        var roof = CreateRoof(building, polygon);

        // Generate walls with 3D windows and doors (walls always generated, HasWalls only affects windows/doors)
        {
            var wallScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;

            var wallBuilder = new MeshBuilder()
                .WithName($"walls_{building.OsmId}")
                .WithMaterial(building.WallMaterial);
            var glassBuilder = new MeshBuilder()
                .WithName($"glass_{building.OsmId}")
                .WithMaterial("WINDOW_GLASS");

            // Per-material element builders (keyed by material key)
            var elementBuilders = new Dictionary<string, MeshBuilder>();
            MeshBuilder GetElementBuilder(string key)
            {
                if (!elementBuilders.TryGetValue(key, out var b))
                {
                    b = new MeshBuilder().WithName($"elem_{key}_{building.OsmId}").WithMaterial(key);
                    elementBuilders[key] = b;
                }
                return b;
            }

            float floorEle = building.MinHeight;
            float heightWithoutRoof = building.WallHeight;

            var walls = ExteriorBuildingWall.SplitIntoWalls(roof, polygon, floorEle, heightWithoutRoof);

            // Place passages before windows/doors (passages take priority)
            if (building.Passages.Count > 0)
            {
                float wallHeight = heightWithoutRoof - floorEle;
                foreach (var wall in walls)
                    wall.PlacePassages(building.Passages, wallHeight);
            }

            // Place doors before rendering (separate from RenderLod2, matching OSM2World flow)
            // Port of OSM2World ExteriorBuildingWall.java lines 257-278:
            // 1. If entrance=*/door=* nodes exist on outline, place doors at those positions
            // 2. For garages without entrance nodes, auto-place on walls > perimeter/8
            // 3. For non-garages without entrance nodes, fallback to longest wall
            bool isGarage = building.BuildingType is "garage" or "garages";
            bool hasEntranceNodes = building.EntrancePositions.Count > 0;

            if (hasEntranceNodes)
            {
                // Place doors at OSM entrance/door node positions
                int placedCount = 0;
                foreach (var entrancePos in building.EntrancePositions)
                {
                    foreach (var wall in walls)
                    {
                        if (wall.TryPlaceDoorAtEntrance(entrancePos, building))
                        {
                            placedCount++;
                            break; // Each entrance matches at most one wall
                        }
                    }
                }

                // Fallback: if entrance nodes exist but none matched any wall (coordinate mismatch),
                // place a default door on the longest wall so the building isn't doorless
                if (placedCount == 0)
                {
                    PlaceDoorOnLongestWall(walls, building);
                }
            }
            else if (isGarage)
            {
                // Port of OSM2World: garage doors on all walls longer than perimeter/8
                float perimeter = 0;
                for (int i = 0; i < polygon.Count; i++)
                {
                    int next = (i + 1) % polygon.Count;
                    perimeter += Vector2.Distance(polygon[i], polygon[next]);
                }
                float minWallLength = perimeter / 8f;

                foreach (var wall in walls)
                {
                    if (wall.Surface.Length > minWallLength)
                        wall.PlaceDefaultGarageDoors(building);
                }
            }
            else
            {
                // No entrance nodes mapped in OSM — fallback to longest wall
                PlaceDoorOnLongestWall(walls, building);
            }

            // Render all walls (doors already placed above)
            foreach (var wall in walls)
            {
                wall.RenderLod2(wallBuilder, GetElementBuilder, glassBuilder, wallScale, building);
            }

            // Hole walls
            if (building.FootprintHoles != null)
            {
                GenerateHoleWalls(wallBuilder, building, wallScale);
            }

            var wallMesh = wallBuilder.Build();
            if (wallMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.WallMaterial, wallMesh);

            foreach (var (matKey, elemBuilder) in elementBuilders)
            {
                var elemMesh = elemBuilder.Build();
                if (elemMesh.HasGeometry)
                    AddOrMergeMesh(meshes, matKey, elemMesh);
            }

            var glassMesh = glassBuilder.Build();
            if (glassMesh.HasGeometry)
                AddOrMergeMesh(meshes, "WINDOW_GLASS", glassMesh);
        }

        // Roof (same as LOD0)
        {
            var roofScale = getTextureScale?.Invoke(building.RoofMaterial) ?? Vector2.One;
            var roofMesh = GenerateRoofMesh(building, roof, roofScale);
            if (roofMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.RoofMaterial, roofMesh);
        }

        // Floor (same as LOD0)
        if (building.MinHeight > 0)
        {
            var floorScale = getTextureScale?.Invoke(building.WallMaterial) ?? Vector2.One;
            var floorMesh = GenerateFloor(building, floorScale);
            if (floorMesh != null && floorMesh.HasGeometry)
                AddOrMergeMesh(meshes, building.WallMaterial, floorMesh);
        }

        return meshes;
    }

    /// <summary>
    /// Creates a HeightfieldRoof instance for the building's roof shape.
    /// Places a default door on the longest wall (fallback when no entrance nodes are available).
    /// </summary>
    private static void PlaceDoorOnLongestWall(List<ExteriorBuildingWall> walls, BuildingData building)
    {
        int longestWallIdx = -1;
        float longestWallLength = 0;
        for (int i = 0; i < walls.Count; i++)
        {
            if (walls[i].Surface.Length > longestWallLength)
            {
                longestWallLength = walls[i].Surface.Length;
                longestWallIdx = i;
            }
        }

        if (longestWallIdx >= 0)
            walls[longestWallIdx].PlaceDefaultDoor(building);
    }

    /// <summary>
    /// Port of Roof.createRoofForShape() in Java's Roof.java.
    /// All roof shapes (including flat) go through HeightfieldRoof so that
    /// ExteriorBuildingWall can uniformly query GetRoofHeightAt().
    /// </summary>
    private static HeightfieldRoof CreateRoof(BuildingData building, List<Vector2> polygon)
    {
        return building.RoofShape switch
        {
            "gabled" or "pitched" => new GabledRoof(building, polygon),
            "hipped" or "side_hipped" => new HippedRoof(building, polygon),
            "half-hipped" => new HalfHippedRoof(building, polygon),
            "pyramidal" => new PyramidalRoof(building, polygon),
            "skillion" or "lean_to" => new SkillionRoof(building, polygon),
            "gambrel" => new GambrelRoof(building, polygon),
            "mansard" => new MansardRoof(building, polygon),
            "round" => new RoundRoof(building, polygon),
            "cone" => new ConeRoof(building, polygon),
            "dome" => new DomeRoof(building, polygon),
            // Unknown roof shapes: if height data says there's a roof (from facade_height,
            // roof:height, or roof:levels), fall back to gabled (most common non-flat shape)
            // instead of flat, which would create a floating slab at building.Height.
            // This handles roof:shape=3dr and other unrecognized OSM values.
            _ => building.RoofHeight > 0 ? new GabledRoof(building, polygon) : new FlatRoof(building, polygon)
        };
    }

    /// <summary>
    /// Generates walls using ExteriorBuildingWall with roof-aware top boundaries.
    /// Port of: walls = splitIntoWalls(area, this) + forEach wall.renderTo() in BuildingPart.java.
    ///
    /// Key difference from previous implementation:
    /// - OLD: all wall vertices at constant Z = building.RoofBaseHeight (flat tops)
    /// - NEW: each wall vertex extends up to meet the roof at that position
    ///        (Z = baseEle + heightWithoutRoof + roof.GetRoofHeightAt(vertex))
    ///
    /// For gabled roofs, this means the wall top boundary includes the ridge endpoint,
    /// creating the triangular gable wall automatically — no separate GenerateAdditionalGeometry() needed.
    /// </summary>
    private Mesh GenerateWalls(BuildingData building, HeightfieldRoof roof,
        List<Vector2> polygon, Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"walls_{building.OsmId}")
            .WithMaterial(building.WallMaterial);

        float floorEle = building.MinHeight;
        float heightWithoutRoof = building.WallHeight;

        // Outer walls: roof-aware via ExteriorBuildingWall
        // floorEle = MinHeight (wall bottom), heightWithoutRoof = Height - RoofHeight (eave from ground)
        var walls = ExteriorBuildingWall.SplitIntoWalls(roof, polygon, floorEle, heightWithoutRoof);

        // Place building passages BEFORE rendering (structural openings at all LOD levels)
        // Port of OSM2World BuildingPart.java lines 152-267
        if (building.Passages.Count > 0)
        {
            float wallHeight = heightWithoutRoof - floorEle;
            foreach (var wall in walls)
                wall.PlacePassages(building.Passages, wallHeight);
        }

        foreach (var wall in walls)
        {
            if (wall.Surface.Elements.Count > 0)
            {
                // Wall has passages — render with holes via RenderWithElements
                wall.Surface.RenderWithElements(builder, _ => builder, null, textureScale);
            }
            else
            {
                wall.Render(builder, building.WallMaterial, textureScale);
            }
        }

        // Hole walls: simple flat-topped quads (roof doesn't apply to inner rings)
        if (building.FootprintHoles != null)
        {
            GenerateHoleWalls(builder, building, textureScale);
        }

        return builder.Build();
    }

    /// <summary>
    /// Generates flat-topped walls for hole (courtyard) inner rings.
    /// Holes always have constant-height walls since roofs don't extend over courtyards.
    /// Preserved from original GenerateWalls() — not ported from OSM2World.
    /// </summary>
    private static void GenerateHoleWalls(MeshBuilder builder, BuildingData building, Vector2 textureScale)
    {
        if (building.FootprintHoles == null) return;

        float bottom = building.MinHeight;
        float top = building.RoofBaseHeight;
        float wallHeight = top - bottom;
        float scaleU = textureScale.X;
        float scaleV = textureScale.Y;

        foreach (var hole in building.FootprintHoles)
        {
            float cumulativeDistance = 0;
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

                float u0 = cumulativeDistance / scaleU;
                float u1 = (cumulativeDistance + segmentLength) / scaleU;

                var bl = builder.AddVertex(new Vector3(p0.X, p0.Y, bottom), wallNormal, new Vector2(u0, 0));
                var br = builder.AddVertex(new Vector3(p1.X, p1.Y, bottom), wallNormal, new Vector2(u1, 0));
                var tr = builder.AddVertex(new Vector3(p1.X, p1.Y, top), wallNormal, new Vector2(u1, wallHeight / scaleV));
                var tl = builder.AddVertex(new Vector3(p0.X, p0.Y, top), wallNormal, new Vector2(u0, wallHeight / scaleV));

                builder.AddQuad(bl, br, tr, tl);

                cumulativeDistance += segmentLength;
            }
        }
    }

    /// <summary>
    /// Generates the roof mesh using the shared HeightfieldRoof instance.
    /// For non-flat roofs: uses HeightfieldRoof.GenerateMesh() (face decomposition + triangulation).
    /// For flat roofs: uses direct polygon triangulation with holes support.
    /// </summary>
    private Mesh GenerateRoofMesh(BuildingData building, HeightfieldRoof roof, Vector2 textureScale)
    {
        // Non-flat roofs: use HeightfieldRoof.GenerateMesh() which handles face decomposition,
        // height assignment, and per-triangle normals.
        // DomeRoof, ConeRoof, RoundRoof override GenerateMesh() for custom rendering.
        if (roof is not FlatRoof)
        {
            return roof.GenerateMesh(textureScale);
        }

        // Flat roof: use direct polygon triangulation (supports holes/courtyards)
        return GenerateFlatRoof(building, textureScale);
    }

    /// <summary>
    /// Generates the flat roof surface using polygon triangulation.
    /// Placed at the top of the building (Height above ground).
    /// Supports buildings with holes/courtyards.
    /// </summary>
    private Mesh GenerateFlatRoof(BuildingData building, Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        float roofZ = building.RoofBaseHeight;
        Vector3 upNormal = Vector3.UnitZ;

        return GenerateHorizontalSurface(builder, building, roofZ, upNormal, false, textureScale);
    }

    /// <summary>
    /// Generates the floor/bottom polygon for elevated building parts.
    /// Placed at MinHeight, facing downward.
    /// </summary>
    private Mesh? GenerateFloor(BuildingData building, Vector2 textureScale)
    {
        if (building.MinHeight <= 0)
            return null;

        var builder = new MeshBuilder()
            .WithName($"floor_{building.OsmId}")
            .WithMaterial(building.WallMaterial);

        float floorZ = building.MinHeight;
        Vector3 downNormal = -Vector3.UnitZ;

        return GenerateHorizontalSurface(builder, building, floorZ, downNormal, true, textureScale);
    }

    /// <summary>
    /// Generates a horizontal surface (roof or floor) by triangulating the building footprint.
    /// UV coordinates are divided by textureScale for correct tiling.
    /// </summary>
    private Mesh GenerateHorizontalSurface(
        MeshBuilder builder,
        BuildingData building,
        float z,
        Vector3 normal,
        bool reverseWinding,
        Vector2 textureScale)
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

        // Add vertices to mesh with planar UV mapping scaled by texture dimensions
        int baseIndex = builder.VertexCount;
        foreach (var v2d in allVertices)
        {
            var position = new Vector3(v2d.X, v2d.Y, z);
            var uv = new Vector2(v2d.X / textureScale.X, v2d.Y / textureScale.Y);
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
    /// For non-flat roof shapes, ensures RoofHeight is set so walls don't extend to full height.
    /// If RoofHeight is 0, allocates a proportional amount from the total height.
    /// </summary>
    private static void EnsureRoofHeight(BuildingData building)
    {
        if (building.RoofShape is "flat" or "")
            return;

        if (building.RoofHeight > 0)
            return;

        // Allocate roof height: use roof:angle if available, else ~30% of total height
        // (clamped so walls are at least 1 level high)
        float defaultRoofHeight = building.Height * 0.3f;
        float minWallHeight = building.HeightPerLevel;
        float maxRoofHeight = MathF.Max(0, building.Height - minWallHeight);
        building.RoofHeight = MathF.Min(defaultRoofHeight, maxRoofHeight);
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
