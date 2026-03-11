using Godot;

// A single node in the tri-tree. Not a Godot node — kept as a plain C# class
// so the tree can be manipulated without the overhead of the scene tree.
public class TriNode
{
    public Vector3 va, vb, vc;
    public int depth;
    public TriNode[] children;       // null when this is a leaf
    public MeshInstance3D mesh_instance;

    public Vector3 center   => (va + vb + vc) / 3.0f;
    public bool    is_leaf  => children == null;

    public TriNode(Vector3 a, Vector3 b, Vector3 c, int d)
    {
        va = a; vb = b; vc = c; depth = d;
    }
}

// Manages LOD subdivision for one base triangle on the sphere.
// Each Continent owns one TriTree per hull triangle.
// At runtime, _Process checks camera distance and splits/merges as needed.
[Tool]
public partial class TriTree : Node3D
{
    private TriNode             root;
    private float               sphere_radius;
    private Color               face_color;
    private StandardMaterial3D  shared_material;
    private int                 max_depth;
    private float               lod_distance;

    // Root triangle vertices — stored so getCentroid() works without traversing the tree.
    private Vector3 root_va, root_vb, root_vc;

    public void initialize(
        Vector3 va, Vector3 vb, Vector3 vc,
        float   radius,
        Color   color,
        int     depth_limit,
        float   lod_dist,
        StandardMaterial3D material)
    {
        root_va = va; root_vb = vb; root_vc = vc;
        sphere_radius   = radius;
        face_color      = color;
        max_depth       = depth_limit;
        lod_distance    = lod_dist;
        shared_material = material;

        root = new TriNode(va, vb, vc, 0);
        root.mesh_instance = spawnMesh(va, vb, vc);
    }

    public Vector3 getRootCenter() => (root_va + root_vb + root_vc) / 3.0f;

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() || root == null) return;

        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;

        updateLOD(root, camera.GlobalPosition);
    }

    // -------------------------------------------------------------------------
    // LOD tree traversal
    // -------------------------------------------------------------------------

    private void updateLOD(TriNode node, Vector3 target_pos)
    {
        // Split distance halves at each depth level so deeper triangles only
        // subdivide when the camera is very close.
        float split_dist = sphere_radius * lod_distance / Mathf.Pow(2.0f, node.depth);
        float dist       = target_pos.DistanceTo(node.center);

        if (dist < split_dist && node.depth < max_depth)
        {
            if (node.is_leaf) split(node);
            foreach (var child in node.children)
                updateLOD(child, target_pos);
        }
        else
        {
            if (!node.is_leaf) merge(node);
        }
    }

    private void split(TriNode node)
    {
        if (node.mesh_instance != null)
            node.mesh_instance.Visible = false;

        // Midpoints projected back onto the sphere so subdivision stays spherical
        Vector3 mid_ab = projectToSphere((node.va + node.vb) * 0.5f);
        Vector3 mid_bc = projectToSphere((node.vb + node.vc) * 0.5f);
        Vector3 mid_ca = projectToSphere((node.vc + node.va) * 0.5f);

        // Triforce pattern — 3 corner children + 1 centre child
        node.children = new TriNode[]
        {
            spawnChild(node.va, mid_ab, mid_ca, node.depth + 1),
            spawnChild(mid_ab, node.vb, mid_bc, node.depth + 1),
            spawnChild(mid_ca, mid_bc, node.vc, node.depth + 1),
            spawnChild(mid_ab, mid_bc, mid_ca, node.depth + 1),
        };
    }

    private void merge(TriNode node)
    {
        foreach (var child in node.children)
        {
            if (!child.is_leaf) merge(child);
            child.mesh_instance?.QueueFree();
            child.mesh_instance = null;
        }
        node.children = null;

        if (node.mesh_instance != null)
            node.mesh_instance.Visible = true;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private TriNode spawnChild(Vector3 va, Vector3 vb, Vector3 vc, int depth)
    {
        var node = new TriNode(va, vb, vc, depth);
        node.mesh_instance = spawnMesh(va, vb, vc);
        return node;
    }

    private MeshInstance3D spawnMesh(Vector3 va, Vector3 vb, Vector3 vc)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        // CCW winding (b and c swapped to match the rest of the planet)
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { va, vc, vb };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { va.Normalized(), vc.Normalized(), vb.Normalized() };
        arrays[(int)Mesh.ArrayType.Color]  = new Color[]   { face_color, face_color, face_color };

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = shared_material };
        AddChild(instance);
        return instance;
    }

    private Vector3 projectToSphere(Vector3 v) => v.Normalized() * sphere_radius;
}
