using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// One planet shell quad: four corner positions in <see cref="WorldBuilder"/> (planet) space, transform and mesh
/// so the patch sits on the sphere; optional LOD via <see cref="LODTracker"/> splits into four child planes when the
/// camera is closer than <see cref="divide_distance"/> (doubled on the last quad split tier, <c>depth == max_lod_depth - 1</c>). At <see cref="max_lod_depth"/>, mesh tessellation uses <see cref="WorldBuilder.mesh_subdivisions_final_lod"/>.
/// </summary>
[Tool]
public partial class WorldBuilderPlane : Node3D
{
	public const string mesh_child_name = "Mesh";

	/// <summary>Half of the longest distance between any two of the four corner points.</summary>
	public float divide_distance;

	/// <summary>Set by <see cref="WorldBuilder"/> on root patches (or copied from parent when LOD splits).</summary>
	public int max_lod_depth = 10;

	/// <summary>Set by <see cref="WorldBuilder"/> on root patches (or copied from parent when LOD splits).</summary>
	public double lod_update_interval_seconds = 0.2;

	/// <summary>Set by <see cref="WorldBuilder"/> (or copied from parent patch when LOD splits).</summary>
	public string compute_shader_path = "res://shaders/PerlinHeightmapCompute.glsl";

	/// <summary>If true, run <see cref="compute_shader_path"/> like <see cref="ProceduralPlaneTest"/> and displace the patch mesh radially by the height samples.</summary>
	public bool use_gpu_heightmap = true;

	public float height_scale = 2.0f;

	public float noise_scale = 0.08f;

	public int noise_layers = 5;

	/// <summary>Unused for GPU height on sphere patches (noise samples 3D WorldBuilder-space positions from corners).</summary>
	public float patch_noise_physical_size = 0.0f;

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

	/// <summary>Bumped each GPU heightmap request; stale readbacks are dropped.</summary>
	private int shell_heightmap_request_serial;

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

