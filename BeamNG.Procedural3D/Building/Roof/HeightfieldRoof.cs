namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Abstract base class for roofs that have exactly one height value
/// for each point within their XZ polygon.
/// 1:1 port of OSM2World's HeightfieldRoof.java.
///
/// Subclasses define the roof shape by implementing:
///   - GetPolygon()                     → roof outline (may have extra vertices)
///   - GetInnerPoints()                 → apex points (empty for ridge roofs)
///   - GetInnerSegments()               → ridge/hip lines dividing the roof
///   - GetRoofHeightAtNoInterpolation() → height at known positions
///
/// This base class handles:
///   - Face decomposition (via FaceDecompositionUtil)
///   - Height interpolation fallback for unknown positions
///   - Triangulation + 3D mesh generation
/// </summary>
public abstract class HeightfieldRoof
{
    /// <summary>
    /// Snap distance for InsertIntoPolygon. Matches Java's HeightfieldRoof.SNAP_DISTANCE = 0.01.
    /// </summary>
    protected const float SNAP_DISTANCE = 0.01f;

    protected readonly List<Vector2> originalPolygon;
    protected readonly BuildingData building;
    protected float roofHeight;

    protected HeightfieldRoof(BuildingData building, List<Vector2> polygon)
    {
        this.building = building;
        this.originalPolygon = polygon;
    }

    /// <summary>
    /// Returns the outline of the roof. May have additional vertices inserted
    /// compared to the original polygon (e.g., ridge endpoints for gabled roofs).
    /// Port of HeightfieldRoof.getPolygon() (abstract in Java, defined by subclasses).
    /// </summary>
    public abstract List<Vector2> GetPolygon();

    /// <summary>
    /// Returns inner apex points of the roof (e.g., tip of a pyramidal roof).
    /// Port of HeightfieldRoof.getInnerPoints() (abstract in Java).
    /// </summary>
    public abstract List<Vector2> GetInnerPoints();

    /// <summary>
    /// Returns line segments within the roof polygon that define ridges or edges.
    /// Port of Roof.getInnerSegments() (abstract in Java).
    /// </summary>
    public abstract List<(Vector2 P1, Vector2 P2)> GetInnerSegments();

    /// <summary>
    /// Returns the roof height at a position, without interpolation.
    /// Only required to work for positions that are part of the polygon, segments, or inner points.
    /// Port of HeightfieldRoof.getRoofHeightAt_noInterpolation() (abstract in Java).
    /// Returns null if height is unknown at this position.
    /// </summary>
    protected abstract float? GetRoofHeightAtNoInterpolation(Vector2 pos);

    /// <summary>
    /// Returns roof height at any position, with fallback interpolation from nearest segment.
    /// 1:1 port of HeightfieldRoof.getRoofHeightAt().
    ///
    /// Algorithm:
    /// 1. Try GetRoofHeightAtNoInterpolation() first
    /// 2. If null: find the closest segment (inner + polygon edges), interpolate between its endpoints
    /// </summary>
    public float GetRoofHeightAt(Vector2 v)
    {
        var ele = GetRoofHeightAtNoInterpolation(v);
        if (ele.HasValue)
            return ele.Value;

        // Port of HeightfieldRoof.getRoofHeightAt() else branch:
        // Collect all segments, find the one closest to v, interpolate.

        var segments = new List<(Vector2 P1, Vector2 P2)>();
        segments.AddRange(GetInnerSegments());

        var poly = GetPolygon();
        for (int i = 0; i < poly.Count; i++)
        {
            int next = (i + 1) % poly.Count;
            segments.Add((poly[i], poly[next]));
        }
        // Java also adds hole segments here, but we don't support roof holes.

        (Vector2 P1, Vector2 P2) closestSeg = segments[0];
        float closestDist = float.MaxValue;
        foreach (var seg in segments)
        {
            float d = FaceDecompositionUtil.DistancePointToSegment(v, seg.P1, seg.P2);
            if (d < closestDist)
            {
                closestDist = d;
                closestSeg = seg;
            }
        }

        // Port of: interpolateValue(v, closestSegment.p1, getRoofHeightAt(p1), p2, getRoofHeightAt(p2))
        float h1 = GetRoofHeightAtNoInterpolation(closestSeg.P1) ?? 0;
        float h2 = GetRoofHeightAtNoInterpolation(closestSeg.P2) ?? 0;
        return FaceDecompositionUtil.InterpolateValue(v, closestSeg.P1, h1, closestSeg.P2, h2);
    }

    /// <summary>
    /// Generates the roof mesh by:
    /// 1. Splitting the polygon into faces using FaceDecompositionUtil
    /// 2. Triangulating each face
    /// 3. Assigning heights from the roof height function
    /// 4. Computing per-triangle normals
    ///
    /// Port of HeightfieldRoof.renderTo() + getRoofTriangles().
    /// Virtual so subclasses (e.g., DomeRoof) can use a different rendering approach.
    /// </summary>
    public virtual Mesh GenerateMesh(Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        var poly = GetPolygon();
        var innerSegments = GetInnerSegments();
        var innerPoints = GetInnerPoints();

        // Port of getRoofTriangles():
        // If no inner points → use FaceDecompositionUtil.splitPolygonIntoFaces()
        // If inner points → would use constrained Delaunay (JTSTriangulationUtil in Java)
        List<List<Vector2>> faces;
        if (innerPoints.Count == 0)
        {
            faces = FaceDecompositionUtil.SplitPolygonIntoFaces(poly, innerSegments);
        }
        else
        {
            // Fallback: inner points not yet supported (pyramidal roofs would need this)
            faces = FaceDecompositionUtil.SplitPolygonIntoFaces(poly, innerSegments);
        }

        LogFaceCount(faces.Count, innerSegments.Count, poly.Count);

        float baseEle = building.RoofBaseHeight;

        // knownAngles accumulates across all faces for angle snapping
        // (port of SlopedTrianglesTexCoordFunction's shared angle list)
        var knownAngles = new List<float>();

        // Port of: for each triangle in getRoofTriangles(), makeCounterclockwise, assign heights
        foreach (var face in faces)
        {
            if (face.Count < 3) continue;
            RenderFace(builder, face, baseEle, textureScale, knownAngles);
        }

        // Hook for subclasses to add extra geometry (e.g., gable walls)
        GenerateAdditionalGeometry(builder, textureScale);

        return builder.Build();
    }

