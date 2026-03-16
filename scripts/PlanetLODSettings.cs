
using Godot;

[GlobalClass]
[Tool]
public partial class PlanetLODSettings: Godot.Resource
{
    [Export]
    public float distance = 10f;
    [Export]
    public int divisions = 0;

}