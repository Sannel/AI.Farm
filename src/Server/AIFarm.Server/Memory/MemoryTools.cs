using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AIFarm.Server.Memory;

/// <summary>MCP tools giving AI agents a long-term memory they can search by meaning.</summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryStore store)
{
	[McpServerTool(Name = "remember"), Description(
		"Stores something to remember for later. Memories are private to the agent id and can be " +
		"found again by meaning with recall.")]
	public Task<MemoryItem> Remember(
		[Description("Your agent id; memories are kept per agent.")] string agentId,
		[Description("What to remember, in plain language.")] string content,
		[Description("Optional labels to filter by later, e.g. [\"player:sam\", \"preference\"].")] string[]? tags = null,
		[Description("Optional JSON to keep alongside the memory.")] string? metadata = null,
		CancellationToken cancellationToken = default) =>
		store.RememberAsync(agentId, content, tags, metadata, cancellationToken: cancellationToken);

	[McpServerTool(Name = "recall", ReadOnly = true), Description(
		"Finds the memories closest in meaning to a query, best first, each with a similarity score " +
		"from -1 to 1 (higher is closer).")]
	public Task<IReadOnlyList<MemoryItem>> Recall(
		[Description("Your agent id.")] string agentId,
		[Description("What you are trying to remember, in plain language.")] string query,
		[Description("How many memories to return (1-100).")] int limit = 5,
		[Description("Only consider memories that have all of these tags.")] string[]? tags = null,
		CancellationToken cancellationToken = default) =>
		store.SearchAsync(agentId, query, limit, tags, cancellationToken: cancellationToken);

	[McpServerTool(Name = "recent_memories", ReadOnly = true), Description("Lists your most recent memories, newest first.")]
	public Task<IReadOnlyList<MemoryItem>> Recent(
		[Description("Your agent id.")] string agentId,
		[Description("How many memories to return (1-100).")] int limit = 20,
		[Description("Only list memories that have all of these tags.")] string[]? tags = null,
		CancellationToken cancellationToken = default) =>
		store.RecentAsync(agentId, limit, tags, cancellationToken);

	[McpServerTool(Name = "forget", Destructive = true), Description("Deletes one of your memories by id.")]
	public async Task<string> Forget(
		[Description("Your agent id.")] string agentId,
		[Description("The id of the memory to delete.")] Guid memoryId,
		CancellationToken cancellationToken = default) =>
		await store.ForgetAsync(agentId, memoryId, cancellationToken)
			? $"Forgot memory {memoryId}."
			: $"No memory {memoryId} for agent {agentId}.";
}
