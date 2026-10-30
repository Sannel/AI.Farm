using Godot;

namespace AIFarm.World;

/// <summary>
/// Tool scene entry point: generates the valley layout and saves it as JSON, then quits.
/// Run from the project folder:
///   godot --headless res://tools/generate_valley.tscn
/// Commit the resulting file; the game and server load it instead of regenerating.
/// </summary>
public partial class GenerateValleyTool : Node
{
	[Export]
	public string OutputPath { get; set; } = "res://world/valley.json";

	[Export]
	public ulong Seed { get; set; } = 20261029;

	[Export]
	public int PlainTreeCount { get; set; } = 45;

	[Export]
	public int PlainExtraCount { get; set; } = 30;

	[Export]
	public int GrassCount { get; set; } = 3000;

	public override void _Ready()
	{
		var data = new ValleyGenerator
		{
			Seed = Seed,
			PlainTreeCount = PlainTreeCount,
			PlainExtraCount = PlainExtraCount,
			GrassCount = GrassCount,
		}.Generate();

		DirAccess.MakeDirRecursiveAbsolute(OutputPath.GetBaseDir());
		using (var file = FileAccess.Open(OutputPath, FileAccess.ModeFlags.Write))
		{
			if (file is null)
			{
				GD.PushError($"Could not write {OutputPath}: {FileAccess.GetOpenError()}");
				GetTree().Quit(1);
				return;
			}
			file.StoreString(data.ToJson());
		}

		GD.Print($"Wrote {data.Props.Count} props and {data.Colliders.Count} colliders to {OutputPath}");
		GetTree().Quit();
	}
}
