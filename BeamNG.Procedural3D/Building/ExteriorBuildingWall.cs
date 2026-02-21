namespace BeamNG.Procedural3D.Building;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Building.Facade;
using BeamNG.Procedural3D.Building.Roof;

/// <summary>
/// An outer wall of a building part.
/// Port of OSM2World's ExteriorBuildingWall.java — wall rendering with LOD window/door support.
///
/// Key behavior ported from Java (ExteriorBuildingWall.renderTo() lines 130-191):
/// - Top boundary extracted from ROOF POLYGON (may have extra vertices like ridge endpoints)
/// - Each vertex has individual height from roof.GetRoofHeightAt()
/// - WallSurface handles different vertex counts between bottom and top boundaries
///
/// LOD support (port of ExteriorBuildingWall lines 312-462):
/// - LOD0: Render() — plain solid walls, no windows
/// - LOD1: RenderLod1() — walls with textured window quads
/// - LOD2: RenderLod2() — walls with 3D window geometry + doors
/// </summary>
public class ExteriorBuildingWall
{
    private readonly WallSurface _wallSurface;

    /// <summary>
    /// The 2D footprint start/end vertices of this wall segment (in building local coords).
    /// Used for matching passage positions to walls.
    /// </summary>
    private readonly Vector2 _footprintStart;
    private readonly Vector2 _footprintEnd;

    /// <summary>
    /// Creates an exterior building wall from precomputed bottom and top boundaries.
    /// </summary>
    private ExteriorBuildingWall(List<Vector3> bottomPoints, List<Vector3> topPoints)
    {
        _wallSurface = new WallSurface(bottomPoints, topPoints);
        _footprintStart = new Vector2(bottomPoints[0].X, bottomPoints[0].Y);
        _footprintEnd = new Vector2(bottomPoints[^1].X, bottomPoints[^1].Y);
    }

    /// <summary>
    /// The underlying wall surface (for external access if needed).
    /// </summary>
    public WallSurface Surface => _wallSurface;

    /// <summary>
    /// Renders this wall into a mesh builder (LOD0 — no windows/doors).
    /// Port of WallSurface.renderTo() call in ExteriorBuildingWall.renderTo().
    /// </summary>
    public void Render(MeshBuilder builder, string material, Vector2 textureScale)
    {
        _wallSurface.Render(builder, material, textureScale);
    }

    /// <summary>
    /// Renders this wall at LOD1: wall surface with textured window quads.
    /// Places TexturedWindow instances based on building level heights and wall length.
    /// No separate glass builder at LOD1 — TexturedWindow uses a single textured quad.
    /// </summary>
    /// <param name="wallBuilder">Builder for the wall mesh (with window holes).</param>
    /// <param name="getElementBuilder">Factory that returns a MeshBuilder for a given material key.</param>
    /// <param name="textureScale">Texture scale for wall material.</param>
    /// <param name="building">Building data for level count, heights, etc.</param>
    public void RenderLod1(MeshBuilder wallBuilder, Func<string, MeshBuilder> getElementBuilder,
        Vector2 textureScale, BuildingData building)
    {
        if (!building.HasWindows || _wallSurface.Length < 1.0f)
        {
            _wallSurface.Render(wallBuilder, building.WallMaterial, textureScale);
            return;
        }

        PlaceDefaultWindows(building, useGeometryWindows: false);
        _wallSurface.RenderWithElements(wallBuilder, getElementBuilder, null, textureScale);
    }

    /// <summary>
    /// Renders this wall at LOD2: wall surface with 3D geometry windows and any pre-placed doors.
    /// Windows are placed automatically; doors must be placed before calling this method
    /// (via PlaceDefaultDoor or PlaceDefaultGarageDoors).
    /// Port of OSM2World: frame geometry → WINDOW_FRAME, glass panes → WINDOW_GLASS.
    /// </summary>
    /// <param name="wallBuilder">Builder for the wall mesh (with holes).</param>
    /// <param name="getElementBuilder">Factory that returns a MeshBuilder for a given material key.</param>
    /// <param name="glassBuilder">Builder for glass pane geometry (WINDOW_GLASS material).</param>
    /// <param name="textureScale">Texture scale for wall material.</param>
    /// <param name="building">Building data for level count, heights, etc.</param>
    public void RenderLod2(MeshBuilder wallBuilder, Func<string, MeshBuilder> getElementBuilder, MeshBuilder? glassBuilder,
        Vector2 textureScale, BuildingData building)
    {
        if (building.HasWindows && _wallSurface.Length >= 1.0f)
        {
            PlaceDefaultWindows(building, useGeometryWindows: true);
        }

        if (_wallSurface.Elements.Count > 0)
        {
            _wallSurface.RenderWithElements(wallBuilder, getElementBuilder, glassBuilder, textureScale);
        }
        else
        {
            _wallSurface.Render(wallBuilder, building.WallMaterial, textureScale);
        }
    }

