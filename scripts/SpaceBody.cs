using Godot;

/// <summary>
/// Tool node that builds a cube-sphere: six sides × 3×3 quads (54 <see cref="MeshInstance3D"/> children).
/// Face vertices are sampled on an axis-aligned cube, then projected onto a sphere of <see cref="radius"/>.
/// </summary>
[Tool]
public partial class SpaceBody : Node3D
{

    /// <summary>Distance from local origin to every vertex after projection (sphere radius).</summary>
    [Export] public float radius = 1.0f;

    [Export] public Material face_material;

    [Export]
    public bool generate_world
    {
        get => false;
        set
        {
            if (value)
            {
                regenerate();
            }
        }
    }

    public override void _Ready()
    {
        regenerate();
    }

    private void regenerate()
    {
        clearFaceMeshes();

        float h = radius * 0.5f;
        if (h <= 0.0f || radius <= 0.0f)
        {
            return;
        }

        // Corner order matches buildQuadMesh UVs: v0 (u=0,v=1), v1 (u=1,v=1), v2 (u=1,v=0), v3 (u=0,v=0).
        addSubdividedFace("FaceRight",
            new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(h, -h, h));

        addSubdividedFace("FaceLeft",
            new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(-h, h, -h), new Vector3(-h, -h, -h));

        addSubdividedFace("FaceUp",
            new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(-h, h, h));

        addSubdividedFace("FaceDown",
            new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(-h, -h, -h));

        // Godot Forward = -Z
        addSubdividedFace("FaceForward",
            new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h));

