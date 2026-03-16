using Godot;
using Godot.Collections;
using System.Collections.Generic;

[Tool]
public partial class Planet : Node3D
{
    [Export]
    public int num_points = 100;
    [Export]
    public float radius = 10.0f;
    [Export]
    public Array<PlanetLODSettings> lod_settings = new Array<PlanetLODSettings>();
    [Export]
    public bool generate_planet
    {
        get => false;
        set { if (value) regenerate(); }
    }

    [ExportGroup("Terrain noise")]
    [Export] public int terrain_noise_seed = 0;
    [Export] public float terrain_noise_frequency = 1.0f;
    [Export] public float terrain_height_scale = 2.0f;
    [Export] public int terrain_fractal_octaves = 4;
    [Export] public float terrain_fractal_lacunarity = 2.0f;
    [Export] public float terrain_fractal_gain = 0.5f;

    public enum TerrainColorMode { Grid, ColourBlobs }
    [Export] public TerrainColorMode terrain_color_mode = TerrainColorMode.Grid;
    [Export] public float terrain_color_grid_scale = 10.0f;

    private FastNoiseLite terrain_noise;
    private FastNoiseLite color_noise;
    private Vector3[] point_positions;
    private List<(int a, int b, int c)> triangles;

    public override void _Ready()
    {
        regenerate();
    }

    private void regenerate()
    {
        foreach (Node child in GetChildren())
        {
            child.Free();
        }

        point_positions = generatePoints(num_points, radius);
        triangles = computeConvexHull(point_positions);

        foreach (var triangle in triangles)
        {
            PlanetTriangle planetTriangle = new PlanetTriangle();
            planetTriangle.Name = "PT:0:" + triangles.IndexOf(triangle);
            AddChild(planetTriangle);
            planetTriangle.Owner = GetTree().EditedSceneRoot;
            planetTriangle.init(this, null, 0, point_positions[triangle.a], point_positions[triangle.b], point_positions[triangle.c]);
        }
    }

    // Golden-spiral (Fibonacci) distribution: equal-area bands, irrational azimuth step for even spread.
    // Uses (i+0.5)/count so no point sits exactly on the poles (better equidistance).
    private Vector3[] generatePoints(int count, float sphere_radius)
    {
        Vector3[] points = new Vector3[count];
        float golden_ratio = (1.0f + Mathf.Sqrt(5.0f)) / 2.0f;
        float azimuth_step = 2.0f * Mathf.Pi / golden_ratio; // 2π/φ for longitude

        for (int i = 0; i < count; i++)
        {
            float t = ((float)i + 0.5f) / count; // [0.5/N, 1 - 0.5/N] → no pole points
            float inclination = Mathf.Acos(1.0f - 2.0f * t);
            float azimuth = azimuth_step * i;
            points[i] = new Vector3(
                Mathf.Sin(inclination) * Mathf.Cos(azimuth),
                Mathf.Cos(inclination),
                Mathf.Sin(inclination) * Mathf.Sin(azimuth)
            ) * sphere_radius;
        }

        return points;
    }

