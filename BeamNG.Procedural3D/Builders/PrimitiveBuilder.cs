namespace BeamNG.Procedural3D.Builders;

using System.Numerics;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Provides builders for common primitive shapes for testing and general use.
/// </summary>
public static class PrimitiveBuilder
{
    /// <summary>
    /// Creates a box mesh centered at the specified position.
    /// </summary>
    /// <param name="center">Center position of the box.</param>
    /// <param name="size">Size of the box (width, height, depth).</param>
    /// <param name="materialName">Optional material name.</param>
    public static Mesh CreateBox(Vector3 center, Vector3 size, string? materialName = null)
    {
        var builder = new MeshBuilder()
            .WithName("Box")
            .WithMaterial(materialName);

        float hx = size.X / 2f;
        float hy = size.Y / 2f;
        float hz = size.Z / 2f;

        // Define the 8 corners
        Vector3[] corners =
        [
            center + new Vector3(-hx, -hy, -hz), // 0: left-bottom-back
            center + new Vector3(+hx, -hy, -hz), // 1: right-bottom-back
            center + new Vector3(+hx, +hy, -hz), // 2: right-top-back
            center + new Vector3(-hx, +hy, -hz), // 3: left-top-back
            center + new Vector3(-hx, -hy, +hz), // 4: left-bottom-front
            center + new Vector3(+hx, -hy, +hz), // 5: right-bottom-front
            center + new Vector3(+hx, +hy, +hz), // 6: right-top-front
            center + new Vector3(-hx, +hy, +hz), // 7: left-top-front
        ];

        // Front face (+Z)
        AddFace(builder, corners[4], corners[5], corners[6], corners[7], Vector3.UnitZ);
        // Back face (-Z)
        AddFace(builder, corners[1], corners[0], corners[3], corners[2], -Vector3.UnitZ);
        // Right face (+X)
        AddFace(builder, corners[5], corners[1], corners[2], corners[6], Vector3.UnitX);
        // Left face (-X)
        AddFace(builder, corners[0], corners[4], corners[7], corners[3], -Vector3.UnitX);
        // Top face (+Y)
        AddFace(builder, corners[7], corners[6], corners[2], corners[3], Vector3.UnitY);
        // Bottom face (-Y)
        AddFace(builder, corners[0], corners[1], corners[5], corners[4], -Vector3.UnitY);

        return builder.Build();
    }

    /// <summary>
    /// Creates a cylinder mesh between two points.
    /// </summary>
    /// <param name="start">Start point (center of bottom cap).</param>
    /// <param name="end">End point (center of top cap).</param>
    /// <param name="radius">Cylinder radius.</param>
    /// <param name="segments">Number of segments around the circumference.</param>
    /// <param name="materialName">Optional material name.</param>
    public static Mesh CreateCylinder(Vector3 start, Vector3 end, float radius, int segments = 16, string? materialName = null)
    {
        var builder = new MeshBuilder()
            .WithName("Cylinder")
            .WithMaterial(materialName);

        Vector3 axis = end - start;
        float height = axis.Length();
        Vector3 forward = Vector3.Normalize(axis);

        // Find perpendicular vectors
        Vector3 right = Vector3.Normalize(
            MathF.Abs(forward.Y) < 0.99f
                ? Vector3.Cross(forward, Vector3.UnitY)
                : Vector3.Cross(forward, Vector3.UnitX));
        Vector3 up = Vector3.Cross(right, forward);

        int bottomCenterIdx = builder.AddVertex(start, -forward, new Vector2(0.5f, 0.5f));
        int topCenterIdx = builder.AddVertex(end, forward, new Vector2(0.5f, 0.5f));

        // Generate vertices around circumference
        var bottomRing = new int[segments];
        var topRing = new int[segments];
        var bottomCapRing = new int[segments];
        var topCapRing = new int[segments];

        for (int i = 0; i < segments; i++)
        {
            float angle = (float)i / segments * MathF.PI * 2f;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            Vector3 radialDir = right * cos + up * sin;
            Vector3 bottomPos = start + radialDir * radius;
            Vector3 topPos = end + radialDir * radius;

            float u = (float)i / segments;

            // Side vertices (with outward normal)
            bottomRing[i] = builder.AddVertex(bottomPos, radialDir, new Vector2(u, 0f));
            topRing[i] = builder.AddVertex(topPos, radialDir, new Vector2(u, 1f));

            // Cap vertices (with up/down normals)
            float capU = 0.5f + cos * 0.5f;
            float capV = 0.5f + sin * 0.5f;
            bottomCapRing[i] = builder.AddVertex(bottomPos, -forward, new Vector2(capU, capV));
            topCapRing[i] = builder.AddVertex(topPos, forward, new Vector2(capU, capV));
        }

        // Generate side faces
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            builder.AddQuad(bottomRing[i], bottomRing[next], topRing[next], topRing[i]);
        }

