namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Skillion (lean-to) roof: a single sloped plane.
/// 1:1 port of OSM2World's SkillionRoof.java.
///
/// Uses roof:direction tag to determine which side is highest.
/// The ridge is the upper edge of the polygon; height decreases linearly
/// perpendicular to the ridge direction.
/// </summary>
public class SkillionRoof : HeightfieldRoof
{
    private readonly (Vector2 P1, Vector2 P2)? ridge;
    private readonly float roofLength;

    public SkillionRoof(BuildingData building, List<Vector2> polygon)
        : base(building, polygon)
    {
        // Port of: SimplePolygonXZ simplifiedOuter = originalPolygon.getOuter().getSimplifiedPolygon();
        var simplifiedPolygon = RoofWithRidge.SimplifyPolygon(polygon);

        // Port of: Angle angle = snapDirection(tags.getValue("roof:direction"), simplifiedOuter.getSegments());
        if (building.RoofDirection.HasValue)
        {
            float bearingRad = building.RoofDirection.Value * MathF.PI / 180f;
            // Port of: VectorXZ.fromAngle(angle) = (sin(angle), cos(angle))
            var slopeDirection = new Vector2(MathF.Sin(bearingRad), MathF.Cos(bearingRad));

            // Snap to polygon edge (port of snapDirection)
            var snapped = RoofWithRidge.SnapToPolygonEdge(slopeDirection, simplifiedPolygon);
            if (snapped.HasValue)
                slopeDirection = snapped.Value;
            else
                slopeDirection = Vector2.Normalize(slopeDirection);

            // Port of: find the "top" (upper) segment by calculating the outermost intersections
            // of the quasi-infinite slope "line" towards the centroid vector with segments of the polygon
            var center = RoofWithRidge.CalculateCentroid(simplifiedPolygon);

            // Port of: intersectedSegments = simplifiedOuter.intersectionSegments(
            //     new LineSegmentXZ(center.add(slopeDirection.mult(-1000)), center));
            var lineStart = center - slopeDirection * 1000f;
            var lineEnd = center;

            // Find all polygon segments that intersect this line
            var intersectedSegments = new List<(Vector2 P1, Vector2 P2)>();
            for (int i = 0; i < simplifiedPolygon.Count; i++)
            {
                int next = (i + 1) % simplifiedPolygon.Count;
                var inter = RoofWithRidge.SegmentIntersection(lineStart, lineEnd,
                    simplifiedPolygon[i], simplifiedPolygon[next]);
                if (inter.HasValue)
                {
                    intersectedSegments.Add((simplifiedPolygon[i], simplifiedPolygon[next]));
                }
            }

            if (intersectedSegments.Count > 0)
            {
                // Port of: upperSegment = max(intersectedSegments, comparingDouble(i -> distanceFromLineSegment(center, i)));
                (Vector2 P1, Vector2 P2) upperSegment = intersectedSegments[0];
                float maxDist = FaceDecompositionUtil.DistancePointToSegment(center, upperSegment.P1, upperSegment.P2);
                for (int i = 1; i < intersectedSegments.Count; i++)
                {
                    float d = FaceDecompositionUtil.DistancePointToSegment(center, intersectedSegments[i].P1, intersectedSegments[i].P2);
                    if (d > maxDist)
                    {
                        maxDist = d;
                        upperSegment = intersectedSegments[i];
                    }
                }

                // Port of: if (angleBetween(upperSegment.getDirection(), slopeDirection) < PI / 180)
                var segDir = upperSegment.P2 - upperSegment.P1;
                float segLen = segDir.Length();
                if (segLen > 1e-6f)
                {
                    segDir /= segLen;
                    float angleBetween = AngleBetween(segDir, slopeDirection);

                    if (angleBetween < MathF.PI / 180f)
                    {
                        // Port of: ridge = upperSegment;
                        ridge = upperSegment;
                    }
                    else
                    {
                        // Port of: VectorXZ offset = slopeDirection.rightNormal().mult(simplifiedOuter.getDiameter());
                        float diameter = PolygonDiameter(simplifiedPolygon);
                        var rightNormal = new Vector2(slopeDirection.Y, -slopeDirection.X);
                        var offset = rightNormal * diameter;

                        // Port of: VectorXZ topPoint = max(upperSegment.vertices(),
                        //     comparingDouble(p -> distanceFromLine(p, centerLine.p1, centerLine.p2)));
                        var centerLineP1 = center - offset;
                        var centerLineP2 = center + offset;

                        float distP1 = FaceDecompositionUtil.DistanceFromLine(upperSegment.P1, centerLineP1, centerLineP2);
                        float distP2 = FaceDecompositionUtil.DistanceFromLine(upperSegment.P2, centerLineP1, centerLineP2);
                        var topPoint = distP1 >= distP2 ? upperSegment.P1 : upperSegment.P2;

                        // Port of: ridge = new LineSegmentXZ(topPoint.subtract(offset), topPoint.add(offset));
                        ridge = (topPoint - offset, topPoint + offset);
                    }
                }
                else
                {
                    ridge = null;
                }

                // Port of: roofLength = originalPolygon.getOuter().vertices().stream()
                //     .mapToDouble(v -> distanceFromLine(v, ridge.p1, ridge.p2)).max().getAsDouble();
                if (ridge.HasValue)
                {
                    float maxLen = 0;
                    foreach (var v in polygon)
                    {
                        float d = FaceDecompositionUtil.DistanceFromLine(v, ridge.Value.P1, ridge.Value.P2);
                        maxLen = MathF.Max(maxLen, d);
                    }
                    roofLength = MathF.Max(maxLen, 0.01f);
                }
                else
                {
                    roofLength = 1f;
                }
            }
            else
            {
                ridge = null;
                roofLength = float.NaN;
            }
        }
        else
        {
            // Port of: ridge = null; roofLength = Double.NaN;
            ridge = null;
            roofLength = float.NaN;
        }

        // Calculate roof height
        roofHeight = CalculateRoofHeight();
    }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of SkillionRoof.getPolygon(): return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon() => originalPolygon;

