using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Cubed-sphere quadtree roots: <c>6 × 2 × 2 = 24</c> <see cref="WorldBuilderPlane"/> patches (four children per patch when LOD splits).
/// <see cref="mesh_subdivisions"/> refines mesh density on non-deepest patches; <see cref="mesh_subdivisions_final_lod"/> applies at <see cref="shell_max_lod_depth"/>.
/// GPU heightmap, noise, and LOD (<see cref="shell_max_lod_depth"/>, <see cref="compute_shader_path"/>, etc.) are configured on this node.
/// Shell heightmap compute is queued here: one dispatch in flight, readback after <see cref="shell_gpu_heightmap_wait_frames_before_readback"/> frames (no main-thread sync in patch rebuild).
/// </summary>
[Tool]
public partial class WorldBuilder : Node3D
{
	private const string legacy_shell_name = "PlanetShell";
	private const string shell_name_prefix = "PlanetShell_";

	private const int quads_per_face_axis = 2;

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

	[Export(PropertyHint.Range, "0.01,100000,0.01,or_greater")]
	public float radius = 30.0f;

	/// <summary>Mesh grid cells per axis on each patch quad (minimum 1). LOD still splits the quadtree by four children per level.</summary>
	[Export(PropertyHint.Range, "1,64,1")]
	public int mesh_subdivisions = 1;

	/// <summary>Mesh tessellation for patches at <see cref="shell_max_lod_depth"/> (deepest leaves only).</summary>
	[Export(PropertyHint.Range, "1,128,1")]
	public int mesh_subdivisions_final_lod = 8;

	[ExportGroup("LOD (patches)")]
	[Export(PropertyHint.Range, "0,32,1")]
	public int shell_max_lod_depth = 10;

	[Export(PropertyHint.Range, "0,2,0.01,or_greater")]
	public double shell_lod_update_interval_seconds = 0.2;

	[Export] public Material planet_material;

	[ExportGroup("GPU heightmap (patches)")]
	[Export] public string compute_shader_path = "res://shaders/PerlinHeightmapCompute.glsl";

	[Export] public bool use_gpu_heightmap = true;

	[Export(PropertyHint.Range, "0,100,0.01,or_greater")]
	public float shell_height_scale = 2.0f;

	[Export] public float shell_noise_scale = 0.08f;

	[Export(PropertyHint.Range, "1,16,1")]
	public int shell_noise_layers = 5;

	/// <summary>Reserved; shell GPU noise uses 3D positions in WorldBuilder space (patch corners), not XZ extent.</summary>
	[Export(PropertyHint.Range, "0,100000,0.01,or_greater")]
	public float shell_patch_noise_physical_size = 0.0f;

	/// <summary>Frames to wait after GPU submit before <c>Sync</c> + readback (spreads work across frames).</summary>
	[Export(PropertyHint.Range, "1,32,1")]
	public int shell_gpu_heightmap_wait_frames_before_readback = 2;

	private const int heightmap_params_byte_count = 6 * 16;

	private readonly List<ShellHeightmapPendingJob> shell_heightmap_pending = new();
	private ShellHeightmapInFlightJob shell_heightmap_in_flight;

	public override void _Ready()
	{
		rebuildPlanetShells();
	}

	public override void _Process(double delta)
	{
		processShellHeightmapQueue();
	}

	private sealed class ShellHeightmapInFlightJob
	{
		public WorldBuilderPlane plane;
		public int resolution;
		public int request_serial;
		public RenderingDevice rd;
		public Rid height_buffer_rid;
		public Rid params_buffer_rid;
		public Rid shader_rid;
		public Rid pipeline_rid;
		public Rid uniform_set_rid;
		public int height_bytes;
		public int frames_waited;
	}

	internal void enqueueShellHeightmapJob(ShellHeightmapPendingJob job)
	{
		if (job.plane == null || !GodotObject.IsInstanceValid(job.plane))
		{
			return;
		}

		shell_heightmap_pending.RemoveAll(j => ReferenceEquals(j.plane, job.plane));
		shell_heightmap_pending.Add(job);
	}

