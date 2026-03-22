using Godot;
using System.Collections.Generic;
using System.Linq;

public partial class PlanetTriangle : Node3D
{
	public enum PlanetTriangleState
	{
		Uninitialized,
		Mesh,
		Subdivided,
	}
	public PlanetTriangleState state = PlanetTriangleState.Uninitialized;
	public Planet planet;
	public int depth;
	public PlanetTriangle parent;
	public Vector3 point_a;
	public Vector3 point_b;
	public Vector3 point_c;
	public Vector3 center;
	public Vector3 surface_normal;
	public PlanetLODSettings settings;
	public float max_distance;

	public void init(Planet planet, PlanetTriangle parent, int depth, Vector3 a, Vector3 b, Vector3 c)
	{
		this.planet = planet;
		this.parent = parent;
		this.depth = depth;
		point_a = a;
		point_b = b;
		point_c = c;
		center = (point_a + point_b + point_c) / 3.0f;
		// Outward-facing triangle normal (relative to sphere center at origin).
		Vector3 edge_ab = point_b - point_a;
		Vector3 edge_ac = point_c - point_a;
		Vector3 n = edge_ab.Cross(edge_ac).Normalized();
		if (n.Dot(center) < 0.0f)
		{
			n = -n;
		}
		surface_normal = n;
		settings = getLodSettingsForDepth(depth);
		max_distance = Mathf.Max(point_a.DistanceTo(point_b), Mathf.Max(point_b.DistanceTo(point_c), point_c.DistanceTo(point_a))) / 2;
	}

    public override void _PhysicsProcess(double delta)
    {
		base._PhysicsProcess(delta);

		if (Engine.IsEditorHint())
		{
			if (state != PlanetTriangleState.Mesh)
			{
				GD.Print("Editor: Generating init mesh for triangle: ", Name);
				removeChildren();
				generateMesh();
				state = PlanetTriangleState.Mesh;
			}
			return;
		}

		if (state == PlanetTriangleState.Uninitialized)
		{
			GD.Print("Game: Generating init mesh for triangle: ", Name);
			removeChildren();
			generateMesh();
			state = PlanetTriangleState.Mesh;
		}

		// Height of camera above planet surface at this triangle's center, measured radially
		// from the planet center. Zero height ≈ on the surface, increasing as you move away.
		float height = float.MaxValue;
		if (LODTracker.instance != null)
		{
			Vector3 cam_pos = LODTracker.instance.GlobalPosition;
			height = (center + planet.GlobalPosition).DistanceTo(cam_pos);
		}

		Name = "PT:" + depth + ":" + height;

		// LOD based on radial height above this triangle's center:
		// - If camera is higher than settings.distance, collapse to a simple mesh.
		// - If camera is closer than settings.distance and we can still subdivide, refine.
		if (height > settings.distance)
		{
			if (state != PlanetTriangleState.Mesh)
			{
				GD.Print("Game: too far away, generating mesh for triangle: ", Name);
				removeChildren();
				generateMesh();
				state = PlanetTriangleState.Mesh;
			}
		}
		else
		{
			if (depth >= planet.lod_settings.Count - 1)
			{
				if (state != PlanetTriangleState.Mesh)
				{
					GD.Print("Game: too far away, generating mesh for triangle: ", Name);
					removeChildren();
					generateMesh();
					state = PlanetTriangleState.Mesh;
				}
			}
			else
			{
				if (state != PlanetTriangleState.Subdivided)
				{
					GD.Print("Game: too close, generating child triangles for triangle: ", Name);
					removeChildren();
					generateChildPlanetTriangles();
					state = PlanetTriangleState.Subdivided;
				}
			}
		}



		return;

		// if (depth >= planet.lod_settings.Count - 1)
		// {
		// 	if (!HasNode("Mesh"))
		// 	{
		// 		generateMesh();
		// 	}
		// 	return;
		// }
		// return;
		// if (LODTracker.instance == null)
		// {
		// 	return;
		// }

		// float distance = LODTracker.instance.Position.DistanceTo(center);

		// if (Name == "PlanetTriangle_26")
		// {
		// 	GD.Print("Distance: ", distance, " Max Distance: ", max_distance, " Depth: ", depth, " Max Depth: ", planet.lod_settings.Count);
		// }

		// if (distance > (max_distance * 2))
		// {
		// 	if (HasNode("Subdivided"))
		// 	{
		// 		foreach (Node child in GetChildren())
		// 		{
		// 			child.Free();
		// 		}
		// 	}

		// 	if (!HasNode("Mesh"))
		// 	{
		// 		generateMesh();
		// 	}
		// }
		// else
		// {
		// 	if (HasNode("Mesh"))
		// 	{
		// 		foreach (Node child in GetChildren())
		// 		{
		// 			child.Free();
		// 		}
		// 	}
		// 	if (!HasNode("Subdivided"))
		// 	{
		// 		generateChildPlanetTriangles();
		// 	}
		// }

		// // if (LODTracker.instance != null && LODTracker.instance.Position.DistanceTo(center) > (max_distance * 2))
		// // {
		// // 	if (HasNode("Subdivided"))
		// // 	{
		// // 		foreach (Node child in GetChildren())
		// // 		{
		// // 			child.Free();
		// // 		}
		// // 	}
		// // }
		// // 	if (!HasNode("Mesh"))
		// // 	{
		// // 		GD.Print("Generating mesh for triangle: ", Name);
		// // 		generateMesh();
		// // 	}
		// // }

		// // if (LODTracker.instance != null && LODTracker.instance.Position.DistanceTo(center) < (max_distance * 2))
		// // {
		// // 	if (HasNode("Mesh"))
		// // 	{
		// // 		GD.Print("Freeing mesh for triangle: ", Name);
		// // 		foreach (Node child in GetChildren())
		// // 		{
		// // 			child.Free();
		// // 		}
		// // 		QueueFree();
		// // 	}
		// // }
	}

