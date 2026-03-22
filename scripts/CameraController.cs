using Godot;

public partial class CameraController : Camera3D
{
    public static CameraController instance;
    [Export] public float move_speed       = 10.0f;
    [Export] public float mouse_sensitivity = 0.002f;
    [Export] public float roll_speed       = 1.5f;
    /// <summary>Very expensive on large meshes; disable for normal play.</summary>
    [Export] public bool debug_wireframe_viewport = false;

    public override void _EnterTree()
    {
        instance = this;
    }

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mouse_motion)
        {
            // Yaw and pitch applied locally so they stay relative to current roll
            RotateObjectLocal(Vector3.Up,    -mouse_motion.Relative.X * mouse_sensitivity);
            RotateObjectLocal(Vector3.Right, -mouse_motion.Relative.Y * mouse_sensitivity);
        }

        if (@event is InputEventMouseButton && Input.MouseMode == Input.MouseModeEnum.Visible)
            Input.MouseMode = Input.MouseModeEnum.Captured;

        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _Process(double delta)
    {
        GetViewport().DebugDraw = debug_wireframe_viewport
            ? Viewport.DebugDrawEnum.Wireframe
            : Viewport.DebugDrawEnum.Disabled;

        if (Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }
        float dt = (float)delta;

        // Build a movement direction vector in local camera space, then
        // read the actual world-space axes from the camera's current basis.
        // Basis.Z points behind the camera in Godot, so forward is -Z.
        var move_dir = Vector3.Zero;

        if (Input.IsKeyPressed(Key.W)) move_dir -= Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) move_dir += Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) move_dir -= Transform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) move_dir += Transform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) move_dir += Transform.Basis.Y;
        if (Input.IsKeyPressed(Key.Q)) move_dir -= Transform.Basis.Y;

        if (move_dir.LengthSquared() > 0)
            Position += move_dir.Normalized() * move_speed * dt * (Input.IsKeyPressed(Key.Shift) ? 10.0f : 1.0f);

        // Roll around the camera's own forward axis
        if (Input.IsKeyPressed(Key.X))
            RotateObjectLocal(Vector3.Forward, roll_speed * dt);
        if (Input.IsKeyPressed(Key.Z))
            RotateObjectLocal(Vector3.Forward, -roll_speed * dt);
    }
}
