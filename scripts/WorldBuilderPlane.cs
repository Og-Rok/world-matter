using Godot;
using System.Collections.Generic;

/// <summary>
/// One planet shell quad: four corner positions in <see cref="WorldBuilder"/> (planet) space, transform and mesh
/// so the patch sits on the sphere; optional LOD via <see cref="LODTracker"/> splits into four child planes when the
/// camera is closer than <see cref="divide_distance"/>. At <see cref="max_lod_depth"/>, mesh tessellation uses the final LOD count from <see cref="WorldBuilder.mesh_subdivisions_final_lod"/>.
/// </summary>
[Tool]
public partial class WorldBuilderPlane : Node3D
{
	public const string mesh_child_name = "Mesh";

	/// <summary>Half of the longest distance between any two of the four corner points.</summary>
	public float divide_distance;

	[Export(PropertyHint.Range, "0,32,1")]
	public int max_lod_depth = 10;

	[Export(PropertyHint.Range, "0,2,0.01,or_greater")]
	public double lod_update_interval_seconds = 0.2;

	private Vector3 corner_00;
	private Vector3 corner_10;
	private Vector3 corner_11;
	private Vector3 corner_01;
	private Vector2 uv_00;
	private Vector2 uv_10;
	private Vector2 uv_11;
	private Vector2 uv_01;
	private int mesh_subdivisions = 1;
	private int mesh_subdivisions_final = 8;
	private Material planet_material;
	private bool is_configured;
	private double lod_time_accum;

	/// <summary>Call before the node enters the scene tree. UVs default to the full unit quad; final LOD tessellation defaults to 8.</summary>
	public void configure(
		Vector3 corner_p00,
		Vector3 corner_p10,
		Vector3 corner_p11,
		Vector3 corner_p01,
		Material material,
		int patch_mesh_subdivisions = 1,
		int patch_mesh_subdivisions_final = 8)
	{
		configure(
			corner_p00,
			corner_p10,
			corner_p11,
			corner_p01,
			material,
			new Vector2(0, 0),
			new Vector2(1, 0),
			new Vector2(1, 1),
			new Vector2(0, 1),
			patch_mesh_subdivisions,
			patch_mesh_subdivisions_final);
	}

	/// <param name="uv_corner_p00">UV at <paramref name="corner_p00"/> (matches cubed-sphere face layout from <see cref="WorldBuilder"/>).</param>
	public void configure(
		Vector3 corner_p00,
		Vector3 corner_p10,
		Vector3 corner_p11,
		Vector3 corner_p01,
		Material material,
		Vector2 uv_corner_p00,
		Vector2 uv_corner_p10,
		Vector2 uv_corner_p11,
		Vector2 uv_corner_p01,
		int patch_mesh_subdivisions,
		int patch_mesh_subdivisions_final)
	{
		corner_00 = corner_p00;
		corner_10 = corner_p10;
		corner_11 = corner_p11;
		corner_01 = corner_p01;
		uv_00 = uv_corner_p00;
		uv_10 = uv_corner_p10;
		uv_11 = uv_corner_p11;
		uv_01 = uv_corner_p01;
		mesh_subdivisions = Mathf.Max(1, patch_mesh_subdivisions);
		mesh_subdivisions_final = Mathf.Max(1, patch_mesh_subdivisions_final);
		planet_material = material;
		is_configured = true;
	}

	/// <summary>Face midpoint in planet / <see cref="WorldBuilder"/> local space (same space as configure corners).</summary>
	public Vector3 getFaceMidpointInPlanetSpace()
	{
		return computeShellPatchFaceCenter(corner_00, corner_10, corner_11, corner_01);
	}

	public override void _Ready()
	{
		if (!is_configured)
		{
			return;
		}

		if (!hasWorldBuilderPlaneChildren())
		{
			rebuildShellMesh();
		}

		assignEditorOwnersForShell();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!is_configured || Engine.IsEditorHint())
		{
			return;
		}

		if (LODTracker.instance == null)
		{
			return;
		}

