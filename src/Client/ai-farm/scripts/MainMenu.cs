using Godot;

namespace AIFarm;

/// <summary>
/// Title screen. Wires up the Connect and Exit buttons.
/// </summary>
public partial class MainMenu : Control
{
	private const string GameScene = "res://scenes/game.tscn";

	private Button _connectButton = null!;
	private Button _exitButton = null!;

	public override void _Ready()
	{
		// Returning from the game leaves the mouse captured.
		Input.MouseMode = Input.MouseModeEnum.Visible;

		_connectButton = GetNode<Button>("%ConnectButton");
		_exitButton = GetNode<Button>("%ExitButton");

		_connectButton.Pressed += OnConnectPressed;
		_exitButton.Pressed += OnExitPressed;

		_connectButton.GrabFocus();
	}

	private void OnConnectPressed()
	{
		// TODO: connect to the C# server before entering the game once networking exists.
		GetTree().ChangeSceneToFile(GameScene);
	}

	private void OnExitPressed()
	{
		GetTree().Quit();
	}
}
