using Godot;

public partial class LODTracker : Node3D
{
	public static LODTracker instance;

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
}
