using Godot;

namespace AIFarm;

/// <summary>
/// Slowly spins this node around the Y axis. Parent a camera to it to orbit a scene.
/// </summary>
public partial class OrbitPivot : Node3D
{
	[Export]
	public float DegreesPerSecond { get; set; } = 4f;

	public override void _Process(double delta)
	{
		RotateY(Mathf.DegToRad(DegreesPerSecond) * (float)delta);
	}
}
