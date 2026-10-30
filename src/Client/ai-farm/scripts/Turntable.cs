using Godot;

namespace AIFarm;

/// <summary>
/// Slowly spins this node around its Y axis, like a display turntable.
/// </summary>
public partial class Turntable : Node3D
{
	[Export]
	public float DegreesPerSecond { get; set; } = 4f;

	public override void _Process(double delta)
	{
		RotateY(Mathf.DegToRad(DegreesPerSecond) * (float)delta);
	}
}
