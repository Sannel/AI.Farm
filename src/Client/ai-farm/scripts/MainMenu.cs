using System;
using System.Threading;
using AIFarm.Network;
using Godot;

namespace AIFarm;

/// <summary>
/// Title screen. Connect opens a panel asking for the server and the player's name (both
/// remembered); Join connects, joins the world and enters the game. Exit quits.
/// </summary>
public partial class MainMenu : Control
{
	private const string GameScene = "res://scenes/game.tscn";
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

	private Control _mainPanel = null!;
	private Control _connectPanel = null!;
	private Button _connectButton = null!;
	private Button _exitButton = null!;
	private LineEdit _serverField = null!;
	private LineEdit _nameField = null!;
	private Label _status = null!;
	private Button _joinButton = null!;
	private Button _backButton = null!;
	private bool _connecting;

	public override void _Ready()
	{
		// Returning from the game leaves the mouse captured, and maybe a connection open.
		Input.MouseMode = Input.MouseModeEnum.Visible;
		ServerConnection.Instance.Disconnect();

		_mainPanel = GetNode<Control>("%Panel");
		_connectPanel = GetNode<Control>("%ConnectPanel");
		_connectButton = GetNode<Button>("%ConnectButton");
		_exitButton = GetNode<Button>("%ExitButton");
		_serverField = GetNode<LineEdit>("%ServerField");
		_nameField = GetNode<LineEdit>("%NameField");
		_status = GetNode<Label>("%Status");
		_joinButton = GetNode<Button>("%JoinButton");
		_backButton = GetNode<Button>("%BackButton");

		_connectButton.Pressed += ShowConnectPanel;
		_exitButton.Pressed += OnExitPressed;
		_joinButton.Pressed += Join;
		_backButton.Pressed += ShowMainPanel;
		_serverField.TextSubmitted += _ => _nameField.GrabFocus();
		_nameField.TextSubmitted += _ => Join();
		// Start editing as soon as a field has focus (however it got it), so typing just works;
		// on a Steam Deck this brings up the on-screen keyboard.
		foreach (var field in new[] { _serverField, _nameField })
		{
			field.FocusEntered += () => Callable.From(() => BeginEditing(field)).CallDeferred();
		}

		_serverField.Text = GameSettings.Instance.ServerAddress;
		_nameField.Text = GameSettings.Instance.PlayerName;
		_connectButton.GrabFocus();

		if (ServerConnection.Instance.LostReason is { } reason)
		{
			// Back here because the connection dropped: say so, ready to rejoin.
			ServerConnection.Instance.LostReason = null;
			ShowConnectPanel();
			GD.Print($"Lost the connection: {reason}");
			_status.Text = "Lost the connection to the server.";
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_connectPanel.Visible && !_connecting && (@event.IsActionPressed("ui_cancel") || @event.IsActionPressed("back")))
		{
			ShowMainPanel();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>Starts typing in a field with its text selected, so typing replaces it.</summary>
	private static void BeginEditing(LineEdit field)
	{
		if (!field.HasFocus() || !field.Editable)
		{
			return;
		}
		field.Edit();
		field.CaretColumn = field.Text.Length;
		field.SelectAll();
	}

	private void ShowConnectPanel()
	{
		_mainPanel.Visible = false;
		_connectPanel.Visible = true;
		_status.Text = "";
		// Straight to Join when we already know who and where; otherwise to the first blank field.
		if (_serverField.Text.Trim() == "")
		{
			_serverField.GrabFocus();
		}
		else if (_nameField.Text.Trim() == "")
		{
			_nameField.GrabFocus();
		}
		else
		{
			_joinButton.GrabFocus();
		}
	}

	private void ShowMainPanel()
	{
		_connectPanel.Visible = false;
		_mainPanel.Visible = true;
		_connectButton.GrabFocus();
	}

	private async void Join()
	{
		if (_connecting)
		{
			return;
		}
		var server = _serverField.Text.Trim() is { Length: > 0 } s ? s : _serverField.PlaceholderText;
		var name = _nameField.Text.Trim() is { Length: > 0 } n ? n : _nameField.PlaceholderText;

		SetConnecting(true);
		_status.Text = $"Connecting to {server}…";
		try
		{
			using var timeout = new CancellationTokenSource(ConnectTimeout);
			await ServerConnection.Instance.ConnectAsync(server, name, timeout.Token);
			GameSettings.Instance.SetConnection(server, name);
			GetTree().ChangeSceneToFile(GameScene);
		}
		catch (Exception e)
		{
			_status.Text = $"Couldn't join: {Describe(e)}";
			SetConnecting(false);
			_joinButton.GrabFocus();
		}
	}

	private void SetConnecting(bool connecting)
	{
		_connecting = connecting;
		_serverField.Editable = !connecting;
		_nameField.Editable = !connecting;
		_joinButton.Disabled = connecting;
		_backButton.Disabled = connecting;
	}

	private static string Describe(Exception e) => e switch
	{
		OperationCanceledException => "the server didn't answer.",
		Microsoft.AspNetCore.SignalR.HubException hub => hub.Message.Replace("An unexpected error occurred invoking 'Join' on the server. HubException: ", ""),
		System.Net.Http.HttpRequestException http => http.Message,
		_ => e.Message,
	};

	private void OnExitPressed()
	{
		GetTree().Quit();
	}
}
