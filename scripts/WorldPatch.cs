using Godot;
using System.Collections.Generic;

/// <summary>
/// Sphere quad patch: either a leaf mesh or four child patches. LOD uses distance from
/// <see cref="CameraController"/> to this patch's center on the sphere (same idea as <see cref="PlanetTriangle"/>).
/// </summary>
[Tool]
public partial class WorldPatch : Node3D
{
    private World world;
    private Material material;
    private Vector3 p00;
    private Vector3 p10;
    private Vector3 p11;
    private Vector3 p01;
    private Vector2 uv00;
    private Vector2 uv10;
    private Vector2 uv11;
    private Vector2 uv01;
    private int depth;
    private MeshInstance3D mesh_instance;
    private double lod_time_accum;
    private int last_built_mesh_subdivisions = -1;

    public void initPatch(
        World world_ref,
        Material mat,
        Vector3 p00_corner,
        Vector3 p10_corner,
        Vector3 p11_corner,
        Vector3 p01_corner,
        Vector2 uv_00,
        Vector2 uv_10,
        Vector2 uv_11,
        Vector2 uv_01,
        int depth_level)
    {
        world = world_ref;
        material = mat;
        p00 = p00_corner;
        p10 = p10_corner;
        p11 = p11_corner;
        p01 = p01_corner;
        uv00 = uv_00;
        uv10 = uv_10;
        uv11 = uv_11;
        uv01 = uv_01;
        depth = depth_level;
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
        {
            ensureLeafMesh();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (world == null)
        {
            return;
        }

        if (Engine.IsEditorHint())
        {
            return;
        }

        if (world.lod_update_interval_seconds > 0.0)
        {
            lod_time_accum += delta;
            if (lod_time_accum < world.lod_update_interval_seconds)
            {
                return;
            }

            lod_time_accum = 0.0;
        }

        float distance = getDistanceToCamera();
        bool should_subdivide = world.shouldSubdivideAtDepth(distance, depth);

        bool has_world_patch_children = hasWorldPatchChildren();
        bool has_mesh = mesh_instance != null && IsInstanceValid(mesh_instance);

        // Already in the correct state — do nothing (avoids rebuilding mesh every frame).
        if (should_subdivide && has_world_patch_children)
        {
            return;
        }

        int desired_mesh_subdivisions = world.getMeshSubdivisionCountForDepth(depth, distance);
        if (!should_subdivide && !has_world_patch_children && has_mesh && last_built_mesh_subdivisions == desired_mesh_subdivisions)
        {
            return;
        }

        if (should_subdivide)
        {
            freeLeafMesh();
            if (!has_world_patch_children)
            {
                createFourChildren();
            }
        }
        else
        {
            collapseWorldPatchChildren();
            ensureLeafMesh();
        }
    }

    private bool hasWorldPatchChildren()
    {
        Godot.Collections.Array<Node> children = GetChildren();
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is WorldPatch)
            {
                return true;
            }
        }

