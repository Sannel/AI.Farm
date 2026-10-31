namespace AIFarm.Server.Game;

/// <summary>The "Game" configuration section.</summary>
public sealed class GameOptions
{
	public const string Section = "Game";

	/// <summary>Simulation steps per second.</summary>
	public int TickRate { get; set; } = 20;

	/// <summary>Send a snapshot to clients every this many ticks.</summary>
	public int BroadcastEvery { get; set; } = 2;

	/// <summary>How often changed entities (positions and vitals) are written to the database.</summary>
	public TimeSpan SaveInterval { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>How long a game day lasts in real time; living things age a day per game day.</summary>
	public TimeSpan DayLength { get; set; } = GameWorld.DefaultDayLength;

	/// <summary>How old (in game days) a new player starts out.</summary>
	public double PlayerStartAgeDays { get; set; } = 25 * 365;
}
