using System;
using System.Linq;
using AIFarm.Contracts;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// For players allowed to spawn animals: Q (or Select on a controller) opens a menu of every
/// animal the server offers, grouped by species. Picking one asks the server to spawn it a
/// few metres in front of the player and closes the whole menu; back (Backspace / B) or Q /
/// Select again closes it. A short note confirms what happened.
/// </summary>
public partial class SpawnMenu : Control
{
	private const double NoteSeconds = 2.5;

	private PopupMenu? _menu;
	private bool _picked; // one pick per opening, however many times the press is reported
	private Label _note = null!;
	private double _noteTimer;

	public override void _Ready()
	{
		_note = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			Visible = false,
		};
		_note.AddThemeColorOverride("font_color", Colors.White);
		_note.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.2f, 0.1f));
		_note.AddThemeConstantOverride("outline_size", 8);
		_note.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
		_note.GrowHorizontal = GrowDirection.Both;
		_note.Position += new Vector2(0, -120);
		AddChild(_note);
	}

	public override void _Process(double delta)
	{
		if (_noteTimer > 0 && (_noteTimer -= delta) <= 0)
		{
			_note.Visible = false;
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!@event.IsActionPressed("spawn_menu") || Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			return;
		}
		var connection = ServerConnection.Instance;
		if (!connection.IsOnline || !connection.Can(Permissions.SpawnAnimals) || connection.SpawnableAnimals.Count == 0)
		{
			return;
		}
		Open();
		GetViewport().SetInputAsHandled();
	}

	private void Open()
	{
		var menu = new PopupMenu();
		_menu = menu;
		_picked = false;
		AddChild(menu);
		menu.AddSeparator("Spawn an animal");

		foreach (var species in ServerConnection.Instance.SpawnableAnimals.GroupBy(a => a.Species))
		{
			var animals = species.OrderByDescending(a => a.Sex == "Female").ToArray();
			if (animals.Length == 1)
			{
				// Only one kind (no separate sexes yet): pick it straight from the main menu.
				menu.AddItem(animals[0].Name);
				menu.SetItemMetadata(menu.ItemCount - 1, animals[0].Model);
				continue;
			}
			var submenu = new PopupMenu { Name = species.Key };
			foreach (var animal in animals)
			{
				submenu.AddItem(animal.Sex == "" ? animal.Name : $"{animal.Name} ({animal.Sex.ToLowerInvariant()})");
				submenu.SetItemMetadata(submenu.ItemCount - 1, animal.Model);
			}
			submenu.IndexPressed += index => Pick(submenu, (int)index);
			submenu.WindowInput += OnMenuInput;
			menu.AddChild(submenu);
			menu.AddSubmenuNodeItem(species.Key, submenu);
		}

		menu.IndexPressed += index => Pick(menu, (int)index);
		menu.WindowInput += OnMenuInput;
		menu.PopupHide += OnMenuClosed;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		menu.PopupCentered();
		// Start on the first species (index 0 is the title) so a controller can navigate straight away.
		menu.SetFocusedItem(1);
	}

	private async void Pick(PopupMenu menu, int index)
	{
		// A popup hides itself (and we free it) before it reports the pick, so the menu may
		// already be closed here; it's still valid until the end of the frame.
		if (_picked || !IsInstanceValid(menu))
		{
			return;
		}
		if (menu.GetItemMetadata(index).AsString() is not { Length: > 0 } model)
		{
			return; // a species entry: it opens its submenu instead
		}
		_picked = true;
		// Let the menu finish handling the press before tearing the menus down; closing them
		// from inside this signal leaves a submenu on screen.
		Callable.From(CloseMenu).CallDeferred();
		try
		{
			var animal = await ServerConnection.Instance.SpawnAnimalAsync(model);
			ShowNote($"Spawned a {animal.Name.ToLowerInvariant()}");
		}
		catch (Exception e)
		{
			GD.PushWarning($"Spawning {model} failed: {e.Message}");
			ShowNote($"Couldn't spawn that: {e.Message.Split(": ").Last()}");
		}
	}

	private void ShowNote(string text)
	{
		_note.Text = text;
		_note.Visible = true;
		_noteTimer = NoteSeconds;
	}

	private void OnMenuInput(InputEvent @event)
	{
		if (@event.IsActionPressed("back") || @event.IsActionPressed("spawn_menu"))
		{
			CloseMenu();
		}
	}

	private void CloseMenu()
	{
		_menu?.Hide();
	}

	/// <summary>However the menu closed, free it (and its submenus) and go back to looking around.</summary>
	private void OnMenuClosed()
	{
		_menu?.QueueFree();
		_menu = null;
		if (!GetTree().Paused)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
	}
}
