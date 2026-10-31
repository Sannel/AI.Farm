using System.ComponentModel;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AIFarm.Server.Game;

/// <summary>MCP tools letting AI agents see what is happening in the game world and steer how its creatures feel.</summary>
[McpServerToolType]
public sealed class GameTools(GameWorld world, FarmDbContext db, TimeProvider time)
{
	[McpServerTool(Name = "world_snapshot", ReadOnly = true), Description(
		"Everything in the game world right now: players and animals with their positions (metres; " +
		"Y is up, the ground is the X/Z plane), facing (yaw in radians), current animation, whether " +
		"they keep memories, and for living things their vitals (mood, energy 0-100%, age in game days).")]
	public WorldSnapshot Snapshot() => world.Snapshot();

	[McpServerTool(Name = "find_nearby", ReadOnly = true), Description(
		"Entities within a radius of a point on the ground, nearest first.")]
	public IReadOnlyList<EntitySnapshot> FindNearby(
		[Description("X of the point, in metres.")] float x,
		[Description("Z of the point, in metres.")] float z,
		[Description("Search radius in metres.")] float radius = 10f) =>
		world.Near(x, z, Math.Clamp(radius, 0f, 500f));

	[McpServerTool(Name = "get_entity", ReadOnly = true), Description(
		"One entity by id, with its vitals if it is alive, or null if it isn't in the world right now.")]
	public EntitySnapshot? GetEntity([Description("The entity id.")] Guid id) => world.Find(id);

	[McpServerTool(Name = "set_mood"), Description(
		"Sets how a living entity (an animal or player) feels. Moods: Content, Happy, Playful, Tired, " +
		"Sad, Scared, Angry. Running out of energy still makes anyone Tired until they've rested.")]
	public async Task<string> SetMood(
		[Description("The entity id.")] Guid entityId,
		[Description("The new mood.")] Mood mood,
		CancellationToken cancellationToken = default) =>
		await SetMoodAsync(world, db, time, entityId, mood, cancellationToken)
			? $"{entityId} is now {mood}."
			: $"{entityId} isn't a living entity.";

	/// <summary>
	/// Sets a living entity's mood: live in the world if it's there (saved with the next save),
	/// otherwise straight in the database (e.g. a player who is offline).
	/// </summary>
	/// <returns>False if there is no such living entity.</returns>
	public static async Task<bool> SetMoodAsync(GameWorld world, FarmDbContext db, TimeProvider time, Guid entityId, Mood mood,
		CancellationToken cancellationToken)
	{
		if (world.SetMood(entityId, mood))
		{
			return true;
		}
		var updated = await db.WorldEntities
			.Where(e => e.Id == entityId && (e.Kind == EntityKind.Player || e.Kind == EntityKind.Animal))
			.ExecuteUpdateAsync(set => set.SetProperty(e => e.Mood, mood).SetProperty(e => e.UpdatedAt, time.GetUtcNow()),
				cancellationToken);
		return updated > 0;
	}
}
