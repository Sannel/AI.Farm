using System.Threading.Tasks;
using AIFarm.Characters;
using AIFarm.Contracts;
using AIFarm.Network;
using Godot;

namespace AIFarm.DebugTools;

/// <summary>
/// Debug tool: aim the crosshair at a character and press the action button (E / controller A)
/// to get a popup of every animation it supports, grouped by rig file. Picking one plays it on
/// that character and closes the whole menu; back (Backspace / controller B) closes it too.
/// Online, players allowed to destroy animals also get "Destroy…" on an animal, which asks
/// once more before the server removes it for good. People (players, the farmer) can't be.
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
		if (!@event.IsActionPressed("action") || Input.MouseMode != Input.MouseModeEnum.Captured)
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
		_menu = new PopupMenu();
		AddChild(_menu);

		// Server entities' nodes carry their id in the name; show just what they're called.
		var nodeName = animator.GetParent().Name.ToString();
		var title = animator.GetParent() is RemoteEntity remote ? remote.State.Name : nodeName;
		_menu.AddSeparator($"{title} animations");
		var current = animator.CurrentAnimation;
		foreach (var (group, names) in animator.AnimationGroups)
		{
			var submenu = new PopupMenu { Name = "Group" + _menu.GetChildCount() };
			foreach (var name in names)
			{
				submenu.AddRadioCheckItem(name);
				submenu.SetItemChecked(submenu.ItemCount - 1, name == current);
			}
			submenu.IndexPressed += index =>
			{
				animator.Play(names[index]);
				// Let the submenu finish handling the click before tearing the menus down;
				// closing them from inside this signal leaves the submenu on screen.
				Callable.From(CloseMenu).CallDeferred();
			};
			submenu.WindowInput += OnMenuInput;

			_menu.AddChild(submenu);
			_menu.AddSubmenuNodeItem(group == nodeName ? title : group, submenu);
		}

		if (animator.GetParent() is RemoteEntity { State.Kind: EntityKind.Animal } animal
			&& ServerConnection.Instance.Can(Permissions.DestroyAnimals))
		{
			AddDestroy(animal);
		}

		_menu.WindowInput += OnMenuInput;
		_menu.PopupHide += OnMenuClosed;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_menu.PopupCentered();
		// Start on the first group (index 0 is the title) so a controller can navigate straight away.
		_menu.SetFocusedItem(1);
	}

	/// <summary>"Destroy…" opens a one-item confirmation, so it can't be picked by accident.</summary>
	private void AddDestroy(RemoteEntity animal)
	{
		var id = animal.State.Id;
		var name = animal.State.Name;
		var confirm = new PopupMenu { Name = "Destroy" };
		confirm.AddItem($"Yes, remove this {name.ToLowerInvariant()} for good");
		confirm.IndexPressed += _ =>
		{
			Callable.From(CloseMenu).CallDeferred();
			ServerConnection.Instance.DestroyEntityAsync(id).ContinueWith(
				task => GD.PushWarning($"Couldn't destroy {name}: {task.Exception?.GetBaseException().Message}"),
				TaskContinuationOptions.OnlyOnFaulted);
		};
		confirm.WindowInput += OnMenuInput;
		_menu!.AddSeparator();
		_menu.AddChild(confirm);
		_menu.AddSubmenuNodeItem("Destroy…", confirm);
	}

	private void OnMenuInput(InputEvent @event)
	{
		if (@event.IsActionPressed("back"))
		{
			CloseMenu();
		}
	}

	/// <summary>Closes the menu; <see cref="OnMenuClosed"/> then frees it with its submenus.</summary>
	private void CloseMenu()
	{
		_menu?.Hide();
	}

	/// <summary>
	/// However the menu closed (an item picked, back, or a click outside), free it so none of
	/// its submenus can linger on screen.
	/// </summary>
	private void OnMenuClosed()
	{
		_menu?.QueueFree();
		_menu = null;
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
