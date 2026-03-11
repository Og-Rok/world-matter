using Godot;
using System.Collections.Generic;

[Tool]
public partial class Planet : Node3D
{
    [Export] public int num_points = 100;
    [Export] public float radius = 10.0f;
    [Export] public int noise_seed = 0;
    [Export] public float noise_frequency = 1.0f;

    // Checking this box in the inspector triggers a regeneration.
    // The getter always returns false so the checkbox resets after firing.
    [Export]
    public bool generate_planet
    {
        get => false;
        set { if (value) regenerate(); }
    }

    private Vector3[] point_positions;

    public override void _Ready()
    {
        regenerate();
    }

    private void regenerate()
    {
        foreach (Node child in GetChildren())
            child.QueueFree();

        point_positions = generatePoints(num_points, radius);
        var triangles = computeConvexHull(point_positions);

        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Seed      = noise_seed,
            Frequency = noise_frequency * 0.1f
        };

        // Pre-compute one colour per vertex so shared vertices stay consistent
        var vertex_colors = new Color[point_positions.Length];
        for (int i = 0; i < point_positions.Length; i++)
        {
            Vector3 unit = point_positions[i].Normalized();
            float n = noise.GetNoise3D(unit.X, unit.Y, unit.Z);
            vertex_colors[i] = terrainColor(n);
        }

