using Godot;
using System;

/// <summary>
/// Builds a vertex grid on the XZ plane at <see cref="physical_size"/> world units per side,
/// with <see cref="vertices_per_side"/> vertices along each edge; fills heights on the GPU, then displaces Y and recomputes normals.
/// </summary>
public partial class ProceduralPlaneTest : MeshInstance3D
{
	[Export] public string compute_shader_path = "res://shaders/PerlinHeightmapCompute.glsl";

	/// <summary>Width and depth of the plane in world units (square on XZ).</summary>
	[Export(PropertyHint.Range, "0.01,100000,0.01,or_greater")]
	public float physical_size = 10.0f;

	/// <summary>Number of vertices along each axis (minimum 2).</summary>
	[Export(PropertyHint.Range, "2,4096,1")]
	public int vertices_per_side = 64;

	[Export] public float height_scale = 2.0f;

	/// <summary>Base sampling scale on the vertex grid (coarsest layer). Higher = smaller features.</summary>
	[Export] public float noise_scale = 0.08f;

	/// <summary>
	/// Number of noise layers. Each added layer doubles coordinate scale (finer detail) and halves amplitude vs. the previous layer.
	/// </summary>
	[Export(PropertyHint.Range, "1,16,1")]
	public int noise_layers = 5;

	private RenderingDevice rd;
	private Rid height_buffer_rid;
	private Rid params_buffer_rid;
	private Rid shader_rid;
	private Rid pipeline_rid;
	private Rid uniform_set_rid;