	private static int compareShellHeightmapPendingJobs(ShellHeightmapPendingJob a, ShellHeightmapPendingJob b)
	{
		float da = getShellHeightmapPendingSortDistance(a);
		float db = getShellHeightmapPendingSortDistance(b);
		int by_distance = da.CompareTo(db);
		if (by_distance != 0)
		{
			return by_distance;
		}

		return getShellHeightmapPendingSortLodDepth(b).CompareTo(getShellHeightmapPendingSortLodDepth(a));
	}

	private static float getShellHeightmapPendingSortDistance(ShellHeightmapPendingJob j)
	{
		if (j.plane == null || !GodotObject.IsInstanceValid(j.plane))
		{
			return float.MaxValue;
		}

		return j.plane.getDistanceToLodTrackerForQueueSort();
	}

	private static int getShellHeightmapPendingSortLodDepth(ShellHeightmapPendingJob j)
	{
		if (j.plane == null || !GodotObject.IsInstanceValid(j.plane))
		{
			return -1;
		}

		return j.plane.getShellLodDepthForQueueSort();
	}

	internal void unregisterShellHeightmapJobsForPlane(WorldBuilderPlane plane)
	{
		shell_heightmap_pending.RemoveAll(j => ReferenceEquals(j.plane, plane));
		if (shell_heightmap_in_flight != null && ReferenceEquals(shell_heightmap_in_flight.plane, plane))
		{
			shell_heightmap_in_flight.plane = null;
		}
	}

	private void processShellHeightmapQueue()
	{
		if (shell_heightmap_in_flight != null)
		{
			shell_heightmap_in_flight.frames_waited++;
			int wait_need = Mathf.Max(1, shell_gpu_heightmap_wait_frames_before_readback);
			if (shell_heightmap_in_flight.frames_waited < wait_need)
			{
				return;
			}

			completeShellHeightmapInFlight();
		}

		if (shell_heightmap_pending.Count > 0)
		{
			shell_heightmap_pending.Sort(compareShellHeightmapPendingJobs);
		}

		while (shell_heightmap_pending.Count > 0)
		{
			ShellHeightmapPendingJob next = shell_heightmap_pending[0];
			shell_heightmap_pending.RemoveAt(0);
			if (next.plane == null || !GodotObject.IsInstanceValid(next.plane))
			{
				continue;
			}

			shell_heightmap_in_flight = tryBeginShellHeightmapJob(next);
			if (shell_heightmap_in_flight != null)
			{
				break;
			}
		}
	}

	private ShellHeightmapInFlightJob tryBeginShellHeightmapJob(ShellHeightmapPendingJob job)
	{
		var shader_file = GD.Load<RDShaderFile>(job.shader_path);
		if (shader_file == null)
		{
			GD.PushError($"WorldBuilder: could not load compute shader at {job.shader_path}");
			return null;
		}

		var rd = RenderingServer.CreateLocalRenderingDevice();
		if (rd == null)
		{
			GD.PushError("WorldBuilder: CreateLocalRenderingDevice failed (use Forward+ or Mobile renderer).");
			return null;
		}

		RDShaderSpirV spirv = shader_file.GetSpirV();
		Rid shader_rid = rd.ShaderCreateFromSpirV(spirv);
		if (!shader_rid.IsValid)
		{
			GD.PushError("WorldBuilder: ShaderCreateFromSpirV failed.");
			rd.Free();
			return null;
		}

		int resolution = job.resolution;
		int height_count = resolution * resolution;
		int height_bytes = height_count * sizeof(float);
		Rid height_buffer_rid = rd.StorageBufferCreate((uint)height_bytes, Array.Empty<byte>());

		var params_bytes = new byte[heightmap_params_byte_count];
		float layers = Mathf.Clamp(job.noise_layers, 1, 16);
		writeVec4ToHeightmapParams(params_bytes, 0, new Vector4(resolution, job.noise_scale, layers, 1.0f));
		writeVec4ToHeightmapParams(params_bytes, 16, Vector4.Zero);
		writeVec4ToHeightmapParams(params_bytes, 32, new Vector4(job.corner_00.X, job.corner_00.Y, job.corner_00.Z, 0.0f));
		writeVec4ToHeightmapParams(params_bytes, 48, new Vector4(job.corner_10.X, job.corner_10.Y, job.corner_10.Z, 0.0f));
		writeVec4ToHeightmapParams(params_bytes, 64, new Vector4(job.corner_11.X, job.corner_11.Y, job.corner_11.Z, 0.0f));
		writeVec4ToHeightmapParams(params_bytes, 80, new Vector4(job.corner_01.X, job.corner_01.Y, job.corner_01.Z, 0.0f));
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
			GD.PushError("WorldBuilder: UniformSetCreate failed.");
			freeHeightmapRenderingDevice(rd, height_buffer_rid, params_buffer_rid, shader_rid, default, default);
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

		return new ShellHeightmapInFlightJob
		{
			plane = job.plane,
			resolution = resolution,
			request_serial = job.request_serial,
			rd = rd,
			height_buffer_rid = height_buffer_rid,
			params_buffer_rid = params_buffer_rid,
			shader_rid = shader_rid,
			pipeline_rid = pipeline_rid,
			uniform_set_rid = uniform_set_rid,
			height_bytes = height_bytes,
			frames_waited = 0
		};
	}