        // One shared material for all triangle children
        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled
        };

        foreach (var (a, b, c) in triangles)
        {
            var triangle_node = new MeshInstance3D
            {
                Name = "Face " + a + " " + b + " " + c,
                Mesh = buildTriangleMesh(
                    point_positions[a], point_positions[b], point_positions[c],
                    vertex_colors[a], vertex_colors[b], vertex_colors[c]
                ),
                MaterialOverride = material
            };
            AddChild(triangle_node);
            triangle_node.Owner = GetTree().EditedSceneRoot;
        }
    }

    private Vector3[] generatePoints(int count, float sphere_radius)
    {
        var points = new Vector3[count];
        float golden_angle = Mathf.Pi * 2.0f * ((1.0f + Mathf.Sqrt(5.0f)) / 2.0f);

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / count;
            float inclination = Mathf.Acos(1.0f - 2.0f * t);
            float azimuth = golden_angle * i;
            points[i] = new Vector3(
                Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                Mathf.Cos(inclination),
                Mathf.Sin(inclination) * Mathf.Sin(azimuth)
            ) * sphere_radius;
        }

        return points;
    }

    // Incremental 3D convex hull. Since all points lie on a sphere (a convex surface),
    // the convex hull produces exactly the triangulation we want.
    private List<(int a, int b, int c)> computeConvexHull(Vector3[] pts)
    {
        int n = pts.Length;
        var faces = new List<(int a, int b, int c)>();
        if (n < 4) return faces;

        // Seed the hull with 4 well-separated points to form the initial tetrahedron
        int p0 = 0;

        int p1 = 1;
        float best = 0f;
        for (int i = 1; i < n; i++)
        {
            float d = pts[i].DistanceSquaredTo(pts[p0]);
            if (d > best) { best = d; p1 = i; }
        }

        int p2 = -1; best = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i == p0 || i == p1) continue;
            float d = (pts[p1] - pts[p0]).Cross(pts[i] - pts[p0]).LengthSquared();
            if (d > best) { best = d; p2 = i; }
        }

        int p3 = -1; best = 0f;
        Vector3 plane_n = (pts[p1] - pts[p0]).Cross(pts[p2] - pts[p0]);
        for (int i = 0; i < n; i++)
        {
            if (i == p0 || i == p1 || i == p2) continue;
            float d = Mathf.Abs(plane_n.Dot(pts[i] - pts[p0]));
            if (d > best) { best = d; p3 = i; }
        }

        addFace(faces, pts, p0, p1, p2);
        addFace(faces, pts, p0, p1, p3);
        addFace(faces, pts, p0, p2, p3);
        addFace(faces, pts, p1, p2, p3);

        for (int i = 0; i < n; i++)
        {
            if (i == p0 || i == p1 || i == p2 || i == p3) continue;
            expandHull(faces, pts, i);
        }

        return faces;
    }

    // Adds a triangle with winding order corrected so the normal points away from the
    // sphere center (origin), ensuring consistent outward-facing geometry.
    private void addFace(List<(int a, int b, int c)> faces, Vector3[] pts, int a, int b, int c)
    {
        Vector3 face_center = (pts[a] + pts[b] + pts[c]) / 3.0f;
        Vector3 normal = (pts[b] - pts[a]).Cross(pts[c] - pts[a]);
        if (normal.Dot(face_center) > 0)
            faces.Add((a, b, c));
        else
            faces.Add((a, c, b));
    }

    private void expandHull(List<(int a, int b, int c)> faces, Vector3[] pts, int new_idx)
    {
        var p = pts[new_idx];
        var visible = new HashSet<(int a, int b, int c)>();

        foreach (var face in faces)
        {
            Vector3 fn = (pts[face.b] - pts[face.a]).Cross(pts[face.c] - pts[face.a]);
            if (fn.Dot(p - pts[face.a]) > 1e-7f)
                visible.Add(face);
        }

        if (visible.Count == 0) return;

        // An edge is on the horizon if its reverse does not appear in any other visible face
        var horizon = new List<(int a, int b)>();
        foreach (var face in visible)
        {
            (int, int)[] edges = { (face.a, face.b), (face.b, face.c), (face.c, face.a) };
            foreach (var (ea, eb) in edges)
            {
                bool shared = false;
                foreach (var other in visible)
                {
                    if (other == face) continue;
                    if ((other.a == eb && other.b == ea) ||
                        (other.b == eb && other.c == ea) ||
                        (other.c == eb && other.a == ea))
                    { shared = true; break; }
                }
                if (!shared) horizon.Add((ea, eb));
            }
        }

        faces.RemoveAll(f => visible.Contains(f));

        foreach (var (ea, eb) in horizon)
        {
            Vector3 face_center = (pts[ea] + pts[eb] + pts[new_idx]) / 3.0f;
            Vector3 normal = (pts[eb] - pts[ea]).Cross(pts[new_idx] - pts[ea]);
            if (normal.Dot(face_center) > 0)
                faces.Add((ea, eb, new_idx));
            else
                faces.Add((eb, ea, new_idx));
        }
    }

    private ArrayMesh buildTriangleMesh(Vector3 va, Vector3 vb, Vector3 vc, Color ca, Color cb, Color cc)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { va, vc, vb };
        arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { va.Normalized(), vc.Normalized(), vb.Normalized() };
        arrays[(int)Mesh.ArrayType.Color]  = new Color[]   { ca, cc, cb };

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // Maps a Perlin noise value in [-1, 1] to a terrain colour gradient.
    private Color terrainColor(float n)
    {
        // Normalise to [0, 1] for easier threshold work
        float t = (n + 1.0f) * 0.5f;

        if (t < 0.35f) return new Color(0.05f, 0.18f, 0.55f).Lerp(new Color(0.15f, 0.40f, 0.75f), t / 0.35f);           // deep → shallow ocean
        if (t < 0.42f) return new Color(0.15f, 0.40f, 0.75f).Lerp(new Color(0.82f, 0.75f, 0.50f), (t - 0.35f) / 0.07f); // water → sand
        if (t < 0.58f) return new Color(0.82f, 0.75f, 0.50f).Lerp(new Color(0.24f, 0.55f, 0.18f), (t - 0.42f) / 0.16f); // sand → lowland
        if (t < 0.72f) return new Color(0.24f, 0.55f, 0.18f).Lerp(new Color(0.30f, 0.25f, 0.18f), (t - 0.58f) / 0.14f); // lowland → highland
        if (t < 0.85f) return new Color(0.30f, 0.25f, 0.18f).Lerp(new Color(0.55f, 0.52f, 0.48f), (t - 0.72f) / 0.13f); // highland → rock
        return          new Color(0.55f, 0.52f, 0.48f).Lerp(new Color(1.00f, 1.00f, 1.00f), (t - 0.85f) / 0.15f);        // rock → snow
    }

    public Vector3[] getPoints()
    {
        return point_positions;
    }
}