    // Convex hull does NOT produce equal-size faces: face area depends on which points are
    // on the hull and their spacing. For more uniform triangles, consider an icosahedron + subdivision.
    private List<(int a, int b, int c)> computeConvexHull(Vector3[] pts)
    {
        int n = pts.Length;
        var faces = new List<(int a, int b, int c)>();
        if (n < 4) return faces;

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

    private void addFace(List<(int a, int b, int c)> faces, Vector3[] pts, int a, int b, int c)
    {
        Vector3 face_center = (pts[a] + pts[b] + pts[c]) / 3.0f;
        Vector3 normal = (pts[b] - pts[a]).Cross(pts[c] - pts[a]);
        if (normal.Dot(face_center) > 0)
        {
            faces.Add((a, b, c));
        }
        else
        {
            faces.Add((a, c, b));
        }
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

        if (visible.Count == 0)
        {
            return;
        }

        var horizon = new List<(int a, int b)>();
        foreach (var face in visible)
        {
            (int, int)[] edges = { (face.a, face.b), (face.b, face.c), (face.c, face.a) };
            foreach (var (ea, eb) in edges)
            {
                bool shared = false;
                foreach (var other in visible)
                {
                    if (other == face)
                    {
                        continue;
                    }
                    if ((other.a == eb && other.b == ea) ||
                        (other.b == eb && other.c == ea) ||
                        (other.c == eb && other.a == ea))
                    { shared = true; break; }
                }
                if (!shared)
                {
                    horizon.Add((ea, eb));
                }
            }
        }

        faces.RemoveAll(f => visible.Contains(f));

        foreach (var (ea, eb) in horizon)
        {
            Vector3 face_center = (pts[ea] + pts[eb] + pts[new_idx]) / 3.0f;
            Vector3 normal = (pts[eb] - pts[ea]).Cross(pts[new_idx] - pts[ea]);
            if (normal.Dot(face_center) > 0)
            {
                faces.Add((ea, eb, new_idx));
            }
            else
            {
                faces.Add((eb, ea, new_idx));
            }
        }
    }

    private static float triangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        return 0.5f * (b - a).Cross(c - a).Length();
    }

    /// <summary>
    /// Returns terrain height displacement (in local space) for a point on the planet.
    /// Sampling uses 3D position so the same spot always gets the same value at every LOD.
    /// </summary>
    public float getTerrainDisplacement(Vector3 pos_in_planet_space)
    {
        ensureTerrainNoise();
        Vector3 unit = pos_in_planet_space.Normalized();
        float scale = terrain_noise_frequency;
        float n = terrain_noise.GetNoise3D(unit.X * scale, unit.Y * scale, unit.Z * scale);
        return n * terrain_height_scale;
    }

    /// <summary>
    /// Returns a vertex color for the given sphere position. Grid = grey/white cells; ColourBlobs = noise-driven blobs.
    /// </summary>
    public Color getTerrainColor(Vector3 pos_in_planet_space)
    {
        Vector3 unit = pos_in_planet_space.Normalized();
        if (terrain_color_mode == TerrainColorMode.Grid)
        {
            float s = terrain_color_grid_scale;
            int cell = (int)Mathf.Floor(unit.X * s) + (int)Mathf.Floor(unit.Y * s) + (int)Mathf.Floor(unit.Z * s);
            return (cell % 2 == 0) ? new Color(0.35f, 0.35f, 0.35f) : new Color(0.9f, 0.9f, 0.9f);
        }
        // ColourBlobs: deterministic from position, smooth blob-like regions
        ensureColorNoise();
        float n = color_noise.GetNoise3D(unit.X, unit.Y, unit.Z);
        float t = Mathf.Clamp((n + 1.0f) * 0.5f, 0.0f, 1.0f);
        Color[] palette = {
            new Color(0.9f, 0.35f, 0.3f),
            new Color(0.3f, 0.5f, 0.9f),
            new Color(0.95f, 0.85f, 0.25f),
            new Color(0.4f, 0.75f, 0.4f),
            new Color(0.85f, 0.5f, 0.2f),
            new Color(0.7f, 0.4f, 0.85f)
        };
        int i = (int)Mathf.Clamp(t * palette.Length, 0, palette.Length - 1);
        return palette[i];
    }

    private void ensureColorNoise()
    {
        if (color_noise != null) return;
        color_noise = new FastNoiseLite();
        color_noise.Seed = terrain_noise_seed + 12345;
        color_noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        color_noise.Frequency = 0.8f;
    }

    private void ensureTerrainNoise()
    {
        if (terrain_noise != null) return;
        terrain_noise = new FastNoiseLite();
        terrain_noise.Seed = terrain_noise_seed;
        terrain_noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        terrain_noise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        terrain_noise.Frequency = 1.0f;
        terrain_noise.FractalOctaves = terrain_fractal_octaves;
        terrain_noise.FractalLacunarity = terrain_fractal_lacunarity;
        terrain_noise.FractalGain = terrain_fractal_gain;
    }

}