	public void removeChildren()
	{
		foreach (Node child in GetChildren())
		{
			child.Free();
		}
	}

	public void generateChildPlanetTriangles()
	{
		// split the triangle into 3 smaller triangles by using the center point of point_a, point_b, and point_c
		Vector3 center = this.center;

		PlanetTriangle subTriangle1 = new PlanetTriangle();
		AddChild(subTriangle1);
		subTriangle1.Name = Name + "_1";
		subTriangle1.Owner = GetTree().EditedSceneRoot;
		subTriangle1.init(planet, this, depth + 1, point_a, center, point_c);

		PlanetTriangle subTriangle2 = new PlanetTriangle();
		AddChild(subTriangle2);
		subTriangle2.Name = Name + "_2";
		subTriangle2.Owner = GetTree().EditedSceneRoot;
		subTriangle2.init(planet, this, depth + 1, center, point_b, point_a);

		PlanetTriangle subTriangle3 = new PlanetTriangle();
		AddChild(subTriangle3);
		subTriangle3.Name = Name + "_3";
		subTriangle3.Owner = GetTree().EditedSceneRoot;
		subTriangle3.init(planet, this, depth + 1, point_c, center, point_b);
	}

	public void generateMesh()
	{
		StandardMaterial3D shared_material = new StandardMaterial3D
		{
			VertexColorUseAsAlbedo = true,
			Transparency = BaseMaterial3D.TransparencyEnum.Disabled
		};

		Vector3 sa = projectToSphere(point_a);
		Vector3 sb = projectToSphere(point_b);
		Vector3 sc = projectToSphere(point_c);
		Vector3 va = displaceVertex(sa);
		Vector3 vb = displaceVertex(sb);
		Vector3 vc = displaceVertex(sc);

		Godot.Collections.Array base_arrays = buildBaseTriangleArrays(va, vc, vb, sa, sc, sb);

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, base_arrays);
		subdivide(mesh, settings.divisions);

		var instance = new MeshInstance3D { Mesh = mesh, MaterialOverride = shared_material };
		instance.Name = "Mesh";
		AddChild(instance);
		instance.Owner = GetTree().EditedSceneRoot;

