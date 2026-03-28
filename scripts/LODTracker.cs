using Godot;

public partial class LODTracker : Node3D
{
	public static LODTracker instance;
	[Export] public bool debug_wireframe_viewport = false;
	private bool is_tab_pressed = false;

	public override void _EnterTree()
	{
		instance = this;
		base._EnterTree();
	}

	public override void _ExitTree()
	{
		instance = null;
		base._ExitTree();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (Input.IsKeyPressed(Key.Tab))
		{
			if (!is_tab_pressed)
			{
				debug_wireframe_viewport = !debug_wireframe_viewport;
				is_tab_pressed = true;
			}
		}
		else
		{
			is_tab_pressed = false;
		}
		GetViewport().DebugDraw = debug_wireframe_viewport
			? Viewport.DebugDrawEnum.Wireframe
			: Viewport.DebugDrawEnum.Disabled;
		base._PhysicsProcess(delta);
		Camera3D camera = GetViewport().GetCamera3D();
		GlobalPosition = GlobalPosition.Lerp(camera.GlobalPosition + -camera.GlobalTransform.Basis.Z * 5.0f, (float)delta);
		GlobalRotation = GlobalRotation.Lerp(camera.GlobalRotation, (float)delta);
	}
}
