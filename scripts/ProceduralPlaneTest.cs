using Godot;
using System;

/// <summary>
/// Builds a vertex grid on the XZ plane at <see cref="physical_size"/> world units per side,
/// with <see cref="vertices_per_side"/> vertices along each edge; fills heights on the GPU, recenters so the lowest sample is 0, then displaces Y upward and recomputes normals.
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

	/// <summary>
	/// Noise frequency in world space (multiplied with XZ in the same units as <see cref="physical_size"/>).
	/// Higher = smaller hills; independent of vertex count.
	/// </summary>
	[Export] public float noise_scale = 0.08f;

	/// <summary>
	/// Number of noise layers. Each added layer doubles coordinate scale (finer detail) and halves amplitude vs. the previous layer.
	/// </summary>
	[Export(PropertyHint.Range, "1,16,1")]
	public int noise_layers = 5;

	// --- Water mesh trimming ---
	/// <summary>World Y height of the water surface. Quads fully above this are excluded from the water mesh.</summary>
	[Export] public float water_level = 15.0f;

	/// <summary>
	/// Resolution of the generated water mesh grid (independent of terrain resolution since water is flat).
	/// Lower values = fewer vertices but less precise shoreline.
	/// </summary>
	[Export(PropertyHint.Range, "2,1024,1")]
	public int water_resolution = 256;

	/// <summary>Assign the Water MeshInstance3D here; its mesh will be replaced with a trimmed flat mesh at runtime.</summary>
	[Export] public MeshInstance3D water_mesh_target;

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

		float[] heights = runHeightmapCompute(res, size);
		if (heights == null || heights.Length != res * res)
		{
			heights = new float[res * res];
		}

		normalizeHeightmapSoMinimumIsZero(heights);
		Mesh = buildDisplacedPlaneMesh(heights, res, size);
		if (MaterialOverride == null)
		{
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.35f, 0.55f, 0.28f)
			};
		}

		if (water_mesh_target != null)
		{
			int wres = Mathf.Clamp(water_resolution, 2, 4096);
			water_mesh_target.Mesh = buildWaterMesh(heights, res, size, wres);
			water_mesh_target.Transform = Transform3D.Identity;
		}
	}

	public override void _ExitTree()
	{
		freeComputeResources();
		base._ExitTree();
	}

	private float[] runHeightmapCompute(int resolution, float plane_extent)
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
		BitConverter.GetBytes(plane_extent).CopyTo(params_bytes, 3 * sizeof(float));
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

	private static void writeVec4ToByteBuffer(byte[] buf, int offset, Vector4 vec)
	{
		BitConverter.GetBytes(vec.X).CopyTo(buf, offset);
		BitConverter.GetBytes(vec.Y).CopyTo(buf, offset + 4);
		BitConverter.GetBytes(vec.Z).CopyTo(buf, offset + 8);
		BitConverter.GetBytes(vec.W).CopyTo(buf, offset + 12);
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

	/// <summary>
	/// Shifts all samples by the global minimum so the heightmap is ≥ 0 and the mesh sits on Y = 0 at its lowest points.
	/// Relative relief is unchanged; <see cref="height_scale"/> still scales upward from there.
	/// </summary>
	private static void normalizeHeightmapSoMinimumIsZero(float[] heights)
	{
		if (heights == null || heights.Length == 0)
		{
			return;
		}

		float min_h = heights[0];
		for (int i = 1; i < heights.Length; i++)
		{
			if (heights[i] < min_h)
			{
				min_h = heights[i];
			}
		}

		for (int i = 0; i < heights.Length; i++)
		{
			heights[i] -= min_h;
		}
	}

	/// <summary>
	/// Builds a flat mesh at <see cref="water_level"/> that covers only the cells where the terrain is below the
	/// water surface. Uses an independent <paramref name="water_res"/> grid so water can be lower resolution than terrain.
	/// </summary>
	private ArrayMesh buildWaterMesh(float[] terrain_heights, int terrain_res, float plane_size, int water_res)
	{
		float half = plane_size * 0.5f;

		var vertices = new Vector3[water_res * water_res];
		var normals  = new Vector3[water_res * water_res];
		var uvs      = new Vector2[water_res * water_res];

		for (int z = 0; z < water_res; z++)
		{
			for (int x = 0; x < water_res; x++)
			{
				int i = x + z * water_res;
				float u = x / (float)(water_res - 1);
				float v = z / (float)(water_res - 1);
				vertices[i] = new Vector3(u * plane_size - half, water_level, v * plane_size - half);
				normals[i]  = Vector3.Up;
				uvs[i]      = new Vector2(u, v);
			}
		}

		var indices = new System.Collections.Generic.List<int>();
		for (int z = 0; z < water_res - 1; z++)
		{
			for (int x = 0; x < water_res - 1; x++)
			{
				float h00 = sampleTerrainAtWaterPoint(terrain_heights, terrain_res, water_res, x,     z    ) * height_scale;
				float h10 = sampleTerrainAtWaterPoint(terrain_heights, terrain_res, water_res, x + 1, z    ) * height_scale;
				float h01 = sampleTerrainAtWaterPoint(terrain_heights, terrain_res, water_res, x,     z + 1) * height_scale;
				float h11 = sampleTerrainAtWaterPoint(terrain_heights, terrain_res, water_res, x + 1, z + 1) * height_scale;

				// Skip quads where every corner is above the water line
				if (h00 >= water_level && h10 >= water_level && h01 >= water_level && h11 >= water_level)
				{
					continue;
				}

				int i00 = x + z * water_res;
				int i10 = i00 + 1;
				int i01 = i00 + water_res;
				int i11 = i01 + 1;

				indices.Add(i00); indices.Add(i10); indices.Add(i01);
				indices.Add(i10); indices.Add(i11); indices.Add(i01);
			}
		}

		if (indices.Count == 0)
		{
			return null;
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.TexUV]  = uvs;
		arrays[(int)Mesh.ArrayType.Index]  = indices.ToArray();

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	private static float sampleTerrainAtWaterPoint(float[] terrain_heights, int terrain_res, int water_res, int wx, int wz)
	{
		int tx = Mathf.RoundToInt(wx * (terrain_res - 1.0f) / (water_res - 1.0f));
		int tz = Mathf.RoundToInt(wz * (terrain_res - 1.0f) / (water_res - 1.0f));
		tx = Mathf.Clamp(tx, 0, terrain_res - 1);
		tz = Mathf.Clamp(tz, 0, terrain_res - 1);
		return terrain_heights[tx + tz * terrain_res];
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
