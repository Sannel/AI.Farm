using System.ComponentModel;
using AIFarm.Contracts;
using AIFarm.Server.Data;
using AIFarm.Server.Memory;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AIFarm.Server.Game;

/// <summary>
/// MCP tools and a prompt for AI agents playing the world's NPCs (like Claude, the farmer):
/// find them, take on one's persona, and walk them about. Their memories are the entity
/// memory tools, with the NPC's id.
/// </summary>
[McpServerToolType, McpServerPromptType]
public sealed class NpcTools(GameWorld world, FarmDbContext db, MemoryFiles files, IOptions<BrainOptions> brain)
{
	[McpServerTool(Name = "list_npcs", ReadOnly = true), Description(
		"The non-player people in the world (each played by an AI agent through its persona): where " +
		"they are, what they're doing and how they feel.")]
	public IReadOnlyList<EntitySnapshot> ListNpcs() =>
		[.. world.Snapshot().Entities.Where(e => e.Kind == EntityKind.Npc)];

	[McpServerTool(Name = "get_persona", ReadOnly = true), Description(
		"An NPC's persona: who they are, what they do and how to play them (markdown).")]
	public async Task<string> GetPersona(
		[Description("The NPC's entity id (from list_npcs).")] Guid entityId,
		CancellationToken cancellationToken = default) =>
		await db.WorldEntities.Where(e => e.Id == entityId && e.Kind == EntityKind.Npc).Select(e => e.Persona)
			.FirstOrDefaultAsync(cancellationToken)
		?? throw new KeyNotFoundException($"There's no NPC {entityId}.");

	[McpServerTool(Name = "npc_move_to"), Description(
		"Walks an NPC to a spot on the ground (metres; X and Z, the ground plane), at a stroll. Once " +
		"there, that's where they stay and potter about until sent somewhere else.")]
	public string MoveTo(
		[Description("The NPC's entity id.")] Guid entityId,
		[Description("X to walk to, in metres.")] float x,
		[Description("Z to walk to, in metres.")] float z) =>
		world.MoveTo(entityId, x, z)
			? $"Walking to ({x:0.#}, {z:0.#})."
			: $"There's no NPC {entityId} in the world.";

	[McpServerTool(Name = "read_memory_file", ReadOnly = true), Description(
		"An NPC's memory file (its notebook, of a fixed size), or what it knows about one player " +
		"(a third of that size), with how full it is.")]
	public Task<MemoryFileView> ReadMemoryFile(
		[Description("The NPC's entity id.")] Guid entityId,
		[Description("A player's entity id, for what the NPC knows about them; leave out for its main file.")] Guid? playerId = null,
		CancellationToken cancellationToken = default) =>
		files.ReadAsync(entityId, playerId, cancellationToken);

	[McpServerTool(Name = "rewrite_memory_file"), Description(
		"Replaces an NPC's memory file (or what it knows about a player) with new text. It must fit " +
		"the file's size: summarise and drop what matters least to make room.")]
	public Task<MemoryFileView> RewriteMemoryFile(
		[Description("The NPC's entity id.")] Guid entityId,
		[Description("The complete new text.")] string content,
		[Description("A player's entity id, for what the NPC knows about them; leave out for its main file.")] Guid? playerId = null,
		CancellationToken cancellationToken = default) =>
		files.WriteAsync(entityId, playerId, content, cancellationToken);

	[McpServerPrompt(Name = "play_npc"), Description(
		"Take on an NPC's persona: who they are and how to play them, their entity id, and how " +
		"things stand around them right now. Start here to play an NPC, e.g. the farmer.")]
	public async Task<string> PlayNpc(
		[Description("The NPC's name, e.g. \"Claude\".")] string name,
		CancellationToken cancellationToken = default)
	{
		var npc = await db.WorldEntities.AsNoTracking()
			.FirstOrDefaultAsync(e => e.Kind == EntityKind.Npc && e.Name == name, cancellationToken)
			?? throw new KeyNotFoundException($"There's no NPC called {name}.");
		var situation = NpcBriefing.Describe(world, npc) + "\n" +
			await NpcBriefing.MemoryAsync(world, files, npc, brain.Value.PlayerMemoryRange, cancellationToken) +
			"\nYour memory files above are what you remember. Keep them up to date with `rewrite_memory_file` " +
			"(within their size), then look over the farm.\n";
		return $"{npc.Persona}\n\n{situation}";
	}
}
