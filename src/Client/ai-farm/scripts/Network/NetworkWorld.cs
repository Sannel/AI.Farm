using System;
using System.Collections.Generic;
using AIFarm.Contracts;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// Brings the server's world into the game scene while connected: the server's animals and
/// other players appear as <see cref="RemoteEntity"/> nodes (replacing the scene's own
/// stand-in animals), the local player's position is sent to the server and corrected if they
/// drift too far from it, and a HUD shows the player's vitals. With no connection (e.g. the
/// scene run straight from the editor) it does nothing and the scene plays offline.
/// </summary>
public partial class NetworkWorld : Node3D
{
	/// <summary>Scene nodes that stand in for the server's entities and are removed when online.</summary>
	[Export]
	public NodePath[] ReplacedWhenOnline { get; set; } = [];

	[Export]
	public NodePath PlayerPath { get; set; } = "../Player";

	/// <summary>How many times a second the player's movement is sent.</summary>
	[Export]
	public float SendRate { get; set; } = 20f;

	/// <summary>If the server has the player further away than this, the player is moved to the server's position.</summary>
	[Export]
	public float CorrectionDistance { get; set; } = 4f;

	private const string MainMenuScene = "res://scenes/main_menu.tscn";

	/// <summary>Voices are played for speakers within this many metres.</summary>
	private const float HearingDistance = 35f;

	/// <summary>Matches the server: further than this from whoever you're answering and you can't.</summary>
	private const float TalkDistance = 12f;

	private SpeechBubbles _bubbles = null!;
	private ReplyPanel _replies = null!;

	/// <summary>How far the crosshair can be from you and still dip a bucket.</summary>
	private const float WaterReach = 5f;
	private Label _ownLine = null!;
	private double _ownLineTimer;

	private readonly Dictionary<Guid, RemoteEntity> _entities = [];
	private PlayerController _player = null!;
	private ServerConnection _connection = null!;
	private long _lastTick = -1;
	private double _sendTimer;
	private bool _placedPlayer;

	private Label _nameLabel = null!;
	private ProgressBar _energyBar = null!;
	private Label _moodLabel = null!;
	private Label _ageLabel = null!;
	private Label _waterLabel = null!;
	private Label _airLabel = null!;
	private ProgressBar _airBar = null!;
	private Label _waterHint = null!;
	private Camera3D? _camera;

	public override void _Ready()
	{
		_connection = ServerConnection.Instance;
		if (!_connection.IsOnline)
		{
			SetProcess(false);
			SetPhysicsProcess(false);
			return;
		}

		foreach (var path in ReplacedWhenOnline)
		{
			GetNodeOrNull(path)?.QueueFree();
		}
		_player = GetNode<PlayerController>(PlayerPath);
		_connection.ConnectionLost += OnConnectionLost;
		_connection.SpeechHeard += OnSpeech;
		BuildHud();
	}

	public override void _ExitTree()
	{
		if (_connection is not null)
		{
			_connection.ConnectionLost -= OnConnectionLost;
			_connection.SpeechHeard -= OnSpeech;
		}
	}

	/// <summary>
	/// Someone said something: a bubble over them, their voice (if there is one and you're in
	/// earshot), and if they were talking to you, things you might say back.
	/// </summary>
	private void OnSpeech(SpeechEvent said)
	{
		if (said.SpeakerId == _connection.PlayerId)
		{
			ShowOwnLine(said.Text);
		}
		else if (_entities.TryGetValue(said.SpeakerId, out var speaker))
		{
			_bubbles.Show(speaker, speaker.HeadHeight, said.Text);
			if (said.HasAudio && speaker.GlobalPosition.DistanceTo(_player.GlobalPosition) <= HearingDistance)
			{
				speaker.Voice.Speak(said.SpeechId);
			}
		}
		if (said.ToPlayerId == _connection.PlayerId && said.Replies.Count > 0)
		{
			_replies.Offer(said.SpeakerId, said.SpeakerName, said.Replies);
		}
	}

