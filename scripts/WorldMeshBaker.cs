using Godot;
using System.Collections.Generic;

/// <summary>
/// Pure mesh math for background threads — no Godot <see cref="ArrayMesh"/> / nodes.
/// </summary>
public static class WorldMeshBaker
{
    public sealed class PatchBakeResult
    {
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector2[] uvs;
        public int subdivisions_used;
    }

    public static PatchBakeResult bakeLeafPatch(
        Vector3 p00,
        Vector3 p10,
        Vector3 p11,
        Vector3 p01,
        Vector2 uv00,
        Vector2 uv10,
        Vector2 uv11,
        Vector2 uv01,
        int subdivisions,
        TerrainNoiseSnapshot[] noise_layers,
        float terrain_height_scale)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();

        addTriangle(vertices, normals, uvs, p00, p10, p11, uv00, uv10, uv11);
        addTriangle(vertices, normals, uvs, p00, p11, p01, uv00, uv11, uv01);

        subdivideTriangleLists(vertices, uvs, Mathf.Max(0, subdivisions));

        Vector3[] vert_array = vertices.ToArray();
        Vector2[] uv_array = uvs.ToArray();

        for (int i = 0; i < vert_array.Length; i++)
        {
            Vector3 sphere_pos = vert_array[i];
            float height = WorldTerrainMath.getTerrainDisplacement(sphere_pos, noise_layers, terrain_height_scale);
            Vector3 radial = sphere_pos.Normalized();
            vert_array[i] = sphere_pos + radial * height;
        }

        Vector3[] normal_array = new Vector3[vert_array.Length];
        for (int n = 0; n < vert_array.Length; n++)
        {
            normal_array[n] = vert_array[n].Normalized();
        }

        return new PatchBakeResult
        {
            vertices = vert_array,
            normals = normal_array,
            uvs = uv_array,
            subdivisions_used = subdivisions
        };
    }

    private static void addTriangle(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 uv_a,
        Vector2 uv_b,
        Vector2 uv_c)
    {
        Vector3 normal = (b - a).Cross(c - a).Normalized();
        Vector3 center = (a + b + c) / 3.0f;
        if (normal.Dot(center) > 0.0f)
        {
            (b, c) = (c, b);
            (uv_b, uv_c) = (uv_c, uv_b);
        }

        normal = center.Normalized();

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        uvs.Add(uv_a);
        uvs.Add(uv_b);
        uvs.Add(uv_c);
    }

    private static void subdivideTriangleLists(List<Vector3> vertices, List<Vector2> uvs, int subdivisions)
    {
        for (int pass = 0; pass < subdivisions; pass++)
        {
            var new_vertices = new List<Vector3>();
            var new_uvs = new List<Vector2>();

            for (int vi = 0; vi < vertices.Count; vi += 3)
            {
                Vector3 a = vertices[vi];
                Vector3 b = vertices[vi + 1];
                Vector3 c = vertices[vi + 2];

                Vector2 uv_a = uvs[vi];
                Vector2 uv_b = uvs[vi + 1];
                Vector2 uv_c = uvs[vi + 2];

                Vector3 ab = midpointOnSphere(a, b);
                Vector3 bc = midpointOnSphere(b, c);
                Vector3 ca = midpointOnSphere(c, a);

                Vector2 uv_ab = (uv_a + uv_b) * 0.5f;
                Vector2 uv_bc = (uv_b + uv_c) * 0.5f;
                Vector2 uv_ca = (uv_c + uv_a) * 0.5f;

                addSubdividedTriangle(new_vertices, new_uvs, a, ab, ca, uv_a, uv_ab, uv_ca);
                addSubdividedTriangle(new_vertices, new_uvs, ab, b, bc, uv_ab, uv_b, uv_bc);
                addSubdividedTriangle(new_vertices, new_uvs, ca, bc, c, uv_ca, uv_bc, uv_c);
                addSubdividedTriangle(new_vertices, new_uvs, ab, bc, ca, uv_ab, uv_bc, uv_ca);
            }

            vertices.Clear();
            uvs.Clear();
            vertices.AddRange(new_vertices);
            uvs.AddRange(new_uvs);
        }
    }

    private static Vector3 midpointOnSphere(Vector3 a, Vector3 b)
    {
        Vector3 midpoint = (a + b) * 0.5f;
        float sphere_radius = (a.Length() + b.Length()) * 0.5f;
        if (midpoint == Vector3.Zero)
        {
            return a;
        }

        return midpoint.Normalized() * sphere_radius;
    }

    private static void addSubdividedTriangle(
        List<Vector3> vertices,
        List<Vector2> uvs,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 uv_a,
        Vector2 uv_b,
        Vector2 uv_c)
    {
        Vector3 normal = (b - a).Cross(c - a);
        Vector3 center = (a + b + c) / 3.0f;
        if (normal.Dot(center) > 0.0f)
        {
            (b, c) = (c, b);
            (uv_b, uv_c) = (uv_c, uv_b);
        }

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        uvs.Add(uv_a);
        uvs.Add(uv_b);
        uvs.Add(uv_c);
    }
}