		if (lod_update_interval_seconds > 0.0)
		{
			lod_time_accum += delta;
			if (lod_time_accum < lod_update_interval_seconds)
			{
				return;
			}

			lod_time_accum = 0.0;
		}

		float distance = getDistanceToLodTracker();
		bool should_subdivide = distance < divide_distance && divide_distance > 1e-5f && getLodDepth() < max_lod_depth;
		bool has_plane_children = hasWorldBuilderPlaneChildren();

		if (should_subdivide && has_plane_children)
		{
			return;
		}

		if (!should_subdivide && !has_plane_children)
		{
			return;
		}

		if (should_subdivide)
		{
			freeMeshChild();
			if (!hasWorldBuilderPlaneChildren())
			{
				subdivideIntoFourChildren();
			}

			return;
		}

		collapseChildPlanes();
	}

	private int getLodDepth()
	{
		int depth = 0;
		for (Node n = GetParent(); n != null; n = n.GetParent())
		{
			if (n is WorldBuilderPlane)
			{
				depth++;
			}
		}

		return depth;
	}

	/// <summary>Deepest quadtree leaves (<c>depth == max_lod_depth</c>) use <see cref="mesh_subdivisions_final"/>; others use <see cref="mesh_subdivisions"/>.</summary>
	private int getEffectiveMeshSubdivisions()
	{
		if (getLodDepth() >= max_lod_depth)
		{
			return mesh_subdivisions_final;
		}

		return mesh_subdivisions;
	}

	private float getDistanceToLodTracker()
	{
		Vector3 cam = LODTracker.instance.GlobalPosition;
		getWorldCornersForDistance(out Vector3 w00, out Vector3 w10, out Vector3 w11, out Vector3 w01);
		return WorldPatchGeometry.distancePointToPatchQuad(cam, w00, w10, w11, w01);
	}

	private void getWorldCornersForDistance(out Vector3 w00, out Vector3 w10, out Vector3 w11, out Vector3 w01)
	{
		Vector3 face_mid = computeShellPatchFaceCenter(corner_00, corner_10, corner_11, corner_01);
		w00 = GlobalTransform * (corner_00 - face_mid);
		w10 = GlobalTransform * (corner_10 - face_mid);
		w11 = GlobalTransform * (corner_11 - face_mid);
		w01 = GlobalTransform * (corner_01 - face_mid);
	}

	private bool hasWorldBuilderPlaneChildren()
	{
		foreach (Node child in GetChildren())
		{
			if (child is WorldBuilderPlane)
			{
				return true;
			}
		}

		return false;
	}

	private void freeMeshChild()
	{
		MeshInstance3D mesh_instance = GetNodeOrNull<MeshInstance3D>(mesh_child_name);
		if (mesh_instance != null && IsInstanceValid(mesh_instance))
		{
			mesh_instance.QueueFree();
		}
	}

	private void subdivideIntoFourChildren()
	{
		Vector3 e0 = midpointOnSphere(corner_00, corner_10);
		Vector3 e1 = midpointOnSphere(corner_10, corner_11);
		Vector3 e2 = midpointOnSphere(corner_11, corner_01);
		Vector3 e3 = midpointOnSphere(corner_01, corner_00);
		Vector3 c = midpointOnSphere(
			midpointOnSphere(corner_00, corner_11),
			midpointOnSphere(corner_10, corner_01));

		Vector2 ue0 = (uv_00 + uv_10) * 0.5f;
		Vector2 ue1 = (uv_10 + uv_11) * 0.5f;
		Vector2 ue2 = (uv_11 + uv_01) * 0.5f;
		Vector2 ue3 = (uv_01 + uv_00) * 0.5f;
		Vector2 uc = (uv_00 + uv_10 + uv_11 + uv_01) * 0.25f;

		addChildPlane(corner_00, e0, c, e3, uv_00, ue0, uc, ue3, 0);
		addChildPlane(e0, corner_10, e1, c, ue0, uv_10, ue1, uc, 1);
		addChildPlane(c, e1, corner_11, e2, uc, ue1, uv_11, ue2, 2);
		addChildPlane(e3, c, e2, corner_01, ue3, uc, ue2, uv_01, 3);
	}

	private void addChildPlane(
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
		var child = new WorldBuilderPlane();
		child.Name = "Sub_" + child_index;
		child.max_lod_depth = max_lod_depth;
		child.lod_update_interval_seconds = lod_update_interval_seconds;
		child.configure(c00, c10, c11, c01, planet_material, u00, u10, u11, u01, mesh_subdivisions, mesh_subdivisions_final);
		AddChild(child);
		assignOwnerForEditorSave(child);
	}

	private void collapseChildPlanes()
	{
		foreach (Node child in GetChildren())
		{
			if (child is WorldBuilderPlane plane_child)
			{
				plane_child.Free();
			}
		}

		rebuildShellMesh();
		assignEditorOwnersForShell();
	}

	private void assignOwnerForEditorSave(Node node)
	{
		if (!Engine.IsEditorHint())
		{
			return;
		}

		SceneTree tree = GetTree();
		if (tree == null || tree.EditedSceneRoot == null)
		{
			return;
		}

		node.Owner = tree.EditedSceneRoot;
	}

	private void rebuildShellMesh()
	{
		float max_corner_span = maxDistanceAmongFourCorners(corner_00, corner_10, corner_11, corner_01);
		divide_distance = max_corner_span * 0.5f;

		Vector3 face_midpoint = computeShellPatchFaceCenter(corner_00, corner_10, corner_11, corner_01);
		Vector3 parent_patch_mid = Vector3.Zero;
		if (GetParent() is WorldBuilderPlane parent_plane)
		{
			parent_patch_mid = parent_plane.getFaceMidpointInPlanetSpace();
		}

		Position = face_midpoint - parent_patch_mid;

		var vertices = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();
		WorldBuilder.appendTessellatedSphericalQuadLocalToPatch(
			corner_00,
			corner_10,
			corner_11,
			corner_01,
			uv_00,
			uv_10,
			uv_11,
			uv_01,
			getEffectiveMeshSubdivisions(),
			face_midpoint,
			vertices,
			normals,
			uvs);

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

		var face_mesh = new ArrayMesh();
		face_mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

		MeshInstance3D mesh_instance = GetNodeOrNull<MeshInstance3D>(mesh_child_name);
		if (mesh_instance == null)
		{
			mesh_instance = new MeshInstance3D { Name = mesh_child_name };
			AddChild(mesh_instance);
		}

		mesh_instance.Mesh = face_mesh;
		applyMaterialToMesh(mesh_instance);
	}

	private void applyMaterialToMesh(MeshInstance3D mesh_instance)
	{
		if (planet_material != null)
		{
			mesh_instance.MaterialOverride = planet_material;
		}
		else
		{
			mesh_instance.MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.35f, 0.52f, 0.85f)
			};
		}
	}

	private void assignEditorOwnersForShell()
	{
		if (!Engine.IsEditorHint())
		{
			return;
		}

		SceneTree tree = GetTree();
		if (tree == null || tree.EditedSceneRoot == null)
		{
			return;
		}

		Node root = tree.EditedSceneRoot;
		Owner = root;
		MeshInstance3D mesh_instance = GetNodeOrNull<MeshInstance3D>(mesh_child_name);
		if (mesh_instance != null)
		{
			mesh_instance.Owner = root;
		}
	}

	private static Vector3 computeShellPatchFaceCenter(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01)
	{
		return (p00 + p10 + p11 + p01) * 0.25f;
	}

	private static float maxDistanceAmongFourCorners(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01)
	{
		float max_sq = 0f;
		considerPair(ref max_sq, p00, p10);
		considerPair(ref max_sq, p00, p11);
		considerPair(ref max_sq, p00, p01);
		considerPair(ref max_sq, p10, p11);
		considerPair(ref max_sq, p10, p01);
		considerPair(ref max_sq, p11, p01);
		return Mathf.Sqrt(max_sq);
	}

	private static void considerPair(ref float max_sq, Vector3 a, Vector3 b)
	{
		float d_sq = a.DistanceSquaredTo(b);
		if (d_sq > max_sq)
		{
			max_sq = d_sq;
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
}
