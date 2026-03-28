using Godot;
using System.Collections.Generic;

/// <summary>
/// One planet shell quad: four corner positions in <see cref="WorldBuilder"/> (planet) space, transform and mesh
/// so the patch sits on the sphere; optional LOD via <see cref="LODTracker"/> splits into four child planes when the
/// camera is closer than <see cref="divide_distance"/>.
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
	private Material planet_material;
	private bool is_configured;
	private double lod_time_accum;

	/// <summary>Call before the node enters the scene tree (e.g. before <see cref="Node.AddChild"/>).</summary>
	public void configure(
		Vector3 corner_p00,
		Vector3 corner_p10,
		Vector3 corner_p11,
		Vector3 corner_p01,
		Material material)
	{
		corner_00 = corner_p00;
		corner_10 = corner_p10;
		corner_11 = corner_p11;
		corner_01 = corner_p01;
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

		addChildPlane(corner_00, e0, c, e3, 0);
		addChildPlane(e0, corner_10, e1, c, 1);
		addChildPlane(c, e1, corner_11, e2, 2);
		addChildPlane(e3, c, e2, corner_01, 3);
	}

	private void addChildPlane(Vector3 c00, Vector3 c10, Vector3 c11, Vector3 c01, int child_index)
	{
		var child = new WorldBuilderPlane();
		child.Name = "Sub_" + child_index;
		child.max_lod_depth = max_lod_depth;
		child.lod_update_interval_seconds = lod_update_interval_seconds;
		child.configure(c00, c10, c11, c01, planet_material);
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

		ArrayMesh face_mesh = buildQuadPatchMeshFromSphereCorners(
			corner_00,
			corner_10,
			corner_11,
			corner_01,
			face_midpoint);

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

	private static ArrayMesh buildQuadPatchMeshFromSphereCorners(
		Vector3 p00,
		Vector3 p10,
		Vector3 p11,
		Vector3 p01,
		Vector3 patch_face_center)
	{
		var vertices = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();

		addTriangleWorld(
			vertices,
			normals,
			uvs,
			patch_face_center,
			p00,
			p10,
			p11,
			new Vector2(0, 0),
			new Vector2(1, 0),
			new Vector2(1, 1));
		addTriangleWorld(
			vertices,
			normals,
			uvs,
			patch_face_center,
			p00,
			p11,
			p01,
			new Vector2(0, 0),
			new Vector2(1, 1),
			new Vector2(0, 1));

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	private static void addTriangleWorld(
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs,
		Vector3 patch_center,
		Vector3 wa,
		Vector3 wb,
		Vector3 wc,
		Vector2 uv_a,
		Vector2 uv_b,
		Vector2 uv_c)
	{
		Vector3 flat_normal = (wb - wa).Cross(wc - wa).Normalized();
		Vector3 tri_center = (wa + wb + wc) / 3.0f;
		if (flat_normal.Dot(tri_center) > 0.0f)
		{
			(wb, wc) = (wc, wb);
			(uv_b, uv_c) = (uv_c, uv_b);
		}

		vertices.Add(wa - patch_center);
		vertices.Add(wb - patch_center);
		vertices.Add(wc - patch_center);
		normals.Add(wa.Normalized());
		normals.Add(wb.Normalized());
		normals.Add(wc.Normalized());
		uvs.Add(uv_a);
		uvs.Add(uv_b);
		uvs.Add(uv_c);
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
