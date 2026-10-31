using System;
using System.Collections.Generic;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// When an NPC speaks to you, the things you might say back: pick one with 1/2/3, or the
/// D-pad / arrow keys and A / Enter. Back (B / Backspace) says nothing. It goes away by itself
/// after a while, or if you walk off.
/// </summary>
public partial class ReplyPanel : PanelContainer
{
	private const double Timeout = 45;

	private Label _title = null!;
	private Label _hint = null!;
	private VBoxContainer _options = null!;
	private Guid _npcId;
	private double _remaining;
	private readonly List<string> _replies = [];

	/// <summary>Raised when the player picks a reply: (npc id, what they say).</summary>
	public event Action<Guid, string>? Replied;

	public override void _Ready()
	{
		Theme = GD.Load<Theme>("res://ui/menu_theme.tres");
		ThemeTypeVariation = "MenuPanel";
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		_title = new Label();
		_options = new VBoxContainer();
		_options.AddThemeConstantOverride("separation", 6);
		column.AddChild(_title);
		column.AddChild(_options);
		_hint = new Label();
		_hint.AddThemeFontSizeOverride("font_size", 18);
		column.AddChild(_hint);
		AddChild(column);
		SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
		GrowHorizontal = GrowDirection.Both;
		GrowVertical = GrowDirection.Begin;
		Position += new Vector2(0, -40);
		Visible = false;
	}

	public Guid NpcId => _npcId;

	/// <summary>Offers these replies to what an NPC just said.</summary>
	public void Offer(Guid npcId, string npcName, IReadOnlyList<string> replies)
	{
		foreach (var child in _options.GetChildren())
		{
			child.QueueFree();
		}
		_npcId = npcId;
		_replies.Clear();
		_replies.AddRange(replies);
		_title.Text = $"Answer {npcName}:";
		_hint.Text = $"1-{replies.Count} to answer · Backspace / B to say nothing";
		for (var i = 0; i < replies.Count; i++)
		{
			var text = replies[i];
			var button = new Button { Text = $"{i + 1}.  {text}", Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(520, 0) };
			button.AddThemeFontSizeOverride("font_size", 24);
			button.Pressed += () => Pick(text);
			_options.AddChild(button);
		}
		_remaining = Timeout;
		Visible = true;
		Callable.From(() => (_options.GetChildCount() > 0 ? _options.GetChild<Button>(0) : null)?.GrabFocus()).CallDeferred();
	}

	public void Close()
	{
		Visible = false;
		foreach (var child in _options.GetChildren())
		{
			child.QueueFree();
		}
	}

	public override void _Process(double delta)
	{
		if (Visible && (_remaining -= delta) <= 0)
		{
			Close();
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (!Visible)
		{
			return;
		}
		if (@event is InputEventKey { Pressed: true, Echo: false } key && key.Keycode is >= Key.Key1 and <= Key.Key9)
		{
			var index = (int)(key.Keycode - Key.Key1);
			if (index < _replies.Count)
			{
				GetViewport().SetInputAsHandled();
				Pick(_replies[index]);
			}
		}
		else if (@event.IsActionPressed("back"))
		{
			Close(); // say nothing
			GetViewport().SetInputAsHandled();
		}
	}

	private void Pick(string text)
	{
		var npc = _npcId;
		Close();
		Replied?.Invoke(npc, text);
	}
}
