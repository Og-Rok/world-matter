using Godot;
using System.Collections.Generic;

/// <summary>
/// Computes cubed-sphere corner positions and spawns a <see cref="WorldBuilderPlane"/> per quad
/// (<c>PlanetShell_0</c> … <c>PlanetShell_23</c>). Each plane builds its own mesh.
/// </summary>
[Tool]
public partial class WorldBuilder : Node3D
{
	private const string legacy_shell_name = "PlanetShell";
	private const string shell_name_prefix = "PlanetShell_";

	[Export]
	public bool generate_planet
	{
		get => false;
		set
		{
			if (value)
			{
				rebuildPlanetShells();
			}
		}
	}

	[Export(PropertyHint.Range, "100,1000,1,or_greater")]
	public float radius = 30.0f;

	[Export] public Material planet_material;

	public override void _Ready()
	{
		rebuildPlanetShells();
	}

	private void rebuildPlanetShells()
	{
		removePreviousPlanetShellChildren();

		var face_normals = new Vector3[]
		{
			Vector3.Right,
			Vector3.Left,
			Vector3.Up,
			Vector3.Down,
			Vector3.Forward,
			Vector3.Back
		};

		int quad_index = 0;
		for (int face_index = 0; face_index < face_normals.Length; face_index++)
		{
			Vector3 face_normal = face_normals[face_index];
			Vector3 axis_u = getFaceAxisU(face_normal);
			Vector3 axis_v = getFaceAxisV(face_normal, axis_u);
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

					Vector3 p00 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v0, radius);
					Vector3 p10 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v0, radius);
					Vector3 p11 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v1, radius);
					Vector3 p01 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v1, radius);

					var plane = new WorldBuilderPlane();
					plane.Name = shell_name_prefix + quad_index;
					plane.configure(p00, p10, p11, p01, planet_material);
					AddChild(plane);
					quad_index++;
				}
			}
		}
	}

	private void removePreviousPlanetShellChildren()
	{
		for (int i = GetChildCount() - 1; i >= 0; i--)
		{
			Node child = GetChild(i);
			string n = child.Name;
			if (n == legacy_shell_name || n.ToString().StartsWith(shell_name_prefix))
			{
				child.Free();
			}
		}
	}

	/// <summary>Same 24-patch layout as <see cref="World"/> uses for base terrain quads (single combined mesh).</summary>
	/// <param name="mesh_center_local">
	/// Centroid of the full mesh before re-centering; use as a parent offset if you merge instances yourself.
	/// </param>
	public static ArrayMesh buildCubedSphereMesh(float sphere_radius, out Vector3 mesh_center_local)
	{
		mesh_center_local = Vector3.Zero;
		var face_normals = new Vector3[]
		{
			Vector3.Right,
			Vector3.Left,
			Vector3.Up,
			Vector3.Down,
			Vector3.Forward,
			Vector3.Back
		};

		var vertices = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();

		for (int face_index = 0; face_index < face_normals.Length; face_index++)
		{
			Vector3 face_normal = face_normals[face_index];
			Vector3 axis_u = getFaceAxisU(face_normal);
			Vector3 axis_v = getFaceAxisV(face_normal, axis_u);
			appendFaceQuads(face_normal, axis_u, axis_v, sphere_radius, vertices, normals, uvs);
		}

		int vertex_count = vertices.Count;
		if (vertex_count > 0)
		{
			Vector3 mesh_center = Vector3.Zero;
			for (int i = 0; i < vertex_count; i++)
			{
				mesh_center += vertices[i];
			}

			mesh_center /= vertex_count;
			mesh_center_local = mesh_center;
			for (int i = 0; i < vertex_count; i++)
			{
				Vector3 v = vertices[i] - mesh_center;
				vertices[i] = v;
				normals[i] = (v + mesh_center).Normalized();
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/// <summary>Build mesh with vertices centered on the mesh instance origin; centroid is discarded.</summary>
	public static ArrayMesh buildCubedSphereMesh(float sphere_radius)
	{
		return buildCubedSphereMesh(sphere_radius, out _);
	}

	private static void appendFaceQuads(
		Vector3 face_normal,
		Vector3 axis_u,
		Vector3 axis_v,
		float sphere_radius,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs)
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

				Vector3 p00 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v0, sphere_radius);
				Vector3 p10 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v0, sphere_radius);
				Vector3 p11 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v1, sphere_radius);
				Vector3 p01 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v1, sphere_radius);

				addTriangle(vertices, normals, uvs, p00, p10, p11, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1));
				addTriangle(vertices, normals, uvs, p00, p11, p01, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1));
			}
		}
	}

	private static Vector3 cubeToSphere(Vector3 point_on_cube, float sphere_radius)
	{
		return point_on_cube.Normalized() * sphere_radius;
	}

	private static Vector3 getFaceAxisU(Vector3 face_normal)
	{
		if (Mathf.Abs(face_normal.Y) > 0.5f)
		{
			return Vector3.Right;
		}

		return Vector3.Up;
	}

	private static Vector3 getFaceAxisV(Vector3 face_normal, Vector3 axis_u)
	{
		return face_normal.Cross(axis_u).Normalized();
	}

	private static void addTriangle(
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
		Vector3 flat_normal = (b - a).Cross(c - a).Normalized();
		Vector3 center = (a + b + c) / 3.0f;
		if (flat_normal.Dot(center) > 0.0f)
		{
			(b, c) = (c, b);
			(uv_b, uv_c) = (uv_c, uv_b);
		}

		Vector3 n = center.Normalized();
		vertices.Add(a);
		vertices.Add(b);
		vertices.Add(c);
		normals.Add(n);
		normals.Add(n);
		normals.Add(n);
		uvs.Add(uv_a);
		uvs.Add(uv_b);
		uvs.Add(uv_c);
	}
}
