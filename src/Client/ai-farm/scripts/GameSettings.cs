using Godot;

namespace AIFarm;

/// <summary>
/// Autoload that owns the player's display settings: loads them at startup, applies them,
/// and saves every change to user://settings.cfg. Also draws the optional FPS counter.
/// </summary>
public partial class GameSettings : Node
{
	public readonly record struct Resolution(string Label, Vector2I Size);

	public static readonly Resolution[] Resolutions =
	[
		new("1280 × 720  (720p 16:9)", new Vector2I(1280, 720)),
		new("1280 × 800  (Steam Deck)", new Vector2I(1280, 800)),
		new("1920 × 1080  (1080p 16:9)", new Vector2I(1920, 1080)),
		new("2560 × 1440  (2K 16:9)", new Vector2I(2560, 1440)),
		new("3840 × 2160  (4K 16:9)", new Vector2I(3840, 2160)),
		new("1680 × 720  (720p 21:9)", new Vector2I(1680, 720)),
		new("2560 × 1080  (1080p 21:9)", new Vector2I(2560, 1080)),
		new("3440 × 1440  (2K 21:9)", new Vector2I(3440, 1440)),
		new("5120 × 2160  (4K 21:9)", new Vector2I(5120, 2160)),
	];

	private const string SettingsPath = "user://settings.cfg";
	private const int DefaultResolutionIndex = 1;
	private const double FpsRefreshSeconds = 0.25;

	public static GameSettings Instance { get; private set; } = null!;

	public int ResolutionIndex { get; private set; } = DefaultResolutionIndex;
	public bool Fullscreen { get; private set; }
	public bool VSync { get; private set; } = true;
	public bool ShowFps { get; private set; }

	private Label _fpsLabel = null!;
	private double _fpsTimer;

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		BuildFpsOverlay();
		Load();
		ApplyDisplay();
		ApplyVSync();
		ApplyShowFps();
	}

	public override void _Process(double delta)
	{
		if (!ShowFps)
		{
			return;
		}

		_fpsTimer -= delta;
		if (_fpsTimer <= 0)
		{
			_fpsTimer = FpsRefreshSeconds;
			_fpsLabel.Text = $"{Engine.GetFramesPerSecond():0} FPS";
		}
	}

	public void SetResolution(int index)
	{
		ResolutionIndex = Mathf.Clamp(index, 0, Resolutions.Length - 1);
		ApplyDisplay();
		Save();
	}

	public void SetFullscreen(bool fullscreen)
	{
		Fullscreen = fullscreen;
		ApplyDisplay();
		Save();
	}

	public void SetVSync(bool enabled)
	{
		VSync = enabled;
		ApplyVSync();
		Save();
	}

	public void SetShowFps(bool show)
	{
		ShowFps = show;
		ApplyShowFps();
		Save();
	}

	private void ApplyDisplay()
	{
		// The editor's embedded Game view owns the window, so size and mode requests are ignored there.
		if (Engine.IsEmbeddedInEditor())
		{
			return;
		}

		var size = Resolutions[ResolutionIndex].Size;
		var root = GetTree().Root;

		if (Fullscreen)
		{
			// Fullscreen always uses the monitor's native mode; the chosen resolution sets
			// the 3D render resolution instead, while the UI stays sharp at native size.
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
			var screen = DisplayServer.ScreenGetSize();
			root.Scaling3DScale = Mathf.Clamp((float)size.Y / screen.Y, 0.25f, 2f);
			return;
		}

		root.Scaling3DScale = 1f;
		DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);

		// A window can't be bigger than the desktop, so clamp large picks to the usable area.
		var usable = DisplayServer.ScreenGetUsableRect();
		var windowSize = new Vector2I(Mathf.Min(size.X, usable.Size.X), Mathf.Min(size.Y, usable.Size.Y));
		DisplayServer.WindowSetSize(windowSize);
		DisplayServer.WindowSetPosition(usable.Position + (usable.Size - windowSize) / 2);
	}

	private void ApplyVSync()
	{
		DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
	}

	private void ApplyShowFps()
	{
		_fpsLabel.Visible = ShowFps;
		_fpsTimer = 0;
	}

	private void BuildFpsOverlay()
	{
		var layer = new CanvasLayer { Layer = 100 };
		_fpsLabel = new Label
		{
			Theme = GD.Load<Theme>("res://ui/menu_theme.tres"),
			ThemeTypeVariation = "CreditLabel",
			HorizontalAlignment = HorizontalAlignment.Right,
			Visible = false,
		};
		_fpsLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
		_fpsLabel.GrowHorizontal = Control.GrowDirection.Begin;
		_fpsLabel.Position += new Vector2(-24, 16);
		layer.AddChild(_fpsLabel);
		AddChild(layer);
	}

	private void Load()
	{
		var config = new ConfigFile();
		if (config.Load(SettingsPath) != Error.Ok)
		{
			return;
		}

		var saved = (string)config.GetValue("display", "resolution", "");
		var index = System.Array.FindIndex(Resolutions, r => SizeKey(r.Size) == saved);
		ResolutionIndex = index >= 0 ? index : DefaultResolutionIndex;
		Fullscreen = (bool)config.GetValue("display", "fullscreen", Fullscreen);
		VSync = (bool)config.GetValue("display", "vsync", VSync);
		ShowFps = (bool)config.GetValue("debug", "show_fps", ShowFps);
	}

	private void Save()
	{
		var config = new ConfigFile();
		// Stored as "WxH" rather than an index so reordering the list doesn't break saved files.
		config.SetValue("display", "resolution", SizeKey(Resolutions[ResolutionIndex].Size));
		config.SetValue("display", "fullscreen", Fullscreen);
		config.SetValue("display", "vsync", VSync);
		config.SetValue("debug", "show_fps", ShowFps);
		config.Save(SettingsPath);
	}

	private static string SizeKey(Vector2I size) => $"{size.X}x{size.Y}";
}