	public override void _ExitTree()
	{
		findAncestorWorldBuilderForThisPlane()?.unregisterShellHeightmapJobsForPlane(this);
		base._ExitTree();
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
		float subdivide_threshold = divide_distance;
		if (max_lod_depth > 0 && getLodDepth() == max_lod_depth - 1)
		{
			subdivide_threshold *= 4f;
		}

		bool should_subdivide = distance < subdivide_threshold && subdivide_threshold > 1e-5f && getLodDepth() < max_lod_depth;
		bool has_plane_children = hasWorldBuilderPlaneChildren();
		if (has_plane_children)
		{
			freeMeshChild();
		}

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
			findAncestorWorldBuilderForThisPlane()?.unregisterShellHeightmapJobsForPlane(this);
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

	/// <summary>Live distance for <see cref="WorldBuilder"/> queue ordering (closest patches first).</summary>
	internal float getDistanceToLodTrackerForQueueSort()
	{
		if (LODTracker.instance == null)
		{
			return float.MaxValue;
		}

		return getDistanceToLodTracker();
	}

	/// <summary>Quadtree depth for queue tie-break (deeper / finer patches first when distances match).</summary>
	internal int getShellLodDepthForQueueSort()
	{
		return getLodDepth();
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
		child.compute_shader_path = compute_shader_path;
		child.use_gpu_heightmap = use_gpu_heightmap;
		child.height_scale = height_scale;
		child.noise_scale = noise_scale;
		child.noise_layers = noise_layers;
		child.patch_noise_physical_size = patch_noise_physical_size;
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
		if (hasWorldBuilderPlaneChildren())
		{
			freeMeshChild();
			return;
		}

		float max_corner_span = maxDistanceAmongFourCorners(corner_00, corner_10, corner_11, corner_01);
		divide_distance = max_corner_span * 0.5f;

		Vector3 face_midpoint = computeShellPatchFaceCenter(corner_00, corner_10, corner_11, corner_01);
		applyPatchOriginAtCornerCentroid(face_midpoint);

		var vertices = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();

		int n = getEffectiveMeshSubdivisions();
		int res = n + 1;
		float[] heights = null;
		bool gpu_queued = false;
		if (use_gpu_heightmap)
		{
			WorldBuilder wb = findAncestorWorldBuilderForThisPlane();
			if (wb != null)
			{
				shell_heightmap_request_serial++;
				wb.enqueueShellHeightmapJob(new ShellHeightmapPendingJob
				{
					plane = this,
					resolution = res,
					request_serial = shell_heightmap_request_serial,
					shader_path = compute_shader_path,
					noise_scale = noise_scale,
					noise_layers = noise_layers,
					corner_00 = corner_00,
					corner_10 = corner_10,
					corner_11 = corner_11,
					corner_01 = corner_01
				});
				gpu_queued = true;
			}
			else
			{
				heights = runHeightmapComputeSynchronously(res);
			}
		}

		int[] index_array = null;
		if (heights != null && heights.Length == res * res)
		{
			var indices = new List<int>();
			buildTessellatedPatchMeshFromHeights(
				heights,
				res,
				face_midpoint,
				vertices,
				normals,
				uvs,
				indices);
			index_array = indices.ToArray();
		}
		else
		{
			if (use_gpu_heightmap && heights == null && !gpu_queued)
			{
				GD.PushWarning("WorldBuilderPlane: GPU heightmap failed; using flat tessellation.");
			}

			WorldBuilder.appendTessellatedSphericalQuadLocalToPatch(
				corner_00,
				corner_10,
				corner_11,
				corner_01,
				uv_00,
				uv_10,
				uv_11,
				uv_01,
				n,
				face_midpoint,
				vertices,
				normals,
				uvs);
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		if (index_array != null)
		{
			arrays[(int)Mesh.ArrayType.Index] = index_array;
		}

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

	/// <summary>Called from <see cref="WorldBuilder"/> after async GPU readback; ignores stale serials.</summary>
	internal void applyQueuedShellHeightmapReadback(int request_serial, float[] heights, int resolution)
	{
		if (!is_configured || request_serial != shell_heightmap_request_serial)
		{
			return;
		}

		if (heights == null || heights.Length != resolution * resolution)
		{
			return;
		}

		Vector3 face_midpoint = computeShellPatchFaceCenter(corner_00, corner_10, corner_11, corner_01);
		rebuildShellMeshSurfaceFromHeights(heights, resolution, face_midpoint);
	}

	private void rebuildShellMeshSurfaceFromHeights(float[] heights, int res, Vector3 face_midpoint)
	{
		if (hasWorldBuilderPlaneChildren())
		{
			return;
		}

		var vertices = new List<Vector3>();
		var normals = new List<Vector3>();
		var uvs = new List<Vector2>();
		var indices = new List<int>();
		buildTessellatedPatchMeshFromHeights(heights, res, face_midpoint, vertices, normals, uvs, indices);

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

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

	/// <summary>
	/// Places this node’s origin on the four-corner centroid: same as <see cref="computeShellPatchFaceCenter"/>, in parent
	/// local space (no extra subtract of a parent face midpoint).
	/// </summary>
	private void applyPatchOriginAtCornerCentroid(Vector3 face_midpoint_planet_space)
	{
		WorldBuilder wb = findAncestorWorldBuilderForThisPlane();
		if (wb == null)
		{
			Position = face_midpoint_planet_space;
			return;
		}

		Vector3 centroid_world = wb.GlobalTransform * face_midpoint_planet_space;
		if (GetParent() is Node3D parent_nd)
		{
			Position = parent_nd.GlobalTransform.AffineInverse() * centroid_world;
		}
		else
		{
			Position = face_midpoint_planet_space;
		}
	}

	private WorldBuilder findAncestorWorldBuilderForThisPlane()
	{
		for (Node n = GetParent(); n != null; n = n.GetParent())
		{
			if (n is WorldBuilder builder)
			{
				return builder;
			}
		}

		return null;
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

	private const int heightmap_params_byte_count = 6 * 16;

	private static void writeVec4ToByteBuffer(byte[] buf, int offset, Vector4 vec)
	{
		BitConverter.GetBytes(vec.X).CopyTo(buf, offset);
		BitConverter.GetBytes(vec.Y).CopyTo(buf, offset + 4);
		BitConverter.GetBytes(vec.Z).CopyTo(buf, offset + 8);
		BitConverter.GetBytes(vec.W).CopyTo(buf, offset + 12);
	}

	/// <summary>Blocking GPU path when no <see cref="WorldBuilder"/> ancestor exists (e.g. editor) or run synchronously.</summary>
	private float[] runHeightmapComputeSynchronously(int resolution)
	{
		var shader_file = GD.Load<RDShaderFile>(compute_shader_path);
		if (shader_file == null)
		{
			GD.PushError($"WorldBuilderPlane: could not load compute shader at {compute_shader_path}");
			return null;
		}

		RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice();
		if (rd == null)
		{
			GD.PushError("WorldBuilderPlane: CreateLocalRenderingDevice failed (use Forward+ or Mobile renderer).");
			return null;
		}

		RDShaderSpirV spirv = shader_file.GetSpirV();
		Rid shader_rid = rd.ShaderCreateFromSpirV(spirv);
		if (!shader_rid.IsValid)
		{
			GD.PushError("WorldBuilderPlane: ShaderCreateFromSpirV failed.");
			rd.Free();
			return null;
		}

		int height_count = resolution * resolution;
		int height_bytes = height_count * sizeof(float);
		Rid height_buffer_rid = rd.StorageBufferCreate((uint)height_bytes, Array.Empty<byte>());

		var params_bytes = new byte[heightmap_params_byte_count];
		float layers = Mathf.Clamp(noise_layers, 1, 16);
		writeVec4ToByteBuffer(
			params_bytes,
			0,
			new Vector4(resolution, noise_scale, layers, 1.0f));
		writeVec4ToByteBuffer(params_bytes, 16, Vector4.Zero);
		writeVec4ToByteBuffer(params_bytes, 32, new Vector4(corner_00.X, corner_00.Y, corner_00.Z, 0.0f));
		writeVec4ToByteBuffer(params_bytes, 48, new Vector4(corner_10.X, corner_10.Y, corner_10.Z, 0.0f));
		writeVec4ToByteBuffer(params_bytes, 64, new Vector4(corner_11.X, corner_11.Y, corner_11.Z, 0.0f));
		writeVec4ToByteBuffer(params_bytes, 80, new Vector4(corner_01.X, corner_01.Y, corner_01.Z, 0.0f));
		Rid params_buffer_rid = rd.StorageBufferCreate((uint)params_bytes.Length, params_bytes);

		var uniform_params = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 0
		};
		uniform_params.AddId(params_buffer_rid);

		var uniform_heights = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = 1
		};
		uniform_heights.AddId(height_buffer_rid);

		var uniforms = new Godot.Collections.Array<RDUniform> { uniform_params, uniform_heights };
		Rid uniform_set_rid = rd.UniformSetCreate(uniforms, shader_rid, 0);
		if (!uniform_set_rid.IsValid)
		{
			GD.PushError("WorldBuilderPlane: UniformSetCreate failed.");
			freeLocalHeightmapDevice(rd, height_buffer_rid, params_buffer_rid, shader_rid, default, default);
			return null;
		}

		Rid pipeline_rid = rd.ComputePipelineCreate(shader_rid);
		int groups = Mathf.CeilToInt(resolution / 8.0f);
		long cl = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(cl, pipeline_rid);
		rd.ComputeListBindUniformSet(cl, uniform_set_rid, 0);
		rd.ComputeListDispatch(cl, (uint)groups, (uint)groups, 1);
		rd.ComputeListEnd();

		rd.Submit();
		rd.Sync();

		byte[] raw = rd.BufferGetData(height_buffer_rid);
		if (raw == null || raw.Length < height_bytes)
		{
			GD.PushError("WorldBuilderPlane: BufferGetData returned unexpected size.");
			freeLocalHeightmapDevice(rd, height_buffer_rid, params_buffer_rid, shader_rid, pipeline_rid, uniform_set_rid);
			return null;
		}

		var heights = new float[height_count];
		Buffer.BlockCopy(raw, 0, heights, 0, height_bytes);

		freeLocalHeightmapDevice(rd, height_buffer_rid, params_buffer_rid, shader_rid, pipeline_rid, uniform_set_rid);
		return heights;
	}

	private static void freeLocalHeightmapDevice(
		RenderingDevice rd,
		Rid height_buffer_rid,
		Rid params_buffer_rid,
		Rid shader_rid,
		Rid pipeline_rid,
		Rid uniform_set_rid)
	{
		if (uniform_set_rid.IsValid)
		{
			rd.FreeRid(uniform_set_rid);
		}

		if (pipeline_rid.IsValid)
		{
			rd.FreeRid(pipeline_rid);
		}

		if (shader_rid.IsValid)
		{
			rd.FreeRid(shader_rid);
		}

		if (height_buffer_rid.IsValid)
		{
			rd.FreeRid(height_buffer_rid);
		}

		if (params_buffer_rid.IsValid)
		{
			rd.FreeRid(params_buffer_rid);
		}

		rd.Free();
	}

	private void buildTessellatedPatchMeshFromHeights(
		float[] heights,
		int res,
		Vector3 face_midpoint,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs,
		List<int> indices)
	{
		float rf = Mathf.Max(res - 1, 1);
		var world = new Vector3[res, res];

		for (int j = 0; j < res; j++)
		{
			float tv = j / rf;
			for (int i = 0; i < res; i++)
			{
				float tu = i / rf;
				Vector3 base_on_sphere = sampleSphericalQuadWorld(corner_00, corner_10, corner_11, corner_01, tu, tv);
				float h = heights[i + j * res];
				world[i, j] = base_on_sphere + base_on_sphere.Normalized() * (h * height_scale);
			}
		}

		for (int j = 0; j < res; j++)
		{
			for (int i = 0; i < res; i++)
			{
				Vector3 n = computeDisplacedGridNormal(world, i, j, res);
				Vector3 local = world[i, j] - face_midpoint;
				vertices.Add(local);
				normals.Add(n);
				float tu = i / rf;
				float tv = j / rf;
				Vector2 uv = (1.0f - tu) * (1.0f - tv) * uv_00
					+ tu * (1.0f - tv) * uv_10
					+ tu * tv * uv_11
					+ (1.0f - tu) * tv * uv_01;
				uvs.Add(uv);
			}
		}

		int quad_span = res - 1;
		for (int j = 0; j < quad_span; j++)
		{
			for (int i = 0; i < quad_span; i++)
			{
				int i00 = i + j * res;
				int i10 = i00 + 1;
				int i01 = i00 + res;
				int i11 = i01 + 1;
				// Winding must match the non-indexed patch path (Godot front-face culling vs. sphere quads).
				indices.Add(i00);
				indices.Add(i01);
				indices.Add(i10);
				indices.Add(i10);
				indices.Add(i01);
				indices.Add(i11);
			}
		}
	}

	private static Vector3 sampleSphericalQuadWorld(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, float tu, float tv)
	{
		Vector3 blended = (1.0f - tu) * (1.0f - tv) * p00
			+ tu * (1.0f - tv) * p10
			+ tu * tv * p11
			+ (1.0f - tu) * tv * p01;
		float r = (p00.Length() + p10.Length() + p11.Length() + p01.Length()) * 0.25f;
		if (blended == Vector3.Zero)
		{
			return p00;
		}

		return blended.Normalized() * r;
	}

	private static Vector3 computeDisplacedGridNormal(Vector3[,] world, int i, int j, int res)
	{
		if (i > 0 && i < res - 1 && j > 0 && j < res - 1)
		{
			Vector3 du = world[i + 1, j] - world[i - 1, j];
			Vector3 dv = world[i, j + 1] - world[i, j - 1];
			Vector3 n = du.Cross(dv);
			if (n.LengthSquared() > 1e-12f)
			{
				n = n.Normalized();
				if (n.Dot(world[i, j]) < 0.0f)
				{
					n = -n;
				}

				return n;
			}
		}

		return world[i, j].Normalized();
	}
}
