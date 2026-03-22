using Godot;
using Godot.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

[Tool]
public partial class World : Node3D
{
    [Export]
    public bool generate_world
    {
        get => false;
        set
        {
            if (value)
            {
                refreshResolvedNoiseLayers();
                applyShaderSurfaceNoiseParameters();
                regenerate();
            }
        }
    }
    [Export] public float radius = 30.0f;
    [Export] public Material patch_material;
    [ExportGroup("LOD (quadtree)")]
    /// <summary>Maximum subdivision depth from each of the 24 base patches (0 = no extra splits).</summary>
    public int max_lod_depth = 4;
    /// <summary>At depth d, subdivide when camera distance is below this (uses last entry if d exceeds array length).</summary>
    [Export] public float[] lod_split_distances = new float[] { 400f, 200f, 100f, 50f };
    /// <summary>
    /// Parallel to <see cref="lod_split_distances"/>: mesh subdivision count for that tier.
    /// Final passes = <c>max(by_quadtree_depth, by_distance_tier)</c> — see <see cref="getMeshSubdivisionCountForDepth"/>.
    /// </summary>
    [Export] public int[] lod_mesh_subdivisions = new int[] { 1, 2, 3, 4 };
    /// <summary>Minimum seconds between LOD updates per patch. 0 = check every physics frame (still avoids redundant mesh rebuilds).</summary>
    [Export] public double lod_update_interval_seconds = 0.2;
    [Export] public Array<WorldPatchNoise> noises = new Array<WorldPatchNoise>();
    [ExportGroup("Terrain noise")]
    [Export] public float terrain_height_scale = 2.0f;
    [ExportGroup("Shader surface noise (GPU)")]
    /// <summary>Extra vertex displacement in the terrain shader (world-space FBM). Tune with CPU terrain separately.</summary>
    [Export] public float shader_noise_height = 1.5f;
    [Export] public float shader_noise_frequency = 0.015f;

    private readonly List<WorldPatchNoise> resolved_noise_layers = new List<WorldPatchNoise>();

    /// <summary>True while initial world regeneration is baking meshes off-thread (patches should not start competing leaf bakes).</summary>
    public bool is_regenerating_baking { get; private set; }

    private int regeneration_sequence;

    private struct InitialPatchGeometry
    {
        public Vector3 p00;
        public Vector3 p10;
        public Vector3 p11;
        public Vector3 p01;
        public Vector2 uv00;
        public Vector2 uv10;
        public Vector2 uv11;
        public Vector2 uv01;
    }

    public override void _Ready()
    {
        max_lod_depth = Mathf.Max(max_lod_depth, lod_split_distances.Length);
        refreshResolvedNoiseLayers();
        applyShaderSurfaceNoiseParameters();
        regenerate();
    }

    /// <summary>Pushes <see cref="shader_noise_height"/> / <see cref="shader_noise_frequency"/> into <see cref="patch_material"/> when it is a <see cref="ShaderMaterial"/>.</summary>
    public void applyShaderSurfaceNoiseParameters()
    {
        if (patch_material is ShaderMaterial shader_material)
        {
            shader_material.SetShaderParameter("shader_noise_height", shader_noise_height);
            shader_material.SetShaderParameter("shader_noise_frequency", shader_noise_frequency);
        }
    }

