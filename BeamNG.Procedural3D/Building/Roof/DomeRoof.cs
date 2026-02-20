namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Dome roof: hemisphere shape created by stacking scaled copies of the polygon.
/// Port of OSM2World's DomeRoof.java (extends SpindleRoof).
///
/// Rendering approach (port of SpindleRoof.renderSpindle()):
///   - 10 height rings approximate the dome curve
///   - Each ring: relativeHeight = ring / 9, scaleFactor = sqrt(1 - relativeHeight²)
///   - Adjacent rings are connected with quad strips
///   - The top ring converges to the center point (triangles)
///
/// Wall interaction (port of SpindleRoof.getRoofHeightAt() → returns 0):
///   - GetRoofHeightAt() returns 0 at all positions
///   - Walls are flat-topped at building.RoofBaseHeight
///   - The dome sits on top of the walls
///
/// Extends HeightfieldRoof for compatibility with ExteriorBuildingWall.SplitIntoWalls().
/// </summary>
public class DomeRoof : HeightfieldRoof
{
    /// <summary>
    /// Number of height rings to approximate the round dome shape.
    /// Matches Java DomeRoof.HEIGHT_RINGS = 10.
    /// </summary>
    private const int HEIGHT_RINGS = 10;

    public DomeRoof(BuildingData building, List<Vector2> polygon)
        : base(building, polygon)
    {
        roofHeight = building.RoofHeight;
    }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of SpindleRoof.getPolygon(): return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon() => originalPolygon;

    /// <summary>
    /// No inner points for dome roofs.
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// No inner segments for dome roofs.
    /// Port of SpindleRoof.getInnerSegments(): return emptyList();
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments() => new();