    // ==========================================
    // Window/door placement logic
    // ==========================================

    /// <summary>
    /// Places default windows on the wall based on building level heights.
    /// Port of Java ExteriorBuildingWall lines 312-380 (default window placement).
    ///
    /// Algorithm:
    /// 1. For each level above ground: calculate window positions
    /// 2. Columns: equally spaced, with spacing ≈ 2× window width
    /// 3. Each window is centered in its column × level cell
    /// </summary>
    private void PlaceDefaultWindows(BuildingData building, bool useGeometryWindows)
    {
        float wallLength = _wallSurface.Length;
        float levelHeight = building.HeightPerLevel;
        float wallHeight = building.WallHeight;
        int levels = MathF.Max(1, wallHeight / levelHeight) is var fl ? (int)fl : 1;

        // Default pane layout: 2x1 (2 columns, 1 row) for geometry windows, matching OSM2World default
        var defaultPanes = useGeometryWindows ? new PaneLayout(2, 1) : null;
        var windowParams = new WindowParameters(levelHeight, panes: defaultPanes);
        float windowWidth = windowParams.OverallProperties.Width;
        float windowHeight = windowParams.OverallProperties.Height;

        // Calculate number of columns: wall length / (2 × window width), at least 1
        int columns = Math.Max(1, (int)MathF.Round(wallLength / (2f * windowWidth)));
        float columnWidth = wallLength / columns;

        for (int level = 0; level < levels; level++)
        {
            // Y position: bottom of window at breast height above level floor
            float levelBase = level * levelHeight + building.MinHeight;
            float breast = windowParams.Breast;
            float windowY = levelBase + breast;

            // Skip if window top would exceed wall height
            if (windowY + windowHeight > wallHeight + building.MinHeight + 0.1f) continue;

            for (int col = 0; col < columns; col++)
            {
                // X position: center of column
                float windowX = (col + 0.5f) * columnWidth;

                // Ensure window fits (with margin from wall edges)
                float halfW = windowWidth / 2f;
                if (windowX - halfW < 0.05f || windowX + halfW > wallLength - 0.05f) continue;

                var position = new Vector2(windowX, windowY);

                IWallElement window = useGeometryWindows
                    ? new GeometryWindow(position, windowParams)
                    : new TexturedWindow(position, windowParams);

                _wallSurface.AddElementIfSpaceFree(window);
            }
        }
    }

    /// <summary>
    /// Places a default door at the center bottom of the wall.
    /// Port of Java ExteriorBuildingWall lines 392-425 (default door placement).
    /// </summary>
    public void PlaceDefaultDoor(BuildingData building)
    {
        var doorParams = DoorParameters.FromBuildingType(building.BuildingType);

        // Position at center bottom of wall
        float doorX = _wallSurface.Length / 2f;
        float doorY = building.MinHeight; // doors start at floor level

        // Check if door fits
        float halfW = doorParams.Width / 2f;
        if (doorX - halfW < 0.05f || doorX + halfW > _wallSurface.Length - 0.05f) return;
        if (doorParams.Height > building.WallHeight + 0.1f) return;

        var door = new Door(new Vector2(doorX, doorY), doorParams);
        _wallSurface.AddElementIfSpaceFree(door);
    }