    private void regenerate()
    {
        regeneration_sequence++;
        int regen_id = regeneration_sequence;
        is_regenerating_baking = true;

        clearChildren();

        List<InitialPatchGeometry> geometries = buildInitialPatchGeometryList();
        TerrainNoiseSnapshot[] noise_snapshots = captureTerrainNoiseSnapshots();
        int base_subdivisions = getMeshSubdivisionCountForDepth(0, float.MaxValue);

        for (int i = 0; i < geometries.Count; i++)
        {
            InitialPatchGeometry g = geometries[i];
            WorldPatch patch_node = new WorldPatch();
            patch_node.Name = "Patch_" + i;
            patch_node.initPatch(
                this,
                patch_material,
                g.p00,
                g.p10,
                g.p11,
                g.p01,
                g.uv00,
                g.uv10,
                g.uv11,
                g.uv01,
                0);
            AddChild(patch_node);
            SceneTree tree = GetTree();
            if (tree != null && tree.EditedSceneRoot != null)
            {
                patch_node.Owner = tree.EditedSceneRoot;
            }
        }

        InitialPatchGeometry[] geometry_array = geometries.ToArray();
        float height_scale = terrain_height_scale;
        int count = geometry_array.Length;

        int collision_subdivisions = Mathf.Max(0, base_subdivisions / 2);

        _ = Task.Run(() =>
        {
            var results = new WorldMeshBaker.PatchBakeResult[count];
            var collision_results = new WorldMeshBaker.PatchBakeResult[count];
            for (int i = 0; i < count; i++)
            {
                InitialPatchGeometry g = geometry_array[i];
                results[i] = WorldMeshBaker.bakeLeafPatch(
                    g.p00,
                    g.p10,
                    g.p11,
                    g.p01,
                    g.uv00,
                    g.uv10,
                    g.uv11,
                    g.uv01,
                    base_subdivisions,
                    noise_snapshots,
                    height_scale);
                collision_results[i] = WorldMeshBaker.bakeLeafPatch(
                    g.p00,
                    g.p10,
                    g.p11,
                    g.p01,
                    g.uv00,
                    g.uv10,
                    g.uv11,
                    g.uv01,
                    collision_subdivisions,
                    noise_snapshots,
                    height_scale);
            }

            Callable.From(() => finishRegenerationApplyMeshes(regen_id, results, collision_results)).CallDeferred();
        });
    }

    private void finishRegenerationApplyMeshes(
        int regen_id,
        WorldMeshBaker.PatchBakeResult[] results,
        WorldMeshBaker.PatchBakeResult[] collision_results)
    {
        if (regen_id != regeneration_sequence)
        {
            return;
        }

        Godot.Collections.Array<Node> children = GetChildren();
        int n = Mathf.Min(children.Count, results.Length);
        for (int i = 0; i < n; i++)
        {
            if (children[i] is WorldPatch wp && results[i] != null)
            {
                WorldMeshBaker.PatchBakeResult col = collision_results != null && i < collision_results.Length
                    ? collision_results[i]
                    : null;
                wp.applyBakedLeafMesh(results[i], col);
            }
        }

        is_regenerating_baking = false;
    }

    private List<InitialPatchGeometry> buildInitialPatchGeometryList()
    {
        var list = new List<InitialPatchGeometry>(24);

        Vector3[] face_normals = new Vector3[]
        {
            Vector3.Right,
            Vector3.Left,
            Vector3.Up,
            Vector3.Down,
            Vector3.Forward,
            Vector3.Back
        };

        for (int face_index = 0; face_index < face_normals.Length; face_index++)
        {
            Vector3 face_normal = face_normals[face_index];
            Vector3 axis_u = getFaceAxisU(face_normal);
            Vector3 axis_v = getFaceAxisV(face_normal, axis_u);
            appendFacePatchGeometries(face_normal, axis_u, axis_v, list);
        }

        return list;
    }