	/// <summary>The spot on the water's surface under the crosshair, if it is within reach to dip a bucket.</summary>
	private Vector3? AimedWater()
	{
		_camera ??= _player.GetNodeOrNull<Camera3D>("Head/Camera3D");
		if (_camera is null)
		{
			return null;
		}
		var origin = _camera.GlobalPosition;
		var forward = -_camera.GlobalTransform.Basis.Z;
		if (forward.Y > -0.01f)
		{
			return null; // looking level or upwards
		}
		var t = (WaterMap.SurfaceY - origin.Y) / forward.Y;
		if (t < 0f || t > WaterReach)
		{
			return null;
		}
		var point = origin + forward * t;
		var at = _player.GlobalPosition;
		return WaterMap.IsWater(point.X, point.Z) && WaterMap.DistanceToWater(at.X, at.Z) <= WaterMap.CollectReach ? point : null;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!_connection.IsOnline || !@event.IsActionPressed("action") || _replies.Visible
			|| Input.MouseMode != Input.MouseModeEnum.Captured || AimedWater() is null)
		{
			return;
		}
		GetViewport().SetInputAsHandled();
		CollectWater();
	}

	private async void CollectWater()
	{
		try
		{
			var water = await _connection.CollectWaterAsync();
			ShowNote($"Collected water ({water} of {WaterMap.BucketCapacity})");
		}
		catch (Exception e)
		{
			ShowNote(e.Message.Split(": ")[^1]);
		}
	}

	private async void Reply(Guid npcId, string text)
	{
		try
		{
			await _connection.SayToNpcAsync(npcId, text);
		}
		catch (Exception e)
		{
			ShowOwnLine($"({e.Message.Split(": ")[^1]})");
		}
	}

	private void ShowOwnLine(string text) => ShowNote($"You: {text}");

	private void ShowNote(string text)
	{
		_ownLine.Text = text;
		_ownLine.Visible = true;
		_ownLineTimer = 3.0 + text.Length * 0.05;
	}

	public override void _Process(double delta)
	{
		_waterHint.Visible = !_replies.Visible && Input.MouseMode == Input.MouseModeEnum.Captured
			&& AimedWater() is not null;
		if (_ownLineTimer > 0 && (_ownLineTimer -= delta) <= 0)
		{
			_ownLine.Visible = false;
		}
		// Walked away from whoever you were answering: the moment's passed.
		if (_replies.Visible && (!_entities.TryGetValue(_replies.NpcId, out var asking)
			|| asking.GlobalPosition.DistanceTo(_player.GlobalPosition) > TalkDistance))
		{
			_replies.Close();
		}

		if (_connection.Latest is not { } snapshot || snapshot.Tick == _lastTick)
		{
			return;
		}
		_lastTick = snapshot.Tick;

		var seen = new HashSet<Guid>();
		foreach (var entity in snapshot.Entities)
		{
			seen.Add(entity.Id);
			if (entity.Id == _connection.PlayerId)
			{
				ApplyOwnState(entity);
				continue;
			}
			if (!_entities.TryGetValue(entity.Id, out var node))
			{
				node = RemoteEntity.Create(entity);
				AddChild(node);
				_entities[entity.Id] = node;
			}
			node.Apply(entity);
		}
		foreach (var id in new List<Guid>(_entities.Keys))
		{
			if (!seen.Contains(id))
			{
				_entities[id].QueueFree(); // left the world
				_entities.Remove(id);
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		_sendTimer -= delta;
		if (_sendTimer > 0 || !_placedPlayer)
		{
			return;
		}
		_sendTimer = 1.0 / SendRate;

		var position = _player.GlobalPosition;
		var velocity = _player.Velocity;
		var speed = new Vector2(velocity.X, velocity.Z).Length();
		var direction = speed > 0.1f ? new Vector2(velocity.X, velocity.Z) / Mathf.Max(speed, _player.MoveSpeed) : Vector2.Zero;
		_connection.SendInput(new PlayerInput(direction.X, direction.Y, ToEntityYaw(_player.Rotation.Y), _player.IsRunning,
			position.X, position.Y, position.Z));
	}

	/// <summary>The server's view of us: where we are (correcting big drifts) and our vitals.</summary>
	private void ApplyOwnState(EntitySnapshot me)
	{
		var serverPosition = new Vector3(me.X, me.Y, me.Z);
		if (!_placedPlayer)
		{
			// Joining: start where the server has us (where we left off last time).
			_player.GlobalPosition = serverPosition;
			_player.Rotation = new Vector3(0, FromEntityYaw(me.Yaw), 0);
			_placedPlayer = true;
		}
		else if (_player.GlobalPosition.DistanceTo(serverPosition) > CorrectionDistance)
		{
			_player.GlobalPosition = serverPosition;
			_player.Velocity = Vector3.Zero;
		}

		if (me.Vitals is { } vitals)
		{
			_player.CanRun = vitals.Energy > 0f;
			_energyBar.Value = vitals.Energy;
			_moodLabel.Text = $"Feeling {vitals.Mood.ToString().ToLowerInvariant()}";
			_ageLabel.Text = DescribeAge(vitals.AgeDays);
			// The breath meter only shows while it isn't full.
			var breathless = vitals.Air < 99.5f;
			_airLabel.Visible = _airBar.Visible = breathless;
			_airBar.Value = vitals.Air;
			_airBar.Modulate = vitals.Air < 25f ? new Color(1f, 0.55f, 0.5f) : Colors.White;
		}
		_waterLabel.Text = $"Water  {me.Water} / {WaterMap.BucketCapacity}";
	}

	private void OnConnectionLost(string reason)
	{
		_connection.LostReason = reason;
		GetTree().ChangeSceneToFile(MainMenuScene);
	}

	/// <summary>
	/// Entities face +Z at yaw 0 (like the models); the first-person player looks down -Z at
	/// rotation 0, so its facing is half a turn round from its rotation.
	/// </summary>
	private static float ToEntityYaw(float rotationY) => Mathf.Wrap(rotationY + Mathf.Pi, -Mathf.Pi, Mathf.Pi);

	private static float FromEntityYaw(float yaw) => Mathf.Wrap(yaw - Mathf.Pi, -Mathf.Pi, Mathf.Pi);

	public static string DescribeAge(double days)
	{
		var years = (int)(days / 365);
		var rest = (int)(days % 365);
		return years > 0 ? $"Age {years} {(years == 1 ? "year" : "years")}, {rest} days" : $"Age {rest} days";
	}

	private void BuildHud()
	{
		var layer = new CanvasLayer { Name = "Hud", Layer = 5 };
		var panel = new PanelContainer
		{
			Theme = GD.Load<Theme>("res://ui/menu_theme.tres"),
			ThemeTypeVariation = "MenuPanel",
			Position = new Vector2(24, 24),
		};
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		_nameLabel = new Label { Text = _connection.PlayerName };
		_energyBar = new ProgressBar
		{
			MinValue = 0,
			MaxValue = 100,
			Value = 100,
			CustomMinimumSize = new Vector2(260, 22),
			ShowPercentage = true,
		};
		_moodLabel = new Label();
		_ageLabel = new Label();
		_airLabel = new Label { Text = "Air", Visible = false };
		_airBar = new ProgressBar
		{
			MinValue = 0,
			MaxValue = 100,
			Value = 100,
			CustomMinimumSize = new Vector2(260, 22),
			ShowPercentage = false,
			Visible = false,
		};
		_waterLabel = new Label { Text = $"Water  0 / {WaterMap.BucketCapacity}" };
		column.AddChild(_nameLabel);
		column.AddChild(new Label { Text = "Energy" });
		column.AddChild(_energyBar);
		column.AddChild(_moodLabel);
		column.AddChild(_airLabel);
		column.AddChild(_airBar);
		column.AddChild(_ageLabel);
		column.AddChild(_waterLabel);
		panel.AddChild(column);
		layer.AddChild(panel);

		_bubbles = new SpeechBubbles { Name = "SpeechBubbles" };
		layer.AddChild(_bubbles);
		_ownLine = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
		_ownLine.AddThemeColorOverride("font_color", Colors.White);
		_ownLine.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.2f, 0.1f));
		_ownLine.AddThemeConstantOverride("outline_size", 8);
		_ownLine.AddThemeFontSizeOverride("font_size", 26);
		_ownLine.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
		_ownLine.GrowHorizontal = Control.GrowDirection.Both;
		_ownLine.Position += new Vector2(0, -60);
		layer.AddChild(_ownLine);
		_waterHint = new Label { Text = "Press E to collect water", Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
		_waterHint.AddThemeColorOverride("font_color", Colors.White);
		_waterHint.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.2f, 0.3f));
		_waterHint.AddThemeConstantOverride("outline_size", 8);
		_waterHint.AddThemeFontSizeOverride("font_size", 24);
		_waterHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
		_waterHint.GrowHorizontal = Control.GrowDirection.Both;
		_waterHint.Position += new Vector2(0, -130);
		layer.AddChild(_waterHint);
		_replies = new ReplyPanel { Name = "ReplyPanel" };
		_replies.Replied += Reply;
		layer.AddChild(_replies);
		AddChild(layer);
	}
}
