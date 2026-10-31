using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Memory;

namespace AIFarm.Server.Game;

/// <summary>"How things stand right now" for an NPC: who it is, where, how it feels, and who's around.</summary>
public static class NpcBriefing
{
	public const float NearbyRadius = 15f;

	public static string Describe(GameWorld world, WorldEntity npc)
	{
		var live = world.Find(npc.Id);
		var x = live?.X ?? npc.X;
		var z = live?.Z ?? npc.Z;
		var nearby = world.Near(x, z, NearbyRadius).Where(e => e.Id != npc.Id).Take(12).ToList();

		var text = new System.Text.StringBuilder();
		text.AppendLine("## Right now");
		text.AppendLine();
		text.AppendLine(FormattableString.Invariant($"- Your entity id: `{npc.Id}`"));
		var feeling = live?.Vitals is { } v
			? FormattableString.Invariant($" You feel {v.Mood.ToString().ToLowerInvariant()}, with {v.Energy:0}% energy.")
			: "";
		text.AppendLine(FormattableString.Invariant($"- You are at ({x:0.#}, {z:0.#}).") + feeling);
		text.AppendLine(nearby.Count == 0
			? FormattableString.Invariant($"- Nobody is within {NearbyRadius:0} m of you.")
			: "- Near you: " + string.Join(", ", nearby.Select(e => FormattableString.Invariant(
				$"{e.Name} ({e.Kind.ToString().ToLowerInvariant()}{(e.Vitals is { } ev ? FormattableString.Invariant($", {ev.Mood.ToString().ToLowerInvariant()}, {ev.Energy:0}% energy") : "")}, {MathF.Sqrt((e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z)):0} m away)"))) + ".");
		return text.ToString();
	}

	/// <summary>
	/// The NPC's memory file, and what it knows about each player within range, with how full
	/// each is, so it works from (and keeps within) its memory.
	/// </summary>
	public static async Task<string> MemoryAsync(GameWorld world, MemoryFiles files, WorldEntity npc, float playerRange,
		CancellationToken cancellationToken)
	{
		var text = new System.Text.StringBuilder();
		var main = await files.ReadAsync(npc.Id, cancellationToken: cancellationToken);
		text.AppendLine($"## Your memory file ({main.Used} of {main.Limit} characters used)");
		text.AppendLine();
		text.AppendLine(main.Content.Length > 0 ? main.Content : "(empty: nothing written yet)");

		var here = world.Find(npc.Id);
		var players = here is null ? []
			: world.Near(here.X, here.Z, playerRange).Where(e => e.Kind == EntityKind.Player).Select(e => e.Id);
		foreach (var about in await files.AboutPlayersAsync(npc.Id, players, cancellationToken))
		{
			text.AppendLine();
			text.AppendLine($"## What you know about {about.SubjectName} (player id `{about.SubjectId}`; {about.Used} of {about.Limit} characters used)");
			text.AppendLine();
			text.AppendLine(about.Content.Length > 0 ? about.Content : "(nothing yet: you haven't written anything about them)");
		}
		return text.ToString();
	}
}