    private void appendFacePatchGeometries(
        Vector3 face_normal,
        Vector3 axis_u,
        Vector3 axis_v,
        List<InitialPatchGeometry> list)
    {
        const int cells_per_axis = 2;
        float step = 2.0f / cells_per_axis;

        for (int y = 0; y < cells_per_axis; y++)
        {
            for (int x = 0; x < cells_per_axis; x++)
            {
                float u0 = -1.0f + (x * step);
                float u1 = -1.0f + ((x + 1) * step);
                float v0 = -1.0f + (y * step);
                float v1 = -1.0f + ((y + 1) * step);

                list.Add(new InitialPatchGeometry
                {
                    p00 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v0),
                    p10 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v0),
                    p11 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v1),
                    p01 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v1),
                    uv00 = new Vector2(0, 0),
                    uv10 = new Vector2(1, 0),
                    uv11 = new Vector2(1, 1),
                    uv01 = new Vector2(0, 1)
                });
            }
        }
    }

    /// <summary>Call from main thread only (touches noise resources).</summary>
    public TerrainNoiseSnapshot[] captureTerrainNoiseSnapshots()
    {
        if (resolved_noise_layers.Count == 0)
        {
            return System.Array.Empty<TerrainNoiseSnapshot>();
        }

        var snapshots = new TerrainNoiseSnapshot[resolved_noise_layers.Count];
        int write = 0;
        for (int i = 0; i < resolved_noise_layers.Count; i++)
        {
            WorldPatchNoise layer = resolved_noise_layers[i];
            if (layer == null)
            {
                continue;
            }

            snapshots[write++] = layer.captureSnapshotForBake();
        }

        if (write == resolved_noise_layers.Count)
        {
            return snapshots;
        }

        var trimmed = new TerrainNoiseSnapshot[write];
        for (int i = 0; i < write; i++)
        {
            trimmed[i] = snapshots[i];
        }

        return trimmed;
    }

    private void clearChildren()
    {
        foreach (Node child in GetChildren())
        {
            child.Free();
        }
    }

    public ArrayMesh buildPatchMesh(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01)
    {
        return buildPatchMesh(
            p00,
            p10,
            p11,
            p01,
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1));
    }

    public ArrayMesh buildPatchMesh(
        Vector3 p00,
        Vector3 p10,
        Vector3 p11,
        Vector3 p01,
        Vector2 uv_00,
        Vector2 uv_10,
        Vector2 uv_11,
        Vector2 uv_01)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();

        addTriangle(vertices, normals, uvs, p00, p10, p11, uv_00, uv_10, uv_11);
        addTriangle(vertices, normals, uvs, p00, p11, p01, uv_00, uv_11, uv_01);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

        ArrayMesh mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private void addTriangle(
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
        // Godot treats clockwise triangles as front-facing by default.
        // Flip winding when our computed normal points outward so outside remains visible.
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

    private Vector3 cubeToSphere(Vector3 point_on_cube)
    {
        return point_on_cube.Normalized() * radius;
    }

    private Vector3 getFaceAxisU(Vector3 face_normal)
    {
        if (Mathf.Abs(face_normal.Y) > 0.5f)
        {
            return Vector3.Right;
        }
        return Vector3.Up;
    }

    private Vector3 getFaceAxisV(Vector3 face_normal, Vector3 axis_u)
    {
        return face_normal.Cross(axis_u).Normalized();
    }

    public bool shouldSubdivideAtDepth(float distance, int depth)
    {
        if (depth >= max_lod_depth)
        {
            return false;
        }

        if (lod_split_distances == null || lod_split_distances.Length == 0)
        {
            return false;
        }

        int index = Mathf.Clamp(depth, 0, lod_split_distances.Length - 1);
        return distance < lod_split_distances[index];
    }

    /// <summary>
    /// Triangle-subdivision passes before terrain displacement: max of (1) quadtree depth slot and (2) distance tier.
    /// Distance tier = how many consecutive <see cref="lod_split_distances"/> entries satisfy <c>distance &lt; threshold</c> (same idea as “closer” rings).
    /// This fixes “no visible mesh detail” when depth is still 0 but the camera is close (previously only <see cref="lod_mesh_subdivisions"/>[depth] applied).
    /// </summary>
    public int getMeshSubdivisionCountForDepth(int depth, float distance_to_patch)
    {
        int from_depth = getMeshSubdivisionAtArrayIndex(depth);
        int from_distance = getMeshSubdivisionFromDistanceTier(distance_to_patch);
        return Mathf.Max(from_depth, from_distance);
    }

    private int getMeshSubdivisionAtArrayIndex(int index)
    {
        if (lod_mesh_subdivisions == null || lod_mesh_subdivisions.Length == 0)
        {
            return 0;
        }

        int i = Mathf.Clamp(index, 0, lod_mesh_subdivisions.Length - 1);
        return Mathf.Max(0, lod_mesh_subdivisions[i]);
    }

    /// <summary>
    /// Count consecutive distance thresholds (from index 0) that <paramref name="distance_to_patch"/> passes; map to <see cref="lod_mesh_subdivisions"/> index <c>Clamp(tierCount - 1)</c>.
    /// </summary>
    private int getMeshSubdivisionFromDistanceTier(float distance_to_patch)
    {
        if (lod_split_distances == null || lod_mesh_subdivisions == null || lod_mesh_subdivisions.Length == 0)
        {
            return 0;
        }

        int tier_count = 0;
        int n = Mathf.Min(lod_split_distances.Length, lod_mesh_subdivisions.Length);
        for (int i = 0; i < n; i++)
        {
            if (distance_to_patch < lod_split_distances[i])
            {
                tier_count++;
            }
            else
            {
                break;
            }
        }

        int idx = Mathf.Clamp(tier_count - 1, 0, lod_mesh_subdivisions.Length - 1);
        return Mathf.Max(0, lod_mesh_subdivisions[idx]);
    }

    /// <summary>
    /// Radial displacement along the sphere normal. Sums all <see cref="WorldPatchNoise"/> layers.
    /// </summary>
    public float getTerrainDisplacement(Vector3 position_on_sphere)
    {
        if (resolved_noise_layers.Count == 0)
        {
            return 0.0f;
        }

        Vector3 unit = position_on_sphere.Normalized();
        float total = 0.0f;
        for (int i = 0; i < resolved_noise_layers.Count; i++)
        {
            WorldPatchNoise layer = resolved_noise_layers[i];
            if (layer == null)
            {
                continue;
            }

            total += layer.sampleNoise(unit);
        }

        return total * terrain_height_scale;
    }

    /// <summary>
    /// Rebuild cached noise layers after editing the noises array in the inspector (call from property setter if needed).
    /// </summary>
    public void refreshResolvedNoiseLayers()
    {
        resolved_noise_layers.Clear();
        if (noises == null || noises.Count == 0)
        {
            return;
        }

        for (int i = 0; i < noises.Count; i++)
        {
            WorldPatchNoise layer = tryResolveNoiseLayer(i);
            if (layer != null)
            {
                resolved_noise_layers.Add(layer);
            }
        }
    }

    private WorldPatchNoise tryResolveNoiseLayer(int index)
    {
        if (noises == null || index < 0 || index >= noises.Count)
        {
            return null;
        }

        Variant v = Variant.From(noises[index]);
        if (v.VariantType == Variant.Type.Nil)
        {
            return null;
        }

        WorldPatchNoise typed = v.As<WorldPatchNoise>();
        if (typed != null)
        {
            return typed;
        }

        Resource r = v.As<Resource>();
        if (r == null)
        {
            return null;
        }

        typed = r as WorldPatchNoise;
        if (typed != null)
        {
            return typed;
        }

        // Loader sometimes gives a plain Resource without the C# subclass — copy exported fields (same idea as PlanetTerrainLayer).
        WorldPatchNoise copy = new WorldPatchNoise();
        copyNoiseFromResource(r, copy);
        return copy;
    }

    private static void copyNoiseFromResource(Resource r, WorldPatchNoise target)
    {
        Variant noise_v = r.Get("noise");
        if (noise_v.VariantType != Variant.Type.Nil)
        {
            GodotObject obj = noise_v.AsGodotObject();
            if (obj is FastNoiseLite fn)
            {
                target.noise = fn;
            }
        }

        if (r.Get("amplitude").VariantType != Variant.Type.Nil)
        {
            target.amplitude = r.Get("amplitude").As<float>();
        }
    }
}