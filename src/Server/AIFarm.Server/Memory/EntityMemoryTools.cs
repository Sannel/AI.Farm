using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AIFarm.Server.Memory;

/// <summary>
/// MCP tools for the memories of entities in the game world (an animal's AI remembering who
/// fed it, where the good grass is). Only entities that keep memories (hasMemory in
/// world_snapshot) can use them; each memory records where the entity was standing.
/// </summary>
[McpServerToolType]
public sealed class EntityMemoryTools(MemoryStore store)
{
	[McpServerTool(Name = "entity_remember"), Description(
		"Stores a memory for an entity in the game world, along with where it is standing now.")]
	public Task<MemoryItem> Remember(
		[Description("The entity's id (from world_snapshot or find_nearby).")] Guid entityId,
		[Description("What the entity should remember, in plain language.")] string content,
		[Description("Optional labels to filter by later.")] string[]? tags = null,
		[Description("Optional JSON to keep alongside the memory.")] string? metadata = null,
		CancellationToken cancellationToken = default) =>
		store.RememberForEntityAsync(entityId, content, tags, metadata, cancellationToken: cancellationToken);

	[McpServerTool(Name = "entity_recall", ReadOnly = true), Description(
		"Finds an entity's memories closest in meaning to a query, best first, with where it was " +
		"when it formed each one and a similarity score (higher is closer).")]
	public Task<IReadOnlyList<MemoryItem>> Recall(
		[Description("The entity's id.")] Guid entityId,
		[Description("What the entity is trying to remember.")] string query,
		[Description("How many memories to return (1-100).")] int limit = 5,
		[Description("Only consider memories that have all of these tags.")] string[]? tags = null,
		CancellationToken cancellationToken = default) =>
		store.SearchForEntityAsync(entityId, query, limit, tags, cancellationToken: cancellationToken);

	[McpServerTool(Name = "entity_recent_memories", ReadOnly = true), Description("Lists an entity's most recent memories, newest first.")]
	public Task<IReadOnlyList<MemoryItem>> Recent(
		[Description("The entity's id.")] Guid entityId,
		[Description("How many memories to return (1-100).")] int limit = 20,
		[Description("Only list memories that have all of these tags.")] string[]? tags = null,
		CancellationToken cancellationToken = default) =>
		store.RecentForEntityAsync(entityId, limit, tags, cancellationToken);

	[McpServerTool(Name = "entity_forget", Destructive = true), Description("Deletes one of an entity's memories by id.")]
	public async Task<string> Forget(
		[Description("The entity's id.")] Guid entityId,
		[Description("The id of the memory to delete.")] Guid memoryId,
		CancellationToken cancellationToken = default) =>
		await store.ForgetForEntityAsync(entityId, memoryId, cancellationToken)
			? $"Forgot memory {memoryId}."
			: $"Entity {entityId} has no memory {memoryId}.";
}
