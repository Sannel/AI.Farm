using Godot;

namespace AIFarm.Interaction;

/// <summary>
/// Makes its parent mesh a door: adds a box collider sized to the mesh and swings the mesh
/// about its origin (the hinge) when the player interacts. A closed door blocks the way; an
/// open one stays aimable (so it can be closed again) but lets the player and NPCs walk through.
/// </summary>
public partial class Door : StaticBody3D, IInteractable
{
	/// <summary>Yaw of the open door in degrees; negative swings it out of a front wall.</summary>
	[Export]
	public float OpenAngle { get; set; } = -105f;

	[Export]
	public float SwingSeconds { get; set; } = 0.6f;

	[Export]
	public bool StartOpen { get; set; }

	/// <summary>Physics layer 1: what bodies collide with.</summary>
	private const uint BlockingLayer = 1;

	/// <summary>Physics layer 3: only the player's interact ray looks at it.</summary>
	private const uint InteractableLayer = 1 << 2;

	private Node3D _leaf = null!;
	private float _closedYaw;
	private bool _open;
	private Tween? _tween;

	public override void _Ready()
	{
		_leaf = GetParent<Node3D>();
		_closedYaw = _leaf.Rotation.Y;

		if (_leaf is MeshInstance3D mesh)
		{
			var bounds = mesh.GetAabb();
			AddChild(new CollisionShape3D
			{
				Shape = new BoxShape3D { Size = bounds.Size },
				Position = bounds.GetCenter(),
			});
		}

		_open = StartOpen;
		SetYaw(_open ? OpenYaw : _closedYaw);
		SetBlocking(!_open);
	}

	private float OpenYaw => _closedYaw + Mathf.DegToRad(OpenAngle);

	public void Interact()
	{
		_open = !_open;
		_tween?.Kill();
		_tween = CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
		_tween.TweenMethod(Callable.From<float>(SetYaw), _leaf.Rotation.Y, _open ? OpenYaw : _closedYaw, SwingSeconds);

		// Stop blocking as soon as it starts opening, but only block again once it's fully shut,
		// so the swing never shoves anyone.
		if (_open)
		{
			SetBlocking(false);
		}
		else
		{
			_tween.TweenCallback(Callable.From(() => SetBlocking(true)));
		}
	}

	private void SetBlocking(bool blocking)
	{
		CollisionLayer = blocking ? BlockingLayer | InteractableLayer : InteractableLayer;
	}

	private void SetYaw(float yaw)
	{
		var rotation = _leaf.Rotation;
		rotation.Y = yaw;
		_leaf.Rotation = rotation;
	}
}