    /// <summary>
    /// Places multiple garage doors evenly distributed across the wall.
    /// Port of OSM2World's ExteriorBuildingWall.placeDefaultGarageDoors() (lines 446-463).
    /// Door spacing: 1.25 × door width. Number of doors: round(wallLength / doorSpacing).
    /// </summary>
    public void PlaceDefaultGarageDoors(BuildingData building)
    {
        var doorParams = DoorParameters.FromBuildingType(building.BuildingType);
        float doorDistance = 1.25f * doorParams.Width; // 3.125m for standard 2.5m garage door
        int numDoors = Math.Max(1, (int)MathF.Round(_wallSurface.Length / doorDistance));

        for (int i = 0; i < numDoors; i++)
        {
            float doorX = _wallSurface.Length / numDoors * (i + 0.5f);
            float doorY = building.MinHeight;
            var door = new Door(new Vector2(doorX, doorY), doorParams);
            _wallSurface.AddElementIfSpaceFree(door);
        }
    }

    /// <summary>
    /// Attempts to place a door at an entrance position if it falls on this wall's footprint edge.
    /// Port of OSM2World ExteriorBuildingWall.java lines 257-278: for each node on the wall,
    /// if isDoorNode(node), place a door at points.offsetOf(node.getPos()).
    /// Returns true if a door was placed on this wall.
    /// </summary>
    /// <param name="entrancePos">Entrance position in building local coordinates.</param>
    /// <param name="building">Building data for door parameters.</param>
    /// <param name="tolerance">Distance tolerance for matching entrance position to wall edge (meters).</param>
    public bool TryPlaceDoorAtEntrance(Vector2 entrancePos, BuildingData building, float tolerance = 0.5f)
    {
        if (_wallSurface.Length < 0.5f) return false;

        var edgeDir = _footprintEnd - _footprintStart;
        float edgeLen = edgeDir.Length();
        if (edgeLen < 0.01f) return false;
        var edgeNorm = edgeDir / edgeLen;

        // Project entrance position onto this wall's footprint edge
        var toPoint = entrancePos - _footprintStart;
        float t = Vector2.Dot(toPoint, edgeNorm);

        // Check if projection falls within the wall segment
        if (t < -tolerance || t > edgeLen + tolerance) return false;

        // Check perpendicular distance to the edge
        float perpDist = MathF.Abs(toPoint.X * edgeNorm.Y - toPoint.Y * edgeNorm.X);
        if (perpDist > tolerance) return false;

        // Entrance position is on this wall — place a door
        float wallX = MathF.Max(0, MathF.Min(_wallSurface.Length, t));

        var doorParams = DoorParameters.FromBuildingType(building.BuildingType);

        // Check if door fits
        float halfW = doorParams.Width / 2f;
        if (wallX - halfW < 0.05f) wallX = halfW + 0.05f;
        if (wallX + halfW > _wallSurface.Length - 0.05f) wallX = _wallSurface.Length - halfW - 0.05f;
        if (wallX - halfW < 0 || wallX + halfW > _wallSurface.Length) return false;
        if (doorParams.Height > building.WallHeight + 0.1f) return false;

        float doorY = building.MinHeight;
        var door = new Door(new Vector2(wallX, doorY), doorParams);
        _wallSurface.AddElementIfSpaceFree(door);
        return true;
    }

    /// <summary>
    /// Places building passage openings on this wall for any passages whose position
    /// lies on this wall's footprint edge.
    ///
    /// Port of OSM2World BuildingPart.java lines 189-216:
    /// - Check if passage shared nodes are on this wall segment
    /// - Create full-height (or clearance-height) opening at the shared node position
    /// </summary>
    /// <param name="passages">All passage info for this building (entry/exit points).</param>
    /// <param name="wallHeight">The wall height (for clamping passage height).</param>
    /// <param name="tolerance">Distance tolerance for matching passage position to wall edge (meters).</param>
    public void PlacePassages(List<PassageInfo> passages, float wallHeight, float tolerance = 1.0f)
    {
        if (passages.Count == 0 || _wallSurface.Length < 0.5f) return;

        var edgeDir = _footprintEnd - _footprintStart;
        float edgeLen = edgeDir.Length();
        if (edgeLen < 0.01f) return;
        var edgeNorm = edgeDir / edgeLen;

        foreach (var passage in passages)
        {
            // Project passage position onto this wall's footprint edge
            var toPoint = passage.Position - _footprintStart;
            float t = Vector2.Dot(toPoint, edgeNorm);

            // Check if projection falls within the wall segment (with margin)
            if (t < -tolerance || t > edgeLen + tolerance) continue;

            // Check perpendicular distance to the edge
            float perpDist = MathF.Abs(toPoint.X * edgeNorm.Y - toPoint.Y * edgeNorm.X);
            if (perpDist > tolerance) continue;

            // Passage position is on this wall — compute wall surface X coordinate
            float wallX = MathF.Max(0, MathF.Min(_wallSurface.Length, t));

            // Clamp passage height to wall height
            float passageHeight = MathF.Min(passage.Height, wallHeight);
            if (passageHeight < 0.5f) continue;

            // Clamp passage width so it fits within the wall
            float halfW = passage.Width / 2f;
            float clampedX = MathF.Max(halfW + 0.01f, MathF.Min(_wallSurface.Length - halfW - 0.01f, wallX));

            var passageElement = new Passage(
                new Vector2(clampedX, 0), // bottom-center of opening
                passage.Width,
                passageHeight);

            _wallSurface.AddElementIfSpaceFree(passageElement);
        }
    }