        return false;
    }

    private float getDistanceToCamera()
    {
        if (CameraController.instance == null)
        {
            return float.MaxValue;
        }

        Vector3 cam = CameraController.instance.GlobalPosition;
        Transform3D xf = GlobalTransform;
        Vector3 w00 = xf * p00;
        Vector3 w10 = xf * p10;
        Vector3 w11 = xf * p11;
        Vector3 w01 = xf * p01;

        return WorldPatchGeometry.distancePointToPatchQuad(cam, w00, w10, w11, w01);
    }

    private void freeLeafMesh()
    {
        if (mesh_instance != null && IsInstanceValid(mesh_instance))
        {
            mesh_instance.QueueFree();
            mesh_instance = null;
        }

        last_built_mesh_subdivisions = -1;
    }

    /// <summary>
    /// Only remove quadtree child patches. Never frees the leaf <see cref="MeshInstance3D"/> (that was causing a full mesh rebuild every frame).
    /// </summary>
    private void collapseWorldPatchChildren()
    {
        Godot.Collections.Array<Node> children = GetChildren();
        for (int i = children.Count - 1; i >= 0; i--)
        {
            Node child = children[i];
            if (child is WorldPatch)
            {
                child.Free();
            }
        }
    }

    private void createFourChildren()
    {
        Vector3 e0 = midpointOnSphere(p00, p10);
        Vector3 e1 = midpointOnSphere(p10, p11);
        Vector3 e2 = midpointOnSphere(p11, p01);
        Vector3 e3 = midpointOnSphere(p01, p00);
        Vector3 c = midpointOnSphere(midpointOnSphere(p00, p11), midpointOnSphere(p10, p01));

        Vector2 ue0 = (uv00 + uv10) * 0.5f;
        Vector2 ue1 = (uv10 + uv11) * 0.5f;
        Vector2 ue2 = (uv11 + uv01) * 0.5f;
        Vector2 ue3 = (uv01 + uv00) * 0.5f;
        Vector2 uc = (uv00 + uv10 + uv11 + uv01) * 0.25f;

        addChildPatch(p00, e0, c, e3, uv00, ue0, uc, ue3, 0);
        addChildPatch(e0, p10, e1, c, ue0, uv10, ue1, uc, 1);
        addChildPatch(c, e1, p11, e2, uc, ue1, uv11, ue2, 2);
        addChildPatch(e3, c, e2, p01, ue3, uc, ue2, uv01, 3);
    }

    private void addChildPatch(
        Vector3 c00,
        Vector3 c10,
        Vector3 c11,
        Vector3 c01,
        Vector2 u00,
        Vector2 u10,
        Vector2 u11,
        Vector2 u01,
        int child_index)
    {
        WorldPatch child = new WorldPatch();
        child.Name = Name + "_" + child_index;
        child.initPatch(world, material, c00, c10, c11, c01, u00, u10, u11, u01, depth + 1);
        AddChild(child);
        SceneTree tree = GetTree();
        if (tree != null && tree.EditedSceneRoot != null)
        {
            child.Owner = tree.EditedSceneRoot;
        }
    }

    private void ensureLeafMesh()
    {
        if (GetChildCount() > 0)
        {
            return;
        }

        float distance_for_mesh = getDistanceToCamera();
        int desired_subdivisions = world.getMeshSubdivisionCountForDepth(depth, distance_for_mesh);
        if (mesh_instance != null && IsInstanceValid(mesh_instance) && last_built_mesh_subdivisions == desired_subdivisions)
        {
            return;
        }

        freeLeafMesh();

        ArrayMesh mesh = world.buildPatchMesh(p00, p10, p11, p01, uv00, uv10, uv11, uv01);
        subdivideTriangleMesh(mesh, desired_subdivisions);
        applyTerrainDisplacement(mesh, world);

        last_built_mesh_subdivisions = desired_subdivisions;

        mesh_instance = new MeshInstance3D();
        mesh_instance.Name = "Mesh";
        mesh_instance.Mesh = mesh;
        mesh_instance.MaterialOverride = material;
        AddChild(mesh_instance);
        SceneTree tree = GetTree();
        if (tree != null && tree.EditedSceneRoot != null)
        {
            mesh_instance.Owner = tree.EditedSceneRoot;
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

    /// <summary>
    /// Each pass splits every triangle into 4 (midpoints on sphere), same as former mesh-only LOD.
    /// </summary>
    private static void subdivideTriangleMesh(ArrayMesh mesh, int subdivisions)
    {
        if (subdivisions <= 0)
        {
            return;
        }

        for (int pass = 0; pass < subdivisions; pass++)
        {
            if (mesh.GetSurfaceCount() == 0)
            {
                return;
            }

            var arrays = mesh.SurfaceGetArrays(0);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
            Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].As<Vector2[]>();

            List<Vector3> new_vertices = new List<Vector3>();
            List<Vector2> new_uvs = new List<Vector2>();

            for (int vertex_index = 0; vertex_index < vertices.Length; vertex_index += 3)
            {
                Vector3 a = vertices[vertex_index];
                Vector3 b = vertices[vertex_index + 1];
                Vector3 c = vertices[vertex_index + 2];

                Vector2 uv_a = getUvAt(uvs, vertex_index);
                Vector2 uv_b = getUvAt(uvs, vertex_index + 1);
                Vector2 uv_c = getUvAt(uvs, vertex_index + 2);

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

            mesh.ClearSurfaces();

            Vector3[] normal_array = new Vector3[new_vertices.Count];
            for (int n = 0; n < new_vertices.Count; n++)
            {
                normal_array[n] = new_vertices[n].Normalized();
            }

            var new_arrays = new Godot.Collections.Array();
            new_arrays.Resize((int)Mesh.ArrayType.Max);
            new_arrays[(int)Mesh.ArrayType.Vertex] = new_vertices.ToArray();
            new_arrays[(int)Mesh.ArrayType.Normal] = normal_array;
            new_arrays[(int)Mesh.ArrayType.TexUV] = new_uvs.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, new_arrays);
        }
    }

    private static Vector2 getUvAt(Vector2[] uvs, int index)
    {
        if (uvs == null || index < 0 || index >= uvs.Length)
        {
            return Vector2.Zero;
        }

        return uvs[index];
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

    private static void applyTerrainDisplacement(ArrayMesh mesh, World world_ref)
    {
        if (mesh.GetSurfaceCount() == 0)
        {
            return;
        }

        var arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].As<Vector2[]>();

        if (vertices == null || vertices.Length == 0)
        {
            return;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 sphere_pos = vertices[i];
            float height = world_ref.getTerrainDisplacement(sphere_pos);
            Vector3 radial = sphere_pos.Normalized();
            vertices[i] = sphere_pos + radial * height;
        }

        Vector3[] normals = new Vector3[vertices.Length];
        for (int n = 0; n < vertices.Length; n++)
        {
            normals[n] = vertices[n].Normalized();
        }

        var new_arrays = new Godot.Collections.Array();
        new_arrays.Resize((int)Mesh.ArrayType.Max);
        new_arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        new_arrays[(int)Mesh.ArrayType.Normal] = normals;
        new_arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        mesh.ClearSurfaces();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, new_arrays);
    }
}