    /// <summary>
    /// No inner segments for skillion roofs.
    /// Port of SkillionRoof.getInnerSegments(): return emptyList();
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments() => new();

    /// <summary>
    /// No inner points for skillion roofs.
    /// Port of SkillionRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Height at a position: linear falloff from ridge using infinite line distance.
    /// Port of SkillionRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of: if (ridge == null) return roofHeight;
        if (!ridge.HasValue)
            return roofHeight;

        // Port of: double distance = distanceFromLine(pos, ridge.p1, ridge.p2);
        //          double relativeDistance = distance / roofLength;
        //          return roofHeight - relativeDistance * roofHeight;
        float distance = FaceDecompositionUtil.DistanceFromLine(pos, ridge.Value.P1, ridge.Value.P2);
        float relativeDistance = distance / roofLength;
        return roofHeight - relativeDistance * roofHeight;
    }

    /// <summary>
    /// Calculates roof height from tags.
    /// Port of SkillionRoof.calculatePreliminaryHeight().
    /// </summary>
    private float CalculateRoofHeight()
    {
        // Port of: Double roofHeight = parseMeasure(tags.getValue("roof:height"));
        if (building.RoofHeight > 0)
            return building.RoofHeight;

        // Port of: Double angle = parseAngle(tags.getValue("roof:angle"));
        //          if (angle != null && angle >= 0 && angle < 90.0)
        //              roofHeight = tan(toRadians(angle)) * roofLength;
        if (building.RoofAngle.HasValue && building.RoofAngle.Value > 0 && building.RoofAngle.Value < 90
            && !float.IsNaN(roofLength))
        {
            float angleRad = building.RoofAngle.Value * MathF.PI / 180f;
            return MathF.Tan(angleRad) * roofLength;
        }

        // Default
        return MathF.Max(1.5f, (float.IsNaN(roofLength) ? 3f : roofLength) * 0.3f);
    }

    /// <summary>
    /// Angle between two direction vectors (unsigned, [0, PI]).
    /// Port of VectorXZ.angleBetween().
    /// </summary>
    private static float AngleBetween(Vector2 a, Vector2 b)
    {
        float dot = Vector2.Dot(a, b);
        dot = MathF.Max(-1f, MathF.Min(1f, dot));
        return MathF.Acos(dot);
    }

    /// <summary>
    /// Maximum distance between any two polygon vertices.
    /// Port of SimplePolygonXZ.getDiameter().
    /// </summary>
    private static float PolygonDiameter(List<Vector2> polygon)
    {
        float maxDistSq = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            for (int j = i + 1; j < polygon.Count; j++)
            {
                float dSq = Vector2.DistanceSquared(polygon[i], polygon[j]);
                if (dSq > maxDistSq) maxDistSq = dSq;
            }
        }
        return MathF.Sqrt(maxDistSq);
    }
}
