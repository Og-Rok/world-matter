using Godot;

/// <summary>Snapshot for one deferred GPU heightmap dispatch (queued on <see cref="WorldBuilder"/>).</summary>
internal sealed class ShellHeightmapPendingJob
{
	public WorldBuilderPlane plane;
	public int resolution;
	public int request_serial;
	public string shader_path;
	public float noise_scale;
	public int noise_layers;
	public Vector3 corner_00;
	public Vector3 corner_10;
	public Vector3 corner_11;
	public Vector3 corner_01;
}