    /// <summary>
    /// Splits a building footprint into exterior walls, each with roof-aware top boundaries.
    /// Simplified port of BuildingPart.splitIntoWalls() + ExteriorBuildingWall.renderTo() top boundary logic.
    ///
    /// Algorithm (port of Java ExteriorBuildingWall.renderTo() lines 95-191):
    /// 1. Walk the footprint polygon edges (each edge = one wall segment)
    /// 2. For each wall segment (footprint[i] → footprint[i+1]):
    ///    a. Bottom boundary: [footprint[i], footprint[i+1]] at constant Z = floorEle
    ///    b. Top boundary: extracted from roof polygon by walking from footprint[i] to footprint[i+1],
    ///       collecting any extra vertices (e.g., ridge endpoints for gabled roofs)
    ///    c. Each top vertex: Z = heightWithoutRoof + roof.GetRoofHeightAt(vertex)
    ///       (from ground level, NOT from floorEle — matches Java baseEle=groundEle)
    /// 3. Create WallSurface(bottomBoundary, topBoundary)
    ///
    /// IMPORTANT: Java uses TWO separate base elevations:
    ///   - floorEle  = groundEle + minHeight  → for wall BOTTOM
    ///   - baseEle   = groundEle (= 0)        → for wall TOP calculation
    /// The top boundary is always relative to ground, not to floorEle.
    /// </summary>
    /// <param name="roof">The roof instance (shared with roof mesh generation).</param>
    /// <param name="footprint">The building footprint polygon — MUST be the same polygon
    /// that was passed to the roof constructor (the prepared version from PreparePolygon).
    /// This ensures vertex coordinates match between footprint and roof polygon.</param>
    /// <param name="floorEle">Floor elevation (Z) for wall bottoms. Typically building.MinHeight.
    /// Port of Java: floorEle = baseEle + floorHeight (where baseEle=groundEle=0).</param>
    /// <param name="heightWithoutRoof">Eave height from ground (= Height - RoofHeight).
    /// The wall top is at heightWithoutRoof + roofHeightAt(v), measured from GROUND (Z=0),
    /// not from floorEle. Port of Java: baseEle + heightWithoutRoof where baseEle=groundEle=0.</param>
    public static List<ExteriorBuildingWall> SplitIntoWalls(
        HeightfieldRoof roof,
        List<Vector2> footprint,
        float floorEle,
        float heightWithoutRoof)
    {
        var result = new List<ExteriorBuildingWall>();
        var roofPolygon = roof.GetPolygon();

        if (footprint.Count < 3 || roofPolygon.Count < 3)
            return result;

        for (int i = 0; i < footprint.Count; i++)
        {
            int next = (i + 1) % footprint.Count;
            var wallStart = footprint[i];
            var wallEnd = footprint[next];

            // Skip degenerate edges
            if (Vector2.Distance(wallStart, wallEnd) < 0.01f) continue;

            // Bottom boundary: constant height (flat)
            // Port of Java: listXYZ(points.vertices(), floorEle)
            // where floorEle = baseEle + floorHeight = groundEle + minHeight
            var bottomPoints = new List<Vector3>
            {
                new(wallStart.X, wallStart.Y, floorEle),
                new(wallEnd.X, wallEnd.Y, floorEle)
            };

            // Top boundary: extract from roof polygon, may have extra vertices
            // Port of: ExteriorBuildingWall.renderTo() lines 136-187
            var topPointsXZ = ExtractTopBoundary(wallStart, wallEnd, roofPolygon);

            // Convert to 3D with per-vertex roof height
            // Port of Java: topPointsXZ.map(p -> p.xyz(baseEle + heightWithoutRoof + roof.getRoofHeightAt(p)))
            // where baseEle = groundEle = 0 (NOT floorEle!)
            var topPoints = new List<Vector3>(topPointsXZ.Count);
            foreach (var p in topPointsXZ)
            {
                float roofH = roof.GetRoofHeightAt(p);
                topPoints.Add(new Vector3(p.X, p.Y, heightWithoutRoof + roofH));
            }

            if (topPoints.Count < 2) continue;

            try
            {
                result.Add(new ExteriorBuildingWall(bottomPoints, topPoints));
            }
            catch
            {
                // Skip wall if WallSurface construction fails (degenerate geometry)
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the top boundary vertices from the roof polygon between two wall endpoints.
    /// Port of ExteriorBuildingWall.renderTo() lines 136-187.
    ///
    /// The roof polygon may have extra vertices compared to the building footprint
    /// (e.g., ridge endpoints inserted by GabledRoof.GetPolygon()). This method walks
    /// the roof polygon from wallStart to wallEnd, collecting ALL intermediate vertices.
    ///
    /// Example for gabled roof rectangle [A, R1, B, C, R2, D] (R1, R2 = ridge endpoints):
    ///   Wall A→B: extracts top boundary [A, R1, B] → creates the triangular gable wall!
    ///   Wall B→C: extracts top boundary [B, C] → simple rectangular wall
    ///   Wall C→D: extracts top boundary [C, R2, D] → creates the other gable wall!
    ///   Wall D→A: extracts top boundary [D, A] → simple rectangular wall
    /// </summary>
    private static List<Vector2> ExtractTopBoundary(
        Vector2 wallStart, Vector2 wallEnd, List<Vector2> roofPolygon)
    {
        // Find wall endpoints in roof polygon (by proximity)
        // Port of: polygon.verticesNoDup().indexOf(firstBottomPoint/lastBottomPoint)
        int startIdx = FindVertexIndex(roofPolygon, wallStart);
        int endIdx = FindVertexIndex(roofPolygon, wallEnd);

        if (startIdx == -1 || endIdx == -1)
        {
            // Fallback: if endpoints not found in roof polygon, just use the wall endpoints directly.
            // This should only happen with unusual geometry (Java warns: "cannot construct top boundary of wall").
            // Java also tries inserting the point into the polygon (allowInsertion pass), but we skip that
            // since building passages are not supported.
            return new List<Vector2> { wallStart, wallEnd };
        }

        // Walk roof polygon forward from startIdx to endIdx, collecting all vertices.
        // Port of Java lines 167-173:
        //   if (lastIndex < firstIndex) lastIndex += polygon.size();
        //   for (int i = firstIndex; i <= lastIndex; i++)
        //       topPointsXZ.add(polygon.getVertex(i % polygon.size()));
        int n = roofPolygon.Count;

        if (endIdx <= startIdx)
        {
            endIdx += n;
        }

        var topPoints = new List<Vector2>();
        for (int i = startIdx; i <= endIdx; i++)
        {
            topPoints.Add(roofPolygon[i % n]);
        }

        return topPoints;
    }

    /// <summary>
    /// Finds the index of a vertex in the roof polygon by proximity.
    /// Port of: polygon.verticesNoDup().indexOf(point)
    ///
    /// Uses tolerance matching since float coordinates may differ slightly
    /// between the original footprint and the roof polygon (e.g., after
    /// InsertIntoPolygon operations in GabledRoof.GetPolygon()).
    /// </summary>
    private static int FindVertexIndex(List<Vector2> polygon, Vector2 target, float tolerance = 0.05f)
    {
        int bestIdx = -1;
        float bestDist = tolerance;

        for (int i = 0; i < polygon.Count; i++)
        {
            float dist = Vector2.Distance(polygon[i], target);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }
}
