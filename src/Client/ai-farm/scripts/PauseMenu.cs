using Godot;

namespace AIFarm;

/// <summary>
/// In-game pause overlay. Toggled with the "pause" action (Escape / Start).
/// Must run with ProcessMode = Always so it still gets input while the tree is paused.
/// </summary>
public partial class PauseMenu : Control
{
	private Control _pauseView = null!;
	private SettingsMenu _settingsMenu = null!;
	private Button _continueButton = null!;
	private Button _settingsButton = null!;

	public override void _Ready()
	{
		_pauseView = GetNode<Control>("%PauseView");
		_settingsMenu = GetNode<SettingsMenu>("%SettingsMenu");
		_continueButton = GetNode<Button>("%ContinueButton");
		_settingsButton = GetNode<Button>("%SettingsButton");

		_continueButton.Pressed += Resume;
		_settingsButton.Pressed += OpenSettings;
		GetNode<Button>("%ExitButton").Pressed += ExitGame;
		_settingsMenu.Closed += CloseSettings;

		Hide();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event.IsActionPressed("pause"))
		{
			if (Visible)
			{
				Resume();
			}
			else
			{
				Pause();
			}
			GetViewport().SetInputAsHandled();
		}
		else if (Visible && (@event.IsActionPressed("ui_cancel") || @event.IsActionPressed("back")))
		{
			// The settings screen handles its own cancel first, so this only fires on the main pause view.
			Resume();
			GetViewport().SetInputAsHandled();
		}
	}

	private void Pause()
	{
		GetTree().Paused = true;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_settingsMenu.Hide();
		_pauseView.Show();
		Show();
		_continueButton.GrabFocus();
	}

	private void Resume()
	{
		Hide();
		Input.MouseMode = Input.MouseModeEnum.Captured;
		GetTree().Paused = false;
	}

	private void OpenSettings()
	{
		_pauseView.Hide();
		_settingsMenu.Open();
	}

	private void CloseSettings()
	{
		_pauseView.Show();
		_settingsButton.GrabFocus();
	}

	private void ExitGame()
	{
		GetTree().Quit();
	}
}