    /// <summary>
    /// Override to add additional geometry beyond the roof surface.
    /// Used by GabledRoof to add triangular gable walls.
    /// </summary>
    protected virtual void GenerateAdditionalGeometry(MeshBuilder builder, Vector2 textureScale) { }

    /// <summary>
    /// Override to add diagnostic logging for face count validation.
    /// </summary>
    protected virtual void LogFaceCount(int faceCount, int innerSegmentCount, int polyVertexCount) { }

    /// <summary>
    /// Renders a single roof face: triangulate, assign heights, compute normals, add to mesh.
    /// Port of the triangle generation loop in HeightfieldRoof.getRoofTriangles().
    /// UV coordinates use slope-aware projection (port of SlopedTrianglesTexCoordFunction).
    /// Protected so ConeRoof can reuse it.
    /// </summary>
    protected void RenderFace(MeshBuilder builder, List<Vector2> face, float baseEle,
        Vector2 textureScale, List<float> knownAngles)
    {
        var indices = PolygonTriangulator.Triangulate(face);
        if (indices.Count < 3) return;

        // Pre-compute 3D positions using roof height function
        // Port of: tCCW.xyz(v -> v.xyz(baseEle + getRoofHeightAt(v)))
        var positions = new Vector3[face.Count];
        for (int i = 0; i < face.Count; i++)
        {
            float h = GetRoofHeightAt(face[i]);
            positions[i] = new Vector3(face[i].X, face[i].Y, baseEle + h);
        }

        for (int i = 0; i < indices.Count; i += 3)
        {
            int idx0 = indices[i], idx1 = indices[i + 1], idx2 = indices[i + 2];
            var p0 = positions[idx0];
            var p1 = positions[idx1];
            var p2 = positions[idx2];

            // Ensure CCW winding (normal pointing up)
            // Port of: TriangleXZ.makeCounterclockwise()
            var normal = Vector3.Cross(p1 - p0, p2 - p0);
            if (normal.LengthSquared() < 1e-10f) continue;
            normal = Vector3.Normalize(normal);

            // Flip if normal points downward
            if (normal.Z < 0)
            {
                normal = -normal;
                (p1, p2) = (p2, p1);
                (idx1, idx2) = (idx2, idx1);
            }

            // UV coordinates: slope-aware projection
            // Port of SlopedTrianglesTexCoordFunction from OSM2World.
            // 1. Project normal to horizontal plane → downslope direction
            // 2. Snap angle to nearby known angles (avoids seams between adjacent triangles)
            // 3. Rotate vertex positions by -angle, use rotated coords as UV
            float downAngle = 0;
            float nx = normal.X, ny = normal.Y;
            if (nx * nx + ny * ny > 1e-8f)
            {
                downAngle = MathF.Atan2(ny, nx);
                downAngle = SnapAngle(downAngle, knownAngles);
            }

            var uv0 = ComputeSlopedTexCoord(face[idx0], downAngle, textureScale);
            var uv1 = ComputeSlopedTexCoord(face[idx1], downAngle, textureScale);
            var uv2 = ComputeSlopedTexCoord(face[idx2], downAngle, textureScale);

            var vi0 = builder.AddVertex(p0, normal, uv0);
            var vi1 = builder.AddVertex(p1, normal, uv1);
            var vi2 = builder.AddVertex(p2, normal, uv2);

            builder.AddTriangle(vi0, vi1, vi2);
        }
    }

    /// <summary>
    /// Computes slope-aware texture coordinates for a vertex.
    /// Port of SlopedTrianglesTexCoordFunction: rotates the vertex position
    /// by the downslope angle so texture aligns with the slope direction.
    ///
    /// Coordinate system mapping: Java's v.rotateY(-angle) with Y-up right-handed 3D
    /// is equivalent to a standard 2D rotation by +angle in our X/Y horizontal plane
    /// (because rotateY in RH coords has opposite handedness to 2D rotation in XZ).
    /// </summary>
    private static Vector2 ComputeSlopedTexCoord(Vector2 pos, float downAngle, Vector2 textureScale)
    {
        float cos = MathF.Cos(downAngle);
        float sin = MathF.Sin(downAngle);
        float rotX = pos.X * cos - pos.Y * sin;
        float rotY = pos.X * sin + pos.Y * cos;
        return new Vector2(-rotX / textureScale.X, -rotY / textureScale.Y);
    }

    /// <summary>
    /// Snaps an angle to a nearby known angle (within 0.02 radians ≈ 1.1°).
    /// Port of SlopedTrianglesTexCoordFunction's angle snapping logic.
    /// Prevents visible texture seams between adjacent triangles with slightly different normals.
    /// </summary>
    private static float SnapAngle(float angle, List<float> knownAngles)
    {
        foreach (var known in knownAngles)
        {
            if (MathF.Abs(angle - known) < 0.02f)
                return known;
        }
        knownAngles.Add(angle);
        return angle;
    }
}
