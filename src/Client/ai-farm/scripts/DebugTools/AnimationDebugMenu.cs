using AIFarm.Characters;
using Godot;

namespace AIFarm.DebugTools;

/// <summary>
/// Debug tool: aim the crosshair at a character and left-click to get a popup of every
/// animation it supports, grouped by rig file. Picking one plays it on that character.
/// </summary>
public partial class AnimationDebugMenu : Control
{
	[Export]
	public float MaxClickDistance { get; set; } = 40f;

	private Control _crosshair = null!;
	private PopupMenu? _menu;

	public override void _Ready()
	{
		_crosshair = GetNode<Control>("Crosshair");
	}

	public override void _Process(double delta)
	{
		// The crosshair only means something while the mouse is captured for looking around.
		_crosshair.Visible = Input.MouseMode == Input.MouseModeEnum.Captured;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
			|| Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			return;
		}

		if (FindAnimatorUnderCrosshair() is { } animator)
		{
			Open(animator);
			GetViewport().SetInputAsHandled();
		}
	}

	private CharacterAnimator? FindAnimatorUnderCrosshair()
	{
		var camera = GetViewport().GetCamera3D();
		if (camera is null)
		{
			return null;
		}

		var center = GetViewport().GetVisibleRect().Size / 2f;
		var from = camera.ProjectRayOrigin(center);
		var query = PhysicsRayQueryParameters3D.Create(from, from + camera.ProjectRayNormal(center) * MaxClickDistance);
		if (FindAncestor<CollisionObject3D>(camera) is { } self)
		{
			query.Exclude = [self.GetRid()];
		}

		var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0)
		{
			return null;
		}

		// The animator sits beside the collision body, so search the hit node and its parents' children.
		for (var node = hit["collider"].As<Node>(); node is not null; node = node.GetParent())
		{
			foreach (var child in node.GetChildren())
			{
				if (child is CharacterAnimator animator)
				{
					return animator;
				}
			}
		}
		return null;
	}

	private void Open(CharacterAnimator animator)
	{
		_menu?.QueueFree();
		_menu = new PopupMenu();
		AddChild(_menu);

		_menu.AddSeparator($"{animator.GetParent().Name} animations");
		var current = animator.CurrentAnimation;
		foreach (var (group, names) in animator.AnimationGroups)
		{
			var submenu = new PopupMenu { Name = group.Replace(" ", "") };
			foreach (var name in names)
			{
				submenu.AddRadioCheckItem(name);
				submenu.SetItemChecked(submenu.ItemCount - 1, name == current);
			}
			submenu.IndexPressed += index => animator.Play(names[index]);

			_menu.AddChild(submenu);
			_menu.AddSubmenuNodeItem(group, submenu);
		}

		_menu.PopupHide += OnMenuClosed;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_menu.PopupCentered();
	}

	private void OnMenuClosed()
	{
		if (!GetTree().Paused)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
	}

	private static T? FindAncestor<T>(Node node) where T : Node
	{
		for (var current = node.GetParent(); current is not null; current = current.GetParent())
		{
			if (current is T match)
			{
				return match;
			}
		}
		return null;
	}
}
