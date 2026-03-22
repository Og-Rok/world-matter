using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    private int leaf_bake_sequence;
    private bool mesh_bake_pending;

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
        if (Engine.IsEditorHint() && world != null && world.is_regenerating_baking)
        {
            return;
        }

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

        if (world.is_regenerating_baking)
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
        if (hasWorldPatchChildren())
        {
            return;
        }

        if (mesh_bake_pending)
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

        TerrainNoiseSnapshot[] noise_snapshots = world.captureTerrainNoiseSnapshots();
        float terrain_scale = world.terrain_height_scale;

        if (Engine.IsEditorHint())
        {
            WorldMeshBaker.PatchBakeResult baked = WorldMeshBaker.bakeLeafPatch(
                p00,
                p10,
                p11,
                p01,
                uv00,
                uv10,
                uv11,
                uv01,
                desired_subdivisions,
                noise_snapshots,
                terrain_scale);
            applyBakedLeafMesh(baked);
            return;
        }

        mesh_bake_pending = true;
        leaf_bake_sequence++;
        int bake_id = leaf_bake_sequence;
        Vector3 c00 = p00;
        Vector3 c10 = p10;
        Vector3 c11 = p11;
        Vector3 c01 = p01;
        Vector2 u00 = uv00;
        Vector2 u10 = uv10;
        Vector2 u11 = uv11;
        Vector2 u01 = uv01;
        int subdiv = desired_subdivisions;

        _ = Task.Run(() =>
        {
            WorldMeshBaker.PatchBakeResult baked = null;
            try
            {
                baked = WorldMeshBaker.bakeLeafPatch(
                    c00,
                    c10,
                    c11,
                    c01,
                    u00,
                    u10,
                    u11,
                    u01,
                    subdiv,
                    noise_snapshots,
                    terrain_scale);
            }
            catch (Exception ex)
            {
                GD.PushError("WorldPatch mesh bake failed: " + ex.Message);
            }

            int captured_id = bake_id;
            Callable.From(() => completeAsyncLeafMeshBake(captured_id, baked, subdiv)).CallDeferred();
        });
    }

    /// <summary>Main thread only. Replaces any existing leaf mesh.</summary>
    public void applyBakedLeafMesh(WorldMeshBaker.PatchBakeResult baked)
    {
        if (baked == null)
        {
            return;
        }

        freeLeafMesh();

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = baked.vertices;
        arrays[(int)Mesh.ArrayType.Normal] = baked.normals;
        arrays[(int)Mesh.ArrayType.TexUV] = baked.uvs;

        ArrayMesh mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        last_built_mesh_subdivisions = baked.subdivisions_used;

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

    private void completeAsyncLeafMeshBake(int bake_id, WorldMeshBaker.PatchBakeResult baked, int subdivisions_built)
    {
        mesh_bake_pending = false;

        if (!IsInstanceValid(this))
        {
            return;
        }

        if (world == null || !IsInstanceValid(world))
        {
            return;
        }

        if (bake_id != leaf_bake_sequence)
        {
            return;
        }

        if (hasWorldPatchChildren())
        {
            return;
        }

        float distance_for_mesh = getDistanceToCamera();
        int desired_now = world.getMeshSubdivisionCountForDepth(depth, distance_for_mesh);
        if (desired_now != subdivisions_built)
        {
            return;
        }

        applyBakedLeafMesh(baked);
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
}
