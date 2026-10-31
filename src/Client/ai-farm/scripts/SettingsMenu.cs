using Godot;

namespace AIFarm;

/// <summary>
/// Settings screen for resolution, fullscreen, vsync and the FPS counter.
/// Changes apply and save immediately through <see cref="GameSettings"/>.
/// </summary>
public partial class SettingsMenu : VBoxContainer
{
	[Signal]
	public delegate void ClosedEventHandler();

	private OptionButton _resolution = null!;
	private Button _fullscreen = null!;
	private Button _vsync = null!;
	private Button _showFps = null!;

	public override void _Ready()
	{
		_resolution = GetNode<OptionButton>("%ResolutionOption");
		_fullscreen = GetNode<Button>("%FullscreenToggle");
		_vsync = GetNode<Button>("%VSyncToggle");
		_showFps = GetNode<Button>("%FpsToggle");

		foreach (var resolution in GameSettings.Resolutions)
		{
			_resolution.AddItem(resolution.Label);
		}

		_resolution.ItemSelected += index => GameSettings.Instance.SetResolution((int)index);
		_fullscreen.Toggled += on => { GameSettings.Instance.SetFullscreen(on); UpdateToggleText(_fullscreen); };
		_vsync.Toggled += on => { GameSettings.Instance.SetVSync(on); UpdateToggleText(_vsync); };
		_showFps.Toggled += on => { GameSettings.Instance.SetShowFps(on); UpdateToggleText(_showFps); };
		GetNode<Button>("%BackButton").Pressed += Close;

		// Window size and mode can't change while the game is embedded in the editor's Game view.
		var embedded = Engine.IsEmbeddedInEditor();
		_resolution.Disabled = embedded;
		_fullscreen.Disabled = embedded;
		GetNode<Control>("%EmbeddedNote").Visible = embedded;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Visible && (@event.IsActionPressed("ui_cancel") || @event.IsActionPressed("back")))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}

	public void Open()
	{
		var settings = GameSettings.Instance;
		_resolution.Select(settings.ResolutionIndex);
		SetToggle(_fullscreen, settings.Fullscreen);
		SetToggle(_vsync, settings.VSync);
		SetToggle(_showFps, settings.ShowFps);

		Show();
		(_resolution.Disabled ? _vsync : _resolution).GrabFocus();
	}

	public void Close()
	{
		Hide();
		EmitSignal(SignalName.Closed);
	}

	private static void SetToggle(Button toggle, bool on)
	{
		toggle.SetPressedNoSignal(on);
		UpdateToggleText(toggle);
	}

	private static void UpdateToggleText(Button toggle)
	{
		toggle.Text = toggle.ButtonPressed ? "On" : "Off";
	}
}