		int collision_divisions = Mathf.Max(0, settings.divisions / 2);
		var collision_mesh = new ArrayMesh();
		collision_mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, buildBaseTriangleArrays(va, vc, vb, sa, sc, sb));
		subdivide(collision_mesh, collision_divisions);

		Shape3D terrain_shape = collision_mesh.CreateTrimeshShape();
		if (terrain_shape == null)
		{
			Godot.Collections.Array collision_surface = collision_mesh.SurfaceGetArrays(0);
			var collision_verts = (Vector3[])collision_surface[(int)Mesh.ArrayType.Vertex];
			var concave_fallback = new ConcavePolygonShape3D
			{
				Data = collision_verts,
				BackfaceCollision = true
			};
			terrain_shape = concave_fallback;
		}
		else if (terrain_shape is ConcavePolygonShape3D concave)
		{
			concave.BackfaceCollision = true;
		}

		var static_body = new StaticBody3D { Name = "TerrainCollision" };
		var collision_shape = new CollisionShape3D { Shape = terrain_shape };
		static_body.AddChild(collision_shape);
		AddChild(static_body);
		static_body.Owner = GetTree().EditedSceneRoot;
		collision_shape.Owner = GetTree().EditedSceneRoot;
	}

	private Godot.Collections.Array buildBaseTriangleArrays(
		Vector3 va,
		Vector3 vc,
		Vector3 vb,
		Vector3 sa,
		Vector3 sc,
		Vector3 sb)
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[] { va, vc, vb };
		arrays[(int)Mesh.ArrayType.Normal] = new Vector3[] { va.Normalized(), vc.Normalized(), vb.Normalized() };
		arrays[(int)Mesh.ArrayType.Color] = new Color[] { vertexColor(sa), vertexColor(sc), vertexColor(sb) };
		return arrays;
	}

	private PlanetLODSettings getLodSettingsForDepth(int d)
	{
		if (planet == null || planet.lod_settings == null || planet.lod_settings.Count == 0)
		{
			return new PlanetLODSettings();
		}

		int clamped_depth = Mathf.Clamp(d, 0, planet.lod_settings.Count - 1);

		Variant variant = (Variant)planet.lod_settings[clamped_depth];
		Resource raw = variant.As<Resource>();
		PlanetLODSettings lod = raw as PlanetLODSettings;
		if (lod != null)
		{
			return lod;
		}

		// Fallback: copy exported properties from Resource into a typed instance.
		lod = new PlanetLODSettings();
		if (raw != null)
		{
			var dist_v = raw.Get("distance");
			if (dist_v.VariantType != Variant.Type.Nil)
			{
				lod.distance = dist_v.As<float>();
			}

			var div_v = raw.Get("divisions");
			if (div_v.VariantType != Variant.Type.Nil)
			{
				lod.divisions = div_v.As<int>();
			}
		}

		return lod;
	}

	public void subdivide(ArrayMesh mesh, int division_count)
	{
		// Subdivide all triangles in the mesh `division_count` times using barycentric subdivision.
		// On each pass, replace each triangle with 4 triangles using the midpoints, projected back
		// onto the planet sphere and with a consistent outward-facing winding.
		for (int division = 0; division < division_count; division++)
		{
			GD.Print("Subdividing mesh: ", division, " surfaces: ", mesh.GetSurfaceCount());
			// Extract current surface from mesh (assume one surface)
			if (mesh.GetSurfaceCount() == 0)
				break;

			// Get the current vertices
			var arrays = mesh.SurfaceGetArrays(0);
			var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];

			// Build index list (triangles, 3 verts per triangle)
			List<Vector3> new_verts = new List<Vector3>();
			List<Color> new_colors = new List<Color>();
			for (int j = 0; j < vertices.Length; j += 3)
			{
				// Original triangle vertices, re-projected to the planet radius to avoid drift.
				Vector3 v0 = projectToSphere(vertices[j]);
				Vector3 v1 = projectToSphere(vertices[j + 1]);
				Vector3 v2 = projectToSphere(vertices[j + 2]);

				// Edge midpoints, projected back onto the sphere.
				Vector3 m0 = projectToSphere((v0 + v1) * 0.5f);
				Vector3 m1 = projectToSphere((v1 + v2) * 0.5f);
				Vector3 m2 = projectToSphere((v2 + v0) * 0.5f);

				// Four new triangles with consistent outward winding.
				addTriangleOnSphere(new_verts, new_colors, v0, m0, m2);
				addTriangleOnSphere(new_verts, new_colors, m0, v1, m1);
				addTriangleOnSphere(new_verts, new_colors, m2, m1, v2);
				addTriangleOnSphere(new_verts, new_colors, m0, m1, m2);
			}

			// Remove previous surface
			mesh.ClearSurfaces();

			// Rebuild color and normal arrays
			Color[] colors = new_colors.ToArray();
			Vector3[] normals = new_verts.Select(v => v.Normalized()).ToArray();

			var new_arrays = new Godot.Collections.Array();
			new_arrays.Resize((int)Mesh.ArrayType.Max);
			new_arrays[(int)Mesh.ArrayType.Vertex] = new_verts.ToArray();
			new_arrays[(int)Mesh.ArrayType.Normal] = normals;
			new_arrays[(int)Mesh.ArrayType.Color] = colors;

			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, new_arrays);
		}
	}

	private Vector3 projectToSphere(Vector3 v)
	{
		// Keep everything on the planet surface with a consistent radius.
		return v.Normalized() * planet.radius;
	}

	private void addTriangleOnSphere(List<Vector3> verts, List<Color> colors, Vector3 a, Vector3 b, Vector3 c)
	{
		// Ensure points are on the sphere.
		a = projectToSphere(a);
		b = projectToSphere(b);
		c = projectToSphere(c);

		// Ensure winding is outward-facing. Our convex hull + subdivision currently produces
		// triangles wound the opposite way, so flip when the normal points *outward* relative
		// to the sphere center (origin) to keep the visible side outside.
		Vector3 n = (b - a).Cross(c - a);
		Vector3 tri_center = (a + b + c) / 3.0f;
		if (n.Dot(tri_center) > 0.0f)
		{
			(b, c) = (c, b);
		}

		// Displace by terrain noise and color by height (same position => same color at every LOD).
		verts.Add(displaceVertex(a));
		verts.Add(displaceVertex(b));
		verts.Add(displaceVertex(c));
		colors.Add(vertexColor(a));
		colors.Add(vertexColor(b));
		colors.Add(vertexColor(c));
	}

	/// <summary>
	/// Displace a point on the sphere by planet terrain noise. Same position => same result at any LOD.
	/// </summary>
	private Vector3 displaceVertex(Vector3 sphere_pos)
	{
		if (planet == null) return sphere_pos;
		float h = planet.getTerrainDisplacement(sphere_pos);
		return sphere_pos + sphere_pos.Normalized() * h;
	}

	private Color vertexColor(Vector3 sphere_pos)
	{
		if (planet == null) return new Color(1.0f, 1.0f, 1.0f);
		return planet.getTerrainColor(sphere_pos);
	}
}
