namespace BeamNG.Procedural3D.RoadMesh;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Specialized mesh builder for generating road surface geometry from cross-sections.
/// </summary>
public class RoadMeshBuilder : IMeshBuilder
{
    private readonly List<RoadCrossSection> _crossSections = [];
    private RoadMeshOptions _options = new();

    /// <summary>
    /// Gets the number of cross-sections added to the builder.
    /// </summary>
    public int CrossSectionCount => _crossSections.Count;

    /// <summary>
    /// Configures the builder with the specified options.
    /// </summary>
    public RoadMeshBuilder WithOptions(RoadMeshOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>
    /// Adds a single cross-section to the road.
    /// </summary>
    public RoadMeshBuilder AddCrossSection(RoadCrossSection crossSection)
    {
        if (crossSection == null)
            throw new ArgumentNullException(nameof(crossSection));

        _crossSections.Add(crossSection);
        return this;
    }

    /// <summary>
    /// Adds multiple cross-sections to the road.
    /// </summary>
    public RoadMeshBuilder AddCrossSections(IEnumerable<RoadCrossSection> crossSections)
    {
        if (crossSections == null)
            throw new ArgumentNullException(nameof(crossSections));

        foreach (var cs in crossSections)
        {
            AddCrossSection(cs);
        }
        return this;
    }

    /// <summary>
    /// Builds the road mesh from the configured cross-sections.
    /// </summary>
    public Mesh Build()
    {
        if (_crossSections.Count < _options.MinimumCrossSections)
        {
            return new Mesh { Name = _options.MeshName, MaterialName = _options.MaterialName };
        }

        var builder = new MeshBuilder()
            .WithName(_options.MeshName)
            .WithMaterial(_options.MaterialName);

        // Build main road surface
        BuildRoadSurface(builder);

        // Build optional components
        if (_options.IncludeShoulders)
        {
            // Note: Shoulders would be built as separate meshes with different materials
            // For now, we add them to the same mesh
            BuildShoulders(builder);
        }

        if (_options.IncludeCurbs)
        {
            BuildCurbs(builder);
        }

        if (_options.GenerateEndCaps)
        {
            BuildEndCaps(builder);
        }

        // Calculate normals
        if (_options.SmoothNormals)
        {
            builder.CalculateSmoothNormals();
        }
        else
        {
            builder.CalculateFlatNormals();
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds multiple meshes with separate materials for road, shoulders, and curbs.
    /// </summary>
    public IReadOnlyList<Mesh> BuildSeparateMeshes()
    {
        var meshes = new List<Mesh>();

        if (_crossSections.Count < _options.MinimumCrossSections)
        {
            return meshes;
        }

        // Main road surface
        var roadBuilder = new MeshBuilder()
            .WithName(_options.MeshName)
            .WithMaterial(_options.MaterialName);
        BuildRoadSurface(roadBuilder);
        ApplyNormals(roadBuilder);
        meshes.Add(roadBuilder.Build());

        // Shoulders (if enabled)
        if (_options.IncludeShoulders)
        {
            var shoulderBuilder = new MeshBuilder()
                .WithName($"{_options.MeshName}_Shoulders")
                .WithMaterial(_options.ShoulderMaterialName);
            BuildShoulders(shoulderBuilder);
            ApplyNormals(shoulderBuilder);
            var shoulderMesh = shoulderBuilder.Build();
            if (shoulderMesh.HasGeometry)
            {
                meshes.Add(shoulderMesh);
            }
        }

        // Curbs (if enabled)
        if (_options.IncludeCurbs)
        {
            var curbBuilder = new MeshBuilder()
                .WithName($"{_options.MeshName}_Curbs")
                .WithMaterial(_options.CurbMaterialName);
            BuildCurbs(curbBuilder);
            ApplyNormals(curbBuilder);
            var curbMesh = curbBuilder.Build();
            if (curbMesh.HasGeometry)
            {
                meshes.Add(curbMesh);
            }
        }

        return meshes;
    }

    /// <summary>
    /// Clears all cross-sections and resets the builder.
    /// </summary>
    public void Clear()
    {
        _crossSections.Clear();
        _options = new RoadMeshOptions();
    }

    private void BuildRoadSurface(MeshBuilder builder)
    {
        // Add vertices for each cross-section (left and right edges)
        for (int i = 0; i < _crossSections.Count; i++)
        {
            var cs = _crossSections[i];

            Vector3 leftPos = cs.GetLeftEdgePosition();
            Vector3 rightPos = cs.GetRightEdgePosition();

            // UV coordinates: U = distance along road, V = 0 (left) to 1 (right)
            float u = cs.DistanceAlongRoad / _options.TextureRepeatMetersU;
            Vector2 leftUV = new(u, 0f);
            Vector2 rightUV = new(u, cs.WidthMeters / _options.TextureRepeatMetersV);

            // Initial normal pointing up (will be recalculated)
            Vector3 initialNormal = Vector3.UnitZ;

            builder.AddVertex(leftPos, initialNormal, leftUV);
            builder.AddVertex(rightPos, initialNormal, rightUV);
        }

        // Create triangles between consecutive cross-sections
        for (int i = 0; i < _crossSections.Count - 1; i++)
        {
            int baseIdx = i * 2;
            // Vertices: [left0, right0, left1, right1]
            int left0 = baseIdx;
            int right0 = baseIdx + 1;
            int left1 = baseIdx + 2;
            int right1 = baseIdx + 3;

            // Two triangles forming a quad (counter-clockwise winding for upward-facing normals)
            // Triangle 1: left0 -> right0 -> left1
            builder.AddTriangle(left0, right0, left1);
            // Triangle 2: right0 -> right1 -> left1
            builder.AddTriangle(right0, right1, left1);
        }
    }

    private void BuildShoulders(MeshBuilder builder)
    {
        int vertexOffset = builder.VertexCount;

        // Add vertices for shoulder edges
        for (int i = 0; i < _crossSections.Count; i++)
        {
            var cs = _crossSections[i];

            // Left shoulder (normal points right, so left outer is negative normal direction)
            Vector3 leftRoadEdge = cs.GetLeftEdgePosition();
            Vector2 leftOuterPoint2D = cs.CenterPoint - cs.NormalDirection * (cs.WidthMeters / 2f + _options.ShoulderWidthMeters);
            float leftOuterElevation = leftRoadEdge.Z - _options.ShoulderDropMeters;
            Vector3 leftShoulderOuter = new(leftOuterPoint2D.X, leftOuterPoint2D.Y, leftOuterElevation);

            // Right shoulder (normal points right, so right outer is positive normal direction)
            Vector3 rightRoadEdge = cs.GetRightEdgePosition();
            Vector2 rightOuterPoint2D = cs.CenterPoint + cs.NormalDirection * (cs.WidthMeters / 2f + _options.ShoulderWidthMeters);
            float rightOuterElevation = rightRoadEdge.Z - _options.ShoulderDropMeters;
            Vector3 rightShoulderOuter = new(rightOuterPoint2D.X, rightOuterPoint2D.Y, rightOuterElevation);

            // UV coordinates for shoulders
            float u = cs.DistanceAlongRoad / _options.TextureRepeatMetersU;

            // Left shoulder: road edge (V=0) to outer (V=shoulder width)
            builder.AddVertex(leftRoadEdge, Vector3.UnitZ, new Vector2(u, 0f));
            builder.AddVertex(leftShoulderOuter, Vector3.UnitZ, new Vector2(u, _options.ShoulderWidthMeters / _options.TextureRepeatMetersV));

            // Right shoulder: outer (V=0) to road edge (V=shoulder width)
            builder.AddVertex(rightShoulderOuter, Vector3.UnitZ, new Vector2(u, 0f));
            builder.AddVertex(rightRoadEdge, Vector3.UnitZ, new Vector2(u, _options.ShoulderWidthMeters / _options.TextureRepeatMetersV));
        }

        // Create triangles for shoulders
        for (int i = 0; i < _crossSections.Count - 1; i++)
        {
            int baseIdx = vertexOffset + i * 4;

            // Left shoulder quad
            int leftInner0 = baseIdx;
            int leftOuter0 = baseIdx + 1;
            int leftInner1 = baseIdx + 4;
            int leftOuter1 = baseIdx + 5;

            builder.AddTriangle(leftInner0, leftOuter0, leftInner1);
            builder.AddTriangle(leftOuter0, leftOuter1, leftInner1);

            // Right shoulder quad
            int rightOuter0 = baseIdx + 2;
            int rightInner0 = baseIdx + 3;
            int rightOuter1 = baseIdx + 6;
            int rightInner1 = baseIdx + 7;

            builder.AddTriangle(rightOuter0, rightInner0, rightOuter1);
            builder.AddTriangle(rightInner0, rightInner1, rightOuter1);
        }
    }

    private void BuildCurbs(MeshBuilder builder)
    {
        int vertexOffset = builder.VertexCount;

        // Curbs are vertical faces at the road edge
        for (int i = 0; i < _crossSections.Count; i++)
        {
            var cs = _crossSections[i];

            // Left curb vertices (4 per cross-section: bottom-inner, top-inner, top-outer, bottom-outer)
            // Normal points right, so left outer is negative normal direction
            Vector3 leftEdge = cs.GetLeftEdgePosition();
            Vector2 leftOuterPoint2D = cs.CenterPoint - cs.NormalDirection * (cs.WidthMeters / 2f + _options.CurbWidthMeters);

            Vector3 leftBottomInner = leftEdge;
            Vector3 leftTopInner = leftEdge + new Vector3(0, 0, _options.CurbHeightMeters);
            Vector3 leftTopOuter = new(leftOuterPoint2D.X, leftOuterPoint2D.Y, leftEdge.Z + _options.CurbHeightMeters);
            Vector3 leftBottomOuter = new(leftOuterPoint2D.X, leftOuterPoint2D.Y, leftEdge.Z);

            // Right curb vertices
            // Normal points right, so right outer is positive normal direction
            Vector3 rightEdge = cs.GetRightEdgePosition();
            Vector2 rightOuterPoint2D = cs.CenterPoint + cs.NormalDirection * (cs.WidthMeters / 2f + _options.CurbWidthMeters);

            Vector3 rightBottomInner = rightEdge;
            Vector3 rightTopInner = rightEdge + new Vector3(0, 0, _options.CurbHeightMeters);
            Vector3 rightTopOuter = new(rightOuterPoint2D.X, rightOuterPoint2D.Y, rightEdge.Z + _options.CurbHeightMeters);
            Vector3 rightBottomOuter = new(rightOuterPoint2D.X, rightOuterPoint2D.Y, rightEdge.Z);

            float u = cs.DistanceAlongRoad / _options.TextureRepeatMetersU;

            // Left curb - inner face (facing road)
            builder.AddVertex(leftBottomInner, -cs.GetNormal3D(), new Vector2(u, 0f));
            builder.AddVertex(leftTopInner, -cs.GetNormal3D(), new Vector2(u, _options.CurbHeightMeters));

            // Left curb - top face
            builder.AddVertex(leftTopInner, Vector3.UnitZ, new Vector2(u, 0f));
            builder.AddVertex(leftTopOuter, Vector3.UnitZ, new Vector2(u, _options.CurbWidthMeters));

            // Right curb - inner face (facing road)
            builder.AddVertex(rightBottomInner, cs.GetNormal3D(), new Vector2(u, 0f));
            builder.AddVertex(rightTopInner, cs.GetNormal3D(), new Vector2(u, _options.CurbHeightMeters));

            // Right curb - top face
            builder.AddVertex(rightTopInner, Vector3.UnitZ, new Vector2(u, 0f));
            builder.AddVertex(rightTopOuter, Vector3.UnitZ, new Vector2(u, _options.CurbWidthMeters));
        }

        // Create triangles for curb faces
        for (int i = 0; i < _crossSections.Count - 1; i++)
        {
            int baseIdx = vertexOffset + i * 8;
            int nextBaseIdx = baseIdx + 8;

            // Left curb inner face
            builder.AddTriangle(baseIdx, baseIdx + 1, nextBaseIdx);
            builder.AddTriangle(nextBaseIdx, baseIdx + 1, nextBaseIdx + 1);

            // Left curb top face
            builder.AddTriangle(baseIdx + 2, baseIdx + 3, nextBaseIdx + 2);
            builder.AddTriangle(nextBaseIdx + 2, baseIdx + 3, nextBaseIdx + 3);

            // Right curb inner face
            builder.AddTriangle(baseIdx + 4, nextBaseIdx + 4, baseIdx + 5);
            builder.AddTriangle(baseIdx + 5, nextBaseIdx + 4, nextBaseIdx + 5);

            // Right curb top face
            builder.AddTriangle(baseIdx + 6, nextBaseIdx + 6, baseIdx + 7);
            builder.AddTriangle(baseIdx + 7, nextBaseIdx + 6, nextBaseIdx + 7);
        }
    }

    private void BuildEndCaps(MeshBuilder builder)
    {
        if (_crossSections.Count < 2)
            return;

        // Start cap
        var startCs = _crossSections[0];
        Vector3 startLeft = startCs.GetLeftEdgePosition();
        Vector3 startRight = startCs.GetRightEdgePosition();
        Vector3 startCenter = startCs.GetCenterPosition();

        // Simple triangle fan for start cap (facing backward along road)
        int startCenterIdx = builder.AddVertex(startCenter, -GetTangent3D(startCs), new Vector2(0.5f, 0.5f));
        int startLeftIdx = builder.AddVertex(startLeft, -GetTangent3D(startCs), new Vector2(0f, 0.5f));
        int startRightIdx = builder.AddVertex(startRight, -GetTangent3D(startCs), new Vector2(1f, 0.5f));
        builder.AddTriangle(startCenterIdx, startRightIdx, startLeftIdx);

        // End cap
        var endCs = _crossSections[^1];
        Vector3 endLeft = endCs.GetLeftEdgePosition();
        Vector3 endRight = endCs.GetRightEdgePosition();
        Vector3 endCenter = endCs.GetCenterPosition();

        // Simple triangle fan for end cap (facing forward along road)
        int endCenterIdx = builder.AddVertex(endCenter, GetTangent3D(endCs), new Vector2(0.5f, 0.5f));
        int endLeftIdx = builder.AddVertex(endLeft, GetTangent3D(endCs), new Vector2(0f, 0.5f));
        int endRightIdx = builder.AddVertex(endRight, GetTangent3D(endCs), new Vector2(1f, 0.5f));
        builder.AddTriangle(endCenterIdx, endLeftIdx, endRightIdx);
    }

    private void ApplyNormals(MeshBuilder builder)
    {
        if (_options.SmoothNormals)
        {
            builder.CalculateSmoothNormals();
        }
        else
        {
            builder.CalculateFlatNormals();
        }
    }

    private static Vector3 GetTangent3D(RoadCrossSection cs)
    {
        return new Vector3(cs.TangentDirection.X, cs.TangentDirection.Y, 0f);
    }
}

/// <summary>
/// Extension methods for RoadCrossSection.
/// </summary>
internal static class RoadCrossSectionExtensions
{
    /// <summary>
    /// Gets the normal direction as a 3D vector (in XY plane).
    /// </summary>
    public static Vector3 GetNormal3D(this RoadCrossSection cs)
    {
        return new Vector3(cs.NormalDirection.X, cs.NormalDirection.Y, 0f);
    }
}