        addSubdividedFace("FaceBack",
            new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h));
    }

    private void clearFaceMeshes()
    {
        Godot.Collections.Array<Node> children = GetChildren();
        for (int i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is MeshInstance3D)
            {
                children[i].Free();
            }
        }
    }

    /// <summary>
    /// Splits one face (four corners in UV order) into a 3×3 grid of child meshes named
    /// <c>{prefix}_{u}_{v}</c> (u along v0→v1, v along v3→v0 in UV space; indices 0..2).
    /// </summary>
    private void addSubdividedFace(
        string prefix,
        Vector3 v0_u0_v1,
        Vector3 v1_u1_v1,
        Vector3 v2_u1_v0,
        Vector3 v3_u0_v0)
    {
        const int cells = 3;
        for (int iu = 0; iu < cells; iu++)
        {
            for (int iv = 0; iv < cells; iv++)
            {
                float u0 = iu / (float)cells;
                float u1 = (iu + 1) / (float)cells;
                float vv0 = iv / (float)cells;
                float vv1 = (iv + 1) / (float)cells;

                // Same corner order as full-face quad: bottom (high v_uv) then top (low v_uv).
                Vector3 q0 = projectCubePointOntoSphere(quadPointAtUv(v0_u0_v1, v1_u1_v1, v2_u1_v0, v3_u0_v0, u0, vv1));
                Vector3 q1 = projectCubePointOntoSphere(quadPointAtUv(v0_u0_v1, v1_u1_v1, v2_u1_v0, v3_u0_v0, u1, vv1));
                Vector3 q2 = projectCubePointOntoSphere(quadPointAtUv(v0_u0_v1, v1_u1_v1, v2_u1_v0, v3_u0_v0, u1, vv0));
                Vector3 q3 = projectCubePointOntoSphere(quadPointAtUv(v0_u0_v1, v1_u1_v1, v2_u1_v0, v3_u0_v0, u0, vv0));

                string name = prefix + "_" + iu + "_" + iv;
                addFaceMesh(name, buildQuadMesh(q0, q1, q2, q3));
            }
        }
    }

    /// <summary>
    /// Bilinear point on quad: u ∈ [0,1] along v0→v1, vUv ∈ [0,1] from v3/v2 (0) toward v0/v1 (1).
    /// </summary>
    private static Vector3 quadPointAtUv(
        Vector3 v0_u0_v1,
        Vector3 v1_u1_v1,
        Vector3 v2_u1_v0,
        Vector3 v3_u0_v0,
        float u,
        float vUv)
    {
        Vector3 bottom = v0_u0_v1.Lerp(v1_u1_v1, u);
        Vector3 top = v3_u0_v0.Lerp(v2_u1_v0, u);
        return top.Lerp(bottom, vUv);
    }

    /// <summary>Maps a point on the sampling cube to the sphere: direction from origin, length <see cref="radius"/>.</summary>
    private Vector3 projectCubePointOntoSphere(Vector3 pointOnCube)
    {
        float lenSq = pointOnCube.LengthSquared();
        if (lenSq < 1e-20f)
        {
            return Vector3.Up * radius;
        }

        return pointOnCube.Normalized() * radius;
    }

    private void addFaceMesh(string name, ArrayMesh mesh)
    {
        var instance = new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = face_material
        };
        AddChild(instance);
        if (Engine.IsEditorHint())
        {
            SceneTree tree = GetTree();
            if (tree != null && tree.EditedSceneRoot != null)
            {
                instance.Owner = tree.EditedSceneRoot;
            }
        }
    }

    /// <summary>
    /// Builds a quad (two triangles). Winding matches <see cref="World"/> / Godot front-face rules.
    /// Vertex normals are radial from the sphere center: <c>position.Normalized()</c>.
    /// </summary>
    private static ArrayMesh buildQuadMesh(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        Vector3 a0 = v0;
        Vector3 b0 = v1;
        Vector3 c0 = v2;
        Vector2 ua0 = new Vector2(0, 1);
        Vector2 ub0 = new Vector2(1, 1);
        Vector2 uc0 = new Vector2(1, 0);
        ensureTriangleWindingOutwardFromOrigin(ref a0, ref b0, ref c0, ref ub0, ref uc0);

        Vector3 a1 = v0;
        Vector3 b1 = v2;
        Vector3 c1 = v3;
        Vector2 ua1 = new Vector2(0, 1);
        Vector2 ub1 = new Vector2(1, 0);
        Vector2 uc1 = new Vector2(0, 0);
        ensureTriangleWindingOutwardFromOrigin(ref a1, ref b1, ref c1, ref ub1, ref uc1);

        var vertices = new Vector3[]
        {
            a0, b0, c0,
            a1, b1, c1
        };

        var normals = new Vector3[]
        {
            normalFromCubeCenter(a0), normalFromCubeCenter(b0), normalFromCubeCenter(c0),
            normalFromCubeCenter(a1), normalFromCubeCenter(b1), normalFromCubeCenter(c1)
        };

        var uvs = new Vector2[]
        {
            ua0, ub0, uc0,
            ua1, ub1, uc1
        };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Unit vector from cube center (local origin) through <paramref name="position"/>.</summary>
    private static Vector3 normalFromCubeCenter(Vector3 position)
    {
        if (position.LengthSquared() < 1e-20f)
        {
            return Vector3.Up;
        }

        return position.Normalized();
    }

    /// <summary>
    /// Same winding rule as <see cref="World.addTriangle"/>: Godot uses clockwise front faces here;
    /// swap <paramref name="b"/>/<paramref name="c"/> (and UVs) when <c>(b−a)×(c−a)</c> aligns with
    /// the centroid direction from the origin so the outside of the sphere stays the visible side.
    /// </summary>
    private static void ensureTriangleWindingOutwardFromOrigin(
        ref Vector3 a,
        ref Vector3 b,
        ref Vector3 c,
        ref Vector2 uvB,
        ref Vector2 uvC)
    {
        Vector3 centroid = (a + b + c) / 3.0f;
        Vector3 geometricNormal = (b - a).Cross(c - a);
        // Same as World.addTriangle: flip when cross normal points outward from the origin.
        if (geometricNormal.Dot(centroid) > 0.0f)
        {
            (b, c) = (c, b);
            (uvB, uvC) = (uvC, uvB);
        }
    }
}
