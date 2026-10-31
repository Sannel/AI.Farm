using AIFarm.Contracts;
using AIFarm.Interaction;
using Godot;

namespace AIFarm;

/// <summary>
/// First-person walker. Mouse or right stick looks around, WASD or left stick moves (hold Shift
/// or click the left stick to run), and the action button (E / controller A) uses whatever is
/// under the crosshair.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
	[Export]
	public float MoveSpeed { get; set; } = 6f;

	/// <summary>Speed while the run button is held (the server allows the same).</summary>
	[Export]
	public float RunSpeed { get; set; } = 9f;

	/// <summary>Whether the player has the energy to run; the server says when they don't.</summary>
	public bool CanRun { get; set; } = true;

	/// <summary>True while the player is running (moving with the run button held, able to run).</summary>
	public bool IsRunning { get; private set; }

	[Export]
	public float Acceleration { get; set; } = 12f;

	/// <summary>Radians of rotation per pixel of mouse movement.</summary>
	[Export]
	public float MouseSensitivity { get; set; } = 0.0025f;

	/// <summary>Radians per second at full right-stick deflection.</summary>
	[Export]
	public float StickLookSpeed { get; set; } = 2.5f;

	/// <summary>Walking through water is slower.</summary>
	private const float WadingSpeed = 0.7f;

	/// <summary>Horizontal speeds while swimming (there's no running in the water).</summary>
	private const float SwimSpeed = 3.2f;
	private const float FastSwimSpeed = 4.4f;

	/// <summary>Up and down speeds while swimming.</summary>
	private const float DiveSpeed = 2.2f;
	private const float SurfaceSpeed = 2.0f;

	/// <summary>Whether the player is out of their depth and swimming.</summary>
	public bool IsSwimming { get; private set; }

	/// <summary>Whether the player's eyes are below the water's surface.</summary>
	public bool EyesUnderwater { get; private set; }

	private const float MaxPitch = 1.5f; // ~86 degrees

	private Node3D _head = null!;
	private RayCast3D _interactRay = null!;
	private readonly float _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");

	private ColorRect _underwater = null!;

	public override void _Ready()
	{
		_head = GetNode<Node3D>("Head");
		// A blue wash over the view while the head is under water.
		var layer = new CanvasLayer { Name = "UnderwaterLayer", Layer = 2 };
		_underwater = new ColorRect { Color = new Color(0.1f, 0.4f, 0.6f, 0.16f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
		_underwater.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_underwater);
		AddChild(layer);
		_interactRay = GetNode<RayCast3D>("Head/Camera3D/InteractRay");
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			Look(-motion.ScreenRelative * MouseSensitivity);
		}
		else if (@event.IsActionPressed("action") && _interactRay.GetCollider() is IInteractable target)
		{
			target.Interact();
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		var dt = (float)delta;
		// A visible mouse means a menu has the controls (the sticks are navigating it).
		var controlling = Input.MouseMode == Input.MouseModeEnum.Captured;

		var stick = controlling ? Input.GetVector("look_left", "look_right", "look_up", "look_down") : Vector2.Zero;
		if (stick != Vector2.Zero)
		{
			Look(-stick * StickLookSpeed * dt);
		}

		var input = controlling ? Input.GetVector("move_left", "move_right", "move_forward", "move_back") : Vector2.Zero;
		var direction = Transform.Basis * new Vector3(input.X, 0, input.Y);
		IsRunning = CanRun && input != Vector2.Zero && controlling && Input.IsActionPressed("run");
		var depth = WaterMap.WaterDepth(GlobalPosition.X, GlobalPosition.Z);
		// Out of your depth you swim; you only stand again once it is clearly shallower (so the edge doesn't flicker).
		IsSwimming = depth >= WaterMap.SwimDepth || (IsSwimming && depth >= WaterMap.SwimDepth - 0.2f);
		var wading = !IsSwimming && depth > 0.15f;
		var speed = IsSwimming
			? (Input.IsActionPressed("run") && CanRun ? FastSwimSpeed : SwimSpeed)
			: (IsRunning ? RunSpeed : MoveSpeed) * (wading ? WadingSpeed : 1f);
		IsRunning = IsRunning && !IsSwimming;
		var target = direction * speed;

		var velocity = Velocity;
		velocity.X = Mathf.Lerp(velocity.X, target.X, Acceleration * dt);
		velocity.Z = Mathf.Lerp(velocity.Z, target.Z, Acceleration * dt);
		if (IsSwimming)
		{
			// Afloat with your head just clear of the water; hold a key to dive or to climb back up.
			var floatY = WaterMap.SurfaceY + 0.15f - WaterMap.SwimEyeHeight;
			var wantsDown = controlling && Input.IsActionPressed("swim_down");
			var wantsUp = controlling && Input.IsActionPressed("swim_up");
			var rise = wantsDown ? -DiveSpeed : wantsUp ? SurfaceSpeed : Mathf.Clamp((floatY - GlobalPosition.Y) * 3f, -SurfaceSpeed, SurfaceSpeed);
			if (GlobalPosition.Y >= floatY && rise > 0f)
			{
				rise = Mathf.Clamp((floatY - GlobalPosition.Y) * 6f, -SurfaceSpeed, 0f); // can't climb out of the water
			}
			velocity.Y = Mathf.Lerp(velocity.Y, rise, 8f * dt);
		}
		else if (!IsOnFloor())
		{
			velocity.Y -= _gravity * dt;
		}

		// The view drops to water level while swimming.
		var headHeight = IsSwimming ? WaterMap.SwimEyeHeight : WaterMap.WalkEyeHeight;
		var headPosition = _head.Position;
		headPosition.Y = Mathf.Lerp(headPosition.Y, headHeight, 1f - Mathf.Exp(-8f * dt));
		_head.Position = headPosition;
		EyesUnderwater = _head.GlobalPosition.Y < WaterMap.SurfaceY - 0.04f && depth > 0f;
		if (_underwater.Visible != EyesUnderwater)
		{
			SetUnderwaterFog(EyesUnderwater);
		}
		_underwater.Visible = EyesUnderwater;

		Velocity = velocity;
		MoveAndSlide();
	}

	private Environment? _environment;
	private (bool Enabled, float Density, Color Colour)? _dryFog;

	/// <summary>Under water the world fades into blue with distance; back on land the scene's own fog returns.</summary>
	private void SetUnderwaterFog(bool under)
	{
		_environment ??= (GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment)?.Environment;
		if (_environment is null)
		{
			return;
		}
		if (under)
		{
			_dryFog ??= (_environment.FogEnabled, _environment.FogDensity, _environment.FogLightColor);
			_environment.FogEnabled = true;
			_environment.FogDensity = 0.04f;
			_environment.FogLightColor = new Color(0.12f, 0.42f, 0.6f);
		}
		else if (_dryFog is { } dry)
		{
			_environment.FogEnabled = dry.Enabled;
			_environment.FogDensity = dry.Density;
			_environment.FogLightColor = dry.Colour;
		}
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
