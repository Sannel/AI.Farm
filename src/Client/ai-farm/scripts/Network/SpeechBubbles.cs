using System;
using System.Collections.Generic;
using Godot;

namespace AIFarm.Network;

/// <summary>
/// Speech bubbles over whoever is talking: a panel drawn on screen above the speaker's head,
/// following them, for long enough to read (longer lines stay longer). Bubbles behind the
/// camera or far away aren't shown.
/// </summary>
public partial class SpeechBubbles : Control
{
	[Export]
	public float MaxDistance { get; set; } = 30f;

	private const double BaseSeconds = 3.5;
	private const double SecondsPerCharacter = 0.06;

	private readonly Dictionary<Node3D, Bubble> _bubbles = [];
	private Theme _theme = null!;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsPreset(LayoutPreset.FullRect);
		_theme = GD.Load<Theme>("res://ui/menu_theme.tres");
	}

	/// <summary>Shows what someone said over their head, replacing anything they said before.</summary>
	public void Show(Node3D speaker, float headHeight, string text)
	{
		if (_bubbles.Remove(speaker, out var old))
		{
			old.Panel.QueueFree();
		}
		var panel = new PanelContainer { Theme = _theme, ThemeTypeVariation = "MenuPanel", MouseFilter = MouseFilterEnum.Ignore };
		var label = new Label
		{
			Text = text,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			CustomMinimumSize = new Vector2(Math.Min(360, 40 + text.Length * 9), 0),
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		panel.AddChild(label);
		AddChild(panel);
		_bubbles[speaker] = new Bubble(panel, headHeight, BaseSeconds + text.Length * SecondsPerCharacter);
	}

	public override void _Process(double delta)
	{
		var camera = GetViewport().GetCamera3D();
		foreach (var (speaker, bubble) in new List<KeyValuePair<Node3D, Bubble>>(_bubbles))
		{
			bubble.Remaining -= delta;
			if (bubble.Remaining <= 0 || !IsInstanceValid(speaker))
			{
				bubble.Panel.QueueFree();
				_bubbles.Remove(speaker);
				continue;
			}
			var top = speaker.GlobalPosition + Vector3.Up * (bubble.HeadHeight + 0.8f); // above the name tag
			var visible = camera is not null && !camera.IsPositionBehind(top)
				&& camera.GlobalPosition.DistanceTo(top) <= MaxDistance;
			bubble.Panel.Visible = visible;
			if (visible)
			{
				// Above their head, but kept on screen (someone standing close has their head off the top).
				var screen = camera!.UnprojectPosition(top);
				var size = bubble.Panel.Size;
				var area = GetViewportRect().Size;
				bubble.Panel.Position = new Vector2(
					Math.Clamp(screen.X - size.X / 2, 12, Math.Max(12, area.X - size.X - 12)),
					Math.Clamp(screen.Y - size.Y, 12, Math.Max(12, area.Y - size.Y - 12)));
				// Fade out over the last half second.
				bubble.Panel.Modulate = new Color(1, 1, 1, (float)Math.Min(1, bubble.Remaining * 2));
			}
		}
	}

	private sealed class Bubble(PanelContainer panel, float headHeight, double seconds)
	{
		public PanelContainer Panel { get; } = panel;
		public float HeadHeight { get; } = headHeight;
		public double Remaining { get; set; } = seconds;
	}
}
