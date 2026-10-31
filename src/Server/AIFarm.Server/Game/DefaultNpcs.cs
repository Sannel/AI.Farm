using AIFarm.Contracts;
using AIFarm.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIFarm.Server.Game;

/// <summary>
/// The "Npcs" configuration section: the non-player people who live in the world, each with a
/// persona file (markdown, under the server's Personas folder) for the AI agent who plays them.
/// appsettings.json sets up Claude, the farmer.
/// </summary>
public sealed class NpcsOptions
{
	public const string Section = "Npcs";

	public List<DefaultNpc> Defaults { get; set; } = [];

	public sealed class DefaultNpc
	{
		public string Name { get; set; } = "";

		public string Model { get; set; } = GameWorld.PlayerModel;

		/// <summary>Path of the persona file, relative to the server's folder.</summary>
		public string PersonaFile { get; set; } = "";

		/// <summary>Where it lives (and first appears).</summary>
		public float X { get; set; }

		public float Z { get; set; }

		public double AgeYears { get; set; } = 40;

		/// <summary>Which Kokoro voice it speaks with, by name (am_michael is an American man, bm_george a British one).</summary>
		public string Voice { get; set; } = NpcBrain.DefaultVoice;
	}
}

public static class DefaultNpcs
{
	/// <summary>
	/// Makes sure every default NPC exists, and refreshes its persona from its file so edits to
	/// the file take effect on the next start. Existing NPCs keep their position and vitals.
	/// </summary>
	public static async Task EnsureAsync(FarmDbContext db, NpcsOptions options, TimeProvider time,
		CancellationToken cancellationToken = default)
	{
		foreach (var npc in options.Defaults.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
		{
			var persona = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, npc.PersonaFile), cancellationToken);
			var entity = await db.WorldEntities.FirstOrDefaultAsync(e => e.Kind == EntityKind.Npc && e.Name == npc.Name, cancellationToken);
			if (entity is null)
			{
				entity = new WorldEntity
				{
					Id = Guid.CreateVersion7(), Kind = EntityKind.Npc, Name = npc.Name, Model = npc.Model,
					X = npc.X, Z = npc.Z, Yaw = GameWorld.PlayerSpawnYaw, HomeX = npc.X, HomeZ = npc.Z, HasMemory = true,
					Mood = Mood.Content, Energy = 100f, AgeDays = npc.AgeYears * 365, UpdatedAt = time.GetUtcNow(),
				};
				db.WorldEntities.Add(entity);
			}
			entity.Persona = persona;
		}
		await db.SaveChangesAsync(cancellationToken);
	}
}
