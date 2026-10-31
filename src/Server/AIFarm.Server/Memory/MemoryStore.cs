using AIFarm.Server.Data;
using AIFarm.Server.Game;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace AIFarm.Server.Memory;

/// <summary>
/// A memory as returned to callers. Score is cosine similarity (1 = same meaning). Entity
/// memories carry the entity and where it was standing (X, Y, Z) when it formed the memory.
/// </summary>
public sealed record MemoryItem(
	Guid Id,
	string AgentId,
	Guid? EntityId,
	string Content,
	string[] Tags,
	string? Metadata,
	float? X,
	float? Y,
	float? Z,
	DateTimeOffset CreatedAt,
	double? Score = null);

/// <summary>
/// Long-term memory, stored in PostgreSQL: text is embedded on the way in and found again by
/// meaning (cosine similarity over pgvector). Memories belong either to an outside AI agent
/// (by agent id) or to an entity in the game world that keeps memories; every search stays
/// within one owner's memories.
/// </summary>
public sealed class MemoryStore(FarmDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings,
	GameWorld world, TimeProvider time)
{
	public const int MaxResults = 100;

	/// <summary>Agent ids with this prefix are reserved for world entities' own memories.</summary>
	public const string EntityOwnerPrefix = "entity:";

	public static string EntityOwner(Guid entityId) => EntityOwnerPrefix + entityId.ToString("D");

	// --- outside agents --------------------------------------------------------------------

	/// <summary>Stores a memory. Pass an embedding to use your own; otherwise the content is embedded.</summary>
	public Task<MemoryItem> RememberAsync(string agentId, string content, IEnumerable<string>? tags = null,
		string? metadata = null, float[]? embedding = null, CancellationToken cancellationToken = default) =>
		AddAsync(AgentOwner(agentId), null, null, content, tags, metadata, embedding, cancellationToken);

	/// <summary>
	/// The agent's memories closest in meaning to the query (or to the given embedding), best
	/// first. With tags, only memories carrying all of them are considered.
	/// </summary>
	public Task<IReadOnlyList<MemoryItem>> SearchAsync(string agentId, string? query, int limit = 5,
		IEnumerable<string>? tags = null, float[]? embedding = null, CancellationToken cancellationToken = default) =>
		FindAsync(AgentOwner(agentId), query, limit, tags, embedding, cancellationToken);

	/// <summary>The agent's most recent memories, newest first.</summary>
	public Task<IReadOnlyList<MemoryItem>> RecentAsync(string agentId, int limit = 20,
		IEnumerable<string>? tags = null, CancellationToken cancellationToken = default) =>
		ListAsync(AgentOwner(agentId), limit, tags, cancellationToken);

	/// <summary>Deletes one of the agent's memories; false if it has no memory with that id.</summary>
	public Task<bool> ForgetAsync(string agentId, Guid id, CancellationToken cancellationToken = default) =>
		DeleteAsync(AgentOwner(agentId), id, cancellationToken);

	// --- world entities --------------------------------------------------------------------

	/// <summary>
	/// Stores a memory for an entity in the world, noting where it is standing now.
	/// </summary>
	/// <exception cref="KeyNotFoundException">No such entity.</exception>
	/// <exception cref="ArgumentException">The entity doesn't keep memories.</exception>
	public async Task<MemoryItem> RememberForEntityAsync(Guid entityId, string content, IEnumerable<string>? tags = null,
		string? metadata = null, float[]? embedding = null, CancellationToken cancellationToken = default)
	{
		var entity = await RequireMemoryAsync(entityId, cancellationToken);
		// Its live position if it is in the world now, otherwise where it was last saved.
		var live = world.Find(entityId);
		var position = live is null ? (entity.X, entity.Y, entity.Z) : (live.X, live.Y, live.Z);
		return await AddAsync(EntityOwner(entityId), entityId, position, content, tags, metadata, embedding, cancellationToken);
	}

	/// <summary>The entity's memories closest in meaning to the query, best first.</summary>
	public async Task<IReadOnlyList<MemoryItem>> SearchForEntityAsync(Guid entityId, string? query, int limit = 5,
		IEnumerable<string>? tags = null, float[]? embedding = null, CancellationToken cancellationToken = default)
	{
		await RequireMemoryAsync(entityId, cancellationToken);
		return await FindAsync(EntityOwner(entityId), query, limit, tags, embedding, cancellationToken);
	}

	/// <summary>The entity's most recent memories, newest first.</summary>
	public async Task<IReadOnlyList<MemoryItem>> RecentForEntityAsync(Guid entityId, int limit = 20,
		IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
	{
		await RequireMemoryAsync(entityId, cancellationToken);
		return await ListAsync(EntityOwner(entityId), limit, tags, cancellationToken);
	}

	/// <summary>Deletes one of the entity's memories; false if it has no memory with that id.</summary>
	public async Task<bool> ForgetForEntityAsync(Guid entityId, Guid id, CancellationToken cancellationToken = default)
	{
		await RequireMemoryAsync(entityId, cancellationToken);
		return await DeleteAsync(EntityOwner(entityId), id, cancellationToken);
	}

	// --- shared ----------------------------------------------------------------------------

	private async Task<MemoryItem> AddAsync(string owner, Guid? entityId, (float X, float Y, float Z)? position,
		string content, IEnumerable<string>? tags, string? metadata, float[]? embedding, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(content);
		var record = new MemoryRecord
		{
			Id = Guid.CreateVersion7(),
			AgentId = owner,
			EntityId = entityId,
			X = position?.X,
			Y = position?.Y,
			Z = position?.Z,
			Content = content,
			Tags = NormaliseTags(tags),
			Metadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata,
			Embedding = await ToVectorAsync(content, embedding, cancellationToken),
			CreatedAt = time.GetUtcNow(),
		};
		db.Memories.Add(record);
		await db.SaveChangesAsync(cancellationToken);
		return ToItem(record);
	}

	private async Task<IReadOnlyList<MemoryItem>> FindAsync(string owner, string? query, int limit,
		IEnumerable<string>? tags, float[]? embedding, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(query) && embedding is null)
		{
			throw new ArgumentException("Give a query or an embedding to search with.");
		}
		var target = await ToVectorAsync(query ?? "", embedding, cancellationToken);
		var results = await Filter(owner, tags)
			.Select(m => new { Memory = m, Distance = m.Embedding.CosineDistance(target) })
			.OrderBy(m => m.Distance)
			.Take(Math.Clamp(limit, 1, MaxResults))
			.ToListAsync(cancellationToken);
		return [.. results.Select(r => ToItem(r.Memory, 1 - r.Distance))];
	}

	private async Task<IReadOnlyList<MemoryItem>> ListAsync(string owner, int limit, IEnumerable<string>? tags,
		CancellationToken cancellationToken)
	{
		var records = await Filter(owner, tags)
			.OrderByDescending(m => m.CreatedAt)
			.Take(Math.Clamp(limit, 1, MaxResults))
			.ToListAsync(cancellationToken);
		return [.. records.Select(r => ToItem(r))];
	}

	private async Task<bool> DeleteAsync(string owner, Guid id, CancellationToken cancellationToken) =>
		await db.Memories.Where(m => m.AgentId == owner && m.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

	private async Task<WorldEntity> RequireMemoryAsync(Guid entityId, CancellationToken cancellationToken)
	{
		var entity = await db.WorldEntities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entityId, cancellationToken)
			?? throw new KeyNotFoundException($"There is no entity {entityId}.");
		if (!entity.HasMemory)
		{
			throw new ArgumentException($"{entity.Name} ({entityId}) doesn't keep memories.");
		}
		return entity;
	}

	private static string AgentOwner(string agentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
		if (agentId.StartsWith(EntityOwnerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"Agent ids starting \"{EntityOwnerPrefix}\" are reserved; use the entity memory tools.");
		}
		return agentId;
	}

	private IQueryable<MemoryRecord> Filter(string owner, IEnumerable<string>? tags)
	{
		var required = NormaliseTags(tags);
		var memories = db.Memories.AsNoTracking().Where(m => m.AgentId == owner);
		return required.Length == 0 ? memories : memories.Where(m => required.All(tag => m.Tags.Contains(tag)));
	}

	private async Task<Vector> ToVectorAsync(string text, float[]? embedding, CancellationToken cancellationToken)
	{
		var values = embedding ?? (await embeddings.GenerateVectorAsync(text, cancellationToken: cancellationToken)).ToArray();
		if (values.Length != FarmDbContext.EmbeddingDimensions)
		{
			throw new ArgumentException(
				$"Embeddings must have {FarmDbContext.EmbeddingDimensions} dimensions; got {values.Length}.");
		}
		return new Vector(values);
	}

	private static string[] NormaliseTags(IEnumerable<string>? tags) =>
		tags is null ? [] : [.. tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct()];

	private static MemoryItem ToItem(MemoryRecord r, double? score = null) =>
		new(r.Id, r.AgentId, r.EntityId, r.Content, r.Tags, r.Metadata, r.X, r.Y, r.Z, r.CreatedAt, score);
}
