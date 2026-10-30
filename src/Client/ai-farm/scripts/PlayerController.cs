using Godot;

namespace AIFarm;

/// <summary>
/// First-person walker. Mouse or right stick looks around, WASD or left stick moves.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
	[Export]
	public float MoveSpeed { get; set; } = 6f;

	[Export]
	public float Acceleration { get; set; } = 12f;

	/// <summary>Radians of rotation per pixel of mouse movement.</summary>
	[Export]
	public float MouseSensitivity { get; set; } = 0.0025f;

	/// <summary>Radians per second at full right-stick deflection.</summary>
	[Export]
	public float StickLookSpeed { get; set; } = 2.5f;

	private const float MaxPitch = 1.5f; // ~86 degrees

	private Node3D _head = null!;
	private readonly float _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");

	public override void _Ready()
	{
		_head = GetNode<Node3D>("Head");
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			Look(-motion.ScreenRelative * MouseSensitivity);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		var dt = (float)delta;

		var stick = Input.GetVector("look_left", "look_right", "look_up", "look_down");
		if (stick != Vector2.Zero)
		{
			Look(-stick * StickLookSpeed * dt);
		}

		var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
		var direction = Transform.Basis * new Vector3(input.X, 0, input.Y);
		var target = direction * MoveSpeed;

		var velocity = Velocity;
		velocity.X = Mathf.Lerp(velocity.X, target.X, Acceleration * dt);
		velocity.Z = Mathf.Lerp(velocity.Z, target.Z, Acceleration * dt);
		if (!IsOnFloor())
		{
			velocity.Y -= _gravity * dt;
		}

		Velocity = velocity;
		MoveAndSlide();
	}

	/// <summary>Yaws the body and pitches the head by the given amounts (radians).</summary>
	private void Look(Vector2 amount)
	{
		RotateY(amount.X);
		var rotation = _head.Rotation;
		rotation.X = Mathf.Clamp(rotation.X + amount.Y, -MaxPitch, MaxPitch);
		_head.Rotation = rotation;
	}
}