    /// <summary>
    /// Roof height at a position: always 0.
    /// Port of SpindleRoof.getRoofHeightAt(): return 0;
    /// This means walls don't extend into the dome — they stay flat-topped,
    /// and the dome mesh sits on top.
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos) => 0f;

    /// <summary>
    /// Generates the dome mesh using the spindle extrusion approach.
    /// Port of SpindleRoof.renderTo() + DomeRoof.getSpindleSteps() + ExtrusionGeometry UV mapping.
    ///
    /// UV mapping (port of ExtrusionGeometry.java lines 246-286):
    ///   - lengthAlong: accumulated 3D distance from base ring upward (per vertex)
    ///   - lengthAcross: evenly distributed around polygon perimeter (by vertex index)
    ///   - UV = (lengthAlong, lengthAcross) / textureScale
    ///   - Apex triangles use averaged UV of adjacent vertices (lines 310-311)
    /// </summary>
    public override Mesh GenerateMesh(Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        if (originalPolygon.Count < 3 || roofHeight <= 0)
            return builder.Build();

        var center = ComputeCentroid(originalPolygon);
        float baseEle = building.RoofBaseHeight;
        int polyCount = originalPolygon.Count;
        int vertsPerRing = polyCount + 1; // include closing vertex for correct UV wrapping

        // Pre-compute spindle steps (port of DomeRoof.getSpindleSteps())
        var steps = new (float RelativeHeight, float ScaleFactor)[HEIGHT_RINGS];
        for (int ring = 0; ring < HEIGHT_RINGS; ring++)
        {
            float relH = (float)ring / (HEIGHT_RINGS - 1);
            steps[ring] = (relH, MathF.Sqrt(1f - relH * relH));
        }

        // Compute 3D positions for all rings (including closing vertex per ring)
        var ringPositions = new Vector3[HEIGHT_RINGS][];
        for (int ring = 0; ring < HEIGHT_RINGS; ring++)
        {
            float z = baseEle + steps[ring].RelativeHeight * roofHeight;
            float scale = steps[ring].ScaleFactor;
            ringPositions[ring] = new Vector3[vertsPerRing];
            for (int j = 0; j < vertsPerRing; j++)
            {
                var scaled = ScaleFromCenter(originalPolygon[j % polyCount], center, scale);
                ringPositions[ring][j] = new Vector3(scaled.X, scaled.Y, z);
            }
        }

        // Compute totalLengthAcross (perimeter of base polygon)
        float totalLengthAcross = 0;
        for (int i = 0; i < polyCount; i++)
            totalLengthAcross += Vector2.Distance(originalPolygon[i], originalPolygon[(i + 1) % polyCount]);

        // Compute lengthAlong per vertex per ring (accumulated 3D distance from base)
        // Port of ExtrusionGeometry.java lines 250-257, 270-273
        var lengthAlong = new float[HEIGHT_RINGS][];
        lengthAlong[0] = new float[vertsPerRing];
        for (int ring = 1; ring < HEIGHT_RINGS; ring++)
        {
            lengthAlong[ring] = new float[vertsPerRing];
            for (int j = 0; j < vertsPerRing; j++)
                lengthAlong[ring][j] = lengthAlong[ring - 1][j]
                    + Vector3.Distance(ringPositions[ring - 1][j], ringPositions[ring][j]);
        }

        // Compute lengthAcross per vertex (evenly distributed by index)
        // Port of ExtrusionGeometry.java line 275
        var lengthAcross = new float[vertsPerRing];
        for (int j = 0; j < vertsPerRing; j++)
            lengthAcross[j] = (totalLengthAcross / vertsPerRing) * j;

        // Add vertices for non-apex rings (0..HEIGHT_RINGS-2)
        var ringStartIndices = new int[HEIGHT_RINGS - 1];
        for (int ring = 0; ring < HEIGHT_RINGS - 1; ring++)
        {
            ringStartIndices[ring] = builder.VertexCount;
            for (int j = 0; j < vertsPerRing; j++)
            {
                // UV: (lengthAlong, lengthAcross) matching ExtrusionGeometry default
                var uv = new Vector2(
                    lengthAlong[ring][j] / textureScale.X,
                    lengthAcross[j] / textureScale.Y);
                builder.AddVertex(ringPositions[ring][j], Vector3.UnitZ, uv);
            }
        }

        // Build quads between non-apex adjacent rings
        for (int ring = 0; ring < HEIGHT_RINGS - 2; ring++)
        {
            for (int j = 0; j < vertsPerRing - 1; j++)
            {
                int lowerJ = ringStartIndices[ring] + j;
                int lowerNext = lowerJ + 1;
                int upperJ = ringStartIndices[ring + 1] + j;
                int upperNext = upperJ + 1;
                builder.AddQuad(lowerJ, lowerNext, upperNext, upperJ);
            }
        }

        // Build triangles to apex with per-triangle apex vertices (averaged UV)
        // Port of ExtrusionGeometry.java lines 305-312
        int lastRing = HEIGHT_RINGS - 2;
        int apexRing = HEIGHT_RINGS - 1;
        var apexPos = new Vector3(center.X, center.Y,
            baseEle + steps[apexRing].RelativeHeight * roofHeight);

        for (int j = 0; j < vertsPerRing - 1; j++)
        {
            int lowerJ = ringStartIndices[lastRing] + j;
            int lowerNext = lowerJ + 1;

            // Averaged UV for apex vertex (port of texCoordsB.get(i).add(texCoordsB.get(i+1)).mult(0.5))
            float apexAlong = (lengthAlong[apexRing][j] + lengthAlong[apexRing][j + 1]) / 2f;
            float apexAcross = (lengthAcross[j] + lengthAcross[j + 1]) / 2f;
            var apexUv = new Vector2(apexAlong / textureScale.X, apexAcross / textureScale.Y);
            int apexIdx = builder.AddVertex(apexPos, Vector3.UnitZ, apexUv);

            builder.AddTriangle(lowerJ, lowerNext, apexIdx);
        }

        // Compute smooth normals for dome appearance (port of ExtrudeOption.SMOOTH_SIDES)
        builder.CalculateSmoothNormals();

        return builder.Build();
    }

    /// <summary>
    /// Scales a point from a center by a given factor.
    /// </summary>
    private static Vector2 ScaleFromCenter(Vector2 point, Vector2 center, float scale)
    {
        return center + (point - center) * scale;
    }

    /// <summary>
    /// Computes the centroid (average position) of a polygon.
    /// </summary>
    private static Vector2 ComputeCentroid(List<Vector2> polygon)
    {
        if (polygon.Count == 0) return Vector2.Zero;
        var sum = Vector2.Zero;
        foreach (var p in polygon) sum += p;
        return sum / polygon.Count;
    }
}
