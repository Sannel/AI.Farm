using Godot;

namespace AIFarm;

/// <summary>
/// Title screen. Wires up the Connect and Exit buttons.
/// </summary>
public partial class MainMenu : Control
{
	[Signal]
	public delegate void ConnectRequestedEventHandler();

	private Button _connectButton = null!;
	private Button _exitButton = null!;

	public override void _Ready()
	{
		_connectButton = GetNode<Button>("%ConnectButton");
		_exitButton = GetNode<Button>("%ExitButton");

		_connectButton.Pressed += OnConnectPressed;
		_exitButton.Pressed += OnExitPressed;

		_connectButton.GrabFocus();
	}

	private void OnConnectPressed()
	{
		// TODO: open the server connection flow once the networking layer exists.
		GD.Print("Connect pressed");
		EmitSignal(SignalName.ConnectRequested);
	}

	private void OnExitPressed()
	{
		GetTree().Quit();
	}
}