	public override void _Ready()
	{
		int res = Mathf.Max(2, vertices_per_side);
		float size = Mathf.Max(0.0001f, physical_size);

		float[] heights = runHeightmapCompute(res);
		if (heights == null || heights.Length != res * res)
		{
			heights = new float[res * res];
		}

		Mesh = buildDisplacedPlaneMesh(heights, res, size);
		if (MaterialOverride == null)
		{
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.35f, 0.55f, 0.28f)
			};
		}
	}

	public override void _ExitTree()
	{
		freeComputeResources();
		base._ExitTree();
	}

	private float[] runHeightmapCompute(int resolution)
	{
		var shader_file = GD.Load<RDShaderFile>(compute_shader_path);
		if (shader_file == null)
		{
			GD.PushError($"ProceduralPlaneTest: could not load compute shader at {compute_shader_path}");
			return null;
		}

		rd = RenderingServer.CreateLocalRenderingDevice();
		if (rd == null)
		{
			GD.PushError("ProceduralPlaneTest: CreateLocalRenderingDevice failed (use Forward+ or Mobile renderer).");
			return null;
		}

		RDShaderSpirV spirv = shader_file.GetSpirV();
		shader_rid = rd.ShaderCreateFromSpirV(spirv);
		if (!shader_rid.IsValid)
		{
			GD.PushError("ProceduralPlaneTest: ShaderCreateFromSpirV failed.");
			freeComputeResources();
			return null;
		}

		int height_count = resolution * resolution;
		int height_bytes = height_count * sizeof(float);
		height_buffer_rid = rd.StorageBufferCreate((uint)height_bytes, Array.Empty<byte>());

		byte[] params_bytes = new byte[4 * sizeof(float)];
		BitConverter.GetBytes((float)resolution).CopyTo(params_bytes, 0);
		BitConverter.GetBytes(noise_scale).CopyTo(params_bytes, sizeof(float));
		BitConverter.GetBytes((float)Mathf.Clamp(noise_layers, 1, 16)).CopyTo(params_bytes, 2 * sizeof(float));
		BitConverter.GetBytes(0.0f).CopyTo(params_bytes, 3 * sizeof(float));
		params_buffer_rid = rd.StorageBufferCreate((uint)params_bytes.Length, params_bytes);

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
		uniform_set_rid = rd.UniformSetCreate(uniforms, shader_rid, 0);
		if (!uniform_set_rid.IsValid)
		{
			GD.PushError("ProceduralPlaneTest: UniformSetCreate failed.");
			freeComputeResources();
			return null;
		}

		pipeline_rid = rd.ComputePipelineCreate(shader_rid);
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
			GD.PushError("ProceduralPlaneTest: BufferGetData returned unexpected size.");
			freeComputeResources();
			return null;
		}

		var heights = new float[height_count];
		Buffer.BlockCopy(raw, 0, heights, 0, height_bytes);

		freeComputeResources();
		return heights;
	}

	private void freeComputeResources()
	{
		if (rd == null)
		{
			return;
		}

		if (uniform_set_rid.IsValid)
		{
			rd.FreeRid(uniform_set_rid);
			uniform_set_rid = default;
		}

		if (pipeline_rid.IsValid)
		{
			rd.FreeRid(pipeline_rid);
			pipeline_rid = default;
		}

		if (shader_rid.IsValid)
		{
			rd.FreeRid(shader_rid);
			shader_rid = default;
		}

		if (height_buffer_rid.IsValid)
		{
			rd.FreeRid(height_buffer_rid);
			height_buffer_rid = default;
		}

		if (params_buffer_rid.IsValid)
		{
			rd.FreeRid(params_buffer_rid);
			params_buffer_rid = default;
		}

		rd.Free();
		rd = null;
	}

	private static float sampleHeight(float[] heights, int x, int z, int vx, int vz)
	{
		int cx = Mathf.Clamp(x, 0, vx - 1);
		int cz = Mathf.Clamp(z, 0, vz - 1);
		return heights[cx + cz * vx];
	}

	private ArrayMesh buildDisplacedPlaneMesh(float[] heights, int resolution, float plane_size)
	{
		int vx = resolution;
		int vz = resolution;
		int vert_count = vx * vz;
		var vertices = new Vector3[vert_count];
		var normals = new Vector3[vert_count];
		var uvs = new Vector2[vert_count];

		float cell = plane_size / (resolution - 1);
		float half = plane_size * 0.5f;

		for (int z = 0; z < vz; z++)
		{
			for (int x = 0; x < vx; x++)
			{
				int i = x + z * vx;
				float u = x / (float)(vx - 1);
				float v = z / (float)(vz - 1);
				float px = u * plane_size - half;
				float pz = v * plane_size - half;
				float h = heights[i] * height_scale;
				vertices[i] = new Vector3(px, h, pz);
				uvs[i] = new Vector2(u, v);
			}
		}

		for (int z = 0; z < vz; z++)
		{
			for (int x = 0; x < vx; x++)
			{
				int i = x + z * vx;
				float dhdx = (sampleHeight(heights, x + 1, z, vx, vz) - sampleHeight(heights, x - 1, z, vx, vz)) * height_scale / (2.0f * cell);
				float dhdz = (sampleHeight(heights, x, z + 1, vx, vz) - sampleHeight(heights, x, z - 1, vx, vz)) * height_scale / (2.0f * cell);
				var n = new Vector3(-dhdx, 1.0f, -dhdz);
				normals[i] = n.LengthSquared() > 0.0001f ? n.Normalized() : Vector3.Up;
			}
		}

		int quad_x = vx - 1;
		int quad_z = vz - 1;
		var indices = new int[quad_x * quad_z * 6];
		int t = 0;
		for (int z = 0; z < quad_z; z++)
		{
			for (int x = 0; x < quad_x; x++)
			{
				// Godot (Vulkan) uses clockwise front faces by default. Winding below makes
				// both tris use the same screen-space handedness so the top (+vertex normals)
				// is the drawn side when viewed from +Y, not the underside.
				int i00 = x + z * vx;
				int i10 = i00 + 1;
				int i01 = i00 + vx;
				int i11 = i01 + 1;
				indices[t++] = i00;
				indices[t++] = i10;
				indices[t++] = i01;
				indices[t++] = i10;
				indices[t++] = i11;
				indices[t++] = i01;
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.Index] = indices;

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}
}
