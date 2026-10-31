using AIFarm.Contracts;
using AIFarm.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIFarm.Server.Game;

/// <summary>
/// The "Users" configuration section: players who should exist from the start, with their
/// permissions. appsettings.json sets up Adam, who may spawn animals.
/// </summary>
public sealed class UsersOptions
{
	public const string Section = "Users";

	public List<DefaultUser> Defaults { get; set; } = [];

	public sealed class DefaultUser
	{
		public string Name { get; set; } = "";

		public List<string> Permissions { get; set; } = [];
	}
}

public static class DefaultUsers
{
	/// <summary>
	/// Makes sure every default user exists as a player and has (at least) their configured
	/// permissions. Existing players keep their position, vitals and any other permissions.
	/// </summary>
	public static async Task EnsureAsync(FarmDbContext db, UsersOptions options, double startAgeDays, TimeProvider time,
		CancellationToken cancellationToken = default)
	{
		foreach (var user in options.Defaults.Where(u => !string.IsNullOrWhiteSpace(u.Name)))
		{
			var name = user.Name.Trim();
			var player = await db.WorldEntities.FirstOrDefaultAsync(e => e.Kind == EntityKind.Player && e.Name == name, cancellationToken);
			if (player is null)
			{
				player = new WorldEntity
				{
					Id = Guid.CreateVersion7(), Kind = EntityKind.Player, Name = name, Model = GameWorld.PlayerModel,
					X = GameWorld.PlayerSpawn.X, Z = GameWorld.PlayerSpawn.Z, Yaw = GameWorld.PlayerSpawnYaw,
					Mood = Mood.Content, Energy = 100f, AgeDays = startAgeDays, UpdatedAt = time.GetUtcNow(),
				};
				db.WorldEntities.Add(player);
			}
			player.Permissions = [.. player.Permissions.Union(user.Permissions).Distinct()];
		}
		await db.SaveChangesAsync(cancellationToken);
	}
}
