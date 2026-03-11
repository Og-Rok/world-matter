using Godot;
using System.Collections.Generic;

[Tool]
public partial class Continent : Node3D
{
    public int   plate_index { get; private set; }
    public Color plate_color { get; private set; }

    private float               sphere_radius;
    private int                 max_lod_depth;
    private float               lod_distance;
    private StandardMaterial3D  shared_material;

    private readonly List<TriTree> tri_trees = new();

    public void initialize(int index, Color color, float radius, int lod_depth, float lod_dist, StandardMaterial3D material)
    {
        plate_index     = index;
        plate_color     = color;
        sphere_radius   = radius;
        max_lod_depth   = lod_depth;
        lod_distance    = lod_dist;
        shared_material = material;
        Name = $"Continent_{index}";
    }

    public void addTriangle(Vector3 va, Vector3 vb, Vector3 vc, Node scene_root)
    {
        var tree = new TriTree();
        AddChild(tree);
        tree.Owner = scene_root;
        tree.initialize(va, vb, vc, sphere_radius, plate_color, max_lod_depth, lod_distance, shared_material);
        tri_trees.Add(tree);
    }

    public IReadOnlyList<TriTree> getTriTrees() => tri_trees;

    // Average of each TriTree's root triangle centre
    public Vector3 getCentroid()
    {
        if (tri_trees.Count == 0) return GlobalPosition;

        var sum = Vector3.Zero;
        foreach (var tree in tri_trees)
            sum += tree.getRootCenter();

        return sum / tri_trees.Count;
    }
}