        // Generate caps
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            // Bottom cap (reverse winding)
            builder.AddTriangle(bottomCenterIdx, bottomCapRing[next], bottomCapRing[i]);
            // Top cap
            builder.AddTriangle(topCenterIdx, topCapRing[i], topCapRing[next]);
        }

        return builder.Build();
    }

    /// <summary>
    /// Creates a plane mesh centered at the specified position.
    /// </summary>
    /// <param name="center">Center position of the plane.</param>
    /// <param name="normal">Normal direction of the plane.</param>
    /// <param name="width">Width of the plane.</param>
    /// <param name="height">Height of the plane.</param>
    /// <param name="materialName">Optional material name.</param>
    public static Mesh CreatePlane(Vector3 center, Vector3 normal, float width, float height, string? materialName = null)
    {
        var builder = new MeshBuilder()
            .WithName("Plane")
            .WithMaterial(materialName);

        normal = Vector3.Normalize(normal);

        // Find perpendicular vectors
        Vector3 right = Vector3.Normalize(
            MathF.Abs(normal.Y) < 0.99f
                ? Vector3.Cross(normal, Vector3.UnitY)
                : Vector3.Cross(normal, Vector3.UnitX));
        Vector3 up = Vector3.Cross(right, normal);

        float hw = width / 2f;
        float hh = height / 2f;

        // Counter-clockwise vertices
        int v0 = builder.AddVertex(center - right * hw - up * hh, normal, new Vector2(0, 0));
        int v1 = builder.AddVertex(center + right * hw - up * hh, normal, new Vector2(1, 0));
        int v2 = builder.AddVertex(center + right * hw + up * hh, normal, new Vector2(1, 1));
        int v3 = builder.AddVertex(center - right * hw + up * hh, normal, new Vector2(0, 1));

        builder.AddQuad(v0, v1, v2, v3);

        return builder.Build();
    }

    /// <summary>
    /// Creates a subdivided plane mesh for terrain or detailed surfaces.
    /// </summary>
    /// <param name="center">Center position of the plane.</param>
    /// <param name="normal">Normal direction of the plane.</param>
    /// <param name="width">Width of the plane.</param>
    /// <param name="height">Height of the plane.</param>
    /// <param name="subdivisionsX">Number of subdivisions along width.</param>
    /// <param name="subdivisionsY">Number of subdivisions along height.</param>
    /// <param name="materialName">Optional material name.</param>
    public static Mesh CreateSubdividedPlane(
        Vector3 center,
        Vector3 normal,
        float width,
        float height,
        int subdivisionsX,
        int subdivisionsY,
        string? materialName = null)
    {
        var builder = new MeshBuilder()
            .WithName("SubdividedPlane")
            .WithMaterial(materialName);

        normal = Vector3.Normalize(normal);

        // Find perpendicular vectors
        Vector3 right = Vector3.Normalize(
            MathF.Abs(normal.Y) < 0.99f
                ? Vector3.Cross(normal, Vector3.UnitY)
                : Vector3.Cross(normal, Vector3.UnitX));
        Vector3 up = Vector3.Cross(right, normal);

        int vertsX = subdivisionsX + 1;
        int vertsY = subdivisionsY + 1;

        // Generate vertices
        var vertexIndices = new int[vertsX, vertsY];
        for (int y = 0; y < vertsY; y++)
        {
            float ty = (float)y / subdivisionsY;
            float py = (ty - 0.5f) * height;

            for (int x = 0; x < vertsX; x++)
            {
                float tx = (float)x / subdivisionsX;
                float px = (tx - 0.5f) * width;

                Vector3 pos = center + right * px + up * py;
                vertexIndices[x, y] = builder.AddVertex(pos, normal, new Vector2(tx, ty));
            }
        }

        // Generate triangles
        for (int y = 0; y < subdivisionsY; y++)
        {
            for (int x = 0; x < subdivisionsX; x++)
            {
                int v0 = vertexIndices[x, y];
                int v1 = vertexIndices[x + 1, y];
                int v2 = vertexIndices[x + 1, y + 1];
                int v3 = vertexIndices[x, y + 1];

                builder.AddQuad(v0, v1, v2, v3);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Creates a sphere mesh.
    /// </summary>
    /// <param name="center">Center position of the sphere.</param>
    /// <param name="radius">Radius of the sphere.</param>
    /// <param name="segments">Number of horizontal segments.</param>
    /// <param name="rings">Number of vertical rings.</param>
    /// <param name="materialName">Optional material name.</param>
    public static Mesh CreateSphere(Vector3 center, float radius, int segments = 16, int rings = 8, string? materialName = null)
    {
        var builder = new MeshBuilder()
            .WithName("Sphere")
            .WithMaterial(materialName);

        // Generate vertices
        var vertexIndices = new int[segments + 1, rings + 1];

        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = MathF.PI * ring / rings;
            float y = MathF.Cos(phi);
            float ringRadius = MathF.Sin(phi);

            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = 2f * MathF.PI * seg / segments;
                float x = ringRadius * MathF.Cos(theta);
                float z = ringRadius * MathF.Sin(theta);

                Vector3 normal = new(x, y, z);
                Vector3 pos = center + normal * radius;
                Vector2 uv = new((float)seg / segments, (float)ring / rings);

                vertexIndices[seg, ring] = builder.AddVertex(pos, normal, uv);
            }
        }

        // Generate triangles
        for (int ring = 0; ring < rings; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                int v0 = vertexIndices[seg, ring];
                int v1 = vertexIndices[seg + 1, ring];
                int v2 = vertexIndices[seg + 1, ring + 1];
                int v3 = vertexIndices[seg, ring + 1];

                if (ring > 0)
                {
                    builder.AddTriangle(v0, v1, v2);
                }
                if (ring < rings - 1)
                {
                    builder.AddTriangle(v0, v2, v3);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Helper method to add a face (quad) to the builder.
    /// </summary>
    private static void AddFace(MeshBuilder builder, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
    {
        int v0 = builder.AddVertex(p0, normal, new Vector2(0, 0));
        int v1 = builder.AddVertex(p1, normal, new Vector2(1, 0));
        int v2 = builder.AddVertex(p2, normal, new Vector2(1, 1));
        int v3 = builder.AddVertex(p3, normal, new Vector2(0, 1));

        builder.AddQuad(v0, v1, v2, v3);
    }
}