	private void completeShellHeightmapInFlight()
	{
		ShellHeightmapInFlightJob job = shell_heightmap_in_flight;
		shell_heightmap_in_flight = null;
		if (job == null || job.rd == null)
		{
			return;
		}

		job.rd.Sync();

		byte[] raw = job.rd.BufferGetData(job.height_buffer_rid);
		WorldBuilderPlane plane = job.plane;
		bool plane_ok = plane != null && GodotObject.IsInstanceValid(plane);

		freeHeightmapRenderingDevice(
			job.rd,
			job.height_buffer_rid,
			job.params_buffer_rid,
			job.shader_rid,
			job.pipeline_rid,
			job.uniform_set_rid);

		if (!plane_ok)
		{
			return;
		}

		if (raw == null || raw.Length < job.height_bytes)
		{
			GD.PushError("WorldBuilder: heightmap BufferGetData returned unexpected size.");
			return;
		}

		var heights = new float[job.resolution * job.resolution];
		Buffer.BlockCopy(raw, 0, heights, 0, job.height_bytes);
		plane.applyQueuedShellHeightmapReadback(job.request_serial, heights, job.resolution);
	}

	private static void freeHeightmapRenderingDevice(
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

	private static void writeVec4ToHeightmapParams(byte[] buf, int offset, Vector4 vec)
	{
		BitConverter.GetBytes(vec.X).CopyTo(buf, offset);
		BitConverter.GetBytes(vec.Y).CopyTo(buf, offset + 4);
		BitConverter.GetBytes(vec.Z).CopyTo(buf, offset + 8);
		BitConverter.GetBytes(vec.W).CopyTo(buf, offset + 12);
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

		int cells_per_axis = quads_per_face_axis;
		float step = 2.0f / cells_per_axis;
		float inv_cells = 1.0f / cells_per_axis;
		int patch_mesh = Mathf.Max(1, mesh_subdivisions);
		int patch_mesh_final = Mathf.Max(1, mesh_subdivisions_final_lod);

		int quad_index = 0;
		for (int face_index = 0; face_index < face_normals.Length; face_index++)
		{
			Vector3 face_normal = face_normals[face_index];
			Vector3 axis_u = getFaceAxisU(face_normal);
			Vector3 axis_v = getFaceAxisV(face_normal, axis_u);

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
					plane.configure(
						p00,
						p10,
						p11,
						p01,
						planet_material,
						new Vector2(x * inv_cells, y * inv_cells),
						new Vector2((x + 1) * inv_cells, y * inv_cells),
						new Vector2((x + 1) * inv_cells, (y + 1) * inv_cells),
						new Vector2(x * inv_cells, (y + 1) * inv_cells),
						patch_mesh,
						patch_mesh_final);
					applyShellParametersToPlane(plane);
					AddChild(plane);
					quad_index++;
				}
			}
		}
	}

	private void applyShellParametersToPlane(WorldBuilderPlane plane)
	{
		plane.max_lod_depth = shell_max_lod_depth;
		plane.lod_update_interval_seconds = shell_lod_update_interval_seconds;
		plane.compute_shader_path = compute_shader_path;
		plane.use_gpu_heightmap = use_gpu_heightmap;
		plane.height_scale = shell_height_scale;
		plane.noise_scale = shell_noise_scale;
		plane.noise_layers = shell_noise_layers;
		plane.patch_noise_physical_size = shell_patch_noise_physical_size;
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

	/// <summary>Combined cubed-sphere mesh: 24 coarse quads, each tessellated <paramref name="mesh_subdivisions"/>× per axis.</summary>
	public static ArrayMesh buildCubedSphereMesh(float sphere_radius, int mesh_subdivisions, out Vector3 mesh_center_local)
	{
		mesh_center_local = Vector3.Zero;
		int tess = Mathf.Max(1, mesh_subdivisions);
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
			appendFaceQuadsWithMeshTessellation(
				face_normal,
				axis_u,
				axis_v,
				sphere_radius,
				quads_per_face_axis,
				tess,
				vertices,
				normals,
				uvs);
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

	public static ArrayMesh buildCubedSphereMesh(float sphere_radius, int mesh_subdivisions)
	{
		return buildCubedSphereMesh(sphere_radius, mesh_subdivisions, out _);
	}

	public static ArrayMesh buildCubedSphereMesh(float sphere_radius)
	{
		return buildCubedSphereMesh(sphere_radius, 1, out _);
	}

	public static ArrayMesh buildCubedSphereMesh(float sphere_radius, out Vector3 mesh_center_local)
	{
		return buildCubedSphereMesh(sphere_radius, 1, out mesh_center_local);
	}

	private static void appendFaceQuadsWithMeshTessellation(
		Vector3 face_normal,
		Vector3 axis_u,
		Vector3 axis_v,
		float sphere_radius,
		int coarse_cells_per_axis,
		int mesh_subdivisions,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs)
	{
		float step = 2.0f / coarse_cells_per_axis;
		float inv_cells = 1.0f / coarse_cells_per_axis;

		for (int y = 0; y < coarse_cells_per_axis; y++)
		{
			for (int x = 0; x < coarse_cells_per_axis; x++)
			{
				float u0 = -1.0f + (x * step);
				float u1 = -1.0f + ((x + 1) * step);
				float v0 = -1.0f + (y * step);
				float v1 = -1.0f + ((y + 1) * step);

				Vector3 p00 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v0, sphere_radius);
				Vector3 p10 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v0, sphere_radius);
				Vector3 p11 = cubeToSphere(face_normal + axis_u * u1 + axis_v * v1, sphere_radius);
				Vector3 p01 = cubeToSphere(face_normal + axis_u * u0 + axis_v * v1, sphere_radius);

				Vector2 uv00 = new Vector2(x * inv_cells, y * inv_cells);
				Vector2 uv10 = new Vector2((x + 1) * inv_cells, y * inv_cells);
				Vector2 uv11 = new Vector2((x + 1) * inv_cells, (y + 1) * inv_cells);
				Vector2 uv01 = new Vector2(x * inv_cells, (y + 1) * inv_cells);

				appendTessellatedSphericalQuad(
					p00,
					p10,
					p11,
					p01,
					uv00,
					uv10,
					uv11,
					uv01,
					mesh_subdivisions,
					vertices,
					normals,
					uvs);
			}
		}
	}

	/// <summary>Same tessellation as <see cref="appendTessellatedSphericalQuad"/> but vertices are relative to <paramref name="patch_face_center"/> with radial normals (patch meshes).</summary>
	internal static void appendTessellatedSphericalQuadLocalToPatch(
		Vector3 p00,
		Vector3 p10,
		Vector3 p11,
		Vector3 p01,
		Vector2 uv00,
		Vector2 uv10,
		Vector2 uv11,
		Vector2 uv01,
		int mesh_subdivisions,
		Vector3 patch_face_center,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs)
	{
		int n = Mathf.Max(1, mesh_subdivisions);
		float r = (p00.Length() + p10.Length() + p11.Length() + p01.Length()) * 0.25f;

		Vector3[,] pos = new Vector3[n + 1, n + 1];
		Vector2[,] tuv = new Vector2[n + 1, n + 1];
		for (int j = 0; j <= n; j++)
		{
			float tv = j / (float)n;
			for (int i = 0; i <= n; i++)
			{
				float tu = i / (float)n;
				Vector3 blended = (1.0f - tu) * (1.0f - tv) * p00
					+ tu * (1.0f - tv) * p10
					+ tu * tv * p11
					+ (1.0f - tu) * tv * p01;
				pos[i, j] = blended.Normalized() * r;
				tuv[i, j] = (1.0f - tu) * (1.0f - tv) * uv00
					+ tu * (1.0f - tv) * uv10
					+ tu * tv * uv11
					+ (1.0f - tu) * tv * uv01;
			}
		}

		for (int j = 0; j < n; j++)
		{
			for (int i = 0; i < n; i++)
			{
				Vector3 w00 = pos[i, j];
				Vector3 w10 = pos[i + 1, j];
				Vector3 w11 = pos[i + 1, j + 1];
				Vector3 w01 = pos[i, j + 1];
				Vector2 uq00 = tuv[i, j];
				Vector2 uq10 = tuv[i + 1, j];
				Vector2 uq11 = tuv[i + 1, j + 1];
				Vector2 uq01 = tuv[i, j + 1];

				addPatchTriangleWorldLocal(
					vertices,
					normals,
					uvs,
					patch_face_center,
					w00,
					w10,
					w11,
					uq00,
					uq10,
					uq11);
				addPatchTriangleWorldLocal(
					vertices,
					normals,
					uvs,
					patch_face_center,
					w00,
					w11,
					w01,
					uq00,
					uq11,
					uq01);
			}
		}
	}

	private static void addPatchTriangleWorldLocal(
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

	/// <summary>Bilinear blend on the quad, then project onto the sphere (average corner radius).</summary>
	internal static void appendTessellatedSphericalQuad(
		Vector3 p00,
		Vector3 p10,
		Vector3 p11,
		Vector3 p01,
		Vector2 uv00,
		Vector2 uv10,
		Vector2 uv11,
		Vector2 uv01,
		int mesh_subdivisions,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Vector2> uvs)
	{
		int n = Mathf.Max(1, mesh_subdivisions);
		float r = (p00.Length() + p10.Length() + p11.Length() + p01.Length()) * 0.25f;

		Vector3[,] pos = new Vector3[n + 1, n + 1];
		Vector2[,] tuv = new Vector2[n + 1, n + 1];
		for (int j = 0; j <= n; j++)
		{
			float tv = j / (float)n;
			for (int i = 0; i <= n; i++)
			{
				float tu = i / (float)n;
				Vector3 blended = (1.0f - tu) * (1.0f - tv) * p00
					+ tu * (1.0f - tv) * p10
					+ tu * tv * p11
					+ (1.0f - tu) * tv * p01;
				pos[i, j] = blended.Normalized() * r;
				tuv[i, j] = (1.0f - tu) * (1.0f - tv) * uv00
					+ tu * (1.0f - tv) * uv10
					+ tu * tv * uv11
					+ (1.0f - tu) * tv * uv01;
			}
		}

		for (int j = 0; j < n; j++)
		{
			for (int i = 0; i < n; i++)
			{
				Vector3 w00 = pos[i, j];
				Vector3 w10 = pos[i + 1, j];
				Vector3 w11 = pos[i + 1, j + 1];
				Vector3 w01 = pos[i, j + 1];
				Vector2 uq00 = tuv[i, j];
				Vector2 uq10 = tuv[i + 1, j];
				Vector2 uq11 = tuv[i + 1, j + 1];
				Vector2 uq01 = tuv[i, j + 1];

				addTriangle(vertices, normals, uvs, w00, w10, w11, uq00, uq10, uq11);
				addTriangle(vertices, normals, uvs, w00, w11, w01, uq00, uq11, uq01);
			}
		}
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
}
